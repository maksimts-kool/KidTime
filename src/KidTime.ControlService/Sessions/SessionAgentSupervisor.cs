using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KidTime.Domain.Contracts;
using Microsoft.Win32.SafeHandles;

namespace KidTime.ControlService.Sessions;

public sealed class SessionAgentSupervisor(
    Enforcement.EnforcementCoordinator coordinator,
    PcSignOutState signOutState,
    ILogger<SessionAgentSupervisor> logger) : BackgroundService
{
    /// <summary>
    /// A launch that dies faster than this never reached the child's screen. The agent takes a
    /// second or two to build its window and register its tray icon, so anything shorter is a
    /// start-up failure rather than a session ending.
    /// </summary>
    private static readonly TimeSpan HealthyLifetime = TimeSpan.FromSeconds(15);

    /// <summary>How many of those in a row before the parent is told. Ten seconds of looping.</summary>
    private const int CrashLoopLaunches = 5;

    /// <summary>
    /// The longest the loop stands down after the agent reports Windows ending its session. It
    /// normally ends sooner, when the session loses its user; the cap only matters if the child
    /// cancels a shutdown, and then they get their tray agent back.
    /// </summary>
    private static readonly TimeSpan SessionEndingWindow = TimeSpan.FromSeconds(60);

    private const uint StillActive = 259;
    private const uint WaitTimeout = 0x102;

    private int _agentProcessId = -1;
    private SafeProcessHandle? _agent;
    private long _sessionEndedTimestamp;
    private long _launchedTimestamp;
    private int _shortLivedLaunches;
    private bool _crashLoopReported;

    public int AgentProcessId => Volatile.Read(ref _agentProcessId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Once per service start, which covers a fresh install, every automatic update, and every
        // reboot - and puts the entries back if they are ever deleted. See AgentShortcuts for why
        // this is the service's job rather than setup's.
        AgentShortcuts.Ensure(ResolveExecutable(), logger);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var activeUser = WindowsSession.GetActiveUser();
            if (activeUser is null || !WindowsSession.IsActiveUserControlled(coordinator.Rules))
            {
                StopAgent();
                ForgetAgent();
                // Signing out is not a crash, so the run of short lives starts over.
                ResetCrashLoop();
                _sessionEndedTimestamp = 0;
                continue;
            }
            var activeSessionId = activeUser.SessionId;
            if (IsAgentHealthy((int)activeSessionId)) continue;
            NoteSessionEnded();

            // A session that is signing out or otherwise not running its desktop kills whatever is
            // started in it. Those deaths are the system doing its job - a forced sign-out is the
            // ordinary end of a blocked session, and the child restarting the PC the ordinary end
            // of any other - so the loop neither relaunches into them nor counts them towards a
            // crash loop the parent would be asked to act on.
            if (signOutState.IsSigningOut || IsSessionEnding || !WindowsSession.IsSessionActive(activeSessionId))
            {
                ForgetAgent();
                ResetCrashLoop();
                _launchedTimestamp = 0;
                continue;
            }

            NoteAgentExited();
            ForgetAgent();
            try
            {
                // The creation handle keeps the exit code readable after the process is gone, which
                // is the one fact that says why a start-up failure failed. Reopening the process by
                // id cannot: an agent that dies within milliseconds is gone before it is opened.
                _agent = Launch(activeSessionId, out var processId);
                Volatile.Write(ref _agentProcessId, processId);
                _launchedTimestamp = Stopwatch.GetTimestamp();
                logger.LogInformation("SessionAgent started with a hardened process ACL in session {SessionId} as process {ProcessId}.", activeSessionId, processId);
                // The coordinator decides whether this is a sign-in the child's window should
                // open for; every relaunch into the same session asks and is told no.
                await coordinator.NoteAgentLaunchedAsync(activeSessionId, stoppingToken);
            }
            catch (Exception exception)
            {
                _launchedTimestamp = 0;
                logger.LogWarning(exception, "SessionAgent could not be started in session {SessionId}.", activeSessionId);
            }
        }
    }

    /// <summary>
    /// Counts a launch that has just ended, and reports a crash loop once per episode.
    ///
    /// A tray agent that dies before it finishes starting cannot deliver its own fault - it never
    /// reaches an exchange - so without this the child sits with no UI and no notifications while
    /// the parent's error log stays empty. The service is the only component still running to say
    /// so, and it says it as an error, which is what carries it to the panel.
    /// </summary>
    private void NoteAgentExited()
    {
        if (_launchedTimestamp == 0) return;
        var lifetime = Stopwatch.GetElapsedTime(_launchedTimestamp);
        _launchedTimestamp = 0;
        if (lifetime >= HealthyLifetime)
        {
            ResetCrashLoop();
            return;
        }

        _shortLivedLaunches++;
        if (_shortLivedLaunches < CrashLoopLaunches || _crashLoopReported) return;
        _crashLoopReported = true;
        logger.LogError(
            "SessionAgent has exited within {LifetimeSeconds:F0}s of starting {Attempts} times in a row (last exit code {ExitCode}); the controlled user has no KidTime interface or notifications. Enforcement is unaffected.",
            lifetime.TotalSeconds,
            _shortLivedLaunches,
            DescribeExitCode());
    }

    /// <summary>
    /// The last agent's exit code as the panel should read it. 0xE0434352 is a .NET exception that
    /// escaped, which is the difference between "the agent crashed" and "something killed it".
    /// </summary>
    private string DescribeExitCode()
    {
        if (ReadExitCode() is not { } code) return "unknown";
        return code == unchecked((int)0xE0434352)
            ? "0xE0434352 (unhandled .NET exception)"
            : $"0x{code:X8}";
    }

    private int? ReadExitCode() =>
        _agent is { IsInvalid: false } handle && GetExitCodeProcess(handle, out var code) && code != StillActive
            ? unchecked((int)code)
            : null;

    /// <summary>
    /// The agent exits with <see cref="SessionAgentExitCodes.SessionEnded"/> when Windows ends the
    /// session. That session still answers for its user for several seconds afterwards and every
    /// copy launched into it dies at once, so the loop stands down until the user is gone.
    /// </summary>
    private void NoteSessionEnded()
    {
        if (ReadExitCode() != SessionAgentExitCodes.SessionEnded) return;
        _sessionEndedTimestamp = Stopwatch.GetTimestamp();
        logger.LogInformation("SessionAgent exited because Windows is ending the session; it is not relaunched into it.");
    }

    private bool IsSessionEnding =>
        _sessionEndedTimestamp != 0 && Stopwatch.GetElapsedTime(_sessionEndedTimestamp) < SessionEndingWindow;

    private void ForgetAgent()
    {
        Volatile.Write(ref _agentProcessId, -1);
        _agent?.Dispose();
        _agent = null;
    }

    private void ResetCrashLoop()
    {
        if (_shortLivedLaunches == 0 && !_crashLoopReported) return;
        if (_crashLoopReported)
            logger.LogInformation("SessionAgent is staying up again after {Attempts} failed starts.", _shortLivedLaunches);
        _shortLivedLaunches = 0;
        _crashLoopReported = false;
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
            // The handle is asked rather than the id: while it is held the id cannot be reused by
            // something else, so the session check below still describes the agent.
            if (_agent is not { IsInvalid: false } handle || WaitForSingleObject(handle, 0) != WaitTimeout) return false;
            using var process = Process.GetProcessById(processId);
            return process.SessionId == sessionId;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    /// <summary>
    /// Where the agent lives: beside the service in a published layout, next to it in a flat one.
    /// The shortcuts point at the same answer, so a child's desktop icon cannot come to name a
    /// different executable from the one this service supervises.
    /// </summary>
    internal static string ResolveExecutable()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "SessionAgent", "KidTime.SessionAgent.exe");
        return File.Exists(executable)
            ? executable
            : Path.Combine(AppContext.BaseDirectory, "KidTime.SessionAgent.exe");
    }

    private static SafeProcessHandle Launch(uint sessionId, out int processId)
    {
        var executable = ResolveExecutable();
        if (!File.Exists(executable)) throw new FileNotFoundException("SessionAgent executable is missing.", executable);

        if (Environment.UserInteractive && Process.GetCurrentProcess().SessionId == sessionId)
        {
            using var started = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true })
                ?? throw new InvalidOperationException("Process.Start returned no process.");
            processId = started.Id;
            var opened = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, processId);
            return opened.IsInvalid ? throw new Win32Exception(Marshal.GetLastWin32Error()) : opened;
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
                CloseHandle(processInfo.Thread);
                var process = new SafeProcessHandle(processInfo.Process, ownsHandle: true);
                try
                {
                    HardenProcessAccess(processInfo.Process);
                    processId = processInfo.ProcessId;
                    return process;
                }
                catch
                {
                    process.Dispose();
                    throw;
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

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
