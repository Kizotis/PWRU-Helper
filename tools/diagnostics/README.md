# PWRU Helper — diagnostics scripts

Three read-only PowerShell scripts that collect the facts needed to explain **why PWRU Helper
takes 6–10 s to start on some machines** (P1) and **why Google Translate answers "wait a minute"**
(P2). They change nothing on your PC. They upload nothing. They write a couple of small text
files into `%USERPROFILE%\PWRU-Diagnostics`, and you decide what to send back.

Requirements: Windows 10/11, stock Windows PowerShell 5.1 (already installed), **no admin needed**.

---

## The three commands (copy, paste, press Enter)

Open **PowerShell** (Start menu → type `powershell` → Enter), then paste one line at a time.
Replace `C:\path\to\tools\diagnostics` with the folder these scripts are in.

### 1. Machine sheet — what your PC looks like (30 seconds)

```
powershell -ExecutionPolicy Bypass -File "C:\path\to\tools\diagnostics\Get-MachineSheet.ps1"
```

Add `-ExePath "C:\where\you\keep\PWRUHelper.exe"` if the app is not in this repo's build folder —
it lets the script check that exact file (signature, Mark-of-the-Web, where it lives).

### 2. Startup timing — where the seconds actually go (about 1 minute)

```
powershell -ExecutionPolicy Bypass -File "C:\path\to\tools\diagnostics\Measure-Startup.ps1" -ExePath "C:\where\you\keep\PWRUHelper.exe" -Runs 3
```

The app will open and close three times. That is normal — the script kills it between runs so the
next launch starts clean. **Do not click anything while it runs.**

The first launch after you download a new version is the interesting one, so run this **before**
you have opened that new version manually.

### 3. Google probe — is Google throttling this connection? (15 seconds)

```
powershell -ExecutionPolicy Bypass -File "C:\path\to\tools\diagnostics\Probe-GoogleTranslate.ps1" -Smoke
```

Five requests, two seconds apart. Completely safe — this is roughly what the app does when you
translate five short lines.

---

## What to send back

Everything lands in `%USERPROFILE%\PWRU-Diagnostics` (paste that into the Explorer address bar).
Send **all** the files from that folder:

| File | What it is |
|---|---|
| `machine-sheet-<PC>-<date>.txt` / `.json` | your Windows/antivirus/network configuration |
| `startup-<PC>-<timestamp>.csv` / `.txt` | one line per launch, with the timing split |
| `google-probe-smoke-<PC>-<timestamp>.csv` / `.txt` | what Google answered |

Also useful, in one sentence each: is this the **portable exe** or the **MSI installer**? Where did
you put the exe (Desktop, Downloads, OneDrive folder…)? Was the app slow on the *second* launch too,
or only the first after an update?

---

## Privacy

- **Nothing is sent anywhere by the scripts.** You send the files yourself.
- Your Windows user name and your profile path are replaced with `<USER>` / `<USERPROFILE>` in the
  machine sheet.
- Your **public IP address is deliberately NOT collected**. `Get-MachineSheet.ps1 -IncludePublicIp`
  adds it, and is only worth using if we are chasing a per-IP Google block.
- The machine sheet does record: Windows version, CPU/RAM model, antivirus product and settings,
  GPU, monitor resolution, proxy/DNS settings, whether the PC is joined to a company account, and
  the names/sizes/dates of the app's own files. Open the `.txt` and read it before sending — it is
  plain text on purpose.
- The Google probe sends ten short generic Russian phrases (`hello everyone`, `who is going to the
  dungeon`…). None of your own text is ever sent.

---

## Reference — the parameters that matter

### `Measure-Startup.ps1`

| Parameter | Default | Why you would change it |
|---|---|---|
| `-ExePath` | this repo's portable publish output | point at the exe you actually run |
| `-Runs` | 3 | more runs = better averages; 5 is plenty |
| `-LaunchMode` | `Shell` | `Shell` = what a double-click does (SmartScreen applies). `Direct` = raw process creation, no shell layer. Running both isolates the shell/reputation cost. |
| `-ClearExtractionCache` | off | deletes `%TEMP%\.net\PWRUHelper\*` before each run, forcing the app to re-extract its 5 native DLLs (~8 MB) and the antivirus to re-scan them. This reproduces "first launch of a brand-new version". Off by default because it is not what a normal launch does. |
| `-OutDir` | `%USERPROFILE%\PWRU-Diagnostics` | |
| `-Note` | empty | free text copied into every CSV row — use it to label a condition ("after reboot", "on OneDrive folder") |

It reports three numbers per run:

- **`pre_process_ms`** — from "launch requested" to "the process exists". Antivirus scan-on-execute,
  SmartScreen / Smart App Control reputation lookup, and loading a 180 MB unsigned image live here.
  **The app's own code has not started yet.**
- **`in_process_ms`** — from the process starting to its first window. Native-library extraction,
  JIT, reading `settings.json` / `phrases.json` / `slang.json` / `squad.json`, XAML load and the
  first layout of the Phrasebook grid live here.
- **`total_ms`** — the two added together; this is what a stopwatch would show.

A big `pre_process_ms` means the problem is outside the app (security software / reputation).
A big `in_process_ms` means it is the app plus its file I/O.

### `Get-MachineSheet.ps1`

No required parameters. Every probe is optional: a missing cmdlet or an "access denied" is recorded
as `(unavailable: …)` and the sheet continues. Two fields **do** need admin and will say so:
Defender's `ExclusionPath` / `ExclusionProcess` / `ExclusionExtension`.

### `Probe-GoogleTranslate.ps1`

| Mode | What it does | Risk |
|---|---|---|
| `-Smoke` (default) | 5 requests, 2 s apart | none |
| `-Burst` | up to `-Count` requests every `-IntervalMs`, stops at the first 429, then polls once every 30 s for up to `-MaxWaitMinutes` to measure how long the block lasts | **see below** |
| `-Variant` | same as Burst, cycling User-Agent / `Accept-Language` variants to test whether the request fingerprint moves the threshold | **see below** |

> ### ⚠ Burst / Variant warning
> These modes **deliberately provoke Google into rate-limiting your public IP address**. The block
> can last from a few minutes to a few hours and applies to **every device on the same internet
> connection** — other people in the house, your phone, and PWRU Helper itself. Do not run them
> while streaming or working. They refuse to start unless you add `-IUnderstandTheRisk`.
>
> `-Smoke` carries none of this risk and is what you should run unless asked otherwise.

The probe reproduces the app's request exactly: same URL and query parameters, the same hard-coded
Chrome 120 User-Agent from `Services/GoogleGtxTranslator.cs`, no `Accept`, no `Accept-Language`, no
cookies, the Windows system proxy, HTTP/1.1, a 12 s timeout.

**Known limitation.** PowerShell 5.1 runs on .NET Framework, so its TLS handshake is not
byte-identical to the app's .NET 8 client (both use Windows SChannel, so it is close). If Google
discriminates on TLS fingerprint, a result here is indicative, not proof.

**Encoding note.** Output files are written as ASCII so they survive email and chat clients intact;
Cyrillic inside a captured response body therefore shows up as `?`. The HTTP status, the headers and
the body *shape* — which is all the diagnosis needs — are unaffected.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `... cannot be loaded because running scripts is disabled` | you dropped the `-ExecutionPolicy Bypass` part — paste the whole line |
| `ERROR: exe not found` | pass `-ExePath "C:\full\path\to\PWRUHelper.exe"` |
| `timeout-no-window` in the CSV | the app never showed a window within 90 s — that is itself a finding; send the CSV |
| `exited-before-window` | another copy of PWRU Helper was already running (it allows only one). Close it and re-run. |
| Startup numbers look far too good | you had already launched that exact exe today; the antivirus has cached its verdict. The *first* launch of a *new* download is the one that matters. |
