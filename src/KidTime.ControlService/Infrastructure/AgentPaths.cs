namespace KidTime.ControlService.Infrastructure;

public static class AgentPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KidTime");
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");
    public static string ConfigurationFile { get; } = Path.Combine(DataDirectory, "agentsettings.json");
    public static string CredentialFile { get; } = Path.Combine(DataDirectory, "device.credential");
    public static string DatabaseFile { get; } = Path.Combine(DataDirectory, "agent.db");
    public static string ServiceLogFile { get; } = Path.Combine(LogDirectory, "control-service.ndjson");
    public static string DiagnosticSpoolFile { get; } = Path.Combine(LogDirectory, "diagnostics.ndjson");
    public static string UpdateDirectory { get; } = Path.Combine(DataDirectory, "updates");
    public static string UpdateStatusFile { get; } = Path.Combine(UpdateDirectory, "status.json");
    public static string UpdateScriptFile { get; } = Path.Combine(UpdateDirectory, "apply-update.ps1");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(UpdateDirectory);
    }
}
