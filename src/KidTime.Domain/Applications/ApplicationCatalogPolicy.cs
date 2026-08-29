using System.Text;

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
        // Vendor background machinery that ships beside an application a child does use. Each of
        // these was observed on a real controlled PC announcing itself as the product it belongs
        // to - "NVIDIA App", "Microsoft Office LTSC Professional Plus 2024" - which is exactly the
        // card a parent would mistake for the application itself.
        "nvcontainer.exe", "nvidia overlay.exe", "nvidia share.exe", "nvidia web helper.exe",
        "officeclicktorun.exe", "officec2rclient.exe",
        // Steam runs a small fleet of satellites beside steam.exe. Only steamwebhelper.exe draws
        // a window a child actually uses, and that one is resolved onto steam.exe below; the rest
        // are background machinery and must never earn a card of their own.
        "gameoverlayui.exe", "steamservice.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe",
        "steam_monitor.exe", "streaming_client.exe",
        // Command-line tools that ship inside an application and are never opened by a person.
        // adb.exe arrives with every Android emulator - BlueStacks starts it on its own - and
        // ffmpeg.exe is bundled by half the recorders and launchers on a gaming PC. Both reached a
        // real parent's panel as cards named after the bare executable, because neither carries any
        // version metadata to name them with.
        "adb.exe", "ffmpeg.exe", "ffprobe.exe",
        // Riot's anti-cheat. The game it protects keeps its own card; the tray icon and the driver
        // service are not things a child opens, and blocking them would break the game silently.
        "vgc.exe", "vgtray.exe",
        // Valve's capability probes. Steam runs them at start-up to ask the GPU what it supports,
        // and each one flashes through the foreground long enough to be sampled.
        "steamsysinfo.exe", "vulkandriverquery.exe", "vulkandriverquery64.exe",
        "gldriverquery.exe", "gldriverquery64.exe"
    };

    private static readonly string[] PackageInfrastructureTokens =
    [
        "microsoft.vclibs", "microsoft.net.native", "microsoft.ui.xaml", "microsoft.windowsappruntime",
        "microsoft.services.store.engagement", "microsoftwindows.client.", "microsoftwindows.crossdevice",
        "microsoft.desktopappinstaller", "microsoft.applicationcompatibilityenhancements", "microsoft.storepurchaseapp",
        "microsoft.microsoftedge.stable", "microsoft.edge.gameassist", "microsoft.sechealthui", "microsoft.xbox.tcui",
        "microsoft.xboxidentityprovider", "microsoft.xboxspeechtotextoverlay", "mdodrmcpfilterpackage",
        "microsoft.directxruntime", "microsoft.gamingservices", "microsoft.gameinput", "gameinput",
        "languageexperiencepack", "extension", "codec",
        // Windows ships a shelf of packages that exist only to serve the shell: handwriting
        // dictionaries, the search box, the widget feed, the OneDrive sync engine. They are
        // packaged applications by every mechanical test and none of them is a thing a child
        // opens, so a parent should never be offered a rule for one.
        "winappruntime", "microsoft.widgetsplatformruntime", "microsoft.startexperiencesapp",
        "microsoft.ink.handwriting", "microsoft.gethelp", "microsoft.onedrivesync",
        "microsoft.bingsearch", "microsoft.microsoftofficehub",
        // Copilot is real, but this package is only the stub that registers it; the window the
        // child actually looks at belongs to mscopilot.exe, which keeps its own card.
        "microsoft.copilot"
    ];

    private static readonly string[] ProductInfrastructureTokens =
    [
        "redistributable", "runtime", " update", "updater", "bootstrapper", "servicing stack", "driver package",
        "gameinput", "maintenance service", "webview2", "vmware tools", "vmware svga", "graphics driver",
        // An anti-cheat starting is the game starting, but the card it would earn is not one a
        // parent can act on: blocking it breaks the game without saying so.
        "anti-cheat", "anticheat", "battleye",
        // A hypervisor is the machinery an emulator runs on, never the window a child looks at:
        // BlueStacks' BstkSVC.exe announces itself as "Bluestack Hypervisor" beside the
        // HD-Player.exe card that is the emulator itself.
        "hypervisor", "telemetry", "crash handler", "crash reporter",
        // An installer is not a thing to put a screen-time rule on, whatever it installs. The
        // packaged "LG Monitor App Installer" is the shape this catches that a file name cannot.
        "installer"
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
    /// Executables that carry a background role word but are the application itself. Riot names
    /// the window a child logs into and launches games from RiotClientServices.exe, so the rule
    /// that a name ending in "service" is machinery gets this one wrong. The rule is still right
    /// - it retires BlueStacksServices.exe, WidgetService.exe and steamservice.exe - which is why
    /// the exception is a named list rather than a softer rule.
    /// </summary>
    private static readonly HashSet<string> InteractiveDespiteRoleName = new(StringComparer.OrdinalIgnoreCase)
    {
        "riotclientservices.exe", "riotclientux.exe"
    };

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
            || executablePath.Contains(@"\ProgramData\Package Cache\", StringComparison.OrdinalIgnoreCase)
            // NVIDIA's backend directory, where its telemetry and cache processes live. They all
            // report the product name of the component - "NVIDIA GeForce Experience Application
            // Ontology" - and NVIDIA renames them between driver versions: OAWrapper.exe became
            // NvOAWrapperCache.exe on one controlled PC inside a week. The directory is the stable
            // fact, and nothing a child opens is in it.
            || executablePath.Contains(@"\NvBackend\", StringComparison.OrdinalIgnoreCase))
            return false;

        // A runtime host or a background satellite is what it is whether or not the process
        // reported a package family, so this runs ahead of the packaged short-circuit below.
        // msedgewebview2.exe hosting WhatsApp reports WhatsApp's family and would otherwise be
        // taken at its word - and "Microsoft Edge WebView2" is not an application a parent
        // recognizes, let alone one blocking WhatsApp through would be honest about.
        if (executableName.Length > 0
            && !InteractiveDespiteRoleName.Contains(executableName)
            && (IsInfrastructureExecutable(executableName)
                || IsNonInteractiveExecutableName(executableName)))
            return false;

        // A packaged application observed without its family name - the process was gone before it
        // could be read, or a stored catalog row predates the reading of it - still carries the
        // identity in its install directory. Reading it back here filters on what the application
        // is rather than on how completely it happened to be observed.
        var packageIdentity = packageFamily.Length > 0
            ? packageFamily
            : ReadWindowsAppsPackageFamily(executablePath);
        var productText = $"{displayName}|{productName}";
        // What a package calls itself is checked before its family is taken as proof that it is an
        // application. A reported family says "this is a real packaged thing", not "a person opens
        // this": LGElectronics.LGMonitorApp announces itself as "LG Monitor App Installer", and an
        // installer is not something to put a screen-time rule on however it was packaged.
        if (ProductInfrastructureTokens.Any(token =>
                productText.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return false;
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

        return executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The name a Windows package answers to, for the packages whose own identity is not it.
    /// Keyed on the package name - the part of the family before the publisher id.
    ///
    /// This is a shortlist, not a catalog. An agent that can read the package manifest sends the
    /// real display name and never reaches here; these are the in-box applications whose identity
    /// name is actively misleading, where <see cref="HumanizePackageName"/> would confidently
    /// produce a wrong answer ("Zune Music" for Media Player, "Windows Alarms" for Clock) rather
    /// than merely a plain one.
    /// </summary>
    private static readonly Dictionary<string, string> PackageDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.XboxApp"] = "Xbox",
        ["Microsoft.XboxGamingOverlay"] = "Xbox Game Bar",
        ["Microsoft.WindowsNotepad"] = "Notepad",
        ["Microsoft.WindowsCalculator"] = "Calculator",
        ["Microsoft.Windows.Photos"] = "Photos",
        ["Microsoft.ScreenSketch"] = "Snipping Tool",
        ["Microsoft.WindowsTerminal"] = "Terminal",
        ["Microsoft.YourPhone"] = "Phone Link",
        ["Microsoft.WindowsStore"] = "Microsoft Store",
        ["Microsoft.ZuneMusic"] = "Media Player",
        ["Microsoft.ZuneVideo"] = "Movies & TV",
        ["Microsoft.WindowsAlarms"] = "Clock",
        ["Microsoft.WindowsCamera"] = "Camera",
        ["Microsoft.WindowsSoundRecorder"] = "Sound Recorder",
        ["Microsoft.MicrosoftStickyNotes"] = "Sticky Notes",
        ["Microsoft.MicrosoftSolitaireCollection"] = "Solitaire Collection",
        ["Microsoft.WindowsFeedbackHub"] = "Feedback Hub",
        ["Microsoft.OutlookForWindows"] = "Outlook",
        ["Microsoft.Todos"] = "Microsoft To Do",
        ["Microsoft.BingNews"] = "News",
        ["Microsoft.BingWeather"] = "Weather",
        ["Microsoft.Windows.DevHome"] = "Dev Home",
        ["Microsoft.PowerAutomateDesktop"] = "Power Automate",
        ["MicrosoftCorporationII.QuickAssist"] = "Quick Assist",
        ["MSTeams"] = "Microsoft Teams",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
        ["38833FF26BA1D.UnigramPreview"] = "Unigram",
        ["5319275A.WhatsAppDesktop"] = "WhatsApp",
        ["TelegramMessengerLLP.TelegramDesktop"] = "Telegram",
        ["SpotifyAB.SpotifyMusic"] = "Spotify"
    };

    public static string GetFriendlyDisplayName(ApplicationDescriptor application)
    {
        application = ResolvePrincipal(application);
        if (application.ExecutableName.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase)
            && ($"{application.Company}|{application.SignaturePublisher}".Contains("Mozilla", StringComparison.OrdinalIgnoreCase)))
            return "Firefox";
        var displayName = application.DisplayName.Trim();
        var packageName = ReadPackageName(application);
        if (packageName.Length > 0 && PackageDisplayNames.TryGetValue(packageName, out var known)) return known;
        return IsPackageIdentity(displayName, packageName) ? HumanizePackageName(displayName) : displayName;
    }

    /// <summary>
    /// The package name a descriptor belongs to - the family without its publisher id - taken from
    /// the reported family, or read back out of a WindowsApps install path when the process did
    /// not report one.
    /// </summary>
    private static string ReadPackageName(ApplicationDescriptor application)
    {
        var family = application.PackageFamilyName?.Trim() ?? string.Empty;
        if (family.Length == 0)
            family = ReadWindowsAppsPackageFamily(application.ExecutablePath.Replace('/', '\\').Trim());
        var separator = family.LastIndexOf('_');
        return separator > 0 ? family[..separator] : family;
    }

    /// <summary>
    /// Whether a display name is really a package identity wearing the label. An agent that could
    /// not read the manifest sends the identity itself, which is how "38833FF26BA1D.UnigramPreview"
    /// and "Microsoft.BingNews" came to sit on a parent's applications page. Anything a person
    /// would have written - a name with a space in it, or one that is not this package's own
    /// identity - is left exactly as it is.
    /// </summary>
    private static bool IsPackageIdentity(string displayName, string packageName)
    {
        if (packageName.Length == 0 || displayName.Length == 0) return false;
        if (displayName.Contains(' ')) return false;
        return displayName.Equals(packageName, StringComparison.OrdinalIgnoreCase)
               || packageName.EndsWith($".{displayName}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns a package identity into something readable: the leaf of the dotted name, split where
    /// its words run together. "Microsoft.BingNews" reads "Bing News" and
    /// "NVIDIACorp.NVIDIAControlPanel" reads "NVIDIA Control Panel" - a run of capitals stays one
    /// word until the last of them starts the next. It is the plain name rather than the marketed
    /// one, which is the point: a parent can tell what it is.
    /// </summary>
    private static string HumanizePackageName(string packageName)
    {
        var leaf = packageName[(packageName.LastIndexOf('.') + 1)..];
        if (leaf.Length == 0) return packageName;
        var text = new StringBuilder(leaf.Length + 8);
        for (var index = 0; index < leaf.Length; index++)
        {
            var current = leaf[index];
            if (index > 0 && char.IsUpper(current))
            {
                var previous = leaf[index - 1];
                var startsWord = !char.IsUpper(previous)
                                 || (index + 1 < leaf.Length && char.IsLower(leaf[index + 1]));
                if (startsWord && text.Length > 0 && text[^1] != ' ') text.Append(' ');
            }
            text.Append(current);
        }
        return text.ToString();
    }

    /// <summary>
    /// Microsoft-published applications that are entertainment rather than Windows tooling, and so
    /// stay on the parent's page whatever the "show Microsoft apps" switch says. Minecraft is why
    /// this list exists: its package family is Microsoft.MinecraftUWP_8wekyb3d8bbwe, so a switch
    /// that filtered on the publisher alone would hide the one application a parent most wants a
    /// rule on. Keyed on the package name, the part of the family before the publisher id.
    /// </summary>
    private static readonly HashSet<string> MicrosoftEntertainmentPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.MinecraftUWP", "Microsoft.MinecraftWindowsBeta", "Microsoft.MinecraftEducationEdition",
        "Microsoft.MicrosoftSolitaireCollection", "Microsoft.GamingApp", "Microsoft.XboxApp",
        "Microsoft.MicrosoftMahjong", "Microsoft.MicrosoftJigsaw", "Microsoft.MicrosoftSudoku",
        "Microsoft.MicrosoftMinesweeper", "Microsoft.MicrosoftUltimateWordGames", "Microsoft.MicrosoftTreasureHunt"
    };

    /// <summary>
    /// Whether an application is one Microsoft ships with Windows rather than one somebody chose to
    /// install. A real controlled PC carries three dozen of them - Clock, Weather, Feedback Hub,
    /// Quick Assist - and listing them all by default buries Steam, Roblox and Discord, so the
    /// parent's page hides them behind a switch.
    ///
    /// This is a presentation filter and nothing else. A rule on a hidden application is still in
    /// the snapshot, still evaluated, and still enforced; hiding one never relaxes it.
    /// </summary>
    public static bool IsMicrosoftPublished(ApplicationDescriptor application)
    {
        application = ResolvePrincipal(application);
        if (MicrosoftEntertainmentPackages.Contains(ReadPackageName(application))) return false;
        // 8wekyb3d8bbwe is Microsoft's own publisher id and the tail of every in-box package
        // family, which identifies a packaged application even when nothing else was readable.
        if ((application.PackageFamilyName ?? string.Empty)
            .EndsWith("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase))
            return true;
        return $"{application.SignaturePublisher}|{application.Company}"
            .Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Whether a name is one of the known background executables, with a trailing bitness marker
    /// discounted. Windows ships both halves of most of these - gameoverlayui.exe on one PC is
    /// gameoverlayui64.exe on the next - and listing every pair separately is how the 64-bit one
    /// gets forgotten and earns a card.
    /// </summary>
    private static bool IsInfrastructureExecutable(string executableName)
    {
        if (InfrastructureExecutables.Contains(executableName)) return true;
        var stem = Path.GetFileNameWithoutExtension(executableName).Trim();
        var narrowed = StripBitnessMarker(stem.ToLowerInvariant());
        return narrowed.Length != stem.Length && InfrastructureExecutables.Contains($"{narrowed}.exe");
    }

    private static bool IsNonInteractiveExecutableName(string executableName)
    {
        var stem = StripBitnessMarker(
            StripCopyMarker(Path.GetFileNameWithoutExtension(executableName).Trim().ToLowerInvariant()));
        if (stem is "install" or "installer" or "setup" or "uninstall" or "uninstaller" or "update" or "updater")
            return true;
        // A file that carries its own version number is a download, not an installed application:
        // ProtonVPN_v5.1.7_x64.exe is the installer a child ran once, while the application it
        // installed is plain ProtonVPN.exe and keeps its card.
        if (HasEmbeddedVersion(stem)) return true;
        // A role word can lead the name as readily as it can end it: Rockstar's
        // uninstallRGSCRedistributable.exe reached a real parent's panel as "Rockstar Games SDK",
        // and unins000.exe is what every Inno Setup package leaves behind.
        if (stem.StartsWith("uninstall", StringComparison.Ordinal)
            || stem.StartsWith("unins0", StringComparison.Ordinal)
            || stem.StartsWith("setup", StringComparison.Ordinal))
            return true;
        return HasRoleSuffix(stem, "helper")
               || HasRoleSuffix(stem, "installer")
               || HasRoleSuffix(stem, "setup")
               || HasRoleSuffix(stem, "updater")
               // A background agent or service is the half of a product that runs whether or not
               // the child ever opens it: lghub_agent.exe beside Logitech G HUB,
               // BlueStacksServices.exe beside BlueStacks, WidgetService.exe behind the widget
               // board. Counting one as usage would charge a child for being logged in.
               || HasRoleSuffix(stem, "agent")
               || HasRoleSuffix(stem, "service")
               || HasRoleSuffix(stem, "services")
               // The same role, abbreviated the way a vendor writes it in a file name.
               // BlueStacks' BstkSVC.exe is the hypervisor service behind HD-Player.exe, and a
               // watchdog or a daemon is machinery by definition.
               || HasRoleSuffix(stem, "svc")
               || HasRoleSuffix(stem, "daemon")
               || HasRoleSuffix(stem, "watchdog")
               // A tray icon is a vendor's background half announcing itself, not the application:
               // lghub_system_tray.exe sits beside the real lghub.exe, and vgtray.exe is Riot's
               // anti-cheat, which a parent must not be offered a rule for.
               || HasRoleSuffix(stem, "tray")
               // A proxy process hosts a window that belongs to something else. Edge names its
               // mscopilot_proxy.exe and msedge_proxy.exe that way, and both reached a parent's
               // panel as a second "Microsoft Edge".
               || HasRoleSuffix(stem, "proxy")
               // Probes an application runs to interrogate the machine. Steam starts
               // vulkandriverquery.exe, gldriverquery64.exe and steamsysinfo.exe at every launch,
               // and each is in the foreground just long enough to be sampled.
               || HasRoleSuffix(stem, "driverquery")
               || HasRoleSuffix(stem, "sysinfo")
               // Android Debug Bridge, which every emulator ships and starts on its own -
               // adb.exe beside BlueStacks, HD-Adb.exe inside it.
               || HasRoleSuffix(stem, "adb")
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
    /// Drops a trailing bitness marker so a name is judged on the role it names rather than on
    /// which build of it is installed: "nvsphelper64" is the helper "nvsphelper" is. The marker is
    /// only dropped when a real name is left behind, so a game called cs2 or Portal2 keeps its own.
    /// </summary>
    private static string StripBitnessMarker(string stem)
    {
        foreach (var marker in (string[])["x64", "x86", "64", "32"])
        {
            if (!stem.EndsWith(marker, StringComparison.Ordinal)) continue;
            var head = stem[..^marker.Length].TrimEnd('_', '-', '.', ' ');
            if (head.Length >= 3) return head;
        }
        return stem;
    }

    /// <summary>
    /// Whether a name carries a dotted version number, the shape a downloaded installer has and an
    /// installed application does not.
    /// </summary>
    private static bool HasEmbeddedVersion(string stem)
    {
        var digitsBefore = 0;
        var dots = 0;
        for (var index = 0; index < stem.Length; index++)
        {
            var current = stem[index];
            if (char.IsAsciiDigit(current))
            {
                digitsBefore++;
                continue;
            }
            if (current == '.' && digitsBefore > 0 && index + 1 < stem.Length && char.IsAsciiDigit(stem[index + 1]))
            {
                if (++dots == 2) return true;
                continue;
            }
            digitsBefore = 0;
            dots = 0;
        }
        return false;
    }

    /// <summary>
    /// A role word at the end of an executable name, with or without a separator before it:
    /// "steamwebhelper" is one word to Steam and a helper to everybody else.
    /// </summary>
    private static bool HasRoleSuffix(string stem, string role) => stem.EndsWith(role, StringComparison.Ordinal);

    /// <summary>The same idea for names carrying a trailing bitness, such as UnityCrashHandler64.</summary>
    private static bool ContainsRole(string stem, string role) => stem.Contains(role, StringComparison.Ordinal);
}
