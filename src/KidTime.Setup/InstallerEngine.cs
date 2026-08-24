using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;

namespace KidTime.Setup;

public sealed record InstallationRequest(string ServerUrl, string EnrollmentToken, LocalAccount ChildAccount);

public static class InstallerEngine
{
    private const string ServiceName = "KidTimeControl";
    private const string PayloadResourceName = "KidTime.Payload.zip";

    public static async Task InstallAsync(
        InstallationRequest request,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        EnsureAdministrator();
        var dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KidTime");
        var credentialPath = Path.Combine(dataPath, "device.credential");
        if (File.Exists(credentialPath))
            throw new InvalidOperationException("This PC is already connected to KidTime. Open the existing device in the web panel instead of enrolling it again.");

        var installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KidTime");
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"KidTimeSetup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryPath);
        try
        {
            progress.Report("Preparing KidTime…");
            await ExtractPayloadAsync(temporaryPath, cancellationToken);

            progress.Report("Installing Windows services…");
            await StopExistingServiceAsync(cancellationToken);
            StopSessionAgents();
            await Task.Run(() => CopyDirectory(temporaryPath, installPath), cancellationToken);
            Directory.CreateDirectory(dataPath);
            await HardenDirectoriesAsync(installPath, dataPath, cancellationToken);

            var serviceExecutable = Path.Combine(installPath, "KidTime.ControlService.exe");
            if (!File.Exists(serviceExecutable)) throw new InvalidDataException("The setup payload is incomplete.");
            await ConfigureServiceAsync(serviceExecutable, cancellationToken);

            progress.Report("Connecting securely to the KidTime server…");
            var enrollment = await RunProcessAsync(serviceExecutable,
                [
                    "enroll",
                    "--server", request.ServerUrl,
                    "--token", request.EnrollmentToken,
                    "--user-sid", request.ChildAccount.Sid,
                    "--user-name", request.ChildAccount.AccountName,
                    "--user-display-name", request.ChildAccount.DisplayName
                ],
                cancellationToken);
            if (enrollment.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(enrollment.Error)
                    ? "The server could not enroll this PC. Check the server URL and enrollment token, then try again."
                    : enrollment.Error.Trim());

            progress.Report("Starting KidTime…");
            await RunRequiredProcessAsync("sc.exe", ["start", ServiceName], cancellationToken, 0, 1056);
            using var service = new ServiceController(ServiceName);
            await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)), cancellationToken);
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
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static async Task HardenDirectoriesAsync(string installPath, string dataPath, CancellationToken cancellationToken)
    {
        await RunRequiredProcessAsync("icacls.exe",
            [installPath, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-32-545:(OI)(CI)RX", "/T", "/C"],
            cancellationToken,
            0);
        await RunRequiredProcessAsync("icacls.exe",
            [dataPath, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "/T", "/C"],
            cancellationToken,
            0);
    }

    private static async Task ConfigureServiceAsync(string serviceExecutable, CancellationToken cancellationToken)
    {
        var exists = ServiceController.GetServices().Any(service => string.Equals(service.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            await RunRequiredProcessAsync("sc.exe", ["config", ServiceName, "binPath=", $"\"{serviceExecutable}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "KidTime Control Service"], cancellationToken, 0);
        }
        else
        {
            await RunRequiredProcessAsync("sc.exe", ["create", ServiceName, "binPath=", $"\"{serviceExecutable}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "KidTime Control Service"], cancellationToken, 0);
        }
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
