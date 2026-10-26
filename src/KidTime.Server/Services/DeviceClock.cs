using System.Globalization;
using KidTime.Domain.Rules;

namespace KidTime.Server.Services;

/// <summary>
/// Times the panel shows, written in the PC's own wall clock. A Windows PC reports a Windows
/// timezone id, which .NET resolves on any platform and the browser's <c>Intl</c> does not, so the
/// panel is handed the local reading and never converts an instant itself.
/// </summary>
public static class DeviceClock
{
    public static string ToLocalDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"2026-08-31T21:00" on the PC's clock; null stays null.</summary>
    public static string? ToLocalStamp(DateTimeOffset? instant, string timeZoneId) =>
        instant is { } value
            ? RuleEvaluator.ToLocalTime(value, timeZoneId).ToString("yyyy-MM-dd'T'HH':'mm", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// When the window a grant opened over a manual block or a closed schedule runs out, while it
    /// is still running. That half of a grant is wall-clock time from the parent's decision.
    /// </summary>
    public static string? ExtraTimeUntilLocal(TimeBonus? bonus, DateTimeOffset now, string timeZoneId) =>
        TimeBonus.LiftsBlocksAt(bonus, now) ? ToLocalStamp(bonus!.LiftedUntilUtc, timeZoneId) : null;
}
