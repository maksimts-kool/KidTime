using KidTime.Server.Data;
using KidTime.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KidTime.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/enrollment-tokens")]
public sealed class EnrollmentController(KidTimeDbContext dbContext, TimeProvider timeProvider) : ControllerBase
{
    public sealed record CreateTokenRequest(int ValidForMinutes = 30);

    [HttpPost]
    public async Task<IActionResult> Create(CreateTokenRequest request, CancellationToken cancellationToken)
    {
        var minutes = Math.Clamp(request.ValidForMinutes, 5, 1_440);
        var token = TokenUtilities.Generate();
        var now = timeProvider.GetUtcNow();
        dbContext.EnrollmentTokens.Add(new EnrollmentToken
        {
            TokenHash = TokenUtilities.Hash(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(minutes)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { token, expiresAtUtc = now.AddMinutes(minutes) });
    }
}
