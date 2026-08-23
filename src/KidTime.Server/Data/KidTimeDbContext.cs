using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Data;

public sealed class KidTimeDbContext(DbContextOptions<KidTimeDbContext> options) : DbContext(options)
{
    public DbSet<ParentUser> ParentUsers => Set<ParentUser>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<DeviceRule> DeviceRules => Set<DeviceRule>();
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<DeviceApplication> DeviceApplications => Set<DeviceApplication>();
    public DbSet<ApplicationRule> ApplicationRules => Set<ApplicationRule>();
    public DbSet<DailyDeviceUsage> DailyDeviceUsages => Set<DailyDeviceUsage>();
    public DbSet<DailyApplicationUsage> DailyApplicationUsages => Set<DailyApplicationUsage>();
    public DbSet<ProcessedUsageBatch> ProcessedUsageBatches => Set<ProcessedUsageBatch>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ParentUser>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(320);
            entity.Property(x => x.PasswordHash).HasMaxLength(1024);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(255);
            entity.Property(x => x.WindowsVersion).HasMaxLength(255);
            entity.Property(x => x.TimeZoneId).HasMaxLength(100);
            entity.Property(x => x.LoggedInUser).HasMaxLength(255);
            entity.Property(x => x.ForegroundApplication).HasMaxLength(255);
            entity.Property(x => x.ForegroundIdentityKey).HasMaxLength(64);
            entity.Property(x => x.WindowsUsersJson).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            entity.Property(x => x.AgentVersion).HasMaxLength(50);
            entity.Property(x => x.AgentUpdateStatus).HasMaxLength(50);
            entity.Property(x => x.AgentUpdateError).HasMaxLength(1000);
            entity.HasOne(x => x.Rule).WithOne(x => x.Device)
                .HasForeignKey<DeviceRule>(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeviceCredential>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DeviceId, x.TokenHash });
            entity.Property(x => x.TokenHash).HasMaxLength(64);
        });

        modelBuilder.Entity<EnrollmentToken>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.TokenHash).HasMaxLength(64);
        });

        modelBuilder.Entity<DeviceRule>(entity =>
        {
            entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.ControlledUserSid).HasMaxLength(184);
            entity.Property(x => x.ControlledUserName).HasMaxLength(255);
            entity.Property(x => x.ScheduleJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Application>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.IdentityKey).IsUnique();
            entity.Property(x => x.IdentityKey).HasMaxLength(64);
            entity.Property(x => x.DisplayName).HasMaxLength(255);
            entity.Property(x => x.ExecutableName).HasMaxLength(255);
            entity.Property(x => x.ProductName).HasMaxLength(255);
            entity.Property(x => x.OriginalFilename).HasMaxLength(255);
            entity.Property(x => x.Company).HasMaxLength(255);
            entity.Property(x => x.SignaturePublisher).HasMaxLength(512);
            entity.Property(x => x.PackageFamilyName).HasMaxLength(255);
        });

        modelBuilder.Entity<DeviceApplication>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DeviceId, x.ApplicationId }).IsUnique();
            entity.Property(x => x.ExecutablePath).HasMaxLength(2048);
            entity.Property(x => x.FileVersion).HasMaxLength(100);
            entity.Property(x => x.Sha256).HasMaxLength(64);
            entity.Property(x => x.IconPng).HasColumnType("bytea");
            entity.HasOne(x => x.Rule).WithOne(x => x.DeviceApplication)
                .HasForeignKey<ApplicationRule>(x => x.DeviceApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApplicationRule>(entity =>
        {
            entity.HasKey(x => x.DeviceApplicationId);
            entity.Property(x => x.ScheduleJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<DailyDeviceUsage>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DeviceId, x.LocalDate }).IsUnique();
        });

        modelBuilder.Entity<DailyApplicationUsage>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DeviceApplicationId, x.LocalDate }).IsUnique();
        });

        modelBuilder.Entity<ProcessedUsageBatch>(entity => entity.HasKey(x => x.BatchId));

        modelBuilder.Entity<DeviceCommand>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DeviceId, x.AcknowledgedAtUtc });
            entity.Property(x => x.Type).HasMaxLength(50);
        });
    }
}
