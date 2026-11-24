using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using XrayDesktop.Config;

namespace XrayDesktop.Windows;

internal static class TcpListeners
{
    internal static bool IsOwned(Inbound endpoint, int pid)
    {
        var v6 = endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var family = v6 ? 23 : 2;
        uint size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        if (result != 122 && result != 0) throw new Win32Exception((int)result);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size < 4 || size > 64 * 1024 * 1024) throw new InvalidDataException("Invalid TCP owner table size.");
            var allocation = Marshal.AllocHGlobal((int)size);
            try
            {
                var allocated = size;
                result = GetExtendedTcpTable(allocation, ref size, false, family, 3, 0);
                if (result == 122) continue;
                if (result != 0) throw new Win32Exception((int)result);
                var count = Marshal.ReadInt32(allocation);
                var stride = v6 ? 56 : 24;
                if (count < 0 || 4L + (long)count * stride > allocated) throw new InvalidDataException("Invalid TCP owner table.");
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(allocation, 4 + i * stride);
                    var portOffset = v6 ? 20 : 8;
                    var port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                    var owner = Marshal.ReadInt32(row, v6 ? 52 : 20);
                    var state = Marshal.ReadInt32(row, v6 ? 48 : 0);
                    if (port != endpoint.Port || owner != pid || state != 2) continue;
                    var bytes = new byte[v6 ? 16 : 4];
                    Marshal.Copy(IntPtr.Add(row, v6 ? 0 : 4), bytes, 0, bytes.Length);
                    if (new IPAddress(bytes).Equals(endpoint.Address)) return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(allocation); }
        }
        throw new InvalidOperationException("TCP owner table changed repeatedly.");
    }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool sort, int family, int tableClass, uint reserved);
}
