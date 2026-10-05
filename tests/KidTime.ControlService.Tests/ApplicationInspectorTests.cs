using System.Diagnostics;
using System.Runtime.InteropServices;
using KidTime.ControlService.Enforcement;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidTime.ControlService.Tests;

public sealed class ApplicationInspectorTests
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;

    private static ApplicationInspector CreateInspector() => new(
        new ApplicationIconExtractor(NullLogger<ApplicationIconExtractor>.Instance),
        NullLogger<ApplicationInspector>.Instance);

    [Fact]
    public void A_process_that_has_not_started_running_is_already_identified()
    {
        // Steam starts its games suspended to inject the overlay. The loader has not run in a
        // process like that, so anything that reads its module list fails - and a game the
        // service could not identify at launch used to run past its limit unclosed.
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        Assert.True(CreateProcess(executable, null, IntPtr.Zero, IntPtr.Zero, false,
            CreateSuspended | CreateNoWindow, IntPtr.Zero, null, ref startup, out var created));
        try
        {
            using var process = Process.GetProcessById(created.ProcessId);

            var descriptor = CreateInspector().Inspect(process);

            Assert.NotNull(descriptor);
            Assert.Equal(executable, descriptor.ExecutablePath, ignoreCase: true);
            Assert.Equal("cmd.exe", descriptor.ExecutableName, ignoreCase: true);
        }
        finally
        {
            TerminateProcess(created.Process, 1);
            CloseHandle(created.Thread);
            CloseHandle(created.Process);
        }
    }

    [Fact]
    public void A_running_process_is_identified_by_its_own_executable()
    {
        using var process = Process.GetCurrentProcess();

        var descriptor = CreateInspector().Inspect(process);

        Assert.NotNull(descriptor);
        Assert.Equal(Environment.ProcessPath, descriptor.ExecutablePath, ignoreCase: true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, string? commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
