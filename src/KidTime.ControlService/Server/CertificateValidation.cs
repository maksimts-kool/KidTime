using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KidTime.ControlService.Infrastructure;

namespace KidTime.ControlService.Server;

public static class CertificateValidation
{
    public static HttpClientHandler CreateHandler(AgentOptions options)
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            Validate(certificate, errors, options.PinnedServerCertificateSha256);
        return handler;
    }

    private static bool Validate(X509Certificate2? certificate, SslPolicyErrors errors, string? expectedPin)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || string.IsNullOrWhiteSpace(expectedPin)) return false;
        var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var expected = expectedPin.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual.ToUpperInvariant()),
            System.Text.Encoding.ASCII.GetBytes(expected.ToUpperInvariant()));
    }
}
