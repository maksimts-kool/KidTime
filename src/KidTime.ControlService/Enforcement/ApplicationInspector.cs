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
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// What a running process is, or null when it cannot be told yet.
    ///
    /// The path comes from <c>QueryFullProcessImageName</c> on a limited-information handle, the
    /// same call the tray agent makes, and never from <see cref="Process.MainModule"/>. Reading
    /// the main module walks the process's loader list, which does not exist yet in a process
    /// that is still starting - and Steam starts its games suspended to inject the overlay. The
    /// agent then counted cs2 by its window while the service, which had failed to read the same
    /// process once, never looked at it again, and a two-hour limit ran on until the schedule
    /// closed the PC. The image name is known from the moment the process exists.
    /// </summary>
    public ApplicationDescriptor? Inspect(Process process, bool includeHash = false)
    {
        try
        {
            var path = ReadProcessPath(process.Id);
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
                PackageFamilyName = ReadPackageFamilyName(process.Id),
                Sha256 = descriptor.Sha256,
                IconPngBase64 = descriptor.IconPngBase64
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or NotSupportedException or IOException or UnauthorizedAccessException)
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
        catch (Exception exception) when (exception is CryptographicException or IOException
                                          or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadProcessPath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var capacity = 32_768;
            var value = new StringBuilder(capacity);
            return QueryFullProcessImageName(handle, 0, value, ref capacity) ? value.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private static string? ReadPackageFamilyName(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var length = 0;
            var result = GetPackageFamilyName(handle, ref length, null);
            if (result != 122 || length <= 1) return null;
            var value = new StringBuilder(length);
            return GetPackageFamilyName(handle, ref length, value) == 0 ? value.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private static string First(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref int packageFamilyNameLength, StringBuilder? packageFamilyName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder executableName, ref int size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
