using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Server;

/// <summary>
/// What the sync loop currently knows about the server. It records states, never sentences: the
/// wording belongs to the tray agent, which paints it in the language the parent chose.
/// </summary>
public sealed class AgentRuntimeStatus
{
    private readonly object _gate = new();
    private ServerConnectionStatus _snapshot = new(
        false,
        ServerConnectionState.Connecting,
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
                State = ServerConnectionState.NotEnrolled
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
                State = ServerConnectionState.Connected,
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
                State = ServerConnectionState.Connected,
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
                State = ServerConnectionState.Offline
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
                State = ServerConnectionState.Offline,
                LastSynchronizationError = Shorten(error)
            };
        }
    }

    private static string Shorten(string value) => value.Length <= 160 ? value : value[..157] + "...";
}
