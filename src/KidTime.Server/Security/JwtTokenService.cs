using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using KidTime.Server.Data;
using Microsoft.IdentityModel.Tokens;

namespace KidTime.Server.Security;

public sealed class JwtTokenService(IConfiguration configuration, TimeProvider timeProvider)
{
    public string Create(ParentUser user)
    {
        var signingKey = configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
        var now = timeProvider.GetUtcNow();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"] ?? "KidTime",
            audience: configuration["Jwt:Audience"] ?? "KidTime.Web",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Name, user.Email)
            ],
            notBefore: now.UtcDateTime,
            expires: now.AddHours(12).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
