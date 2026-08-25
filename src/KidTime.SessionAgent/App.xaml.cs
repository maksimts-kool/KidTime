using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using KidTime.Domain.Contracts;
using Microsoft.Toolkit.Uwp.Notifications;
using Wpf.Ui.Appearance;

namespace KidTime.SessionAgent;

public partial class App : Application
{
    private const uint FailCriticalErrors = 0x0001;
    private const uint NoGeneralProtectionFaultErrorBox = 0x0002;

    private Mutex? _singleInstance;
    private AgentApplicationHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The control service restarts this process every two seconds, so one unhandled
        // exception used to become an endless stack of Windows error dialogs on the child's
        // screen. Faults are suppressed here, recorded, and shown to the parent in the panel.
        SetErrorMode(FailCriticalErrors | NoGeneralProtectionFaultErrorBox);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Clicking a toast makes Windows launch this executable through its notification COM
        // server. That second process is not the one the service supervises, so it would sit
        // there with a dead tray icon; the toast itself is the whole interaction.
        if (WasStartedByToast())
        {
            Shutdown();
            return;
        }

        if (!TryEnterSingleInstance())
        {
            SessionLogger.Information("Another SessionAgent already owns this session; exiting.");
            Shutdown();
            return;
        }

        try
        {
            ApplicationThemeManager.ApplySystemTheme();
            SessionLogger.Information($"SessionAgent {SessionLogger.Version} started.");
            _host = new AgentApplicationHost();
        }
        catch (Exception exception)
        {
            SessionLogger.ReportFault(
                DiagnosticSeverities.Fatal,
                "The KidTime tray agent could not start.",
                exception);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// A failed window, timer, or notification must not take the agent down: while it is gone
    /// the child gets no warnings and no usage is sampled. The fault is reported and the
    /// dispatcher keeps running.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        SessionLogger.ReportFault(
            DiagnosticSeverities.Error,
            "The KidTime tray agent recovered from an unexpected error.",
            e.Exception);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        SessionLogger.ReportFault(
            DiagnosticSeverities.Fatal,
            "The KidTime tray agent stopped because of an unhandled error.",
            e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        SessionLogger.ReportFault(
            DiagnosticSeverities.Error,
            "A KidTime tray agent background task failed.",
            e.Exception);
        e.SetObserved();
    }

    private bool TryEnterSingleInstance()
    {
        // Local\ scopes the name to the Windows session, so each signed-in user gets one agent.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\KidTime.SessionAgent.v1", out var created);
        if (created) return true;
        _singleInstance.Dispose();
        _singleInstance = null;
        return false;
    }

    private static bool WasStartedByToast()
    {
        try { return ToastNotificationManagerCompat.WasCurrentProcessToastActivated(); }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or IOException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
