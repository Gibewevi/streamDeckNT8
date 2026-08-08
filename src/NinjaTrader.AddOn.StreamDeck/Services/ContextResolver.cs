using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Root symbol -> resolved contract, so the 500ms state publisher does not rescan the
        // instrument database on every tick. The entry is short-lived on purpose: when the
        // contract rolls, the resolution must follow within the hour without restarting
        // NinjaTrader — a permanently cached contract would keep routing to the expired one.
        private static readonly TimeSpan InstrumentCacheTtl = TimeSpan.FromHours(1);

        private sealed class CachedInstrument
        {
            public Instrument Instrument;
            public DateTime ResolvedAt;
        }

        private readonly Dictionary<string, CachedInstrument> _instrumentCache =
            new Dictionary<string, CachedInstrument>(StringComparer.OrdinalIgnoreCase);
        private readonly object _instrumentCacheLock = new object();

        /// <summary>
        /// Gets the instrument object from NT8's instrument manager.
        ///
        /// Accepts both a full contract name ("MNQ 09-26") and a root symbol ("MNQ"): the
        /// Stream Deck instrument keys are configured with roots, and NinjaTrader's
        /// GetInstrument only matches full names, so a root must be resolved to the front
        /// month before any order can be placed.
        /// </summary>
        public Instrument FindInstrument(string instrumentName)
        {
            if (string.IsNullOrWhiteSpace(instrumentName)) return null;

            var name = instrumentName.Trim();

            lock (_instrumentCacheLock)
            {
                CachedInstrument cached;
                if (_instrumentCache.TryGetValue(name, out cached) &&
                    DateTime.Now - cached.ResolvedAt < InstrumentCacheTtl &&
                    !IsExpiredContract(cached.Instrument))
                {
                    return cached.Instrument;
                }
            }

            var resolved = ResolveInstrument(name);

            if (resolved != null)
            {
                lock (_instrumentCacheLock)
                {
                    _instrumentCache[name] = new CachedInstrument { Instrument = resolved, ResolvedAt = DateTime.Now };
                }

                if (!string.Equals(resolved.FullName, name, StringComparison.OrdinalIgnoreCase))
                    SdLogger.Info("Instrument '{0}' resolved to active contract '{1}'", name, resolved.FullName);
            }
            else
            {
                SdLogger.Warn("Instrument not found: {0}", name);
            }

            return resolved;
        }

        private static Instrument ResolveInstrument(string name)
        {
            // A name that already carries a contract month is taken as-is.
            if (HasExpirySuffix(name))
            {
                var exactDated = TryGetInstrument(name);
                if (exactDated != null) return exactDated;
            }
            else
            {
                // Root symbol. A dated contract must win over the continuous instrument
                // whatever NinjaTrader reports as InstrumentType — relying on that type is
                // what let "MNQ " (continuous, refused by the broker) through twice.
                var dated = FindDatedContract(name);
                if (dated != null) return dated;

                SdLogger.Warn("No dated contract found for root '{0}' — falling back to a direct lookup", name);
            }

            // Fallback for anything without an expiry by nature (stocks, forex, CFDs...)
            var fallback = TryGetInstrument(name);
            if (fallback == null)
            {
                try { fallback = Instrument.GetInstrumentFuzzy(name); }
                catch (Exception ex) { SdLogger.Debug("GetInstrumentFuzzy('{0}') failed: {1}", name, ex.Message); }
            }

            if (fallback == null) return null;

            // NinjaTrader names a continuous/master future "MNQ " — the symbol followed by an
            // empty expiry, hence the trailing space seen in the broker's rejection:
            //   instrument 'MNQ ' is not supported by this account
            // CreateOrder accepts it, so it must be stopped here rather than at the broker.
            if (!IsRoutableName(fallback))
            {
                SdLogger.Error(null, string.Format(
                    "Instrument '{0}' resolved to the continuous instrument '{1}' which no broker accepts. " +
                    "Configure the Stream Deck instrument key with the full contract name (e.g. \"{0} {2}\").",
                    name, fallback.FullName, DateTime.Now.ToString("MM-yy", CultureInfo.InvariantCulture)));
                return null;
            }

            SdLogger.Info("Instrument '{0}' resolved to '{1}'", name, fallback.FullName);
            return fallback;
        }

        /// <summary>
        /// Finds the nearest non-expired dated contract for a root symbol, first from the
        /// instruments NinjaTrader already knows, then by probing the upcoming months.
        /// </summary>
        private static Instrument FindDatedContract(string root)
        {
            // Ask NinjaTrader which contract is active today. It owns the roll calendar, so
            // this is what keeps the deck on the front month across a roll without any
            // configuration change.
            var current = AskNinjaTraderForCurrentContract(root);
            if (current != null) return current;

            Instrument front = null;
            int examined = 0;

            try
            {
                lock (Instrument.All)
                {
                    foreach (Instrument candidate in Instrument.All)
                    {
                        if (candidate == null || candidate.MasterInstrument == null) continue;
                        if (!string.Equals(candidate.MasterInstrument.Name, root, StringComparison.OrdinalIgnoreCase))
                            continue;

                        examined++;
                        if (!HasExpirySuffix(candidate.FullName) || IsExpiredContract(candidate)) continue;

                        if (front == null || candidate.Expiry < front.Expiry)
                            front = candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error scanning instruments for root '" + root + "'");
            }

            if (front != null)
            {
                SdLogger.Debug("Root '{0}': {1} known instrument(s), front month = {2}", root, examined, front.FullName);
                return front;
            }

            // Instrument.All only holds what NinjaTrader has already loaded — probe the
            // upcoming contract months by name.
            for (int i = 0; i < 14; i++)
            {
                var probeName = root + " " + DateTime.Now.AddMonths(i).ToString("MM-yy", CultureInfo.InvariantCulture);
                var candidate = TryGetInstrument(probeName);

                if (candidate != null && HasExpirySuffix(candidate.FullName) && !IsExpiredContract(candidate))
                {
                    SdLogger.Debug("Root '{0}' resolved by probing: {1}", root, candidate.FullName);
                    return candidate;
                }
            }

            SdLogger.Warn("Root '{0}': no dated contract found ({1} known instrument(s) examined, 14 months probed)",
                root, examined);
            return null;
        }

        /// <summary>
        /// Uses NinjaTrader's own contract calendar to get the contract active today for a
        /// root symbol, so the deck follows the roll on its own. Returns null on any doubt —
        /// every caller has a fallback.
        /// </summary>
        private static Instrument AskNinjaTraderForCurrentContract(string root)
        {
            // The continuous instrument is not routable, but it carries the MasterInstrument
            // that owns the expiry calendar.
            var seed = TryGetInstrument(root);
            if (seed == null || seed.MasterInstrument == null) return null;

            // 1. "Which contract is active on this date?"
            try
            {
                var byDate = MasterInstrument.GetInstrumentByDate(seed, DateTime.Now, false, true, null);
                if (byDate != null && IsRoutableName(byDate) && HasExpirySuffix(byDate.FullName) && !IsExpiredContract(byDate))
                {
                    SdLogger.Debug("Root '{0}': NinjaTrader reports '{1}' as the current contract", root, byDate.FullName);
                    return byDate;
                }
            }
            catch (Exception ex)
            {
                SdLogger.Debug("GetInstrumentByDate('{0}') failed: {1}", root, ex.Message);
            }

            // 2. "When is the next expiry?" -> build the contract name from it
            try
            {
                var nextExpiry = seed.MasterInstrument.GetNextExpiry(DateTime.Now);
                if (nextExpiry > DateTime.MinValue && nextExpiry.Year > 1900)
                {
                    var byExpiry = TryGetInstrument(
                        root + " " + nextExpiry.ToString("MM-yy", CultureInfo.InvariantCulture));

                    if (byExpiry != null && IsRoutableName(byExpiry) && HasExpirySuffix(byExpiry.FullName))
                    {
                        SdLogger.Debug("Root '{0}': next expiry {1:yyyy-MM-dd} -> '{2}'", root, nextExpiry, byExpiry.FullName);
                        return byExpiry;
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Debug("GetNextExpiry('{0}') failed: {1}", root, ex.Message);
            }

            return null;
        }

        private static Instrument TryGetInstrument(string name)
        {
            try
            {
                return Instrument.GetInstrument(name);
            }
            catch (Exception ex)
            {
                SdLogger.Debug("GetInstrument('{0}') failed: {1}", name, ex.Message);
                return null;
            }
        }

        /// <summary>Contracts stay tradable until the end of their expiry month.</summary>
        private static bool IsExpiredContract(Instrument instrument)
        {
            try
            {
                var expiry = instrument.Expiry;
                if (expiry == DateTime.MinValue || expiry == DateTime.MaxValue || expiry.Year < 1900)
                    return false; // no usable expiry: the dated name is what matters

                var endOfExpiryMonth = new DateTime(expiry.Year, expiry.Month,
                    DateTime.DaysInMonth(expiry.Year, expiry.Month));

                return endOfExpiryMonth.Date < DateTime.Now.Date;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// False for NinjaTrader's continuous/master instruments, whose FullName is the symbol
        /// followed by an empty expiry ("MNQ "). Those cannot be routed to a broker.
        /// </summary>
        private static bool IsRoutableName(Instrument instrument)
        {
            if (instrument == null) return false;

            var fullName = instrument.FullName;
            if (string.IsNullOrWhiteSpace(fullName)) return false;

            return fullName == fullName.TrimEnd();
        }

        /// <summary>
        /// True when the instrument name carries a contract month, e.g. "MNQ 09-26".
        /// </summary>
        private static bool HasExpirySuffix(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return false;

            var trimmed = fullName.Trim();
            var lastSpace = trimmed.LastIndexOf(' ');
            if (lastSpace <= 0 || lastSpace == trimmed.Length - 1) return false;

            var suffix = trimmed.Substring(lastSpace + 1); // "09-26"
            if (suffix.Length != 5 || suffix[2] != '-') return false;

            int month, year;
            if (!int.TryParse(suffix.Substring(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month))
                return false;
            if (!int.TryParse(suffix.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out year))
                return false;

            return month >= 1 && month <= 12;
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
        /// Sorted by stop price ascending, so callers get a deterministic order instead of
        /// whatever sequence NinjaTrader happens to return.
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

                orders.Sort((a, b) => a.StopPrice.CompareTo(b.StopPrice));
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error finding stop orders");
            }

            return orders;
        }

        /// <summary>
        /// Returns the stop that protects the position most tightly — the highest stop for a
        /// long (closest below the market), the lowest for a short. Used for display so the
        /// deck shows the stop that actually matters.
        /// </summary>
        public Order FindTightestStopOrder(Account account, Instrument instrument, MarketPosition marketPosition)
        {
            var stops = FindStopOrders(account, instrument);
            if (stops.Count == 0) return null;

            // FindStopOrders sorts by stop price ascending
            return marketPosition == MarketPosition.Long ? stops[stops.Count - 1] : stops[0];
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
                                // Runs on every state publish (several times a second): Trace,
                                // otherwise this single line owns the log file.
                                SdLogger.Trace("Order {0} ({1}) state={2} -- skipped",
                                    order.OrderId, order.OrderType, order.OrderState);
                            }
                        }
                    }

                    SdLogger.Trace("FindActiveOrders for {0}: total={1}, matchInstrument={2}, matchActive={3}",
                        instrument.FullName, totalOrders, matchInstrument, matchActive);
                }
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error finding active orders");
            }

            return orders;
        }

        /// <summary>
        /// Every active order on the account, whatever the instrument.
        ///
        /// Exists for the account-wide liquidation: a resting entry on an instrument the trader is
        /// not currently watching would otherwise survive a "the day is over" flatten and fill
        /// afterwards — reopening the exposure the rule had just closed.
        /// </summary>
        public List<Order> FindAllActiveOrders(Account account)
        {
            var orders = new List<Order>();
            if (account == null) return orders;

            try
            {
                lock (account.Orders)
                {
                    foreach (Order order in account.Orders)
                    {
                        if (IsActiveOrder(order)) orders.Add(order);
                    }
                }
            }
            catch (Exception ex)
            {
                SdLogger.Error(ex, "Error finding active orders account-wide");
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

                // Deterministic order for callers that index into the list
                orders.Sort((a, b) => a.LimitPrice.CompareTo(b.LimitPrice));
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
