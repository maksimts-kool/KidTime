using System.Security.Claims;
using System.Text.Encodings.Web;
using KidTime.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KidTime.Server.Security;

public static class DeviceAuthenticationDefaults
{
    public const string Scheme = "DeviceToken";
}

public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    KidTimeDbContext dbContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var deviceIdText = Request.Headers["X-Device-Id"].FirstOrDefault();
        var token = Request.Headers["X-Device-Token"].FirstOrDefault();

        if (!Guid.TryParse(deviceIdText, out var deviceId) || string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var tokenHash = TokenUtilities.Hash(token);
        var valid = await dbContext.DeviceCredentials.AsNoTracking().AnyAsync(
            credential => credential.DeviceId == deviceId &&
                          credential.TokenHash == tokenHash &&
                          credential.RevokedAtUtc == null,
            Context.RequestAborted);
        if (!valid)
        {
            return AuthenticateResult.Fail("Invalid device credentials.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, deviceId.ToString()),
            new Claim(ClaimTypes.Name, deviceId.ToString()),
            new Claim("credential_type", "device")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
