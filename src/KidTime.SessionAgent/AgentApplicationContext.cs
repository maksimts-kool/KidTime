using System.Diagnostics;
using KidTime.Domain.Contracts;

namespace KidTime.SessionAgent;

internal sealed class AgentApplicationContext : ApplicationContext
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2_000 };
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly PipeClient _client = new();
    private long _sequence;
    private bool _busy;

    public AgentApplicationContext()
    {
        _timer.Tick += Tick;
        _timer.Start();
        _ = SendSampleAsync();
    }

    private async void Tick(object? sender, EventArgs e) => await SendSampleAsync();

    private async Task SendSampleAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var foreground = ForegroundDetector.GetForeground();
            var idleSeconds = ForegroundDetector.GetIdleSeconds();
            var sample = new SessionUsageSample(
                Interlocked.Increment(ref _sequence),
                _stopwatch.ElapsedMilliseconds,
                idleSeconds >= 300,
                idleSeconds,
                foreground.ProcessId,
                foreground.WindowTitle,
                foreground.Application);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var state = await _client.ExchangeAsync(sample, timeout.Token);
            if (state is not null) ApplyState(state);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            SessionLogger.Information("ControlService IPC unavailable; it will be retried.", exception);
        }
        finally { _busy = false; }
    }

    private void ApplyState(EnforcementState state)
    {
        if (state.Notification is { } notification)
        {
            if (notification.DismissPersistentNotification && notification.PersistentNotificationKey is { } dismissKey)
                PersistentWarningBanner.Dismiss(dismissKey);
            else if (notification.CountdownSeconds is int countdownSeconds
                     && notification.PersistentNotificationKey is { } warningKey)
                PersistentWarningBanner.Display(warningKey, notification.Title, notification.Message, countdownSeconds);
            else
                NativeWindowsNotification.Show(notification.Title, notification.Message);
        }
    }
}
