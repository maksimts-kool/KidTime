using System.Diagnostics;

namespace KidTime.ControlService.Infrastructure;

public sealed class TrustedClock
{
    private readonly object _lock = new();
    private DateTimeOffset _anchorUtc = DateTimeOffset.UtcNow;
    private long _anchorTimestamp = Stopwatch.GetTimestamp();

    public DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _anchorUtc + Stopwatch.GetElapsedTime(_anchorTimestamp);
        }
    }

    public void Synchronize(DateTimeOffset serverUtc)
    {
        lock (_lock)
        {
            _anchorUtc = serverUtc;
            _anchorTimestamp = Stopwatch.GetTimestamp();
        }
    }
}
