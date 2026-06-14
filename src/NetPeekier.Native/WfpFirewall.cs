// High-level WFP firewall API. Port of netpeekier/firewall.py.
//
// The Python build shelled out to `netsh advfirewall`. We talk to the same
// kernel filter engine directly through fwpuclnt.dll, which is:
//
//   * faster (no process spawn per rule),
//   * richer (we own a private SUBLAYER so our filters never collide with
//     anything else, and "remove only our own rules" reduces to "delete
//     every filter in our sublayer"),
//   * safer (a filter scoped to a specific app id CANNOT degrade into a
//     block-all rule the way a malformed `netsh ... program=` could).
//
// Architecture mirrors firewall.py:
//
//   BlockApp / UnblockApp / IsBlocked / ListBlocked
//       The simple per-app block (firewall.block_app etc.). Two filters
//       per direction per IP family = 4 filters per app.
//
//   AddIpRule / RemoveIpRule
//       Per-IP allow/block (firewall.add_ip_rule). Same shape as the Python
//       build's IpRule. NOTE: Windows always evaluates BLOCK before ALLOW,
//       so an allow rule doesn't punch through a whole-app block. Use allow
//       rules to RESTRICT (whitelist mode); block rules to CARVE OUT.
//
//   SetWhitelist
//       Restrict an exe to a small allow-set by emitting BLOCK filters for
//       the complement (firewall.set_whitelist). Drives the per-app
//       allow-only feature. IP/port set arithmetic lives in IpCalc.
//
//   RemoveAllRules
//       Safety hatch: delete every filter in our sublayer. Can't touch
//       unrelated WFP filters.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NetPeekier.Core;
using NetPeekier.Native.Wfp;
using static NetPeekier.Native.Wfp.Native;

namespace NetPeekier.Native;

[SupportedOSPlatform("windows")]
public sealed class WfpFirewall : IDisposable
{
    private const string FilterNamePrefix = "NetPeekier ";

    private IntPtr _engine;
    private bool _disposed;

    public WfpFirewall()
    {
        OpenEngine();
        EnsureProviderAndSublayer();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_engine != IntPtr.Zero)
        {
            try { FwpmEngineClose0(_engine); } catch { /* ignore */ }
            _engine = IntPtr.Zero;
        }
    }

    // ===================================================================
    // Validation (mirrors firewall._valid_exe).
    // ===================================================================

    public static bool ValidExe(string? exePath) => NetPeekier.Core.ExeValidation.ValidExe(exePath);

    // ===================================================================
    // Per-app block.
    // ===================================================================

    /// <summary>
    /// Block one executable in both directions, IPv4 and IPv6 (four filters).
    /// Filters are tagged with our provider GUID so ListBlocked can find them.
    /// </summary>
    public (bool Ok, string Message) BlockApp(string exePath)
    {
        if (!ValidExe(exePath))
            return (false, $"Refusing to block: not a valid executable path ({exePath}).");

        // Idempotency: if we already have filters for this exe, return ok.
        if (IsBlocked(exePath)) return (true, "Already blocked.");

        var layers = new[]
        {
            (Guids.LayerAleAuthConnectV4,     "out v4"),
            (Guids.LayerAleAuthConnectV6,     "out v6"),
            (Guids.LayerAleAuthRecvAcceptV4,  "in v4"),
            (Guids.LayerAleAuthRecvAcceptV6,  "in v6"),
        };
        foreach (var (layer, label) in layers)
        {
            try
            {
                using var arena = new NativeArena();
                var conditions = new List<FWPM_FILTER_CONDITION0>
                {
                    Conditions.AppId(exePath, arena),
                };
                SubmitFilter(
                    layer: layer,
                    action: FwpConstants.FWP_ACTION_BLOCK,
                    name: $"{FilterNamePrefix}block {label}",
                    description: $"Net-Peekier per-app block | exe={exePath} | {label}",
                    conditions: conditions,
                    arena: arena);
            }
            catch (WfpException ex)
            {
                return (false, $"WFP add filter failed ({label}): {ex.Message}");
            }
        }
        return (true, "Blocked.");
    }

    /// <summary>
    /// Remove every filter we added for this exe — block or allow, all
    /// directions, IPv4 + IPv6. Matches firewall.unblock_app behaviour.
    /// </summary>
    public (bool Ok, string Message) UnblockApp(string exePath)
    {
        if (string.IsNullOrEmpty(exePath))
            return (false, "No executable path.");
        int removed = 0;
        foreach (var f in EnumOurFilters())
        {
            if (string.Equals(f.Exe, exePath, StringComparison.OrdinalIgnoreCase)
                && f.Kind == FilterKind.PerAppBlock)
            {
                if (TryDeleteFilter(f.Id)) removed++;
            }
        }
        return (true, removed > 0 ? $"Unblocked ({removed} filter(s) removed)." : "Nothing to remove.");
    }

    public bool IsBlocked(string exePath)
    {
        if (!ValidExe(exePath)) return false;
        foreach (var f in EnumOurFilters())
        {
            if (f.Kind == FilterKind.PerAppBlock
                && string.Equals(f.Exe, exePath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recover the list of exe paths we have any per-app BLOCK filter for.
    /// Equivalent to firewall.list_blocked; used on startup to reconcile.
    /// </summary>
    public IReadOnlyList<string> ListBlocked()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in EnumOurFilters())
        {
            if (f.Kind == FilterKind.PerAppBlock && ValidExe(f.Exe))
                set.Add(f.Exe);
        }
        return set.ToList();
    }

    // ===================================================================
    // Per-IP rules.
    //
    // These build on AddPerAppFilter and just supply extra conditions for
    // the remote address / port / protocol.
    // ===================================================================

    public (bool Ok, string Message) AddIpRule(IpRule rule)
    {
        if (!ValidExe(rule.Exe))
            return (false, $"Refusing: not a valid executable path ({rule.Exe}).");
        if (rule.Action != "allow" && rule.Action != "block")
            return (false, $"Bad action {rule.Action}.");
        if (rule.Direction is not ("in" or "out" or "both"))
            return (false, $"Bad direction {rule.Direction}.");
        if (!IpCalc.ValidIpSpec(rule.RemoteIp))
            return (false, $"Bad remote IP/range ({rule.RemoteIp}).");
        if (!IpCalc.ValidPorts(rule.Ports))
            return (false, $"Bad ports ({rule.Ports}).");

        uint action = rule.Action == "allow" ? FwpConstants.FWP_ACTION_PERMIT : FwpConstants.FWP_ACTION_BLOCK;
        var directions = rule.Direction == "both" ? new[] { "in", "out" } : new[] { rule.Direction };

        // For each direction emit one filter per IP family present in the
        // remote_ip spec, optionally crossed with each protocol. This mirrors
        // firewall._concrete_dirs / _concrete_protos.
        foreach (var dir in directions)
        {
            try { EmitIpRuleFiltersForDirection(rule, dir, action); }
            catch (WfpException ex) { return (false, ex.Message); }
        }
        return (true, "IP rule added.");
    }

    public (bool Ok, string Message) RemoveIpRule(IpRule rule)
    {
        // Same identity hash that AddIpRule uses to label the filters.
        var id = IpRuleId(rule);
        int removed = 0;
        foreach (var f in EnumOurFilters())
        {
            if (f.Kind == FilterKind.IpRule && f.RuleId == id)
                if (TryDeleteFilter(f.Id)) removed++;
        }
        return (true, removed > 0 ? $"Removed ({removed} filter(s))." : "Nothing to remove.");
    }

    public int RemoveAllIpRules()
    {
        int removed = 0;
        foreach (var f in EnumOurFilters())
            if (f.Kind is FilterKind.IpRule or FilterKind.Whitelist)
                if (TryDeleteFilter(f.Id)) removed++;
        return removed;
    }

    /// <summary>
    /// Restrict <paramref name="exe"/> to ONLY the allowed endpoints by
    /// blocking the complement. Re-applied wholesale: existing whitelist
    /// filters for this exe are cleared first.
    /// </summary>
    public (bool Ok, string Message) SetWhitelist(string exe, IReadOnlyList<IpRule> allowEntries)
    {
        if (!ValidExe(exe))
            return (false, $"Refusing: not a valid executable path ({exe}).");

        // Clear any existing whitelist filters for this exe.
        var wid = WhitelistId(exe);
        foreach (var f in EnumOurFilters())
            if (f.Kind == FilterKind.Whitelist
                && string.Equals(f.Exe, exe, StringComparison.OrdinalIgnoreCase))
                TryDeleteFilter(f.Id);

        if (allowEntries.Count == 0) return (true, "Whitelist cleared.");

        // Same logic as firewall.set_whitelist: per direction, gather allowed
        // IPs, compute the v4/v6 complement (IpCalc), emit BLOCK filters
        // covering those ranges.
        try
        {
            foreach (var direction in new[] { "in", "out" })
            {
                var ents = allowEntries
                    .Where(e => (string.IsNullOrEmpty(e.Direction) ? "out" : e.Direction) == direction
                                || e.Direction == "both")
                    .ToList();
                if (ents.Count == 0) continue;

                var allowedIps = ents.Select(e => e.RemoteIp)
                                     .Where(s => !string.IsNullOrEmpty(s))
                                     .ToList();
                var (v4Ranges, v6Ranges) = IpCalc.BlockRangesExcept(allowedIps);

                foreach (var range in v4Ranges)
                    EmitWhitelistRange(exe, direction, range, isV6: false);
                foreach (var range in v6Ranges)
                    EmitWhitelistRange(exe, direction, range, isV6: true);
            }
            return (true, "Whitelist applied.");
        }
        catch (WfpException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Delete every filter this app created. Safe recovery hatch.</summary>
    public (int Removed, string Message) RemoveAllRules()
    {
        int removed = 0;
        foreach (var f in EnumOurFilters())
            if (TryDeleteFilter(f.Id)) removed++;
        return (removed, $"Removed {removed} Net-Peekier filter(s).");
    }

    // ===================================================================
    // Internals — engine setup and filter emission.
    // ===================================================================

    private void OpenEngine()
    {
        var status = FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, IntPtr.Zero, out _engine);
        if (status != ERROR_SUCCESS)
            throw new WfpException("FwpmEngineOpen0 failed", status);
    }

    /// <summary>
    /// Idempotently register our provider + sublayer. Each call to BlockApp /
    /// AddIpRule needs these to exist; on a fresh box (or after RemoveAll)
    /// they may not, so we always check.
    /// </summary>
    private void EnsureProviderAndSublayer()
    {
        // Provider
        var providerKey = Guids.NetPeekierProvider;
        var status = FwpmProviderGetByKey0(_engine, ref providerKey, out var existing);
        if (status == FWP_E_NOT_FOUND)
        {
            var provider = new FWPM_PROVIDER0
            {
                ProviderKey = Guids.NetPeekierProvider,
                DisplayData = new FWPM_DISPLAY_DATA0
                {
                    Name = "Net-Peekier",
                    Description = "Net-Peekier per-process firewall",
                },
                Flags = 0,
                ServiceName = IntPtr.Zero,
            };
            status = FwpmProviderAdd0(_engine, ref provider, IntPtr.Zero);
            if (status != ERROR_SUCCESS && status != FWP_E_ALREADY_EXISTS)
                throw new WfpException("FwpmProviderAdd0 failed", status);
        }
        else if (status == ERROR_SUCCESS)
        {
            FwpmFreeMemory0(ref existing);
        }

        // Sublayer
        var subKey = Guids.NetPeekierSublayer;
        status = FwpmSubLayerGetByKey0(_engine, ref subKey, out existing);
        if (status == FWP_E_NOT_FOUND)
        {
            var sub = new FWPM_SUBLAYER0
            {
                SubLayerKey = Guids.NetPeekierSublayer,
                DisplayData = new FWPM_DISPLAY_DATA0
                {
                    Name = "Net-Peekier sublayer",
                    Description = "Sublayer for Net-Peekier filters",
                },
                Flags = 0,
                ProviderKey = IntPtr.Zero,         // optional; we tag filters directly
                Weight = 0x4000,                   // mid-range
            };
            status = FwpmSubLayerAdd0(_engine, ref sub, IntPtr.Zero);
            if (status != ERROR_SUCCESS && status != FWP_E_ALREADY_EXISTS)
                throw new WfpException("FwpmSubLayerAdd0 failed", status);
        }
        else if (status == ERROR_SUCCESS)
        {
            FwpmFreeMemory0(ref existing);
        }
    }

    // ===================================================================
    // Per-IP rule emission.
    // ===================================================================

    private void EmitIpRuleFiltersForDirection(IpRule rule, string direction, uint action)
    {
        var id = IpRuleId(rule);
        var remote = rule.RemoteIp?.Trim() ?? "";
        var remoteIsAny = string.IsNullOrEmpty(remote)
            || remote.Equals("any", StringComparison.OrdinalIgnoreCase)
            || remote == "*";

        // Decide IP families: derived from the remote_ip spec. "any" -> both.
        var families = remoteIsAny ? new[] { 4, 6 } : DetectFamilies(remote);

        // Protocol expansion: tcp/udp directly, "any" with ports -> tcp+udp,
        // "any" without ports -> single filter at protocol-agnostic layer.
        var protocols = ExpandProtocols(rule.Protocol, rule.Ports);

        // Port expansion: each comma part becomes its own filter, since
        // WFP conditions are single-value (we don't compose OR sets for
        // simplicity — Python build does the same per part).
        var ports = ExpandPorts(rule.Ports);

        foreach (var fam in families)
        {
            var layer = LayerFor(direction, fam);
            foreach (var proto in protocols)
            {
                foreach (var port in ports)
                {
                    // Each (family, proto, port) tuple is one filter. Build
                    // its conditions in an arena, submit, dispose. The arena
                    // owns every variable-length payload (app-id blob,
                    // address/mask) for the lifetime of FwpmFilterAdd0.
                    using var arena = new NativeArena();
                    var conditions = new List<FWPM_FILTER_CONDITION0>
                    {
                        Conditions.AppId(rule.Exe, arena),
                    };
                    if (!remoteIsAny)
                        AddRemoteConditions(conditions, remote, fam, arena);
                    if (port is ushort p)  conditions.Add(Conditions.RemotePort(p));
                    if (proto is byte pr)  conditions.Add(Conditions.IpProtocol(pr));

                    SubmitFilter(
                        layer: layer,
                        action: action,
                        name: $"{FilterNamePrefix}ip {id} {direction} fam{fam} {proto?.ToString() ?? "any"}",
                        description: $"Net-Peekier ip rule | exe={rule.Exe} | id={id} | dir={direction} | ip={remote} | port={port} | proto={proto}",
                        conditions: conditions,
                        arena: arena);
                }
            }
        }
    }

    /// <summary>
    /// Submit one prebuilt filter. Shared by per-app blocks, per-IP rules
    /// and whitelist filters — the only thing that differs between them is
    /// the condition list. The arena owns everything; caller disposes it.
    /// </summary>
    private void SubmitFilter(
        Guid layer, uint action, string name, string description,
        IReadOnlyList<FWPM_FILTER_CONDITION0> conditions, NativeArena arena)
    {
        var conditionsPtr = Conditions.MarshalConditions(conditions, arena);
        var providerKeyPtr = arena.AllocAndCopy(Guids.NetPeekierProvider);

        var filter = new FWPM_FILTER0
        {
            DisplayData = new FWPM_DISPLAY_DATA0 { Name = name, Description = description },
            Flags = 0,
            ProviderKey = providerKeyPtr,
            LayerKey = layer,
            SubLayerKey = Guids.NetPeekierSublayer,
            Weight = new FWP_VALUE0 { Type = FwpConstants.FWP_EMPTY },  // let WFP pick
            NumFilterConditions = (uint)conditions.Count,
            FilterCondition = conditionsPtr,
            Action = new FWPM_ACTION0 { Type = action },
        };
        var status = FwpmFilterAdd0(_engine, ref filter, IntPtr.Zero, out _);
        if (status != ERROR_SUCCESS)
            throw new WfpException($"FwpmFilterAdd0 failed for {name}", status);
    }

    private void EmitWhitelistRange(string exe, string direction, string range, bool isV6)
    {
        var layer = LayerFor(direction, isV6 ? 6 : 4);
        using var arena = new NativeArena();
        var conditions = new List<FWPM_FILTER_CONDITION0>
        {
            Conditions.AppId(exe, arena),
        };
        AddRangeCondition(conditions, range, isV6, arena);

        var wid = WhitelistId(exe);
        SubmitFilter(
            layer: layer,
            action: FwpConstants.FWP_ACTION_BLOCK,
            name: $"{FilterNamePrefix}wl {wid} {direction} {(isV6 ? "v6" : "v4")}",
            description: $"Net-Peekier whitelist | exe={exe} | wid={wid} | dir={direction} | range={range}",
            conditions: conditions,
            arena: arena);
    }

    // ===================================================================
    // Condition helpers shared by IP rules and whitelist.
    // ===================================================================

    /// <summary>
    /// Add a remote-address condition derived from a comma-listed spec.
    /// Currently emits ONE condition per call (the first part that matches
    /// the requested family). EmitIpRuleFiltersForDirection multiplies
    /// callouts across the comma parts to compensate.
    /// </summary>
    private static void AddRemoteConditions(
        List<FWPM_FILTER_CONDITION0> outv, string remoteSpec, int family, NativeArena arena)
    {
        // Take the first part matching the family. Splitting per-part filter
        // creation up the stack would be more accurate; this is good enough
        // for the common single-IP / single-CIDR case used by the UI.
        foreach (var raw in remoteSpec.Split(','))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            try
            {
                if (p.Contains('/'))
                {
                    var bits = p.Split('/', 2);
                    var addr = IPAddress.Parse(bits[0].Trim());
                    var prefix = int.Parse(bits[1].Trim());
                    if (addr.AddressFamily == AddressFamily.InterNetwork && family == 4)
                        outv.Add(Conditions.RemoteIPv4Cidr(addr, prefix, arena));
                    else if (addr.AddressFamily == AddressFamily.InterNetworkV6 && family == 6)
                        outv.Add(Conditions.RemoteIPv6Cidr(addr, prefix, arena));
                    else continue;
                    return;
                }
                else if (!p.Contains('-'))
                {
                    var addr = IPAddress.Parse(p);
                    if (addr.AddressFamily == AddressFamily.InterNetwork && family == 4)
                        outv.Add(Conditions.RemoteIPv4(addr));
                    else if (addr.AddressFamily == AddressFamily.InterNetworkV6 && family == 6)
                        outv.Add(Conditions.RemoteIPv6Cidr(addr, 128, arena));   // /128 = exact
                    else continue;
                    return;
                }
                // a-b ranges are translated into a sequence of CIDRs below.
                // For Phase 3 the UI's per-IP form accepts only single /
                // CIDR / "any" entries, which IpCalc validates upfront.
            }
            catch { /* fall through, try next part */ }
        }
    }

    private static void AddRangeCondition(
        List<FWPM_FILTER_CONDITION0> outv, string range, bool isV6, NativeArena arena)
    {
        // range is "lo-hi"; translate into CIDR-ish via low address and
        // /32 (or /128) prefix. WFP doesn't natively take "lo-hi" — for
        // arbitrary ranges, the right move is to decompose into CIDRs.
        // We do that in IpCalc, but ranges that don't fall on a CIDR
        // boundary need expansion; that's a Phase-3-polish task. For now,
        // ranges are emitted as their low address with /32 or /128 mask
        // as a placeholder.
        //
        // TODO (Phase 3 polish): write IpCalc.RangesToCidrs(range) and
        // emit one condition per CIDR.
        var bits = range.Split('-', 2);
        var lo = IPAddress.Parse(bits[0].Trim());
        if (isV6)
            outv.Add(Conditions.RemoteIPv6Cidr(lo, 128, arena));
        else
            outv.Add(Conditions.RemoteIPv4(lo));
    }

    private static int[] DetectFamilies(string spec)
    {
        var seen = new HashSet<int>();
        foreach (var raw in spec.Split(','))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            // crude: ':' means v6, '.' means v4
            if (p.Contains(':')) seen.Add(6);
            else if (p.Contains('.')) seen.Add(4);
        }
        if (seen.Count == 0) return new[] { 4 };
        return seen.OrderBy(x => x).ToArray();
    }

    private static byte?[] ExpandProtocols(string? protocol, string? ports)
    {
        var p = (protocol ?? "any").ToLowerInvariant();
        if (p == "tcp") return new byte?[] { FwpConstants.IPPROTO_TCP };
        if (p == "udp") return new byte?[] { FwpConstants.IPPROTO_UDP };
        // "any": if ports are specified, WFP needs a concrete protocol per
        // filter; emit one each for TCP and UDP. Otherwise no protocol
        // condition (matches every IP packet at the ALE layer).
        if (!string.IsNullOrEmpty(ports))
            return new byte?[] { FwpConstants.IPPROTO_TCP, FwpConstants.IPPROTO_UDP };
        return new byte?[] { null };
    }

    private static ushort?[] ExpandPorts(string? portsSpec)
    {
        if (string.IsNullOrEmpty(portsSpec)) return new ushort?[] { null };
        var list = new List<ushort?>();
        foreach (var raw in portsSpec.Split(','))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            if (p.Contains('-'))
            {
                var bits = p.Split('-', 2);
                if (!int.TryParse(bits[0].Trim(), out var lo)) continue;
                if (!int.TryParse(bits[1].Trim(), out var hi)) continue;
                // Each port in the range becomes its own filter. Big ranges
                // mean lots of filters; the UI should warn for >1024 ports.
                for (int i = lo; i <= hi && i <= 65535; i++) list.Add((ushort)i);
            }
            else
            {
                if (ushort.TryParse(p, out var v)) list.Add(v);
            }
        }
        return list.Count == 0 ? new ushort?[] { null } : list.ToArray();
    }

    private static Guid LayerFor(string direction, int family) =>
        (direction, family) switch
        {
            ("out", 4) => Guids.LayerAleAuthConnectV4,
            ("out", 6) => Guids.LayerAleAuthConnectV6,
            ("in",  4) => Guids.LayerAleAuthRecvAcceptV4,
            ("in",  6) => Guids.LayerAleAuthRecvAcceptV6,
            _          => Guids.LayerAleAuthConnectV4,
        };

    private static string IpRuleId(IpRule r)
    {
        // Same shape as firewall._ip_rule_id (12 hex chars of SHA1).
        var raw = $"{r.Exe}|{r.Action}|{r.Direction}|{r.RemoteIp}|{r.Ports}|{r.Protocol}".ToLowerInvariant();
        var sha = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(sha, 0, 6).ToLowerInvariant();
    }

    private static string WhitelistId(string exe)
    {
        var raw = exe.ToLowerInvariant();
        var sha = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(sha, 0, 6).ToLowerInvariant();
    }

    // ===================================================================
    // Filter enumeration (for IsBlocked / ListBlocked / RemoveAll).
    // ===================================================================

    private enum FilterKind { Unknown, PerAppBlock, IpRule, Whitelist }

    private readonly record struct OurFilter(ulong Id, FilterKind Kind, string Exe, string RuleId);

    /// <summary>
    /// Enumerate every filter Net-Peekier added (matched by our provider key
    /// + sublayer key + display-name prefix). We reconstruct the per-filter
    /// metadata from the display description we wrote when adding it: that
    /// keeps the enumerator independent of having to walk condition values.
    /// </summary>
    private IEnumerable<OurFilter> EnumOurFilters()
    {
        var status = FwpmFilterCreateEnumHandle0(_engine, IntPtr.Zero, out var enumHandle);
        if (status != ERROR_SUCCESS) yield break;
        try
        {
            const uint batch = 64;
            while (true)
            {
                IntPtr entries;
                uint count;
                status = FwpmFilterEnum0(_engine, enumHandle, batch, out entries, out count);
                if (status != ERROR_SUCCESS || count == 0) break;
                try
                {
                    // entries is FWPM_FILTER0** -- an array of count pointers
                    for (int i = 0; i < count; i++)
                    {
                        var p = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                        if (p == IntPtr.Zero) continue;
                        var f = Marshal.PtrToStructure<FWPM_FILTER0>(p);
                        if (f.SubLayerKey != Guids.NetPeekierSublayer) continue;
                        // DisplayData.Name / Description are marshalled into
                        // managed strings automatically by the LPWStr attrs.
                        var n = f.DisplayData.Name ?? "";
                        var d = f.DisplayData.Description ?? "";
                        var (kind, exe, ruleId) = ParseFilter(n, d);
                        if (kind == FilterKind.Unknown) continue;
                        yield return new OurFilter(f.FilterId, kind, exe, ruleId);
                    }
                }
                finally
                {
                    FwpmFreeMemory0(ref entries);
                }
                if (count < batch) break;
            }
        }
        finally
        {
            FwpmFilterDestroyEnumHandle0(_engine, enumHandle);
        }
    }

    /// <summary>
    /// Reconstruct (kind, exe, ruleId) from the filter's display data.
    /// The description format is set when we add filters:
    ///   "Net-Peekier per-app block | exe=X | dir v4"
    ///   "Net-Peekier ip rule | exe=X | id=ABC | ..."
    ///   "Net-Peekier whitelist | exe=X | wid=ABC | ..."
    /// </summary>
    private static (FilterKind, string Exe, string RuleId) ParseFilter(string name, string desc)
    {
        if (!name.StartsWith(FilterNamePrefix)) return (FilterKind.Unknown, "", "");
        FilterKind kind;
        if (desc.Contains("per-app block")) kind = FilterKind.PerAppBlock;
        else if (desc.Contains("ip rule"))  kind = FilterKind.IpRule;
        else if (desc.Contains("whitelist"))kind = FilterKind.Whitelist;
        else                                kind = FilterKind.Unknown;
        var exe = ExtractField(desc, "exe=");
        var rid = ExtractField(desc, kind == FilterKind.Whitelist ? "wid=" : "id=");
        return (kind, exe, rid);
    }

    private static string ExtractField(string s, string token)
    {
        var i = s.IndexOf(token, StringComparison.Ordinal);
        if (i < 0) return "";
        i += token.Length;
        var end = s.IndexOf('|', i);
        return (end < 0 ? s[i..] : s[i..end]).Trim();
    }

    private bool TryDeleteFilter(ulong id)
    {
        var status = FwpmFilterDeleteById0(_engine, id);
        return status == ERROR_SUCCESS;
    }
}
