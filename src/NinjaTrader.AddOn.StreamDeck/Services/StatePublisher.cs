using System;
using System.Collections.Generic;
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
        private Timer _stateTimer;
        private string _trackedAccount;
        private string _trackedInstrument;
        private bool _disposed;

        public string TrackedAccount
        {
            get { return _trackedAccount; }
        }

        public string TrackedInstrument
        {
            get { return _trackedInstrument; }
        }

        public StatePublisher(ContextResolver resolver, BridgeClient bridgeClient, AddOnConfig config)
        {
            _resolver = resolver;
            _bridgeClient = bridgeClient;
            _config = config;
        }

        public void Start(string accountName, string instrumentName)
        {
            if (!string.IsNullOrWhiteSpace(accountName))
                _trackedAccount = accountName;

            if (!string.IsNullOrWhiteSpace(instrumentName))
                _trackedInstrument = instrumentName;

            _stateTimer = new Timer(
                _ => PublishState(),
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

        private void PublishState()
        {
            if (!_bridgeClient.IsConnected) return;

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

                var state = BuildState(account, instrument, availableAccounts);
                var msg = BridgeMessage.CreateEvent("stateUpdate", state);
                _bridgeClient.SendAsync(msg).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Failed to publish state");
            }
        }

        private object BuildState(Account account, Instrument instrument, List<string> availableAccounts)
        {
            var accountDict = new Dictionary<string, object>();
            accountDict["name"] = account != null ? account.Name : _trackedAccount;
            accountDict["connected"] = account != null;

            Dictionary<string, object> instrumentDict = null;
            Dictionary<string, object> positionDict = null;

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

                        positionDict["exists"] = true;
                        positionDict["direction"] = position.MarketPosition.ToString();
                        positionDict["quantity"] = (int)Math.Abs(position.Quantity);
                        positionDict["averagePrice"] = position.AveragePrice;
                        positionDict["unrealizedPnl"] = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency);
                        positionDict["hasStopOrder"] = stops.Count > 0;
                        positionDict["stopPrice"] = stops.Count > 0 ? stops[0].StopPrice : 0.0;
                        positionDict["hasTargetOrder"] = targets.Count > 0;
                        positionDict["targetPrice"] = targets.Count > 0 ? targets[0].LimitPrice : 0.0;
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
                        positionDict["hasTargetOrder"] = false;
                        positionDict["targetPrice"] = 0.0;
                        positionDict["activeOrderCount"] = 0;
                    }
                }
            }

            var state = new Dictionary<string, object>();
            state["connected"] = true;
            state["account"] = accountDict;
            state["instrument"] = instrumentDict;
            state["position"] = positionDict;
            state["availableAccounts"] = availableAccounts;
            return state;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_stateTimer != null) _stateTimer.Dispose();
        }
    }
}
