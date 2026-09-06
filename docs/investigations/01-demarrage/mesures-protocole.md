# 01 — Startup measurement protocol (P1)

_Phase 1 · Amelia (Dev, BMAD QD) · 2026-09-06 · baseline `f0efc26` (main v0.14.0 + docs)_

Tooling: [`tools/diagnostics/Measure-Startup.ps1`](../../../tools/diagnostics/Measure-Startup.ps1),
[`tools/diagnostics/Get-MachineSheet.ps1`](../../../tools/diagnostics/Get-MachineSheet.ps1),
[`tools/diagnostics/README.md`](../../../tools/diagnostics/README.md) (end-user instructions).
First execution on the dev box: [`mesures-resultats-dev-box.md`](mesures-resultats-dev-box.md).

The owner's answer to gating question Q1.1 is settled: on the slow machines the 6–10 s pass
**before any window appears**. Everything below is built to split that interval in two and to say
which half is guilty, per machine, reproducibly.

---

## 1. Definitions

| Marker | Definition | How it is measured | Precision |
|---|---|---|---|
| **T0** | Launch requested. The instant the user's double-click (or the script's `Start-Process`) is issued. | `[DateTime]::Now` taken immediately before the launch call, plus a `Stopwatch` started at the same instant. | ~1–15 ms (`DateTime.Now` tick resolution) |
| **T1** | The process exists. The kernel has created the process object. | `Process.StartTime` — reported by the kernel, independent of our polling. | ~1 ms |
| **T2** | First top-level window. | Poll `Process.MainWindowHandle` every 5 ms until non-zero. | ±5 ms |
| **T3** | UI usable — the first frame is painted **and** the dispatcher accepts input. | **Not measured today.** See §6. | — |

Derived, and the only two numbers that matter:

- **Pre-process time = T1 − T0.** The app's own code has not run yet. What lives here: antivirus
  scan-on-execute of a 178 MB unsigned image, SmartScreen / Smart App Control reputation lookup
  (Mark-of-the-Web only), image mapping, shell overhead.
- **In-process time = T2 − T1.** What lives here: single-file host self-extraction of the native
  libraries to `%TEMP%\.net\PWRUHelper\<hash>\`, JIT, the synchronous `%APPDATA%` file I/O of
  `Logging` / `SettingsService` / `LoadPhrases` / `LoadSlang` / `LoadSquad`, the 52 KB BAML load,
  `BuildSquadTab()`, `ApplySettings()`, 5 × `RegisterHotKey`, and the first measure/arrange of the
  restored tab (≈140 non-virtualized buttons if it is the Phrasebook).
  Timeline reference: `00-annexe-demarrage-et-reseau.md` §1.6, steps 1–19.

**Total = T2 − T0** is what a stopwatch in a user's hand would show.

### Why the split is the whole point

`00-annexe-demarrage-et-reseau.md` §1.7 established **[CONFIRMED]** that no network, registry, WinRT
or WMI access happens before the window. So the 6–10 s must be one of two very different stories,
and the split names which:

- **pre ≫ in** → the operating system and its security stack. The app is a passenger. Fixes are
  code signing, reputation, exclusions, file location — not code.
- **in ≫ pre** → extraction + roaming/synced `%APPDATA%` + first layout. Fixes are in the app and
  its packaging.

---

## 2. Instruments

| Instrument | What it produces | Admin? |
|---|---|---|
| `Measure-Startup.ps1` | one CSV row per launch (`pre_process_ms`, `in_process_ms`, `total_ms`, extraction-dir state, result) + a text summary | no |
| `Get-MachineSheet.ps1` | `machine-sheet-<host>-<date>.txt` + `.json` — the environment fields listed in §4 | no (Defender exclusion lists need admin and are reported as such) |
| Stopwatch / phone video | sanity check that the script's T2 matches what a human sees | no |

Both scripts are read-only, PowerShell 5.1 compatible, write ASCII output into
`%USERPROFILE%\PWRU-Diagnostics`, and never leave the app running.

---

## 3. Condition matrix

Run the **core set** on every machine. Run the extended rows only where they apply, or when the
core set does not separate the hypotheses.

### Core set (every machine, ~10 minutes)

| # | Condition | How | Why it discriminates |
|---|---|---|---|
| C1 | **First launch of a new build/download** (new file hash, never executed here) | download or copy the exe, then run the script **before** opening it manually | This is the reputation/scan worst case. A new hash has no local AV verdict and no SmartScreen reputation. |
| C2 | **Warm, 3 runs** | `-Runs 3` right after C1 | Establishes the machine's floor. The gap C1 − C2 is the one-off cost of a new build. |
| C3 | **Cold after reboot**, 1 run | reboot, log in, wait 2 min for the desktop to settle, run `-Runs 1` | Separates "AV verdict cache lost" from "file cache lost". |
| C4 | **Extraction cache cleared**, 2 runs | `-Runs 2 -ClearExtractionCache` | Isolates the cost of re-extracting the 5 native DLLs (~8.2 MB) and of the AV re-scanning them. |
| C5 | **Shell vs Direct**, 3 runs each | `-LaunchMode Shell` then `-LaunchMode Direct` | Direct skips ShellExecute and therefore the SmartScreen gate. A large Shell−Direct gap on a MOTW file = SmartScreen. |

### Extended set (where applicable)

| # | Condition | How | Why |
|---|---|---|---|
| E1 | **Mark-of-the-Web present vs removed** | measure as downloaded, then `Unblock-File .\PWRUHelper.exe`, measure again | The single cleanest test of the SmartScreen hypothesis. Only meaningful on a file downloaded with a browser. |
| E2 | **Portable exe vs MSI install** | measure both on the same machine | The MSI build attempts 3 denied writes into `Program Files\…\Data\` at every launch (F4). Also a different file location and a different reputation record. |
| E3 | **Exe on plain local disk vs OneDrive-synced folder vs Desktop/Downloads** | copy the exe, measure in each | A OneDrive placeholder can force a full 178 MB hydration before execution. `Get-MachineSheet` records `location_class` and the file attributes (`ReparsePoint`/`Offline`). |
| E4 | **Network cable vs airplane mode** | disable the NIC, measure, re-enable | Defender's Block-at-First-Seen sends the file's hash to the cloud and, with `CloudExtendedTimeout`, will **wait** for a verdict. No network ⇒ no cloud wait. A large drop offline is near-proof of BAFS. |
| E5 | **Defender real-time off, or an exclusion for the exe** | needs admin; add `Add-MpPreference -ExclusionPath <exe>` (or `-ExclusionProcess PWRUHelper.exe`), measure, then **remove it again** | The direct test of the AV hypothesis. Only with the owner's explicit consent, and always reverted. |
| E6 | **Standard user vs administrator session** | measure in both | Different reputation/scan behaviour and different `%APPDATA%` path. |
| E7 | **`%APPDATA%` redirected / roaming / OneDrive-KFM** | read `appdata_path` + `appdata_reparse` from the machine sheet; if redirected, compare with a machine that is not | Steps 5, 7, 8, 13–15 do synchronous I/O there on the UI thread (F1). |
| E8 | **`LastTab` = Phrasebook vs About** | set the tab, close the app cleanly (so it saves), then measure | Tests Q1.5: the cost of first-laying-out ≈140 non-virtualized buttons. |
| E9 | **Second launch after an app update** | measure launch 1 and launch 2 after upgrading from ≤ v0.13.0 | Tests Q1.6: the one-time `SettingsVersion` migration write. |

### Runs per condition

- 3 runs for anything described as "warm"; report min / median / max, not just the average.
- 1 run for conditions that are one-shot by nature (C1, C3, E9 launch 1) — they cannot be repeated
  without recreating the condition. Note that explicitly.
- Discard and re-run any row whose `result` column is not `ok`.

---

## 4. Per-machine sheet — the fields that must be captured

Produced automatically by `Get-MachineSheet.ps1`. Grouped by what they decide:

**Identity** — computer name, Windows product/`DisplayVersion`/build+UBR, PowerShell version,
elevated yes/no, CPU, cores, RAM, uptime, Azure-AD/domain/workplace join state.

**Antivirus (the P1 prime suspect)** — `Get-MpPreference`: `CloudBlockLevel`,
`CloudExtendedTimeout`, `DisableBlockAtFirstSeen`, `SubmitSamplesConsent`, `MAPSReporting`,
`DisableRealtimeMonitoring`, `ExclusionPath` / `ExclusionProcess` / `ExclusionExtension` (admin
only). `Get-MpComputerStatus`: `AMEngineVersion`, `AMProductVersion`,
`AntivirusSignatureLastUpdated`, `RealTimeProtectionEnabled`, `IsTamperProtected`,
`BehaviorMonitorEnabled`. **Plus the `root\SecurityCenter2` product list** — the only reliable way
to notice a third-party AV the user forgot to mention. Plus the last 20 entries of the
Defender operational event log when readable without admin.

**Reputation gates** — SmartScreen at `HKCU\…\Explorer\SmartScreenEnabled`,
`HKLM\…\Explorer\SmartScreenEnabled` and the policy `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\EnableSmartScreen`;
**Smart App Control** at `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState`
(0 = off, 1 = enforced, 2 = evaluation); VBS/HVCI via `Win32_DeviceGuard`.

**The executable** — full path, size, SHA-256, `Get-AuthenticodeSignature` status (expected
`NotSigned` until SignPath lands), the `Zone.Identifier` alternate stream (Mark-of-the-Web) or an
explicit "none", file attributes (`ReparsePoint`/`Offline` reveal a OneDrive placeholder), and a
location classification (OneDrive / Desktop / Downloads / Program Files / UNC / plain local, plus
the drive).

**Folders the app touches** — `%TEMP%\.net\PWRUHelper` (present? file count, total size, oldest and
newest write — this is the proof that extraction happened and when); `%APPDATA%\PWRUHelper`
(settings/log/data files with sizes and dates — a huge `log.txt` is its own suspect); `%APPDATA%`
path and attributes (redirection/reparse); `%TEMP%` path; the `OneDrive` environment variable.

**Display / power** — GPU name + driver version and date (software-rendering fallback is a real
WPF first-frame cost), active power plan, monitor count and resolutions, applied DPI.

**Network (mostly P2, but WPAD affects the post-window freeze)** — `netsh winhttp show proxy`, the
HKCU Internet Settings `ProxyEnable` / `ProxyServer` / `AutoConfigURL` / `AutoDetect`, connection
profiles and category, DNS servers. Public IP is **not** collected unless `-IncludePublicIp` is
passed.

---

## 5. Decision tree the results feed

Compute the ratio `pre / total` on the **cold, new-hash** run (C1), and compare it with the warm
runs (C2).

```
C1 pre / total > 0.5   (pre-process dominates)
├─ and C1 pre >> C2 pre  → the cost is per-NEW-FILE, not per-launch
│   ├─ Shell − Direct gap large (C5) AND Mark-of-the-Web present (E1)
│   │       → SMARTSCREEN branch. Fix: code signing (SignPath), Unblock-File as a workaround,
│   │         MSI distribution (installed files carry no MOTW).
│   ├─ offline run much faster (E4), CloudExtendedTimeout > 0, DisableBlockAtFirstSeen = False
│   │       → DEFENDER BLOCK-AT-FIRST-SEEN branch. Fix: code signing raises reputation;
│   │         an exclusion is the user-side workaround; a smaller exe helps marginally.
│   └─ neither → plain scan-on-execute of 178 MB. Fix: signing, and revisit exe size.
└─ and C1 pre ~= C2 pre  → EVERY launch pays it → real-time scanning with no verdict cache,
        or a third-party AV/EDR. Check the SecurityCenter2 product list first.

C1 pre / total < 0.5   (in-process dominates)
├─ C4 (cleared extraction) much slower than C2
│       → SELF-EXTRACTION branch. ~8.2 MB of native DLLs written to %TEMP% then scanned.
│         Fix candidates: DOTNET_BUNDLE_EXTRACT_BASE_DIR, or drop single-file for the MSI path.
├─ %APPDATA% is redirected/roaming/OneDrive-synced (E7) or log.txt is near its 1 MB cap
│       → SYNCHRONOUS ROAMING I/O branch. Steps 5,7,8,13-15 run on the UI thread.
├─ E8 shows Phrasebook ≫ About
│       → FIRST-LAYOUT branch (~140 non-virtualized buttons). Fix: virtualization or deferred tabs.
└─ none of the above, and in-process is flat across every condition
        → baseline WPF + JIT cost. Compare against the dev-box floor before calling it a bug.

Window appears fast, THEN freezes  (should not happen per the owner's answer (a) — but if a user
reports it) → step 24, the GitHub update check: WPAD/PAC proxy resolution on the first HttpClient
request, 8 s timeout, issued from the UI thread. Check the proxy fields of the machine sheet.
```

---

## 6. T3 (UI usable) — how it could be measured later

T2 says the window exists. It does not prove the app answers a click. Measuring T3 needs
**in-process instrumentation, i.e. a production code change**, which Phase 1 does not have the
owner's go for. When it is authorised, the cheapest honest version is:

1. Capture `Process.GetCurrentProcess().StartTime` at the top of `App.OnStartup` — this gives the
   process a reference point identical to the script's T1, so external and internal traces align.
2. Log elapsed-ms marks at: end of `MainWindow` field init, end of `InitializeComponent`, end of
   `LoadPhrases`/`LoadSlang`/`LoadSquad`, end of `ApplySettings`, `OnSourceInitialized` after the
   5 `RegisterHotKey` calls, and `Loaded`.
3. Post a `DispatcherPriority.ContextIdle` callback from `OnWindowLoaded`; the moment it runs is
   **T3** — the dispatcher has finished layout/render and is idle enough to accept input.
4. Gate the whole thing behind an env var (e.g. `PWRUHELPER_TRACE=1`) or a `--trace` argument so
   shipped builds pay nothing, and write to the existing `Logging` sink.

That instrumentation is the subject of `trace-instrumentation.md`; it is **not** part of this
protocol, and nothing in this protocol requires it.

---

## 7. Reporting

One row per (machine, condition) in the Phase-1 results table:

| machine id | condition | runs | pre ms (min/med/max) | in ms (min/med/max) | total ms | MOTW | signed | AV | notes |

Rules:

- Grade every line **[MEASURED]** (a CSV row exists) or **[INFERRED]** (reasoning from other rows).
  Never mix the two in one cell.
- Attach the raw `startup-*.csv` and the `machine-sheet-*.txt`; the table is a summary, the CSV is
  the evidence.
- Anonymise machines as `M1`, `M2`… and keep the mapping outside the repo.
- A machine that reproduces the 6–10 s symptom is worth ten that do not. Record the *fast* ones
  anyway — the contrast is what identifies the variable.
- The dev box is **not** a reference machine for P1 (see the results document: it is Azure-AD
  joined and carries third-party security software, unlike the affected personal machines).
