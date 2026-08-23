using System.Text;
using System.Text.Json.Serialization;
using System.Security.Cryptography.X509Certificates;
using KidTime.Server.Data;
using KidTime.Server.Hubs;
using KidTime.Server.Security;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("KidTime")
    ?? throw new InvalidOperationException("ConnectionStrings:KidTime is not configured.");
var jwtKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 UTF-8 bytes.");
}

var dataProtectionCertificatePath = builder.Configuration["Kestrel:Certificates:Default:Path"];
var dataProtectionCertificatePassword = builder.Configuration["Kestrel:Certificates:Default:Password"];
if (!string.IsNullOrWhiteSpace(dataProtectionCertificatePath)
    && File.Exists(dataProtectionCertificatePath))
{
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(
        dataProtectionCertificatePath,
        dataProtectionCertificatePassword);
    builder.Services.AddDataProtection()
        .SetApplicationName("KidTime")
        .PersistKeysToFileSystem(new DirectoryInfo("/home/app/.aspnet/DataProtection-Keys"))
        .ProtectKeysWithCertificate(certificate);
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<KidTimeDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<RuleSnapshotFactory>();
builder.Services.AddScoped<ApplicationCatalogReconciler>();
builder.Services.AddSingleton<AgentUpdateCatalog>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "KidTime",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "KidTime.Web",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = System.Security.Claims.ClaimTypes.Name
        };
    })
    .AddScheme<AuthenticationSchemeOptions, DeviceAuthenticationHandler>(
        DeviceAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks().AddNpgSql(connectionString);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<DeviceHub>("/hubs/device");
app.MapHealthChecks("/health");

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
await app.RunAsync();

public partial class Program;
