using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Server;

public sealed class AgentRuntimeStatus
{
    private readonly object _gate = new();
    private ServerConnectionStatus _snapshot = new(
        false,
        "Connecting to server",
        null,
        null,
        null);

    public ServerConnectionStatus Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void MarkNotEnrolled()
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                IsConnected = false,
                ConnectionMessage = "Device is not enrolled"
            };
        }
    }

    public void MarkContactSucceeded()
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                IsConnected = true,
                ConnectionMessage = "Connected",
                LastSuccessfulContactUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public void MarkSynchronizationSucceeded()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                IsConnected = true,
                ConnectionMessage = "Connected",
                LastSuccessfulContactUtc = now,
                LastSuccessfulSynchronizationUtc = now,
                LastSynchronizationError = null
            };
        }
    }

    public void MarkDisconnected()
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                IsConnected = false,
                ConnectionMessage = "Offline - cached rules active"
            };
        }
    }

    public void MarkSynchronizationFailed(string error)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                IsConnected = false,
                ConnectionMessage = "Offline - cached rules active",
                LastSynchronizationError = Shorten(error)
            };
        }
    }

    private static string Shorten(string value) => value.Length <= 160 ? value : value[..157] + "...";
}
