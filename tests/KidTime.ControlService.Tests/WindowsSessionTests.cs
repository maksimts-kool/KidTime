using KidTime.ControlService.Sessions;
using KidTime.Domain.Rules;

namespace KidTime.ControlService.Tests;

public sealed class WindowsSessionTests
{
    [Fact]
    public void Only_the_selected_windows_sid_is_controlled()
    {
        var rules = new DeviceRuleSnapshot { ControlledUserSid = "S-1-5-21-1000" };

        Assert.True(WindowsSession.IsUserControlled(rules,
            new WindowsSession.SessionUser(1, "PC\\child", "s-1-5-21-1000")));
        Assert.False(WindowsSession.IsUserControlled(rules,
            new WindowsSession.SessionUser(2, "PC\\parent", "S-1-5-21-2000")));
        Assert.False(WindowsSession.IsUserControlled(new DeviceRuleSnapshot(),
            new WindowsSession.SessionUser(1, "PC\\child", "S-1-5-21-1000")));
    }
}
