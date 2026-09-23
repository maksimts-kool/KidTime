using System.Diagnostics;
using KidTime.ControlService.Sessions;

namespace KidTime.ControlService.Tests;

public sealed class PcSignOutStateTests
{
    private static long Seconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void A_session_Windows_says_is_being_torn_down_is_still_signing_out_past_the_settle_period()
    {
        var state = new PcSignOutState();
        var issued = Stopwatch.GetTimestamp();
        state.MarkIssued(issued);

        // A slow PC: the agent the supervisor tried once the fifteen-second window passed was refused by a
        // window station that is shutting down.
        state.NoteTeardownObserved(issued + Seconds(18));

        Assert.True(state.IsTearingDown(issued + Seconds(PcSignOutSchedule.SettlePeriod.TotalSeconds)));
    }

    [Fact]
    public void Without_that_word_from_Windows_a_session_that_outlived_its_sign_out_is_not_excused()
    {
        var state = new PcSignOutState();
        var issued = Stopwatch.GetTimestamp();
        state.MarkIssued(issued);

        Assert.False(state.IsTearingDown(issued + Seconds(30)));
    }

    [Fact]
    public void Teardown_seen_before_the_sign_out_was_issued_says_nothing_about_it()
    {
        var state = new PcSignOutState();
        var start = Stopwatch.GetTimestamp();
        state.NoteTeardownObserved(start);
        state.MarkIssued(start + Seconds(5));

        Assert.False(state.IsTearingDown(start + Seconds(35)));
    }

    [Fact]
    public void The_evidence_expires_unless_the_supervisor_renews_it()
    {
        var state = new PcSignOutState();
        var issued = Stopwatch.GetTimestamp();
        state.MarkIssued(issued);
        state.NoteTeardownObserved(issued + Seconds(18));

        var expiry = issued + Seconds(18) + Seconds(PcSignOutState.TeardownEvidenceWindow.TotalSeconds);
        Assert.False(state.IsTearingDown(expiry));

        state.NoteTeardownObserved(issued + Seconds(80));
        Assert.True(state.IsTearingDown(expiry));
    }

    [Fact]
    public void Nothing_is_tearing_down_before_any_sign_out()
    {
        var state = new PcSignOutState();
        var now = Stopwatch.GetTimestamp();
        state.NoteTeardownObserved(now);

        Assert.False(state.IsTearingDown(now));
    }
}
