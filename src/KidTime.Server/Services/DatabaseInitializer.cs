using KidTime.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Services;

public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KidTimeDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ApplicationCatalogReconciler>()
            .ReconcileAsync(cancellationToken);

        if (await dbContext.ParentUsers.AnyAsync(cancellationToken)) return;

        var email = configuration["Admin:Email"];
        var password = configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("No parent account exists. Set Admin__Email and Admin__Password, then restart the server.");
            return;
        }

        var user = new ParentUser
        {
            Email = email.Trim(),
            NormalizedEmail = email.Trim().ToUpperInvariant(),
            PasswordHash = string.Empty
        };
        user.PasswordHash = new PasswordHasher<ParentUser>().HashPassword(user, password);
        dbContext.ParentUsers.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Created the initial parent account for {Email}.", user.Email);
    }
}
