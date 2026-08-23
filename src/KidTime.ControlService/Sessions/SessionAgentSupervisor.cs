using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KidTime.ControlService.Sessions;

public sealed class SessionAgentSupervisor(
    Enforcement.EnforcementCoordinator coordinator,
    ILogger<SessionAgentSupervisor> logger) : BackgroundService
{
    private int _agentProcessId = -1;

    public int AgentProcessId => Volatile.Read(ref _agentProcessId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var activeUser = WindowsSession.GetActiveUser();
            if (activeUser is null || !WindowsSession.IsActiveUserControlled(coordinator.Rules))
            {
                StopAgent();
                Volatile.Write(ref _agentProcessId, -1);
                continue;
            }
            var activeSessionId = activeUser.SessionId;
            if (IsAgentHealthy((int)activeSessionId)) continue;
            Volatile.Write(ref _agentProcessId, -1);
            try
            {
                var processId = Launch(activeSessionId);
                Volatile.Write(ref _agentProcessId, processId);
                logger.LogInformation("SessionAgent started with a hardened process ACL in session {SessionId} as process {ProcessId}.", activeSessionId, processId);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "SessionAgent could not be started in session {SessionId}.", activeSessionId);
            }
        }
    }

    public bool IsTrustedAgentProcess(uint processId)
    {
        if (processId == 0 || processId != (uint)Volatile.Read(ref _agentProcessId)) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "SessionAgent", "KidTime.SessionAgent.exe"));
            var actual = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
            return !process.HasExited
                   && process.SessionId == WindowsSession.ActiveSessionId
                   && WindowsSession.IsActiveUserControlled(coordinator.Rules)
                   && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private void StopAgent()
    {
        var processId = Volatile.Read(ref _agentProcessId);
        if (processId <= 0) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill();
            logger.LogInformation("SessionAgent stopped because the active Windows account is not controlled.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            logger.LogDebug(exception, "SessionAgent {ProcessId} was already stopped.", processId);
        }
    }

    private bool IsAgentHealthy(int sessionId)
    {
        var processId = Volatile.Read(ref _agentProcessId);
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.SessionId == sessionId;
        }
        catch (ArgumentException) { return false; }
    }

    private static int Launch(uint sessionId)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "SessionAgent", "KidTime.SessionAgent.exe");
        if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "KidTime.SessionAgent.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("SessionAgent executable is missing.", executable);

        if (Environment.UserInteractive && Process.GetCurrentProcess().SessionId == sessionId)
        {
            return Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true })?.Id
                ?? throw new InvalidOperationException("Process.Start returned no process.");
        }

        if (!WTSQueryUserToken(sessionId, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!CreateEnvironmentBlock(out var environment, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                var commandLine = new StringBuilder($"\"{executable}\"");
                if (!CreateProcessAsUser(token, executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                        0x00000400, environment, Path.GetDirectoryName(executable), ref startup, out var processInfo))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    HardenProcessAccess(processInfo.Process);
                    return processInfo.ProcessId;
                }
                finally
                {
                    CloseHandle(processInfo.Thread);
                    CloseHandle(processInfo.Process);
                }
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
    }

    private static void HardenProcessAccess(IntPtr processHandle)
    {
        const string processSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x101410;;;IU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(processSddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!SetKernelObjectSecurity(processHandle, 0x00000004, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X; public int Y; public int XSize; public int YSize;
        public int XCountChars; public int YCountChars; public int FillAttribute; public int Flags;
        public short ShowWindow; public short Reserved2; public IntPtr ReservedPointer;
        public IntPtr StandardInput; public IntPtr StandardOutput; public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public int ProcessId; public int ThreadId; }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        IntPtr handle,
        uint securityInformation,
        IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
