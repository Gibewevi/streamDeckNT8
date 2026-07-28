using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Services
{
    /// <summary>
    /// Resolves trading context: account, instrument, position, orders.
    /// All lookups go through NinjaTrader's official API.
    /// </summary>
    public class ContextResolver
    {
        /// <summary>
        /// Finds an account by name from NT8's account list.
        /// </summary>
        public Account FindAccount(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName)) return null;

            lock (Connection.Connections)
            {
                foreach (Connection connection in Connection.Connections)
                {
                    if (connection == null || connection.Status != ConnectionStatus.Connected)
                        continue;

                    foreach (Account acct in connection.Accounts)
                    {
                        if (IsSelectableAccount(acct) &&
                            string.Equals(acct.Name, accountName, StringComparison.OrdinalIgnoreCase))
                            return acct;
                    }
                }
            }

            lock (Account.All)
            {
                foreach (Account acct in Account.All)
                {
                    if (IsSelectableAccount(acct) &&
                        string.Equals(acct.Name, accountName, StringComparison.OrdinalIgnoreCase))
                        return acct;
                }
            }

            SdLogger.Warn("Account not found: {0}", accountName);
            return null;
        }

        /// <summary>
        /// Returns account names that are currently selectable for trading.
        /// NinjaTrader keeps old broker accounts in Account.All, so filter out
        /// disabled/disconnected historical broker accounts before publishing to Stream Deck.
        /// </summary>
        public List<string> GetAccountNames()
        {
            var liveNames = new List<string>();
            var simNames = new List<string>();

            lock (Connection.Connections)
            {
                foreach (Connection connection in Connection.Connections)
                {
                    if (connection == null || connection.Status != ConnectionStatus.Connected)
                        continue;

                    foreach (Account acct in connection.Accounts)
                        AddSelectableAccount(acct, liveNames, simNames);
                }
            }

            lock (Account.All)
            {
                foreach (Account acct in Account.All)
                {
                    if (acct != null && acct.Provider == Provider.Simulator)
                        AddSelectableAccount(acct, liveNames, simNames);
                }
            }

            return FinalizeAccountNames(liveNames.Concat(simNames).ToList());
        }

        private static List<string> FinalizeAccountNames(List<string> names)
        {
            return PruneRolledAccountNames(names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        private static List<string> PruneRolledAccountNames(List<string> names)
        {
            var passthrough = new List<AccountNameCandidate>();
            var rolledByBase = new Dictionary<string, AccountNameCandidate>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                if (!TryGetRolledAccountParts(name, out var baseName, out var suffix))
                {
                    passthrough.Add(new AccountNameCandidate(name, i, long.MinValue + i));
                    continue;
                }

                if (!rolledByBase.TryGetValue(baseName, out var current) || suffix > current.Suffix)
                    rolledByBase[baseName] = new AccountNameCandidate(name, i, suffix);
            }

            return passthrough
                .Concat(rolledByBase.Values)
                .OrderBy(candidate => candidate.Index)
                .Select(candidate => candidate.Name)
                .ToList();
        }

        private static bool TryGetRolledAccountParts(string accountName, out string baseName, out long suffix)
        {
            baseName = null;
            suffix = 0;

            if (string.IsNullOrWhiteSpace(accountName))
                return false;

            var lastDash = accountName.LastIndexOf('-');
            if (lastDash <= 0 || lastDash >= accountName.Length - 1)
                return false;

            var prefix = accountName.Substring(0, lastDash + 1);
            var suffixText = accountName.Substring(lastDash + 1);

            if (suffixText.Length > 4 || !prefix.Any(char.IsDigit))
                return false;

            if (!long.TryParse(suffixText, out suffix))
                return false;

            baseName = prefix;
            return true;
        }

        private struct AccountNameCandidate
        {
            public readonly string Name;
            public readonly int Index;
            public readonly long Suffix;

            public AccountNameCandidate(string name, int index, long suffix)
            {
                Name = name;
                Index = index;
                Suffix = suffix;
            }
        }

        private static void AddSelectableAccount(Account account, List<string> liveNames, List<string> simNames)
        {
            if (!IsSelectableAccount(account))
                return;

            if (account.Provider == Provider.Simulator)
                simNames.Add(account.Name);
            else
                liveNames.Add(account.Name);
        }

        private static bool IsSelectableAccount(Account account)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.Name))
                return false;

            if (string.Equals(account.Name, "Backtest", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(account.Name, "Playback101", StringComparison.OrdinalIgnoreCase))
                return false;

            if (account.AccountStatus != AccountStatus.Enabled)
                return false;

            if (account.Provider == Provider.Simulator)
                return true;

            if (account.ConnectionStatus == ConnectionStatus.Connected)
                return true;

            return account.Connection != null && account.Connection.Status == ConnectionStatus.Connected;
        }

        /// <summary>
        /// Gets the instrument object from NT8's instrument manager.
        /// </summary>
        public Instrument FindInstrument(string instrumentName)
        {
            if (string.IsNullOrWhiteSpace(instrumentName)) return null;

            try
            {
                var instrument = Instrument.GetInstrument(instrumentName);
                if (instrument == null)
                    SdLogger.Warn("Instrument not found: {0}", instrumentName);
                return instrument;
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error resolving instrument: " + instrumentName);
                return null;
            }
        }

        /// <summary>
        /// Safe instrument comparison using FullName string match.
        /// Avoids reference equality issues between different Instrument instances.
        /// </summary>
        private static bool SameInstrument(Instrument a, Instrument b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            return string.Equals(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the current position for an account+instrument pair.
        /// Returns null if no position or flat.
        /// </summary>
        public Position FindPosition(Account account, Instrument instrument)
        {
            if (account == null || instrument == null) return null;

            try
            {
                lock (account.Positions)
                {
                    foreach (Position pos in account.Positions)
                    {
                        if (SameInstrument(pos.Instrument, instrument) && pos.MarketPosition != MarketPosition.Flat)
                            return pos;
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error finding position");
            }

            return null;
        }

        /// <summary>
        /// Finds active stop orders for a given account+instrument.
        /// Includes both StopMarket and StopLimit order types.
        /// </summary>
        public List<Order> FindStopOrders(Account account, Instrument instrument)
        {
            var orders = new List<Order>();
            if (account == null || instrument == null) return orders;

            try
            {
                lock (account.Orders)
                {
                    foreach (Order order in account.Orders)
                    {
                        if (SameInstrument(order.Instrument, instrument) &&
                            (order.OrderType == OrderType.StopMarket || order.OrderType == OrderType.StopLimit) &&
                            IsActiveOrder(order))
                        {
                            orders.Add(order);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error finding stop orders");
            }

            return orders;
        }

        /// <summary>
        /// Finds active limit (target) orders for a given account+instrument.
        /// </summary>
        public List<Order> FindTargetOrders(Account account, Instrument instrument)
        {
            return FindOrdersByType(account, instrument, OrderType.Limit);
        }

        /// <summary>
        /// Gets all active orders for an account+instrument pair.
        /// </summary>
        public List<Order> FindActiveOrders(Account account, Instrument instrument)
        {
            var orders = new List<Order>();
            if (account == null || instrument == null) return orders;

            try
            {
                lock (account.Orders)
                {
                    int totalOrders = 0;
                    int matchInstrument = 0;
                    int matchActive = 0;

                    foreach (Order order in account.Orders)
                    {
                        totalOrders++;
                        if (SameInstrument(order.Instrument, instrument))
                        {
                            matchInstrument++;
                            if (IsActiveOrder(order))
                            {
                                matchActive++;
                                orders.Add(order);
                            }
                            else
                            {
                                SdLogger.Debug("Order {0} ({1}) state={2} -- skipped",
                                    order.OrderId, order.OrderType, order.OrderState);
                            }
                        }
                    }

                    SdLogger.Debug("FindActiveOrders for {0}: total={1}, matchInstrument={2}, matchActive={3}",
                        instrument.FullName, totalOrders, matchInstrument, matchActive);
                }
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error finding active orders");
            }

            return orders;
        }

        private List<Order> FindOrdersByType(Account account, Instrument instrument, OrderType orderType)
        {
            var orders = new List<Order>();
            if (account == null || instrument == null) return orders;

            try
            {
                lock (account.Orders)
                {
                    foreach (Order order in account.Orders)
                    {
                        if (SameInstrument(order.Instrument, instrument) &&
                            order.OrderType == orderType &&
                            IsActiveOrder(order))
                        {
                            orders.Add(order);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error finding orders of type " + orderType);
            }

            return orders;
        }

        private static bool IsActiveOrder(Order order)
        {
            return order.OrderState == OrderState.Working ||
                   order.OrderState == OrderState.Accepted ||
                   order.OrderState == OrderState.ChangeSubmitted ||
                   order.OrderState == OrderState.Submitted ||
                   order.OrderState == OrderState.TriggerPending;
        }
    }
}
