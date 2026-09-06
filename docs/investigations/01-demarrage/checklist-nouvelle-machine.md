# 01 — Installing PWRU Helper on a new machine: checklist

_Phase 1, **updated in Phase 2** (2026-09-06) to reflect the owner's decisions and the first measurements · author: Amelia (BMAD Senior Software Engineer) · baseline `4759712` (main, v0.14.0)._

Two audiences, deliberately separated:

- **Part A — for the player installing the app.** Plain language, no PowerShell required for the mandatory steps. This is the part that can be lifted into the README or a Discord pin.
- **Part B — for the owner (Kizotis), reproducing a slow machine.** What to capture, what to run, what to send back.

Everything here follows from `hypotheses-matrice.md`; where a step exists because of a specific hypothesis, the ID is given so the advice can be revised (or dropped) when the measurements land. **Nothing in Part A is a workaround for a confirmed bug — the causes are still under investigation. It is placement and expectation advice that is correct regardless.**

> **What changed in Phase 2** (see [`recommandations.md`](recommandations.md), now FINAL):
> - **Code signing is decided: SignPath Foundation**, free, which keeps the app **MIT**. It is **not in place yet** — the application has not been submitted and approval takes *weeks*. Until then, and for some weeks after the first signed release, users still see the "unknown publisher" warning. Part A says so honestly.
> - **The first data point exists.** On one measured machine, the first two launches of a brand-new build cost **2.2–2.5 s before the app runs a single instruction**; from the third launch that drops to **15–42 ms**. The app's own startup is a flat ~1.5 s that never varies. That is the basis of the expectation line in **A4**. **[MEASURED]** — `mesures-resultats-dev-box.md` §3–§4.
> - **Part B now carries the exact commands** to run, because `tools/diagnostics/` exists and is documented in [`tools/diagnostics/README.md`](../../../tools/diagnostics/README.md).

---

# Part A — For the player

## A1. Choose portable or MSI

| | Portable `PWRUHelper.exe` | `PWRUHelper-<version>-setup.msi` |
|---|---|---|
| Download size | ~180 MB | smaller (the exe is compressed inside the installer) |
| Where it ends up | wherever you put it | `C:\Program Files\PWRU Helper\` |
| Needs admin | no | yes, once, to install |
| Start-menu / desktop shortcut | no | yes |
| Updating | download the new exe, replace the old one | run the new installer; it replaces the old version |
| Windows "unknown publisher" warning | yes (the app is not code-signed yet — see below) | yes |
| Best if… | you want zero install, or no admin rights | you want it to behave like a normal installed app |

**Recommendation: the MSI, if you can.** It puts the exe in `Program Files`, which is never synced to OneDrive and does not carry the "downloaded from the internet" mark — two of the things that can make the first launch slow (hypotheses **F1** and **S1**).

> **About the "unknown publisher" warning — where things stand.** PWRU Helper will be code-signed through **SignPath Foundation**, the free programme for open-source projects; that is decided, and it is why the app stays under the **MIT** licence. It is **not done yet**: the project still has to be submitted and approved, which takes weeks. And even once it is signed, Windows builds a publisher's reputation gradually — Microsoft's own guidance says it can take *several weeks and hundreds of clean installs* — so the warning will fade rather than vanish overnight. Signing is also expected to reduce, not eliminate, the first-launch wait described in **A4**: Windows still checks a file it has never seen. Anyone who tells you a certificate makes the warning disappear the next day is wrong. **[CONFIRMED — `recommandations.md` §4]**

## A2. If you use the portable exe: where to put it

**Put it in a plain local folder**, for example:

```
C:\Tools\PWRU Helper\PWRUHelper.exe
```

**Avoid** — these are the placements most likely to make launching slow:

- ❌ **Desktop or Downloads, if you use OneDrive.** On a personal Microsoft account, OneDrive often takes over your Desktop, Documents and Pictures folders. With "Files On-Demand", a file you have not used for a while is evicted and has to be **re-downloaded (all ~180 MB) before it can start** — which can take many seconds with no visible sign that anything is happening. (**F1**)
  If you must keep it there: right-click the exe → **"Always keep on this device"**.
- ❌ **A USB stick or a network drive** — much slower to read and to scan. (**F3**)
- ❌ **Inside a folder protected by "Controlled folder access"** (if you have turned that Windows feature on) — the app creates a small `Data` folder next to itself on first run, and that would be blocked. (**D5**)

The app creates, next to the exe on first run: a `Data\` folder with three small `.json` files you can edit (phrases, slang, squad lists). That is normal.

## A3. Unblock the downloaded file

Windows marks anything downloaded from the internet. Because PWRU Helper is **not code-signed yet**, that mark makes Windows check the file's reputation online before it will run it, and the first launch can be slow while it does (**S1**).

Either:

- right-click `PWRUHelper.exe` → **Properties** → tick **Unblock** at the bottom → OK, **or**
- in PowerShell: `Unblock-File "C:\Tools\PWRU Helper\PWRUHelper.exe"`

If Windows shows a blue **"Windows protected your PC"** screen the first time: click **More info** → **Run anyway**. This is expected for an app without a code-signing certificate; it is not a sign that anything is wrong. (See the box in **A1** for where code signing stands.)

**Unblocking is also the single most useful thing to try if your launches are slow.** Removing that "downloaded from the internet" mark takes the file out of scope for *both* Windows checks that can hold it — the reputation lookup and the cloud check. Time a launch before and after; if it gets noticeably faster, please tell us (A7), because that is a real result.

## A4. What to expect on the first launch

**The expectation line** (Sally's copy, `../02-traduction/ux-mode-degrade.md` §3.8 — use these exact words here and in the README):

> **The first launch after downloading — and after every update — can take up to about 10 seconds, with nothing on screen.** Windows checks a file it has never seen before. Later launches are fast (about a second). Every update is a brand-new file as far as Windows is concerned, so the check happens again after each one.

Why, and what we actually measured:

- **Every release is a brand-new file as far as Windows is concerned**, so Windows checks it before letting it start, and that check happens again after each update. (**D2**, **D3**, **E1**)
- On the one machine we have measured end to end, that check cost **about 2.2–2.5 seconds on each of the first two launches of a new version, then dropped to about 0.02 seconds** from the third launch onwards — same file, same folder, nothing else changed. The app's own startup is a steady ~1.5 seconds on top of that, and it does not vary. **[MEASURED — `mesures-resultats-dev-box.md` §3]** On slower or busier machines the check can take longer; Microsoft's own documentation allows Windows to hold an unknown program for up to 10 seconds by default. **[CONFIRMED]**
- **The second launch is much faster.** **If the second launch is still slow, that is the interesting case** — please report it (see A7). That is the difference between "Windows is checking a new file", which is normal, and something we need to fix.
- There is **no splash screen, and there cannot usefully be one**: most of the wait happens *before* the app is allowed to start, so there is nothing running that could draw anything. Between the double-click and the window appearing you will see nothing at all. That is expected.
- The window is "always on top" by default.

## A5. Optional: a Windows Defender exclusion — read this before doing it

**This is optional, and it has a real security cost. Do not do it just because it makes the app start faster.**

**Try A2 (where the file lives), A3 (`Unblock-File`) and the MSI first.** Those cost nothing and remove the same delay if it is the one we think it is. Only reach for an exclusion if those have been tried and the launches are still consistently slow.

If your launches are consistently slow and you understand and accept the trade-off, you can tell Windows Defender not to scan the app. In an **administrator** PowerShell:

```powershell
Add-MpPreference -ExclusionPath    "C:\Tools\PWRU Helper"
Add-MpPreference -ExclusionProcess "PWRUHelper.exe"
```

To undo it later:

```powershell
Remove-MpPreference -ExclusionPath    "C:\Tools\PWRU Helper"
Remove-MpPreference -ExclusionProcess "PWRUHelper.exe"
```

**What you are agreeing to:** Windows will stop scanning that folder and that program. If anything malicious ever ended up in that folder, Defender would not catch it. PWRU Helper is open source and you can read or build it yourself, which is why this is a defensible choice — but it is *your* choice, and the app will never make it for you. The installer does not do this, and you should be suspicious of any app that offers to.

## A6. Optional but recommended: check the app works

1. Launch it. The window should open on the **Phrasebook** tab.
2. Click any phrase — it is copied to your clipboard; paste it in the game chat.
3. **Screen OCR** tab: it will say whether the Russian OCR language pack is installed. If it is missing, the tab shows the exact command to install it (it needs admin, and it is a Windows feature, not a download from us).
4. Global shortcuts: `Ctrl+Alt+P` (show), `Ctrl+Alt+T` (translate), `Ctrl+Alt+L` (live), `Ctrl+Alt+M` (compact overlay), `Ctrl+Alt+R` (read once). If another app already owns one of these, the **About** tab tells you which ones did not work.

## A7. If it is still slow — what to send

Please include:

1. **How long**, roughly, from double-click to the window appearing — and whether the **second** launch straight after is fast.
2. **Portable or MSI**, and the **full path** of the exe.
3. Whether the folder it is in is **inside OneDrive**.
4. Windows version (`Win + R` → `winver`).
5. Whether you have any antivirus other than Windows Defender.
6. The **About** tab → **Copy error report** button, pasted into your message.

**Even better, if you are willing (about 2 minutes, no admin, nothing is uploaded):** run the two diagnostic scripts from `tools/diagnostics/` and send back what lands in `%USERPROFILE%\PWRU-Diagnostics`. The full instructions and the privacy note are in [`tools/diagnostics/README.md`](../../../tools/diagnostics/README.md). Run them **before** you open the new version manually — the first launch of a new download is the one that matters.

```
powershell -ExecutionPolicy Bypass -File "C:\path\to\tools\diagnostics\Get-MachineSheet.ps1" -ExePath "C:\where\you\keep\PWRUHelper.exe"
powershell -ExecutionPolicy Bypass -File "C:\path\to\tools\diagnostics\Measure-Startup.ps1" -ExePath "C:\where\you\keep\PWRUHelper.exe" -Runs 3
```

The app will open and close three times — that is normal, and you should not click anything while it runs.

---

# Part B — For the owner: reproducing and capturing a slow machine

The goal of a capture session is **one number and one state snapshot**, per machine:

- **the number:** the split between "before the process existed" and "after the process existed" (`t_pre` / `t_in` — measurement **M1** in `hypotheses-matrice.md` §4). Every prior benchmark measured only the total, which is why P1 is still open.
- **the state:** the machine sheet below, so that fast and slow machines can be diffed field by field.

## B1. Machine sheet — fill one per machine, fast machines included

A fast machine is not a control unless it is documented the same way. **Capture at least one machine that starts quickly**, or nothing is comparable.

| Field | How to get it |
|---|---|
| Machine label + is it fast or slow | — |
| OS name, build, edition | `Get-ComputerInfo | Select OsName,OsBuildNumber,WindowsVersion,WindowsEditionId` |
| CPU model, RAM, disk media type | `Get-CimInstance Win32_Processor`, `Get-PhysicalDisk | Select MediaType` |
| Laptop? on battery or plugged? power plan | `powercfg /getactivescheme` (**O3**) |
| Install kind: portable or MSI, **full exe path** | — |
| Exe size, and file attributes | `Get-Item <exe> | Select Length,Attributes` — look for `Offline` / `ReparsePoint` / `SparseFile` (**F1**) |
| Is the exe path under a OneDrive root? | compare with `$env:OneDrive` (**F1**) |
| Mark-of-the-Web present? | `Get-Item <exe> -Stream Zone.Identifier` (an error means no MOTW) (**S1**) |
| `%APPDATA%` and `%TEMP%` resolved paths; are they reparse points? | `echo`, `fsutil reparsepoint query` (**F2**) |
| `%TEMP%\.net\PWRUHelper\` — exists? how many files, total size, newest timestamp? | `Get-ChildItem -Recurse | Measure-Object Length -Sum` (**E1**) — capture this **before** the timed launch |
| `settings.json` → `SettingsVersion`, `LastTab` | `Get-Content "$env:APPDATA\PWRUHelper\settings.json"` (**R3**, **A2**) |
| Antivirus products present (prove Defender-only) | `Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct` |
| Defender preferences | `Get-MpPreference | Select MAPSReporting,SubmitSamplesConsent,DisableBlockAtFirstSeen,CloudBlockLevel,CloudExtendedTimeout,DisableRealtimeMonitoring,EnableControlledFolderAccess,ExclusionPath,ExclusionProcess` (**D1**, **D2**, **D5**) |
| Defender status | `Get-MpComputerStatus | Select AMRunningMode,RealTimeProtectionEnabled,AntivirusSignatureLastUpdated,AntivirusSignatureVersion,QuickScanStartTime,IsTamperProtected` (**D1**, **D6**) |
| Smart App Control state | `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState` (0 off / 1 on / 2 evaluation) (**S2**) |
| VBS / HVCI (memory integrity) | `Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard | Select VirtualizationBasedSecurityStatus,SecurityServicesRunning` (**O2**) |
| Font cache service state | `Get-Service *FontCache* | Select Name,Status,StartType` (**R5**) |
| Proxy configuration | `netsh winhttp show proxy` (not a P1 item under answer (a), but one line and it feeds the *other* startup issue) |
| Monitor count and per-monitor scaling | Display settings (**R6**) |

## B2. What to run — the exact commands

The scripts now exist: `tools/diagnostics/Get-MachineSheet.ps1`, `Measure-Startup.ps1`, `Probe-GoogleTranslate.ps1`, documented in [`tools/diagnostics/README.md`](../../../tools/diagnostics/README.md). They are read-only, upload nothing, need **no admin**, and write into `%USERPROFILE%\PWRU-Diagnostics`. `Measure-Startup.ps1` reports **`pre_process_ms`** (launch requested → the process exists: antivirus scan-on-execute, SmartScreen / Smart App Control lookup, loading a 180 MB unsigned image — *the app's code has not started yet*), **`in_process_ms`** (process start → first window) and **`total_ms`**.

**Use a copy of the exe that was actually downloaded from GitHub.** A locally built file carries no Mark-of-the-Web, so it can never exercise SmartScreen or Block-at-First-Sight — that is exactly why the dev-box run could not settle **S1** (`mesures-resultats-dev-box.md` §4.4).

Replace `<D>` with the folder holding the scripts and `<EXE>` with the full path to the exe under test.

```powershell
# 0. State snapshot FIRST — before any timed launch (it records %TEMP%\.net\PWRUHelper as it is now)
powershell -ExecutionPolicy Bypass -File "<D>\Get-MachineSheet.ps1" -ExePath "<EXE>"

# 1. M4 — first launch of a brand-new build vs the ones after it. THE Block-at-First-Sight signal.
#    Run this before the machine has ever opened that exact build.
powershell -ExecutionPolicy Bypass -File "<D>\Measure-Startup.ps1" -ExePath "<EXE>" -Runs 3 -Note "new build, first launches"

# 2. M2 — the same file two ways. Shell = the real double-click (MOTW + SmartScreen apply).
#    Direct = raw CreateProcess, which skips the shell reputation gate.
powershell -ExecutionPolicy Bypass -File "<D>\Measure-Startup.ps1" -ExePath "<EXE>" -Runs 3 -LaunchMode Shell  -Note "shell"
powershell -ExecutionPolicy Bypass -File "<D>\Measure-Startup.ps1" -ExePath "<EXE>" -Runs 3 -LaunchMode Direct -Note "direct"

# 3. E1/D3 — force a re-extraction of the 5 native DLLs (~8 MB) before each run
powershell -ExecutionPolicy Bypass -File "<D>\Measure-Startup.ps1" -ExePath "<EXE>" -Runs 3 -ClearExtractionCache -Note "extraction cleared"

# 4. S1 — remove the Mark-of-the-Web, then repeat step 2. Same file, same hash, no MOTW.
Unblock-File "<EXE>"
powershell -ExecutionPolicy Bypass -File "<D>\Measure-Startup.ps1" -ExePath "<EXE>" -Runs 3 -Note "after Unblock-File"

# 5. M3 — cold: reboot, then run step 1 again before opening anything else.
```

How to read it:

1. **Always report `pre_process_ms` and `in_process_ms`, never only the total.** Every prior benchmark measured only the total, which is why P1 stayed open. A big `pre_process_ms` means the cost is outside the app (security software / reputation); a big `in_process_ms` means it is the app plus its file I/O (**M1**).
2. **Shell slow + Direct fast ⇒ SmartScreen (S1). Both slow ⇒ Defender or the loader (D1/D2).** Cheapest decisive test in the whole investigation (**M2**).
3. **Cold and warm, ≥ 3 runs each** (**M3**). Run-to-run noise on the owner's box is ±400 ms; a single sample proves nothing.
4. **First launch of a new hash vs the second** (**M4**) is the Block-at-First-Sight and self-extraction signal. Measured on the dev box: **2163 ms then 2528 ms, then 15–42 ms from the third launch** (`mesures-resultats-dev-box.md` §3).
5. **Optional, one machine, consented:** launch with networking disconnected. **If it gets faster offline, D2 (cloud/BAFS) is confirmed** — the cloud query is failing fast instead of waiting out its timeout (**M5**).

> `Probe-GoogleTranslate.ps1` belongs to P2, not P1. If you run it, use `-Smoke` only. `-Burst` / `-Variant` deliberately provoke Google into rate-limiting your **public IP address**, which affects every device on the connection, including the app itself.

## B3. Optional deeper captures (only if B2 leaves it unexplained)

- **`t_pre` is the large half →** on one slow machine:
  `New-MpPerformanceRecording -RecordTo C:\temp\defender.etl` around a cold launch, then
  `Get-MpPerformanceReport -Path C:\temp\defender.etl -TopFiles 20 -TopProcesses 10 -TopScansPerFile 10`.
  This is the only tool that attributes wall-clock time to Defender scanning a *named* file. Also worth a look: `C:\ProgramData\Microsoft\Windows Defender\Support\MPLog-*.txt`.
- **`t_in` is the large half →** apply `trace-instrumentation.md` on a branch (owner's go required), build that branch, and have the user launch with `PWRUHELPER_TRACE_STARTUP=1`; collect `%APPDATA%\PWRUHelper\logs\startup-trace.log`.
- **Still unexplained →** a WPR trace (`wpr -start GeneralProfile -start FileIO -start DiskIO`, launch, `wpr -stop startup.etl`) shows the process-create event, every read of the 178 MB image, and **which other process** (`MsMpEng.exe`, `smartscreen.exe`, `OneDrive.exe`) was busy during the gap. ETL files are large — zip before sending.

## B4. A/B tests, in the order that costs least

Run each on one machine, one variable at a time, timing 3 cold launches before and after. **Restore every change afterwards.**

| # | Change | Confirms / refutes |
|---|---|---|
| 1 | `Unblock-File` the exe | **S1** (SmartScreen / MOTW) |
| 2 | Move the exe from a OneDrive folder to `C:\Tools\` (or "Always keep on this device") | **F1** |
| 3 | Delete `%TEMP%\.net\PWRUHelper\`, launch, then launch again | **E1** + **D3** |
| 4 | Add a Defender path + process exclusion, then remove it | **D1** + **D2** together |
| 5 | `Set-MpPreference -DisableBlockAtFirstSeen $true`, launch, **restore immediately** | **D2** alone — separates it from D1 |
| 6 | Close the app on the **About** tab (index 4) so `LastTab` restores to a near-empty tab, then relaunch | **R3** (first layout of ~140 phrase buttons) |
| 7 | Compare an MSI-installed machine against a portable one | **S1** (no MOTW on the installed payload) and **F1** (`Program Files` is never OneDrive-synced) |

**Safety rules for B4:** tests 4 and 5 weaken the machine's defences. Do them with the user's explicit, informed consent, one at a time, and restore the previous state in the same session. Never leave a test exclusion in place, and never suggest a user do #5 as a fix.

## B5. What to send back for each machine

Everything lands in `%USERPROFILE%\PWRU-Diagnostics`. Send:

1. `machine-sheet-<PC>-<date>.txt` / `.json` — the completed machine sheet (B1).
2. All `startup-<PC>-<timestamp>.csv` / `.txt` files — one row per launch, `pre_process_ms` / `in_process_ms` / `total_ms`, with the `-Note` label identifying the condition (B2 steps 1–4).
3. The new-build first-vs-second launch pair (B2 step 1), explicitly called out.
4. Results of whichever A/B tests were run (B4), with before/after numbers.
5. If captured: the Defender performance report, the `startup-trace.log`, or the zipped WPR trace.

The fields to diff between a fast and a slow machine — and between before and after the signed release — are listed in [`recommandations.md`](recommandations.md) §7.3, and the numbers that would count as success are in §7.4.

## B6. What NOT to conclude

- **Do not conclude from a single launch.** ±400 ms of noise is documented on the owner's own machine; a 6 s vs 8 s difference between two single launches means nothing.
- **Do not compare a `dotnet run` / Debug launch to a shipped one.** `Launch PWRU Helper.bat` runs a Debug, framework-dependent build — a completely different startup path (no single-file host, no extraction, no 178 MB image). Its timings are not comparable to P1.
- **Do not assume a "slow" machine is anomalous until it is compared against a fast one on the same protocol.** The owner's own machine measured cold 3.9–8.9 s / warm 1.08–1.22 s. **If a reported "slow" machine measures the same, there is no slow machine — there is a cold-start cost that the whole user base pays and nobody has been told about**, and the investigation's conclusion changes accordingly (see `hypotheses-matrice.md` §5).

---

_Part A survived Phase 2 and is cleared to be lifted into the README — it is quick wins §3.1–§3.4 of [`recommandations.md`](recommandations.md) (FINAL). The expectation wording in **A4** is Sally's, verbatim from [`../02-traduction/ux-mode-degrade.md`](../02-traduction/ux-mode-degrade.md) §3.8; keep the two in sync. Part B is the "reproduce a slow machine" procedure referenced by `recommandations.md` §7, and it feeds `mesures-protocole.md` and `hypotheses-matrice.md` §4._
