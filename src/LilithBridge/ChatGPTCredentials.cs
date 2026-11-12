using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace LilithBridge;

// Only credentials issued to this installation of LilithBridge. No import path,
// environment-token fallback, browser cookies, or Codex/ChatGPT credential lookup.
internal sealed class ChatGPTProfile
{
    public string ClientId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Email { get; set; } = "";
    public string Binding { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset EarliestRefreshAt { get; set; }
    public bool WelcomeShown { get; set; }
}
internal sealed class ChatGPTAccounts
{
    public string HostId { get; set; } = "";
    public string ActiveClient { get; set; } = "";
    public string PendingClient { get; set; } = "";
    public List<ChatGPTProfile> Profiles { get; set; } = new();
    internal ChatGPTProfile? Active => Profiles.FirstOrDefault(p => p.ClientId == ActiveClient);
}
internal sealed record ChatGPTConnection(bool Connected, bool Sharing, string Email, string Binding);
internal static class ChatGPTCredentials
{
    internal static string DirectoryFor(string root) => Path.Combine(root, "chatgpt-signin");
    internal static string PathFor(string root) => Path.Combine(DirectoryFor(root), "accounts.dpapi");
    private static void PrepareDirectory(string root)
    {
        string directory=DirectoryFor(root);DataPaths.EnsureNoReparseAncestors(Path.GetFullPath(directory));
        Directory.CreateDirectory(directory);
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("ChatGPT credentials require current-user Windows protection");
        var owner=WindowsIdentity.GetCurrent().User??throw new InvalidOperationException("Missing Windows user");
        var access=new DirectorySecurity();access.SetAccessRuleProtection(true,false);
        access.AddAccessRule(new FileSystemAccessRule(owner,FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(access);
    }
    private static void CheckFile(string path)
    {
        DataPaths.EnsureNoReparseAncestors(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if(File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Credential path is a reparse point");
    }
    internal static FileStream Lock(string root)
    {
        string directory = DirectoryFor(root);
        DataPaths.EnsureNoReparseAncestors(Path.GetFullPath(directory));
        PrepareDirectory(root);
        // A second runtime must not race a rotating refresh token. Fail closed;
        // the user can retry the operation after the other runtime releases it.
        string path=Path.Combine(directory, "session.lock");CheckFile(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal static ChatGPTAccounts Read(string root)
    {
        string path = PathFor(root);
        CheckFile(path);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("ChatGPT credential record too large");
        byte[] plain = OpenAICredentialStore.Transform(File.ReadAllBytes(path), false);
        try { return JsonSerializer.Deserialize<ChatGPTAccounts>(plain) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static void Save(string root, ChatGPTAccounts accounts)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(accounts);
        try
        {
            byte[] encrypted = OpenAICredentialStore.Transform(plain, true);
            string path = PathFor(root), pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            CheckFile(path);
            PrepareDirectory(root);
            try
            {
                File.WriteAllBytes(pending, encrypted);
                if (File.Exists(path)) File.Replace(pending, path, null); else File.Move(pending, path);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static ChatGPTConnection Describe(string root)
    {
        try
        {
            var p = Read(root).Active;
            bool connected = p != null && p.AccessToken.Length > 0 && p.Subject.Length > 0;
            bool sharing = connected && p!.Scopes.Contains(ChatGPTAuth.SharingScope, StringComparer.Ordinal);
            return new(connected, sharing, connected ? p!.Email : "", sharing ? p!.Binding : "");
        }
        catch { return new(false, false, "", ""); }
    }
}
