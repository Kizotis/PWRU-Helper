# 01 — Startup measurements, machine 2 (owner's personal machine, MSI install)

_Phase 4 field data · collected by the owner with `tools/diagnostics/` on 2026-09-06 16:52 local · summarised by Winston (raw sheets kept off the public repo: they contain the machine's Defender exclusion paths)._

## 1. Machine sheet (relevant fields only)

| Field | Value | Relevance |
|---|---|---|
| Host / OS | `DESKTOP-GE2OJQS`, Windows 11 build 26200 (25H2), x64 | consumer build |
| Hardware | Ryzen 9 5900X (24 threads), 32 GB, RTX 3060 Ti, 3 monitors (2560×1440 ×2, 1920×1080 primary), 96 DPI | fast, rules out hardware |
| Account | not domain/Azure AD joined (`WorkplaceJoined: YES` only) | personal machine, no GPO |
| Security | **Windows Defender only**; RTP on, tamper-protected; `MAPSReporting=2` (Advanced), `SubmitSamplesConsent=1`, `DisableBlockAtFirstSeen=False`, `CloudBlockLevel=0` (default), `CloudExtendedTimeout=0` (→ default 10 s hold) | **Block at First Sight is armed with consumer defaults** (research A1.7) |
| Reputation | SmartScreen at OS default (keys unset); **Smart App Control off**; VBS/HVCI off | SmartScreen live for MOTW files; SAC not a factor |
| Exe under test | `C:\Program Files\PWRU Helper\PWRUHelper.exe`, 187,458,534 B, sha256 `D61DDA49…`, modified 2026-08-04 (v0.14.0 MSI), **NotSigned, no Mark-of-the-Web** | MSI-installed → BAFS gate not armed for this file (MOTW-gated) |
| Single-file extraction | `%TEMP%\.net\PWRUHelper`: 6 files, 12.6 MB, written 2026-07-07 … 2026-08-30 | several builds' extractions accumulated; nothing re-extracted today |
| Profile | `%APPDATA%` Roaming = plain local directory; OneDrive present (`<USERPROFILE>\OneDrive`), app data not under it | no sync-folder involvement for the MSI exe |
| Network | direct (no WinHTTP/IE proxy, no autodetect), Ethernet, DNS = router | no WPAD |
| Uptime at test | ~9 h (booted 07:55) | **not** a cold-after-reboot run |

## 2. Startup — `Measure-Startup.ps1`, Shell mode, 3 runs, note `msi-install` [MEASURED]

| run | pre_process_ms | in_process_ms | total_ms |
|---|---|---|---|
| 1 | 29.1 | 1110.4 | 1139.5 |
| 2 | 3.2 | 1046.8 | 1050.1 |
| 3 | 3.0 | 1054.6 | 1057.6 |
| **avg** | **11.8** | **1070.6** | **1082.4** |

## 3. Google probe — `-Smoke` (5 requests, 2 s apart) [MEASURED]

`client=gtx` from this machine's connection: **5 × HTTP 200**, avg 342.6 ms, **no `Retry-After`**, JSON array body, `Accept-CH` client-hint request present. **This connection is not throttled at the time of the test.**

## 4. Reading

1. **A known hash starts in ~1.1 s on a consumer Defender-default machine.** Pre-process time is 3–29 ms: nothing in the OS/AV path delays a file Defender has seen for a month. In-process is ~1.05 s — slightly *below* the dev box (~1.5 s): faster CPU/GPU, same code. **The 6–10 s do not reproduce on a known file.** [MEASURED]
2. This is exactly the shape predicted by hypothesis D2/D1 (`hypotheses-matrice.md` §2): the cost is paid on the **first launches of a new file** (every release, every fresh download) and possibly on the **first launch after a reboot** (cold file cache for a 187 MB image). Neither condition was present in this run. [INFERRED]
3. The machine is **configured to pay the BAFS hold** (MAPS Advanced + sample consent + BAFS on, default 10 s) — but only for a MOTW file. The MSI-installed exe carries no MOTW, which supports `recommandations.md` rank #3 ("MSI as the default download"). What this run cannot tell: whether the *portable* download (MOTW) pays the 10 s on this machine. [INFERRED]
4. P2 on this connection: not blocked today; the smoke shape (200, no `Retry-After`) matches the dev box's healthy state. The block is intermittent, as reported. [MEASURED]

## 5. Next measurements requested from the owner (protocol conditions C3 and E1)

| # | Condition | Command (given to the owner) | Settles |
|---|---|---|---|
| T1 | First launch **right after a Windows reboot**, known MSI exe, 2 runs, note `after-reboot` | `Measure-Startup.ps1 -ExePath "C:\Program Files\PWRU Helper\PWRUHelper.exe" -Runs 2 -Note after-reboot` | cold file cache / Defender post-boot scan (O-group hypotheses) |
| T2 | **Freshly downloaded portable exe** (MOTW) on the Desktop, never launched by hand, 3 runs, note `fresh-download` | `Measure-Startup.ps1 -ExePath "C:\Users\<user>\Desktop\PWRUHelper.exe" -Runs 3 -Note fresh-download` | BAFS 10 s hold + SmartScreen (D2, S1) — the everyday "after an update" experience |

Also asked: whether this machine is one of the machines on which the 6–10 s were observed (if not, the "slow" population still has no measured member — R-12 stays open).
