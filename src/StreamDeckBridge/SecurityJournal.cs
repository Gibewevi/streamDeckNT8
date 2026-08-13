using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StreamDeckBridge.Models;

namespace StreamDeckBridge;

/// <summary>
/// Writes behavioural events straight from the bridge into the journal spool the host uploads.
///
/// WHY THE BRIDGE AND NOT THE HOST. The host already records this class of event — guard
/// violations, tilt episodes, locks — and it does it well. But it records nothing while it is not
/// running, and the session of 2026-08-12 showed exactly what that costs: the deck was unplugged
/// at 11:57, the host process ended a minute later, and the add-on went on detecting nineteen
/// manual orders placed past a breached daily-loss limit. Not one of them reached the journal. The
/// only witness left was a log file, which is precisely the outcome the journal exists to prevent.
///
/// The bridge is the process that stays up: it outlives the deck, the host and NinjaTrader alike.
/// Anything that must be recorded EVEN WHEN THE TRADER IS WORKING AROUND HIS OWN PROTECTION
/// therefore belongs here, not upstream.
///
/// HOW IT COEXISTS WITH THE HOST'S SPOOL. Its own file, never the host's: two processes appending
/// to one file would eventually interleave a torn line, and a torn line is a lost event. The
/// uploader globs `events-*.ndjson` and keeps one cursor per file, so a second file is picked up
/// with no change on that side. Event ids are random, so a duplicate cannot be created by writing
/// from two places — but the same event must still be written by ONE of them, which is why
/// `guard.violation` moved here wholesale rather than being mirrored.
///
/// Nothing here may disturb trading: every failure is swallowed after one warning, and no call
/// blocks on anything but a few dozen bytes of synchronous append.
/// </summary>
public sealed class SecurityJournal
{
    private readonly ILogger<SecurityJournal> _logger;
    private readonly string _directory;
    private readonly object _lock = new();
    private bool _failureReported;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SecurityJournal(BridgeConfig config, ILogger<SecurityJournal> logger)
    {
        _logger = logger;
        _directory = ResolveDirectory(config);
    }

    /// <summary>
    /// Appends one event. `type` follows the host's vocabulary (`guard.violation`, `deck.lost`…)
    /// so both spools land in the same Bitlearn timeline.
    /// </summary>
    public void Record(string type, string account, string instrument, object? data = null)
    {
        try
        {
            var line = BuildLine(type, account, instrument, data);

            lock (_lock)
            {
                Directory.CreateDirectory(_directory);
                var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
                // `events-bridge-` and not `events-`: same glob on the uploader side, distinct
                // file, distinct cursor.
                File.AppendAllText(Path.Combine(_directory, $"events-bridge-{day}.ndjson"),
                    line + "\n", Encoding.UTF8);
            }

            _failureReported = false;
        }
        catch (Exception ex)
        {
            // Once, not on every event: a full disk would otherwise bury the log under identical
            // lines at the exact moment the log is the last thing left.
            if (_failureReported) return;
            _failureReported = true;
            _logger.LogError(ex, "Security journal unavailable — behavioural events are not being recorded");
        }
    }

    private static string BuildLine(string type, string account, string instrument, object? data)
    {
        // The host's schema, field for field: the server reads one stream and cannot be asked to
        // know which process wrote a given line.
        var evenement = new Dictionary<string, object?>
        {
            ["kind"] = "event",
            // Without a unique id, a batch re-sent after an outage — the normal case, not the
            // exception — would double every count on the Bitlearn side.
            ["eid"] = NewEventId(),
            ["type"] = type,
            ["atUtc"] = DateTime.UtcNow.ToString("o"),
            ["account"] = account ?? string.Empty,
            ["instrument"] = instrument ?? string.Empty,
            // Says which process saw it. The host cannot report what happens while it is down, so
            // a reader needs to know that these lines have a different vantage point.
            ["source"] = "bridge"
        };

        if (data != null)
        {
            foreach (var champ in JsonSerializer.SerializeToElement(data, Json).EnumerateObject())
            {
                evenement[champ.Name] = champ.Value.Clone();
            }
        }

        return JsonSerializer.Serialize(evenement, Json);
    }

    /// <summary>Same shape as the host's: nine random bytes, base64url.</summary>
    private static string NewEventId()
    {
        var octets = RandomNumberGenerator.GetBytes(9);
        return Convert.ToBase64String(octets).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string ResolveDirectory(BridgeConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.JournalDirectory))
            return Environment.ExpandEnvironmentVariables(config.JournalDirectory);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamDeckTrader", "journal");
    }
}
