using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KidTime.Domain.Applications;

namespace KidTime.SessionAgent;

internal static class ForegroundDetector
{
    public static (int ProcessId, string? WindowTitle, ApplicationDescriptor? Application) GetForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return (0, null, null);
        GetWindowThreadProcessId(window, out var processId);
        var title = ReadWindowTitle(window);
        processId = ResolveApplicationProcess(window, processId);
        try
        {
            var path = ReadProcessPath(processId);
            if (string.IsNullOrWhiteSpace(path)) return ((int)processId, title, null);
            var version = FileVersionInfo.GetVersionInfo(path);
            return ((int)processId, title, new ApplicationDescriptor
            {
                DisplayName = First(version.ProductName, Path.GetFileNameWithoutExtension(path)),
                ExecutableName = Path.GetFileName(path),
                ExecutablePath = path,
                ProductName = NullIfEmpty(version.ProductName),
                OriginalFilename = NullIfEmpty(version.OriginalFilename),
                Company = NullIfEmpty(version.CompanyName),
                SignaturePublisher = ReadSignaturePublisher(path),
                FileVersion = NullIfEmpty(version.FileVersion),
                PackageFamilyName = ReadPackageFamilyName(processId)
            });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ((int)processId, title, null);
        }
    }

    private static uint ResolveApplicationProcess(IntPtr window, uint ownerProcessId)
    {
        var ownerPath = ReadProcessPath(ownerProcessId);
        if (!string.Equals(Path.GetFileName(ownerPath), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return ownerProcessId;

        var candidates = new List<uint>();
        EnumChildWindows(window, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childProcessId);
            if (childProcessId != 0 && childProcessId != ownerProcessId) candidates.Add(childProcessId);
            return true;
        }, IntPtr.Zero);
        return candidates.Distinct().FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(ReadProcessPath(candidate))
            && !string.IsNullOrWhiteSpace(ReadPackageFamilyName(candidate)), ownerProcessId);
    }

    public static int GetIdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var now = unchecked((uint)Environment.TickCount);
        return (int)(unchecked(now - info.Time) / 1000);
    }

    private static string? ReadWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0) return null;
        var builder = new StringBuilder(length + 1);
        return GetWindowText(window, builder, builder.Capacity) > 0 ? builder.ToString() : null;
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
        catch (CryptographicException) { return null; }
    }

    private static string? ReadProcessPath(uint processId)
    {
        var handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var capacity = 32_768;
            var value = new StringBuilder(capacity);
            return QueryFullProcessImageName(handle, 0, value, ref capacity) ? value.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private static string? ReadPackageFamilyName(uint processId)
    {
        var handle = OpenProcess(0x1000, false, processId);
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

    private static string First(params string?[] values) => values.First(value => !string.IsNullOrWhiteSpace(value))!;
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
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
