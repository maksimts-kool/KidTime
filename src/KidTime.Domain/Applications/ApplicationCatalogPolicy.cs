namespace KidTime.Domain.Applications;

public static class ApplicationCatalogPolicy
{
    private static readonly HashSet<string> InfrastructureExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "backgroundtaskhost.exe", "conhost.exe", "crashhelper.exe", "crashreporter.exe", "default-browser-agent.exe", "dllhost.exe",
        "elevation_service.exe", "identity_helper.exe", "logonui.exe", "maintenanceservice.exe", "msedgewebview2.exe", "rundll32.exe",
        "helper.exe", "pingsender.exe", "runtimebroker.exe", "searchindexer.exe", "searchprotocolhost.exe", "services.exe", "setup.exe",
        "sftp-server.exe", "sihost.exe", "smartscreen.exe", "sshd.exe", "svchost.exe",
        "taskhostw.exe", "wininit.exe", "winlogon.exe", "wmiprvse.exe"
    };

    private static readonly string[] PackageInfrastructureTokens =
    [
        "microsoft.vclibs", "microsoft.net.native", "microsoft.ui.xaml", "microsoft.windowsappruntime",
        "microsoft.services.store.engagement", "microsoftwindows.client.", "microsoftwindows.crossdevice",
        "microsoft.desktopappinstaller", "microsoft.applicationcompatibilityenhancements", "microsoft.storepurchaseapp",
        "microsoft.microsoftedge.stable", "microsoft.sechealthui", "microsoft.xbox.tcui",
        "microsoft.xboxidentityprovider", "microsoft.xboxspeechtotextoverlay", "mdodrmcpfilterpackage",
        "microsoft.directxruntime", "microsoft.gamingservices", "microsoft.gameinput", "gameinput",
        "languageexperiencepack", "extension", "codec"
    ];

    private static readonly string[] ProductInfrastructureTokens =
    [
        "redistributable", "runtime", " update", "updater", "bootstrapper", "servicing stack", "driver package",
        "gameinput", "maintenance service", "webview2", "vmware tools", "vmware svga", "graphics driver"
    ];

    public static bool IsUserManageable(ApplicationDescriptor application)
    {
        var displayName = application.DisplayName.Trim();
        var productName = application.ProductName?.Trim() ?? string.Empty;
        var executableName = application.ExecutableName.Trim();
        var executablePath = application.ExecutablePath.Replace('/', '\\').Trim();
        var packageFamily = application.PackageFamilyName?.Trim() ?? string.Empty;

        if (displayName.StartsWith("KidTime", StringComparison.OrdinalIgnoreCase)
            || productName.StartsWith("KidTime", StringComparison.OrdinalIgnoreCase)
            || executableName.StartsWith("KidTime.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (displayName.Equals("Microsoft® Windows® Operating System", StringComparison.OrdinalIgnoreCase)
            || productName.Equals("Microsoft® Windows® Operating System", StringComparison.OrdinalIgnoreCase)
            || displayName.Equals("Microsoft Windows Operating System", StringComparison.OrdinalIgnoreCase)
            || productName.Equals("Microsoft Windows Operating System", StringComparison.OrdinalIgnoreCase))
            return false;

        if (executablePath.Contains(@":\Windows\", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains(@"\ProgramData\Package Cache\", StringComparison.OrdinalIgnoreCase))
            return false;

        if (packageFamily.Length > 0)
        {
            var packageText = $"{displayName}|{productName}|{packageFamily}";
            return !PackageInfrastructureTokens.Any(token =>
                packageText.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        if (!executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || InfrastructureExecutables.Contains(executableName)
            || IsNonInteractiveExecutableName(executableName))
            return false;

        var productText = $"{displayName}|{productName}";
        return !ProductInfrastructureTokens.Any(token =>
            productText.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetFriendlyDisplayName(ApplicationDescriptor application)
    {
        if (application.ExecutableName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
            && ($"{application.Company}|{application.SignaturePublisher}".Contains("Mozilla", StringComparison.OrdinalIgnoreCase)))
            return "Firefox";
        var package = application.PackageFamilyName ?? string.Empty;
        if (package.StartsWith("Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase)
            || package.StartsWith("Microsoft.XboxApp_", StringComparison.OrdinalIgnoreCase)) return "Xbox";
        if (package.StartsWith("Microsoft.XboxGamingOverlay_", StringComparison.OrdinalIgnoreCase)) return "Xbox Game Bar";
        if (package.StartsWith("Microsoft.WindowsNotepad_", StringComparison.OrdinalIgnoreCase)) return "Notepad";
        if (package.StartsWith("Microsoft.WindowsCalculator_", StringComparison.OrdinalIgnoreCase)) return "Calculator";
        if (package.StartsWith("Microsoft.Windows.Photos_", StringComparison.OrdinalIgnoreCase)) return "Photos";
        if (package.StartsWith("Microsoft.ScreenSketch_", StringComparison.OrdinalIgnoreCase)) return "Snipping Tool";
        if (package.StartsWith("Microsoft.WindowsTerminal_", StringComparison.OrdinalIgnoreCase)) return "Terminal";
        if (package.StartsWith("Microsoft.YourPhone_", StringComparison.OrdinalIgnoreCase)) return "Phone Link";
        if (package.StartsWith("Microsoft.WindowsStore_", StringComparison.OrdinalIgnoreCase)) return "Microsoft Store";
        return application.DisplayName.Trim();
    }

    public static ApplicationDescriptor NormalizeForCatalog(ApplicationDescriptor application) => new()
    {
        DisplayName = GetFriendlyDisplayName(application),
        ExecutableName = application.ExecutableName,
        ExecutablePath = application.ExecutablePath,
        ProductName = application.ExecutableName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
                      && ($"{application.Company}|{application.SignaturePublisher}".Contains("Mozilla", StringComparison.OrdinalIgnoreCase))
            ? "Firefox"
            : application.ProductName,
        OriginalFilename = application.OriginalFilename,
        Company = IsFirefox(application) ? "Mozilla Corporation" : application.Company,
        SignaturePublisher = IsFirefox(application) ? "Mozilla Corporation" : application.SignaturePublisher,
        FileVersion = application.FileVersion,
        PackageFamilyName = application.PackageFamilyName,
        Sha256 = application.Sha256,
        IconPngBase64 = application.IconPngBase64
    };

    private static bool IsFirefox(ApplicationDescriptor application) =>
        application.ExecutableName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
        && ($"{application.DisplayName}|{application.ProductName}|{application.Company}|{application.SignaturePublisher}"
            .Contains("Mozilla", StringComparison.OrdinalIgnoreCase)
            || application.DisplayName.Equals("Firefox", StringComparison.OrdinalIgnoreCase));

    private static bool IsNonInteractiveExecutableName(string executableName)
    {
        var stem = Path.GetFileNameWithoutExtension(executableName).Trim().ToLowerInvariant();
        if (stem is "install" or "installer" or "setup" or "uninstall" or "uninstaller" or "update" or "updater")
            return true;
        return HasRoleSuffix(stem, "helper")
               || HasRoleSuffix(stem, "installer")
               || HasRoleSuffix(stem, "setup")
               || HasRoleSuffix(stem, "updater")
               || stem.Contains("crashreporter", StringComparison.Ordinal)
               || stem.Contains("crashpad", StringComparison.Ordinal);
    }

    private static bool HasRoleSuffix(string stem, string role) =>
        stem.Equals(role, StringComparison.Ordinal)
        || stem.EndsWith($"_{role}", StringComparison.Ordinal)
        || stem.EndsWith($"-{role}", StringComparison.Ordinal)
        || stem.EndsWith($".{role}", StringComparison.Ordinal);
}
