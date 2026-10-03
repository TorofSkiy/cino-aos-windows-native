using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Cino.Native;

public sealed record HostConfig(string ManagementUrl = "", string EnrollmentTokenFile = "",
    string DisplayName = "CINO-AOS Windows Host", bool AllowLoopbackForTesting = false,
    string ManagementRootCertificateFile = "", string ManagementRootCertificateSha256 = "",
    string BridgeTokenFile = "", string UpdateInbox = "", string ReleasePublicKeyFile = "");
public sealed record PendingRequest(string Kind, string Method, string Path, string Body, string Key);
public sealed class HostState
{
    public string ExternalId { get; set; } = "windows_" + Guid.NewGuid().ToString("N");
    public string ManagementUrl { get; set; } = "";
    public string? HostId { get; set; }
    public string? HostToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long Sequence { get; set; }
    public PendingRequest? Pending { get; set; }
    public PendingRequest? PendingClaim { get; set; }
    public PendingRequest? PendingTaskResult { get; set; }
    public JsonElement? ActiveTask { get; set; }
}

public sealed class StateStore : IDisposable
{
    readonly string path;
    readonly FileStream instanceLock;
    public HostState Value { get; }
    public string DirectoryPath { get; }

    public StateStore(string directory)
    {
        directory = Path.GetFullPath(directory);
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("state_reparse_point");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
        instanceLock = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        path = Path.Combine(directory, "host-state.json");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("state_file_reparse_point");
        Value = File.Exists(path)
            ? JsonSerializer.Deserialize<HostState>(File.ReadAllText(path)) ?? throw new InvalidOperationException("invalid_state")
            : new HostState();
        Save();
    }

    public void Save()
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, Value);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    public void Dispose() => instanceLock.Dispose();
}
