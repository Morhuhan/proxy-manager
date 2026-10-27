using System.Net;
using System.Runtime.InteropServices;

namespace ProxyManager.Services;

/// <summary>
/// Resets IPv4 TCP connections that programs opened before the TUN adapter came up: they bypass
/// sing-box entirely. After the reset the program reconnects, now through the TUN.
/// </summary>
internal static partial class TcpConnections
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const uint StateEstablished = 5;
    private const uint StateDeleteTcb = 12;

    /// <summary>Returns how many connections were reset. Requires administrator rights.</summary>
    public static int ResetOutsideTun(IReadOnlySet<int> pids)
    {
        if (pids.Count == 0) return 0;

        var tun = IPAddress.Parse(SingBoxConfigBuilder.TunAddress);
        var reset = 0;
        foreach (var row in ReadTable())
        {
            if (row.State != StateEstablished || !pids.Contains((int)row.OwningPid)) continue;
            var local = new IPAddress(row.LocalAddr);
            if (IPAddress.IsLoopback(local) || local.Equals(tun)) continue;

            var entry = new MIB_TCPROW
            {
                State = StateDeleteTcb,
                LocalAddr = row.LocalAddr,
                LocalPort = row.LocalPort,
                RemoteAddr = row.RemoteAddr,
                RemotePort = row.RemotePort,
            };
            if (SetTcpEntry(ref entry) == 0)
                reset++;
        }
        return reset;
    }

    private static List<MIB_TCPROW_OWNER_PID> ReadTable()
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
        // The table can grow between the two calls; retry with the new size.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0) != 0)
                    continue;

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                var rows = new List<MIB_TCPROW_OWNER_PID>(count);
                for (var i = 0; i < count; i++)
                    rows.Add(Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buffer + 4 + i * rowSize));
                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint SetTcpEntry(ref MIB_TCPROW row);
}
