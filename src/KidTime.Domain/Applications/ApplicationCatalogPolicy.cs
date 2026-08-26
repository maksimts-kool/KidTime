namespace KidTime.Domain.Applications;

public static class ApplicationCatalogPolicy
{
    private static readonly HashSet<string> InfrastructureExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "backgroundtaskhost.exe", "conhost.exe", "crashhelper.exe", "crashreporter.exe", "default-browser-agent.exe", "dllhost.exe",
        "elevation_service.exe", "identity_helper.exe", "logonui.exe", "maintenanceservice.exe", "msedgewebview2.exe", "rundll32.exe",
        "helper.exe", "pingsender.exe", "runtimebroker.exe", "searchindexer.exe", "searchprotocolhost.exe", "services.exe", "setup.exe",
        "sftp-server.exe", "sihost.exe", "smartscreen.exe", "sshd.exe", "svchost.exe",
        "taskhostw.exe", "wininit.exe", "winlogon.exe", "wmiprvse.exe", "werfault.exe", "wermgr.exe",
        // Steam runs a small fleet of satellites beside steam.exe. Only steamwebhelper.exe draws
        // a window a child actually uses, and that one is resolved onto steam.exe below; the rest
        // are background machinery and must never earn a card of their own.
        "gameoverlayui.exe", "steamservice.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe",
        "steam_monitor.exe", "streaming_client.exe"
    };

    private static readonly string[] PackageInfrastructureTokens =
    [
        "microsoft.vclibs", "microsoft.net.native", "microsoft.ui.xaml", "microsoft.windowsappruntime",
        "microsoft.services.store.engagement", "microsoftwindows.client.", "microsoftwindows.crossdevice",
        "microsoft.desktopappinstaller", "microsoft.applicationcompatibilityenhancements", "microsoft.storepurchaseapp",
        "microsoft.microsoftedge.stable", "microsoft.edge.gameassist", "microsoft.sechealthui", "microsoft.xbox.tcui",
        "microsoft.xboxidentityprovider", "microsoft.xboxspeechtotextoverlay", "mdodrmcpfilterpackage",
        "microsoft.directxruntime", "microsoft.gamingservices", "microsoft.gameinput", "gameinput",
        "languageexperiencepack", "extension", "codec"
    ];

    private static readonly string[] ProductInfrastructureTokens =
    [
        "redistributable", "runtime", " update", "updater", "bootstrapper", "servicing stack", "driver package",
        "gameinput", "maintenance service", "webview2", "vmware tools", "vmware svga", "graphics driver"
    ];

    /// <summary>
    /// Satellite processes that are, to a parent, simply the application they belong to.
    ///
    /// Steam is why this exists. Its window is drawn by steamwebhelper.exe out of a nested CEF
    /// directory, so without this the child's Steam time would either land on a "helper" nobody
    /// recognizes or - once helpers are filtered out - on nothing at all. Renaming the satellite
    /// onto its principal puts both observations on the one identity the parent already edits,
    /// which is also what makes blocking Steam close the window the child is looking at.
    ///
    /// This is only for satellites that carry the application's own interactive window. A crash
    /// handler or an updater is excluded outright instead: it running is not the child using the
    /// application.
    /// </summary>
    private static readonly Dictionary<string, PrincipalApplication> SatelliteExecutables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["steamwebhelper.exe"] = new("steam.exe", "Steam", "Steam")
        };

    private sealed record PrincipalApplication(string ExecutableName, string ProductName, string RootDirectoryName);

    /// <summary>
    /// Rewrites a satellite process onto the application it belongs to; everything else is
    /// returned untouched. Both <see cref="IsUserManageable"/> and <see cref="NormalizeForCatalog"/>
    /// start here, so discovery, enforcement, and the parent's catalog all agree on which
    /// application a process is.
    /// </summary>
    public static ApplicationDescriptor ResolvePrincipal(ApplicationDescriptor application)
    {
        if (!string.IsNullOrWhiteSpace(application.PackageFamilyName)) return application;
        if (!SatelliteExecutables.TryGetValue(application.ExecutableName.Trim(), out var principal))
            return application;
        // A signed application is keyed on publisher, product, and executable name, so carrying
        // the satellite's own publisher across is what lands it on the principal's key.
        return new ApplicationDescriptor
        {
            DisplayName = principal.ProductName,
            ExecutableName = principal.ExecutableName,
            ExecutablePath = ResolvePrincipalPath(application.ExecutablePath, principal),
            ProductName = principal.ProductName,
            OriginalFilename = principal.ExecutableName,
            Company = application.Company,
            SignaturePublisher = application.SignaturePublisher,
            FileVersion = application.FileVersion,
            PackageFamilyName = application.PackageFamilyName,
            // The hash described the satellite on disk, and claiming it for the principal would
            // be a lie the diagnosis pages would repeat.
            Sha256 = null,
            IconPngBase64 = application.IconPngBase64
        };
    }

    public static bool IsUserManageable(ApplicationDescriptor application)
    {
        application = ResolvePrincipal(application);
        var displayName = application.DisplayName.Trim();
        var productName = application.ProductName?.Trim() ?? string.Empty;
        var executableName = application.ExecutableName.Trim();
        var executablePath = application.ExecutablePath.Replace('/', '\\').Trim();
        var originalFilename = application.OriginalFilename?.Trim() ?? string.Empty;
        var packageFamily = application.PackageFamilyName?.Trim() ?? string.Empty;

        // Not "KidTime." - the download a parent runs is KidTimeSetup.exe, and a browser renames
        // a repeat download to KidTimeSetup(2).exe.
        if (displayName.StartsWith("KidTime", StringComparison.OrdinalIgnoreCase)
            || productName.StartsWith("KidTime", StringComparison.OrdinalIgnoreCase)
            || executableName.StartsWith("KidTime", StringComparison.OrdinalIgnoreCase))
            return false;

        if (displayName.Equals("Microsoft® Windows® Operating System", StringComparison.OrdinalIgnoreCase)
            || productName.Equals("Microsoft® Windows® Operating System", StringComparison.OrdinalIgnoreCase)
            || displayName.Equals("Microsoft Windows Operating System", StringComparison.OrdinalIgnoreCase)
            || productName.Equals("Microsoft Windows Operating System", StringComparison.OrdinalIgnoreCase))
            return false;

        // An IExpress self-extracting package keeps wextract.exe's version resource, which is why
        // KidTime's own setup announced itself on the parent's panel as "Internet Explorer".
        // Anything built that way is an installer, whatever its version resource claims.
        if (originalFilename.Equals("wextract.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        if (executablePath.Contains(@":\Windows\", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains(@"\ProgramData\Package Cache\", StringComparison.OrdinalIgnoreCase))
            return false;

        // A packaged application observed without its family name - the process was gone before it
        // could be read, or a stored catalog row predates the reading of it - still carries the
        // identity in its install directory. Reading it back here filters on what the application
        // is rather than on how completely it happened to be observed.
        var packageIdentity = packageFamily.Length > 0
            ? packageFamily
            : ReadWindowsAppsPackageFamily(executablePath);
        if (packageIdentity.Length > 0)
        {
            var packageText = $"{displayName}|{productName}|{packageIdentity}";
            if (PackageInfrastructureTokens.Any(token =>
                    packageText.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return false;
            // A reported family name identifies the application outright. A family only inferred
            // from the install path does not, so those still face the executable checks below and
            // a packaged updater is filtered on its name as any other updater would be.
            if (packageFamily.Length > 0) return true;
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
        application = ResolvePrincipal(application);
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

    public static ApplicationDescriptor NormalizeForCatalog(ApplicationDescriptor application)
    {
        application = ResolvePrincipal(application);
        return new ApplicationDescriptor
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
    }

    private static bool IsFirefox(ApplicationDescriptor application) =>
        application.ExecutableName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
        && ($"{application.DisplayName}|{application.ProductName}|{application.Company}|{application.SignaturePublisher}"
            .Contains("Mozilla", StringComparison.OrdinalIgnoreCase)
            || application.DisplayName.Equals("Firefox", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// steamwebhelper.exe lives several directories below the install root, so the principal's
    /// real path is only reconstructed when that root is actually on the path. Otherwise the
    /// satellite's own path stays, where it is a diagnostic detail rather than part of the key.
    /// </summary>
    private static string ResolvePrincipalPath(string satellitePath, PrincipalApplication principal)
    {
        var path = satellitePath.Replace('/', '\\');
        var marker = $@"\{principal.RootDirectoryName}\";
        var index = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? satellitePath : $"{path[..(index + marker.Length)]}{principal.ExecutableName}";
    }

    /// <summary>
    /// Reads the package family out of a <c>WindowsApps\Name_Version_Arch__PublisherId</c>
    /// directory, the form Windows installs packaged applications under. Anything else - including
    /// the unversioned directories some in-box applications use - yields nothing, so the ordinary
    /// executable checks still decide.
    /// </summary>
    private static string ReadWindowsAppsPackageFamily(string executablePath)
    {
        const string marker = @"\WindowsApps\";
        var index = executablePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return string.Empty;
        var remainder = executablePath[(index + marker.Length)..];
        var separator = remainder.IndexOf('\\');
        var folder = separator >= 0 ? remainder[..separator] : remainder;
        var publisherIndex = folder.IndexOf("__", StringComparison.Ordinal);
        if (publisherIndex <= 0) return string.Empty;
        var name = folder[..publisherIndex];
        var publisherId = folder[(publisherIndex + 2)..];
        var versionIndex = name.IndexOf('_');
        return versionIndex <= 0 || publisherId.Length == 0
            ? string.Empty
            : $"{name[..versionIndex]}_{publisherId}";
    }

    private static bool IsNonInteractiveExecutableName(string executableName)
    {
        var stem = StripCopyMarker(Path.GetFileNameWithoutExtension(executableName).Trim().ToLowerInvariant());
        if (stem is "install" or "installer" or "setup" or "uninstall" or "uninstaller" or "update" or "updater")
            return true;
        return HasRoleSuffix(stem, "helper")
               || HasRoleSuffix(stem, "installer")
               || HasRoleSuffix(stem, "setup")
               || HasRoleSuffix(stem, "updater")
               // A crash reporter starting is the opposite of a child using an application, yet it
               // is exactly what the foreground sample catches at that moment. Steam and Unity each
               // ship one, and neither belongs on the parent's panel.
               || ContainsRole(stem, "crashreporter")
               || ContainsRole(stem, "crashhandler")
               || ContainsRole(stem, "crashpad")
               || ContainsRole(stem, "errorreporter");
    }

    /// <summary>
    /// Drops the "(2)" a browser appends when the same file is downloaded twice, so a repeat
    /// download is still recognized as the installer it is.
    /// </summary>
    private static string StripCopyMarker(string stem)
    {
        var trimmed = stem.TrimEnd();
        if (!trimmed.EndsWith(')')) return stem;
        var open = trimmed.LastIndexOf('(');
        if (open <= 0) return stem;
        var inner = trimmed[(open + 1)..^1];
        return inner.Length > 0 && inner.All(char.IsAsciiDigit) ? trimmed[..open].TrimEnd() : stem;
    }

    /// <summary>
    /// A role word at the end of an executable name, with or without a separator before it:
    /// "steamwebhelper" is one word to Steam and a helper to everybody else.
    /// </summary>
    private static bool HasRoleSuffix(string stem, string role) => stem.EndsWith(role, StringComparison.Ordinal);

    /// <summary>The same idea for names carrying a trailing bitness, such as UnityCrashHandler64.</summary>
    private static bool ContainsRole(string stem, string role) => stem.Contains(role, StringComparison.Ordinal);
}
