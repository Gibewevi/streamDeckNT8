namespace StreamDeckBridge.Models;

/// <summary>
/// What the account copier is doing, as the bridge broadcasts it.
///
/// A merge of two sources, exactly like <see cref="TrendState"/>. The CONFIGURATION half —
/// enabled, master, the follower list, entriesBlocked — belongs to the bridge, which persists it
/// and stamps it onto every snapshot so a NinjaTrader publish cannot overwrite it. The HEALTH half
/// — resolved, drifted, lastError, copiedToday — can only come from the add-on, which is the sole
/// component that can see an account or a position.
/// </summary>
public sealed class CopierStatus
{
    /// <summary>Bridge-owned: the trader's setting, from the Account key's layout entry.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Bridge-owned: the SELECTED account. There is no separate master setting — the copier
    /// mirrors whatever the Account key has selected, which is also what the deck trades on.
    /// </summary>
    public string Master { get; set; } = string.Empty;

    /// <summary>Add-on: the master name resolved to a live NinjaTrader account.</summary>
    public bool MasterResolved { get; set; }

    /// <summary>
    /// Bridge-owned: the safety macro is refusing entries, so copies of entries stop with it.
    /// Exits keep being copied — trapping a follower inside a position is the one outcome no rule
    /// in this system may produce.
    /// </summary>
    public bool EntriesBlocked { get; set; }

    public List<CopierFollowerStatus> Followers { get; set; } = [];

    /// <summary>Add-on: master orders copied today. Display and diagnosis only.</summary>
    public int CopiedToday { get; set; }
}

public sealed class CopierFollowerStatus
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Bridge-owned. 0 disables this follower without removing it from the list.</summary>
    public double Multiplier { get; set; } = 1;

    /// <summary>Bridge-owned cap PER COPIED ORDER. 0 = no cap of its own.</summary>
    public int MaxContracts { get; set; }

    /// <summary>Add-on: the account exists and its connection is live right now.</summary>
    public bool Resolved { get; set; }

    /// <summary>
    /// Add-on: the follower's position no longer matches what the master's implies, and the gap
    /// has settled. Entry copies to this account are stopped; exits keep flowing. Nothing is ever
    /// sent to correct it.
    /// </summary>
    public bool Drifted { get; set; }

    /// <summary>Add-on: signed gap in contracts, <c>actual − expected</c>.</summary>
    public int Drift { get; set; }

    /// <summary>Add-on: last refusal seen on this follower, empty when none.</summary>
    public string LastError { get; set; } = string.Empty;
}
