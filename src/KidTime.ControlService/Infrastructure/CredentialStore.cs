using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace KidTime.ControlService.Infrastructure;

public sealed record DeviceCredential(Guid DeviceId, string Token);

public sealed class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KidTime.DeviceCredential.v1");

    public DeviceCredential? Load()
    {
        if (!File.Exists(AgentPaths.CredentialFile)) return null;
        var protectedBytes = File.ReadAllBytes(AgentPaths.CredentialFile);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        return JsonSerializer.Deserialize<DeviceCredential>(bytes);
    }

    public void Save(DeviceCredential credential)
    {
        AgentPaths.EnsureDirectories();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credential);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(AgentPaths.CredentialFile, protectedBytes);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(AgentPaths.CredentialFile).SetAccessControl(security);
    }
}
