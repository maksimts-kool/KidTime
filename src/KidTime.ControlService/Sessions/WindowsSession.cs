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

    private enum WtsInfoClass { UserName = 5, DomainName = 7 }

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
