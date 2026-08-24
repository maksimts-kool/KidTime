using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KidTime.Domain.Contracts;
using KidTime.Server.Data;
using KidTime.Server.Security;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/enrollment-tokens")]
public sealed class EnrollmentController(
    KidTimeDbContext dbContext,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<EnrollmentController> logger) : ControllerBase
{
    public sealed record CreateTokenRequest(int ValidForMinutes = 30);

    [HttpPost]
    public async Task<IActionResult> Create(CreateTokenRequest request, CancellationToken cancellationToken)
    {
        var minutes = Math.Clamp(request.ValidForMinutes, 5, 1_440);
        var token = TokenUtilities.Generate();
        var now = timeProvider.GetUtcNow();
        var enrollment = new EnrollmentToken
        {
            TokenHash = TokenUtilities.Hash(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(minutes)
        };
        dbContext.EnrollmentTokens.Add(enrollment);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            id = enrollment.Id,
            token = EnrollmentCode.Create(token, GetCertificatePin()),
            expiresAtUtc = enrollment.ExpiresAtUtc,
            serverUrl = configuration["AgentEnrollment:PublicUrl"]
        });
    }

    [HttpGet("{tokenId:guid}")]
    public async Task<IActionResult> GetStatus(Guid tokenId, CancellationToken cancellationToken)
    {
        var enrollment = await dbContext.EnrollmentTokens.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == tokenId,
            cancellationToken);
        if (enrollment is null) return NotFound();

        if (enrollment.EnrolledDeviceId is Guid deviceId)
        {
            var device = await dbContext.Devices.AsNoTracking().Include(item => item.Rule)
                .SingleOrDefaultAsync(item => item.Id == deviceId, cancellationToken);
            if (device is not null)
            {
                var now = timeProvider.GetUtcNow();
                var isReady = device.LastSeenUtc >= now.AddMinutes(-2) && !string.IsNullOrWhiteSpace(device.AgentVersion);
                return Ok(new
                {
                    status = isReady ? "connected" : "finishing",
                    device = new
                    {
                        device.Id,
                        device.Name,
                        controlledUserName = device.Rule.ControlledUserName,
                        isOnline = device.LastSeenUtc >= now.AddMinutes(-2)
                    }
                });
            }
        }

        return Ok(new
        {
            status = enrollment.ExpiresAtUtc <= timeProvider.GetUtcNow() ? "expired" : "pending"
        });
    }

    private string? GetCertificatePin()
    {
        var path = configuration["Kestrel:Certificates:Default:Path"];
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                configuration["Kestrel:Certificates:Default:Password"]);
            return Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not add the server certificate pin to an enrollment token.");
            return null;
        }
    }
}
