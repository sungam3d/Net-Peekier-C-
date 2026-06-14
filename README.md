# Net-Peekier (C# port)

C# / .NET 8 rebuild of [Net-Peekier](../) — same UX (dashboard + app list +
firewall + monitor), but **no third-party kernel driver**. Enforcement is the
OS's own WFP filter engine via its user-mode API; monitoring is ETW.

## Status

Phase 0 + most of Phase 1 are in place. The native and GUI layers are
scaffolded with the right interface shapes so the implementation lands
chunk-by-chunk without disturbing the rest. See **MIGRATION_PLAN.md** in the
repo root for the live checklist.

## Build

You need .NET 8 SDK and (eventually) a Windows machine to run.

```powershell
# from the NetPeekier folder
dotnet restore
dotnet build
```

To produce a single-file exe:

```powershell
dotnet publish src/NetPeekier.App/NetPeekier.App.csproj -c Release
```

The output is `src/NetPeekier.App/bin/Release/net8.0-windows/win-x64/publish/NetPeekier.exe`.
It is fully self-contained: no .NET runtime install required on the target
machine.

## Why three projects

- **`NetPeekier.Core`** — models, settings, IP math, history. Plain C#, no
  Win32, no GUI. Unit-testable. Mirror of `netpeekier/models.py`,
  `settings.py`, `ipcalc.py`, `util.py`, `history.py`.
- **`NetPeekier.Native`** — Windows-specific interop: IP Helper for the
  connection table, ETW for byte counters, WFP for firewall enforcement,
  PerformanceCounter/LibreHWM for system stats. Mirror of `capture.py`,
  `procmap.py`, `firewall.py`, `sysstats.py`, plus the `Monitor` orchestrator.
- **`NetPeekier.App`** — WPF GUI. Mirror of `netpeekier/gui/*`.

The split keeps the Core layer testable on any machine and lets you swap GUIs
(WPF → Avalonia for the AOT path, say) without disturbing the engine.

## Hardware temperatures

The System Stats window (View → Statistics) shows CPU/GPU/RAM load and clock,
plus temperatures and GPU details. The sensor library
(LibreHardwareMonitorLib, MPL-2.0) is bundled via NuGet — there's nothing to
install by hand.

Reading hardware sensors needs administrator privileges (the embedded
manifest already requests this). If you launch without elevation, load/clock
still work but temperatures show `--` and the status bar says so. Some
motherboard sensors additionally need the PawnIO driver; CPU/GPU temps
generally work without it.

## Optional: per-process byte rates (ETW)

Per-process upload/download numbers come from an ETW kernel session, which
needs administrator privileges. Run elevated and the Up/Down columns populate
and the status bar reads `backend: ETW Kernel-Network`. Without elevation the
app still runs, showing the connection table and system-wide totals, with the
status bar noting per-process speeds are unavailable.

## Optional: packet capture & payload view (Npcap)

The Packets window (View → Packets) shows live traffic with a Wireshark-style
hex/ASCII payload dump, per-process filtering, and pause/clear. This uses
Npcap, the same capture driver Wireshark ships.

Npcap isn't bundled (its license requires you install it yourself). If it's
missing, the Packets window shows a one-click link to the installer; the rest
of the app is unaffected. During the Npcap install, leave "WinPcap
API-compatible mode" checked, then restart Net-Peekier as administrator.

Capture is read-only — it never blocks traffic. Blocking is still handled by
WFP, entirely separately. The packet-to-process mapping correlates each
packet's local port against the live connection table (the same approach the
original Python build used); it's reliable for established connections and may
occasionally miss a very short-lived socket.
