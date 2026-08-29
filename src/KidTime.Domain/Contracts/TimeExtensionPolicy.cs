namespace KidTime.Domain.Contracts;

/// <summary>
/// The shape of an extra-time request, agreed by the controlled PC and the server so neither can
/// be talked into something the other would refuse.
///
/// The controlled PC's agent is unelevated and the child is the one holding the mouse, so nothing
/// here is a security boundary on its own - the LocalSystem service re-checks every rule below
/// before a request leaves the machine, and the server checks them again on arrival. What a child
/// can do at worst is put a request in front of their parent.
/// </summary>
public static class TimeExtensionPolicy
{
    /// <summary>
    /// How little has to be left before asking is offered at all. Asking for more time while an
    /// hour remains is not what this is for, and an always-visible button would be pressed all day.
    /// </summary>
    public const int RequestThresholdSeconds = 5 * 60;

    /// <summary>
    /// The amounts a child can ask for, as a slider rather than a keyboard: five minutes to
    /// thirty, in steps of five. A dial with a floor and a ceiling asks a smaller question than a
    /// free number field, and there is nothing to haggle over between the stops.
    /// </summary>
    public const int MinimumRequestMinutes = 5;
    public const int MaximumRequestMinutes = 30;
    public const int RequestStepMinutes = 5;

    /// <summary>The stops on that slider, for a UI that needs to enumerate them.</summary>
    public static readonly int[] AllowedMinutes =
        [.. Enumerable.Range(0, (MaximumRequestMinutes - MinimumRequestMinutes) / RequestStepMinutes + 1)
            .Select(step => MinimumRequestMinutes + step * RequestStepMinutes)];

    /// <summary>Where the child's slider starts before they move it.</summary>
    public const int DefaultRequestMinutes = 15;

    /// <summary>
    /// The most a parent can grant in one decision. The panel offers the same slider the child
    /// used, so this is the ceiling the API enforces rather than the one a parent sees - an older
    /// or hand-made request is still bounded by something.
    /// </summary>
    public const int MaximumGrantedMinutes = 240;

    /// <summary>Snaps a number onto the nearest slider stop, for a value arriving from anywhere.</summary>
    public static int ClampToStep(int minutes)
    {
        var stepped = (int)Math.Round(minutes / (double)RequestStepMinutes) * RequestStepMinutes;
        return Math.Clamp(stepped, MinimumRequestMinutes, MaximumRequestMinutes);
    }

    /// <summary>
    /// How many requests one PC may make in a day, over every scope together. A child who is told
    /// no can ask again - a handful of times, not until somebody gives in.
    /// </summary>
    public const int MaximumRequestsPerDay = 8;

    public static bool IsAllowedRequest(int minutes) =>
        minutes >= MinimumRequestMinutes
        && minutes <= MaximumRequestMinutes
        && minutes % RequestStepMinutes == 0;

    public static bool IsAllowedGrant(int minutes) => minutes is > 0 && minutes <= MaximumGrantedMinutes;
}
