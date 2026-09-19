using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KidTime.Domain.Applications;
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
    private readonly CountdownCard _countdownCard;

    // What the service offered on the last reply. The card and the toast button both need to know
    // whether asking is possible before they draw anything, and this is the only source of truth
    // for that - the agent never works it out for itself.
    private IReadOnlyList<TimeExtensionOffer> _extensionOffers = [];
    private readonly HwndSource _trayParentSource;
    private readonly int _taskbarCreatedMessage;

    /// <summary>
    /// Waited on for as long as the agent runs, so the shortcuts on the child's Start menu and
    /// desktop open the same window the tray icon does. See <see cref="AgentActivation"/> for why
    /// a shortcut signals this rather than starting a second agent.
    /// </summary>
    private readonly EventWaitHandle _showRequested;

    private readonly RegisteredWaitHandle _showRegistration;
    private long _sequence;
    private bool _busy;
    private bool _disposed;

    public AgentApplicationHost()
    {
        // WPF-UI.Tray registers through Application.Current.MainWindow. Create its
        // presentation source without visibly opening it so the icon exists before
        // the first click. EnsureHandle alone does not attach a WPF visual source.
        _countdownCard = new CountdownCard(OpenExtraTimeRequest);
        _statusWindow = new StatusWindow(RemoveKidTimeAsync, RequestExtraTimeAsync);
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

        // The shortcuts reach the window through here. A thread-pool wait rather than a thread of
        // its own: this is idle for hours at a time on the household's weakest PC.
        _showRequested = AgentActivation.CreateListener();
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showRequested,
            (_, _) => OnShowRequested(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        NativeWindowsNotification.ExtraTimeRequested += OnToastExtraTimeRequested;
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
                ShouldRequestStatus(sequence),
                AudioSessionDetector.GetAudibleApplications(),
                // Read here and never sent: the title stays in this session and only the
                // classification crosses, so the service can explain the household's web
                // filtering without either end learning which site the child asked for.
                BrowserPageErrorDetector.Detect(foreground.Application, foreground.WindowTitle));
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

        // Read before the notifications below, so a final warning drawn on this same reply
        // already knows whether it may offer to ask for more time.
        _extensionOffers = state.ExtensionOffers ?? [];
        _statusWindow.UpdateExtraTime(_extensionOffers);

        if (state.Status is { } status)
        {
            UpdateTrayStatus(status);
            _statusWindow.UpdateStatus(status);
        }

        // Everything the service had waiting arrives together, so a final warning is never held
        // behind an earlier reminder while its deadline runs down.
        foreach (var notification in state.Notifications) Deliver(notification);
    }

    private void Deliver(UserNotification notification)
    {
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
            var canAsk = CanAskForExtraTime();
            var shown = _countdownCard.Show(
                warningKey,
                notification.Title,
                notification.Message,
                countdownSeconds,
                canAsk);
            if (!shown)
                NativeWindowsNotification.ShowFinalWarning(
                    warningKey,
                    notification.Title,
                    notification.Message,
                    countdownSeconds,
                    canAsk ? AgentUi.Text.ExtraTimeAskButton : null);
        }
        else if (notification.IsUrgent && notification.PersistentNotificationKey is { } reminderKey)
        {
            // A running-out reminder: urgent, so it survives a full-screen game, but keyed so the
            // next one takes its place rather than adding a banner beside it. This is the one a
            // child absorbed in a game actually sees, so it carries the way to ask for more time.
            NativeWindowsNotification.ShowUrgentReminder(
                reminderKey,
                notification.Title,
                notification.Message,
                CanAskForExtraTime() ? AgentUi.Text.ExtraTimeAskButton : null);
        }
        else
        {
            NativeWindowsNotification.Show(notification.Title, notification.Message, notification.IsUrgent);
        }
    }

    /// <summary>
    /// Whether the service is currently offering extra time for anything, and the child is not
    /// already waiting on an answer. Only then is the button worth drawing: one that replied
    /// "you cannot ask for that" would be worse than no button at all.
    /// </summary>
    private bool CanAskForExtraTime() =>
        _extensionOffers.Any(offer => offer.State != TimeExtensionOfferState.Pending);

    /// <summary>
    /// Opens the request for whatever is actually running out - the application in the foreground
    /// when there is one, the PC otherwise. Nothing here asks for anything by itself; the child
    /// still chooses the amount in the popup that opens.
    /// </summary>
    private void OpenExtraTimeRequest()
    {
        try
        {
            var offer = _extensionOffers.FirstOrDefault(item => item.ApplicationIdentityKey is not null)
                        ?? _extensionOffers.FirstOrDefault();
            _statusWindow.ShowExtraTimeRequest(offer);
        }
        catch (Exception exception)
        {
            SessionLogger.ReportFault(
                DiagnosticSeverities.Error,
                "The KidTime screen-time window could not be opened for an extra-time request.",
                exception);
        }
    }

    /// <summary>
    /// The toast's own button. Windows raises this on a background thread through the
    /// notification COM server, so it has to be marshalled onto the UI thread before any window
    /// is touched.
    /// </summary>
    private void OnToastExtraTimeRequested(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Application.Current?.Dispatcher.BeginInvoke(OpenExtraTimeRequest);
    }

    private async Task<TimeExtensionSubmissionResult> RequestExtraTimeAsync(
        TimeExtensionSubmission submission,
        CancellationToken cancellationToken)
    {
        // The sampling exchange and this one share a single-instance pipe, so they must not
        // overlap. A press is rare and the sample is due again in two seconds either way.
        while (_busy) await Task.Delay(50, cancellationToken);
        _busy = true;
        try { return await _client.RequestTimeExtensionAsync(submission, cancellationToken); }
        finally { _busy = false; }
    }

    /// <summary>Repaints the parts of the tray chrome that carry fixed wording.</summary>
    private void ApplyLanguage()
    {
        _openMenuItem.Header = AgentUi.Text.TrayOpen;
        _statusWindow.ApplyLanguage();
        UpdateTrayStatus(null);
    }

    /// <summary>
    /// A shortcut asked for the window. The wait runs on a thread-pool thread, so the work has to
    /// be handed to the dispatcher; and the agent may be shutting down by the time it arrives,
    /// which is why both the disposal flag and the application are checked there rather than here.
    /// </summary>
    private void OnShowRequested()
    {
        if (_disposed) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            OpenStatusWindow();
        });
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
            NativeWindowsNotification.ExtraTimeRequested -= OnToastExtraTimeRequested;
            _trayIcon.LeftClick -= _trayLeftClickHandler;
            _trayIcon.Dispose();
            _showRegistration.Unregister(null);
            _showRequested.Dispose();
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
