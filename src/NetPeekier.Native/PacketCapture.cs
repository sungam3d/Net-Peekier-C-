// Packet capture + payload view, via Npcap (SharpPcap + PacketDotNet).
//
// This is the replacement for the WinDivert sniff side of the Python build's
// packets window. It's READ-ONLY: Npcap sees traffic but never blocks it, so
// it sits entirely alongside our WFP enforcement and ETW counters without
// touching them.
//
// Npcap is an OPTIONAL dependency. It can't be bundled (its license requires
// users install it themselves), so:
//   * If Npcap isn't installed, Available stays false and the Packets window
//     shows an "Install Npcap" prompt. Everything else in the app works.
//   * SharpPcap/PacketDotNet are managed NuGet packages and always present;
//     only the native Npcap driver is the user-supplied piece. We detect its
//     absence by catching the DllNotFoundException / load error that
//     CaptureDeviceList throws when wpcap.dll is missing.
//
// PID correlation: Npcap gives us the packet 5-tuple but not the owning PID.
// We map (protocol, localPort) -> PID against the live connection table that
// ProcessMap already maintains. This is exactly what the Python build did;
// it's reliable for established connections and can occasionally miss a
// very short-lived socket.

using System.Runtime.Versioning;
using NetPeekier.Core;

namespace NetPeekier.Native;

[SupportedOSPlatform("windows")]
public sealed class PacketCapture : IDisposable
{
    /// <summary>True once capture is running on at least one adapter.</summary>
    public bool Available { get; private set; }

    /// <summary>True if Npcap (wpcap.dll) appears to be installed.</summary>
    public static bool NpcapInstalled => DetectNpcap();

    private readonly ProcessMap _procmap;
    private readonly object _gate = new();

    // Bounded ring buffer of recent packets. The GUI drains/reads this.
    private readonly LinkedList<CapturedPacket> _buffer = new();
    private int _capacity = 5000;

    // Capture filter state (set from the GUI).
    private int? _filterPid;
    private bool _paused;

    // SharpPcap devices we opened. Typed as object to keep the public
    // surface free of the SharpPcap types (so callers don't need the
    // reference); the real types are used only inside the guarded methods.
    private readonly List<IDisposable> _devices = new();

    public PacketCapture(ProcessMap procmap) { _procmap = procmap; }

    /// <summary>Total packets currently held in the ring buffer.</summary>
    public int Count { get { lock (_gate) return _buffer.Count; } }

    public bool Paused
    {
        get => _paused;
        set { _paused = value; Diag.Log($"PacketCapture: paused={value}"); }
    }

    /// <summary>Only keep packets for this PID (null = all processes).</summary>
    public int? FilterPid
    {
        get => _filterPid;
        set { _filterPid = value; Diag.Log($"PacketCapture: filterPid={value}"); }
    }

    public void SetCapacity(int n)
    {
        _capacity = Math.Max(100, n);
        lock (_gate) { TrimLocked(); }
    }

    public void Clear()
    {
        lock (_gate) { _buffer.Clear(); }
    }

    /// <summary>Drop all captured packets belonging to a pid (it went idle).</summary>
    public void ForgetPid(int pid)
    {
        lock (_gate)
        {
            var node = _buffer.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Pid == pid) _buffer.Remove(node);
                node = next;
            }
        }
    }

    /// <summary>
    /// Drop packets older than the given age in seconds. Called from the
    /// monitor tick using the "Keep packet logs for" preference, so the hex
    /// view retains only recent traffic. 0 / negative = keep everything (up to
    /// the ring-buffer capacity).
    /// </summary>
    public void PurgeOlderThan(double maxAgeSeconds)
    {
        if (maxAgeSeconds <= 0) return;
        double cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - maxAgeSeconds;
        lock (_gate)
        {
            // Packets are appended in time order, so the oldest are at the
            // front — remove from the front until we hit a fresh one.
            while (_buffer.First is { } f && f.Value.Timestamp < cutoff)
                _buffer.RemoveFirst();
        }
    }

    /// <summary>Newest-first snapshot of the buffer (optionally PID-filtered).</summary>
    public IReadOnlyList<CapturedPacket> Snapshot(int max = 2000)
    {
        lock (_gate)
        {
            var outp = new List<CapturedPacket>(Math.Min(max, _buffer.Count));
            // LinkedList is appended at tail; iterate from tail backwards for
            // newest-first.
            var node = _buffer.Last;
            while (node is not null && outp.Count < max)
            {
                outp.Add(node.Value);
                node = node.Previous;
            }
            return outp;
        }
    }

    public void Start()
    {
        if (Available) return;
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            StartInternal();
        }
        catch (Exception ex)
        {
            // DllNotFoundException here means Npcap isn't installed — that's
            // an expected, non-fatal condition. Anything else we log too.
            Diag.LogException("PacketCapture.Start", ex);
            Available = false;
        }
    }

    public void Stop()
    {
        lock (_devices)
        {
            foreach (var d in _devices)
            {
                try { d.Dispose(); } catch { /* ignore */ }
            }
            _devices.Clear();
        }
        Available = false;
    }

    public void Dispose() => Stop();

    // =====================================================================
    // SharpPcap-touching internals. Isolated so a missing wpcap.dll throws
    // only when we actually call in, letting Start() catch it cleanly.
    // =====================================================================

    private static bool DetectNpcap()
    {
        // Npcap installs wpcap.dll into System32 (and the Npcap subdir). A
        // cheap presence check avoids loading SharpPcap's native layer just
        // to discover it's missing.
        try
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (File.Exists(Path.Combine(sys, "wpcap.dll"))) return true;
            if (File.Exists(Path.Combine(sys, "Npcap", "wpcap.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    private void StartInternal()
    {
        var devices = SharpPcap.CaptureDeviceList.Instance;
        if (devices is null || devices.Count == 0)
        {
            Diag.Log("PacketCapture: no capture devices found");
            Available = false;
            return;
        }

        int opened = 0;
        foreach (var dev in devices)
        {
            // Only live adapters; skip the loopback meta-device handling to
            // keep it simple (loopback shows up as its own adapter on Npcap
            // with the right install option).
            try
            {
                if (dev is not SharpPcap.LibPcap.LibPcapLiveDevice live) continue;

                live.OnPacketArrival += OnPacketArrival;
                // Open via DeviceConfiguration (the canonical SharpPcap 6.x
                // overload). Mode None = not promiscuous: we want our own
                // machine's traffic, not the whole segment. Short read
                // timeout keeps the capture loop responsive.
                live.Open(new SharpPcap.DeviceConfiguration
                {
                    Mode = SharpPcap.DeviceModes.None,
                    ReadTimeout = 250,
                });
                live.StartCapture();
                lock (_devices) _devices.Add(live);
                opened++;
            }
            catch (Exception ex)
            {
                Diag.LogException($"PacketCapture.open[{dev.Name}]", ex);
            }
        }

        Available = opened > 0;
        Diag.Log($"PacketCapture: opened {opened} adapter(s); available={Available}");
    }

    private void OnPacketArrival(object sender, SharpPcap.PacketCapture e)
    {
        if (_paused) return;
        try
        {
            var raw = e.GetPacket();
            var parsed = PacketDotNet.Packet.ParsePacket(raw.LinkLayerType, raw.Data);

            var ip = parsed.Extract<PacketDotNet.IPPacket>();
            if (ip is null) return;

            string proto;
            int srcPort = 0, dstPort = 0;
            byte[] payload = Array.Empty<byte>();

            var tcp = parsed.Extract<PacketDotNet.TcpPacket>();
            var udp = parsed.Extract<PacketDotNet.UdpPacket>();
            if (tcp is not null)
            {
                proto = "TCP";
                srcPort = tcp.SourcePort;
                dstPort = tcp.DestinationPort;
                payload = tcp.PayloadData ?? Array.Empty<byte>();
            }
            else if (udp is not null)
            {
                proto = "UDP";
                srcPort = udp.SourcePort;
                dstPort = udp.DestinationPort;
                payload = udp.PayloadData ?? Array.Empty<byte>();
            }
            else
            {
                proto = ip.Protocol.ToString().ToUpperInvariant();
                payload = ip.PayloadData ?? Array.Empty<byte>();
            }

            // Decide direction by which endpoint is one of our local
            // addresses. ProcessMap knows our local socket table; we use the
            // local-port match for both direction and PID.
            var srcIp = ip.SourceAddress.ToString();
            var dstIp = ip.DestinationAddress.ToString();

            // Try outbound first: src is local. The PID lookup keys on the
            // local (source) port for outbound, dest port for inbound.
            int? pidOut = _procmap.PidForPort(proto, srcPort);
            int? pidIn  = _procmap.PidForPort(proto, dstPort);

            bool outbound;
            int localPort, remotePort, pid;
            string localIp, remoteIp;

            if (pidOut is not null)
            {
                outbound = true;
                localIp = srcIp; localPort = srcPort;
                remoteIp = dstIp; remotePort = dstPort;
                pid = pidOut.Value;
            }
            else if (pidIn is not null)
            {
                outbound = false;
                localIp = dstIp; localPort = dstPort;
                remoteIp = srcIp; remotePort = srcPort;
                pid = pidIn.Value;
            }
            else
            {
                // No PID match — still record it, guessing direction by
                // private-ness of the source address isn't reliable, so we
                // default to treating the lower-typically-ephemeral side as
                // local. Keep it simple: assume outbound from src.
                outbound = true;
                localIp = srcIp; localPort = srcPort;
                remoteIp = dstIp; remotePort = dstPort;
                pid = -1;
            }

            // Apply the PID filter (if set). -1 (unknown) is shown only when
            // no filter is active.
            if (_filterPid is int want)
            {
                if (pid != want) return;
            }

            var pkt = new CapturedPacket
            {
                Timestamp  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                Outbound   = outbound,
                Protocol   = proto,
                LocalIp    = localIp,
                LocalPort  = localPort,
                RemoteIp   = remoteIp,
                RemotePort = remotePort,
                Length     = raw.Data.Length,
                Pid        = pid > 0 ? pid : null,
                ProcessName = pid > 0 ? _procmap.Name(pid) : "",
                Payload    = payload,
            };

            lock (_gate)
            {
                _buffer.AddLast(pkt);
                TrimLocked();
            }
        }
        catch (Exception ex)
        {
            // A malformed frame shouldn't kill the capture thread.
            Diag.LogException("PacketCapture.OnPacketArrival", ex);
        }
    }

    private void TrimLocked()
    {
        while (_buffer.Count > _capacity)
            _buffer.RemoveFirst();
    }
}
