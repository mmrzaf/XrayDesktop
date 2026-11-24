using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using XrayDesktop.Storage;

namespace XrayDesktop.Windows;

internal sealed record ProxyJournal(int Version, Guid Id, string UserSid, DateTimeOffset CreatedUtc, ProxySettings Original, ProxySettings Applied, string Stage);

internal interface IProxyJournalStore
{
    ProxyJournal? Load();
    void Create(ProxyJournal journal);
    void Delete();
    string UserSid { get; }
}

internal sealed class ProxyRecovery(string directory, string userSid) : IProxyJournalStore
{
    internal static ProxyRecovery ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Cannot determine proxy recovery user.");
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new InvalidOperationException("Cannot locate per-user proxy recovery storage.");
        return new(Path.Combine(root, "XrayDesktop", "ProxyRecovery"), sid);
    }
    public string UserSid => userSid;
    private string JournalPath => Path.Combine(directory, "pending.dpapi");
    public ProxyJournal? Load()
    {
        byte[] encrypted;
        try { encrypted = AtomicFile.ReadBounded(JournalPath, 1024 * 1024); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var plain = UserDataProtection.Transform(encrypted, protect: false);
        try
        {
            var journal = JsonSerializer.Deserialize<ProxyJournal>(plain) ?? throw new InvalidDataException("Empty recovery journal.");
            Validate(journal); return journal;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Create(ProxyJournal journal)
    {
        Validate(journal);
        var plain = JsonSerializer.SerializeToUtf8Bytes(journal);
        byte[] encrypted;
        try { encrypted = UserDataProtection.Transform(plain, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(encrypted); stream.Flush(flushToDisk: true); }
            File.Move(temporary, JournalPath, overwrite: false); // never replace unresolved recovery
        }
        finally { File.Delete(temporary); }
    }
    public void Delete() => File.Delete(JournalPath);
    private void Validate(ProxyJournal journal)
    {
        if (journal.Version != 1 || journal.Id == Guid.Empty || journal.UserSid != UserSid || journal.CreatedUtc == default || journal.Stage != "prepared" || journal.Original is null || journal.Applied is null || journal.Original == journal.Applied)
            throw new InvalidDataException("Unsupported or invalid proxy recovery journal.");
        journal.Original.Validate(); journal.Applied.Validate();
        if (journal.Applied.Flags != (ProxySettings.Direct | ProxySettings.Manual) || journal.Applied.Bypass != ProxySettings.ManagedBypass || journal.Applied.Pac != journal.Original.Pac ||
            !ProxySettings.IsManagedServer(journal.Applied.Server))
            throw new InvalidDataException("Recovery journal has an unsupported managed endpoint.");
    }
}

// DPAPI current-user scope, without a package or UI prompt. Unmanaged plaintext
// buffers are cleared before freeing. User-level DPAPI is not a sandbox.
internal static class UserDataProtection
{
    internal static byte[] Transform(byte[] input, bool protect)
    {
        var source = new Blob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var output = default(Blob);
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            var ok = protect ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not access current-user proxy recovery data.");
            if (output.Size is < 1 or > 1024 * 1024 || output.Data == IntPtr.Zero) throw new InvalidDataException("Invalid DPAPI output.");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Clear(source); Marshal.FreeHGlobal(source.Data);
            if (output.Data != IntPtr.Zero) { Clear(output); _ = LocalFree(output.Data); }
        }
    }
    private static void Clear(Blob blob)
    {
        if (blob.Data == IntPtr.Zero || blob.Size <= 0) return;
        for (var i = 0; i < blob.Size; i++) Marshal.WriteByte(blob.Data, i, 0);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { internal int Size; internal IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
