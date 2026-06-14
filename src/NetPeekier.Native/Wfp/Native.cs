// fwpuclnt.dll P/Invoke surface.
//
// The Windows Filtering Platform (WFP) user-mode API. Everything declared
// here is documented at:
//   https://learn.microsoft.com/windows/win32/api/_fwp/
//
// We split P/Invoke from the high-level API so the dangerous parts are in
// one place. Each method here is one DllImport line; the choices made are:
//
//   * SetLastError is false because WFP returns its own DWORD result codes
//     (FwpmEngineOpen0 etc. return ERROR_* values directly).
//   * Strings cross as UTF-16 (CharSet.Unicode). Win32 Wide variants only.
//   * Output handles come back as IntPtr (HANDLE) and are explicitly closed
//     via FwpmEngineClose0 / FwpmFreeMemory0 — no SafeHandle yet because
//     these resources nest awkwardly. A future polish task.
//
// IMPORTANT lifetime rule: when FwpmFilterEnum0 / FwpmFilterGetById0 hand us
// a pointer to allocated memory, we MUST pair it with FwpmFreeMemory0. Every
// Get / Enum path in WfpFirewall.cs does this in a finally.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetPeekier.Native.Wfp;

[SupportedOSPlatform("windows")]
internal static class Native
{
    public const uint ERROR_SUCCESS    = 0;
    public const uint FWP_E_NOT_FOUND  = 0x80320008;
    public const uint FWP_E_ALREADY_EXISTS = 0x80320009;
    public const uint RPC_C_AUTHN_WINNT = 10;

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        uint authnService,
        IntPtr authIdentity,                       // SEC_WINNT_AUTH_IDENTITY_W*; NULL = caller's identity
        IntPtr session,                            // FWPM_SESSION0*; NULL = transient session
        out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern void FwpmFreeMemory0(ref IntPtr p);

    // ---- transactions ----
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

    // ---- provider / sublayer ----
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmProviderAdd0(
        IntPtr engineHandle, ref FWPM_PROVIDER0 provider, IntPtr sd);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmProviderGetByKey0(
        IntPtr engineHandle, ref Guid key, out IntPtr provider);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmSubLayerAdd0(
        IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmSubLayerGetByKey0(
        IntPtr engineHandle, ref Guid key, out IntPtr subLayer);

    // ---- filters ----
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterAdd0(
        IntPtr engineHandle,
        ref FWPM_FILTER0 filter,
        IntPtr sd,
        out ulong id);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterDeleteById0(
        IntPtr engineHandle, ulong id);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterCreateEnumHandle0(
        IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterEnum0(
        IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested,
        out IntPtr entries, out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterDestroyEnumHandle0(
        IntPtr engineHandle, IntPtr enumHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterGetById0(
        IntPtr engineHandle, ulong id, out IntPtr filter);

    // ---- app id helper ----
    // Converts an executable file path into an "app id" byte blob that the
    // ALE_APP_ID condition expects. The returned FWP_BYTE_BLOB* must be freed
    // with FwpmFreeMemory0.
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint FwpmGetAppIdFromFileName0(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        out IntPtr appId);

    // ===================================================================
    // Structures.
    //
    // Layout-only translations of the FWPM_* types we touch. The fields we
    // don't use are kept as IntPtr so the sizes line up.
    // ===================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct FWPM_DISPLAY_DATA0
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_PROVIDER0
    {
        public Guid               ProviderKey;
        public FWPM_DISPLAY_DATA0 DisplayData;
        public uint               Flags;          // FWPM_PROVIDER_FLAG_PERSISTENT, etc.
        public FWP_BYTE_BLOB      ProviderData;
        public IntPtr             ServiceName;    // LPWSTR or null
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_SUBLAYER0
    {
        public Guid               SubLayerKey;
        public FWPM_DISPLAY_DATA0 DisplayData;
        public uint               Flags;          // FWPM_SUBLAYER_FLAG_PERSISTENT, etc.
        public IntPtr             ProviderKey;    // GUID*; can be null
        public FWP_BYTE_BLOB      ProviderData;
        public ushort             Weight;
    }

    /// <summary>
    /// FWPM_FILTER0. We use the simple, non-callout form: no rawContext, no
    /// reauthorization-condition extras. action.Type is set explicitly to
    /// FWP_ACTION_BLOCK or FWP_ACTION_PERMIT.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER0
    {
        public Guid               FilterKey;          // in/out; we leave it zero so WFP assigns one
        public FWPM_DISPLAY_DATA0 DisplayData;
        public uint               Flags;
        public IntPtr             ProviderKey;        // GUID*
        public FWP_BYTE_BLOB      ProviderData;
        public Guid               LayerKey;
        public Guid               SubLayerKey;
        public FWP_VALUE0         Weight;             // unioned with empty -> we set type=FWP_EMPTY for "let WFP pick"
        public uint               NumFilterConditions;
        public IntPtr             FilterCondition;    // FWPM_FILTER_CONDITION0*
        public FWPM_ACTION0       Action;
        // The remaining fields are output-only and untouched on add.
        public ulong              RawContext;          // union: ulong / Guid
        public Guid               RawContextGuid;
        public Guid               ReservedKey;
        public ulong              FilterId;
        public FWP_VALUE0         EffectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_FILTER_CONDITION0
    {
        public Guid                FieldKey;
        public uint                MatchType;          // FWP_MATCH_EQUAL etc.
        public FWP_CONDITION_VALUE0 ConditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWPM_ACTION0
    {
        public uint Type;          // FWP_ACTION_BLOCK / FWP_ACTION_PERMIT / ...
        public Guid FilterTypeOrCalloutKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_BYTE_BLOB
    {
        public uint   Size;
        public IntPtr Data;
    }

    // FWP_VALUE0 / FWP_CONDITION_VALUE0 are tagged unions. In C they are
    // { FWP_DATA_TYPE type; <padding to ptr alignment>; <union of 8-byte
    // pointer or smaller primitive> } — 16 bytes on x64. We let the C#
    // marshaller compute that padding automatically by putting the uint
    // first and the IntPtr second under [StructLayout(Sequential)]:
    // Type lands at offset 0, ValuePtr at offset 8 (natural pointer
    // alignment), total size 16. DO NOT add an explicit padding field —
    // that breaks downstream field offsets in FWPM_FILTER0.
    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_VALUE0
    {
        public uint   Type;
        public IntPtr ValuePtr;        // union slot; biggest member is a pointer
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FWP_CONDITION_VALUE0
    {
        public uint   Type;
        public IntPtr ValuePtr;
    }
}
