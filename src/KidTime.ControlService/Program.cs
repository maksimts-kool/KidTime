using KidTime.ControlService;
using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.ControlService.Ipc;
using KidTime.ControlService.Removal;
using KidTime.ControlService.Server;
using KidTime.ControlService.Sessions;
using Microsoft.Extensions.Options;

if (args.FirstOrDefault()?.Equals("enroll", StringComparison.OrdinalIgnoreCase) == true)
{
    Environment.ExitCode = await EnrollmentCommand.RunAsync(args[1..]);
    return;
}

AgentPaths.EnsureDirectories();

// The controlled PC is normally unreachable once it is handed over, so every fault has to end
// up in the parent's panel. The reporter is created before the host so that a failure during
// startup - the window where nothing else is wired yet - is still recorded and uploaded later.
var diagnostics = new DiagnosticReporter();
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => diagnostics.ReportFatal(
    "The control service stopped because of an unhandled exception.",
    eventArgs.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    diagnostics.ReportError("A background task failed without being observed.", eventArgs.Exception);
    eventArgs.SetObserved();
};

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(AgentPaths.ConfigurationFile, optional: true, reloadOnChange: true);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddWindowsService(options => options.ServiceName = "KidTime Control Service");
builder.Logging.AddProvider(new JsonFileLoggerProvider(AgentPaths.ServiceLogFile, diagnostics));
builder.Services.AddSingleton(diagnostics);
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<LocalStore>();
builder.Services.AddSingleton<AgentApiClient>();
builder.Services.AddSingleton<SystemUninstaller>();
builder.Services.AddSingleton<IParentDeviceRemovalClient>(provider => provider.GetRequiredService<AgentApiClient>());
builder.Services.AddSingleton<ISystemUninstaller>(provider => provider.GetRequiredService<SystemUninstaller>());
builder.Services.AddSingleton<DeviceRemovalService>();
builder.Services.AddSingleton<EnforcementCoordinator>();
builder.Services.AddSingleton<ApplicationInspector>();
builder.Services.AddSingleton<ApplicationIconExtractor>();
builder.Services.AddSingleton<InstalledApplicationDiscovery>();
builder.Services.AddSingleton<TrustedClock>();
builder.Services.AddSingleton<SyncTrigger>();
builder.Services.AddSingleton<WindowsAccountProvider>();
builder.Services.AddSingleton<AgentUpdateState>();
builder.Services.AddSingleton<AgentRuntimeStatus>();
builder.Services.AddSingleton<SessionAgentSupervisor>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<AgentUpdateWorker>();
builder.Services.AddHostedService<RealtimeCommandClient>();
builder.Services.AddHostedService<NamedPipeHost>();
builder.Services.AddHostedService<ProcessMonitor>();
builder.Services.AddHostedService<SessionLockoutService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SessionAgentSupervisor>());

var host = builder.Build();
await host.RunAsync();
