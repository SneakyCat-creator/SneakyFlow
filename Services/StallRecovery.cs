namespace SneakyFlow.Services;

/// <summary>At most three local requester repairs an hour, separated by ten minutes.</summary>
internal sealed class StallRecovery
{
    private readonly Queue<DateTimeOffset> _attempts = new();
    public bool TryRecover(DateTimeOffset now, TimeSpan silence, bool eligible)
    {
        while (_attempts.TryPeek(out var first) && now - first >= TimeSpan.FromHours(1)) _attempts.Dequeue();
        if (!eligible || silence < TimeSpan.FromMinutes(2) || _attempts.Count >= 3 ||
            (_attempts.Count > 0 && now - _attempts.Last() < TimeSpan.FromMinutes(10))) return false;
        _attempts.Enqueue(now);
        return true;
    }
}
