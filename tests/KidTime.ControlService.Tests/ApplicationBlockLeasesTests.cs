using System.Diagnostics;
using KidTime.ControlService.Enforcement;

namespace KidTime.ControlService.Tests;

public sealed class ApplicationBlockLeasesTests
{
    private const string Identity = "product:notepad|notepad.exe";
    private const string Episode = "638000000000000|2026-08-26|DailyLimitReached|";

    private static long Seconds(int seconds) => (long)(seconds * (double)Stopwatch.Frequency);

    [Fact]
    public void The_save_work_period_runs_out_only_once_it_has_elapsed()
    {
        var leases = new ApplicationBlockLeases();
        var start = Stopwatch.GetTimestamp();

        Assert.True(leases.NeedsLease(Identity, Episode));
        leases.Start(Identity, Episode, 60, start);

        Assert.False(leases.NeedsLease(Identity, Episode));
        Assert.False(leases.TryTakeExpired(Identity, start + Seconds(59)));
        Assert.True(leases.TryTakeExpired(Identity, start + Seconds(60)));
    }

    [Fact]
    public void An_application_relaunched_before_the_monitor_sees_it_stop_is_closed_again()
    {
        var leases = new ApplicationBlockLeases();
        var start = Stopwatch.GetTimestamp();
        leases.Start(Identity, Episode, 60, start);
        Assert.True(leases.TryTakeExpired(Identity, start + Seconds(60)));

        // The child relaunches inside the same two-second sweep window, so the monitor never
        // observes the application stopped and never releases the lease itself. The spent lease
        // must not stand in for the new process, or it runs unblocked for the whole episode.
        var relaunch = start + Seconds(61);
        Assert.True(leases.NeedsLease(Identity, Episode));
        leases.Start(Identity, Episode, 20, relaunch);
        Assert.False(leases.TryTakeExpired(Identity, relaunch + Seconds(19)));
        Assert.True(leases.TryTakeExpired(Identity, relaunch + Seconds(20)));
    }

    [Fact]
    public void A_close_that_did_not_take_effect_is_tried_again()
    {
        var leases = new ApplicationBlockLeases();
        var start = Stopwatch.GetTimestamp();
        leases.Start(Identity, Episode, 60, start);
        Assert.True(leases.TryTakeExpired(Identity, start + Seconds(60)));

        // The processes survived the close, so they are still running on the next sweep and
        // have to be warned about and closed again rather than left alone.
        Assert.True(leases.NeedsLease(Identity, Episode));
    }

    [Fact]
    public void A_new_restriction_episode_replaces_a_running_lease()
    {
        var leases = new ApplicationBlockLeases();
        var start = Stopwatch.GetTimestamp();
        leases.Start(Identity, Episode, 60, start);

        Assert.True(leases.NeedsLease(Identity, "638000000009999|2026-08-26|ManualBlock|"));
    }

    [Fact]
    public void A_lease_is_released_when_the_application_stops()
    {
        var leases = new ApplicationBlockLeases();
        leases.Start(Identity, Episode, 60, Stopwatch.GetTimestamp());

        Assert.Equal([Identity], leases.IdentitiesNoLongerRunning([]));
        Assert.Empty(leases.IdentitiesNoLongerRunning(new[] { Identity }));

        Assert.True(leases.Release(Identity));
        Assert.False(leases.Release(Identity));
        Assert.True(leases.NeedsLease(Identity, Episode));
    }
}
