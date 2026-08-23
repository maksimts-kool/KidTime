using System.Reflection;

namespace KidTime.ControlService.Server;

public sealed class AgentUpdateState
{
    private readonly object _gate = new();
    private AgentUpdateStatus _status = new("Checking", null, null);

    public string CurrentVersion { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    public AgentUpdateStatus Snapshot
    {
        get { lock (_gate) return _status; }
    }

    public void Set(string status, string? error = null)
    {
        lock (_gate) _status = new AgentUpdateStatus(status, error, DateTimeOffset.UtcNow);
    }
}

public sealed record AgentUpdateStatus(string Status, string? Error, DateTimeOffset? CheckedAtUtc);
