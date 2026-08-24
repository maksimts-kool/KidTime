using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace KidTime.Setup;

public sealed record LocalAccount(string Sid, string AccountName, string DisplayName)
{
    public string Label => $"{DisplayName}  ({AccountName})";
}

public static class LocalAccountDiscovery
{
    private const int Success = 0;
    private const int MoreData = 234;
    private const uint FilterNormalAccount = 2;
    private const uint AccountDisabled = 0x0002;

    public static IReadOnlyList<LocalAccount> GetStandardUsers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var administratorSids = GetAdministratorSids();
        var result = new List<LocalAccount>();
        nint buffer = 0;
        var resume = 0;
        try
        {
            int status;
            do
            {
                status = NetUserEnum(null, 1, FilterNormalAccount, out buffer, -1, out var entriesRead, out _, ref resume);
                if (status is not Success and not MoreData) throw new Win32Exception(status);
                var size = Marshal.SizeOf<UserInfo1>();
                for (var index = 0; index < entriesRead; index++)
                {
                    var entry = Marshal.PtrToStructure<UserInfo1>(buffer + index * size);
                    var name = Marshal.PtrToStringUni(entry.Name);
                    if (string.IsNullOrWhiteSpace(name) || (entry.Flags & AccountDisabled) != 0) continue;
                    var accountName = $"{Environment.MachineName}\\{name}";
                    SecurityIdentifier sid;
                    try { sid = (SecurityIdentifier)new NTAccount(accountName).Translate(typeof(SecurityIdentifier)); }
                    catch (IdentityNotMappedException) { continue; }
                    if (administratorSids.Contains(sid.Value)) continue;
                    result.Add(new LocalAccount(sid.Value, accountName, name));
                }
                if (buffer != 0)
                {
                    NetApiBufferFree(buffer);
                    buffer = 0;
                }
            } while (status == MoreData);
        }
        finally
        {
            if (buffer != 0) NetApiBufferFree(buffer);
        }

        return result.OrderBy(account => account.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static HashSet<string> GetAdministratorSids()
    {
        var administratorAccount = (NTAccount)new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null).Translate(typeof(NTAccount));
        var separator = administratorAccount.Value.LastIndexOf('\\');
        var groupName = separator >= 0 ? administratorAccount.Value[(separator + 1)..] : administratorAccount.Value;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        nint buffer = 0;
        try
        {
            var status = NetLocalGroupGetMembers(null, groupName, 0, out buffer, -1, out var entriesRead, out _, IntPtr.Zero);
            if (status != Success) throw new Win32Exception(status);
            var size = Marshal.SizeOf<LocalGroupMemberInfo0>();
            for (var index = 0; index < entriesRead; index++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupMemberInfo0>(buffer + index * size);
                if (entry.Sid != 0) result.Add(new SecurityIdentifier(entry.Sid).Value);
            }
            return result;
        }
        finally
        {
            if (buffer != 0) NetApiBufferFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo1
    {
        public nint Name;
        public nint Password;
        public uint PasswordAge;
        public uint Privilege;
        public nint HomeDirectory;
        public nint Comment;
        public uint Flags;
        public nint ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMemberInfo0
    {
        public nint Sid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetUserEnum(
        string? serverName,
        int level,
        uint filter,
        out nint buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        ref int resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetLocalGroupGetMembers(
        string? serverName,
        string localGroupName,
        int level,
        out nint buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        nint resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(nint buffer);
}
