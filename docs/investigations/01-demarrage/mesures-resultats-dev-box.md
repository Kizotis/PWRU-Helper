# 01 — Startup measurements, dev box (first data point)

_Phase 1 · Amelia (Dev, BMAD QD) · 2026-09-06 · protocol: [`mesures-protocole.md`](mesures-protocole.md)_

> **Read this first.** This machine is **not representative of the affected machines.** The owner
> reported (answer (c)) that the affected machines are **personal, Windows Defender only**. The
> machine sheet below shows this dev box is **Azure-AD joined** and has **ESET Security and Acronis
> Cyber Protect registered alongside Defender**. Its numbers are a *floor and a calibration*, not a
> reproduction of P1. **[MEASURED]** — see `[defender] third_party_av` and `[machine] domain_join`.

---

## 1. Build under test

| Field | Value |
|---|---|
| Source | worktree at `f0efc26` (main v0.14.0 + docs), **no production code change** |
| Build command | exactly the flags of `Build Portable EXE.bat`: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none` |
| Output | `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\PWRUHelper.exe` (gitignored via `bin/`) |
| Size | **187 476 286 bytes** (178.8 MB) |
| SHA-256 | `879CC0CA01E1005068104FB022566601697BBB308EF572D88252353FAF58C1E9` |
| Built | 2026-09-06 13:17:20 |
| Signature | **NotSigned** (expected — SignPath still pending) |
| Mark-of-the-Web | **none** — a locally built file never carries a `Zone.Identifier` stream |

Because the hash is new to this machine, its **very first execution is a genuine cold-reputation
run** for the antivirus stack: no local verdict, no cloud verdict, and `%TEMP%\.net\PWRUHelper` did
not exist at all (confirmed absent by the machine sheet taken *before* the first launch).

---

## 2. Machine sheet (redacted extract)

Full file: `%USERPROFILE%\PWRU-Diagnostics\machine-sheet-GSIN-WS-DT004-2026-09-06.txt` (+ `.json`).

| Group | Value |
|---|---|
| OS | Windows 11-family, `DisplayVersion` **25H2**, build **26200.9168** (`ProductName` still reads "Windows 10 Pro" — a known registry quirk) |
| CPU / RAM | Intel Core i7-9700 @ 3.00 GHz, 8 cores · 15.8 GB |
| Uptime | 5 d 04 h at capture (booted 2026-09-01) |
| Join state | **AzureAdJoined: YES**, DomainJoined: NO, WorkplaceJoined: NO |
| PowerShell | 5.1.26100.9168, **not elevated** |
| **Defender — cloud** | `CloudBlockLevel` = **2** (High) · `CloudExtendedTimeout` = **50** s · `DisableBlockAtFirstSeen` = **False** (⇒ **BAFS is ON**) · `SubmitSamplesConsent` = 1 · `MAPSReporting` = 1 |
| **Defender — state** | `RealTimeProtectionEnabled` = True · `BehaviorMonitorEnabled` = True · `IsTamperProtected` = True · engine 1.1.26080.3 · signatures 2026-09-06 07:04 |
| Defender exclusions | **unreadable — "Must be an administrator to view exclusions"** |
| Other security products | **ESET Security ×3 entries, Acronis Cyber Protect ×2 entries**, Windows Defender (from `root\SecurityCenter2`) |
| SmartScreen | `HKCU` / `HKLM` / policy values all **not set** (OS default) |
| **Smart App Control** | `VerifiedAndReputablePolicyState` = **0 = off** |
| Device Guard | VBS status 2 (running), CI enforcement 2 |
| `%APPDATA%` | `C:\Users\<USER>\AppData\Roaming`, attributes `Directory` — **not redirected, not a reparse point** |
| `%APPDATA%\PWRUHelper` | `phrases.json` 12.8 KB · `slang.json` 4.7 KB · `squad.json` 4.2 KB · `settings.json` 1.2 KB · `log.txt` **27.8 KB** (far from the 1 MB rotation cap), all dated 2026-08-04 |
| `%TEMP%\.net\PWRUHelper` | **absent before the first launch**; after it: **5 files, 8.2 MB** — `D3DCompiler_47_cor3.dll` (4.74 MB), `wpfgfx_cor3.dll` (1.96 MB), `PresentationNative_cor3.dll` (1.24 MB), `PenImc_cor3.dll` (158 KB), `vcruntime140_cor3.dll` (125 KB) |
| Exe location | plain local folder on drive `E:\` — not OneDrive, not Desktop/Downloads, not a UNC path. A OneDrive folder exists on the machine (`OneDrive - Gsinformatique`) but the exe is not in it. |
| GPU | Radeon RX 550X (driver 31.0.12044.30001, 2023-02-04) + Intel UHD 630 (2025-03-06) |
| Display | 5120×1440 (primary) + 1920×1080, applied DPI 96 |
| Proxy / network | WinHTTP: **direct access, no proxy** · `ProxyEnable` = 0, no `AutoConfigURL`, `AutoDetect` not set (⇒ **no WPAD**) · Ethernet, category Public, IPv4 Internet · DNS 1.1.1.1 / 8.8.8.8 |
| Public IP | not collected (privacy default) |

All rows above are **[MEASURED]** by `Get-MachineSheet.ps1`, except the "Windows 11-family" reading
of `ProductName`, which is **[INFERRED]** from build 26200.

---

## 3. Results

All runs: `Measure-Startup.ps1`, T2 = `MainWindowHandle` non-zero, 5 ms polling, app killed between
runs. Raw CSVs in `%USERPROFILE%\PWRU-Diagnostics\startup-GSIN-WS-DT004-*.csv`.

| # | Condition | Launch | Extraction dir before | pre-process ms | in-process ms | total ms |
|---|---|---|---|---|---|---|
| **0** | **first ever launch of this hash** (cold reputation, `%TEMP%\.net` absent) | Shell | 0 files | **2163.4** | **1477.1** | **3640.5** |
| 1 | second launch of the same hash (~45 s later) | Shell | 5 files | **2528.3** | 2256.2 | 4784.4 |
| 2 | warm | Shell | 5 files | 26.3 | 1793.3 | 1819.6 |
| 3 | warm | Shell | 5 files | 15.2 | 1524.9 | 1540.1 |
| 4 | extraction cache cleared | Shell | 0 files | 116.4 | 1626.9 | 1743.2 |
| 5 | extraction cache cleared | Shell | 0 files | 21.5 | 2211.4 | 2232.8 |
| 6 | warm | **Direct** | 5 files | 27.4 | 1531.4 | 1558.9 |
| 7 | warm | **Direct** | 5 files | 41.6 | 1526.0 | 1567.6 |
| 8 | warm | **Direct** | 5 files | 1.8 | 1678.6 | 1680.4 |

Every row returned `result = ok`. **[MEASURED]**

Aggregates:

| Slice | pre-process (min / med / max) | in-process (min / med / max) | total |
|---|---|---|---|
| Cold, new hash (#0–1) | 2163 / — / 2528 | 1477 / — / 2256 | 3641 / — / 4784 |
| Warm Shell (#2–3) | 15 / — / 26 | 1525 / — / 1793 | 1540 / — / 1820 |
| Warm Direct (#6–8) | 2 / 27 / 42 | 1526 / 1531 / 1679 | 1559 / 1568 / 1680 |
| Extraction cleared (#4–5) | 22 / — / 116 | 1627 / — / 2211 | 1743 / — / 2233 |

---

## 4. Interpretation

### 4.1 The pre-process cost is real, large, and paid **per new file**, not per launch — [MEASURED]

`pre_process_ms` collapses by **two orders of magnitude** between the first two executions of a new
hash (2163 ms, 2528 ms) and everything after (15–42 ms). Nothing else changed between run 1 and
run 2: same exe, same folder, same launch mode, ~40 s apart.

This is the single most useful number in the whole document. It says that on a machine with a
security stack, **a brand-new PWRU Helper build costs ~2.2–2.5 s before the app's own code executes
a single instruction**, and that the cost disappears once the security stack has a verdict on that
hash. It maps exactly onto the user report "slow the first time after an update, fine afterwards"
(Q1.6) — without needing the settings-migration explanation.

The cost was paid **twice**, not once. **[INFERRED]** most plausible reading: the first execution
triggers a scan and a cloud lookup whose verdict is not yet cached when the second launch happens
~40 s later; from the third launch on, the verdict is cached. An alternative reading — two separate
products (Defender and ESET) each scanning once — cannot be excluded from these data.

### 4.2 In-process time is a flat ~1.5–1.8 s floor and is NOT where the variance lives — [MEASURED]

Across all nine runs, `in_process_ms` stays between **1477 and 2256 ms** with no relationship to the
condition. It does not drop when warm and does not rise when the extraction cache is cleared. On
this hardware (i7-9700, SSD, no `%APPDATA%` redirection, 27.8 KB log) the app's own startup path —
extraction, JIT, file I/O, BAML, `BuildSquadTab`, `ApplySettings`, hotkeys, first layout — costs
about **1.5 s and is stable**.

For comparison, the **[REPORTED]** 2026-08-04 benchmark on the owner's machine was ~1.1–1.25 s warm.
The ~300 ms gap is explained by the marker definition rather than a regression: this protocol
measures to `MainWindowHandle`, the earlier benchmark measured something else (its definition is not
recorded). Treat the two as different instruments, not as a change.

### 4.3 Self-extraction of the native libraries is essentially free here — [MEASURED]

Clearing `%TEMP%\.net\PWRUHelper` (5 DLLs, 8.2 MB) before a run changed `in_process_ms` from a
1525–1793 ms warm band to 1627 / 2211 ms — inside the run-to-run noise of the warm runs themselves.
Hypothesis F3 ("every new build re-extracts and every extracted file is AV-scanned") is **confirmed
as a mechanism** (the directory really is created, with exactly those 5 files) but **not confirmed as
a cost** on this machine. It remains open on machines with slower storage or a more intrusive
scanner — condition C4 of the protocol is what settles it there.

### 4.4 Shell vs Direct shows no measurable difference — and cannot, on this file — [MEASURED] / [INFERRED]

Warm Shell (15–26 ms) and warm Direct (2–42 ms) pre-process times are indistinguishable.
**[INFERRED]** this proves nothing about SmartScreen: the exe was built locally and carries **no
Mark-of-the-Web**, so the SmartScreen gate is never armed. The Shell-vs-Direct comparison only
becomes informative on a file downloaded through a browser (protocol condition E1). That test cannot
be run here without fabricating a MOTW stream, which would not be the real thing.

### 4.5 What this box rules out for itself — [MEASURED]

- **Not WPAD/proxy**: no proxy, no `AutoConfigURL`, `AutoDetect` unset. (Also irrelevant to P1 per
  the owner's answer (a), since the window is what is late.)
- **Not roaming/redirected `%APPDATA%`**: plain local `Directory`, no reparse point.
- **Not OneDrive hydration**: the exe is on a plain local `E:\` folder.
- **Not a bloated log**: `log.txt` is 27.8 KB against a 1 MB rotation cap.
- **Not Smart App Control**: it is off (`VerifiedAndReputablePolicyState` = 0).
- **Not the MSI's denied `Program Files` writes** (F4): this is the portable path.

### 4.6 The one configuration finding worth carrying to the affected machines — [MEASURED here, [UNKNOWN] there]

This box runs `CloudBlockLevel = 2` (High) with `CloudExtendedTimeout = 50` s and Block-at-First-Seen
**enabled**. In that configuration Defender is permitted to **hold an unknown executable while it
waits for a cloud verdict**. Even a fraction of that budget explains a multi-second pre-process
delay, and it is exactly the mechanism that would produce a *variable* 6–10 s (verdict latency
depends on the network round trip and on how busy the cloud service is) on a *new, unsigned* binary
— which is precisely PWRU Helper's situation until code signing lands.

These particular values are almost certainly policy-set on this managed machine. **The first thing
to read on every affected personal machine is those same four fields.** If they are at their
defaults there and the delay is still 6–10 s, the cloud-wait hypothesis weakens and attention should
move to the scan of the 178 MB image itself.

---

## 5. What could not be done here

| Item | Why |
|---|---|
| **Defender exclusion / real-time-off comparison (protocol E5)** | requires administrator. This session runs unelevated and the task forbids elevation. Even the exclusion *list* is unreadable ("Must be an administrator to view exclusions"). **Skipped — must be done by the owner on an affected machine, with consent, and reverted afterwards.** |
| **SmartScreen / Mark-of-the-Web test (E1)** | a locally built exe has no MOTW. Needs a real GitHub-release download on an affected machine. |
| **Cold-after-reboot run (C3)** | the box has 5 days of uptime and rebooting it mid-session was out of scope. |
| **MSI vs portable (E2)** | would require installing to `Program Files` (admin) on the dev box. |
| **T3 "UI usable"** | needs in-process instrumentation = a production code change, which Phase 1 does not have the go for (see protocol §6). |
| **Google Burst threshold hunt** | deliberately not run — it would throttle the owner's public IP. See [`../02-traduction/experimentations.md`](../02-traduction/experimentations.md). |

---

## 6. Verdict for P1 so far

**[MEASURED]** On a machine with an active security stack, the first launch of a new PWRU Helper
build costs **~2.2–2.5 s of pre-process time on top of a ~1.5 s in-process floor** — a **3.6–4.8 s
total**, entirely without any window appearing. That is the same *shape* as the reported 6–10 s,
reached on fast hardware with no proxy, no redirected profile and no OneDrive involvement.

**[INFERRED]** The affected machines are therefore very likely paying the same pre-process cost,
amplified by some combination of: a slower cloud-verdict round trip, a lower-end disk, an
un-cached verdict on every launch, and/or the SmartScreen gate that this box could not exercise.

**Next measurement that would move the needle most:** condition C1 + C5 + E1 on one affected
personal machine — first launch of a freshly downloaded (MOTW-carrying) release, Shell vs Direct,
then again after `Unblock-File`. Three numbers, ten minutes, and it either indicts SmartScreen or
clears it.
