// IP Helper wrappers: enumerate the live TCP/UDP tables with PID ownership.
// Replaces psutil.net_connections() from the Python build.
//
// The whole interesting part is GetExtendedTcpTable / GetExtendedUdpTable.
// They take a buffer + four flavours of struct (TCP4/TCP6/UDP4/UDP6) and
// return contiguous arrays. We hand-roll the P/Invoke rather than depend on
// the CsWin32 generator so the project compiles offline.

using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NetPeekier.Core;

namespace NetPeekier.Native;

[SupportedOSPlatform("windows")]
public static class IpHelper
{
    /// <summary>
    /// Snapshot every live TCP + UDP socket on the box with its owning PID.
    /// Equivalent to psutil.net_connections(kind="inet") in the Python build.
    /// </summary>
    public static IReadOnlyList<Connection> SnapshotConnections()
    {
        var list = new List<Connection>(256);
        try { ReadTcp4(list); } catch { /* best-effort per family */ }
        try { ReadTcp6(list); } catch { }
        try { ReadUdp4(list); } catch { }
        try { ReadUdp6(list); } catch { }
        return list;
    }

    public static IReadOnlyDictionary<(string Proto, int LocalPort), int>
        BuildLoosePidTable(IEnumerable<Connection> conns)
    {
        var d = new Dictionary<(string, int), int>();
        foreach (var c in conns)
            d[(c.Protocol, c.LocalPort)] = c.Pid;
        return d;
    }

    // =====================================================================
    // TCP / UDP enumeration.
    //
    // The GetExtendedTcpTable / GetExtendedUdpTable contract is "two-call":
    // pass a null buffer to learn the required size, allocate, call again.
    // The buffer holds DWORD count + count * row struct, contiguous.
    // =====================================================================

    private const int AF_INET  = 2;
    private const int AF_INET6 = 23;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int NO_ERROR = 0;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf,
        int TableClass, int Reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf,
        int TableClass, int Reserved);

    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID    = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;          // big-endian, low 16 bits significant
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    private static void ReadTcp4(List<Connection> outList)
    {
        WalkTable(
            (IntPtr p, ref int sz) => GetExtendedTcpTable(p, ref sz, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0),
            Marshal.SizeOf<MIB_TCPROW_OWNER_PID>(),
            (rowPtr) =>
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                outList.Add(new Connection
                {
                    Pid        = (int)row.OwningPid,
                    Protocol   = "TCP",
                    LocalIp    = new IPAddress(BitConverter.GetBytes(row.LocalAddr)).ToString(),
                    LocalPort  = NetPort(row.LocalPort),
                    RemoteIp   = new IPAddress(BitConverter.GetBytes(row.RemoteAddr)).ToString(),
                    RemotePort = NetPort(row.RemotePort),
                    Status     = TcpState((int)row.State),
                });
            });
    }

    private static void ReadTcp6(List<Connection> outList)
    {
        WalkTable(
            (IntPtr p, ref int sz) => GetExtendedTcpTable(p, ref sz, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0),
            Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>(),
            (rowPtr) =>
            {
                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                outList.Add(new Connection
                {
                    Pid        = (int)row.OwningPid,
                    Protocol   = "TCP",
                    LocalIp    = new IPAddress(row.LocalAddr).ToString(),
                    LocalPort  = NetPort(row.LocalPort),
                    RemoteIp   = new IPAddress(row.RemoteAddr).ToString(),
                    RemotePort = NetPort(row.RemotePort),
                    Status     = TcpState((int)row.State),
                });
            });
    }

    private static void ReadUdp4(List<Connection> outList)
    {
        WalkTable(
            (IntPtr p, ref int sz) => GetExtendedUdpTable(p, ref sz, false, AF_INET, UDP_TABLE_OWNER_PID, 0),
            Marshal.SizeOf<MIB_UDPROW_OWNER_PID>(),
            (rowPtr) =>
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                outList.Add(new Connection
                {
                    Pid        = (int)row.OwningPid,
                    Protocol   = "UDP",
                    LocalIp    = new IPAddress(BitConverter.GetBytes(row.LocalAddr)).ToString(),
                    LocalPort  = NetPort(row.LocalPort),
                    RemoteIp   = "",
                    RemotePort = 0,
                    Status     = "",
                });
            });
    }

    private static void ReadUdp6(List<Connection> outList)
    {
        WalkTable(
            (IntPtr p, ref int sz) => GetExtendedUdpTable(p, ref sz, false, AF_INET6, UDP_TABLE_OWNER_PID, 0),
            Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>(),
            (rowPtr) =>
            {
                var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(rowPtr);
                outList.Add(new Connection
                {
                    Pid        = (int)row.OwningPid,
                    Protocol   = "UDP",
                    LocalIp    = new IPAddress(row.LocalAddr).ToString(),
                    LocalPort  = NetPort(row.LocalPort),
                    RemoteIp   = "",
                    RemotePort = 0,
                    Status     = "",
                });
            });
    }

    /// <summary>
    /// Shared two-call enumeration. Calls <paramref name="getter"/> with a
    /// null buffer first to learn the required size, allocates it, calls
    /// again, then invokes <paramref name="readRow"/> on each row pointer.
    /// Buffer layout: DWORD count, then count * rowSize bytes.
    /// </summary>
    private delegate uint TableGetter(IntPtr buffer, ref int size);

    private static void WalkTable(TableGetter getter, int rowSize, Action<IntPtr> readRow)
    {
        int sz = 0;
        var status = getter(IntPtr.Zero, ref sz);
        if (status != ERROR_INSUFFICIENT_BUFFER && status != NO_ERROR) return;
        if (sz <= 0) return;

        var buf = Marshal.AllocHGlobal(sz);
        try
        {
            status = getter(buf, ref sz);
            if (status != NO_ERROR) return;
            int count = Marshal.ReadInt32(buf);
            // First field of the table is a DWORD count, then the rows are
            // 4-byte aligned. The rows begin at offset 4 on 32-bit and 8 on
            // 64-bit due to natural alignment of the first row's first field;
            // safest is to compute it from the struct.
            var rowsBase = buf + IntPtr.Size; // matches MIB_*TABLE_OWNER_PID layout
            for (int i = 0; i < count; i++)
            {
                var rowPtr = rowsBase + (i * rowSize);
                readRow(rowPtr);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// IP Helper port fields are big-endian 32-bit values; only the low 16
    /// bits matter. ((b1 << 8) | b0) is the host-order port.
    /// </summary>
    private static int NetPort(uint p) =>
        ((int)(p & 0xff) << 8) | (int)((p >> 8) & 0xff);

    /// <summary>Map TCP state enum to the names psutil uses.</summary>
    private static string TcpState(int s) => s switch
    {
        1  => "CLOSED",
        2  => "LISTEN",
        3  => "SYN_SENT",
        4  => "SYN_RECV",
        5  => "ESTABLISHED",
        6  => "FIN_WAIT1",
        7  => "FIN_WAIT2",
        8  => "CLOSE_WAIT",
        9  => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _  => "",
    };
}
