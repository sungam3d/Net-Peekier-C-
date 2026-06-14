// Port of netpeekier/ipcalc.py — interval maths for the per-process whitelist.
//
// Windows Firewall (and the WFP engine underneath it) evaluates block rules
// before allow rules, so "allow only IP X" cannot be done with an allow rule.
// Instead we invert it: compute every IP / port *except* the allowed ones
// and emit those as BLOCK filters.
//
// This module does the set arithmetic. Pure functions only -- no Win32, no
// GUI. This is the part most worth testing hard, hence Phase 1's exit
// criterion in MIGRATION_PLAN.md.

using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace NetPeekier.Core;

public static class IpCalc
{
    private static readonly BigInteger V4Max = (BigInteger.One << 32)  - 1;
    private static readonly BigInteger V6Max = (BigInteger.One << 128) - 1;

    /// <summary>One contiguous IP-range interval. Family is 4 or 6.</summary>
    private readonly record struct Interval(int Family, BigInteger Lo, BigInteger Hi);

    // =====================================================================
    // Strict validators (single source of truth; firewall delegates here).
    // =====================================================================

    /// <summary>
    /// True if <paramref name="spec"/> is a single IP / CIDR / a-b range /
    /// comma-list of those, or "any" / "*". Rejects reversed ranges and
    /// mixed v4/v6 ranges. Strict: every comma part must be valid.
    /// </summary>
    public static bool ValidIpSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;
        spec = spec.Trim();
        if (spec.Equals("any", StringComparison.OrdinalIgnoreCase) || spec == "*")
            return true;
        foreach (var raw in spec.Split(','))
        {
            var p = raw.Trim();
            if (p.Length == 0) return false;
            try
            {
                if (p.Contains('-') && !p.Contains('/'))
                {
                    var bits = p.Split('-', 2);
                    var a = IPAddress.Parse(bits[0].Trim());
                    var b = IPAddress.Parse(bits[1].Trim());
                    if (a.AddressFamily != b.AddressFamily) return false;
                    if (ToBig(a) > ToBig(b)) return false;
                }
                else if (p.Contains('/'))
                {
                    if (!TryParseCidr(p, out _, out _)) return false;
                }
                else
                {
                    _ = IPAddress.Parse(p);
                }
            }
            catch { return false; }
        }
        return true;
    }

    /// <summary>
    /// True if <paramref name="spec"/> is empty (= all ports) or a comma list
    /// of ports / ranges in 1..65535. Rejects reversed ranges. Strict: every
    /// part must be valid.
    /// </summary>
    public static bool ValidPorts(string? spec)
    {
        if (string.IsNullOrEmpty(spec)) return true;
        foreach (var raw in spec.Split(','))
        {
            var p = raw.Trim();
            if (p.Length == 0) return false;
            if (p.Contains('-'))
            {
                var bits = p.Split('-', 2);
                if (!int.TryParse(bits[0].Trim(), out var a)) return false;
                if (!int.TryParse(bits[1].Trim(), out var b)) return false;
                if (a < 1 || a > 65535 || b < 1 || b > 65535 || a > b) return false;
            }
            else
            {
                if (!int.TryParse(p, out var n)) return false;
                if (n < 1 || n > 65535) return false;
            }
        }
        return true;
    }

    // =====================================================================
    // IP intervals.
    // =====================================================================

    /// <summary>
    /// Given a list of allowed IP specs, return (v4Ranges, v6Ranges) as
    /// "lo-hi" range strings covering EVERYTHING EXCEPT the allowed addresses.
    ///
    /// If a family has no allowed entries, its full range is returned (so
    /// allowing only an IPv4 also blocks all IPv6, and vice-versa) -- a
    /// strict whitelist. If allowed covers an entire family, that family's
    /// list is empty (nothing to block there).
    /// </summary>
    public static (List<string> V4, List<string> V6) BlockRangesExcept(
        IEnumerable<string> allowedSpecs)
    {
        var v4 = new List<(BigInteger Lo, BigInteger Hi)>();
        var v6 = new List<(BigInteger Lo, BigInteger Hi)>();
        foreach (var spec in allowedSpecs)
            foreach (var iv in SpecToIntervals(spec))
                (iv.Family == 4 ? v4 : v6).Add((iv.Lo, iv.Hi));

        var v4Ranges = Complement(v4, BigInteger.Zero, V4Max)
            .Select(r => $"{V4String(r.Lo)}-{V4String(r.Hi)}")
            .ToList();
        var v6Ranges = Complement(v6, BigInteger.Zero, V6Max)
            .Select(r => $"{V6String(r.Lo)}-{V6String(r.Hi)}")
            .ToList();
        return (v4Ranges, v6Ranges);
    }

    /// <summary>
    /// Comma-separated remote-port string for every port 1..65535 EXCEPT
    /// those in <paramref name="spec"/>. Empty result means the spec
    /// already covers all ports.
    /// </summary>
    public static string ComplementPorts(string? spec)
    {
        var intervals = PortIntervals(spec);
        var bigIntervals = intervals
            .Select(p => ((BigInteger)p.Lo, (BigInteger)p.Hi))
            .ToList();
        var comp = Complement(bigIntervals, BigInteger.One, 65535);
        return string.Join(",",
            comp.Select(r => r.Lo == r.Hi
                ? r.Lo.ToString()
                : $"{r.Lo}-{r.Hi}"));
    }

    // =====================================================================
    // Internals.
    // =====================================================================

    /// <summary>
    /// Parse an IP spec (single / CIDR / a-b range / comma list) into a list
    /// of intervals. Unparseable parts are skipped (lenient -- callers that
    /// want strict validation use ValidIpSpec first).
    /// </summary>
    private static List<Interval> SpecToIntervals(string spec)
    {
        var outv = new List<Interval>();
        if (string.IsNullOrEmpty(spec)) return outv;
        foreach (var raw in spec.Split(','))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            if (p.Equals("any", StringComparison.OrdinalIgnoreCase) || p == "*") continue;
            try
            {
                if (p.Contains('-') && !p.Contains('/'))
                {
                    var bits = p.Split('-', 2);
                    var lo = IPAddress.Parse(bits[0].Trim());
                    var hi = IPAddress.Parse(bits[1].Trim());
                    if (lo.AddressFamily != hi.AddressFamily) continue;
                    var fam = lo.AddressFamily == AddressFamily.InterNetwork ? 4 : 6;
                    var a = ToBig(lo); var b = ToBig(hi);
                    outv.Add(new Interval(fam, BigInteger.Min(a, b), BigInteger.Max(a, b)));
                }
                else if (p.Contains('/'))
                {
                    if (TryParseCidr(p, out var fam, out var range))
                        outv.Add(new Interval(fam, range.Lo, range.Hi));
                }
                else
                {
                    var a = IPAddress.Parse(p);
                    var fam = a.AddressFamily == AddressFamily.InterNetwork ? 4 : 6;
                    var n = ToBig(a);
                    outv.Add(new Interval(fam, n, n));
                }
            }
            catch
            {
                // skip; matches Python behaviour
            }
        }
        return outv;
    }

    private static List<(int Lo, int Hi)> PortIntervals(string? spec)
    {
        var outv = new List<(int, int)>();
        if (string.IsNullOrEmpty(spec)) return outv;
        foreach (var raw in spec.Split(','))
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            try
            {
                int a, b;
                if (p.Contains('-'))
                {
                    var bits = p.Split('-', 2);
                    a = int.Parse(bits[0]);
                    b = int.Parse(bits[1]);
                }
                else
                {
                    a = b = int.Parse(p);
                }
                if (a > b) (a, b) = (b, a);
                outv.Add((Math.Max(1, a), Math.Min(65535, b)));
            }
            catch { /* skip; matches Python behaviour */ }
        }
        return outv;
    }

    /// <summary>Merge overlapping / adjacent [lo,hi] intervals.</summary>
    private static List<(BigInteger Lo, BigInteger Hi)> Merge(
        List<(BigInteger Lo, BigInteger Hi)> intervals)
    {
        if (intervals.Count == 0) return new();
        var sorted = intervals.OrderBy(x => x.Lo).ThenBy(x => x.Hi).ToList();
        var merged = new List<(BigInteger Lo, BigInteger Hi)> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var (lo, hi) = sorted[i];
            var (mLo, mHi) = merged[^1];
            if (lo <= mHi + 1)
                merged[^1] = (mLo, BigInteger.Max(mHi, hi));
            else
                merged.Add((lo, hi));
        }
        return merged;
    }

    /// <summary>Everything in [loBound, hiBound] not covered by intervals.</summary>
    private static List<(BigInteger Lo, BigInteger Hi)> Complement(
        List<(BigInteger Lo, BigInteger Hi)> intervals,
        BigInteger loBound, BigInteger hiBound)
    {
        var clipped = intervals
            .Select(x => (Lo: BigInteger.Max(x.Lo, loBound),
                          Hi: BigInteger.Min(x.Hi, hiBound)))
            .Where(x => x.Hi >= loBound && x.Lo <= hiBound)
            .ToList();
        var merged = Merge(clipped);

        var result = new List<(BigInteger Lo, BigInteger Hi)>();
        var cur = loBound;
        foreach (var (lo, hi) in merged)
        {
            if (lo > cur) result.Add((cur, lo - 1));
            cur = BigInteger.Max(cur, hi + 1);
            if (cur > hiBound) break;
        }
        if (cur <= hiBound) result.Add((cur, hiBound));
        return result;
    }

    private static bool TryParseCidr(string p, out int family,
                                     out (BigInteger Lo, BigInteger Hi) range)
    {
        family = 0; range = (0, 0);
        var bits = p.Split('/', 2);
        if (bits.Length != 2) return false;
        if (!IPAddress.TryParse(bits[0].Trim(), out var addr)) return false;
        if (!int.TryParse(bits[1].Trim(), out var prefix)) return false;
        family = addr.AddressFamily == AddressFamily.InterNetwork ? 4 : 6;
        var maxBits = family == 4 ? 32 : 128;
        if (prefix < 0 || prefix > maxBits) return false;

        var bigAddr = ToBig(addr);
        // Mask the address to the network address (matches Python's
        // strict=False behaviour: any host bits in the input are ignored).
        var totalBits = (BigInteger.One << maxBits) - 1;
        var hostBits  = maxBits - prefix;
        BigInteger mask;
        if (hostBits == 0)         mask = totalBits;
        else if (hostBits >= maxBits) mask = BigInteger.Zero;
        else                       mask = totalBits ^ ((BigInteger.One << hostBits) - 1);

        var network   = bigAddr & mask;
        var broadcast = network | ((BigInteger.One << hostBits) - 1);
        range = (network, broadcast);
        return true;
    }

    private static BigInteger ToBig(IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        // BigInteger ctor wants little-endian; IPAddress bytes are big-endian.
        // Reverse and append a 0 byte so v4 / v6 are never interpreted as
        // negative.
        Array.Reverse(b);
        var padded = new byte[b.Length + 1];
        Buffer.BlockCopy(b, 0, padded, 0, b.Length);
        return new BigInteger(padded);
    }

    private static string V4String(BigInteger n)
    {
        var b = new byte[4];
        for (int i = 3; i >= 0; i--) { b[i] = (byte)(n & 0xff); n >>= 8; }
        return new IPAddress(b).ToString();
    }

    private static string V6String(BigInteger n)
    {
        var b = new byte[16];
        for (int i = 15; i >= 0; i--) { b[i] = (byte)(n & 0xff); n >>= 8; }
        return new IPAddress(b).ToString();
    }
}
