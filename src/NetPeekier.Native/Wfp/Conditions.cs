// Helpers that build FWPM_FILTER_CONDITION0 entries.
//
// Why these aren't trivial: WFP conditions hold variable-length payloads
// (FWP_BYTE_BLOB for app ids, FWP_V4_ADDR_AND_MASK for CIDR, etc.) that
// must outlive the FwpmFilterAdd0 call. The classic Marshal.AllocHGlobal /
// FreeHGlobal lifetime gets unwieldy fast with multiple allocations per
// filter, so we collect them in an "arena" that disposes everything in
// one shot once the filter has been submitted.

using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static NetPeekier.Native.Wfp.Native;

namespace NetPeekier.Native.Wfp;

/// <summary>
/// Owns a list of unmanaged allocations and frees them all in Dispose.
/// One arena per filter-build; build conditions, submit the filter, dispose.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeArena : IDisposable
{
    private readonly List<IntPtr> _allocs = new();
    private bool _disposed;

    public IntPtr Alloc(int size)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeArena));
        var p = Marshal.AllocHGlobal(size);
        _allocs.Add(p);
        return p;
    }

    public IntPtr AllocAndCopy<T>(T value) where T : struct
    {
        var p = Alloc(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, p, fDeleteOld: false);
        return p;
    }

    public IntPtr AllocAndCopyBytes(ReadOnlySpan<byte> bytes)
    {
        var p = Alloc(bytes.Length);
        Marshal.Copy(bytes.ToArray(), 0, p, bytes.Length);
        return p;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var p in _allocs)
        {
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
        _allocs.Clear();
    }
}

[SupportedOSPlatform("windows")]
internal static class Conditions
{
    /// <summary>
    /// Build an ALE_APP_ID condition. The app-id blob is acquired from
    /// FwpmGetAppIdFromFileName0 — the pointer it hands back is owned by
    /// WFP and must be freed with FwpmFreeMemory0. We copy out the bytes
    /// (so the condition can live independently) and free the WFP buffer.
    /// </summary>
    public static FWPM_FILTER_CONDITION0 AppId(string exePath, NativeArena arena)
    {
        uint status = FwpmGetAppIdFromFileName0(exePath, out var appIdPtr);
        if (status != ERROR_SUCCESS)
            throw new WfpException($"FwpmGetAppIdFromFileName0 failed for {exePath}", status);

        try
        {
            // The pointer is to an FWP_BYTE_BLOB whose Data buffer lives
            // alongside it in WFP-owned memory. Copy the bytes out so we
            // can free the WFP allocation right here.
            var srcBlob = Marshal.PtrToStructure<FWP_BYTE_BLOB>(appIdPtr);
            var bytes = new byte[srcBlob.Size];
            Marshal.Copy(srcBlob.Data, bytes, 0, (int)srcBlob.Size);

            // Reconstruct an FWP_BYTE_BLOB in arena-owned memory so the
            // condition can keep referencing it through the FwpmFilterAdd0
            // call.
            var dataPtr = arena.AllocAndCopyBytes(bytes);
            var blobPtr = arena.AllocAndCopy(new FWP_BYTE_BLOB
            {
                Size = (uint)bytes.Length,
                Data = dataPtr,
            });

            return new FWPM_FILTER_CONDITION0
            {
                FieldKey = Guids.ConditionAleAppId,
                MatchType = FwpConstants.FWP_MATCH_EQUAL,
                ConditionValue = new FWP_CONDITION_VALUE0
                {
                    Type = FwpConstants.FWP_BYTE_BLOB_TYPE,
                    ValuePtr = blobPtr,
                },
            };
        }
        finally
        {
            FwpmFreeMemory0(ref appIdPtr);
        }
    }

    /// <summary>
    /// Single IPv4 remote-address condition (host-order uint32 stored
    /// inline in the union — no separate allocation).
    /// </summary>
    public static FWPM_FILTER_CONDITION0 RemoteIPv4(IPAddress addr)
    {
        var b = addr.GetAddressBytes();           // network order
        // FWP_UINT32 wants host order, big-endian network -> uint host order.
        uint host = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

        return new FWPM_FILTER_CONDITION0
        {
            FieldKey = Guids.ConditionIpRemoteAddress,
            MatchType = FwpConstants.FWP_MATCH_EQUAL,
            ConditionValue = new FWP_CONDITION_VALUE0
            {
                Type = FwpConstants.FWP_UINT32,
                ValuePtr = (IntPtr)host,           // inline; no allocation
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWP_V4_ADDR_AND_MASK
    {
        public uint Addr;
        public uint Mask;
    }

    /// <summary>IPv4 CIDR (addr + mask) condition.</summary>
    public static FWPM_FILTER_CONDITION0 RemoteIPv4Cidr(IPAddress addr, int prefix, NativeArena arena)
    {
        var b = addr.GetAddressBytes();
        uint host = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint mask = prefix == 0 ? 0u : 0xffffffffu << (32 - prefix);
        var ptr = arena.AllocAndCopy(new FWP_V4_ADDR_AND_MASK
        {
            Addr = host & mask,                    // mask out host bits
            Mask = mask,
        });
        return new FWPM_FILTER_CONDITION0
        {
            FieldKey = Guids.ConditionIpRemoteAddress,
            MatchType = FwpConstants.FWP_MATCH_EQUAL,
            ConditionValue = new FWP_CONDITION_VALUE0
            {
                Type = FwpConstants.FWP_V4_ADDR_MASK,
                ValuePtr = ptr,
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWP_V6_ADDR_AND_MASK
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Addr;
        public byte PrefixLength;
    }

    /// <summary>IPv6 CIDR (addr + prefix length) condition.</summary>
    public static FWPM_FILTER_CONDITION0 RemoteIPv6Cidr(IPAddress addr, int prefix, NativeArena arena)
    {
        var ptr = arena.AllocAndCopy(new FWP_V6_ADDR_AND_MASK
        {
            Addr = addr.GetAddressBytes(),
            PrefixLength = (byte)prefix,
        });
        return new FWPM_FILTER_CONDITION0
        {
            FieldKey = Guids.ConditionIpRemoteAddress,
            MatchType = FwpConstants.FWP_MATCH_EQUAL,
            ConditionValue = new FWP_CONDITION_VALUE0
            {
                Type = FwpConstants.FWP_V6_ADDR_MASK,
                ValuePtr = ptr,
            },
        };
    }

    /// <summary>Remote-port equality condition (FWP_UINT16, inline).</summary>
    public static FWPM_FILTER_CONDITION0 RemotePort(ushort port) => new()
    {
        FieldKey = Guids.ConditionIpRemotePort,
        MatchType = FwpConstants.FWP_MATCH_EQUAL,
        ConditionValue = new FWP_CONDITION_VALUE0
        {
            Type = FwpConstants.FWP_UINT16,
            ValuePtr = (IntPtr)port,
        },
    };

    /// <summary>IP-protocol equality condition (FWP_UINT8, inline).</summary>
    public static FWPM_FILTER_CONDITION0 IpProtocol(byte proto) => new()
    {
        FieldKey = Guids.ConditionIpProtocol,
        MatchType = FwpConstants.FWP_MATCH_EQUAL,
        ConditionValue = new FWP_CONDITION_VALUE0
        {
            Type = FwpConstants.FWP_UINT8,
            ValuePtr = (IntPtr)proto,
        },
    };

    /// <summary>
    /// Marshal an array of FWPM_FILTER_CONDITION0 into the arena. Returns
    /// the unmanaged pointer suitable for FWPM_FILTER0.FilterCondition.
    /// </summary>
    public static IntPtr MarshalConditions(
        IReadOnlyList<FWPM_FILTER_CONDITION0> conditions, NativeArena arena)
    {
        if (conditions.Count == 0) return IntPtr.Zero;
        int one = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
        var arr = arena.Alloc(one * conditions.Count);
        for (int i = 0; i < conditions.Count; i++)
            Marshal.StructureToPtr(conditions[i], arr + (i * one), fDeleteOld: false);
        return arr;
    }
}

/// <summary>Thrown by WfpFirewall when the engine reports a hard failure.</summary>
public sealed class WfpException : Exception
{
    public uint Status { get; }
    public WfpException(string message, uint status)
        : base($"{message} (status=0x{status:X8})") { Status = status; }
}
