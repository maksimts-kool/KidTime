using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace KidTime.ControlService.Removal;

public interface ISystemUninstaller
{
    Task ScheduleAsync(CancellationToken cancellationToken);
}

public sealed class SystemUninstaller : ISystemUninstaller
{
    private const string ServiceName = "KidTimeControl";

    public async Task ScheduleAsync(CancellationToken cancellationToken)
    {
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var programData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var installPath = GetKidTimePath(programFiles);
        var dataPath = GetKidTimePath(programData);
        var shortcuts = ShortcutPaths();
        var scriptPath = Path.Combine(
            Path.GetFullPath(Path.GetTempPath()),
            $"KidTime-Uninstall-{Guid.NewGuid():N}.ps1");
        var script = BuildScript(installPath, dataPath, shortcuts);

        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        ProtectScript(scriptPath);

        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the KidTime removal helper.");
        }
        catch
        {
            TryDeleteScript(scriptPath);
            throw;
        }
    }

    /// <summary>
    /// The Start menu and desktop entries the service writes. Removal has to take them with it -
    /// a KidTime icon left on the child's desktop after KidTime is gone is a link to nothing, and
    /// the removal flow promises the PC is returned to how it was.
    /// </summary>
    private static IReadOnlyList<string> ShortcutPaths() =>
        new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory }
            .Select(Environment.GetFolderPath)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.Combine(directory, "KidTime.lnk"))
            .ToList();

    private static string GetKidTimePath(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Windows did not provide a valid system directory.");
        var expected = Path.GetFullPath(Path.Combine(root, "KidTime"));
        if (!string.Equals(Path.GetFileName(expected), "KidTime", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(expected)?.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("KidTime removal resolved an unexpected directory.");
        }
        return expected;
    }

    private static string BuildScript(
        string installPath,
        string dataPath,
        IReadOnlyList<string> shortcutPaths) => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        Start-Sleep -Seconds 3
        $serviceName = '{{ServiceName}}'
        $installPath = '{{EscapePowerShellLiteral(installPath)}}'
        $dataPath = '{{EscapePowerShellLiteral(dataPath)}}'
        $shortcuts = @({{string.Join(", ", shortcutPaths.Select(path => $"'{EscapePowerShellLiteral(path)}'"))}})

        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($null -eq $service -or $service.Status -eq 'Stopped') { break }
            Start-Sleep -Milliseconds 500
        }

        Get-Process -Name 'KidTime.SessionAgent' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        & sc.exe delete $serviceName | Out-Null

        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            if (Test-Path -LiteralPath $installPath) {
                Remove-Item -LiteralPath $installPath -Recurse -Force -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath $dataPath) {
                Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue
            }
            if (-not (Test-Path -LiteralPath $installPath) -and -not (Test-Path -LiteralPath $dataPath)) { break }
            Start-Sleep -Milliseconds 500
        }

        foreach ($shortcut in $shortcuts) {
            if (Test-Path -LiteralPath $shortcut) {
                Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue
            }
        }

        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void ProtectScript(string scriptPath)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(scriptPath).SetAccessControl(security);
    }

    private static void TryDeleteScript(string scriptPath)
    {
        try { File.Delete(scriptPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
