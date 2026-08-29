using KidTime.Domain.Applications;

namespace KidTime.Domain.Tests;

public sealed class ApplicationCatalogPolicyTests
{
    [Theory]
    [InlineData("Microsoft Edge", "msedge.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData("Notepad", "Notepad.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad\Notepad.exe")]
    public void User_applications_are_manageable(string name, string executable, string path)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = executable,
            ExecutablePath = path
        };

        Assert.True(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Fact]
    public void Xbox_msix_application_is_manageable()
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Microsoft.XboxApp",
            ProductName = "Microsoft.XboxApp",
            PackageFamilyName = "Microsoft.XboxApp_8wekyb3d8bbwe",
            ExecutablePath = @"C:\Program Files\WindowsApps\Microsoft.XboxApp_1.0_x64__8wekyb3d8bbwe"
        };

        Assert.True(ApplicationCatalogPolicy.IsUserManageable(descriptor));
        Assert.Equal("Xbox", ApplicationCatalogPolicy.GetFriendlyDisplayName(descriptor));
    }

    [Theory]
    [InlineData("Microsoft Edge Update", "MicrosoftEdgeUpdate.exe")]
    [InlineData("Discord Update", "Update.exe")]
    [InlineData("Discord Updater", "discord-updater.exe")]
    [InlineData("Discord GPU Encoder Helper", "gpu_encoder_helper.exe")]
    [InlineData("Chromium Crashpad Handler", "crashpad_handler.exe")]
    [InlineData("Mozilla Maintenance Service", "maintenanceservice.exe")]
    [InlineData("Firefox", "crashreporter.exe")]
    [InlineData("Firefox", "crashhelper.exe")]
    [InlineData("Firefox", "default-browser-agent.exe")]
    [InlineData("Firefox", "helper.exe")]
    [InlineData("Firefox", "pingsender.exe")]
    [InlineData("Firefox Setup", "setup.exe")]
    [InlineData("Microsoft Edge WebView2", "msedgewebview2.exe")]
    [InlineData("Microsoft.XboxIdentityProvider", "")]
    [InlineData("Microsoft.Xbox.TCUI", "")]
    [InlineData("Microsoft.DirectXRuntime", "")]
    [InlineData("Microsoft.GamingServices", "")]
    [InlineData("Microsoft Windows GameInput", "")]
    public void Helpers_and_updaters_are_not_manageable(string name, string executable)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = executable,
            ExecutablePath = executable.Length > 0
                ? $@"C:\Program Files\Example\{executable}"
                : $@"C:\Program Files\WindowsApps\{name}_1.0_x64__8wekyb3d8bbwe",
            PackageFamilyName = executable.Length > 0 ? null : $"{name}_8wekyb3d8bbwe"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Fact]
    public void Discord_main_executable_is_manageable()
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Discord",
            ProductName = "Discord",
            ExecutableName = "Discord.exe",
            OriginalFilename = "Discord.exe",
            ExecutablePath = @"C:\Users\child\AppData\Local\Discord\app-1.0.9254\Discord.exe",
            SignaturePublisher = "Discord Inc."
        };

        Assert.True(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    [InlineData("VMware Tools", "vmtoolsd.exe")]
    [InlineData("VMware SVGA 3D", "vm3dservice.exe")]
    [InlineData("Roblox Bootstrapper", "RobloxPlayerInstaller.exe")]
    public void Drivers_services_and_bootstrappers_are_not_manageable(string name, string executable)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = executable,
            ExecutablePath = $@"C:\Program Files\Example\{executable}"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    [InlineData("Microsoft® Windows® Operating System", "taskhostw.exe", @"C:\Windows\System32\taskhostw.exe")]
    [InlineData("KidTime.SessionAgent", "KidTime.SessionAgent.exe", @"C:\Program Files\KidTime\SessionAgent\KidTime.SessionAgent.exe")]
    [InlineData("OpenSSH for Windows", "sshd.exe", @"C:\Windows\System32\OpenSSH\sshd.exe")]
    public void Infrastructure_is_not_manageable(string name, string executable, string path)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = executable,
            ExecutablePath = path
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Fact]
    public void Steam_web_helper_is_counted_as_steam()
    {
        // Steam draws its window from a CEF helper several directories below the install root,
        // so a parent who blocks "Steam" has to reach that process through the same rule.
        var helper = new ApplicationDescriptor
        {
            DisplayName = "Steam",
            ProductName = "Steam",
            ExecutableName = "steamwebhelper.exe",
            OriginalFilename = "steamwebhelper.exe",
            ExecutablePath = @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe",
            SignaturePublisher = "Valve Corp."
        };
        var steam = new ApplicationDescriptor
        {
            DisplayName = "Steam",
            ProductName = "Steam",
            ExecutableName = "steam.exe",
            OriginalFilename = "steam.exe",
            ExecutablePath = @"C:\Program Files (x86)\Steam\steam.exe",
            SignaturePublisher = "Valve Corp."
        };

        Assert.True(ApplicationCatalogPolicy.IsUserManageable(helper));
        Assert.Equal(
            ApplicationIdentity.CreateKey(ApplicationCatalogPolicy.NormalizeForCatalog(steam)),
            ApplicationIdentity.CreateKey(ApplicationCatalogPolicy.NormalizeForCatalog(helper)));
        Assert.Equal("Steam", ApplicationCatalogPolicy.GetFriendlyDisplayName(helper));
        Assert.Equal(
            @"C:\Program Files (x86)\Steam\steam.exe",
            ApplicationCatalogPolicy.NormalizeForCatalog(helper).ExecutablePath);
    }

    [Theory]
    [InlineData("Steam", "gameoverlayui.exe")]
    [InlineData("Steam", "steamerrorreporter.exe")]
    [InlineData("Steam", "steamservice.exe")]
    [InlineData("Steam", "steamcrashhandler.exe")]
    [InlineData("Rail Route", "UnityCrashHandler64.exe")]
    public void Crash_handlers_and_background_satellites_are_not_manageable(string name, string executable)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = executable,
            OriginalFilename = executable,
            ExecutablePath = $@"C:\Program Files (x86)\{name}\{executable}",
            SignaturePublisher = "Valve Corp."
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    [InlineData("KidTimeSetup.exe", "KidTimeSetup.exe")]
    [InlineData("KidTimeSetup(8).exe", "wextract.exe")]
    [InlineData("VendorSetup (2).exe", "vendor.exe")]
    public void Downloaded_installers_are_not_manageable(string executable, string originalFilename)
    {
        // An IExpress package keeps wextract.exe's version resource, which is why KidTime's own
        // setup arrived on the parent's panel calling itself Internet Explorer.
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Internet Explorer",
            ProductName = "Internet Explorer",
            ExecutableName = executable,
            OriginalFilename = originalFilename,
            ExecutablePath = $@"C:\Users\testChild\Downloads\{executable}",
            Company = "Microsoft Corporation"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Fact]
    public void Packaged_windows_components_are_not_manageable_without_a_reported_family()
    {
        // The family name could not be read from this process, but the install directory still
        // says exactly which package it is.
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Game Assist",
            ProductName = "Microsoft Edge",
            ExecutableName = "msedge.exe",
            ExecutablePath = @"C:\Program Files\WindowsApps\Microsoft.Edge.GameAssist_1.0.4019.0_x64__8wekyb3d8bbwe\msedge.exe"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    // Every one of these reached a real parent's applications page, wearing the name of the
    // product it belongs to rather than its own.
    [InlineData("NVIDIA App", "NVIDIA Overlay.exe")]
    [InlineData("nvcontainer", "nvcontainer.exe")]
    [InlineData("Microsoft Office LTSC Professional Plus 2024 - en-us", "OfficeClickToRun.exe")]
    [InlineData("BlueStacks Services", "BlueStacksServices.exe")]
    [InlineData("LGHUB Agent", "lghub_agent.exe")]
    [InlineData("Widgets Platform Runtime", "WidgetService.exe")]
    [InlineData("Rockstar Games SDK", "uninstallRGSCRedistributable.exe")]
    [InlineData("Some Game", "unins000.exe")]
    [InlineData("FACEIT Anti-Cheat", "faceitclient.exe")]
    public void Vendor_background_processes_are_not_manageable(string name, string executable)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = executable,
            OriginalFilename = executable,
            ExecutablePath = $@"C:\Program Files\Vendor\{executable}",
            SignaturePublisher = "Vendor Inc."
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    // NVIDIA renames these between driver versions - OAWrapper.exe became NvOAWrapperCache.exe on
    // one controlled PC inside a week - and each arrives wearing the component's product name. The
    // directory they live in is the fact that holds still.
    [InlineData("OAWrapper.exe")]
    [InlineData("NvOAWrapperCache.exe")]
    public void The_nvidia_backend_directory_holds_no_applications(string executable)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "NVIDIA GeForce Experience Application Ontology",
            ProductName = "NVIDIA GeForce Experience Application Ontology",
            ExecutableName = executable,
            OriginalFilename = executable,
            ExecutablePath = $@"C:\Users\child\AppData\Local\NVIDIA Corporation\NVIDIA App\NvBackend\ApplicationOntology\{executable}",
            SignaturePublisher = "NVIDIA Corporation"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Fact]
    public void A_launcher_named_like_a_service_is_still_manageable()
    {
        // Riot's client - the window a child signs in and launches games from - is
        // RiotClientServices.exe. The rule that retires BlueStacksServices.exe must not take it.
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "RiotClient",
            ProductName = "RiotClient",
            ExecutableName = "RiotClientServices.exe",
            OriginalFilename = "RiotClientServices.exe",
            ExecutablePath = @"C:\Riot Games\Riot Client\RiotClientServices.exe",
            SignaturePublisher = "Riot Games, Inc."
        };

        Assert.True(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Fact]
    public void A_runtime_host_is_not_manageable_even_when_it_reports_the_hosted_family()
    {
        // WhatsApp draws itself through WebView2, so the host process reports WhatsApp's package
        // family. Taken at its word it earns a card called "Microsoft Edge WebView2" - which is
        // neither an application a parent recognizes nor one that blocking WhatsApp goes through.
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Microsoft Edge WebView2",
            ProductName = "Microsoft Edge WebView2",
            ExecutableName = "msedgewebview2.exe",
            OriginalFilename = "msedgewebview2.exe",
            PackageFamilyName = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm",
            ExecutablePath = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\151.0.4129.107\msedgewebview2.exe",
            Company = "Microsoft Corporation"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    [InlineData("Microsoft.Ink.Handwriting.Main.en-US.1.0.1")]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Main.1.8")]
    [InlineData("Microsoft.GetHelp")]
    [InlineData("Microsoft.OneDriveSync")]
    [InlineData("Microsoft.BingSearch")]
    [InlineData("Microsoft.MicrosoftOfficeHub")]
    [InlineData("Microsoft.StartExperiencesApp")]
    [InlineData("Microsoft.WidgetsPlatformRuntime")]
    public void Packaged_windows_components_are_not_manageable(string packageName)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = packageName,
            ProductName = packageName,
            PackageFamilyName = $"{packageName}_8wekyb3d8bbwe",
            ExecutablePath = $@"C:\Program Files\WindowsApps\{packageName}_1.0.0.0_x64__8wekyb3d8bbwe"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    // An agent that cannot read the package manifest sends the identity as the name. The panel
    // makes it readable rather than printing a package id at a parent.
    [InlineData("Microsoft.BingNews", "News")]
    [InlineData("38833FF26BA1D.UnigramPreview", "Unigram")]
    [InlineData("Microsoft.MicrosoftStickyNotes", "Sticky Notes")]
    [InlineData("NVIDIACorp.NVIDIAControlPanel", "NVIDIA Control Panel")]
    [InlineData("LGElectronics.LGMonitorApp", "LG Monitor App")]
    [InlineData("SomeVendor.PuzzleQuestDeluxe", "Puzzle Quest Deluxe")]
    public void Package_identities_are_shown_as_readable_names(string packageName, string expected)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = packageName,
            ProductName = packageName,
            PackageFamilyName = $"{packageName}_8wekyb3d8bbwe",
            ExecutablePath = $@"C:\Program Files\WindowsApps\{packageName}_1.0.0.0_x64__8wekyb3d8bbwe"
        };

        Assert.Equal(expected, ApplicationCatalogPolicy.GetFriendlyDisplayName(descriptor));
    }

    [Fact]
    public void A_real_display_name_survives_the_package_identity_rewrite()
    {
        // An agent that did read the manifest already sent the name Windows shows, and the leaf of
        // the package identity must not be allowed to overwrite it.
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Rail Route",
            ProductName = "SomeStudio.RailRoute",
            PackageFamilyName = "SomeStudio.RailRoute_8wekyb3d8bbwe",
            ExecutablePath = @"C:\Program Files\WindowsApps\SomeStudio.RailRoute_1.0.0.0_x64__8wekyb3d8bbwe"
        };

        Assert.Equal("Rail Route", ApplicationCatalogPolicy.GetFriendlyDisplayName(descriptor));
    }

    [Theory]
    // Every one of these earned a card on a real controlled PC. None of them is a window a child
    // opens; several are named only after the bare executable because they carry no metadata at all.
    [InlineData("adb", "adb.exe")]
    [InlineData("HD-Adb", "HD-Adb.exe")]
    [InlineData("ffmpeg", "ffmpeg.exe")]
    [InlineData("steamsysinfo", "steamsysinfo.exe")]
    [InlineData("vulkandriverquery", "vulkandriverquery.exe")]
    [InlineData("vulkandriverquery64", "vulkandriverquery64.exe")]
    [InlineData("gldriverquery64", "gldriverquery64.exe")]
    [InlineData("Vanguard Tray", "vgtray.exe")]
    [InlineData("G HUB", "lghub_system_tray.exe")]
    [InlineData("BlueStacks Watchdog", "BstkWatchdog.exe")]
    [InlineData("NVIDIA App", "nvsphelper64.exe")]
    [InlineData("Discord", "DiscordHookHelper64.exe")]
    [InlineData("Steam", "gameoverlayui64.exe")]
    [InlineData("Microsoft Edge", "mscopilot_proxy.exe")]
    [InlineData("Proton VPN", "ProtonVPN_v5.1.7_x64.exe")]
    public void Vendor_probes_trays_and_downloads_are_not_manageable(string name, string executable)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = executable,
            OriginalFilename = executable,
            ExecutablePath = $@"C:\Program Files\Vendor\{executable}"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Fact]
    public void A_hypervisor_is_not_manageable_however_it_is_named()
    {
        // BlueStacks' BstkSVC.exe announces itself as "Bluestack Hypervisor" beside the
        // HD-Player.exe card that is the emulator the child actually looks at.
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Bluestack Hypervisor",
            ProductName = "Bluestack Hypervisor",
            ExecutableName = "BstkSVC.exe",
            OriginalFilename = "BstkSVC.exe",
            Company = "Bluestack System Inc.",
            ExecutablePath = @"C:\Program Files\BlueStacks_nxt\BstkSVC.exe"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    // A trailing bitness marker must not be read as part of a name, and a game whose name simply
    // ends in a digit must not be read as carrying one.
    [InlineData("Counter-Strike 2", "cs2.exe")]
    [InlineData("Counter-Strike: Global Offensive", "csgo.exe")]
    [InlineData("Grand Theft Auto V Enhanced", "GTA5_Enhanced.exe")]
    [InlineData("BlueStacks", "HD-Player.exe")]
    [InlineData("Logitech G HUB", "lghub.exe")]
    [InlineData("Rockstar Games Launcher", "Launcher.exe")]
    [InlineData("Epic Games Launcher", "EpicGamesLauncher.exe")]
    [InlineData("Proton VPN", "ProtonVPN.exe")]
    public void Applications_beside_their_own_machinery_are_still_manageable(string name, string executable)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = executable,
            OriginalFilename = executable,
            ExecutablePath = $@"C:\Program Files\Vendor\{executable}"
        };

        Assert.True(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }

    [Theory]
    [InlineData("Clock", "Microsoft.WindowsAlarms_8wekyb3d8bbwe", "")]
    [InlineData("Feedback Hub", "Microsoft.WindowsFeedbackHub_8wekyb3d8bbwe", "")]
    [InlineData("Microsoft Edge", "", "Microsoft Corporation")]
    public void Windows_own_applications_are_recognized_as_Microsoft_published(
        string name, string family, string company)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            ExecutableName = "app.exe",
            ExecutablePath = @"C:\Program Filespp.exe",
            PackageFamilyName = family.Length == 0 ? null : family,
            Company = company.Length == 0 ? null : company
        };

        Assert.True(ApplicationCatalogPolicy.IsMicrosoftPublished(descriptor));
    }

    [Theory]
    // The switch exists to hide Windows tooling, so a game Microsoft happens to publish must stay
    // visible - Minecraft is the application a parent most wants a rule on.
    [InlineData("Minecraft", "Microsoft.MinecraftUWP_8wekyb3d8bbwe")]
    [InlineData("Solitaire Collection", "Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe")]
    [InlineData("Xbox", "Microsoft.GamingApp_8wekyb3d8bbwe")]
    public void Microsoft_published_games_are_not_hidden_as_Windows_applications(string name, string family)
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = name,
            ProductName = name,
            PackageFamilyName = family,
            Company = "Microsoft Corporation",
            ExecutablePath = $@"C:\Program Files\WindowsApps\{family}"
        };

        Assert.False(ApplicationCatalogPolicy.IsMicrosoftPublished(descriptor));
    }

    [Fact]
    public void A_third_party_application_is_not_Microsoft_published()
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Steam",
            ProductName = "Steam",
            ExecutableName = "steam.exe",
            ExecutablePath = @"C:\Program Files (x86)\Steam\steam.exe",
            Company = "Valve Corporation",
            SignaturePublisher = "Valve Corp."
        };

        Assert.False(ApplicationCatalogPolicy.IsMicrosoftPublished(descriptor));
    }

    [Fact]
    public void Packaged_applications_still_face_the_executable_checks_when_the_family_is_only_inferred()
    {
        var descriptor = new ApplicationDescriptor
        {
            DisplayName = "Example Game",
            ProductName = "Example Game",
            ExecutableName = "ExampleUpdater.exe",
            ExecutablePath = @"C:\Program Files\WindowsApps\Example.Game_1.2.3.0_x64__abcdefghijklm\ExampleUpdater.exe"
        };

        Assert.False(ApplicationCatalogPolicy.IsUserManageable(descriptor));
    }
}
