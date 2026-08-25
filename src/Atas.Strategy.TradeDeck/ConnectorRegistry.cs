using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ATAS.DataFeedsCore;

namespace TradeDeck.Atas.Strategy;

/// <summary>
/// Reaches the platform's own connector registry — the thing that makes multi-account selection
/// possible on classic ATAS, since <see cref="ATAS.Strategies.Chart.ChartStrategy.Connector"/> is
/// confirmed permanently null (measured across 472 published frames, confirmed in writing by ATAS
/// support: "the connector must be assigned manually if you need it").
///
/// ENTIRELY BY REFLECTION, NO COMPILE-TIME REFERENCE to OFT.Platform.Core, OFT.Platform or
/// CommonServiceLocator. These are host internals, not ATAS's documented plugin surface: a hard
/// reference means the strategy stops loading AT ALL the day one of them is renamed — a dead cockpit
/// over a feature the trader never asked to lose. By reflection, a rename instead costs one field
/// (IsAvailable flips to false) and this class degrades quietly; ContextResolver then falls back to
/// TradingManager, chart-scoped, and says so once in the log — see ContextResolver.
///
/// Verified live 2026-08-24 (see progress.md, "SONDE SERVICELOCATOR"): IsLocationProviderSet=True,
/// ITradingCore and IConnectorsManager both resolve, GetConnector(DEMO29ADC) returned a live
/// SbeConnector (Portfolios=1 Securities=5 Orders=2 Positions=2 MyTrades=1), and
/// IConnectorsManager.Connectors listed 12 connectors, 2 of them carrying a portfolio (10 configured
/// but never connected).
/// </summary>
internal sealed class ConnectorRegistry
{
    // STATIC AND RESOLVED ONCE. This class answers GetConnectors() on the 500ms state-publish tick —
    // hundreds of thousands of calls a day. CommonServiceLocator.ServiceLocator.Current,
    // ITradingCore and IConnectorsManager never change identity while ATAS runs, so the reflection
    // LOOKUPS (assembly scan, GetProperty, GetMethod) are paid once for the process, never per tick.
    // What IS re-read on every call is the DATA — Connectors, Portfolios — because those genuinely
    // change: a broker connects, another disconnects, mid-session.
    private static readonly object InitLock = new();
    private static bool _initSucceeded;
    // SPACED RETRY, NOT A ONE-SHOT LATCH. A bare "did we already try" flag left a strategy that
    // started before OFT.Platform.Core or CommonServiceLocator had loaded stuck in degraded mode
    // for the rest of the ATAS process: EnsureInitialized re-entered and returned immediately,
    // recovering only if the trader relaunched ATAS. This timestamp gates a retry to at most once
    // a minute instead — a late-attaching host assembly gets picked up, and the cost stays a lock
    // acquire plus one DateTime comparison on every call that is not due a retry, never a full
    // reflection pass on the 500ms hot path.
    private static DateTime _lastInitAttemptUtc = DateTime.MinValue;
    private static object? _tradingCore;          // ITradingCore instance, optional fast path
    private static object? _connectorsManager;     // IConnectorsManager instance — load-bearing
    private static PropertyInfo? _connectorsProperty;
    private static MethodInfo? _getConnectorByPortfolio;
    private static string? _initFailureReason;

    /// <summary>Transition-tracked, not read every tick: see ReportAvailability.</summary>
    private bool? _lastAvailable;

    public ConnectorRegistry() => EnsureInitialized();

    public bool IsAvailable
    {
        get
        {
            // Retries (spaced, see _lastInitAttemptUtc) live behind every read of availability, not
            // just the constructor — the registry instance is typically constructed once for the
            // whole strategy lifetime, so a call made here is what actually gives a late-attaching
            // host assembly a chance to be found later in the session.
            EnsureInitialized();
            return _connectorsManager != null && _connectorsProperty != null;
        }
    }

    /// <summary>
    /// Every connector currently carrying at least one portfolio.
    ///
    /// FILTERED ON PURPOSE: of the 12 connectors measured live, 10 were configured but never
    /// connected and carried zero portfolios. Presenting them as selectable accounts would let the
    /// deck resolve a name against a connector that can never fill an order.
    ///
    /// Re-enumerated on every call — see the static fields' comment above for why the cost of doing
    /// so is acceptable and the cost of NOT doing so would be a stale account list.
    /// </summary>
    public IReadOnlyList<IDataFeedConnector> GetConnectors()
    {
        if (!IsAvailable)
        {
            ReportAvailability(false, _initFailureReason ?? "registry not initialized");
            return Array.Empty<IDataFeedConnector>();
        }

        try
        {
            var raw = _connectorsProperty!.GetValue(_connectorsManager) as IEnumerable;
            var result = new List<IDataFeedConnector>();
            if (raw != null)
            {
                foreach (var entry in raw)
                {
                    if (entry is IDataFeedConnector connector && SafePortfolioCount(connector) > 0)
                        result.Add(connector);
                }
            }

            ReportAvailability(true, null);
            return result;
        }
        catch (Exception ex)
        {
            ReportAvailability(false, ex.GetType().Name + ": " + ex.Message);
            return Array.Empty<IDataFeedConnector>();
        }
    }

    /// <summary>
    /// The connector that owns this portfolio, or null. Point 4 of the field trial: an Order, a
    /// Position and a MyTrade each belong to a connector — watching, cancelling or modifying them
    /// must act on the one that owns the pair, never on a global connector (there is none).
    ///
    /// Tries the host's own lookup first (measured live: GetConnector(DEMO29ADC) -> SbeConnector,
    /// exact match), falling back to a linear scan of GetConnectors() so this method stays correct
    /// even when ITradingCore itself could not be resolved but IConnectorsManager could.
    /// </summary>
    public IDataFeedConnector? ConnectorFor(Portfolio? portfolio)
    {
        if (portfolio == null) return null;

        // Same spaced-retry entry point as IsAvailable — this method reads _tradingCore directly,
        // ahead of any call to GetConnectors() in this tick, so it needs its own chance to notice a
        // late-attaching host.
        EnsureInitialized();

        if (_tradingCore != null && _getConnectorByPortfolio != null)
        {
            try
            {
                if (_getConnectorByPortfolio.Invoke(_tradingCore, new object?[] { portfolio }) is IDataFeedConnector fast)
                    return fast;
            }
            catch { /* fall through to the scan below — this path is a shortcut, not load-bearing */ }
        }

        foreach (var connector in GetConnectors())
        {
            if (SafeContainsPortfolio(connector, portfolio)) return connector;
        }
        return null;
    }

    /// <summary>
    /// Logged ONLY on a transition, never per tick — this is read from the 500ms publish loop and
    /// CLAUDE.md is explicit that one line there is hundreds of thousands a day. A registry that
    /// never becomes available must still be visible once, at a level a normal run shows; that is
    /// exactly the degraded-mode entry ContextResolver relies on this class to report honestly.
    /// </summary>
    private void ReportAvailability(bool available, string? reason)
    {
        if (_lastAvailable == available) return;
        _lastAvailable = available;

        if (available)
        {
            SdLog.Event("Registry", "Connector registry reachable — resolving deck accounts against IConnectorsManager.Connectors");
        }
        else
        {
            SdLog.EventWarn("Registry",
                "Connector registry unreachable ({0}) — degrading to the chart's own account and instrument via TradingManager",
                reason ?? "?");
        }
    }

    private static int SafePortfolioCount(IDataFeedConnector connector)
    {
        try { return connector.Portfolios?.Count() ?? 0; }
        catch { return 0; }
    }

    private static bool SafeContainsPortfolio(IDataFeedConnector connector, Portfolio portfolio)
    {
        try
        {
            var portfolios = connector.Portfolios;
            if (portfolios == null) return false;
            foreach (var p in portfolios)
            {
                if (ReferenceEquals(p, portfolio)) return true;
                if (!string.IsNullOrEmpty(p?.AccountID) &&
                    string.Equals(p.AccountID, portfolio.AccountID, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Resolves CommonServiceLocator -> ITradingCore / IConnectorsManager, by assembly simple name
    /// and full type name. EVERY STEP OWNS ITS OWN FAILURE PATH: the worst outcome of any of them
    /// failing is IsAvailable staying false, never an exception escaping to the caller — this runs
    /// from a strategy's OnStarted, and a constructor that throws would refuse to load the whole
    /// cockpit over a diagnostic nobody asked for.
    ///
    /// RETRIED ON A SPACED SCHEDULE UNTIL IT SUCCEEDS, THEN CACHED FOR GOOD — see
    /// _lastInitAttemptUtc. Once _initSucceeded is true the handles never change identity while
    /// ATAS runs (see the static fields' comment at the top of the class) and this returns
    /// immediately; until then, every call from IsAvailable/GetConnectors/ConnectorFor is a
    /// candidate to try again, throttled to once a minute.
    /// </summary>
    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initSucceeded) return;
            if ((DateTime.UtcNow - _lastInitAttemptUtc).TotalMinutes < 1) return;
            _lastInitAttemptUtc = DateTime.UtcNow;

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                var locatorType = FindType(assemblies, "CommonServiceLocator", "CommonServiceLocator.ServiceLocator");
                if (locatorType == null) { _initFailureReason = "CommonServiceLocator.ServiceLocator not loaded"; return; }

                var isSet = locatorType.GetProperty("IsLocationProviderSet", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as bool?;
                if (isSet != true) { _initFailureReason = "IsLocationProviderSet=false"; return; }

                var current = locatorType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (current == null) { _initFailureReason = "ServiceLocator.Current returned null"; return; }

                var getInstance = current.GetType().GetMethod("GetInstance", new[] { typeof(Type) });
                if (getInstance == null) { _initFailureReason = "IServiceLocator.GetInstance(Type) not found"; return; }

                // ITradingCore is a nice-to-have shortcut (GetConnector(Portfolio)), not
                // load-bearing: GetConnectors() below works from IConnectorsManager alone. A failure
                // here does not abort initialisation.
                var tradingCoreType = FindType(assemblies, "OFT.Platform.Core", "OFT.Platform.Core.Providers.ITradingCore");
                if (tradingCoreType != null)
                {
                    try
                    {
                        _tradingCore = getInstance.Invoke(current, new object?[] { tradingCoreType });
                        _getConnectorByPortfolio = _tradingCore?.GetType().GetMethod("GetConnector", new[] { typeof(Portfolio) });
                    }
                    catch { _tradingCore = null; _getConnectorByPortfolio = null; }
                }
                // CoreProvider.TradingCore is confirmed NULL live (progress.md, step 4 of the probe) —
                // deliberately not attempted as a second path to ITradingCore.

                var managerType = FindType(assemblies, "OFT.Platform.Core", "OFT.Platform.Core.Managers.Connections.IConnectorsManager");
                if (managerType == null) { _initFailureReason = "IConnectorsManager type not found"; return; }

                object? manager;
                try { manager = getInstance.Invoke(current, new object?[] { managerType }); }
                catch (Exception ex)
                {
                    var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                    _initFailureReason = "GetInstance(IConnectorsManager) threw " + real.GetType().Name;
                    return;
                }
                if (manager == null) { _initFailureReason = "GetInstance(IConnectorsManager) returned null"; return; }

                var connectorsProperty = manager.GetType().GetProperty("Connectors", BindingFlags.Public | BindingFlags.Instance);
                if (connectorsProperty == null) { _initFailureReason = "IConnectorsManager.Connectors property not found"; return; }

                _connectorsManager = manager;
                _connectorsProperty = connectorsProperty;
                _initSucceeded = true;
            }
            catch (Exception ex)
            {
                _initFailureReason = ex.GetType().Name + ": " + ex.Message;
            }
        }
    }

    private static Type? FindType(Assembly[] assemblies, string assemblySimpleName, string fullTypeName)
    {
        foreach (var assembly in assemblies)
        {
            string? name;
            try { name = assembly.GetName().Name; } catch { continue; }
            if (!string.Equals(name, assemblySimpleName, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var type = assembly.GetType(fullTypeName, throwOnError: false);
                if (type != null) return type;
            }
            catch { /* keep scanning the other assemblies matching this simple name, if any */ }
        }
        return null;
    }
}
