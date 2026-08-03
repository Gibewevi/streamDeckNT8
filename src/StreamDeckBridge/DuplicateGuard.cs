using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace StreamDeckBridge;

/// <summary>
/// Tracks recent request IDs to prevent duplicate execution within a time window.
/// </summary>
public sealed class DuplicateGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentRequests = new();
    private readonly TimeSpan _window;
    private readonly ILogger<DuplicateGuard> _logger;

    public DuplicateGuard(int windowSeconds, ILogger<DuplicateGuard> logger)
    {
        _window = TimeSpan.FromSeconds(windowSeconds);
        _logger = logger;
    }

    public bool IsDuplicate(string requestId)
    {
        var now = DateTimeOffset.UtcNow;

        // Periodic cleanup of expired entries
        if (_recentRequests.Count > 0)
        {
            var expiredKeys = _recentRequests
                .Where(kvp => now - kvp.Value > _window)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
                _recentRequests.TryRemove(key, out _);
        }

        // Single atomic operation: a TryGetValue followed by a TryAdd leaves a window in which
        // two identical requestIds both read "absent" and both get through. TryAdd returning
        // false IS the duplicate signal.
        if (_recentRequests.TryAdd(requestId, now)) return false;

        _logger.LogWarning("Duplicate requestId detected: {RequestId}", requestId);
        return true;
    }
}
