using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Infrastructure;

/// <summary>
/// One extra-time request as the controlled PC remembers it: what was asked, what the parent
/// answered, whether it has been uploaded, and whether the child has been told. It is a local
/// record and never travels - <see cref="TimeExtensionRequest"/> is what goes to the server.
/// </summary>
public sealed record LocalTimeExtension(
    Guid RequestId,
    DateOnly LocalDate,
    string? ApplicationIdentityKey,
    string DisplayName,
    int RequestedMinutes,
    int GrantedMinutes,
    TimeExtensionStatus Status,
    DateTimeOffset RequestedAtUtc,
    /// <summary>
    /// The allowance period the request was made in. A refusal holds for exactly this period, so
    /// the child may ask again once a new stretch of screen time begins - and no sooner.
    /// </summary>
    string PeriodKey = "")
{
    /// <summary>The PC's own screen time is stored under an empty key, never a null one.</summary>
    public string ScopeKey => ApplicationIdentityKey ?? string.Empty;
}
