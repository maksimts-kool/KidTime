using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text;
using KidTime.Domain.Applications;

namespace KidTime.ControlService.Enforcement;

public sealed class ApplicationInspector(
    ApplicationIconExtractor iconExtractor,
    ILogger<ApplicationInspector> logger)
{
    public ApplicationDescriptor? Inspect(Process process, bool includeHash = false)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var descriptor = InspectPath(path, includeHash);
            return new ApplicationDescriptor
            {
                DisplayName = descriptor.DisplayName,
                ExecutableName = descriptor.ExecutableName,
                ExecutablePath = descriptor.ExecutablePath,
                ProductName = descriptor.ProductName,
                OriginalFilename = descriptor.OriginalFilename,
                Company = descriptor.Company,
                SignaturePublisher = descriptor.SignaturePublisher,
                FileVersion = descriptor.FileVersion,
                PackageFamilyName = ReadPackageFamilyName(process),
                Sha256 = descriptor.Sha256,
                IconPngBase64 = descriptor.IconPngBase64
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            logger.LogDebug(exception, "Could not inspect process {ProcessId}.", process.Id);
            return null;
        }
    }

    public ApplicationDescriptor InspectPath(string path, bool includeHash = false)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return iconExtractor.AddIcon(new ApplicationDescriptor
        {
            DisplayName = First(version.ProductName, Path.GetFileNameWithoutExtension(path)),
            ExecutableName = Path.GetFileName(path),
            ExecutablePath = path,
            ProductName = NullIfEmpty(version.ProductName),
            OriginalFilename = NullIfEmpty(version.OriginalFilename),
            Company = NullIfEmpty(version.CompanyName),
            SignaturePublisher = ReadSignaturePublisher(path),
            FileVersion = NullIfEmpty(version.FileVersion),
            Sha256 = includeHash ? ComputeSha256(path) : null
        });
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? ReadSignaturePublisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0026, SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0026, SYSLIB0057
            return certificate.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? ReadPackageFamilyName(Process process)
    {
        try
        {
            var length = 0;
            var result = GetPackageFamilyName(process.Handle, ref length, null);
            if (result != 122 || length <= 1) return null;
            var value = new StringBuilder(length);
            return GetPackageFamilyName(process.Handle, ref length, value) == 0 ? value.ToString() : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string First(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref int packageFamilyNameLength, StringBuilder? packageFamilyName);
}
