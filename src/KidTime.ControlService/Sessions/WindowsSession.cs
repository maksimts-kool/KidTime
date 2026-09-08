using System.Runtime.InteropServices;
using System.Security.Principal;
using KidTime.Domain.Rules;
using Microsoft.Win32.SafeHandles;

namespace KidTime.ControlService.Sessions;

public static class WindowsSession
{
    public sealed record SessionUser(uint SessionId, string AccountName, string Sid);

    public static uint ActiveSessionId => WTSGetActiveConsoleSessionId();

    public static string? GetActiveUserName() => GetActiveUser()?.AccountName;

    public static SessionUser? GetActiveUser()
    {
        var sessionId = ActiveSessionId;
        if (sessionId == uint.MaxValue) return null;
        var user = Query(sessionId, WtsInfoClass.UserName);
        if (string.IsNullOrWhiteSpace(user)) return null;
        var domain = Query(sessionId, WtsInfoClass.DomainName);
        var accountName = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
        var sid = ReadSessionSid(sessionId, accountName);
        return string.IsNullOrWhiteSpace(sid) ? null : new SessionUser(sessionId, accountName, sid);
    }

    public static bool IsActiveUserControlled(DeviceRuleSnapshot rules) => IsUserControlled(rules, GetActiveUser());

    public static bool IsUserControlled(DeviceRuleSnapshot rules, SessionUser? user) =>
        user is not null
        && !string.IsNullOrWhiteSpace(rules.ControlledUserSid)
        && string.Equals(user.Sid, rules.ControlledUserSid, StringComparison.OrdinalIgnoreCase);

    public static bool TryLogoff(uint sessionId) => WTSLogoffSession(IntPtr.Zero, sessionId, wait: false);

    /// <summary>
    /// Whether the session is connected and running its desktop. A session that is signing out,
    /// disconnecting, or coming up is still reported by <see cref="ActiveSessionId"/> and still
    /// answers for its user, but nothing started in it survives - so this is what tells an
    /// ordinary transition apart from an agent that cannot start.
    ///
    /// A session that cannot be asked is treated as active. This gates whether the tray agent is
    /// launched at all, and a machine where the query behaves unexpectedly should get a child with
    /// an interface and a false crash-loop report rather than a child with no interface at all.
    /// </summary>
    public static bool IsSessionActive(uint sessionId)
    {
        if (sessionId == uint.MaxValue) return false;
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.ConnectState, out var buffer, out var bytes)
            || buffer == IntPtr.Zero)
            return true;
        try { return bytes < sizeof(int) || Marshal.ReadInt32(buffer) == WtsActive; }
        finally { WTSFreeMemory(buffer); }
    }

    private const int WtsActive = 0;

    private static string? Query(uint sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _) || buffer == IntPtr.Zero)
            return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    private static string? ReadSessionSid(uint sessionId, string accountName)
    {
        if (WTSQueryUserToken(sessionId, out var token))
        {
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                return identity.User?.Value;
        }

        try { return ((SecurityIdentifier)new NTAccount(accountName).Translate(typeof(SecurityIdentifier))).Value; }
        catch (IdentityNotMappedException) { return null; }
    }

    private enum WtsInfoClass { UserName = 5, DomainName = 7, ConnectState = 8 }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        uint sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(
        IntPtr server,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);
}
