using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Server;

public static class EnrollmentCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var values = Parse(args);
        if (!values.TryGetValue("server", out var server) || !values.TryGetValue("token", out var token))
        {
            Console.Error.WriteLine("Usage: KidTime.ControlService.exe enroll --server https://host:5081 --token <one-time-token> [--pin <certificate-sha256>]");
            return 2;
        }

        var serverUri = new Uri(server);
        if (serverUri.Scheme != Uri.UriSchemeHttps)
        {
            Console.Error.WriteLine("Enrollment requires an HTTPS server URL.");
            return 2;
        }

        var options = new AgentOptions
        {
            ServerUrl = server.TrimEnd('/'),
            PinnedServerCertificateSha256 = values.GetValueOrDefault("pin")
        };
        try
        {
            using var client = new HttpClient(CertificateValidation.CreateHandler(options))
            {
                BaseAddress = new Uri(options.ServerUrl + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            var request = new DeviceEnrollmentRequest(
                token,
                Environment.MachineName,
                Environment.OSVersion.VersionString,
                TimeZoneInfo.Local.Id);
            using var response = await client.PostAsJsonAsync("api/agent/enroll", request, JsonOptions);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Enrollment failed ({(int)response.StatusCode}).");
                return 1;
            }

            var enrolled = await response.Content.ReadFromJsonAsync<DeviceEnrollmentResponse>(JsonOptions)
                ?? throw new InvalidDataException("Server returned an empty enrollment response.");
            AgentPaths.EnsureDirectories();
            new CredentialStore().Save(new DeviceCredential(enrolled.DeviceId, enrolled.DeviceToken));
            var configuration = JsonSerializer.Serialize(new { Agent = options }, JsonOptions);
            await File.WriteAllTextAsync(AgentPaths.ConfigurationFile, configuration);
            var store = new LocalStore();
            await store.InitializeAsync(CancellationToken.None);
            await store.SaveRulesAsync(enrolled.Rules, CancellationToken.None);
            Console.WriteLine($"Enrolled {Environment.MachineName} as device {enrolled.DeviceId}.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Enrollment failed: {exception.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
                values[args[index][2..]] = args[index + 1];
        }
        return values;
    }
}
