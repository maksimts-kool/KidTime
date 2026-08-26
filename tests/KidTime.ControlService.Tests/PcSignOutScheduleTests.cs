using System.Diagnostics;
using KidTime.ControlService.Sessions;

namespace KidTime.ControlService.Tests;

public sealed class PcSignOutScheduleTests
{
    private const uint SessionId = 2;
    private const string User = @"KIDPC\child";

    private static long Seconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void A_session_is_warned_once_and_signed_out_when_the_warning_runs_out()
    {
        var schedule = new PcSignOutSchedule();
        var start = Stopwatch.GetTimestamp();

        Assert.False(schedule.IsWarned);
        Assert.Equal(SignOutStep.Warn, schedule.Next(SessionId, User, start));

        schedule.Warn(SessionId, User, 60, start);
        Assert.True(schedule.IsWarned);
        Assert.Equal(SignOutStep.Wait, schedule.Next(SessionId, User, start + Seconds(59)));
        Assert.Equal(SignOutStep.SignOut, schedule.Next(SessionId, User, start + Seconds(60)));
    }

    [Fact]
    public void An_issued_sign_out_is_given_time_to_take_effect()
    {
        var schedule = new PcSignOutSchedule();
        var start = Stopwatch.GetTimestamp();
        schedule.Warn(SessionId, User, 60, start);

        var issued = start + Seconds(60);
        schedule.SignOutIssued(issued);

        // Signing out does not wait for the session to end, so a session still present just
        // afterwards is ordinary teardown and must not be signed out a second time.
        Assert.Equal(SignOutStep.Wait, schedule.Next(SessionId, User, issued + Seconds(1)));
        Assert.Equal(SignOutStep.Wait,
            schedule.Next(SessionId, User, issued + Seconds(PcSignOutSchedule.SettlePeriod.TotalSeconds - 1)));
    }

    [Fact]
    public void A_session_that_outlives_its_sign_out_starts_the_cycle_over()
    {
        var schedule = new PcSignOutSchedule();
        var start = Stopwatch.GetTimestamp();
        schedule.Warn(SessionId, User, 60, start);
        var issued = start + Seconds(60);
        schedule.SignOutIssued(issued);

        // The sign-out did not take effect. Latching it would leave the blocked PC in use for
        // the rest of the restriction, so the child is warned again and it is retried.
        var stalled = issued + Seconds(PcSignOutSchedule.SettlePeriod.TotalSeconds);
        Assert.Equal(SignOutStep.WarnAgain, schedule.Next(SessionId, User, stalled));

        schedule.Warn(SessionId, User, 20, stalled);
        Assert.Equal(SignOutStep.Wait, schedule.Next(SessionId, User, stalled + Seconds(19)));
        Assert.Equal(SignOutStep.SignOut, schedule.Next(SessionId, User, stalled + Seconds(20)));
    }

    [Fact]
    public void A_different_session_or_user_is_warned_on_its_own()
    {
        var schedule = new PcSignOutSchedule();
        var start = Stopwatch.GetTimestamp();
        schedule.Warn(SessionId, User, 60, start);
        schedule.SignOutIssued(start + Seconds(60));

        Assert.Equal(SignOutStep.Warn, schedule.Next(SessionId + 1, User, start + Seconds(61)));
        Assert.Equal(SignOutStep.Warn, schedule.Next(SessionId, @"KIDPC\other", start + Seconds(61)));
        Assert.Equal(SignOutStep.Wait, schedule.Next(SessionId, @"kidpc\CHILD", start + Seconds(61)));
    }

    [Fact]
    public void Clearing_withdraws_the_warning()
    {
        var schedule = new PcSignOutSchedule();
        schedule.Warn(SessionId, User, 60, Stopwatch.GetTimestamp());

        schedule.Clear();

        Assert.False(schedule.IsWarned);
        Assert.Equal(SignOutStep.Warn, schedule.Next(SessionId, User, Stopwatch.GetTimestamp()));
    }
}
