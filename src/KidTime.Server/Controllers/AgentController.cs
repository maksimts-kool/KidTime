using System.Security.Claims;
using KidTime.Domain.Contracts;
using KidTime.Server.Data;
using KidTime.Server.Security;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using KidTime.Domain.Applications;

namespace KidTime.Server.Controllers;

[ApiController]
[Route("api/agent")]
public sealed class AgentController(
    KidTimeDbContext dbContext,
    RuleSnapshotFactory snapshots,
    AgentUpdateCatalog updates,
    TimeProvider timeProvider,
    ILogger<AgentController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("enroll")]
    public async Task<IActionResult> Enroll(DeviceEnrollmentRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!EnrollmentCode.TryParse(request.EnrollmentToken, out var enrollmentToken, out _))
            return Unauthorized(new { message = "Enrollment token is invalid, expired, or already used." });
        var tokenHash = TokenUtilities.Hash(enrollmentToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var enrollment = await dbContext.EnrollmentTokens.SingleOrDefaultAsync(
            token => token.TokenHash == tokenHash && token.UsedAtUtc == null && token.ExpiresAtUtc > now,
            cancellationToken);
        if (enrollment is null)
        {
            return Unauthorized(new { message = "Enrollment token is invalid, expired, or already used." });
        }

        var controlledWindowsUser = NormalizeControlledWindowsUser(request.ControlledWindowsUser);
        if (request.ControlledWindowsUser is not null && controlledWindowsUser is null)
            return BadRequest(new { message = "Choose an enabled Standard User account." });

        var device = new Device
        {
            Name = request.DeviceName.Trim(),
            WindowsVersion = request.WindowsVersion.Trim(),
            TimeZoneId = request.TimeZoneId.Trim(),
            EnrolledAtUtc = now,
            LastSeenUtc = null,
            WindowsUsersJson = controlledWindowsUser is null
                ? "[]"
                : JsonSerializer.Serialize(new[] { controlledWindowsUser }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        device.Rule = new DeviceRule
        {
            DeviceId = device.Id,
            Device = device,
            ControlledUserSid = controlledWindowsUser?.Sid,
            ControlledUserName = controlledWindowsUser?.AccountName
        };
        var rawDeviceToken = TokenUtilities.Generate(48);
        device.Credentials.Add(new DeviceCredential
        {
            DeviceId = device.Id,
            Device = device,
            TokenHash = TokenUtilities.Hash(rawDeviceToken),
            CreatedAtUtc = now
        });
        enrollment.UsedAtUtc = now;
        enrollment.EnrolledDeviceId = device.Id;
        dbContext.Devices.Add(device);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Enrolled device {DeviceName} ({DeviceId}).", device.Name, device.Id);
        var rules = await snapshots.CreateAsync(device.Id, cancellationToken);
        return Ok(new DeviceEnrollmentResponse(device.Id, rawDeviceToken, rules));
    }

    private static WindowsUserAccount? NormalizeControlledWindowsUser(WindowsUserAccount? user)
    {
        if (user is null || !user.IsEnabled || user.IsAdministrator
            || string.IsNullOrWhiteSpace(user.Sid) || string.IsNullOrWhiteSpace(user.AccountName)
            || user.Sid.Length > 184 || user.AccountName.Length > 255)
            return null;
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.AccountName.Trim() : user.DisplayName.Trim();
        return user with
        {
            Sid = user.Sid.Trim(),
            AccountName = user.AccountName.Trim(),
            DisplayName = displayName[..Math.Min(displayName.Length, 255)]
        };
    }

    [Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(DeviceHeartbeatRequest request, CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.SingleAsync(x => x.Id == DeviceId, cancellationToken);
        device.LastSeenUtc = timeProvider.GetUtcNow();
        device.LoggedInUser = Trim(request.LoggedInUser, 255);
        device.ForegroundApplication = Trim(request.ForegroundApplication, 255);
        device.ForegroundIdentityKey = Trim(request.ForegroundIdentityKey, 64);
        if (request.WindowsUsers is not null)
        {
            device.WindowsUsersJson = JsonSerializer.Serialize(
                request.WindowsUsers
                    .Where(user => !string.IsNullOrWhiteSpace(user.Sid) && !string.IsNullOrWhiteSpace(user.AccountName))
                    .DistinctBy(user => user.Sid, StringComparer.OrdinalIgnoreCase)
                    .Take(100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        device.AppliedRuleRevision = request.RuleRevision;
        device.AgentVersion = Trim(request.AgentVersion, 50) ?? device.AgentVersion;
        device.AgentUpdateStatus = Trim(request.AgentUpdateStatus, 50) ?? device.AgentUpdateStatus;
        device.AgentUpdateError = Trim(request.AgentUpdateError, 1000);
        device.AgentUpdateCheckedAtUtc = request.AgentUpdateCheckedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
    [HttpGet("sync")]
    public async Task<IActionResult> Sync(CancellationToken cancellationToken)
    {
        var commands = await dbContext.DeviceCommands.AsNoTracking()
            .Where(command => command.DeviceId == DeviceId && command.AcknowledgedAtUtc == null)
            .OrderBy(command => command.CreatedAtUtc).Take(100)
            .Select(command => new AgentCommand(command.Id, command.Type, command.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return Ok(new AgentSyncResponse(
            await snapshots.CreateAsync(DeviceId, cancellationToken),
            commands,
            timeProvider.GetUtcNow()));
    }

    [Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
    [HttpGet("update")]
    public IActionResult GetUpdate()
    {
        var package = updates.GetLatest();
        return package is null ? NoContent() : Ok(package.Manifest);
    }

    [Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
    [HttpGet("update/package/{version}")]
    public async Task<IActionResult> DownloadUpdate(string version, CancellationToken cancellationToken)
    {
        var package = updates.GetLatest();
        if (package is null || !string.Equals(package.Manifest.Version, version, StringComparison.OrdinalIgnoreCase))
            return NotFound();
        if (!await updates.VerifyPackageAsync(package, cancellationToken))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The published agent package failed integrity verification." });
        return PhysicalFile(package.PackagePath, "application/zip", $"kidtime-agent-{package.Manifest.Version}.zip", enableRangeProcessing: true);
    }

    [Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
    [HttpPost("commands/{commandId:guid}/ack")]
    public async Task<IActionResult> AcknowledgeCommand(Guid commandId, CancellationToken cancellationToken)
    {
        var command = await dbContext.DeviceCommands.SingleOrDefaultAsync(
            item => item.Id == commandId && item.DeviceId == DeviceId,
            cancellationToken);
        if (command is null) return NotFound();
        command.AcknowledgedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
    [HttpPost("usage")]
    public async Task<IActionResult> UploadUsage(UsageBatchRequest request, CancellationToken cancellationToken)
    {
        if (await dbContext.ProcessedUsageBatches.AnyAsync(x => x.BatchId == request.BatchId, cancellationToken))
        {
            return NoContent();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var delta in request.Deltas.Where(x => x.ActiveSeconds > 0))
        {
            if (delta.ApplicationIdentityKey is null)
            {
                var daily = await dbContext.DailyDeviceUsages.SingleOrDefaultAsync(
                    x => x.DeviceId == DeviceId && x.LocalDate == delta.LocalDate,
                    cancellationToken);
                if (daily is null)
                {
                    daily = new DailyDeviceUsage { DeviceId = DeviceId, LocalDate = delta.LocalDate };
                    dbContext.DailyDeviceUsages.Add(daily);
                }

                daily.ActiveSeconds = checked(daily.ActiveSeconds + delta.ActiveSeconds);
                daily.UpdatedAtUtc = timeProvider.GetUtcNow();
                continue;
            }

            var deviceApplication = await dbContext.DeviceApplications
                .Include(x => x.Application)
                .SingleOrDefaultAsync(
                    x => x.DeviceId == DeviceId && x.Application.IdentityKey == delta.ApplicationIdentityKey,
                    cancellationToken);
            if (deviceApplication is null)
            {
                logger.LogWarning("Ignoring usage for undiscovered identity {IdentityKey} from {DeviceId}.",
                    delta.ApplicationIdentityKey, DeviceId);
                continue;
            }

            var appDaily = await dbContext.DailyApplicationUsages.SingleOrDefaultAsync(
                x => x.DeviceApplicationId == deviceApplication.Id && x.LocalDate == delta.LocalDate,
                cancellationToken);
            if (appDaily is null)
            {
                appDaily = new DailyApplicationUsage
                {
                    DeviceApplicationId = deviceApplication.Id,
                    LocalDate = delta.LocalDate
                };
                dbContext.DailyApplicationUsages.Add(appDaily);
            }

            appDaily.ActiveSeconds = checked(appDaily.ActiveSeconds + delta.ActiveSeconds);
            appDaily.UpdatedAtUtc = timeProvider.GetUtcNow();
        }

        dbContext.ProcessedUsageBatches.Add(new ProcessedUsageBatch
        {
            BatchId = request.BatchId,
            DeviceId = DeviceId,
            ProcessedAtUtc = timeProvider.GetUtcNow()
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Accepts fault reports from an enrolled agent. Reports are untrusted input: every field is
    /// re-normalized here, repeats collapse onto one row by fingerprint, an upload that is
    /// retried after an uncertain response does not double count, and the per-device history is
    /// capped so a crash loop cannot grow the database without bound.
    /// </summary>
    [Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
    [HttpPost("diagnostics")]
    public async Task<IActionResult> ReportDiagnostics(DiagnosticReportBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Reports.Count == 0) return NoContent();
        var now = timeProvider.GetUtcNow();
        foreach (var submitted in batch.Reports.Take(DiagnosticReportPolicy.MaximumReportsPerBatch))
        {
            var report = DiagnosticReportPolicy.Normalize(submitted);
            var fingerprint = DiagnosticReportPolicy.CreateFingerprint(report);
            var occurredAtUtc = report.OccurredAtUtc > now ? now : report.OccurredAtUtc;
            var existing = await dbContext.DeviceDiagnosticEvents.SingleOrDefaultAsync(
                item => item.DeviceId == DeviceId && item.Fingerprint == fingerprint,
                cancellationToken);
            if (existing is null)
            {
                dbContext.DeviceDiagnosticEvents.Add(new DeviceDiagnosticEvent
                {
                    DeviceId = DeviceId,
                    LastReportId = report.ReportId,
                    Fingerprint = fingerprint,
                    Component = report.Component,
                    Severity = report.Severity,
                    Message = report.Message,
                    ExceptionType = report.ExceptionType,
                    Detail = report.Detail,
                    AgentVersion = report.AgentVersion,
                    FirstOccurredAtUtc = occurredAtUtc,
                    LastOccurredAtUtc = occurredAtUtc,
                    ReceivedAtUtc = now
                });
                continue;
            }

            if (existing.LastReportId == report.ReportId) continue;
            existing.LastReportId = report.ReportId;
            existing.OccurrenceCount++;
            existing.Severity = report.Severity;
            existing.Message = report.Message;
            existing.ExceptionType = report.ExceptionType;
            existing.Detail = report.Detail;
            existing.AgentVersion = report.AgentVersion;
            existing.LastOccurredAtUtc = occurredAtUtc;
            existing.ReceivedAtUtc = now;
            existing.ResolvedAtUtc = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await TrimDiagnosticsAsync(cancellationToken);
        logger.LogInformation("Stored {Count} diagnostic report(s) from {DeviceId}.", batch.Reports.Count, DeviceId);
        return NoContent();
    }

    private async Task TrimDiagnosticsAsync(CancellationToken cancellationToken)
    {
        const int keep = 200;
        var total = await dbContext.DeviceDiagnosticEvents.CountAsync(
            item => item.DeviceId == DeviceId, cancellationToken);
        if (total <= keep) return;
        var expired = await dbContext.DeviceDiagnosticEvents
            .Where(item => item.DeviceId == DeviceId)
            .OrderByDescending(item => item.LastOccurredAtUtc)
            .Skip(keep)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        await dbContext.DeviceDiagnosticEvents
            .Where(item => expired.Contains(item.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    [Authorize(AuthenticationSchemes = DeviceAuthenticationDefaults.Scheme)]
    [HttpPost("applications")]
    public async Task<IActionResult> DiscoverApplication(
        DiscoveredApplicationRequest request,
        CancellationToken cancellationToken)
    {
        var application = await dbContext.Applications.SingleOrDefaultAsync(
            x => x.IdentityKey == request.IdentityKey,
            cancellationToken);
        if (application is null)
        {
            var existingDeviceApplications = await dbContext.DeviceApplications
                .Include(item => item.Application)
                .Where(item => item.DeviceId == DeviceId)
                .ToListAsync(cancellationToken);
            application = existingDeviceApplications
                .Select(item => item.Application)
                .FirstOrDefault(candidate => ApplicationIdentity.IsSameApplication(ToDescriptor(candidate, request.Descriptor.ExecutablePath), request.Descriptor));
            if (application is not null)
            {
                application.IdentityKey = request.IdentityKey;
                logger.LogInformation("Reconciled legacy application identity for {Application} on {DeviceId}.", request.Descriptor.DisplayName, DeviceId);
            }
            else
            {
                application = new Application
                {
                    IdentityKey = request.IdentityKey,
                    DisplayName = Trim(request.Descriptor.DisplayName, 255) ?? "Unknown application",
                    ExecutableName = Trim(request.Descriptor.ExecutableName, 255) ?? string.Empty
                };
                dbContext.Applications.Add(application);
            }
        }

        application.DisplayName = Trim(request.Descriptor.DisplayName, 255) ?? application.DisplayName;
        application.ExecutableName = Trim(request.Descriptor.ExecutableName, 255) ?? application.ExecutableName;
        application.ProductName = Trim(request.Descriptor.ProductName, 255);
        application.OriginalFilename = Trim(request.Descriptor.OriginalFilename, 255);
        application.Company = Trim(request.Descriptor.Company, 255);
        application.SignaturePublisher = Trim(request.Descriptor.SignaturePublisher, 512);
        application.PackageFamilyName = Trim(request.Descriptor.PackageFamilyName, 255);

        var deviceApplication = await dbContext.DeviceApplications.Include(x => x.Rule)
            .SingleOrDefaultAsync(x => x.DeviceId == DeviceId && x.ApplicationId == application.Id, cancellationToken);
        if (deviceApplication is null)
        {
            deviceApplication = new DeviceApplication
            {
                DeviceId = DeviceId,
                Application = application,
                ExecutablePath = Trim(request.Descriptor.ExecutablePath, 2048) ?? string.Empty,
                FileVersion = Trim(request.Descriptor.FileVersion, 100),
                Sha256 = Trim(request.Descriptor.Sha256, 64),
                FirstSeenUtc = request.FirstSeenUtc,
                LastSeenUtc = request.LastSeenUtc
            };
            deviceApplication.Rule = new ApplicationRule
            {
                DeviceApplicationId = deviceApplication.Id,
                DeviceApplication = deviceApplication
            };
            dbContext.DeviceApplications.Add(deviceApplication);
        }
        else
        {
            deviceApplication.ExecutablePath = Trim(request.Descriptor.ExecutablePath, 2048) ?? deviceApplication.ExecutablePath;
            deviceApplication.FileVersion = Trim(request.Descriptor.FileVersion, 100);
            deviceApplication.Sha256 = Trim(request.Descriptor.Sha256, 64);
            deviceApplication.IconPng = DecodeIcon(request.Descriptor.IconPngBase64);
            deviceApplication.LastSeenUtc = request.LastSeenUtc;
        }

        var icon = DecodeIcon(request.Descriptor.IconPngBase64);
        if (icon is not null) deviceApplication.IconPng = icon;

        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { id = deviceApplication.Id });
    }

    private Guid DeviceId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static string? Trim(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];

    private static byte[]? DecodeIcon(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            return bytes.Length is > 8 and <= 256 * 1024
                   && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                ? bytes
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ApplicationDescriptor ToDescriptor(Application application, string executablePath) => new()
    {
        DisplayName = application.DisplayName,
        ExecutableName = application.ExecutableName,
        ExecutablePath = executablePath,
        ProductName = application.ProductName,
        OriginalFilename = application.OriginalFilename,
        Company = application.Company,
        SignaturePublisher = application.SignaturePublisher,
        PackageFamilyName = application.PackageFamilyName
    };
}
