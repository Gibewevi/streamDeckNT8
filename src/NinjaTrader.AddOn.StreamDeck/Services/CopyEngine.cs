using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Models;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Mirrors the selected account's orders onto follower accounts, inside NinjaTrader.
    ///
    /// PORTED FROM REPEATER9000 (github.com/nikos-repos/REPEATER9000). Its core mechanisms are
    /// sound and are reproduced here on purpose — the per-follower submit worker, the
    /// converge-to-target link model, the two-pass registration, and above all the follower-side
    /// OCO group. The comments that explain WHY each of those exists are kept: every one of them
    /// describes a way a copied bracket can end up protecting nothing.
    ///
    /// WHAT WAS FIXED IN THE PORT.
    ///   - Accounts are re-resolved on every state publish. The original cached them once, at
    ///     window construction, and never subscribed to connection changes: an account that
    ///     reconnected mid-session was silently never copied to again while the screen still
    ///     showed it configured.
    ///   - Per-follower multiplier and contract cap. The original copied the master quantity
    ///     verbatim, which is unusable across accounts of different sizes.
    ///   - A follower rejection is reported instead of being treated like a cancellation.
    ///   - Drift between master and follower is detected — see <see cref="EvaluateDrift"/>.
    ///
    /// THE RULE THAT OUTRANKS EVERYTHING ELSE HERE. This class never sends an order to correct a
    /// divergence. It copies what the master does, and when it can no longer be sure the follower
    /// mirrors the master, it STOPS copying entries to that follower and says so. A copier that
    /// "repairs" a divergence it measured wrongly fires unsolicited market orders into a live
    /// account, and that is how a copier empties one. Correction is a trader's gesture.
    /// </summary>
    public class CopyEngine : IDisposable
    {
        /// <summary>
        /// Name carried by every copied order. Diagnostic only — <see cref="GuardEnforcer"/>
        /// recognises copies by order id, never by this string, because a name can be typed by
        /// hand into a DOM and an id cannot.
        /// </summary>
        public const string CopyOrderName = "StreamDeckCopy";

        /// <summary>
        /// Each follower multiplies the exposure of one key press and lengthens the submit path.
        /// The cap is a deliberate refusal, not a technical limit.
        /// </summary>
        public const int MaxFollowers = 8;

        /// <summary>
        /// A drift is only judged once nothing is in flight for that follower/instrument pair.
        /// Without this delay every copied entry would read as a drift during the few hundred
        /// milliseconds between the master fill and the follower fill.
        /// </summary>
        private const int DriftSettleMs = 3000;

        /// <summary>
        /// Bridges the gap between an entry fill and the position becoming visible on the account,
        /// so a bracket copy arriving in that window is not mistaken for a naked exit.
        /// </summary>
        private const int EntryFillGraceMs = 10000;

        // --- Configuration -------------------------------------------------------------------
        //
        // Immutable and swapped atomically. The original kept a plain bool and a plain Dictionary
        // that the UI thread wrote while account event threads read them.

        private sealed class FollowerSpec
        {
            public readonly string Name;
            public readonly double Multiplier;
            public readonly int MaxContracts;

            public FollowerSpec(string name, double multiplier, int maxContracts)
            {
                Name = name;
                Multiplier = multiplier;
                MaxContracts = maxContracts;
            }
        }

        private sealed class CopierConfig
        {
            public static readonly CopierConfig Empty =
                new CopierConfig(false, string.Empty, new FollowerSpec[0], false);

            public readonly bool Enabled;
            public readonly string Master;
            public readonly FollowerSpec[] Followers;
            public readonly bool EntriesBlocked;

            public CopierConfig(bool enabled, string master, FollowerSpec[] followers, bool entriesBlocked)
            {
                Enabled = enabled;
                Master = master ?? string.Empty;
                Followers = followers ?? new FollowerSpec[0];
                EntriesBlocked = entriesBlocked;
            }

            public CopierConfig WithEnabled(bool enabled)
            {
                return new CopierConfig(enabled, Master, Followers, EntriesBlocked);
            }
        }

        /// <summary>A follower spec bound to the NinjaTrader account it resolved to.</summary>
        private sealed class FollowerRoute
        {
            public readonly FollowerSpec Spec;
            public readonly Account Account;

            public FollowerRoute(FollowerSpec spec, Account account)
            {
                Spec = spec;
                Account = account;
            }
        }

        /// <summary>
        /// Per-follower health, keyed by account name so it survives a route rebuild. Drift and the
        /// last refusal must not be forgotten just because the account list was refreshed.
        /// </summary>
        private sealed class FollowerHealth
        {
            public volatile bool Drifted;
            public int Drift;
            public string DriftInstrument = string.Empty;
            public string LastError = string.Empty;
        }

        // --- Order mirroring state -----------------------------------------------------------
        //
        // Every map below is touched from account event threads AND from submit workers.

        /// <summary>
        /// One mapped follower order per master order per follower. The master is the source of
        /// truth; the link carries the latest master state the follower order must converge to.
        /// </summary>
        private sealed class OrderLink
        {
            public readonly object Gate = new object();
            public readonly Account MasterAccount;
            public readonly long MasterOrderId;
            public readonly string MasterOco;
            public readonly string FollowerName;
            public readonly Account FollowerAccount;
            public readonly Instrument Instrument;
            public readonly OrderAction OrderAction;
            public readonly OrderType OrderType;
            public readonly TimeInForce TimeInForce;
            public readonly string FollowerOco;
            public readonly bool IsExit;

            public Order FollowerOrder;
            public volatile bool IsSubmitted;
            public volatile bool CancelRequested;
            public volatile bool IsTerminal;
            public volatile bool IsFilled;
            public volatile bool MasterTerminal;
            public int SyncQueued;

            public double TargetLimitPrice;
            public double TargetStopPrice;
            public int TargetQuantity;

            public OrderLink(Account masterAccount, long masterOrderId, string masterOco,
                FollowerRoute route, Instrument instrument, OrderAction orderAction,
                OrderType orderType, TimeInForce timeInForce, string followerOco, bool isExit,
                int quantity, double limitPrice, double stopPrice)
            {
                MasterAccount = masterAccount;
                MasterOrderId = masterOrderId;
                MasterOco = masterOco ?? string.Empty;
                FollowerName = route.Spec.Name;
                FollowerAccount = route.Account;
                Instrument = instrument;
                OrderAction = orderAction;
                OrderType = orderType;
                TimeInForce = timeInForce;
                FollowerOco = followerOco ?? string.Empty;
                IsExit = isExit;
                TargetQuantity = quantity;
                TargetLimitPrice = limitPrice;
                TargetStopPrice = stopPrice;
            }
        }

        private sealed class CopyRequest
        {
            public readonly OrderLink Link;
            public readonly bool IsCreate;

            public CopyRequest(OrderLink link, bool isCreate)
            {
                Link = link;
                IsCreate = isCreate;
            }
        }

        private sealed class SubmitQueue
        {
            public readonly ConcurrentQueue<CopyRequest> Requests = new ConcurrentQueue<CopyRequest>();
            public readonly ManualResetEventSlim Signal = new ManualResetEventSlim(false);
            public volatile bool IsCompleted;
        }

        private readonly ContextResolver _resolver;
        private readonly BridgeClient _bridgeClient;
        private readonly GuardEnforcer _enforcer;

        private volatile CopierConfig _config = CopierConfig.Empty;
        private volatile FollowerRoute[] _routes = new FollowerRoute[0];
        private volatile Account _masterAccount;

        private readonly ConcurrentDictionary<string, FollowerHealth> _health =
            new ConcurrentDictionary<string, FollowerHealth>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, SubmitQueue> _submitQueues =
            new ConcurrentDictionary<string, SubmitQueue>(StringComparer.Ordinal);

        // Deduplicates only the INITIAL copy of a master order. It never suppresses later
        // change/cancel/fill mirroring — that flows through the link maps.
        private readonly ConcurrentDictionary<long, byte> _copiedMasterOrders =
            new ConcurrentDictionary<long, byte>();

        private readonly ConcurrentDictionary<string, OrderLink> _linksByMasterFollower =
            new ConcurrentDictionary<string, OrderLink>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, OrderLink> _linksByFollowerOrderId =
            new ConcurrentDictionary<long, OrderLink>();
        private readonly ConcurrentDictionary<long, List<OrderLink>> _linksByMasterOrderId =
            new ConcurrentDictionary<long, List<OrderLink>>();
        private readonly ConcurrentDictionary<string, string> _followerOcoByMasterOco =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<OrderLink>> _followerOcoGroups =
            new ConcurrentDictionary<string, List<OrderLink>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _masterOcoFillSeen =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _activeEntryLinks =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _recentEntryFills =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Last copy activity per follower/instrument, gating the drift verdict.</summary>
        private readonly ConcurrentDictionary<string, long> _lastCopyActivity =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        private readonly object _submissionGate = new object();
        private readonly object _subscriptionLock = new object();
        private readonly HashSet<Account> _subscribed = new HashSet<Account>();
        private string _subscriptionSignature = string.Empty;

        private volatile bool _disposed;
        private int _refreshing;
        private int _copiedToday;
        private string _copiedTodayDate = string.Empty;

        public CopyEngine(ContextResolver resolver, BridgeClient bridgeClient, GuardEnforcer enforcer)
        {
            _resolver = resolver;
            _bridgeClient = bridgeClient;
            _enforcer = enforcer;
        }

        // =====================================================================================
        // Configuration
        // =====================================================================================

        /// <summary>
        /// Adopts the configuration the bridge publishes. The bridge owns and persists it; this
        /// side never invents one, and a payload it cannot understand disables copying rather than
        /// falling back to a guess.
        /// </summary>
        public void Configure(BridgeMessage message)
        {
            var enabled = message.GetPayloadBool("enabled");
            var master = message.GetPayloadString("master") ?? string.Empty;
            var entriesBlocked = message.GetPayloadBool("entriesBlocked");

            var followers = ParseFollowers(message);

            var previous = _config;
            var next = new CopierConfig(enabled, master, followers, entriesBlocked);
            _config = next;

            // The follower positions belong to the PREVIOUS master. Keeping the links alive across
            // a master change would let two different sources converge onto one follower position,
            // which is exactly the divergence the drift rule exists to prevent. The bridge disables
            // copying on its side for the same reason; dropping the links here is the local half.
            if (!string.Equals(previous.Master, next.Master, StringComparison.OrdinalIgnoreCase)
                && previous.Master.Length > 0)
            {
                SdLogger.EventWarn("Copier",
                    "Master account changed {0} → {1} — mappings dropped, copied positions are now unmanaged",
                    previous.Master, next.Master);
                ClearLinks();
            }

            if (previous.Enabled != next.Enabled)
            {
                SdLogger.Event("Copier", "Copying {0} — master={1} followers={2}",
                    next.Enabled ? "ENABLED" : "DISABLED",
                    next.Master.Length > 0 ? next.Master : "(none)",
                    next.Followers.Length);
            }

            // Forget stale refusals when the trader re-enables: a rejection from an hour ago must
            // not keep the key red on a fresh session.
            if (!previous.Enabled && next.Enabled)
            {
                foreach (var health in _health.Values)
                    health.LastError = string.Empty;
            }
        }

        private static FollowerSpec[] ParseFollowers(BridgeMessage message)
        {
            var list = new List<FollowerSpec>();
            if (message.Payload == null) return list.ToArray();

            object raw;
            if (!message.Payload.TryGetValue("followers", out raw)) return list.ToArray();

            var items = raw as List<object>;
            if (items == null) return list.ToArray();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var dict = item as Dictionary<string, object>;
                if (dict == null) continue;

                var name = SimpleJson.GetString(dict, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = name.Trim();
                if (!seen.Add(name)) continue;

                // A malformed number reads as absent, never as an exception: SimpleJson's getters
                // return null rather than throwing, and the defaults below are the safe ones —
                // multiplier 1 copies like-for-like, cap 0 means "no cap of my own".
                var multiplier = SimpleJson.GetDouble(dict, "multiplier");
                var maxContracts = SimpleJson.GetInt(dict, "maxContracts");

                var m = multiplier.HasValue && multiplier.Value >= 0 && multiplier.Value <= 100
                    ? multiplier.Value
                    : 1.0;
                var cap = maxContracts.HasValue && maxContracts.Value > 0 ? maxContracts.Value : 0;

                list.Add(new FollowerSpec(name, m, cap));
                if (list.Count >= MaxFollowers) break;
            }

            return list.ToArray();
        }

        // =====================================================================================
        // Periodic refresh — resolution, subscriptions, drift
        // =====================================================================================

        /// <summary>
        /// Called from the state publisher on every tick. Re-resolves accounts, keeps the
        /// subscriptions in step, and evaluates drift.
        ///
        /// Re-resolving twice a second rather than caching once is the fix for the original's
        /// worst defect: an account that reconnects mid-session comes back on its own here, and a
        /// follower that went away is reported as unresolved instead of silently skipped.
        ///
        /// Never throws — it runs on the publish path, which must not be broken by the copier.
        /// </summary>
        public void Refresh(List<string> availableAccounts)
        {
            if (_disposed) return;

            // Two overlapping publish ticks would otherwise rebuild the routes and re-judge drift
            // at the same time, and the subscription signature is read before the lock that acts
            // on it — enough for one pass to subscribe while the other unsubscribes. A skipped
            // refresh costs half a second; a torn one costs the order feed.
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

            try
            {
                var cfg = _config;

                if (!cfg.Enabled || cfg.Master.Length == 0 || cfg.Followers.Length == 0)
                {
                    if (_subscriptionSignature.Length > 0)
                    {
                        Unsubscribe();
                        _routes = new FollowerRoute[0];
                        _masterAccount = null;
                    }
                    return;
                }

                var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (availableAccounts != null)
                {
                    for (int i = 0; i < availableAccounts.Count; i++)
                        available.Add(availableAccounts[i]);
                }

                var master = ResolveAccount(cfg.Master, available);
                _masterAccount = master;

                var routes = new FollowerRoute[cfg.Followers.Length];
                for (int i = 0; i < cfg.Followers.Length; i++)
                {
                    var spec = cfg.Followers[i];
                    // A follower that is also the master would copy onto itself: doubling every
                    // order on one account. Excluded here as a backstop — the host already keeps
                    // followers out of the account cycle.
                    var account = string.Equals(spec.Name, cfg.Master, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : ResolveAccount(spec.Name, available);
                    routes[i] = new FollowerRoute(spec, account);
                }
                _routes = routes;

                SyncSubscriptions(master, routes);
                EvaluateDrift(master, routes);
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Copier refresh failed");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        }

        /// <summary>
        /// Resolves a name without going through <see cref="ContextResolver.FindAccount"/>'s
        /// not-found path, which warns. At two ticks a second a missing follower would otherwise
        /// write a warning line every 500 ms for the whole session.
        /// </summary>
        private Account ResolveAccount(string name, HashSet<string> available)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (!available.Contains(name)) return null;
            return _resolver.FindAccount(name);
        }

        private void SyncSubscriptions(Account master, FollowerRoute[] routes)
        {
            var signature = (master != null ? master.Name : "-") + "|";
            for (int i = 0; i < routes.Length; i++)
                signature += (routes[i].Account != null ? routes[i].Spec.Name : "?" + routes[i].Spec.Name) + ",";

            if (signature == _subscriptionSignature) return;

            lock (_subscriptionLock)
            {
                Unsubscribe();

                if (master != null && Subscribe(master))
                    SdLogger.Event("Copier", "Watching master account {0}", master.Name);

                for (int i = 0; i < routes.Length; i++)
                {
                    var account = routes[i].Account;
                    if (account != null) Subscribe(account);
                }

                _subscriptionSignature = signature;
            }

            SdLogger.Event("Copier", "Routes resolved — master={0} followers={1}",
                master != null ? master.Name : "UNRESOLVED", DescribeRoutes(routes));
        }

        private static string DescribeRoutes(FollowerRoute[] routes)
        {
            if (routes.Length == 0) return "(none)";
            var parts = new List<string>();
            for (int i = 0; i < routes.Length; i++)
            {
                var spec = routes[i].Spec;
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}{1}×{2}{3}",
                    routes[i].Account != null ? string.Empty : "!",
                    spec.Name, spec.Multiplier,
                    spec.MaxContracts > 0 ? "/cap" + spec.MaxContracts : string.Empty));
            }
            return string.Join(" ", parts.ToArray());
        }

        private bool Subscribe(Account account)
        {
            if (_subscribed.Contains(account)) return false;
            try
            {
                account.OrderUpdate += OnOrderUpdate;
                _subscribed.Add(account);
                return true;
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Could not subscribe to order updates on {0}", account.Name);
                return false;
            }
        }

        private void Unsubscribe()
        {
            foreach (var account in _subscribed)
            {
                try { account.OrderUpdate -= OnOrderUpdate; }
                catch (Exception ex) { SdLogger.Fail("Copier", ex, "Could not unsubscribe from {0}", account.Name); }
            }
            _subscribed.Clear();
            _subscriptionSignature = string.Empty;
        }

        // =====================================================================================
        // Drift — the rule that outranks everything else in this class
        // =====================================================================================

        /// <summary>
        /// Compares each follower's net position against what the master's position implies, and
        /// stops copying ENTRIES to any follower that has drifted. Exits keep being copied: a
        /// drifted follower may well be holding a position, and closing must always remain
        /// possible.
        ///
        /// NO ORDER IS EVER SENT FROM HERE. Not a catch-up market order, not a "flatten to start
        /// clean". A system that corrects a divergence it measured wrongly fires unsolicited market
        /// orders into a live account. The copier's job ends at: notice, stop, say so.
        ///
        /// Two kinds of gap, judged differently:
        ///   - OPPOSITE SIGN, or one side flat and the other not: always a drift. This is the
        ///     dangerous case — exposure nobody asked for.
        ///   - SIZE ONLY, same direction: judged only when no contract cap is active on that
        ///     follower. A cap creates a legitimate, permanent gap, and reporting it would make
        ///     the alert meaningless.
        /// </summary>
        private void EvaluateDrift(Account master, FollowerRoute[] routes)
        {
            if (master == null) return;

            var now = Stopwatch.GetTimestamp();

            for (int i = 0; i < routes.Length; i++)
            {
                var route = routes[i];
                if (route.Account == null) continue;
                if (route.Spec.Multiplier <= 0) continue;

                var health = GetHealth(route.Spec.Name);

                string driftedInstrument = null;
                int driftAmount = 0;

                foreach (var instrumentName in InstrumentsInPlay(master, route.Account))
                {
                    var scope = FollowerScopeKey(route.Spec.Name, instrumentName);

                    long lastActivity;
                    if (_lastCopyActivity.TryGetValue(scope, out lastActivity) &&
                        TicksToMilliseconds(now - lastActivity) < DriftSettleMs)
                        continue;

                    var masterNet = NetPosition(master, instrumentName);
                    var followerNet = NetPosition(route.Account, instrumentName);

                    var expected = ScaleQuantity(masterNet, route.Spec);
                    if (expected == 0 && masterNet != 0 && route.Spec.Multiplier > 0)
                    {
                        // The multiplier rounds this master position down to nothing. A flat
                        // follower is then correct, not drifted.
                        if (followerNet == 0) continue;
                    }

                    var opposite = Math.Sign(expected) != Math.Sign(followerNet);
                    var sizeGap = expected != followerNet;
                    var capActive = route.Spec.MaxContracts > 0;

                    if (opposite || (sizeGap && !capActive))
                    {
                        driftedInstrument = instrumentName;
                        driftAmount = followerNet - expected;
                        break;
                    }
                }

                var wasDrifted = health.Drifted;

                if (driftedInstrument != null)
                {
                    health.Drift = driftAmount;
                    health.DriftInstrument = driftedInstrument;
                    health.Drifted = true;

                    if (!wasDrifted)
                    {
                        SdLogger.EventWarn("Copier",
                            "DRIFT on {0} / {1} — follower is {2} contract(s) off what the master implies. "
                            + "Entry copies to this account are STOPPED; exits keep being copied. No corrective order will be sent.",
                            route.Spec.Name, driftedInstrument, driftAmount);
                        ReportViolation(route.Spec.Name, "drift", driftedInstrument, driftAmount, string.Empty);
                    }
                }
                else if (wasDrifted)
                {
                    // Cleared by the positions agreeing again — never by anything this class did.
                    health.Drifted = false;
                    health.Drift = 0;
                    health.DriftInstrument = string.Empty;
                    SdLogger.Event("Copier", "Drift cleared on {0} — entry copies resume", route.Spec.Name);
                }
            }
        }

        /// <summary>Instrument names either side currently holds a position in.</summary>
        private static IEnumerable<string> InstrumentsInPlay(Account master, Account follower)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddInstruments(master, names, seen);
            AddInstruments(follower, names, seen);
            return names;
        }

        private static void AddInstruments(Account account, List<string> names, HashSet<string> seen)
        {
            try
            {
                lock (account.Positions)
                {
                    foreach (Position position in account.Positions)
                    {
                        if (position.MarketPosition == MarketPosition.Flat) continue;
                        if (position.Instrument == null) continue;
                        var name = position.Instrument.FullName;
                        if (seen.Add(name)) names.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Could not enumerate positions on {0}", account.Name);
            }
        }

        /// <summary>Signed net position: positive long, negative short, zero flat.</summary>
        private static int NetPosition(Account account, string instrumentName)
        {
            try
            {
                lock (account.Positions)
                {
                    foreach (Position position in account.Positions)
                    {
                        if (position.Instrument == null) continue;
                        if (position.Instrument.FullName != instrumentName) continue;
                        if (position.MarketPosition == MarketPosition.Flat) continue;
                        var qty = (int)Math.Abs(position.Quantity);
                        return position.MarketPosition == MarketPosition.Long ? qty : -qty;
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Could not read position on {0}", account.Name);
            }
            return 0;
        }

        // =====================================================================================
        // Sizing
        // =====================================================================================

        /// <summary>
        /// Applies the follower's multiplier and cap. Rounds to the nearest whole contract, and
        /// a result of zero sends NOTHING — a follower set to take a third of the risk must not
        /// silently end up taking all of it because one contract cannot be divided.
        /// </summary>
        private static int ScaleQuantity(int masterQuantity, FollowerSpec spec)
        {
            if (masterQuantity == 0 || spec.Multiplier <= 0) return 0;

            var magnitude = Math.Abs(masterQuantity) * spec.Multiplier;
            var scaled = (int)Math.Round(magnitude, MidpointRounding.AwayFromZero);
            if (spec.MaxContracts > 0 && scaled > spec.MaxContracts) scaled = spec.MaxContracts;

            return masterQuantity < 0 ? -scaled : scaled;
        }

        // =====================================================================================
        // Order events
        // =====================================================================================

        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            if (_disposed) return;

            try
            {
                if (e == null) return;
                var order = e.Order;
                if (order == null) return;

                var account = sender as Account;
                if (account == null) return;

                var cfg = _config;
                if (string.Equals(account.Name, cfg.Master, StringComparison.OrdinalIgnoreCase))
                    HandleMasterOrderUpdate(cfg, account, order, e.OrderState);
                else
                    HandleFollowerOrderUpdate(order, e.OrderState);
            }
            catch (Exception ex)
            {
                // Never let an exception escape into NinjaTrader's event pipeline.
                SdLogger.Fail("Copier", ex, "Error handling order update");
            }
        }

        private void HandleMasterOrderUpdate(CopierConfig cfg, Account account, Order order, OrderState orderState)
        {
            var masterOrderId = order.Id;

            List<OrderLink> links;
            if (!_linksByMasterOrderId.TryGetValue(masterOrderId, out links))
            {
                if (IsCopyableSubmission(orderState))
                {
                    if (cfg.Enabled && IsCopyableMasterOrder(order))
                        CopyMasterOrder(cfg, account, order);
                    return;
                }

                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected ||
                    orderState == OrderState.Filled)
                {
                    byte claimed;
                    _copiedMasterOrders.TryRemove(masterOrderId, out claimed);

                    if (orderState == OrderState.Filled && !string.IsNullOrEmpty(order.Oco))
                        _masterOcoFillSeen[order.Oco] = 1;

                    // Fail closed: a master exit ended but no mapping exists for it (the add-on
                    // was reloaded mid-trade, which NinjaScript does on every recompile). Cancel
                    // matching unmanaged copied exits rather than guessing a remap.
                    if (orderState != OrderState.Rejected && IsExitOrder(account, order))
                        ReconcileUnmappedMasterExit(order);
                }
                return;
            }

            switch (orderState)
            {
                case OrderState.Accepted:
                case OrderState.Working:
                case OrderState.TriggerPending:
                    MirrorMasterOrderChange(order, links);
                    break;

                case OrderState.Filled:
                    HandleMasterOrderFilled(order, links);
                    break;

                case OrderState.Cancelled:
                    HandleMasterOrderCancelled(order, links);
                    break;

                case OrderState.Rejected:
                    CancelFollowerLinks(links);
                    CancelFollowerOcoGroups(links);
                    CleanupMasterOrder(masterOrderId);
                    break;
            }
        }

        private void CopyMasterOrder(CopierConfig cfg, Account account, Order order)
        {
            if (!_copiedMasterOrders.TryAdd(order.Id, 0)) return;

            var routes = _routes;
            if (routes.Length == 0) return;

            var instrument = order.Instrument;
            if (instrument == null) return;

            var isExit = IsExitOrder(account, order);

            // Guard is blocking entries: copies of entries stop with it. Exits keep flowing —
            // trapping a follower inside a position is the one outcome no rule here may produce.
            if (!isExit && cfg.EntriesBlocked)
            {
                SdLogger.Event("Copier", "Entry not copied — the safety macro is blocking entries");
                return;
            }

            var masterOco = order.Oco ?? string.Empty;
            var limitPrice = order.LimitPrice;
            var stopPrice = order.StopPrice;
            var masterQuantity = order.Quantity;
            var instrumentName = instrument.FullName;

            // Pass 1: build and register every link before any submit can run, so a master
            // change or cancel arriving milliseconds later always finds the mapping.
            var links = new List<OrderLink>();
            for (int i = 0; i < routes.Length; i++)
            {
                try
                {
                    var route = routes[i];
                    if (route.Account == null) continue;

                    var health = GetHealth(route.Spec.Name);

                    // A drifted follower stops receiving entries and keeps receiving exits.
                    if (!isExit && health.Drifted)
                    {
                        SdLogger.EventWarn("Copier",
                            "Entry not copied to {0} — account is in drift ({1} contracts on {2})",
                            route.Spec.Name, health.Drift, health.DriftInstrument);
                        continue;
                    }

                    if (!isExit && route.Spec.Multiplier <= 0) continue;

                    // Exits only go to followers with copied exposure to protect, or the order
                    // would open a position instead of closing one.
                    if (isExit && !CanCopyExitToFollower(route, instrumentName)) continue;

                    var quantity = Math.Abs(ScaleQuantity(masterQuantity, route.Spec));
                    if (quantity <= 0)
                    {
                        SdLogger.Event("Copier",
                            "Nothing copied to {0} — {1} × {2} rounds to zero contracts",
                            route.Spec.Name, masterQuantity, route.Spec.Multiplier);
                        continue;
                    }

                    var linkKey = LinkKey(order.Id, route.Spec.Name);
                    if (_linksByMasterFollower.ContainsKey(linkKey)) continue;

                    // One follower OCO id per master OCO group per follower. Sibling stop/target
                    // copies share it, so the venue's own OCO handling protects the bracket even
                    // if this copier lags.
                    var followerOco = string.Empty;
                    if (masterOco.Length > 0)
                    {
                        followerOco = _followerOcoByMasterOco.GetOrAdd(
                            masterOco + "|" + route.Spec.Name,
                            k => "SDCOPY" + Guid.NewGuid().ToString("N"));
                    }

                    var link = new OrderLink(account, order.Id, masterOco, route, instrument,
                        order.OrderAction, order.OrderType, order.TimeInForce, followerOco, isExit,
                        quantity, limitPrice, stopPrice);

                    _linksByMasterFollower[linkKey] = link;
                    links.Add(link);

                    if (followerOco.Length > 0)
                    {
                        var group = _followerOcoGroups.GetOrAdd(followerOco, k => new List<OrderLink>());
                        lock (group) group.Add(link);
                    }

                    if (!isExit) AddActiveEntryLinks(route.Spec.Name, instrumentName, 1);
                    MarkCopyActivity(route.Spec.Name, instrumentName);
                }
                catch (Exception ex)
                {
                    SdLogger.Fail("Copier", ex, "Could not prepare a copy for {0}", routes[i].Spec.Name);
                }
            }

            if (links.Count == 0) return;

            _linksByMasterOrderId[order.Id] = links;
            BumpCopiedToday();

            SdLogger.Event("Copier", "Copying {0} {1} qty={2} on {3} to {4} follower(s)",
                order.OrderAction, order.OrderType, masterQuantity, instrumentName, links.Count);

            // Pass 2: hand off to the per-follower submit workers.
            for (int i = 0; i < links.Count; i++)
            {
                var link = links[i];
                var queue = EnsureSubmitWorker(link.FollowerName);
                queue.Requests.Enqueue(new CopyRequest(link, true));
                queue.Signal.Set();
            }
        }

        /// <summary>
        /// The master moved a stop/target or changed quantity: converge the mapped follower orders
        /// via <c>Account.Change</c>. Never recreate the order — a recreated stop is a window with
        /// no protection at all.
        /// </summary>
        private void MirrorMasterOrderChange(Order masterOrder, List<OrderLink> links)
        {
            var limitPrice = masterOrder.LimitPrice;
            var stopPrice = masterOrder.StopPrice;
            var masterQuantity = masterOrder.Quantity;

            var snapshot = SnapshotLinks(links);
            for (int i = 0; i < snapshot.Length; i++)
            {
                var link = snapshot[i];
                var spec = FindSpec(link.FollowerName);
                var quantity = spec != null ? Math.Abs(ScaleQuantity(masterQuantity, spec)) : masterQuantity;

                var dirty = false;
                lock (link.Gate)
                {
                    if (link.IsTerminal || link.CancelRequested) continue;
                    if (link.TargetLimitPrice != limitPrice) { link.TargetLimitPrice = limitPrice; dirty = true; }
                    if (link.TargetStopPrice != stopPrice) { link.TargetStopPrice = stopPrice; dirty = true; }
                    if (quantity > 0 && link.TargetQuantity != quantity) { link.TargetQuantity = quantity; dirty = true; }
                }
                if (dirty) EnqueueLinkSync(link);
            }
        }

        private void HandleMasterOrderFilled(Order masterOrder, List<OrderLink> links)
        {
            var masterOco = masterOrder.Oco;
            if (!string.IsNullOrEmpty(masterOco)) _masterOcoFillSeen[masterOco] = 1;

            // Follower copies of a filled master order stay working ON PURPOSE: the follower
            // bracket resolves through its own fills and its own OCO. A follower stop is never
            // cancelled just because the master filled.
            var snapshot = SnapshotLinks(links);
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i].MasterTerminal = true;

            CleanupMasterOrder(masterOrder.Id);
        }

        private void HandleMasterOrderCancelled(Order masterOrder, List<OrderLink> links)
        {
            var masterOco = masterOrder.Oco;
            var ocoSiblingFilled = !string.IsNullOrEmpty(masterOco) && _masterOcoFillSeen.ContainsKey(masterOco);

            if (!ocoSiblingFilled)
            {
                // Genuine cancel — the trader or an ATM removed the order. Mirror it.
                CancelFollowerLinks(links);
            }
            else
            {
                // The master cancelled this order because its OCO sibling filled. The follower
                // bracket keeps protecting the follower position and resolves via its own OCO;
                // only enforce the cancel where the follower's own sibling already filled.
                var snapshot = SnapshotLinks(links);
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var link = snapshot[i];
                    link.MasterTerminal = true;
                    if (HasFilledFollowerSibling(link)) RequestLinkCancel(link);
                }
            }
            CleanupMasterOrder(masterOrder.Id);
        }

        private void HandleFollowerOrderUpdate(Order order, OrderState orderState)
        {
            OrderLink link;
            if (!_linksByFollowerOrderId.TryGetValue(order.Id, out link)) return;

            MarkCopyActivity(link.FollowerName, link.Instrument.FullName);

            if (orderState == OrderState.Filled)
            {
                lock (link.Gate)
                {
                    link.IsFilled = true;
                    link.IsTerminal = true;
                }

                if (link.IsExit)
                {
                    // Follower bracket cleanup must not wait for the master: if a follower target
                    // fills before the master's, the sibling stop is cancelled right here. The
                    // shared follower OCO id is the venue-side backstop for the same case.
                    CancelFollowerOcoSiblings(link);
                    SweepFollowerExitsIfMasterFlat(link);
                }
                else
                {
                    _recentEntryFills[FollowerScopeKey(link.FollowerName, link.Instrument.FullName)] =
                        Stopwatch.GetTimestamp();
                    AddActiveEntryLinks(link.FollowerName, link.Instrument.FullName, -1);
                }

                RemoveLink(link);
            }
            else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
            {
                lock (link.Gate) link.IsTerminal = true;

                // A rejection is NOT a cancellation. The original treated both the same way and
                // the trader had no way of learning that one account never took the trade.
                if (orderState == OrderState.Rejected)
                {
                    var health = GetHealth(link.FollowerName);
                    health.LastError = "rejected";

                    SdLogger.EventWarn("Copier",
                        "COPY REJECTED on {0} — {1} {2} qty={3} on {4}. That account did NOT take the trade.",
                        link.FollowerName, link.OrderAction, link.OrderType, link.TargetQuantity,
                        link.Instrument.FullName);
                    ReportViolation(link.FollowerName, "rejected", link.Instrument.FullName, 0, "rejected");

                    if (link.IsExit) CancelFollowerOcoSiblings(link);
                }

                if (!link.IsExit) AddActiveEntryLinks(link.FollowerName, link.Instrument.FullName, -1);

                RemoveLink(link);
            }
        }

        // =====================================================================================
        // Cancels and reconciliation
        // =====================================================================================

        private void CancelFollowerLinks(List<OrderLink> links)
        {
            var snapshot = SnapshotLinks(links);
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].MasterTerminal = true;
                RequestLinkCancel(snapshot[i]);
            }
        }

        /// <summary>
        /// Risk cancels are immediate: they run inline on the event thread, never through the
        /// submit queue. If the follower order is still being created the flag makes the worker
        /// fail it closed instead.
        /// </summary>
        private void RequestLinkCancel(OrderLink link)
        {
            Order followerOrder = null;
            lock (link.Gate)
            {
                if (link.IsTerminal) return;
                link.CancelRequested = true;
                if (link.FollowerOrder != null && link.IsSubmitted) followerOrder = link.FollowerOrder;
            }

            if (followerOrder != null) TryCancelOrder(link.FollowerAccount, followerOrder);
            else EnqueueLinkSync(link);
        }

        private void TryCancelOrder(Account account, Order order)
        {
            try
            {
                lock (_submissionGate)
                {
                    if (!_disposed && IsActiveOrderState(order.OrderState))
                        account.Cancel(new Order[] { order });
                }
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Could not cancel a copied order on {0}", account.Name);
            }
        }

        private void CancelFollowerOcoSiblings(OrderLink filledLink)
        {
            if (filledLink.FollowerOco.Length == 0) return;

            List<OrderLink> group;
            if (!_followerOcoGroups.TryGetValue(filledLink.FollowerOco, out group)) return;

            var members = SnapshotLinks(group);
            for (int i = 0; i < members.Length; i++)
            {
                if (!ReferenceEquals(members[i], filledLink)) RequestLinkCancel(members[i]);
            }
        }

        private void CancelFollowerOcoGroups(List<OrderLink> links)
        {
            var snapshot = SnapshotLinks(links);
            for (int i = 0; i < snapshot.Length; i++) CancelFollowerOcoSiblings(snapshot[i]);
        }

        private bool HasFilledFollowerSibling(OrderLink link)
        {
            if (link.FollowerOco.Length == 0) return false;

            List<OrderLink> group;
            if (!_followerOcoGroups.TryGetValue(link.FollowerOco, out group)) return false;

            var members = SnapshotLinks(group);
            for (int i = 0; i < members.Length; i++)
            {
                if (!ReferenceEquals(members[i], link) && members[i].IsFilled) return true;
            }
            return false;
        }

        /// <summary>
        /// Fail closed: after a follower take profit fills and the master is flat, no copied exit
        /// whose bracket already resolved may stay working. Groups still protecting an open
        /// follower position are left alone.
        /// </summary>
        private void SweepFollowerExitsIfMasterFlat(OrderLink filledExit)
        {
            var instrumentName = filledExit.Instrument.FullName;
            if (NetPosition(filledExit.MasterAccount, instrumentName) != 0) return;

            foreach (var pair in _linksByFollowerOrderId)
            {
                var link = pair.Value;
                if (!link.IsExit || link.IsTerminal) continue;
                if (!string.Equals(link.FollowerName, filledExit.FollowerName, StringComparison.Ordinal)) continue;
                if (link.Instrument.FullName != instrumentName) continue;

                var orphaned = link.FollowerOco.Length == 0
                    ? link.MasterTerminal
                    : HasFilledFollowerSibling(link);

                if (orphaned) RequestLinkCancel(link);
            }
        }

        /// <summary>
        /// Fail closed: a master exit went terminal but we hold no mapping for it — the usual cause
        /// is a NinjaScript recompile, which reloads this add-on mid-trade. Cancel the working
        /// copied exits we no longer manage rather than leaving them to fire on their own.
        /// </summary>
        private void ReconcileUnmappedMasterExit(Order masterOrder)
        {
            var routes = _routes;
            var instrumentName = masterOrder.Instrument != null ? masterOrder.Instrument.FullName : null;
            if (instrumentName == null) return;

            for (int i = 0; i < routes.Length; i++)
            {
                var followerAccount = routes[i].Account;
                if (followerAccount == null) continue;

                try
                {
                    List<Order> toCancel = null;
                    lock (followerAccount.Orders)
                    {
                        foreach (Order followerOrder in followerAccount.Orders)
                        {
                            if (!IsActiveOrderState(followerOrder.OrderState)) continue;
                            if (!IsExitOrder(followerAccount, followerOrder)) continue;
                            if (!string.Equals(followerOrder.Name, CopyOrderName, StringComparison.Ordinal)) continue;
                            if (followerOrder.Instrument == null ||
                                followerOrder.Instrument.FullName != instrumentName) continue;
                            if (_linksByFollowerOrderId.ContainsKey(followerOrder.Id)) continue;

                            if (toCancel == null) toCancel = new List<Order>();
                            toCancel.Add(followerOrder);
                        }
                    }

                    if (toCancel != null)
                    {
                        SdLogger.EventWarn("Copier",
                            "Cancelling {0} unmanaged copied exit(s) on {1} — the master exit ended without a mapping",
                            toCancel.Count, followerAccount.Name);
                        lock (_submissionGate)
                            if (!_disposed) followerAccount.Cancel(toCancel);
                    }
                }
                catch (Exception ex)
                {
                    SdLogger.Fail("Copier", ex, "Exit reconciliation failed on {0}", followerAccount.Name);
                }
            }
        }

        // =====================================================================================
        // Submit workers
        // =====================================================================================

        private SubmitQueue EnsureSubmitWorker(string followerName)
        {
            SubmitQueue queue;
            if (_submitQueues.TryGetValue(followerName, out queue)) return queue;

            queue = new SubmitQueue();
            if (_submitQueues.TryAdd(followerName, queue))
            {
                var worker = new Thread(() => ProcessSubmitQueue(queue));
                worker.IsBackground = true;
                worker.Name = "SDCopy " + followerName;
                worker.Start();
                return queue;
            }

            queue.Signal.Dispose();
            _submitQueues.TryGetValue(followerName, out queue);
            return queue;
        }

        private void ProcessSubmitQueue(SubmitQueue queue)
        {
            try
            {
                while (!queue.IsCompleted)
                {
                    CopyRequest request;
                    if (!queue.Requests.TryDequeue(out request))
                    {
                        queue.Signal.Reset();
                        if (!queue.Requests.IsEmpty) queue.Signal.Set();
                        else queue.Signal.Wait();
                        continue;
                    }

                    try
                    {
                        if (_disposed) continue;
                        if (request.IsCreate)
                        {
                            SubmitCopy(request.Link);
                        }
                        else
                        {
                            Interlocked.Exchange(ref request.Link.SyncQueued, 0);
                            SyncFollowerOrder(request.Link);
                        }
                    }
                    catch (Exception ex)
                    {
                        SdLogger.Fail("Copier", ex, "Submit worker iteration failed");
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Submit worker terminated");
            }
        }

        private void SubmitCopy(OrderLink link)
        {
            Order followerOrder = null;

            try
            {
                if (_disposed) { AbandonUnsubmittedLink(link); return; }

                double limitPrice = 0;
                double stopPrice = 0;
                int quantity = 0;
                var abandoned = false;

                lock (link.Gate)
                {
                    if (link.CancelRequested || link.IsTerminal)
                    {
                        link.IsTerminal = true;
                        abandoned = true;
                    }
                    else
                    {
                        limitPrice = link.TargetLimitPrice;
                        stopPrice = link.TargetStopPrice;
                        quantity = link.TargetQuantity;
                    }
                }

                if (abandoned) { AbandonUnsubmittedLink(link); return; }

                // The same overload TradingEngine uses, and deliberately not the newer one that
                // takes OrderEntry and a GTD date. That one needs `Core.Globals.MaxDate`, and
                // `Globals` exists in BOTH NinjaTrader.Core and NinjaTrader.Client: naming it
                // raises CS0433. Here that is a compile error; under NinjaScript on the trader's
                // machine it would be an all-or-nothing failure that takes their own indicators
                // and strategies down with ours. The obsolete flag is worth far less than that.
                followerOrder = link.FollowerAccount.CreateOrder(
                    link.Instrument,
                    link.OrderAction,
                    link.OrderType,
                    link.TimeInForce,
                    quantity,
                    limitPrice,
                    stopPrice,
                    link.FollowerOco,
                    CopyOrderName,
                    null);

                lock (link.Gate) link.FollowerOrder = followerOrder;
                _linksByFollowerOrderId[followerOrder.Id] = link;

                // Told to the enforcer BEFORE the submit. It cancels external orders that grow
                // exposure while the macro blocks, and a copy landing on the tracked account would
                // otherwise look external to it — the master order already carried the verdict.
                if (_enforcer != null) _enforcer.RegisterCopiedOrder(followerOrder);

                lock (_submissionGate)
                {
                    lock (link.Gate)
                    {
                        if (_disposed || link.CancelRequested || link.IsTerminal) abandoned = true;
                    }
                    if (!abandoned) link.FollowerAccount.Submit(new Order[] { followerOrder });
                }

                if (abandoned) { AbandonUnsubmittedLink(link); return; }

                var cancelNow = false;
                var resync = false;
                lock (link.Gate)
                {
                    link.IsSubmitted = true;
                    if (link.CancelRequested) cancelNow = true;
                    else if (link.TargetLimitPrice != limitPrice ||
                             link.TargetStopPrice != stopPrice ||
                             link.TargetQuantity != quantity) resync = true;
                }

                // The master may have moved or cancelled the order while this submit was in
                // flight; converge before going back to the queue.
                if (cancelNow) TryCancelOrder(link.FollowerAccount, followerOrder);
                else if (resync) SyncFollowerOrder(link);
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Copy to {0} failed", link.FollowerName);

                var health = GetHealth(link.FollowerName);
                health.LastError = ex.Message;
                ReportViolation(link.FollowerName, "submitFailed", link.Instrument.FullName, 0, ex.Message);

                lock (link.Gate) link.IsTerminal = true;
                if (!link.IsExit) AddActiveEntryLinks(link.FollowerName, link.Instrument.FullName, -1);
                RemoveLink(link);
            }
        }

        /// <summary>
        /// Converge a mapped follower order to the master's latest state: cancel if the master
        /// went away, otherwise Change price/quantity in place.
        /// </summary>
        private void SyncFollowerOrder(OrderLink link)
        {
            if (_disposed) return;

            Order followerOrder;
            bool cancelRequested;
            double limitPrice;
            double stopPrice;
            int quantity;

            lock (link.Gate)
            {
                if (link.IsTerminal || link.FollowerOrder == null || !link.IsSubmitted) return;
                followerOrder = link.FollowerOrder;
                cancelRequested = link.CancelRequested;
                limitPrice = link.TargetLimitPrice;
                stopPrice = link.TargetStopPrice;
                quantity = link.TargetQuantity;
            }

            if (cancelRequested)
            {
                TryCancelOrder(link.FollowerAccount, followerOrder);
                return;
            }

            if (!IsActiveOrderState(followerOrder.OrderState)) return;

            try
            {
                var changed = false;
                var orderType = followerOrder.OrderType;

                if ((orderType == OrderType.Limit || orderType == OrderType.StopLimit) &&
                    limitPrice > 0 && followerOrder.LimitPrice != limitPrice)
                {
                    followerOrder.LimitPriceChanged = limitPrice;
                    changed = true;
                }

                if ((orderType == OrderType.StopMarket || orderType == OrderType.StopLimit ||
                     orderType == OrderType.MIT) &&
                    stopPrice > 0 && followerOrder.StopPrice != stopPrice)
                {
                    followerOrder.StopPriceChanged = stopPrice;
                    changed = true;
                }

                if (quantity > 0 && followerOrder.Quantity != quantity)
                {
                    followerOrder.QuantityChanged = quantity;
                    changed = true;
                }

                if (changed)
                {
                    lock (_submissionGate)
                        if (!_disposed) link.FollowerAccount.Change(new Order[] { followerOrder });
                }
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Could not sync a copied order on {0}", link.FollowerName);
            }
        }

        private void EnqueueLinkSync(OrderLink link)
        {
            if (_disposed || Interlocked.Exchange(ref link.SyncQueued, 1) != 0) return;
            var queue = EnsureSubmitWorker(link.FollowerName);
            queue.Requests.Enqueue(new CopyRequest(link, false));
            queue.Signal.Set();
        }

        private void StopSubmitWorkers()
        {
            foreach (var queue in _submitQueues.Values)
            {
                try
                {
                    queue.IsCompleted = true;
                    queue.Signal.Set();
                }
                catch (Exception ex)
                {
                    SdLogger.Fail("Copier", ex, "Could not stop a submit worker");
                }
            }
        }

        // =====================================================================================
        // Panic
        // =====================================================================================

        /// <summary>
        /// Stops copying and flattens every resolved follower. This is the one place the engine
        /// sends orders of its own accord, and it is a deliberate trader gesture — never an
        /// automatic reaction to a measurement.
        /// </summary>
        public int PanicFlatten()
        {
            _config = _config.WithEnabled(false);

            var routes = _routes;
            var flattened = 0;

            for (int i = 0; i < routes.Length; i++)
            {
                var account = routes[i].Account;
                if (account == null) continue;

                try
                {
                    var instruments = new List<Instrument>();
                    lock (account.Positions)
                    {
                        foreach (Position position in account.Positions)
                        {
                            if (position.MarketPosition != MarketPosition.Flat && position.Instrument != null)
                                instruments.Add(position.Instrument);
                        }
                    }

                    if (instruments.Count > 0)
                    {
                        account.Flatten(instruments);
                        flattened++;
                    }
                }
                catch (Exception ex)
                {
                    SdLogger.Fail("Copier", ex, "Could not flatten {0}", account.Name);
                }
            }

            SdLogger.EventWarn("Copier", "PANIC — copying disabled, flatten requested on {0} follower account(s)", flattened);
            return flattened;
        }

        // =====================================================================================
        // State
        // =====================================================================================

        /// <summary>The <c>copier</c> block of the state publish. Must stay cheap: 2 Hz.</summary>
        public object BuildState()
        {
            var cfg = _config;
            var routes = _routes;

            var followers = new List<object>();
            for (int i = 0; i < routes.Length; i++)
            {
                var route = routes[i];
                var health = GetHealth(route.Spec.Name);

                var entry = new Dictionary<string, object>();
                entry["name"] = route.Spec.Name;
                entry["multiplier"] = route.Spec.Multiplier;
                entry["maxContracts"] = route.Spec.MaxContracts;
                entry["resolved"] = route.Account != null;
                entry["drifted"] = health.Drifted;
                entry["drift"] = health.Drift;
                entry["lastError"] = health.LastError ?? string.Empty;
                followers.Add(entry);
            }

            var state = new Dictionary<string, object>();
            state["enabled"] = cfg.Enabled;
            state["master"] = cfg.Master;
            state["masterResolved"] = _masterAccount != null;
            state["entriesBlocked"] = cfg.EntriesBlocked;
            state["followers"] = followers;
            state["copiedToday"] = _copiedToday;
            return state;
        }

        private FollowerHealth GetHealth(string followerName)
        {
            return _health.GetOrAdd(followerName, k => new FollowerHealth());
        }

        private FollowerSpec FindSpec(string followerName)
        {
            var cfg = _config;
            for (int i = 0; i < cfg.Followers.Length; i++)
            {
                if (string.Equals(cfg.Followers[i].Name, followerName, StringComparison.OrdinalIgnoreCase))
                    return cfg.Followers[i];
            }
            return null;
        }

        private void BumpCopiedToday()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (_copiedTodayDate != today)
            {
                _copiedTodayDate = today;
                Interlocked.Exchange(ref _copiedToday, 0);
            }
            Interlocked.Increment(ref _copiedToday);
        }

        private void ReportViolation(string follower, string reason, string instrument, int drift, string error)
        {
            try
            {
                var payload = new Dictionary<string, object>();
                payload["follower"] = follower;
                payload["reason"] = reason;
                payload["instrument"] = instrument ?? string.Empty;
                payload["drift"] = drift;
                if (!string.IsNullOrEmpty(error)) payload["error"] = error;

                // Fire-and-forget: this runs inside NinjaTrader's event pipeline and cannot wait.
                if (_bridgeClient != null)
                    _ = _bridgeClient.SendAsync(BridgeMessage.CreateEvent("copierViolation", payload));
            }
            catch (Exception ex)
            {
                SdLogger.Fail("Copier", ex, "Could not report a copier violation");
            }
        }

        // =====================================================================================
        // Helpers
        // =====================================================================================

        private void MarkCopyActivity(string followerName, string instrumentName)
        {
            _lastCopyActivity[FollowerScopeKey(followerName, instrumentName)] = Stopwatch.GetTimestamp();
        }

        private void AddActiveEntryLinks(string followerName, string instrumentName, int delta)
        {
            var key = FollowerScopeKey(followerName, instrumentName);
            var count = _activeEntryLinks.AddOrUpdate(key, delta > 0 ? delta : 0,
                (k, current) => Math.Max(0, current + delta));
            if (count == 0)
            {
                ((ICollection<KeyValuePair<string, int>>)_activeEntryLinks).Remove(
                    new KeyValuePair<string, int>(key, 0));
            }
        }

        /// <summary>
        /// A master exit is only copied to followers that have copied exposure to protect: a live
        /// copied entry order, a very recent entry fill, or an open position. This keeps a
        /// rejected or skipped follower from receiving a naked exit — which would OPEN a position
        /// in the opposite direction rather than close anything.
        /// </summary>
        private bool CanCopyExitToFollower(FollowerRoute route, string instrumentName)
        {
            var key = FollowerScopeKey(route.Spec.Name, instrumentName);

            int activeEntries;
            if (_activeEntryLinks.TryGetValue(key, out activeEntries) && activeEntries > 0) return true;

            long fillTicks;
            if (_recentEntryFills.TryGetValue(key, out fillTicks))
            {
                if (TicksToMilliseconds(Stopwatch.GetTimestamp() - fillTicks) < EntryFillGraceMs) return true;
                ((ICollection<KeyValuePair<string, long>>)_recentEntryFills).Remove(
                    new KeyValuePair<string, long>(key, fillTicks));
            }

            return NetPosition(route.Account, instrumentName) != 0;
        }

        private void AbandonUnsubmittedLink(OrderLink link)
        {
            lock (link.Gate) link.IsTerminal = true;
            if (!link.IsExit) AddActiveEntryLinks(link.FollowerName, link.Instrument.FullName, -1);
            RemoveLink(link);
        }

        private void CleanupMasterOrder(long masterOrderId)
        {
            List<OrderLink> links;
            _linksByMasterOrderId.TryRemove(masterOrderId, out links);
            byte claimed;
            _copiedMasterOrders.TryRemove(masterOrderId, out claimed);
        }

        private void RemoveLink(OrderLink link)
        {
            OrderLink removed;
            _linksByMasterFollower.TryRemove(LinkKey(link.MasterOrderId, link.FollowerName), out removed);

            Order followerOrder;
            lock (link.Gate) followerOrder = link.FollowerOrder;
            if (followerOrder != null)
            {
                OrderLink byId;
                _linksByFollowerOrderId.TryRemove(followerOrder.Id, out byId);
            }

            List<OrderLink> masterLinks;
            if (_linksByMasterOrderId.TryGetValue(link.MasterOrderId, out masterLinks))
            {
                lock (masterLinks) masterLinks.Remove(link);
            }

            // Filled links stay in their OCO group as fill memory until the whole group is
            // terminal; then the group record is dropped.
            if (link.FollowerOco.Length > 0)
            {
                List<OrderLink> group;
                if (_followerOcoGroups.TryGetValue(link.FollowerOco, out group))
                {
                    var allTerminal = true;
                    lock (group)
                    {
                        for (int i = 0; i < group.Count; i++)
                        {
                            if (!group[i].IsTerminal) { allTerminal = false; break; }
                        }
                    }

                    if (allTerminal)
                    {
                        List<OrderLink> gone;
                        _followerOcoGroups.TryRemove(link.FollowerOco, out gone);
                        string followerOco;
                        _followerOcoByMasterOco.TryRemove(link.MasterOco + "|" + link.FollowerName, out followerOco);
                        if (!HasFollowerOcoMappings(link.MasterOco))
                        {
                            byte fillSeen;
                            _masterOcoFillSeen.TryRemove(link.MasterOco, out fillSeen);
                        }
                    }
                }
            }
        }

        private bool HasFollowerOcoMappings(string masterOco)
        {
            if (string.IsNullOrEmpty(masterOco)) return false;
            var prefix = masterOco + "|";
            foreach (var key in _followerOcoByMasterOco.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Drops every mapping without touching the market. Called on a master change: the orders
        /// already on the follower accounts stay exactly where they are — cancelling a follower's
        /// protective stop because the trader switched accounts would be the worst possible
        /// reading of "clean up".
        /// </summary>
        private void ClearLinks()
        {
            _linksByMasterFollower.Clear();
            _linksByFollowerOrderId.Clear();
            _linksByMasterOrderId.Clear();
            _followerOcoByMasterOco.Clear();
            _followerOcoGroups.Clear();
            _masterOcoFillSeen.Clear();
            _activeEntryLinks.Clear();
            _recentEntryFills.Clear();
            _copiedMasterOrders.Clear();
        }

        private static OrderLink[] SnapshotLinks(List<OrderLink> links)
        {
            lock (links) return links.ToArray();
        }

        private static string LinkKey(long masterOrderId, string followerName)
        {
            return masterOrderId.ToString(CultureInfo.InvariantCulture) + "|" + followerName;
        }

        private static string FollowerScopeKey(string followerName, string instrumentName)
        {
            return followerName + "|" + instrumentName;
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static bool IsCopyableSubmission(OrderState orderState)
        {
            return orderState == OrderState.Submitted ||
                   orderState == OrderState.Accepted ||
                   orderState == OrderState.Working ||
                   orderState == OrderState.TriggerPending;
        }

        /// <summary>
        /// Entries and ATM stop/target children are all copied; the child orders carry the master
        /// OCO that drives follower-side OCO assignment.
        /// </summary>
        private static bool IsCopyableMasterOrder(Order order)
        {
            var action = order.OrderAction;
            if (action != OrderAction.Buy && action != OrderAction.SellShort &&
                action != OrderAction.Sell && action != OrderAction.BuyToCover)
                return false;

            var type = order.OrderType;
            return type == OrderType.Market || type == OrderType.Limit ||
                   type == OrderType.StopMarket || type == OrderType.StopLimit ||
                   type == OrderType.MIT;
        }

        /// <summary>
        /// A master order is an exit only if it closes against the master's live position in that
        /// instrument. A Sell placed while flat is a short ENTRY — NinjaTrader does not reliably
        /// use SellShort — so it must copy as an entry.
        /// </summary>
        private static bool IsExitOrder(Account account, Order order)
        {
            var action = order.OrderAction;
            if (action == OrderAction.BuyToCover) return true;
            if (action == OrderAction.SellShort) return false;
            if (order.Instrument == null) return false;

            var net = NetPosition(account, order.Instrument.FullName);
            return action == OrderAction.Sell ? net > 0 : net < 0;
        }

        private static bool IsActiveOrderState(OrderState state)
        {
            return state == OrderState.Submitted || state == OrderState.Accepted ||
                   state == OrderState.Working || state == OrderState.TriggerPending ||
                   state == OrderState.ChangePending || state == OrderState.ChangeSubmitted ||
                   state == OrderState.PartFilled;
        }

        public void Dispose()
        {
            if (_disposed) return;

            lock (_submissionGate) _disposed = true;

            lock (_subscriptionLock) Unsubscribe();
            StopSubmitWorkers();

            SdLogger.Event("Copier", "Copy engine stopped");
        }
    }
}
