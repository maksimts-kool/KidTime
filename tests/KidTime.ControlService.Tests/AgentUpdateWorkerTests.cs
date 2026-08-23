using KidTime.ControlService.Server;

namespace KidTime.ControlService.Tests;

public sealed class AgentUpdateWorkerTests
{
    [Theory]
    [InlineData("0.2.1", "0.2.0", true)]
    [InlineData("0.3.0", "0.2.9", true)]
    [InlineData("1.0.0", "0.9.99", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.1.9", "0.2.0", false)]
    [InlineData("invalid", "0.2.0", false)]
    [InlineData("0.2.1", "invalid", false)]
    public void Version_comparison_only_accepts_newer_valid_versions(
        string availableVersion,
        string installedVersion,
        bool expected)
    {
        Assert.Equal(expected, AgentUpdateWorker.IsNewer(availableVersion, installedVersion));
    }
}
