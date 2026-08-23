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
}
