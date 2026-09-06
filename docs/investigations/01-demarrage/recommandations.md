# 01 — P1 startup: recommendations — **DRAFT**

> ## ⚠ THIS IS A DRAFT
> **Nothing here is a decision, and nothing here should be implemented yet.**
> It is written *before* any measurement exists, from static reading plus prior **[REPORTED]** benchmarks. Every item is explicitly conditioned on a hypothesis from `hypotheses-matrice.md` being **confirmed by measurement**. The final version is produced in **Phase 2**, after Mary's research and Amelia-QD's measurement script have run, and after the owner has arbitrated. Expect items to move, shrink, or disappear.
>
> _Phase 1 · author: Amelia (BMAD Senior Software Engineer) · baseline `4759712` (main, v0.14.0) · 2026-09-06._

---

## 0. The one-paragraph summary

The in-process startup path is **already fast** — ~1.2 s warm, of which ~900 ms is the .NET/WPF floor and ~500 ms is application code, all of it previously instrumented and judged not worth deferring **[REPORTED]**. A 6–10 s startup is therefore **~85 % time that no code change can reach**. Consequently the recommendations that matter are **packaging and environment** recommendations — signing, exclusions, where the file lives, what the user is told — and the code-level items below are deliberately small, cheap, and last.

---

## 1. Quick wins

### R1 — Document an optional Windows Defender exclusion (folder **and** process)
**Depends on:** D1 and/or D2 confirmed (Defender scan or Block-At-First-Sight). Refuted if a path exclusion changes nothing.
**Expected gain:** if D1/D2 are the cause, this is the **largest single gain available today** — plausibly 2–8 s off a cold launch, i.e. most of the symptom.
**What it is:** a short, honest section in the README / a FAQ entry showing:
`Add-MpPreference -ExclusionPath "C:\Tools\PWRU Helper"` and `Add-MpPreference -ExclusionProcess "PWRUHelper.exe"` (both, because a path exclusion does not cover the extracted `%TEMP%\.net\PWRUHelper\` payload and a process exclusion does not cover the on-execute scan of the image itself). A narrower variant excludes only `%TEMP%\.net\PWRUHelper`.
**Cost:** an hour of documentation. No code.
**Risk — and it must be stated plainly to the user, not buried:** an exclusion means **Windows stops scanning that folder and that process**. For an open-source app the user downloaded from GitHub and can build themselves, that is a defensible trade; for a user who does not understand what they are agreeing to, it is not. The wording must be "here is what this does and why you might not want it", never "do this to make it fast". **It must be presented as optional and never applied by the installer.**
**Do not:** ship an MSI custom action that adds an exclusion. That is exactly the behaviour malware installers exhibit, it would need elevation, and it would harm the app's own reputation signal.

### R5 — Startup feedback (a splash, or a "starting…" affordance) — *with a hard caveat*
**Depends on:** the `t_pre` / `t_in` split (M1). **This item is conditional on the delay being in-process, and the current top-5 says it probably is not.**
**Expected gain: zero measured milliseconds — it is purely perceptual**, and it only covers the part of the timeline after our process exists.
**The caveat, stated first because it is the whole point:** hypotheses D1, D2, S1, F1 and O1 — currently ~85 % of the probability mass — all occur **before any code of ours can run**. A splash screen cannot cover a Defender cloud hold, a SmartScreen round-trip, or a OneDrive hydration. If the delay is pre-process, **a splash shows the user nothing, because there is no process to show it.** Anyone promising "we'll add a splash and it'll feel fast" without the M1 measurement is promising something the mechanism cannot deliver.
**If `t_in` does turn out to be the large half:** WPF's `SplashScreen` class (a PNG marked `<Resource>` / `SplashScreen` in the csproj) is displayed by native code very early in startup — earlier than any XAML — and is the right tool. A *managed* splash `Window` is not: it starts after the runtime and after WPF, i.e. after the ~900 ms floor, so it would cover only the last ~300 ms. **Measure before promising.**
**Cost:** small (one image + one csproj item type change). **Risk:** being sold internally as a fix for something it does not fix; a splash on an app that then takes another 6 s reads as *broken*, not as *loading*.

### R8 — Set the expectation: "the first launch after each update is slower"
**Depends on:** D2/D3/E1 confirmed (any of the new-hash-per-release mechanisms). Currently the best-supported family of hypotheses.
**Expected gain:** none in time; a large gain in **reported severity**. "It hangs for 10 seconds" and "the first start after an update takes a few seconds while Windows checks the new file" are the same event with very different support costs.
**What it is:** two sentences in the README's install section and one line in each release note. It is also *true* and *explainable*, which is the strongest argument for saying it: every release is a new file hash, so Windows treats it as a file it has never seen.
**Cost:** minutes. **Risk:** none, provided it is not used as a substitute for R1/R2.

### R10 — Tell users where to put the portable exe (and to unblock it)
**Depends on:** F1 (OneDrive hydration) or S1 (MOTW/SmartScreen) confirmed — but it costs nothing and is good advice regardless.
**Expected gain:** on an affected machine, potentially the entire delay (F1 can be 6–30 s on a domestic uplink). On an unaffected machine, nothing.
**What it is:** the guidance already drafted in `checklist-nouvelle-machine.md` — a plain local folder such as `C:\Tools\PWRU Helper\`, **not** a OneDrive-synced Desktop or Downloads; `Unblock-File` or the file-properties "Unblock" checkbox after downloading.
**Cost:** documentation only. **Risk:** none.

---

## 2. Structural

### R2 — Code signing
**Depends on:** D1, D2 or S1 confirmed. It is the only intervention that attacks the *root* of all three at once.
**Expected gain: [UNKNOWN] but potentially the whole symptom.** What signing does and does not do must be separated carefully, because they are routinely conflated:

| | Effect of an **OV** certificate (SignPath Foundation) | Effect of an **EV** certificate |
|---|---|---|
| **SmartScreen** (S1) | Reputation accrues **gradually**, per-publisher, as downloads accumulate. Not instant. Already documented honestly in `packaging/signpath-signing.md:14-16`. | Instant SmartScreen reputation. |
| **Defender cloud / BAFS** (D2) | A valid signature from a known publisher is a strong positive trust signal, and publisher reputation is shared across the publisher's files — so a **new release of an already-trusted publisher is far less likely to be an "unknown file"**. This is the mechanism that would end the per-release hash penalty. **[INFERRED — general Windows knowledge, Mary to source.]** | Same, sooner. |
| **Defender local scan** (D1) | **Little or nothing.** The file is still scanned. Signing does not make a 178 MB image cheaper to read. | Same. |
| **Smart App Control** (S2) | Signed + reputable is what SAC requires; unsigned is what it blocks. | Same. |

**So: signing plausibly fixes D2 and improves S1, and does *not* fix D1.** If the measurements land on D1 (permanent local re-scan) rather than D2 (first-seen cloud hold), signing is a smaller win than the memory note `pwru-startup-perf` currently assumes — that assumption is itself worth re-testing.

**Status and blockers (all [CONFIRMED]):**
- SignPath is **not wired**: `.github/workflows/release.yml` contains zero SignPath steps (`00-inventaire-stack.md` §3.6). The YAML exists only inside `packaging/signpath-signing.md:103-156`, and its `if: ${{ secrets.X != '' }}` step guards are suspect (`secrets` is not a documented context for `steps.<id>.if`) — map to a job-level `env:` instead.
- The application has **not been submitted** to SignPath Foundation. Approval takes days to weeks (`packaging/signpath-signing.md:44`).
- **Licence blocker:** SignPath Foundation free signing requires an **OSI-approved** licence. The repo ships **MIT** (`README.md:230`, `packaging/signpath-signing.md:23,33`), which qualifies — but **open PR #49 proposes relicensing to CC BY-NC 4.0, which is not OSI-approved and would disqualify the project.** This is a decision the owner must make *before* any signing work starts; doing signing first and merging PR #49 later would waste the effort.
- The certificate is issued to **"SignPath Foundation"**, not "Kizotis" — Windows will name SignPath as the publisher. That is a product decision, not just a technical one.

**Cost:** one application, one dashboard setup, ~50 lines of workflow, plus fixing the step-guard trap. **Risk:** external dependency and an indefinite approval delay; a per-release CI step that can fail the release pipeline; and it does nothing for D1.

### R3 — Give the single-file bundle a stable extraction directory
**Depends on:** E1 confirmed (extraction cost is material and repeats).
**What it is:** set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a stable per-user path (e.g. under `%LOCALAPPDATA%\PWRUHelper\bundle`) instead of leaving it at the `%TEMP%` default, so that **Storage Sense and Disk Cleanup stop wiping it** and re-extraction only happens when the build actually changes.
**Expected gain:** removes one class of *random* slow launches (the "why is it slow again today" ones). Does nothing for the first launch of a new build, which still extracts.
**Cost:** it cannot be set from inside the process (the host reads it before managed code runs), so it needs either a launcher, an installer-set environment variable, or `AppContext`/runtimeconfig — **none of which is free, and a launcher would break the "one portable exe" promise.** Realistically this is an **MSI-only** option. **Verify the mechanism before planning the work — it is currently [UNKNOWN] whether a `runtimeconfig` knob can set it at all.**
**Risk:** leaves a directory behind that nothing ever cleans; uninstall must remove it.

### R4 — Ship the native libraries beside the exe, for the MSI build only
**Depends on:** E1 + D3 confirmed.
**What it is:** drop `IncludeNativeLibrariesForSelfExtract=true` from the **MSI** publish only (`Build MSI Installer.bat:31`, and the corresponding step in `release.yml:46`), so the native DLLs are laid down next to the exe in `Program Files` at install time and **never extracted, never re-extracted, and scanned once by the installer rather than on every new build**.
**Expected gain:** removes E1 and D3 entirely for MSI users. The one **[REPORTED]** data point that touches this — a fully non-bundled build measured **0.91–0.94 s warm** vs 1.08–1.22 s for the bundled one — suggests ~150–300 ms warm as well, though that build differed in more ways than one.
**Cost:** `installer/Product.wxs` currently installs **exactly one file** (`Product.wxs:42`, `KeyPath="yes"`). It would need a component per native DLL, and that list must track the SDK across .NET servicing updates — a real maintenance burden, and a class of breakage (`MajorUpgrade` leaving orphans) that the current single-file design was chosen to avoid.
**Risk / hard constraint:** **this must NOT be applied to the portable build.** "One file, no install, put it anywhere" is the portable artefact's entire product promise (`README.md`), and `tests/PWRUHelper.Tests/PublishFlagsTests.cs:62-70` currently *enforces* flag parity across the three build paths — so this change requires deliberately breaking that parity test and re-expressing it as "portable and MSI may differ **only** in this flag". That test exists for a good reason and weakening it is not free.

### R9 — Stop the MSI's three denied writes per launch
**Depends on:** F4 measured to cost anything (expected: it does not — the whole `Load*` block is 30 ms **[REPORTED]**).
**What it is:** either install the 3 JSON files with the MSI, or cache the "exe directory is not writable" verdict for the process lifetime instead of retrying per file, per launch, forever (`MainWindow.Phrasebook.cs:237-245`).
**Expected gain:** single-digit ms. **This is a tidiness item, not a performance item**, and it is listed only so the lead from Phase 0 (F4) is closed rather than left open.
**Cost:** small. **Risk:** touching `FindOrCreateEditable` touches the first-run behaviour of all three data files — the code path with the most subtle prior bugs in this repo. Not worth it on current evidence.

---

## 3. Code-level items (small, and last on purpose)

### R6 — Move the first log write off the critical path
**Depends on:** the trace showing `app.beforeFirstLog → app.afterFirstLog` costs something (expected: single-digit ms; only F2, a redirected `%APPDATA%`, could inflate it, and F2 is unlikely on personal machines).
**What it is:** `App.xaml.cs:32` writes the session marker synchronously, on the UI thread, before `MainWindow` exists, via a `Logging` path that does `Directory.CreateDirectory` + open + append + close per line (`Logging.cs:81-87`). Options: queue it and flush after the window is up, or write it from a worker.
**Expected gain:** a few ms in the normal case. **Cost:** small. **Risk:** `Logging`'s contract is "never throws, never blocks the app" (`Logging.cs:89`) and it is what the About tab's error report reads back (`Logging.cs:107-127`); making it asynchronous introduces ordering questions for crash reports — a marker that has not been flushed when the app crashes is a marker that is not in the report. **Only worth doing if measured.**

### R7 — `%LOCALAPPDATA%` instead of Roaming for logs
**Depends on:** F2 confirmed (`%APPDATA%` redirected or synced).
**Reality check: on the affected population this is almost certainly worth nothing.** The machines are personal and non-domain, and **OneDrive Known Folder Move does not cover `%APPDATA%`** — it covers Desktop, Documents and Pictures. So Roaming here is a plain local folder. Recorded because the code path is real (`Logging.cs:18-20`, `SettingsService.cs:87-89`), not because it is promising.
**Cost:** moving `settings.json` would need a migration for existing users, which is not free and touches the settings machinery this project has already been bitten by. **Recommendation as of this draft: do not do it unless F2 is actually observed.**

---

## 4. NOT proposed — rejected by the owner, or measured and rejected

Listed explicitly so nobody re-raises them in Phase 2 (per `pwru-open-threads`: "do NOT re-raise as new findings").

| Item | Why not |
|---|---|
| **`PublishReadyToRun`** | Measured **worse** (cold 6.4–10.7 s vs 3.9–8.9 s) and banned. **[REPORTED]** |
| **`EnableCompressionInSingleFile`** | Removed on purpose in PR #52: it cost **~118 MB of working set** next to the game, which is the actual product requirement. Guarded by `tests/PWRUHelper.Tests/PublishFlagsTests.cs:44-60`. **[CONFIRMED]** |
| **Trimming / NativeAOT** | Impossible with WPF — `error NETSDK1168`, verified not assumed. **[REPORTED]** |
| **Native rewrite (C/C++/Python)** | Answered and closed: capture is native GDI, OCR is native WinRT, translation is network; the only managed CPU in the hot path is ~0.4 % of a core. A rewrite buys memory and binary size, not speed, at the price of ~5,900 lines and 256 tests. **[REPORTED]** |
| **MVVM / view-models / binding frameworks** | Deliberate architectural decision (`project-context.md`). Irrelevant to startup anyway. |
| **Splitting `MainWindow.xaml` to reduce the 206–233 ms `InitializeComponent`** | Already examined; "the biggest code-side cost, not addressable without splitting the XAML" and judged not worth it. **[REPORTED]**. Revisit only if the trace shows it costs *seconds* on a slow machine, which would be a different finding entirely. |
| **Deferring `Load*` / `BuildSquadTab` / `ApplySettings` off the startup path** | Measured at 5–17 ms each; "Nothing here is worth deferring — that was checked and rejected." **[REPORTED]** |
| **Fewer releases, to reduce hash churn** | Mentioned in the brief and **rejected here on principle.** Shipping less often to please a reputation heuristic is optimising the wrong variable: it slows the product down for every user to spare some users a one-off first-launch cost, and it does not remove the cost — it just makes each occurrence rarer and *larger* (bigger diffs, more surprising changes). The correct answer to hash churn is **signing** (R2), which makes a new hash from a known publisher cheap, not shipping less. |
| **Turning off Defender, BAFS, or SmartScreen as a "fix"** | Never. These are *diagnostic* toggles, used once, with consent, and restored. Shipping advice to weaken a machine's defences is out of the question for an app distributed as an unsigned binary. |

---

## 5. Sequencing (proposed, for Phase 2 arbitration)

1. **Measure first.** M1/M2 (the `t_pre` / `t_in` split) from Amelia-QD's script, on ≥ 2 slow and ≥ 1 fast machine. Nothing below is actionable without it. **Cost: zero code.**
2. If `t_pre` dominates → `Get-MpPerformanceReport` on one slow machine, plus the MOTW / OneDrive / `%TEMP%` state capture. Then **R1** (documented exclusion) and **R8** + **R10** (expectation and placement) ship immediately as documentation, and **R2** (signing) becomes the structural project — gated on the **PR #49 licence decision**, which must be settled first.
3. If `t_in` dominates → apply `trace-instrumentation.md` on a branch, get the breakdown, and only then decide between **R5** (splash), **R6**, and revisiting the XAML.
4. **R3 / R4** (extraction) are worth doing only if M4 shows the first-launch-of-a-new-build penalty is large *and* the MSI is the distribution the owner wants to favour.
5. **R7 / R9** stay parked unless their preconditions are observed.

---

_Final version follows in Phase 2, re-scored against real measurements. Companion documents: `hypotheses-matrice.md`, `trace-instrumentation.md`, `checklist-nouvelle-machine.md`, and `mesures-protocole.md` (Amelia-QD)._
