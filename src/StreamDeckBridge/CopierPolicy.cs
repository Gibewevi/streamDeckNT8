using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StreamDeckBridge.Models;

namespace StreamDeckBridge;

/// <summary>
/// Owns and persists the account copier's configuration.
///
/// WHY THE BRIDGE HOLDS THIS. The add-on is reloaded by NinjaScript on every recompile — which is
/// what a trader does after each update — and the host restarts with the session. The bridge is
/// the only process that outlives both, so it is the only place a configuration can be kept and
/// replayed to whoever reconnects. Same reasoning, same shape as <see cref="SafetyMacro"/>.
///
/// It also arbitrates: the follower list is validated here, against the same live-account rule the
/// rest of the bridge applies. Nothing reaches the copy engine that has not passed through this.
/// </summary>
public sealed class CopierPolicy
{
    /// <summary>
    /// Mirror of <c>CopyEngine.MaxFollowers</c>. Each follower multiplies the exposure of one key
    /// press; the cap is a deliberate refusal, not a technical limit.
    /// </summary>
    public const int MaxFollowers = 8;

    /// <summary>
    /// A multiplier above this is far more likely to be a typo than an intention — and a typo here
    /// is an order a hundred times too large on someone else's account.
    /// </summary>
    private const double MaxMultiplier = 100;

    private const int MaxContractsCeiling = 1000;
    private const int MaxAccountNameLength = 64;

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<CopierPolicy> _logger;
    private readonly BridgeConfig _config;
    private readonly string _statePath;
    private readonly object _lock = new();

    private CopierPersistedState _state = new();

    /// <summary>
    /// Master the copier was last seen running against. Changing it holds copying — see
    /// <see cref="NoteMaster"/>.
    /// </summary>
    private string _activeMaster = string.Empty;

    public CopierPolicy(BridgeConfig config, ILogger<CopierPolicy> logger)
    {
        _config = config;
        _logger = logger;
        _statePath = ResolveStatePath(config);
        Load();
    }

    public sealed class FollowerSetting
    {
        public string Name { get; set; } = string.Empty;
        public double Multiplier { get; set; } = 1;
        public int MaxContracts { get; set; }
    }

    private sealed class CopierPersistedState
    {
        public bool Enabled { get; set; }
        public List<FollowerSetting> Followers { get; set; } = [];

        /// <summary>
        /// Non-empty while copying is held after a master change. Persisted on purpose: a bridge
        /// restart must not quietly resume copying onto follower positions that belong to an
        /// account nobody is watching any more.
        /// </summary>
        public string SuspendedReason { get; set; } = string.Empty;
    }

    /// <summary>
    /// The trader's setting AND nothing holding it back. Only this decides whether the copy engine
    /// runs; <see cref="CopierPersistedState.Enabled"/> alone is just what the layout asked for.
    /// </summary>
    public bool IsEffectivelyEnabled
    {
        get
        {
            lock (_lock) return _state.Enabled && _state.SuspendedReason.Length == 0;
        }
    }

    public string SuspendedReason
    {
        get { lock (_lock) return _state.SuspendedReason; }
    }

    public bool RequestedEnabled
    {
        get { lock (_lock) return _state.Enabled; }
    }

    public List<FollowerSetting> Followers
    {
        get
        {
            lock (_lock)
                return _state.Followers.Select(f => new FollowerSetting
                {
                    Name = f.Name,
                    Multiplier = f.Multiplier,
                    MaxContracts = f.MaxContracts
                }).ToList();
        }
    }

    /// <summary>
    /// Applies what the host read out of the Account key's settings.
    ///
    /// <paramref name="followersText"/> is one follower per line, <c>name|multiplier|cap</c>. It
    /// travels as a STRING rather than an array because Bitlearn's layout sanitiser accepts only
    /// strings, booleans and numbers inside a key's settings — an array is silently dropped on the
    /// way through the site, and the trader would watch their selection vanish without a message.
    /// </summary>
    public (bool Ok, string? Code, string? Reason) Configure(bool? enabled, string? followersText, string master)
    {
        lock (_lock)
        {
            if (followersText != null)
            {
                var (parsed, code, reason) = ParseFollowers(followersText, master);
                if (code != null) return (false, code, reason);
                _state.Followers = parsed;
            }

            if (enabled.HasValue)
            {
                // Turning the setting OFF is what releases a hold. Resuming after a master change
                // has to be a deliberate act, and this is the gesture that expresses it: off, then
                // on. A host replaying `enabled: true` on every reconnection must never do it by
                // accident, which is exactly what would happen if the hold cleared on `true`.
                if (!enabled.Value && _state.SuspendedReason.Length > 0)
                {
                    _logger.LogInformation("Copier hold released ({Reason}) — the setting was switched off", _state.SuspendedReason);
                    _state.SuspendedReason = string.Empty;
                }

                if (_state.Enabled != enabled.Value)
                {
                    _logger.LogWarning("Copier {State} — master={Master} followers={Count}",
                        enabled.Value ? "ENABLED" : "DISABLED",
                        string.IsNullOrEmpty(master) ? "-" : master,
                        _state.Followers.Count);
                }

                _state.Enabled = enabled.Value;
            }

            Persist();
            return (true, null, null);
        }
    }

    /// <summary>
    /// Notes the account the copier is running against, and holds copying when it changes.
    ///
    /// Called from the broadcast loop rather than from the <c>setAccount</c> handler on purpose:
    /// NinjaTrader can change the selected account on its own — it auto-selects one when the
    /// tracked account disappears — and that path never goes through a command. Watching the value
    /// the bridge is about to publish catches every route to a changed master.
    ///
    /// Returns true when this call is what held it.
    /// </summary>
    public bool NoteMaster(string master)
    {
        master ??= string.Empty;

        lock (_lock)
        {
            var previous = _activeMaster;
            _activeMaster = master;

            // First master of the session, or losing the account entirely: neither is a switch of
            // masters, and holding on them would mean the copier could never start.
            if (previous.Length == 0 || master.Length == 0) return false;
            if (string.Equals(previous, master, StringComparison.OrdinalIgnoreCase)) return false;
            if (!_state.Enabled || _state.SuspendedReason.Length > 0) return false;

            _state.SuspendedReason = "masterChanged";
            Persist();

            _logger.LogWarning(
                "COPIER HELD — master changed {Previous} → {Master}. Positions already copied belong to "
                + "{Previous} and are now unmanaged; copying resumes only when the setting is switched off and on.",
                previous, master, previous);

            return true;
        }
    }

    /// <summary>
    /// Parses and validates the follower list. Every refusal returns a code, so the host logs a
    /// reason instead of a silently shorter list.
    /// </summary>
    private (List<FollowerSetting> Followers, string? Code, string? Reason) ParseFollowers(string text, string master)
    {
        var followers = new List<FollowerSetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lines = text.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var parts = trimmed.Split('|');
            var name = parts[0].Trim();

            if (name.Length == 0) continue;
            if (name.Length > MaxAccountNameLength)
                return ([], "INVALID_PAYLOAD", $"Follower name '{name[..20]}…' is too long.");

            // A follower that is also the master would copy onto itself, doubling every order on
            // one account. Refused rather than skipped: silently dropping it would leave the
            // trader looking at a follower list that does not describe what runs.
            if (string.Equals(name, master, StringComparison.OrdinalIgnoreCase))
                return ([], "COPIER_MASTER_IS_FOLLOWER", $"'{name}' is the selected account — it cannot also be a follower.");

            if (!seen.Add(name)) continue;

            // Safe mode applies to EVERY follower, not just the account the deck trades on. A
            // copier is otherwise a way to reach a live account while safe mode is on.
            if (!_config.AllowLiveAccounts && !name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase))
                return ([], "LIVE_ACCOUNT_BLOCKED", $"Live account '{name}' is blocked. Safe mode only allows Sim accounts.");

            var multiplier = 1.0;
            if (parts.Length > 1 && double.TryParse(parts[1].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var m))
            {
                multiplier = m;
            }

            if (double.IsNaN(multiplier) || multiplier < 0 || multiplier > MaxMultiplier)
                return ([], "INVALID_PAYLOAD", $"Multiplier for '{name}' must be between 0 and {MaxMultiplier}.");

            var maxContracts = 0;
            if (parts.Length > 2 && int.TryParse(parts[2].Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var c))
            {
                maxContracts = c;
            }

            if (maxContracts < 0 || maxContracts > MaxContractsCeiling)
                return ([], "INVALID_PAYLOAD", $"Contract cap for '{name}' must be between 0 and {MaxContractsCeiling}.");

            followers.Add(new FollowerSetting { Name = name, Multiplier = multiplier, MaxContracts = maxContracts });

            if (followers.Count > MaxFollowers)
                return ([], "COPIER_TOO_MANY_FOLLOWERS", $"At most {MaxFollowers} follower accounts are allowed.");
        }

        return (followers, null, null);
    }

    /// <summary>Fills in the bridge-owned half of the broadcast block.</summary>
    public void StampSnapshot(CopierStatus status, string master, bool entriesBlocked)
    {
        lock (_lock)
        {
            status.Enabled = _state.Enabled && _state.SuspendedReason.Length == 0;
            status.Master = master;
            status.EntriesBlocked = entriesBlocked;
            status.SuspendedReason = _state.SuspendedReason;

            // The follower list is the bridge's, not the add-on's: it is what the trader
            // configured, and it must show even when NinjaTrader is disconnected and nothing can
            // report health for it. Health fields keep whatever the add-on last said.
            var byName = status.Followers.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var merged = new List<CopierFollowerStatus>();

            foreach (var setting in _state.Followers)
            {
                if (!byName.TryGetValue(setting.Name, out var entry))
                    entry = new CopierFollowerStatus { Name = setting.Name };

                entry.Multiplier = setting.Multiplier;
                entry.MaxContracts = setting.MaxContracts;
                merged.Add(entry);
            }

            status.Followers = merged;
        }
    }

    private static string ResolveStatePath(BridgeConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CopierStatePath))
            return config.CopierStatePath;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader");

        return Path.Combine(dir, "copier.json");
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var loaded = JsonSerializer.Deserialize<CopierPersistedState>(File.ReadAllText(_statePath), FileJson);
                if (loaded != null)
                {
                    loaded.Followers ??= [];
                    loaded.SuspendedReason ??= string.Empty;
                    _state = loaded;

                    _logger.LogInformation("Copier configuration loaded — enabled={Enabled} followers={Count} held={Held}",
                        _state.Enabled, _state.Followers.Count,
                        _state.SuspendedReason.Length == 0 ? "-" : _state.SuspendedReason);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt file must not prevent the bridge from starting. Falling back to defaults
            // means copying OFF, which is the safe side of this particular failure.
            _logger.LogError(ex, "Could not read copier state from {Path} — copying starts disabled", _statePath);
        }

        _state = new CopierPersistedState();
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, FileJson));
        }
        catch (Exception ex)
        {
            // In-memory state stays authoritative for this process, so a failed write degrades
            // durability but never enables copying by itself.
            _logger.LogError(ex, "Could not persist copier state to {Path}", _statePath);
        }
    }
}
