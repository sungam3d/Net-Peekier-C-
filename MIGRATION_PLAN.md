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
| Live packet view + hex dump              | WinDivert                 | **DROPPED** *(see note)*       |
| Per-process speed limit (throttle)       | WinDivert token bucket    | **DROPPED** *(see note)*       |
| System stats (CPU/GPU/RAM/temps)         | psutil + HardwareMonitor  | PerfCounter + LibreHWM dll     |
| History log                              | plain file                | plain file ✅                  |

**Dropped features — why:** packet-payload capture and inline byte-rate
throttling both require sitting in the packet path, which means a kernel
driver. That was the whole reason we pivoted away from WinDivert. So:

1. **Hex/packet view is gone.** Replaced with a "Connections" panel driven
   by the ETW connection table. You see: timestamp, PID, direction,
   protocol, local/remote endpoints — no payload.
2. **Throttling is replaced with finer-grained blocking.** Use the per-IP
   rule UI for "Chrome can talk to *these* IPs/ports but nothing else".

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

### Phase 2 — Native: monitor (read-only) ✅ MOSTLY DONE
- [x] `IpHelper.cs`: hand-rolled P/Invoke for TCP4 / TCP6 / UDP4 / UDP6 tables
- [x] `ProcessMap.cs`: pid↔endpoint table, name/exe cache, PID-reuse check
- [x] `Monitor.cs`: full tick body ported from `monitor._tick`
      (idle hiding, WAN/LAN classification, history logging, totals,
      dead-PID cleanup, system-IO baseline + fallback)
- [ ] `EtwMonitor.cs`: still a stub. Needs `Microsoft.Diagnostics.Tracing.TraceEvent`
      NuGet package, which the offline sandbox couldn't restore. Monitor
      falls back to system-wide `NetworkInterface.GetIPStatistics()` for
      dashboard totals — works first try on a connected box.

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

### Phase 4 — GUI ✅ FUNCTIONALLY COMPLETE (polish pending)
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
- [ ] StatsWindow (depends on SystemStats backend — Phase 5)
- [ ] Hierarchical app list (svchost expand UX — polish task)

### Phase 5 — Polish + publish
- [ ] App icon, version metadata, optional sign
- [ ] `dotnet publish` produces one `NetPeekier.exe`
- [ ] Smoke test on Windows 11 23H2 + 24H2 fresh installs
- [ ] Restore `Microsoft.Diagnostics.Tracing.TraceEvent` NuGet ref and flip
      the stub `EtwMonitor` to the real implementation
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
