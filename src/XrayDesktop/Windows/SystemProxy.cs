using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XrayDesktop.Windows;

// The editable LAN options, not transient WPAD cache state. Null native strings
// and empty strings are equivalent for these WinINet options.
internal sealed record ProxySettings(uint Flags, string Server, string Bypass, string Pac)
{
    internal const uint Direct = 1, Manual = 2, AutoUrl = 4, AutoDetect = 8;
    internal const string ManagedBypass = "<local>;localhost;127.0.0.1;[::1]";
    internal static bool IsManagedServer(string server)
    {
        var prefix = server.StartsWith("127.0.0.1:", StringComparison.Ordinal) ? "127.0.0.1:" :
            server.StartsWith("[::1]:", StringComparison.Ordinal) ? "[::1]:" : null;
        return prefix is not null && int.TryParse(server.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) &&
            port is >= 1 and <= 65535 && server == prefix + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    internal bool External => (Flags & (Manual | AutoUrl | AutoDetect)) != 0;
    internal void Validate()
    {
        if (Flags == 0 || (Flags & ~15u) != 0 || new[] { Server, Bypass, Pac }.Any(s => s is null || s.Length > 32768 || s.Contains('\0')))
            throw new InvalidDataException("Unsupported or incomplete Windows proxy settings.");
    }
}

internal interface ISystemProxy
{
    ProxySettings Read();
    void EnsureWritable();
    void Write(ProxySettings settings);
    void Refresh();
}

internal sealed class SystemProxy : ISystemProxy
{
    private const uint PerConnection = 75, SettingsChanged = 39, RefreshOption = 37;
    private const uint Flags = 1, Server = 2, Bypass = 3, Pac = 4, FlagsUi = 10;
    private const string Policy = @"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string Restrictions = @"SOFTWARE\Policies\Microsoft\Internet Explorer\Control Panel";
    private const string Settings = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings";

    public ProxySettings Read()
    {
        // Windows 7+ documents querying FLAGS_UI and restoring using FLAGS.
        Option[] options = [new(FlagsUi), new(Server), new(Bypass), new(Pac)];
        var list = new OptionList((uint)options.Length);
        var pin = GCHandle.Alloc(options, GCHandleType.Pinned);
        try
        {
            list.Options = pin.AddrOfPinnedObject();
            var size = list.Size;
            if (!InternetQueryOptionW(IntPtr.Zero, PerConnection, ref list, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot query LAN proxy (option index {list.ErrorIndex}).");
            var result = new ProxySettings((uint)options[0].Value.ToInt64(), ReadString(options[1]), ReadString(options[2]), ReadString(options[3]));
            result.Validate();
            return result;
        }
        finally
        {
            // Query strings belong to WinINet even on a partially failed query.
            for (var i = 1; i < options.Length; i++) if (options[i].Value != IntPtr.Zero) _ = GlobalFree(options[i].Value);
            pin.Free();
        }
    }

    public void EnsureWritable()
    {
        // Conservative read-only policy detection. Never change policy or machine settings.
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var policy = hive.OpenSubKey(Policy);
            if (policy is not null && policy.GetValueNames().Any(n => n.StartsWith("Proxy", StringComparison.OrdinalIgnoreCase) || n.StartsWith("AutoConfig", StringComparison.OrdinalIgnoreCase) || n.Equals("AutoDetect", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Proxy policy is present; Windows proxy changes are disabled.");
            using var restrictions = hive.OpenSubKey(Restrictions);
            if (restrictions is not null && new[] { "Proxy", "AutoConfig", "Connection Settings" }.Any(n => restrictions.GetValue(n) is { } value && !Equals(value, 0)))
                throw new InvalidOperationException("Internet Options are restricted by policy.");
            using var settings = hive.OpenSubKey(Settings);
            if (settings?.GetValue("ProxySettingsPerUser") is { } scope && !Equals(scope, 1))
                throw new InvalidOperationException("Per-user proxy scope cannot be confirmed.");
        }
    }

    public void Write(ProxySettings settings)
    {
        EnsureWritable(); settings.Validate();
        Option[] options = [new(Flags) { Value = new IntPtr(settings.Flags) }, new(Server), new(Bypass), new(Pac)];
        var pin = GCHandle.Alloc(options, GCHandleType.Pinned);
        try
        {
            options[1].Value = Marshal.StringToHGlobalUni(settings.Server);
            options[2].Value = Marshal.StringToHGlobalUni(settings.Bypass);
            options[3].Value = Marshal.StringToHGlobalUni(settings.Pac);
            var list = new OptionList((uint)options.Length) { Options = pin.AddrOfPinnedObject() };
            if (!InternetSetOptionW(IntPtr.Zero, PerConnection, ref list, list.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot set LAN proxy (option index {list.ErrorIndex}).");
        }
        finally
        {
            for (var i = 1; i < options.Length; i++) Marshal.FreeHGlobal(options[i].Value);
            pin.Free();
        }
    }

    public void Refresh()
    {
        if (!InternetSetOptionW(IntPtr.Zero, SettingsChanged, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not notify Windows of proxy settings change.");
        if (!InternetSetOptionW(IntPtr.Zero, RefreshOption, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not refresh Windows proxy settings.");
    }

    private static string ReadString(Option option) => Marshal.PtrToStringUni(option.Value) ?? string.Empty;

    [StructLayout(LayoutKind.Sequential)]
    private struct Option(uint id)
    {
        internal uint Id = id;
        internal IntPtr Value = IntPtr.Zero; // x64 union: DWORD, pointer or FILETIME (8 bytes)
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct OptionList(uint count)
    {
        internal uint Size = (uint)Marshal.SizeOf<OptionList>();
        internal IntPtr Connection = IntPtr.Zero; // null = default LAN connection
        internal uint Count = count;
        internal uint ErrorIndex = 0;
        internal IntPtr Options = IntPtr.Zero;
    }
    [DllImport("wininet.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetQueryOptionW(IntPtr internet, uint option, ref OptionList buffer, ref uint length);
    [DllImport("wininet.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOptionW(IntPtr internet, uint option, ref OptionList buffer, uint length);
    [DllImport("wininet.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOptionW(IntPtr internet, uint option, IntPtr buffer, uint length);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
