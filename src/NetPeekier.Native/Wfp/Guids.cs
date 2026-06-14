// WFP well-known GUIDs and constants.
//
// These are documented in fwpmu.h / fwpmtypes.h in the Windows SDK. C# has
// no native way to declare GUIDs as compile-time constants, so we define
// them once here as static readonly fields and reference them everywhere.
//
// Provider/sublayer GUIDs at the bottom are OURS — they identify Net-Peekier
// to the WFP engine. Picking stable, unique GUIDs means our filters never
// collide with anyone else's, and "remove all our rules" reduces to
// "delete every filter whose providerKey is ours".

namespace NetPeekier.Native.Wfp;

internal static class Guids
{
    // ---- Layer keys -----------------------------------------------------
    //
    // ALE (Application Layer Enforcement) is the right family for per-app
    // filtering: the layers fire at connect/accept time, so a blocked app
    // gets ECONNREFUSED, not the surprise of half-formed sockets.

    /// <summary>FWPM_LAYER_ALE_AUTH_CONNECT_V4</summary>
    public static readonly Guid LayerAleAuthConnectV4 =
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82");

    /// <summary>FWPM_LAYER_ALE_AUTH_CONNECT_V6</summary>
    public static readonly Guid LayerAleAuthConnectV6 =
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

    /// <summary>FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4</summary>
    public static readonly Guid LayerAleAuthRecvAcceptV4 =
        new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");

    /// <summary>FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6</summary>
    public static readonly Guid LayerAleAuthRecvAcceptV6 =
        new("a3b42c97-9f04-4672-b87e-cee9c483257f");

    // ---- Condition field keys ------------------------------------------

    /// <summary>FWPM_CONDITION_ALE_APP_ID — per-app token (BYTE_BLOB).</summary>
    public static readonly Guid ConditionAleAppId =
        new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    /// <summary>FWPM_CONDITION_IP_REMOTE_ADDRESS — UINT32 (v4) or BYTE_ARRAY16 (v6) or V4_ADDR_MASK / V6_ADDR_MASK.</summary>
    public static readonly Guid ConditionIpRemoteAddress =
        new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

    /// <summary>FWPM_CONDITION_IP_REMOTE_PORT — UINT16.</summary>
    public static readonly Guid ConditionIpRemotePort =
        new("c35a604d-d22b-4e1a-91b4-68f674ee674b");

    /// <summary>FWPM_CONDITION_IP_PROTOCOL — UINT8 (6=TCP, 17=UDP).</summary>
    public static readonly Guid ConditionIpProtocol =
        new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    // ---- Our own GUIDs (Net-Peekier provider + sublayer) ---------------
    //
    // These are stable across runs; pinning them here means any filter we
    // ever leave behind is identifiable as ours forever.

    public static readonly Guid NetPeekierProvider =
        new("9be94c1e-7d4b-4f1d-8bfe-1c5e2f00be01");

    public static readonly Guid NetPeekierSublayer =
        new("9be94c1e-7d4b-4f1d-8bfe-1c5e2f00be02");
}

internal static class FwpConstants
{
    // FWP_ACTION_TYPE
    public const uint FWP_ACTION_BLOCK  = 0x00001001;
    public const uint FWP_ACTION_PERMIT = 0x00001002;

    // FWP_MATCH_TYPE
    public const uint FWP_MATCH_EQUAL = 0;

    // FWP_DATA_TYPE
    public const uint FWP_EMPTY            = 0;
    public const uint FWP_UINT8            = 1;
    public const uint FWP_UINT16           = 2;
    public const uint FWP_UINT32           = 3;
    public const uint FWP_BYTE_ARRAY16_TYPE = 11;
    public const uint FWP_BYTE_BLOB_TYPE   = 10;
    public const uint FWP_V4_ADDR_MASK     = 18;
    public const uint FWP_V6_ADDR_MASK     = 19;
    public const uint FWP_RANGE_TYPE       = 20;

    // FWPM_*_FLAG
    public const uint FWPM_PROVIDER_FLAG_PERSISTENT = 0x00000001;
    public const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0x00000001;
    public const uint FWPM_FILTER_FLAG_PERSISTENT   = 0x00000001;

    // IP protocol numbers (as understood by FWPM_CONDITION_IP_PROTOCOL).
    public const byte IPPROTO_TCP = 6;
    public const byte IPPROTO_UDP = 17;
}
