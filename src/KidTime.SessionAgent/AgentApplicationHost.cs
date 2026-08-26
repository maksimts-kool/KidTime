using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;
using Wpf.Ui.Controls;
using Wpf.Ui.Tray;
using Wpf.Ui.Tray.Controls;
using WpfMenuItem = Wpf.Ui.Controls.MenuItem;

namespace KidTime.SessionAgent;

internal sealed class AgentApplicationHost : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly PipeClient _client = new();
    private readonly WpfMenuItem _serverMenuItem = new() { IsEnabled = false };
    private readonly WpfMenuItem _syncMenuItem = new() { IsEnabled = false };
    private readonly WpfMenuItem _profileMenuItem = new() { IsEnabled = false };
    private readonly NotifyIcon _trayIcon;
    private readonly RoutedNotifyIconEvent _trayLeftClickHandler;
    private readonly StatusWindow _statusWindow;
    private readonly WpfMenuItem _openMenuItem;
    private readonly CountdownCard _countdownCard = new();
    private readonly HwndSource _trayParentSource;
    private readonly int _taskbarCreatedMessage;
    private long _sequence;
    private bool _busy;
    private bool _disposed;

    public AgentApplicationHost()
    {
        // WPF-UI.Tray registers through Application.Current.MainWindow. Create its
        // presentation source without visibly opening it so the icon exists before
        // the first click. EnsureHandle alone does not attach a WPF visual source.
        _statusWindow = new StatusWindow(RemoveKidTimeAsync);
        Application.Current.MainWindow = _statusWindow;
        _statusWindow.ShowActivated = false;
        _statusWindow.ShowInTaskbar = false;
        _statusWindow.Opacity = 0;
        _statusWindow.Show();
        _statusWindow.Hide();
        _statusWindow.Opacity = 1;
        _statusWindow.ShowInTaskbar = true;
        _statusWindow.ShowActivated = true;
        var parentHandle = new WindowInteropHelper(_statusWindow).EnsureHandle();
        _trayParentSource = HwndSource.FromHwnd(parentHandle)
            ?? throw new InvalidOperationException("Could not initialize the tray parent window.");
        _taskbarCreatedMessage = unchecked((int)RegisterWindowMessage("TaskbarCreated"));
        _trayParentSource.AddHook(TrayParentWindowProc);

        var trayMenu = new System.Windows.Controls.ContextMenu();
        trayMenu.Items.Add(_serverMenuItem);
        trayMenu.Items.Add(_syncMenuItem);
        trayMenu.Items.Add(_profileMenuItem);
        trayMenu.Items.Add(new System.Windows.Controls.Separator());
        _openMenuItem = new WpfMenuItem
        {
            Header = AgentUi.Text.TrayOpen,
            Icon = new SymbolIcon(SymbolRegular.Open28)
        };
        _openMenuItem.Click += (_, _) => OpenStatusWindow();
        trayMenu.Items.Add(_openMenuItem);

        _trayIcon = new NotifyIcon
        {
            FocusOnLeftClick = false,
            Icon = CreateTrayIcon(),
            Menu = trayMenu,
            MenuOnRightClick = true,
            TooltipText = AgentUi.Text.TrayTooltipConnecting
        };
        // WPF-UI.Tray 4.3.0 exposes inconsistent nullable metadata for this delegate.
#pragma warning disable CS8622
        _trayLeftClickHandler = (NotifyIcon sender, RoutedEventArgs e) => OpenStatusWindow();
#pragma warning restore CS8622
        _trayIcon.LeftClick += _trayLeftClickHandler;
        RegisterTrayIcon("startup");

        UpdateTrayStatus(null);
        _timer.Tick += TimerTick;
        _timer.Start();
        _ = SendSampleAsync();
    }

    private async void TimerTick(object? sender, EventArgs e) => await SendSampleAsync();

    private IntPtr TrayParentWindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            RegisterTrayIcon("Windows taskbar restart");
        }

        handled = false;
        return IntPtr.Zero;
    }

    /// <summary>
    /// Registration talks to the shell and can fail while Explorer is restarting or still
    /// starting up. It is retried on the next TaskbarCreated broadcast, so a failure here is
    /// recorded rather than propagated out of a window procedure.
    /// </summary>
    private void RegisterTrayIcon(string reason)
    {
        try
        {
            _trayIcon.Register();
            SessionLogger.Information(_trayIcon.IsRegistered
                ? $"Tray icon registered ({reason})."
                : $"Tray icon registration failed ({reason}).");
        }
        catch (Exception exception)
        {
            SessionLogger.ReportFault(
                DiagnosticSeverities.Warning,
                $"The KidTime tray icon could not be registered ({reason}).",
                exception);
        }
    }

    private async Task SendSampleAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        var reports = SessionLogger.TakePendingReports();
        var delivered = false;
        try
        {
            var foreground = ForegroundDetector.GetForeground();
            var idleSeconds = ForegroundDetector.GetIdleSeconds();
            var sequence = Interlocked.Increment(ref _sequence);
            var sample = new SessionUsageSample(
                sequence,
                _stopwatch.ElapsedMilliseconds,
                idleSeconds >= 300,
                idleSeconds,
                foreground.ProcessId,
                foreground.WindowTitle,
                foreground.Application,
                ShouldRequestStatus(sequence));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var state = await _client.ExchangeAsync(sample, reports, timeout.Token);
            delivered = true;
            if (state is not null) ApplyState(state);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            SessionLogger.Information("ControlService IPC unavailable; it will be retried.", exception);
        }
        catch (Exception exception)
        {
            // Anything else is a defect. Sampling continues on the next tick and the parent
            // sees the fault in the panel instead of the child seeing a crash.
            SessionLogger.ReportFault(
                DiagnosticSeverities.Error,
                "A KidTime usage sample could not be processed.",
                exception);
        }
        finally
        {
            if (!delivered && reports.Count > 0) SessionLogger.Requeue(reports);
            _busy = false;
        }
    }

    /// <summary>
    /// The full snapshot costs the service a rule evaluation per controlled application. The
    /// window needs it live; the tray tooltip is happy with a refresh every thirty seconds.
    /// </summary>
    private bool ShouldRequestStatus(long sequence) =>
        _statusWindow.IsVisible || sequence <= 1 || sequence % 15 == 0;

    private async Task<DeviceRemovalResult> RemoveKidTimeAsync(
        ParentRemovalRequest request,
        CancellationToken cancellationToken)
    {
        _timer.Stop();
        while (_busy)
            await Task.Delay(100);
        _busy = true;
        var accepted = false;
        try
        {
            var result = await _client.RequestRemovalAsync(request, cancellationToken);
            accepted = result.Accepted;
            if (result.Accepted)
            {
                NativeWindowsNotification.Unregister();
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            SessionLogger.Information("Parent-authorized removal IPC failed.", exception);
            return new DeviceRemovalResult(false, AgentUi.Text.RemovalServiceSilent);
        }
        finally
        {
            _busy = false;
            if (!accepted && !_disposed) _timer.Start();
        }
    }

    private void ApplyState(EnforcementState state)
    {
        if (AgentUi.TrySetLanguage(state.Language)) ApplyLanguage();

        if (state.Status is { } status)
        {
            UpdateTrayStatus(status);
            _statusWindow.UpdateStatus(status);
        }

        if (state.Notification is not { } notification) return;
        if (notification.DismissPersistentNotification && notification.PersistentNotificationKey is { } dismissKey)
        {
            NativeWindowsNotification.DismissFinalWarning(dismissKey);
            _countdownCard.Dismiss(dismissKey);
        }
        else if (notification.CountdownSeconds is int countdownSeconds
                 && notification.PersistentNotificationKey is { } warningKey)
        {
            // The card is the final warning, because it shows the one thing a toast cannot - the
            // seconds actually draining away - and two warnings for one deadline only compete for
            // the same corner. The toast remains the fallback: the card is best-effort by design,
            // so a child must never be left with no warning because it failed to draw.
            var shown = _countdownCard.Show(
                warningKey,
                notification.Title,
                notification.Message,
                countdownSeconds);
            if (!shown)
                NativeWindowsNotification.ShowFinalWarning(
                    warningKey,
                    notification.Title,
                    notification.Message,
                    countdownSeconds);
        }
        else
        {
            NativeWindowsNotification.Show(notification.Title, notification.Message, notification.IsUrgent);
        }
    }

    /// <summary>Repaints the parts of the tray chrome that carry fixed wording.</summary>
    private void ApplyLanguage()
    {
        _openMenuItem.Header = AgentUi.Text.TrayOpen;
        _statusWindow.ApplyLanguage();
        UpdateTrayStatus(null);
    }

    private void OpenStatusWindow()
    {
        _statusWindow.Show();
        if (_statusWindow.WindowState == WindowState.Minimized)
            _statusWindow.WindowState = WindowState.Normal;
        _statusWindow.Activate();
    }

    private void UpdateTrayStatus(SessionStatusSnapshot? status)
    {
        var text = AgentUi.Text;
        if (status is null)
        {
            _serverMenuItem.Header = text.TrayRow(text.TrayServerLabel, text.TrayConnectingToService);
            _syncMenuItem.Header = text.TrayRow(text.TraySyncLabel, text.TrayWaitingForService);
            _profileMenuItem.Header = text.TrayRow(text.TrayProfileLabel, text.TrayLoading);
            _trayIcon.TooltipText = text.TrayTooltipConnecting;
            return;
        }

        _serverMenuItem.Header = text.TrayRow(text.TrayServerLabel, ConnectionMessage(text, status.Server.State));
        _syncMenuItem.Header = text.TrayRow(
            text.TraySyncLabel,
            status.Server.LastSynchronizationError is { Length: > 0 }
                ? text.TraySyncFailed(text.Relative(status.Server.LastSuccessfulSynchronizationUtc))
                : text.Relative(status.Server.LastSuccessfulSynchronizationUtc));
        _profileMenuItem.Header = text.TrayRow(
            text.TrayProfileLabel,
            status.ControlledUserName ?? text.NotSelected);
        var allowance = status.ScreenTime;
        _trayIcon.TooltipText = allowance.IsAllowed && allowance.DailyRemainingSeconds is int remaining
            ? text.TrayTooltipRemaining(text.DurationLabel(remaining))
            : allowance.IsAllowed
                ? text.TrayTooltipAvailable
                : text.TrayTooltipUnavailable;
    }

    private static string ConnectionMessage(AgentStrings text, ServerConnectionState state) => state switch
    {
        ServerConnectionState.NotEnrolled => text.DeviceNotEnrolled,
        ServerConnectionState.Connected => text.Connected,
        ServerConnectionState.Offline => text.OfflineCachedRules,
        _ => text.ConnectingToServer
    };

    private static BitmapSource CreateTrayIcon()
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            SystemIcons.Shield.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
        source.Freeze();
        return source;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _timer.Stop();
            _timer.Tick -= TimerTick;
            _trayParentSource.RemoveHook(TrayParentWindowProc);
            _trayIcon.LeftClick -= _trayLeftClickHandler;
            _trayIcon.Dispose();
            _countdownCard.Dispose();
            _statusWindow.CloseForExit();
        }
        catch (Exception exception)
        {
            SessionLogger.Information("The KidTime tray agent did not shut down cleanly.", exception);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
