# Net-Peekier → C# / .NET 8 migration plan

Living progress doc. As pieces land, the checkboxes get ticked and notes get
appended.

> **Tech choices made (defaults — change if you prefer):**
> - **Language:** C# 12 / .NET 8
> - **GUI:** WPF (MVVM). Mature, well-documented, friendlier to single-exe
>   publishing than WinUI 3 right now. We can swap to WinUI later — the
>   `NetPeekier.Core` layer is GUI-agnostic.
> - **Publish:** `dotnet publish -c Release -r win-x64 --self-contained` with
>   `PublishSingleFile=true`.
> - **Privileges:** embedded manifest requests `requireAdministrator`. WFP
>   filter changes and ETW kernel sessions both need it.
> - **Enforcement engine:** WFP user-mode API (`fwpuclnt.dll`) — same kernel
>   firewall engine Windows itself uses. **No third-party driver. No netsh.**
> - **Monitoring engine:** ETW (`Microsoft-Windows-Kernel-Network` provider)
>   for per-process byte counts + IP Helper (`iphlpapi.dll`
>   `GetExtendedTcpTable` / `GetExtendedUdpTable`) for the connection table.

---

## Honest scope changes vs. the Python app

| Feature                                  | Python (now)              | C# port plan                   |
|------------------------------------------|---------------------------|--------------------------------|
| Process list + connections + ports       | psutil                    | **IP Helper API** ✅           |
| Per-process up/down bytes (now + total)  | WinDivert sniff           | **ETW Kernel-Network** (pending) |
| Per-app block (persistent)               | netsh                     | **WFP filter, per-app token** ✅ |
| Per-IP allow / block / whitelist         | netsh + IP-set arithmetic | **WFP filter**, same arithmetic ✅ |
| Lockdown / default-deny                  | netsh sweep               | **WFP** sweep, identical UX ✅ |
| Tags, group limits, etc.                 | settings + WinDivert      | settings + WFP                 |
| Live packet view + hex dump              | WinDivert                 | **Npcap** (optional, read-only)|
| Per-process speed limit (throttle)       | WinDivert token bucket    | **DROPPED** *(see note)*       |
| System stats (CPU/GPU/RAM/temps)         | psutil + HardwareMonitor  | PerfCounter + LibreHWM dll     |
| History log                              | plain file                | plain file ✅                  |

**Feature notes:**

1. **Hex/packet view is BACK, via Npcap.** Originally dropped (it needs a
   kernel-level capture component), it's now restored using Npcap — the same
   signed, trusted driver Wireshark uses — instead of WinDivert. Npcap is an
   optional user-installed dependency: if absent, the Packets window shows an
   install prompt and the rest of the app is unaffected. Capture is
   read-only and completely separate from the WFP blocking path, so it can't
   cause the kind of packet-path instability the old WinDivert build had.
   PID attribution is by local-port correlation against the connection table
   (same approach as the Python build).
2. **Throttling is still replaced with finer-grained blocking.** Inline
   byte-rate throttling genuinely needs to sit *in* the packet path (not just
   observe it), which Npcap can't do — that remains a WFP per-IP-rule story:
   "Chrome can talk to *these* IPs/ports but nothing else".

---

## Solution layout

```
NetPeekier.sln
├── src/
│   ├── NetPeekier.Core/        ← models, settings, IP math, history
│   │                             (GUI-free, unit-testable, no Win32 calls)
│   ├── NetPeekier.Native/      ← P/Invoke wrappers + ETW / IP Helper / WFP
│   │                             (Windows-only, low-level)
│   └── NetPeekier.App/         ← WPF GUI + MVVM, the executable
│       ├── ViewModels/         ← MainViewModel, ProcessRow, ObservableObject
│       └── Views/              ← MainWindow, ConnectionsWindow, FirewallWindow,
│                                  IpRuleDialog
├── tests/                     ← stand-alone offline test runner
│   └── NetPeekier.Core.Tests/
└── publish/                   ← `dotnet publish` output (single exe)
```

---

## Phases

### Phase 0 — Scaffolding ✅ DONE
- [x] Solution + three projects
- [x] Embedded manifest with `requireAdministrator`
- [x] `dotnet publish` wired to single-exe
- [x] Core + Native compile clean against .NET 8

### Phase 1 — Core layer (no Win32) ✅ DONE
- [x] `Models.cs`: Connection, ConnectionEvent, ProcStat, Totals
- [x] `Paths.cs`: Root, settings.txt, log/
- [x] `Settings.cs`: load/save with atomic write, all Python fields
- [x] `Formatting.cs`: HumanSpeed / HumanBytes / PortsStr
- [x] `IpCalc.cs`: full port of Python interval math (BigInteger v4 + v6)
- [x] `History.cs`: rolling log
- [x] **97/97 tests passing**: IpCalc, Settings round-trip, Formatting, WfpFirewall.ValidExe

### Phase 2 — Native: monitor (read-only) ✅ DONE
- [x] `IpHelper.cs`: hand-rolled P/Invoke for TCP4 / TCP6 / UDP4 / UDP6 tables
- [x] `ProcessMap.cs`: pid↔endpoint table, name/exe cache, PID-reuse check
- [x] `NetworkMonitor.cs`: full tick body ported from `monitor._tick`
      (idle hiding, WAN/LAN classification, history logging, totals,
      dead-PID cleanup, system-IO baseline + fallback)
- [x] `EtwMonitor.cs`: real implementation against
      `Microsoft.Diagnostics.Tracing.TraceEvent`. Opens the NT Kernel
      Logger, subscribes to TcpIp{Send,Recv} + TcpIp{Send,Recv}IPV6 +
      UdpIp{Send,Recv} + UdpIp{Send,Recv}IPV6, accumulates bytes per-PID
      and per-ConnectionKey. NuGet restore now required for Native.
      Falls back cleanly when not elevated or kernel session is
      already held by another consumer.

### Phase 3 — Native: WFP firewall ✅ STRUCTURALLY COMPLETE
- [x] `Wfp/Native.cs`: hand-rolled P/Invoke for `fwpuclnt.dll`
      (engine open/close, transactions, provider / sublayer / filter CRUD,
      FwpmGetAppIdFromFileName0)
- [x] `Wfp/Guids.cs`: well-known layer + condition GUIDs, plus stable
      provider + sublayer GUIDs that identify Net-Peekier
- [x] `Wfp/Conditions.cs`: arena-based condition builders for AppId,
      RemoteIPv4 / IPv4Cidr / IPv6Cidr, RemotePort, IpProtocol
- [x] `WfpFirewall.cs`: BlockApp / UnblockApp / IsBlocked / ListBlocked,
      AddIpRule / RemoveIpRule / RemoveAllIpRules, SetWhitelist (uses
      `IpCalc.BlockRangesExcept`), RemoveAllRules
- [x] Monitor wires it all together: `Firewall` property (lazy, null on
      non-Windows or non-elevated), `SyncFirewall` reconciliation,
      `SetBlocked` / `AddIpRule` / `RemoveIpRule` / `SetFirewallEnabled` /
      `RemoveAllFirewallRules` direct edit surface, `LockdownSweep` full
      implementation including temp-allow expiry
- [ ] **Verify on a Windows box** — the WFP code compiles clean but has
      not been exercised against a real `fwpuclnt.dll`. Smoke-test plan:
      1. Launch elevated, confirm `Firewall is not null`.
      2. Block notepad.exe → run notepad with `curl http://example.com` →
         confirm `ECONNREFUSED`.
      3. Unblock, re-run, confirm traffic flows.
      4. Add an IP rule for notepad, verify in `netsh wfp show filters` it
         shows under our sublayer GUID.

### Phase 4 — GUI ✅ COMPLETE
- [x] MVVM scaffold: `ObservableObject`, `RelayCommand`, `MainViewModel`
- [x] MainWindow: dashboard (up/down/peak/session), live DataGrid bound to
      `Processes`, in-place row reconciliation by PID (no flicker / no
      selection loss), menu with File / Firewall / View / Help
- [x] ConnectionsWindow: double-click a process to see its live socket
      list, refreshes 1Hz, closes itself if the PID disappears
- [x] FirewallWindow: tabs for blocked apps + IP rules; unblock /
      add / remove / remove-all
- [x] IpRuleDialog: modal form with `IpCalc.ValidIpSpec` / `ValidPorts` /
      `WfpFirewall.ValidExe` validation before OK
- [x] SettingsWindow: speed unit, idle hide, lockdown toggle, temp-allow
      duration, show-LAN/WAN, packet-purge minutes, LAN ranges with CIDR
      validation; calls `ApplySettings(syncFirewall: true)` so changes
      take effect immediately
- [x] LockdownDialog: prompted from `App.ShowLockdownPrompt`, three
      buttons (block permanently / temp allow / allow permanently) wired
      to Monitor.LockdownBlock / AllowTemporarily / SetAllowed; per-exe
      dedupe so a chatty process doesn't pile up dialogs
- [x] AboutWindow: proper window with version from assembly metadata
- [x] Window-geometry persistence: WindowGeometryPersistence helper
      restores Left/Top/Width/Height on open and saves on close (Main,
      Connections, Firewall use it)
- [x] StatsWindow + SystemStats backend (CPU/RAM via PerformanceCounter,
      CPU clock via registry, optional CPU/GPU/RAM temps + GPU load/clock
      via LibreHardwareMonitorLib.dll loaded by reflection — drop the dll
      next to the exe to light up temps, absent = "--")
- [x] Hierarchical app list: TreeView grouping PIDs by process name
      (the svchost.exe cluster collapses under one expandable parent with
      summed up/down/total). Reconciled in place by name+PID so expand/
      collapse and selection survive ticks. Group or leaf both selectable;
      block/unblock and double-click-for-connections resolve a PID from
      either. Matches the Python build's Treeview UX.

### Phase 5 — Polish + publish
- [x] Version metadata embedded in NetPeekier.App.csproj (2.0.0)
- [x] `Microsoft.Diagnostics.Tracing.TraceEvent` integrated;
      `EtwMonitor` is now the real implementation (Phase 2 task moved
      here historically; now actually done)
- [x] `dotnet publish` produces a single `NetPeekier.exe` (verified
      on Windows 11 26200, single-file self-contained)
- [ ] App icon (provide an .ico, reference from csproj)
- [ ] Sign the exe (optional; smooths SmartScreen)
- [ ] Smoke test on Windows 11 23H2 + 24H2 fresh installs
      (preliminary verification on 26200 ✓)
- [ ] Decide AOT / Avalonia migration (notes at bottom)

---

## AOT path (later)

WPF doesn't target NativeAOT. Two routes:

1. **Stay on WPF + PublishSingleFile + trimming.** One ~30–60 MB exe with
   the .NET runtime bundled.
2. **Move the GUI to Avalonia.** Avalonia supports AOT today and the API
   is close enough to WPF that the migration is mechanical. Result: a
   tiny native exe with no .NET install required.

Decide at Phase 5.

---

## Build & run

```powershell
dotnet restore        # once you're online (pulls TraceEvent when re-enabled)
dotnet build
dotnet run --project tests/NetPeekier.Core.Tests
dotnet publish src/NetPeekier.App/NetPeekier.App.csproj -c Release
# Output: src/NetPeekier.App/bin/Release/net8.0-windows/win-x64/publish/NetPeekier.exe
```

The included `nuget.config` clears package sources to let the sandbox build
without internet. Delete it (or replace it with your normal one) on a
machine with NuGet access — you'll want `TraceEvent` for Phase 5.

---

## Status log (newest first)

- **2026-06-14 — Packet capture + hex view restored (via Npcap).**
  - The payload/hex packet view — originally dropped with WinDivert — is
    back, built on Npcap (SharpPcap 6.3 + PacketDotNet 1.4). Read-only
    capture that sits alongside WFP/ETW without touching them, so none of
    the old WinDivert packet-path instability applies.
  - `PacketCapture` (Native): opens all live adapters, parses each frame to
    its transport payload, correlates to a PID by local-port lookup against
    ProcessMap's connection table, and pushes into a bounded ring buffer
    (5000). Supports pause, clear, capacity, and per-PID filter. Detects
    Npcap by probing for wpcap.dll and degrades gracefully if absent
    (Available=false).
  - `PacketsWindow` (App): live grid (time/proc/pid/proto/dir/local/remote/
    len) over a Wireshark-style offset/hex/ASCII payload dump, with a
    process filter dropdown, pause/resume, and clear. If Npcap is missing,
    shows a "Download Npcap" prompt with a re-check button instead. Capture
    starts lazily on first open (it has overhead). New CapturedPacket model
    in Core carries the payload bytes; hex formatter verified against a real
    HTTP request.
  - Npcap is optional + user-installed (license can't be bundled). Added to
    README + THIRD-PARTY-LICENSES.
  - 97/97 tests still pass.

- **2026-06-14 — LibreHardwareMonitor bundled via NuGet (no more drop-in dll).**
  - The reflection-loaded "drop LibreHardwareMonitorLib.dll next to the exe"
    approach was confusing in practice (the dll has to sit beside the
    *published* exe, not the build output, and the published single-file
    exe self-extracts to a temp dir, so a hand-placed dll wasn't found).
    Replaced it with a proper `LibreHardwareMonitorLib` 0.9.6 NuGet
    reference (MPL-2.0, ships its dll + native helper automatically).
  - `SystemStats.cs` LibreHwmBridge rewritten from reflection to the typed
    Computer / IVisitor / IHardware / ISensor API (matches the library's
    official sample). GPU type detection is by enum-name prefix so it's
    robust across library-version enum renames (GpuAti → GpuAmd). Sensor
    selection (_pick / _pick_temp) unchanged.
  - Single-file publish already had `IncludeNativeLibrariesForSelfExtract`,
    so the native WinRing0 helper rides along — nothing for the user to
    place by hand now. Temps need admin (manifest already requires it).
  - Added THIRD-PARTY-LICENSES.md for MPL-2.0 compliance.
  - 97/97 tests still pass.

- **2026-06-14 — Hierarchical app list + stats polish.**
  - Main list is now a grouped tree (TreeView): processes cluster by name,
    so the dozens of svchost.exe instances collapse under one expandable
    parent showing summed up/down/total. New `ProcessGroup` VM +
    `IProcessNode` interface so one set of columns binds to both group and
    leaf rows. Reconciled in place by name (groups) and PID (children) —
    expand/collapse and selection survive each 1Hz tick. Tabular look via
    a fixed-width column header strip aligned to Grid-based item templates.
    Selecting a group acts on its first member's exe; double-click on a
    leaf (or single-member group) opens its connections; multi-member
    groups expand/collapse on double-click instead.
  - StatsWindow: RAM now shows "used / total GB (load%)" via
    GlobalMemoryStatusEx (more intuitive than the bare committed-bytes
    percentage), with the percentage preserved as a fallback. CPU load,
    CPU clock, and RAM all work WITHOUT LibreHardwareMonitorLib.dll — only
    temperatures and GPU details need the optional dll.
  - 97/97 tests still pass (logic-only change to the App layer).

- **2026-06-14 — SystemStats + StatsWindow (CPU/GPU/RAM dashboard).**
  - `SystemStats.cs` ported from sysstats.py. CPU load + RAM% via
    `PerformanceCounter`, CPU base clock from the registry. Optional
    temps/GPU details via LibreHardwareMonitorLib.dll loaded entirely by
    reflection — no compile-time dependency, drop-in: present = temps
    appear, absent = "--". Background polling thread (2s), snapshot read
    by the GUI. `_pick`/`_pick_temp` headline-sensor selection ported
    faithfully (prefers package/core sensors, drops bogus <=0 / >150°C
    readings, avoids GPU hotspot/junction when a better reading exists).
  - `StatsWindow` shows three cards (CPU/GPU/Memory), polls 1Hz, status
    bar names the temp source. Wired into View → Statistics. App now owns
    a `SystemMonitor` alongside `NetworkMonitor`, started/stopped together.
  - `NetPeekier.Native.csproj` gains `System.Diagnostics.PerformanceCounter`
    and `Microsoft.Win32.Registry` package refs.
  - Refactor: moved `ValidExe` path-validation from `WfpFirewall` (Native)
    into `ExeValidation` (Core). WfpFirewall now delegates. This keeps the
    test project pure-Core — it no longer references Native, so the
    offline `dotnet run` test cycle works again now that Native needs
    NuGet. 97/97 tests still pass.

- **2026-06-14 — Phase 2 complete: real ETW per-process byte counting.**
  - `EtwMonitor.cs` rewritten against
    `Microsoft.Diagnostics.Tracing.TraceEvent`. Opens the NT Kernel Logger
    (with defensive Stop of any orphan session from a prior crash), wires
    handlers for the 8 TCP/UDP × send/recv × IPv4/IPv6 event flavours,
    accumulates per-PID and per-Connection bytes into two pairs of
    dictionaries (delta + cumulative).
  - `DrainRates(interval)` snapshots the deltas into bytes/sec rates and
    resets; `PidTotals()` / `ConnTotals()` return the cumulative copies.
    `ForgetPids` drops dead pids from the totals so memory stays bounded.
  - Single-lock thread safety across consumer-thread event handlers and
    monitor-thread reads.
  - Graceful fallback: if `TraceEventSession` construction throws (not
    elevated, another consumer holds the kernel session), Available stays
    false and NetworkMonitor uses its OS-counter path. The log records
    exactly why.
  - `NetPeekier.Native.csproj` now requires `Microsoft.Diagnostics.Tracing.TraceEvent 3.1.16`.
    `dotnet restore` once and you're set.

- **2026-06-14 — Phase 4 binding-mode bug + Diag logging.**
  - `DataGridCheckBoxColumn` defaults to TwoWay binding, but the
    `ProcessRow.Blocked` / `UsesWan` properties have private setters.
    WPF threw `InvalidOperationException` at binding-attach time on the
    first paint, before any window was visible. Fix: `Mode=OneWay` on
    both bindings (the cells are display-only; toggling is done via the
    Firewall menu).
  - The Dispatcher unhandled-exception handler used to MessageBox-flood
    on each cascading binding error. Now it uses `Interlocked.CompareExchange`
    to show ONE dialog and shut down cleanly.
  - Added `Diag` — a robust startup logger that never throws. Writes
    breadcrumbs to `log/startup.log` (or fallback paths) so any future
    "won't launch" issues land in a file the user can paste back.
  - Verified end-to-end on Windows 11 26200: clean startup, WFP engine
    opens, ListBlocked works, main window paints, clean shutdown.

- **2026-06-14 — Rename collision fix + WFP layout bug.**
  - `NetPeekier.Native.Monitor` renamed to `NetworkMonitor` everywhere.
    WPF's implicit usings bring in `System.Threading.Monitor`, which
    collided with our class — every App-project source got CS0104. New
    name is more descriptive and self-documenting.
  - `FWP_VALUE0` / `FWP_CONDITION_VALUE0` had a wrongly-added `_padding`
    field. On x64 the natural alignment of `IntPtr` after a `uint`
    already gives the correct 16-byte layout; the extra padding made the
    struct 24 bytes and would have corrupted downstream offsets in
    `FWPM_FILTER0`. Removed.
  - Added `scripts/Verify-WFP.ps1` — admin-elevated PowerShell that
    drives a manual smoke-test of the WFP path against
    `netsh wfp show filters`.
  - WPF MainWindow has `d:IsDesignTimeCreatable=False` to stop the XAML
    designer from trying to instantiate `MainViewModel` (which takes a
    `NetworkMonitor` constructor arg).
  - 97/97 tests still passing.

- **2026-06-13 — Phase 4 functionally complete.**
  - SettingsWindow with full validation (LAN ranges checked via IpCalc;
    minutes fields parsed and bounded). On OK, calls
    `ApplySettings(syncFirewall: true)` so a lockdown-mode toggle takes
    effect immediately.
  - LockdownDialog wired to `Monitor.LockdownPrompt`. App owns the prompt
    callback, marshals to UI thread via Dispatcher.BeginInvoke, dedupes
    per-exe so a chatty process can't pile up dialogs.
  - AboutWindow with version metadata from the assembly (Version=2.0.0
    embedded in NetPeekier.App.csproj).
  - Window-geometry persistence via WindowGeometryPersistence helper;
    Main / Connections / Firewall remember position+size across runs.
  - EtwMonitor.Start changed from throwing to a clean no-op (Available
    stays false; Monitor's OS-counter fallback handles the gap). Removes
    the need for callers to defensively try/catch around a stub.
  - 97/97 tests still passing.

- **2026-06-13 — Phase 3 complete, Phase 4 core views landed.**
  - Project renamed throughout from "Net-Peeker" to "Net-Peekier" (29
    files, namespaces, csproj names, sln file, dir names).
  - WfpFirewall fully implemented: BlockApp / UnblockApp / IsBlocked /
    ListBlocked / AddIpRule / RemoveIpRule / SetWhitelist / RemoveAllRules,
    all routed through a single `SubmitFilter` helper that owns the
    NativeArena lifetime.
  - Monitor now owns the firewall handle lazily, exposes a clean GUI-facing
    edit surface (`SetBlocked` etc.), implements `SyncFirewall` and
    `LockdownSweep` with parity to Python's `monitor.apply_settings` /
    `_lockdown_sweep`.
  - GUI now MVVM: MainViewModel orchestrates the 1Hz refresh, reconciles
    Processes by PID. New: ConnectionsWindow, FirewallWindow, IpRuleDialog
    (with full IpCalc / WfpFirewall validation before submit).
  - 97/97 tests still passing.

- **2026-06-13 — Phase 1 complete, Phase 2 most-of.**
  - Full IpCalc port with BigInteger v4+v6 set arithmetic.
  - 85/85 tests passing in a stand-alone runner (no NuGet needed).
  - Hand-rolled IpHelper P/Invoke for TCP4 / TCP6 / UDP4 / UDP6.
  - Monitor.Tick body fully ported (LAN/WAN classification, idle hiding,
    history logging, dead-PID cleanup, dashboard totals).
  - EtwMonitor stubbed (needs TraceEvent NuGet).
  - Core + Native both build clean against `net8.0`.

- **2026-06-13 — Phase 0 done, Phase 1 scaffolded.**
  - Solution, three projects, manifest, models, paths, settings, formatting.
