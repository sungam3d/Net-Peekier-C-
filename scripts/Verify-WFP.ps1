# Verify-WFP.ps1
# ---------------
# Sanity-check the WFP integration on a real Windows box. Run as
# administrator — `netsh wfp` and `FwpmFilterAdd0` both require it.
#
# What this script does:
#   1. Confirms the user is elevated.
#   2. Runs the published Net-Peekier exe with a short --probe flag that
#      adds + removes a per-app filter, then exits. (See note below; the
#      --probe flag is a Phase-5 add. Until it exists, do the steps in the
#      manual section at the bottom of this file.)
#   3. Uses `netsh wfp show filters` to confirm the filter appeared under
#      our private sublayer GUID, then disappeared after RemoveAll.
#
# Net-Peekier's stable provider + sublayer GUIDs (defined in
# src/NetPeekier.Native/Wfp/Guids.cs):
#   Provider:  9be94c1e-7d4b-4f1d-8bfe-1c5e2f00be01
#   Sublayer:  9be94c1e-7d4b-4f1d-8bfe-1c5e2f00be02

$ErrorActionPreference = "Stop"

$NetPeekierProvider = "9be94c1e-7d4b-4f1d-8bfe-1c5e2f00be01"
$NetPeekierSublayer = "9be94c1e-7d4b-4f1d-8bfe-1c5e2f00be02"

# 1. Elevation check.
$currentUser = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentUser.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator."
}

# 2. Pre-state.
Write-Host "==> WFP state BEFORE Net-Peekier touches it:" -ForegroundColor Cyan
$dump = & netsh wfp show filters 2>&1
$preCount = ($dump | Select-String -Pattern $NetPeekierSublayer | Measure-Object).Count
Write-Host "    Filters in Net-Peekier sublayer: $preCount" -ForegroundColor Gray
if ($preCount -gt 0) {
    Write-Warning "Found leftover filters from a previous run."
    Write-Host "    Remove them in the app: Firewall menu -> Remove all Net-Peekier rules"
}

# 3. Manual smoke test (until --probe exists).
Write-Host ""
Write-Host "==> Smoke test (manual)" -ForegroundColor Cyan
Write-Host "    Do the following in the GUI, in order:"
Write-Host "      a. Launch NetPeekier.exe (elevated)."
Write-Host "      b. In the process list, pick any non-essential exe."
Write-Host "      c. Right-click -> Block (or use the Firewall menu)."
Write-Host "      d. Try to reach the network from that exe; expect failure."
Write-Host "      e. Press <Enter> here to dump current WFP state..."
Read-Host

$dump = & netsh wfp show filters 2>&1
$postCount = ($dump | Select-String -Pattern $NetPeekierSublayer | Measure-Object).Count
Write-Host "    Filters in Net-Peekier sublayer NOW: $postCount" -ForegroundColor Gray
if ($postCount -gt $preCount) {
    Write-Host "    ✓ WFP filters were added under our sublayer." -ForegroundColor Green
} else {
    Write-Warning "Expected filters under sublayer $NetPeekierSublayer but found none."
}

Write-Host ""
Write-Host "    f. Now unblock the same exe (Firewall -> Unblock selected)."
Write-Host "    g. Confirm traffic flows again."
Write-Host "    h. Press <Enter> here to confirm cleanup..."
Read-Host

$dump = & netsh wfp show filters 2>&1
$finalCount = ($dump | Select-String -Pattern $NetPeekierSublayer | Measure-Object).Count
Write-Host "    Filters in Net-Peekier sublayer NOW: $finalCount" -ForegroundColor Gray
if ($finalCount -eq $preCount) {
    Write-Host "    ✓ WFP filters were removed cleanly." -ForegroundColor Green
} else {
    Write-Warning "Filter count did not return to baseline ($preCount). Stragglers?"
}

# 4. Stretch test: per-IP rule.
Write-Host ""
Write-Host "==> Per-IP rule test (manual)"
Write-Host "    a. Open Firewall -> Manage firewall -> Add IP rule..."
Write-Host "    b. Pick an exe, action=block, remote=1.1.1.1, ports=blank, protocol=any."
Write-Host "    c. From that exe, try: ping 1.1.1.1   AND   ping 8.8.8.8"
Write-Host "    d. Expect: 1.1.1.1 fails, 8.8.8.8 succeeds. Confirms condition-targeted block."
Write-Host "    e. Remove the rule and confirm 1.1.1.1 reachable again."
Write-Host ""
Write-Host "==> Done." -ForegroundColor Cyan
