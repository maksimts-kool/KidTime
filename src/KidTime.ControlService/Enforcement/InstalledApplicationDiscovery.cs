using System.Diagnostics;
using System.Text.Json;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Applications;
using Microsoft.Win32;

namespace KidTime.ControlService.Enforcement;

public sealed class InstalledApplicationDiscovery(
    LocalStore store,
    ApplicationIconExtractor iconExtractor,
    ILogger<InstalledApplicationDiscovery> logger)
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var applications = new Dictionary<string, ApplicationDescriptor>(StringComparer.OrdinalIgnoreCase);
        ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, applications);
        ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, applications);
        ReadRegistry(RegistryHive.CurrentUser, RegistryView.Default, applications);
        foreach (var discovered in (await ReadMsixAsync(cancellationToken))
                     .Where(ApplicationCatalogPolicy.IsUserManageable))
        {
            var descriptor = ApplicationCatalogPolicy.NormalizeForCatalog(discovered);
            applications[ApplicationIdentity.CreateKey(descriptor)] = descriptor;
        }

        foreach (var (identity, descriptor) in applications)
            await store.UpsertApplicationAsync(identity, iconExtractor.AddIcon(descriptor), cancellationToken);
        logger.LogInformation("Application discovery completed with {ApplicationCount} entries.", applications.Count);
    }

    private static void ReadRegistry(RegistryHive hive, RegistryView view, IDictionary<string, ApplicationDescriptor> applications)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null) return;
            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(subKeyName);
                var displayName = key?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                var publisher = key?.GetValue("Publisher") as string;
                var displayIcon = ParseDisplayIcon(key?.GetValue("DisplayIcon") as string);
                var descriptor = new ApplicationDescriptor
                {
                    DisplayName = displayName.Trim(),
                    ExecutableName = displayIcon is null ? string.Empty : Path.GetFileName(displayIcon),
                    ExecutablePath = displayIcon ?? key?.GetValue("InstallLocation") as string ?? string.Empty,
                    ProductName = displayName.Trim(),
                    Company = publisher?.Trim(),
                    FileVersion = key?.GetValue("DisplayVersion") as string
                };
                if (ApplicationCatalogPolicy.IsUserManageable(descriptor))
                {
                    descriptor = ApplicationCatalogPolicy.NormalizeForCatalog(descriptor);
                    applications[ApplicationIdentity.CreateKey(descriptor)] = descriptor;
                }
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    private static string? ParseDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 2 && int.TryParse(path[(comma + 1)..], out _)) path = path[..comma];
        return path.Trim('"');
    }

    private static async Task<IReadOnlyList<ApplicationDescriptor>> ReadMsixAsync(CancellationToken cancellationToken)
    {
        var script = "Get-AppxPackage -AllUsers | Select-Object Name,Publisher,PackageFamilyName,PackageFullName,InstallLocation,Version | ConvertTo-Json -Compress";
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        using var process = Process.Start(startInfo);
        if (process is null) return [];
        var json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        return entries.Select(ToDescriptor).OfType<ApplicationDescriptor>().ToList();
    }

    /// <summary>
    /// Turns one <c>Get-AppxPackage</c> row into a catalog entry, and drops the ones the package
    /// itself says are not applications. Windows installs a shelf of packaged components beside
    /// the applications a child opens - the handwriting dictionary, the widget feed, the OneDrive
    /// sync engine - and none of them is separable from Photos or Notepad by name, publisher, or
    /// install path. The manifest separates them: a component sets <c>AppListEntry="none"</c> or
    /// declares no application at all, which is Windows' own statement that it has no Start entry.
    ///
    /// A manifest that cannot be read keeps its package, named as before. Losing a real
    /// application because one file would not open is the worse failure of the two.
    /// </summary>
    private static ApplicationDescriptor? ToDescriptor(JsonElement entry)
    {
        var packageName = entry.GetProperty("Name").GetString() ?? "Store application";
        var installLocation = entry.GetProperty("InstallLocation").GetString() ?? string.Empty;
        var manifest = MsixPackageReader.Read(
            packageName,
            entry.GetProperty("PackageFullName").GetString() ?? string.Empty,
            installLocation);
        if (manifest is { HasApplicationEntry: false }) return null;
        var displayName = manifest?.DisplayName?.Trim();
        return new ApplicationDescriptor
        {
            DisplayName = string.IsNullOrEmpty(displayName) ? packageName : displayName,
            ProductName = packageName,
            Company = entry.GetProperty("Publisher").GetString(),
            PackageFamilyName = entry.GetProperty("PackageFamilyName").GetString(),
            ExecutablePath = installLocation,
            FileVersion = entry.GetProperty("Version").GetString(),
            ExecutableName = string.Empty
        };
    }
}
