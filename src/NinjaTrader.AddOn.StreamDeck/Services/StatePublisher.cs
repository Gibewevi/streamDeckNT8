using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Models;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Tracks positions and publishes state updates to the bridge.
    /// Subscribes to NT8 account events to detect changes.
    /// </summary>
    public class StatePublisher : IDisposable
    {
        private readonly ContextResolver _resolver;
        private readonly BridgeClient _bridgeClient;
        private readonly AddOnConfig _config;
        private readonly OrderMonitor _orderMonitor;
        private readonly TrendMonitor _trendMonitor;
        private Timer _stateTimer;
        private string _trackedAccount;
        private string _trackedInstrument;
        private bool _disposed;

        // Timer callbacks overlap when a publish takes longer than the interval. Two
        // overlapping publishes mean two concurrent sends, so only one may run at a time.
        private int _publishing;

        // State is republished several times a second. Logging every tick would drown the file,
        // so only transitions are recorded at INFO — that is what a post-mortem reads.
        private string _lastPositionSignature;
        private string _lastProtectionSignature;

        // Consecutive skipped ticks: one is normal jitter, a run of them means the publish path
        // is stuck (NT8 not answering, send blocked) and the deck is showing stale data.
        private int _consecutiveSkips;

        public string TrackedAccount
        {
            get { return _trackedAccount; }
        }

        public string TrackedInstrument
        {
            get { return _trackedInstrument; }
        }

        public StatePublisher(ContextResolver resolver, BridgeClient bridgeClient, AddOnConfig config,
            OrderMonitor orderMonitor, TrendMonitor trendMonitor)
        {
            _resolver = resolver;
            _bridgeClient = bridgeClient;
            _config = config;
            _orderMonitor = orderMonitor;
            _trendMonitor = trendMonitor;
        }

        public void Start(string accountName, string instrumentName)
        {
            if (!string.IsNullOrWhiteSpace(accountName))
                _trackedAccount = accountName;

            if (!string.IsNullOrWhiteSpace(instrumentName))
                _trackedInstrument = instrumentName;

            _stateTimer = new Timer(
                _ => PublishState(false),
                null,
                TimeSpan.FromMilliseconds(1000), // Initial delay
                TimeSpan.FromMilliseconds(_config.StateUpdateIntervalMs));

            SdLogger.Info("State publisher started — tracking {0} / {1}", accountName, instrumentName);
        }

        public void UpdateTracking(string accountName, string instrumentName)
        {
            if (!string.IsNullOrWhiteSpace(accountName))
                _trackedAccount = accountName;

            if (!string.IsNullOrWhiteSpace(instrumentName))
                _trackedInstrument = instrumentName;
            SdLogger.Info("State tracking updated — {0} / {1}", accountName, instrumentName);
        }

        /// <summary>
        /// Publishes the state immediately instead of waiting for the next timer tick.
        ///
        /// A fill used to reach the deck only on the following tick — 250 ms on average, 500 ms
        /// worst case, on top of the ~100 ms NinjaTrader takes to walk the order to Filled. The
        /// trader pressed a key and then watched a stale position for a third of a second. Called
        /// on the NinjaTrader event thread when a position or an order actually changes, so it
        /// must never throw: an exception here escapes into NT8's event pipeline.
        /// </summary>
        public void PublishNow()
        {
            if (_disposed) return;

            try
            {
                PublishState(true);
            }
            catch (Exception ex)
            {
                SdLogger.Fail("StatePublish", ex, "Immediate state publish failed");
            }
        }

        private void PublishState(bool immediate)
        {
            if (!_bridgeClient.IsConnected) return;

            // Skip this tick if the previous publish is still running
            if (Interlocked.CompareExchange(ref _publishing, 1, 0) != 0)
            {
                // An immediate publish losing the race is not a stall: the publish already in
                // flight is milliseconds old, and the timer republishes right after. Counting it
                // would make the stall detector fire on healthy bursts of fills.
                if (immediate)
                {
                    SdLogger.TraceEvent("StatePublish", "Immediate publish skipped — a publish is already in flight");
                    return;
                }

                _consecutiveSkips++;
                // The first skip is routine; a sustained run means the deck is frozen on stale
                // data, which the trader sees as "the buttons stopped updating".
                if (_consecutiveSkips == 5 || _consecutiveSkips % 50 == 0)
                {
                    SdLogger.EventWarn("StatePublish",
                        "State publish stalled — {0} consecutive ticks skipped ({1}ms interval); the deck is showing stale data",
                        _consecutiveSkips, _config.StateUpdateIntervalMs);
                }
                else
                {
                    SdLogger.TraceEvent("StatePublish", "Tick skipped — previous publish still in flight");
                }
                return;
            }

            if (_consecutiveSkips > 0)
            {
                SdLogger.Event("StatePublish", "State publishing resumed after {0} skipped tick(s)", _consecutiveSkips);
                _consecutiveSkips = 0;
            }

            bool sendStarted = false;
            try
            {
                var availableAccounts = _resolver.GetAccountNames();
                if (availableAccounts.Count > 0 &&
                    (string.IsNullOrWhiteSpace(_trackedAccount) ||
                     !availableAccounts.Any(a => string.Equals(a, _trackedAccount, StringComparison.OrdinalIgnoreCase))))
                {
                    _trackedAccount = availableAccounts[0];
                    SdLogger.Info("Account auto-selected from active list: {0}", _trackedAccount);
                }
                else if (availableAccounts.Count == 0 && !string.IsNullOrWhiteSpace(_trackedAccount))
                {
                    _trackedAccount = string.Empty;
                    SdLogger.Info("Tracked account cleared because no active accounts are available.");
                }

                var account = _resolver.FindAccount(_trackedAccount);
                var instrument = _resolver.FindInstrument(_trackedInstrument);

                // Keep order-rejection reporting bound to the account actually being traded
                if (_orderMonitor != null) _orderMonitor.Track(account);

                // Same idea for the trend: it follows the instrument being traded, and reloads its
                // bars by itself when that instrument really changes. Cheap and silent otherwise —
                // this runs twice a second.
                if (_trendMonitor != null) _trendMonitor.SetInstrument(instrument);

                var state = BuildState(account, instrument, availableAccounts);
                LogStateTransitions(state as Dictionary<string, object>);

                var msg = BridgeMessage.CreateEvent("stateUpdate", state);

                var sendTask = _bridgeClient.SendAsync(msg);
                sendStarted = true;
                // Release the guard only once the send has actually completed
                sendTask.ContinueWith(_ => Interlocked.Exchange(ref _publishing, 0));
            }
            catch (Exception ex)
            {
                SdLogger.Fail("StatePublish", ex, "Failed to publish state for {0} / {1}",
                    _trackedAccount, _trackedInstrument);
            }
            finally
            {
                if (!sendStarted) Interlocked.Exchange(ref _publishing, 0);
            }
        }

        /// <summary>
        /// Logs the position and its protective orders whenever they change.
        ///
        /// This is the trading narrative of the day: entries, exits, stop and target moves, all
        /// timestamped. It is written from the published state rather than from the commands so
        /// it also captures what happened outside the deck (a manual NinjaTrader action, a stop
        /// being hit) — exactly the events that make a deck display look "wrong" later on.
        /// </summary>
        private void LogStateTransitions(Dictionary<string, object> state)
        {
            if (state == null) return;

            try
            {
                object positionObj;
                if (!state.TryGetValue("position", out positionObj)) return;

                var position = positionObj as Dictionary<string, object>;
                if (position == null)
                {
                    // No account or no instrument resolved — nothing to compare against.
                    if (_lastPositionSignature != null)
                    {
                        SdLogger.Event("Position", "Position tracking lost — account or instrument unresolved ({0} / {1})",
                            _trackedAccount, _trackedInstrument);
                        _lastPositionSignature = null;
                        _lastProtectionSignature = null;
                    }
                    return;
                }

                var direction = Text(position, "direction");
                var quantity = Text(position, "quantity");
                var averagePrice = Text(position, "averagePrice");

                var positionSignature = direction + "|" + quantity + "|" + averagePrice;
                if (positionSignature != _lastPositionSignature)
                {
                    SdLogger.Event("Position", "Position {0} → {1} qty={2} avgPrice={3} unrealizedPnl={4}",
                        _lastPositionSignature ?? "(unknown)", direction, quantity, averagePrice,
                        Text(position, "unrealizedPnl"));
                    _lastPositionSignature = positionSignature;
                }

                var protectionSignature = string.Join("|", new[]
                {
                    Text(position, "stopOrderCount"), Text(position, "stopPrice"),
                    Text(position, "targetOrderCount"), Text(position, "targetPrice"),
                    Text(position, "activeOrderCount")
                });

                if (protectionSignature != _lastProtectionSignature)
                {
                    SdLogger.Event("Protection", "Orders changed — stops={0} @ {1}, targets={2} @ {3}, active={4}",
                        Text(position, "stopOrderCount"), Text(position, "stopPrice"),
                        Text(position, "targetOrderCount"), Text(position, "targetPrice"),
                        Text(position, "activeOrderCount"));
                    _lastProtectionSignature = protectionSignature;
                }
            }
            catch (Exception ex)
            {
                // Diagnostics must never break the publish they observe.
                SdLogger.Fail("StatePublish", ex, "Failed to log state transition");
            }
        }

        private static string Text(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null) return "-";
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private object BuildState(Account account, Instrument instrument, List<string> availableAccounts)
        {
            var accountDict = new Dictionary<string, object>();
            accountDict["name"] = account != null ? account.Name : _trackedAccount;
            accountDict["connected"] = account != null;

            Dictionary<string, object> instrumentDict = null;
            Dictionary<string, object> positionDict = null;
            double positionUnrealized = 0;

            if (instrument != null)
            {
                double lastPrice = 0;
                try { lastPrice = instrument.MarketData.Last.Price; } catch { }

                instrumentDict = new Dictionary<string, object>();
                instrumentDict["name"] = instrument.FullName;
                instrumentDict["lastPrice"] = lastPrice;
                instrumentDict["tickSize"] = instrument.MasterInstrument.TickSize;
                double pointValue = instrument.MasterInstrument.PointValue;
                instrumentDict["pointValue"] = pointValue > 0 ? pointValue : 1.0;

                if (account != null)
                {
                    var position = _resolver.FindPosition(account, instrument);
                    positionDict = new Dictionary<string, object>();

                    if (position != null)
                    {
                        var stops = _resolver.FindStopOrders(account, instrument);
                        var targets = _resolver.FindTargetOrders(account, instrument);
                        var allActive = _resolver.FindActiveOrders(account, instrument);

                        positionUnrealized = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency);

                        // Show the stop that actually protects the position (tightest), not an
                        // arbitrary one, and the nearest target in the position's direction.
                        var tightestStop = _resolver.FindTightestStopOrder(account, instrument, position.MarketPosition);
                        var nearestTarget = targets.Count == 0
                            ? null
                            : position.MarketPosition == MarketPosition.Long ? targets[0] : targets[targets.Count - 1];

                        positionDict["exists"] = true;
                        positionDict["direction"] = position.MarketPosition.ToString();
                        positionDict["quantity"] = (int)Math.Abs(position.Quantity);
                        positionDict["averagePrice"] = position.AveragePrice;
                        positionDict["unrealizedPnl"] = positionUnrealized;
                        positionDict["hasStopOrder"] = stops.Count > 0;
                        positionDict["stopPrice"] = tightestStop != null ? tightestStop.StopPrice : 0.0;
                        positionDict["stopOrderCount"] = stops.Count;
                        positionDict["hasTargetOrder"] = targets.Count > 0;
                        positionDict["targetPrice"] = nearestTarget != null ? nearestTarget.LimitPrice : 0.0;
                        positionDict["targetOrderCount"] = targets.Count;
                        positionDict["activeOrderCount"] = allActive.Count;
                    }
                    else
                    {
                        positionDict["exists"] = false;
                        positionDict["direction"] = "Flat";
                        positionDict["quantity"] = 0;
                        positionDict["averagePrice"] = 0.0;
                        positionDict["unrealizedPnl"] = 0.0;
                        positionDict["hasStopOrder"] = false;
                        positionDict["stopPrice"] = 0.0;
                        // Counts must be present on BOTH branches: the protocol declares them
                        // required, and omitting them made the protection signature flip between
                        // "-" and a number on every open/close, logging a spurious transition.
                        positionDict["stopOrderCount"] = 0;
                        positionDict["hasTargetOrder"] = false;
                        positionDict["targetPrice"] = 0.0;
                        positionDict["targetOrderCount"] = 0;
                        positionDict["activeOrderCount"] = 0;
                    }
                }
            }

            AddAccountPnl(accountDict, account, positionUnrealized);

            var state = new Dictionary<string, object>();
            state["connected"] = true;
            state["account"] = accountDict;
            state["instrument"] = instrumentDict;
            state["position"] = positionDict;
            state["availableAccounts"] = availableAccounts;
            if (_trendMonitor != null) state["trend"] = _trendMonitor.BuildState();
            return state;
        }

        /// <summary>
        /// Publishes the account-wide realized/unrealized P&amp;L. The bridge's safety macro
        /// measures the daily loss from these, so realized P&amp;L matters as much as open P&amp;L.
        /// pnlAvailable=false tells the bridge the P&amp;L-based rules have no data to work with,
        /// which surfaces on the Stream Deck instead of failing silently.
        /// </summary>
        private static DateTime _lastPnlWarning = DateTime.MinValue;

        private static void AddAccountPnl(Dictionary<string, object> accountDict, Account account, double positionUnrealized)
        {
            double realized = 0;
            double unrealized = positionUnrealized;
            bool available = false;

            if (account != null)
            {
                try
                {
                    var denomination = account.Denomination;
                    realized = account.Get(AccountItem.RealizedProfitLoss, denomination);
                    available = true;

                    try
                    {
                        unrealized = account.Get(AccountItem.UnrealizedProfitLoss, denomination);
                    }
                    catch
                    {
                        // Not every provider exposes account-level open P&L — the tracked
                        // position's unrealized P&L is the closest available substitute.
                        unrealized = positionUnrealized;
                    }
                }
                catch (Exception ex)
                {
                    // Throttled: this runs on every publish tick, so an unsupported provider
                    // would otherwise write the same warning several times a second all day.
                    if ((DateTime.UtcNow - _lastPnlWarning).TotalMinutes >= 1)
                    {
                        _lastPnlWarning = DateTime.UtcNow;
                        SdLogger.EventWarn("Pnl",
                            "Account P&L unavailable for {0}: {1} — {2} (safety macro loss limits have no data)",
                            account.Name, ex.GetType().Name, ex.Message);
                    }
                }
            }

            accountDict["realizedPnl"] = realized;
            accountDict["unrealizedPnl"] = unrealized;
            accountDict["pnlAvailable"] = available;
            AddAccountBalance(accountDict, account);
        }

        /// <summary>
        /// Account cash value, forwarded so the Bitlearn journal can start from the real capital.
        ///
        /// Without it the journal opens at zero, and every percentage it computes is meaningless —
        /// a gain of 200 on nothing is not a percentage. The P&amp;L alone cannot supply it: it says
        /// what changed, never what it changed FROM.
        ///
        /// Kept separate from <see cref="AddAccountPnl"/> because it fails independently: a provider
        /// can expose P&amp;L and not cash value, and losing the balance must not cost us the P&amp;L on
        /// which the safety macro's loss limits depend.
        /// </summary>
        private static DateTime _lastBalanceWarning = DateTime.MinValue;

        /// <summary>
        /// Sources du solde, dans l'ordre de préférence.
        ///
        /// Une seule ne suffit pas : `CashValue` revient NaN sur un compte de simulation dont le
        /// solde est pourtant affiché par la plateforme, et les fournisseurs ne remplissent pas
        /// tous les mêmes champs. On prend le premier utilisable plutôt que d'exiger le bon.
        ///
        /// `NetLiquidation` vient juste après : c'est la valeur liquidative, celle que le trader
        /// lit comme « son solde ». Les deux valeurs de début de séance ferment la marche — mieux
        /// vaut un capital de ce matin qu'aucun capital.
        /// </summary>
        private static readonly AccountItem[] BalanceItems =
        {
            AccountItem.CashValue,
            AccountItem.NetLiquidation,
            AccountItem.TotalCashBalance,
            AccountItem.SodCashValue,
            AccountItem.SodLiquidatingValue,
        };

        /// <summary>Dernière source retenue, pour ne journaliser qu'au changement.</summary>
        private static string _balanceSource;

        private static void AddAccountBalance(Dictionary<string, object> accountDict, Account account)
        {
            if (account == null) return;

            var tentatives = new List<string>();

            foreach (var item in BalanceItems)
            {
                double valeur;
                try
                {
                    valeur = account.Get(item, account.Denomination);
                }
                catch (Exception ex)
                {
                    tentatives.Add(item + "=" + ex.GetType().Name);
                    continue;
                }

                // NOT every unavailable balance throws. Several providers return NaN instead — no
                // exception, so a lone try/catch never fired. SimpleJson then wrote `null`
                // (correctly: NaN is not valid JSON), the journal kept opening at zero, and nothing
                // said why. Asking a single item was the mistake: `CashValue` comes back NaN on a
                // simulation account whose balance is plainly visible in the platform.
                if (double.IsNaN(valeur) || double.IsInfinity(valeur))
                {
                    tentatives.Add(item + "=NaN");
                    continue;
                }

                accountDict["cashValue"] = valeur;

                // Journalisé au CHANGEMENT de source, pas à chaque tic : savoir quel champ porte
                // réellement le solde est la première question qu'on se pose quand un journal
                // s'ouvre au mauvais capital.
                if (_balanceSource != item.ToString())
                {
                    _balanceSource = item.ToString();
                    SdLogger.Event("Account", "Cash value read from {0} for {1}: {2}",
                        item, account.Name, valeur.ToString("0.00", CultureInfo.InvariantCulture));
                }
                return;
            }

            _balanceSource = null;
            // Toutes les sources ont échoué : on dit lesquelles et pourquoi, sinon le diagnostic
            // repart de zéro à chaque fois.
            if ((DateTime.UtcNow - _lastBalanceWarning).TotalMinutes >= 1)
            {
                _lastBalanceWarning = DateTime.UtcNow;
                SdLogger.EventWarn("Account",
                    "No usable cash value for {0} ({1}) — the Bitlearn journal will keep its current starting capital",
                    account.Name, string.Join(", ", tentatives));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_stateTimer != null) _stateTimer.Dispose();
        }
    }
}
