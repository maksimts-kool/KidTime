using KidTime.Domain.Contracts;

namespace KidTime.Domain.Tests;

/// <summary>
/// How the tray agent tells a shortcut launch from the one the service supervises.
///
/// The asymmetry is the whole point. Reading a shortcut launch as the supervised agent costs the
/// child one window that did not open; reading the supervised launch as a shortcut costs them the
/// agent entirely - it would signal nothing, exit, be relaunched two seconds later, and repeat for
/// as long as the PC is on, leaving no tray icon, no screen-time window and no notifications while
/// enforcement carried on without them.
/// </summary>
public class SessionAgentArgumentsTests
{
    /// <summary>The service launches the agent with no arguments at all. This must never move.</summary>
    [Fact]
    public void TheSupervisedLaunchIsNeverAShowRequest()
    {
        Assert.False(SessionAgentArguments.IsShowRequest([]));
        Assert.False(SessionAgentArguments.IsShowRequest(null));
    }

    [Fact]
    public void TheShortcutArgumentIsAShowRequest() =>
        Assert.True(SessionAgentArguments.IsShowRequest([SessionAgentArguments.Show]));

    /// <summary>
    /// Windows and the shell can add their own arguments to a launch, so the switch is looked for
    /// rather than the whole command line matched.
    /// </summary>
    [Fact]
    public void TheArgumentIsFoundBesideOthers() =>
        Assert.True(SessionAgentArguments.IsShowRequest(["/restart", SessionAgentArguments.Show]));

    [Fact]
    public void TheArgumentIsCaseInsensitive() =>
        Assert.True(SessionAgentArguments.IsShowRequest(["--SHOW"]));

    /// <summary>
    /// Anything else is the supervised agent, including something that merely looks like the
    /// switch - a partial match would make the reading permissive in the direction that costs the
    /// child their agent.
    /// </summary>
    [Theory]
    [InlineData("--showwindow")]
    [InlineData("show")]
    [InlineData("-show")]
    [InlineData("")]
    public void AnythingElseIsTheSupervisedAgent(string argument) =>
        Assert.False(SessionAgentArguments.IsShowRequest([argument]));
}
