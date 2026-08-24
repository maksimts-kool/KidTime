using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KidTime.Domain.Contracts;
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
    private readonly HwndSource _trayParentSource;
    private readonly int _taskbarCreatedMessage;
    private SessionStatusSnapshot? _latestStatus;
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
        var openItem = new WpfMenuItem
        {
            Header = "Open KidTime",
            Icon = new SymbolIcon(SymbolRegular.Open28)
        };
        openItem.Click += (_, _) => OpenStatusWindow();
        trayMenu.Items.Add(openItem);

        _trayIcon = new NotifyIcon
        {
            FocusOnLeftClick = false,
            Icon = CreateTrayIcon(),
            Menu = trayMenu,
            MenuOnRightClick = true,
            TooltipText = "KidTime - connecting to service"
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

    private void RegisterTrayIcon(string reason)
    {
        _trayIcon.Register();
        SessionLogger.Information(_trayIcon.IsRegistered
            ? $"Tray icon registered ({reason})."
            : $"Tray icon registration failed ({reason}).");
    }

    private async Task SendSampleAsync()
    {
        if (_busy || _disposed) return;
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
        finally
        {
            _busy = false;
        }
    }

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
            return new DeviceRemovalResult(false, "The KidTime service did not answer. Wait a moment and try again.");
        }
        finally
        {
            _busy = false;
            if (!accepted && !_disposed) _timer.Start();
        }
    }

    private void ApplyState(EnforcementState state)
    {
        if (state.Status is { } status)
        {
            _latestStatus = status;
            UpdateTrayStatus(status);
            _statusWindow.UpdateStatus(status);
        }

        if (state.Notification is not { } notification) return;
        if (notification.DismissPersistentNotification && notification.PersistentNotificationKey is { } dismissKey)
        {
            NativeWindowsNotification.DismissFinalWarning(dismissKey);
        }
        else if (notification.CountdownSeconds is int countdownSeconds
                 && notification.PersistentNotificationKey is { } warningKey)
        {
            NativeWindowsNotification.ShowFinalWarning(
                warningKey,
                notification.Title,
                notification.Message,
                countdownSeconds);
        }
        else
        {
            NativeWindowsNotification.Show(notification.Title, notification.Message);
        }
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
        if (status is null)
        {
            _serverMenuItem.Header = "Server  ·  Connecting to service";
            _syncMenuItem.Header = "Synchronization  ·  Waiting for service";
            _profileMenuItem.Header = "Controlled profile  ·  Loading";
            return;
        }

        _serverMenuItem.Header = $"Server  ·  {status.Server.ConnectionMessage}";
        _syncMenuItem.Header = status.Server.LastSynchronizationError is { Length: > 0 }
            ? $"Synchronization  ·  Failed (last success {FormatRelative(status.Server.LastSuccessfulSynchronizationUtc)})"
            : $"Synchronization  ·  {FormatRelative(status.Server.LastSuccessfulSynchronizationUtc)}";
        _profileMenuItem.Header = $"Controlled profile  ·  {status.ControlledUserName ?? "Not selected"}";
        var allowance = status.ScreenTime;
        _trayIcon.TooltipText = allowance.IsAllowed && allowance.DailyRemainingSeconds is int remaining
            ? $"KidTime - {FormatDuration(remaining)} left today"
            : allowance.IsAllowed
                ? "KidTime - screen time available"
                : "KidTime - screen time unavailable";
    }

    private static BitmapSource CreateTrayIcon()
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            SystemIcons.Shield.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));
        source.Freeze();
        return source;
    }

    private static string FormatRelative(DateTimeOffset? time)
    {
        if (time is null) return "Not yet";
        var elapsed = DateTimeOffset.UtcNow - time.Value;
        if (elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} h ago";
        return time.Value.ToLocalTime().ToString("ddd HH:mm");
    }

    private static string FormatDuration(int seconds)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling(seconds / 60d));
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= TimerTick;
        _trayParentSource.RemoveHook(TrayParentWindowProc);
        _trayIcon.LeftClick -= _trayLeftClickHandler;
        _trayIcon.Dispose();
        _statusWindow.CloseForExit();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
