// Port of netpeekier/firewall.py to the Windows Filtering Platform user-mode
// API. The Python build shelled out to `netsh advfirewall`. We talk to the
// same kernel filter engine directly through fwpuclnt.dll, which is:
//
//   * faster (no process spawn per rule),
//   * richer (we own our own SUBLAYER so we can never collide with anything
//     else and the safety-hatch "delete only our own rules" is trivially
//     correct: delete every filter in our sublayer),
//   * safer (a filter we add for an app id can NEVER turn into a block-all
//     rule the way a malformed `netsh ... program=` could).
//
// STATUS: Phase 3 TODO. The shape below mirrors firewall.py's public API
// 1:1 so Monitor.cs can swap engines without changing.
//
// IMPLEMENTATION NOTES (for when we fill this in):
//
//   1. One GUID for our PROVIDER and one for our SUBLAYER. Stable across
//      runs (hard-code them). Filters always carry our provider key.
//
//   2. FwpmEngineOpen0 -> engine handle. Cache it. Close on app exit.
//
//   3. For per-app blocking:
//          FwpmGetAppIdFromFileName0(exe, out appId)
//          FWPM_FILTER0 with one FWPM_FILTER_CONDITION0:
//              fieldKey = FWPM_CONDITION_ALE_APP_ID
//              matchType = FWP_MATCH_EQUAL
//              conditionValue = FWP_BYTE_BLOB containing appId
//          layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4 (and V6) for outbound,
//                     FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 (and V6) for inbound.
//          action.type = FWP_ACTION_BLOCK
//          subLayerKey = our sublayer GUID
//      Repeat for the IPv6 layer. Two filters per direction per IP version.
//
//   4. Per-IP rules add more conditions:
//          FWPM_CONDITION_IP_REMOTE_ADDRESS  (FWP_V4_ADDR_MASK / V6 / V4_ADDR_AND_MASK)
//          FWPM_CONDITION_IP_REMOTE_PORT     (FWP_UINT16)
//          FWPM_CONDITION_IP_PROTOCOL        (FWP_UINT8)
//
//   5. Whitelist (allow-only) uses the IpCalc complement just like the
//      Python build -- emit BLOCK filters for everything *not* allowed.
//      Same logic; different sink. Tested via Core's IpCalc unit tests.
//
//   6. RemoveAllRules() is trivial in this design: enumerate filters by
//      our provider key and delete them. We can never accidentally delete
//      a rule we didn't create.

using NetPeeker.Core;

namespace NetPeeker.Native;

public static class WfpFirewall
{
    /// <summary>
    /// Validate that a string is a concrete .exe path we're willing to build
    /// a filter from. This guard is the C# port of firewall._valid_exe and is
    /// the single thing preventing a malformed input from producing a
    /// block-everything filter.
    /// </summary>
    public static bool ValidExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var p = exePath.Trim().Trim('"').Trim();
        if (p.Length < 4) return false;
        if (p.IndexOfAny(new[] { '*', '?', '"', '\n', '\r', '\t', '|', '&', ';' }) >= 0)
            return false;
        if (p.Length <= 3 || p[1] != ':' || p[2] != '\\') return false;
        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ---- per-app block (mirrors firewall.block_app / unblock_app) ---------
    public static (bool Ok, string Message) BlockApp(string exe) =>
        throw new NotImplementedException("Phase 3 TODO: implement WFP block filter for app");

    public static (bool Ok, string Message) UnblockApp(string exe) =>
        throw new NotImplementedException("Phase 3 TODO: remove filters owned by this exe");

    public static bool IsBlocked(string exe) =>
        throw new NotImplementedException("Phase 3 TODO: query our sublayer for an app block filter");

    public static IReadOnlyList<string> ListBlocked() =>
        throw new NotImplementedException("Phase 3 TODO: enumerate filters in our sublayer");

    // ---- per-IP rules (mirrors firewall.add_ip_rule / remove_ip_rule) -----
    public static (bool Ok, string Message) AddIpRule(IpRule rule) =>
        throw new NotImplementedException("Phase 3 TODO: add WFP filter with conditions");

    public static (bool Ok, string Message) RemoveIpRule(IpRule rule) =>
        throw new NotImplementedException("Phase 3 TODO");

    public static int RemoveAllIpRules() =>
        throw new NotImplementedException("Phase 3 TODO");

    // ---- whitelist (block-the-complement) ---------------------------------
    public static int RemoveWhitelist(string exe) =>
        throw new NotImplementedException("Phase 3 TODO");

    public static (bool Ok, string Message) SetWhitelist(string exe, IEnumerable<IpRule> allowEntries) =>
        throw new NotImplementedException("Phase 3 TODO: emit BLOCK filters for IpCalc complement");

    // ---- safety hatch -----------------------------------------------------
    public static (int Removed, string Message) RemoveAllRules() =>
        throw new NotImplementedException("Phase 3 TODO: delete every filter in our sublayer");
}
