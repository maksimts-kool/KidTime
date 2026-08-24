using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;

namespace KidTime.Setup;

public sealed record InstallationRequest(string ServerUrl, string EnrollmentCode, LocalAccount ChildAccount);

public sealed record InstallationProgress(int Percent, string Message);

public static class InstallerEngine
{
    private const string ServiceName = "KidTimeControl";
    private const string PayloadResourceName = "KidTime.Payload.zip";

    public static string DataPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KidTime");

    public static string CredentialPath { get; } = Path.Combine(DataPath, "device.credential");

    public static string InstallPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "KidTime");

    public static async Task InstallAsync(
        InstallationRequest request,
        IProgress<InstallationProgress> progress,
        CancellationToken cancellationToken)
    {
        EnsureAdministrator();
        if (File.Exists(CredentialPath))
            throw new InvalidOperationException("This PC is already connected to KidTime. Open the existing device in the web panel instead of enrolling it again.");

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"KidTimeSetup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryPath);
        try
        {
            progress.Report(new InstallationProgress(10, "Preparing the KidTime service files…"));
            await ExtractPayloadAsync(temporaryPath, cancellationToken);

            progress.Report(new InstallationProgress(35, "Installing the KidTime service…"));
            await StopExistingServiceAsync(cancellationToken);
            StopSessionAgents();
            await ReleaseExistingFilesAsync(InstallPath, cancellationToken);
            await Task.Run(() => CopyDirectory(temporaryPath, InstallPath), cancellationToken);
            Directory.CreateDirectory(DataPath);

            progress.Report(new InstallationProgress(55, "Protecting the installation from the child account…"));
            await HardenDirectoriesAsync(InstallPath, DataPath, cancellationToken);

            var serviceExecutable = Path.Combine(InstallPath, "KidTime.ControlService.exe");
            if (!File.Exists(serviceExecutable)) throw new InvalidDataException("The setup payload is incomplete.");
            progress.Report(new InstallationProgress(70, "Registering the Windows service…"));
            await ConfigureServiceAsync(serviceExecutable, cancellationToken);

            progress.Report(new InstallationProgress(80, "Connecting securely to the KidTime server…"));
            var enrollment = await RunProcessAsync(serviceExecutable,
                [
                    "enroll",
                    "--server", request.ServerUrl,
                    "--token", request.EnrollmentCode,
                    "--user-sid", request.ChildAccount.Sid,
                    "--user-name", request.ChildAccount.AccountName,
                    "--user-display-name", request.ChildAccount.DisplayName
                ],
                cancellationToken);
            if (enrollment.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(enrollment.Error)
                    ? "The server could not enroll this PC. Check the server URL and enrollment code, then try again."
                    : enrollment.Error.Trim());

            progress.Report(new InstallationProgress(92, "Starting KidTime…"));
            await RunRequiredProcessAsync("sc.exe", ["start", ServiceName], cancellationToken, 0, 1056);
            using var service = new ServiceController(ServiceName);
            await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)), cancellationToken);
            progress.Report(new InstallationProgress(100, "KidTime is running on this PC"));
        }
        finally
        {
            try { await Task.Run(() => Directory.Delete(temporaryPath, recursive: true), CancellationToken.None); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("KidTime Setup needs administrator approval. Close it, reopen it, and approve the Windows prompt.");
    }

    private static async Task ExtractPayloadAsync(string destination, CancellationToken cancellationToken)
    {
        await using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidDataException("This setup file does not contain the KidTime service package. Download it again from the web panel.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The setup payload contains an invalid path.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = File.Create(target);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task StopExistingServiceAsync(CancellationToken cancellationToken)
    {
        if (!ServiceController.GetServices().Any(service => string.Equals(service.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase)))
            return;
        using var controller = new ServiceController(ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped) return;
        controller.Stop();
        await Task.Run(() => controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)), cancellationToken);
    }

    private static void StopSessionAgents()
    {
        foreach (var process in Process.GetProcessesByName("KidTime.SessionAgent"))
        {
            using (process)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// An earlier installation can leave files an administrator cannot open, so reinstalling
    /// over it first restores inherited access to whatever is already there.
    /// </summary>
    private static async Task ReleaseExistingFilesAsync(string installPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(installPath)) return;
        await ResetChildAccessAsync(installPath, cancellationToken);
    }

    private static async Task HardenDirectoriesAsync(string installPath, string dataPath, CancellationToken cancellationToken)
    {
        // (OI)(CI) rights describe what a container passes down, so icacls skips them on plain
        // files and would leave every copied file with an empty DACL if this ran with /T.
        // Each root gets the inheritable rights and its existing children are reset to inherit them.
        await RunRequiredProcessAsync("icacls.exe",
            [installPath, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-32-545:(OI)(CI)RX"],
            cancellationToken,
            0);
        await ResetChildAccessAsync(installPath, cancellationToken);
        await RunRequiredProcessAsync("icacls.exe",
            [dataPath, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"],
            cancellationToken,
            0);
        await ResetChildAccessAsync(dataPath, cancellationToken);
    }

    private static async Task ResetChildAccessAsync(string path, CancellationToken cancellationToken)
    {
        // The wildcard keeps the directory's own explicit rights while every child inherits them.
        // An empty directory makes icacls report a missing file, which is not a failure here.
        await RunProcessAsync("icacls.exe", [Path.Combine(path, "*"), "/reset", "/T", "/C", "/Q"], cancellationToken);
    }

    private static async Task ConfigureServiceAsync(string serviceExecutable, CancellationToken cancellationToken)
    {
        var exists = ServiceController.GetServices().Any(service => string.Equals(service.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
        var verb = exists ? "config" : "create";
        await RunRequiredProcessAsync("sc.exe",
            [verb, ServiceName, "binPath=", $"\"{serviceExecutable}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "KidTime Control Service"],
            cancellationToken,
            0);
        await RunRequiredProcessAsync("sc.exe", ["description", ServiceName, "Enforces KidTime PC and application limits using cached offline rules."], cancellationToken, 0);
        await RunRequiredProcessAsync("sc.exe", ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"], cancellationToken, 0);
        await RunRequiredProcessAsync("sc.exe", ["failureflag", ServiceName, "1"], cancellationToken, 0);
        await RunRequiredProcessAsync("sc.exe", ["sdset", ServiceName, "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLORC;;;IU)"], cancellationToken, 0);
    }

    private static async Task RunRequiredProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        params int[] allowedExitCodes)
    {
        var result = await RunProcessAsync(fileName, arguments, cancellationToken);
        if (!allowedExitCodes.Contains(result.ExitCode))
            throw new InvalidOperationException($"Windows could not complete the installation step ({Path.GetFileName(fileName)} exited with {result.ExitCode}). {result.Error}".Trim());
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Windows could not start {Path.GetFileName(fileName)}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
