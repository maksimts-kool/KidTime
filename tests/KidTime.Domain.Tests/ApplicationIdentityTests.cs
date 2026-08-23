using KidTime.Domain.Applications;

namespace KidTime.Domain.Tests;

public sealed class ApplicationIdentityTests
{
    [Fact]
    public void Signed_application_identity_survives_path_version_and_hash_changes()
    {
        var oldVersion = Roblox("C:\\Users\\child\\AppData\\Local\\Roblox\\Versions\\v1\\RobloxPlayerBeta.exe", "1.0", "aaa");
        var update = Roblox("C:\\Users\\child\\AppData\\Local\\Roblox\\Versions\\v2\\RobloxPlayerBeta.exe", "1.1", "bbb");

        Assert.Equal(ApplicationIdentity.CreateKey(oldVersion), ApplicationIdentity.CreateKey(update));
        Assert.True(ApplicationIdentity.IsSameApplication(oldVersion, update));
    }

    [Fact]
    public void Package_identity_is_stable()
    {
        var first = new ApplicationDescriptor { PackageFamilyName = "Example.App_123", ExecutablePath = "one.exe" };
        var second = new ApplicationDescriptor { PackageFamilyName = "example.app_123", ExecutablePath = "two.exe" };

        Assert.Equal(ApplicationIdentity.CreateKey(first), ApplicationIdentity.CreateKey(second));
    }

    [Fact]
    public void Installer_and_running_edge_descriptors_share_an_identity()
    {
        var installed = new ApplicationDescriptor
        {
            DisplayName = "Microsoft Edge",
            ExecutableName = "msedge.exe",
            ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            ProductName = "Microsoft Edge",
            Company = "Microsoft Corporation"
        };
        var running = new ApplicationDescriptor
        {
            DisplayName = "Microsoft Edge",
            ExecutableName = "msedge.exe",
            ExecutablePath = installed.ExecutablePath,
            ProductName = "Microsoft Edge",
            OriginalFilename = "msedge.exe",
            SignaturePublisher = "Microsoft Corporation"
        };

        Assert.Equal(ApplicationIdentity.CreateKey(installed), ApplicationIdentity.CreateKey(running));
        Assert.True(ApplicationIdentity.IsSameApplication(installed, running));
    }

    [Fact]
    public void Edge_browser_and_elevation_helper_have_different_identities()
    {
        var browser = new ApplicationDescriptor
        {
            ExecutableName = "msedge.exe",
            ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            ProductName = "Microsoft Edge",
            Company = "Microsoft Corporation"
        };
        var helper = new ApplicationDescriptor
        {
            ExecutableName = "elevation_service.exe",
            ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\elevation_service.exe",
            ProductName = "Microsoft Edge",
            Company = "Microsoft Corporation"
        };

        Assert.NotEqual(ApplicationIdentity.CreateKey(browser), ApplicationIdentity.CreateKey(helper));
    }

    [Fact]
    public void Firefox_installer_and_runtime_descriptors_share_an_identity_after_normalization()
    {
        var installed = new ApplicationDescriptor
        {
            DisplayName = "Mozilla Firefox (x64 en-US)",
            ExecutableName = "firefox.exe",
            ExecutablePath = @"C:\Program Files\Mozilla Firefox\firefox.exe",
            ProductName = "Mozilla Firefox",
            Company = "Mozilla Corporation"
        };
        var running = new ApplicationDescriptor
        {
            DisplayName = "Firefox",
            ExecutableName = "firefox.exe",
            ExecutablePath = installed.ExecutablePath,
            ProductName = "Firefox",
            OriginalFilename = "firefox.exe",
            SignaturePublisher = "Mozilla Corporation"
        };

        var normalizedInstalled = ApplicationCatalogPolicy.NormalizeForCatalog(installed);
        var normalizedRunning = ApplicationCatalogPolicy.NormalizeForCatalog(running);
        Assert.Equal("Firefox", normalizedInstalled.DisplayName);
        Assert.Equal(ApplicationIdentity.CreateKey(normalizedInstalled), ApplicationIdentity.CreateKey(normalizedRunning));
    }

    [Fact]
    public void Unrelated_signed_applications_do_not_match()
    {
        var roblox = Roblox("roblox.exe", "1", "a");
        var other = new ApplicationDescriptor
        {
            ExecutableName = "other.exe",
            ExecutablePath = "C:\\Other\\other.exe",
            ProductName = "Other App",
            OriginalFilename = "other.exe",
            SignaturePublisher = "Other Publisher"
        };

        Assert.False(ApplicationIdentity.IsSameApplication(roblox, other));
    }

    private static ApplicationDescriptor Roblox(string path, string version, string hash) => new()
    {
        DisplayName = "Roblox",
        ExecutableName = "RobloxPlayerBeta.exe",
        ExecutablePath = path,
        ProductName = "Roblox",
        OriginalFilename = "RobloxPlayerBeta.exe",
        Company = "Roblox Corporation",
        SignaturePublisher = "Roblox Corporation",
        FileVersion = version,
        Sha256 = hash
    };
}
