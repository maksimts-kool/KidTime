using KidTime.Domain.Contracts;

namespace KidTime.Domain.Tests;

public sealed class TimeExtensionPolicyTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(25)]
    [InlineData(30)]
    public void Every_stop_on_the_slider_may_be_asked_for(int minutes) =>
        Assert.True(TimeExtensionPolicy.IsAllowedRequest(minutes));

    [Theory]
    // Below the floor, above the ceiling, and between two stops. The child's slider cannot produce
    // any of these, which is exactly why the service and the server check rather than trust it.
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(60)]
    [InlineData(240)]
    [InlineData(-15)]
    public void Anything_off_the_slider_is_refused(int minutes) =>
        Assert.False(TimeExtensionPolicy.IsAllowedRequest(minutes));

    [Fact]
    public void The_stops_are_five_minutes_apart_from_five_to_thirty() =>
        Assert.Equal([5, 10, 15, 20, 25, 30], TimeExtensionPolicy.AllowedMinutes);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(7, 5)]
    [InlineData(8, 10)]
    [InlineData(22, 20)]
    [InlineData(23, 25)]
    [InlineData(1000, 30)]
    public void A_number_from_anywhere_snaps_onto_a_stop(int minutes, int expected) =>
        Assert.Equal(expected, TimeExtensionPolicy.ClampToStep(minutes));

    [Fact]
    public void The_default_the_slider_opens_on_is_one_of_the_stops() =>
        Assert.True(TimeExtensionPolicy.IsAllowedRequest(TimeExtensionPolicy.DefaultRequestMinutes));

    [Theory]
    // A grant is checked against a looser ceiling than a request, so a decision the panel makes
    // outside the slider - or one an older PC asked for - is still bounded by something.
    [InlineData(1, true)]
    [InlineData(240, true)]
    [InlineData(0, false)]
    [InlineData(241, false)]
    public void A_grant_is_bounded_but_not_stepped(int minutes, bool allowed) =>
        Assert.Equal(allowed, TimeExtensionPolicy.IsAllowedGrant(minutes));
}
