# 01 — P1 startup: recommendations — **FINAL**

_Phase 2 · author: Amelia (BMAD Senior Software Engineer) · status: **FINAL** · baseline commit `4759712` (main, v0.14.0) · 2026-09-06._

**Inputs this document is built from** (all read in full):
`hypotheses-matrice.md` (26 hypotheses, IDs used below) · `recherche-environnement-et-profiling.md` (A1–A4, B5–B7, C8–C11, D12–D14, "Implications", "Recommended measurement recipe", 59 sources) · `mesures-resultats-dev-box.md` (9 timed runs, machine sheet) · `mesures-protocole.md` + `tools/diagnostics/` · `checklist-nouvelle-machine.md` · `docs/investigations/README.md` (owner's answers (a)(b)(c) and the **owner's decisions of 2026-09-06**) · `packaging/signpath-signing.md` · `.github/workflows/release.yml` · `tests/PWRUHelper.Tests/PublishFlagsTests.cs` · `Build Portable EXE.bat` · `Build MSI Installer.bat` · `installer/Product.wxs` · `project-context.md`.

Evidence grades are carried through: **[MEASURED]** · **[CONFIRMED]** (read in code/config with `file:line`, or a dated first-party source) · **[INFERRED]** (derived, reasoning stated) · **[UNKNOWN]** (still needs a measurement).

> **No production code is changed by this document.** The workflow change in §4 is *described*, with the exact YAML, for Phase 4 to apply. Nothing here edits `.github/workflows/release.yml` or `packaging/*`.

---

## 1. What P1 is, in one paragraph

P1 is not a code problem. On the one machine measured end to end, the **first two launches of a brand-new build cost 2163 ms and 2528 ms *before the app executes a single instruction*, and from the third launch that same pre-process cost collapses to 15–42 ms** — same file, same folder, same launch mode, ~40 s apart (`mesures-resultats-dev-box.md` §3, runs #0–#3) **[MEASURED]**. Over the same nine runs the in-process half is a **flat 1477–2256 ms floor with no relationship to the condition** — it does not drop when warm and does not rise when the native-library extraction cache is cleared (§4.2–4.3) **[MEASURED]**. So the variable part of the symptom lives entirely in the window between "the user double-clicked" and "our process exists", which is exactly where the matrix put ~85 % of the probability mass, leaving only ~4 % and ~500 ms to the app's own pre-window work (`hypotheses-matrice.md` §2) **[INFERRED, strong]**. The prime suspect for that window is **Windows Defender cloud-delivered protection / Block at First Sight**, which by Microsoft's own documentation holds an unknown executable for **10 s by default, extensible to 60 s**, can be **synchronous** ("the file doesn't open until the cloud renders a verdict"), and is **gated on Mark-of-the-Web** — so a downloaded portable exe is in scope and an MSI-installed one normally is not (`recherche-environnement-et-profiling.md` A1.1–A1.5, MS Learn dated 2026-09-01/02) **[CONFIRMED as a mechanism, [UNKNOWN] on the affected machines]**. Every release produces a new file hash on an **unsigned** binary, which re-arms that machinery for every user, every time (A4.3) **[CONFIRMED]**. Two caveats bound everything below: the dev box is **not representative** (Azure-AD joined, ESET + Acronis alongside Defender, `CloudExtendedTimeout=50`, `MAPSReporting=1`) while the affected machines are personal with Defender only (`mesures-resultats-dev-box.md` §2, §4.6; owner's answer (c)) **[MEASURED]**; and the owner's own August cold numbers were 3.9–8.9 s, so "6–10 s" may be **the universal cold cost of a fresh hash** rather than a defect of "some machines" (`hypotheses-matrice.md` §0) **[INFERRED]**.

---

## 2. Ranked recommendations

Ranked by *(confidence × gain) ÷ effort*, and by what must happen first. "Owner decision status" reflects the decisions recorded in `docs/investigations/README.md` ("Owner's decisions", Phase 2 go, 2026-09-06); anything not arbitrated there is marked **proposed**.

| Rank | Action | Hypothesis addressed | Expected gain | Effort | Risk | Owner decision | Depends on |
|---|---|---|---|---|---|---|---|
| **1** | **Baseline-measure one affected personal machine** — `Get-MachineSheet.ps1` + `Measure-Startup.ps1` on a **freshly downloaded** (MOTW-carrying) v0.14.0: first launch vs second, Shell vs Direct, then again after `Unblock-File` (§7). | D1, D2, S1, E1 — splits the matrix | **0 ms.** Its value is converting D2/S1 from [UNKNOWN] to [MEASURED]; **nothing else in this table is verifiable without it** | **S** (~15 min of a volunteer's time, no admin) | None — read-only scripts, nothing uploaded (`tools/diagnostics/README.md`, "Privacy") | **Proposed** | A volunteer on an affected machine |
| **2** | **Set the expectation** in the README and in every release note: the first launch after an install *and after every update* is slower, because Windows treats each release as a file it has never seen. Copy is written — Sally's `ux-mode-degrade.md` §3.8, reproduced in §3.1. | D2, D3, E1 | **0 ms; large reduction in reported severity.** "It hangs for 10 s" and "the first start after an update takes a few seconds" are the same event with very different support cost | **S** (one README bullet + one release-note line) | None, *provided* it is not sold as a substitute for ranks 3–6 | **Proposed** | — (copy is ready) |
| **3** | **Recommend the MSI as the default download** for non-technical users; keep the portable exe for "no admin / zero install". | D2, S1, F1 | **[INFERRED]** removes the BAFS gate entirely — an exe written into `Program Files` by `msiexec` carries no `Zone.Identifier` (research A1.5), and `Program Files` is never OneDrive-synced. Magnitude, by analogy with the dev box's fresh-hash runs: **−2.2 to −2.5 s** on the first launch of a new build; **[UNKNOWN]** on the affected machines until rank 1 runs | **S** (README wording only; the MSI already ships — `.github/workflows/release.yml:49-68`) | Low. The MSI needs admin once and is a bigger behavioural change for a portable-first audience | **Proposed** | Rank 1, experiment 1 (MSI vs portable) |
| **4** | **Placement + `Unblock-File` guidance for portable users** — plain local folder, not a OneDrive-synced Desktop/Downloads; unblock after downloading. | S1, F1, F3 | **[INFERRED]** removing MOTW disarms both the SmartScreen gate (A3.5) and the BAFS gate (A1.5) → same **2.2–2.5 s** order of magnitude, per new download. F1 alone can be 6–30 s on a domestic uplink | **S** (documentation; already drafted in `checklist-nouvelle-machine.md` A2–A3) | None | **Proposed** | — |
| **5** | **Code signing via SignPath Foundation** — full action plan in §4. | D2, S1, S2; partially D1 | **[UNKNOWN] numerically.** What it buys is structural: a **stable publisher identity so reputation accumulates across releases instead of resetting to zero at every tag** (A4.3, A4.4), removal of Smart App Control's "unsigned ⇒ blocked" path (A4.10), and a stronger signal in the BAFS metadata payload (A4.5). Expect **weeks**, not days, before it shows | **L** (external application + dashboard setup + ~55 lines of workflow + a test release) | Medium: external dependency, indefinite approval delay, a per-release CI step that can fail a release, and the publisher shown is *SignPath Foundation*, not *Kizotis* (`packaging/signpath-signing.md:10-16`) | **ACCEPTED** (`docs/investigations/README.md`, "Owner's decisions" #3) | **PR #49 closed so the licence stays MIT** (`README.md:230`); SignPath approval |
| **6** | **Document an optional Defender exclusion (folder *and* process)**, with its security trade-off stated plainly. Never applied by the installer. | D1 + D2 together | **[MEASURED as a mechanism]** — the only lever that reaches the *local scan* as well as the cloud hold. On the dev box the pre-process cost it would remove is **2.2–2.5 s** on a fresh hash; plausibly more on the affected machines | **S** (documentation) | **High if worded badly.** It really does stop Windows scanning that folder and that program. It must read "here is what this does and why you might not want it", never "do this to make it fast" | **Proposed** | Rank 1 — do not recommend a security trade-off for an unproven cause |
| **7** | Ship the 5 WPF native DLLs beside the exe, **MSI build only**. | E1, D3 | **~0 ms measured** — clearing the extraction cache moved `in_process_ms` from a 1525–1793 ms warm band to 1627/2211 ms, inside the warm runs' own noise (`mesures-resultats-dev-box.md` §4.3) | **M** | Medium — see §5.1; breaks the publish-flag parity the test suite enforces | **NOT recommended** (§5.1) | — |
| **8** | `%LOCALAPPDATA%` instead of Roaming for logs/settings. | F2 | **0 ms** on this population — OneDrive Known Folder Move does not cover `%APPDATA%\Roaming` (`mesures-resultats-dev-box.md` §4.5) | **M** (needs a settings migration) | Touches the settings machinery that has already produced shipped bugs | **NOT recommended** (§5.2) | — |
| **9** | Move the first log write off the UI thread. | A1 | **Single-digit ms** | **S** | Ordering questions for crash reports (§5.3) | **NOT recommended** (§5.3) | — |

---

## 3. Quick wins — no code change at all

These four are documentation. Together they are the whole of what can ship in the next release without touching a line of C#.

### 3.1 Expectation text (README install section + one line per release note)

**Take Sally's copy verbatim** — `docs/investigations/02-traduction/ux-mode-degrade.md` §3.8 ("P1 expectation copy (startup)"). Two of her three placements need no code and belong here:

**README** — a new bullet under `⬇️ Download & use`, and the same words in `checklist-nouvelle-machine.md` §A4:

> **The first launch after downloading — and after every update — can take up to about 10 seconds, with nothing on screen.** Windows checks a file it has never seen before. Later launches are fast (about a second). Every update is a brand-new file as far as Windows is concerned, so the check happens again after each one.

**Release notes** — one line at the top of every release, verbatim, every time:

> First launch after this update can take a few seconds while Windows checks the new file. Launches after that are back to normal.

This is *true and explainable*, which is the strongest argument for saying it. It complements the current README note, which covers only the SmartScreen dialog (`README.md:86-88`) and says nothing about the silent wait that precedes it.

**Sally's third placement — a one-time in-app toast after a version change — is a code change**, so it is out of scope for §3 (quick wins) and is not scored in §2's table. It is a good idea, it is specified in her §3.8 (a new `LastRunVersion` in `AppSettings`, seeded by `Migrate` with the *current* version so no existing user gets a spurious toast; reuse `ShowToast`; **suppress in compact mode**, because `ShowToast` routes to `_overlay.SetStatus` and would overwrite the LIVE status line mid-raid), and it should be sized with the rest of the Phase 2 UX work rather than here.

### 3.2 The new-machine checklist

`checklist-nouvelle-machine.md` Part A is written to be lifted verbatim into the README or pinned on Discord. It has been updated alongside this document to reflect the decisions (signing pending, what to expect today, the exact diagnostic commands). Ship Part A; keep Part B for the owner.

### 3.3 Optional Defender exclusion — with the trade-off stated plainly

For a user whose launches are consistently slow *and* who understands what they are agreeing to, in an **administrator** PowerShell:

```powershell
Add-MpPreference -ExclusionPath    "C:\Tools\PWRU Helper"
Add-MpPreference -ExclusionProcess "PWRUHelper.exe"
```

To undo it later:

```powershell
Remove-MpPreference -ExclusionPath    "C:\Tools\PWRU Helper"
Remove-MpPreference -ExclusionProcess "PWRUHelper.exe"
```

Both are needed: a **path** exclusion does not cover the extracted `%TEMP%\.net\PWRUHelper\` payload, and a **process** exclusion does not cover the on-execute scan of the image itself.

**What the user is agreeing to, in plain words:** *Windows stops scanning that folder and that program. If anything malicious ever ended up in that folder, Defender would not catch it.* PWRU Helper is open source and can be read or rebuilt by anyone, which is why this is a defensible choice — but it is the user's choice, and the app never makes it for them.

**Hard rules.** Never ship an MSI custom action that adds an exclusion: that is exactly what malware installers do, it needs elevation, and it would poison the app's own reputation signal. Never advise disabling Defender, BAFS or SmartScreen as a fix — those are one-off *diagnostic* toggles, used with consent and restored in the same session (`checklist-nouvelle-machine.md` B4, safety rules).

### 3.4 Where to put the portable exe, and unblocking it

- **Put it in a plain local folder**, e.g. `C:\Tools\PWRU Helper\PWRUHelper.exe`. **Not** a OneDrive-synced Desktop or Downloads: with Files On-Demand an evicted file has to be re-downloaded (all ~180 MB) before it can start, with no visible sign that anything is happening (**F1**; research C8.1–C8.3). If it must live there, right-click → **"Always keep on this device"**. Not a USB stick and not a network drive (**F3**).
- **Unblock the download.** Right-click → Properties → tick **Unblock** at the bottom, or:

  ```powershell
  Unblock-File "C:\Tools\PWRU Helper\PWRUHelper.exe"
  ```

  Removing the Mark-of-the-Web takes the file out of scope for both the SmartScreen application-reputation gate (research A3.5) and Block at First Sight, which is explicitly limited to files "downloaded from the Internet, or that originate from the Internet zone" (A1.5) **[CONFIRMED mechanism]**. It is also the cheapest single-command A/B test available on an affected machine (§7).

---

## 4. Structural: code signing via SignPath Foundation — the action plan

This is the owner's chosen route (`docs/investigations/README.md`, "Owner's decisions" #3: *SignPath Foundation — free, requires an OSI licence → the app stays MIT; PR #49 "CC BY-NC" is to be closed by the owner*). Nine steps, in order. Steps 1–5 are the owner's; steps 6–7 are Phase 4 code work; steps 8–9 are verification.

### Prerequisites — all must hold before step 2

| Requirement | Status |
|---|---|
| **OSI-approved licence, no commercial dual-licensing, no proprietary component** | MIT today (`README.md:230`) — **but open PR #49 proposes CC BY-NC 4.0, which is not OSI-approved and would disqualify the project** (`packaging/signpath-signing.md:23,33`; research A4.10, signpath.org/terms). **[CONFIRMED]** |
| Public repository, actively maintained, at least one published release | Yes — `Kizotis/PWRU-Helper`, v0.14.0 shipped. **[CONFIRMED]** |
| Functionality documented | Yes — README. **[CONFIRMED]** |
| The signing team is the maintaining team that owns the repo | Yes — single maintainer. **[CONFIRMED]** |
| Expected timeline | **"a few days to a few weeks"** (`packaging/signpath-signing.md:40`) — plan in **weeks**, not days. |

### Step 1 — Close PR #49 (owner, before anything else)

Closing PR #49 keeps the licence **MIT**, which is what makes the project eligible. Doing the signing work first and merging PR #49 later would waste all of it and would put the certificate at risk. **This is a gate, not a preference.**

### Step 2 — Apply to SignPath Foundation (owner, one-time)

Apply at **https://about.signpath.io/product/open-source**. Every field is pre-answered in `packaging/signpath-signing.md:26-38` — project name, repository URL, download page, licence (MIT), short description, why signing is needed, build system, artifacts to sign, maintainer contacts. Copy that table straight into the form.

### Step 3 — Wait for approval

Days to weeks (`packaging/signpath-signing.md:40`). Nothing in steps 6–9 is worth starting before approval arrives: the connector URL and the artifact-configuration slugs do not exist until then.

### Step 4 — Create the SignPath project (owner, after approval)

In the SignPath dashboard, create and note (`packaging/signpath-signing.md:44-54`):

1. the **Organization ID**;
2. a **Project** (slug e.g. `pwru-helper`) linked to the GitHub repo;
3. a **Signing policy** (slug e.g. `release-signing` — use *release*, not *test*, for published binaries);
4. **two artifact configurations** — one for the PE (`.exe`), one for the MSI. One file is uploaded per request, so a simple single-file configuration works for each;
5. a **CI user API token**.

### Step 5 — Add one secret and six variables to GitHub (owner)

*Settings → Secrets and variables → Actions* (`packaging/signpath-signing.md:55-66`):

| Kind | Name |
|---|---|
| **Secret** | `SIGNPATH_API_TOKEN` |
| Variable | `SIGNPATH_ORGANIZATION_ID` |
| Variable | `SIGNPATH_PROJECT_SLUG` |
| Variable | `SIGNPATH_POLICY_SLUG` |
| Variable | `SIGNPATH_EXE_ARTIFACT_CONFIG` |
| Variable | `SIGNPATH_MSI_ARTIFACT_CONFIG` |
| Variable | `SIGNPATH_CONNECTOR_URL` |

> **Setting these does not switch signing on.** `.github/workflows/release.yml` contains **zero** references to SignPath today — `grep -i signpath .github/workflows/release.yml` returns nothing (verified 2026-09-06) **[CONFIRMED]**. Until step 6 is applied, a tagged release keeps shipping unsigned no matter what secrets exist (`packaging/signpath-signing.md:69-74`).

### Step 6 — Apply the workflow change (Phase 4)

**What changes in `.github/workflows/release.yml`:**

1. A **job-level `env:` mapping** is added to `jobs.release` (which has none today — `release.yml:16-19`). This is the fix for the trap below.
2. The single step *"Build MSI installer + stage both artifacts"* (`release.yml:49-68`) is **split** into: publish → stage the exe → **sign the exe** → build the MSI **from the signed exe in `dist/`** → **sign the MSI** → publish the release.
3. The publish flags (`release.yml:42-47`) are **not touched** — see the constraint in step 7.
4. The winget step (`release.yml:87-103`) is unchanged and stays last.

**The trap, and the fix.** The drafted block gates every SignPath step with `if: ${{ secrets.SIGNPATH_API_TOKEN != '' }}` (`packaging/signpath-signing.md:106,113,141,148`). **`secrets` is not in GitHub's documented list of contexts available to a step-level `if:`** (`github, needs, strategy, matrix, job, runner, env, vars, steps, inputs`), so that guard may evaluate as empty/false — or error — rather than doing what it looks like it does (`packaging/signpath-signing.md:172-189`) **[CONFIRMED as a documented risk]**. `secrets` **is** available in a job-level `env:`, so the unambiguous form is to map it once and test the mapped value. That is what the block below does, and it is the one substantive correction to the draft.

**Order matters.** Sign the **exe first**, then build the MSI *from that signed file*, then sign the **MSI**. The installer wraps exactly one file (`installer/Product.wxs:40-59` — a single `Component` whose `File` is `KeyPath="yes"`), so if the exe is signed after the MSI is built, the exe **inside** the installer is the unsigned one and every MSI user ends up running an unsigned binary. Today the MSI is built directly from the publish output (`release.yml:53,63`); the block re-points it at `dist/PWRUHelper.exe`, which `Product.wxs` accepts unchanged because the path arrives as `-d ExeFile=` (`installer/Product.wxs:8,42`).

```yaml
jobs:
  release:
    runs-on: windows-latest
    # THE FIX: `secrets` is not a documented context for a step-level `if:`, but it IS
    # available in a job-level `env:`. Map it once here and test the mapped value below.
    env:
      SIGNING_ENABLED: ${{ secrets.SIGNPATH_API_TOKEN != '' }}
    steps:
      # ... checkout / setup-dotnet / dotnet test / WiX install unchanged (release.yml:20-33) ...

      # Publish flags unchanged from release.yml:42-47 — see the PublishFlagsTests constraint (step 7).
      - name: Publish portable single-file exe
        run: >
          dotnet publish PWRUHelper.csproj -c Release -r win-x64 --self-contained true
          -p:PublishSingleFile=true
          -p:IncludeNativeLibrariesForSelfExtract=true
          -p:DebugType=none

      - name: Stage the portable exe
        shell: pwsh
        run: |
          $exe = "bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/PWRUHelper.exe"
          if (-not (Test-Path $exe)) { throw "Published exe not found at $exe" }
          New-Item -ItemType Directory -Force dist | Out-Null
          Copy-Item $exe "dist/PWRUHelper.exe"

      # ---- 1. sign the EXE (dormant until SIGNPATH_API_TOKEN exists) ----
      - name: Upload exe for signing
        id: upload-exe
        if: env.SIGNING_ENABLED == 'true'
        uses: actions/upload-artifact@v4
        with:
          name: unsigned-exe
          path: dist/PWRUHelper.exe

      - name: Sign exe with SignPath
        if: env.SIGNING_ENABLED == 'true'
        uses: SignPath/github-action-submit-signing-request@v2
        with:
          connector-url: ${{ vars.SIGNPATH_CONNECTOR_URL }}
          api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
          organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
          project-slug: ${{ vars.SIGNPATH_PROJECT_SLUG }}
          signing-policy-slug: ${{ vars.SIGNPATH_POLICY_SLUG }}
          artifact-configuration-slug: ${{ vars.SIGNPATH_EXE_ARTIFACT_CONFIG }}
          github-artifact-id: ${{ steps.upload-exe.outputs.artifact-id }}
          wait-for-completion: true
          output-artifact-directory: dist   # overwrites dist/PWRUHelper.exe with the SIGNED one

      # ---- 2. build the MSI around whatever is in dist/ (signed, if signing ran) ----
      - name: Build MSI installer (wraps the exe from dist)
        shell: pwsh
        run: |
          $version = "${{ github.ref_name }}".TrimStart('v')     # "v0.15.0" -> "0.15.0"
          wix build installer/Product.wxs -ext WixToolset.UI.wixext -arch x64 `
            -d Version="$version.0" `
            -d ExeFile="dist/PWRUHelper.exe" `
            -d IconFile="assets/icon.ico" `
            -d LicenseFile="installer/license.rtf" `
            -o "dist/PWRUHelper-$version-setup.msi"
          Get-ChildItem dist

      # ---- 3. sign the MSI ----
      - name: Upload msi for signing
        id: upload-msi
        if: env.SIGNING_ENABLED == 'true'
        uses: actions/upload-artifact@v4
        with:
          name: unsigned-msi
          path: dist/PWRUHelper-*-setup.msi

      - name: Sign msi with SignPath
        if: env.SIGNING_ENABLED == 'true'
        uses: SignPath/github-action-submit-signing-request@v2
        with:
          connector-url: ${{ vars.SIGNPATH_CONNECTOR_URL }}
          api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
          organization-id: ${{ vars.SIGNPATH_ORGANIZATION_ID }}
          project-slug: ${{ vars.SIGNPATH_PROJECT_SLUG }}
          signing-policy-slug: ${{ vars.SIGNPATH_POLICY_SLUG }}
          artifact-configuration-slug: ${{ vars.SIGNPATH_MSI_ARTIFACT_CONFIG }}
          github-artifact-id: ${{ steps.upload-msi.outputs.artifact-id }}
          wait-for-completion: true
          output-artifact-directory: dist   # overwrites the msi with the SIGNED one

      - name: Publish GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          name: PWRU Helper ${{ github.ref_name }}
          files: |
            dist/PWRUHelper.exe
            dist/PWRUHelper-*-setup.msi
          generate_release_notes: true

      # ... the winget step (release.yml:87-103) is unchanged and stays last ...
```

### Step 7 — Keep `packaging/signpath-signing.md` in sync, in the same PR

`tests/PWRUHelper.Tests/PublishFlagsTests.cs` fails the build if the publish flags disagree across files. Three assertions bear on this change:

- `All_three_build_paths_publish_with_the_same_flags` (`PublishFlagsTests.cs:62-70`) compares the `-p:` flags of `Build Portable EXE.bat` (`:20-23`), `Build MSI Installer.bat` (`:29-32`) and `.github/workflows/release.yml` (`:42-47`). **The signing change must not add, remove or alter a single `-p:` flag.** The block above does not.
- `The_drafted_signing_workflow_would_not_change_the_shipped_build` (`PublishFlagsTests.cs:72-79`) compares `release.yml`'s flags with **`packaging/signpath-signing.md`'s**. So when Phase 4 edits the workflow, it must edit that markdown in the same PR — otherwise the test goes red on a docs mismatch.
- `The_single_file_bundle_is_never_compressed` (`PublishFlagsTests.cs:44-60`) additionally scans `packaging/signpath-signing.md`. **Never write `-p:EnableCompressionInSingleFile` in that file, even as an example** — the flag costs ~110–118 MB of working set and was removed on purpose in v0.14.0.

Note that `BuildFiles` (`PublishFlagsTests.cs:37-42`) does **not** include `docs/investigations/**`, so the YAML block above is not scanned by the suite today. It becomes scanned the moment someone copies it into `packaging/`.

### Step 8 — Treat the first signed tag as a test

After wiring, tag a release and **confirm both artifacts were produced before assuming the guards behaved** (`packaging/signpath-signing.md:191-192`):

```powershell
gh release view v<x.y.z> --json name,assets
```

Both `PWRUHelper.exe` and `PWRUHelper-<version>-setup.msi` must be attached — the same check the release checklist already mandates (`project-context.md`, release checklist step 5).

### Step 9 — Verify on a real, downloaded copy

On a fresh machine, **download the release from GitHub** — do not use a local build. A locally built file carries no Mark-of-the-Web, which is exactly why the dev box could not exercise SmartScreen at all (`mesures-resultats-dev-box.md` §4.4).

1. **Signature present and valid:**

   ```powershell
   Get-AuthenticodeSignature "C:\Tools\PWRU Helper\PWRUHelper.exe" | Format-List Status,SignerCertificate,TimeStamperCertificate
   Get-AuthenticodeSignature "$env:USERPROFILE\Downloads\PWRUHelper-<version>-setup.msi" | Format-List Status,SignerCertificate
   ```

   Expected: `Status = Valid`, subject **SignPath Foundation**. Then check the exe *inside* an MSI install as well — `Get-AuthenticodeSignature "C:\Program Files\PWRU Helper\PWRUHelper.exe"`. That is what proves the sign-then-build order actually worked.
2. **SmartScreen behaviour on a fresh download:** does the blue "Windows protected your PC" screen still appear? Record yes/no and the exact wording. A signed-but-low-reputation file still shows it (A4.4).
3. **Defender pre-process time on the downloaded copy:**

   ```powershell
   powershell -ExecutionPolicy Bypass -File "<repo>\tools\diagnostics\Measure-Startup.ps1" -ExePath "C:\Tools\PWRU Helper\PWRUHelper.exe" -Runs 3 -Note "signed, downloaded, first launch"
   ```

   Compare `pre_process_ms` against the unsigned baseline captured in §7.1.

### What signing does **not** fix — plainly

- **It does not guarantee the first-hash cloud check goes away.** No first-party source says a signature short-circuits Block at First Sight. What *is* documented is that signer information (`AuthentiCodeHash`, `Issuer`, `Publisher`, `Signer`, `SignerHash`) is part of the metadata sent to the cloud protection service — an **input to** the verdict, not a bypass of it (research A4.5) **[CONFIRMED for the metadata contents, ASSUMED for the latency effect]**. Expect the first launch of a new release to still cost something until publisher reputation has accumulated.
- **Reputation takes weeks.** "It can take several weeks and hundreds of clean installs from a wide audience" before a hash has enough history (A4.4, MS Learn 2026-05-04) **[CONFIRMED]**. The benefit is that from then on reputation **carries across releases** instead of resetting at every tag — which is the actual pathology.
- **EV would not help.** EV certificates stopped granting instant SmartScreen reputation; the EV OIDs were removed from the trusted roots in August 2024 and all code-signing certificates are now treated equally (A4.1–A4.2) **[CONFIRMED]**. There is no paid upgrade that shortcuts this.
- **It does nothing for D1** — the local real-time scan of a 178 MB image. Signing does not make a big file cheaper to read.
- **The publisher shown is "SignPath Foundation", not "Kizotis"** (`packaging/signpath-signing.md:10-13`). That is a product decision as much as a technical one.
- **The only route that removes the warning outright is the Microsoft Store** — Store apps are re-signed by Microsoft and never show SmartScreen (A4.11). Not proposed; it is a different distribution model.

---

## 5. Optional / low-value — explicitly marked

### 5.1 Ship the 5 WPF native DLLs beside the exe, MSI only — **measured ~free, not recommended now**

Dropping `IncludeNativeLibrariesForSelfExtract=true` from the **MSI** publish only would put the native DLLs into `Program Files` at install time, so they are never extracted and never re-extracted. The measured payload is **5 files, 8.2 MB** — `D3DCompiler_47_cor3.dll` (4.74 MB), `wpfgfx_cor3.dll` (1.96 MB), `PresentationNative_cor3.dll` (1.24 MB), `PenImc_cor3.dll` (158 KB), `vcruntime140_cor3.dll` (125 KB) — **not** the 18 DLLs / 24.4 MB the research first estimated from the pre-bundle publish tree, which is corrected in place (`recherche-environnement-et-profiling.md` B5.7; `mesures-resultats-dev-box.md` §2, §4.3) **[MEASURED]**.

**Why it stays optional.** Clearing that cache before a run changed `in_process_ms` from a 1525–1793 ms warm band to 1627/2211 ms — inside the noise. The mechanism is confirmed; the cost is not, at least on an SSD. Against it: `installer/Product.wxs` installs exactly one file with `KeyPath="yes"` (`Product.wxs:40-59`) and would need a component per DLL, tracked across .NET servicing updates, with `MajorUpgrade` orphan risk; and `PublishFlagsTests.cs:62-70` currently **enforces** flag parity across the three build paths, so this change means deliberately weakening that test to "portable and MSI may differ **only** in this flag". **It must never be applied to the portable build** — "one file, put it anywhere" is that artefact's entire product promise. Revisit only if a slow-storage machine measures a material extraction cost.

### 5.2 `%LOCALAPPDATA%` instead of Roaming — **no-op on personal machines**

The code path is real — `Services/Logging.cs:18-20` builds the log directory under `Environment.SpecialFolder.ApplicationData` (Roaming) — but the affected machines are personal and non-domain, and **OneDrive Known Folder Move covers Desktop / Documents / Pictures, not `%APPDATA%`**. On the dev box `%APPDATA%` measured as a plain local `Directory` with no reparse point (`mesures-resultats-dev-box.md` §2, §4.5) **[MEASURED]**. Moving `settings.json` would need a migration for existing users, inside the machinery this project has already been bitten by. **Do not do it unless F2 is actually observed on an affected machine.**

### 5.3 Move the first log write off the UI thread — **~ms**

`App.xaml.cs:32` writes the session marker synchronously on the UI thread before `MainWindow` exists, through a writer that does `Directory.CreateDirectory` + a roll check + `File.AppendAllText` per line (`Services/Logging.cs:81-87`). Expected cost: single-digit ms; only a redirected `%APPDATA%` (F2, unlikely here) could inflate it. Against it: `Logging`'s contract is "never throws, never blocks the app" (`Services/Logging.cs:89`) and it is what the About tab's error report reads back — a marker that has not been flushed when the app crashes is a marker missing from the report. **Only worth doing if measured.**

---

## 6. NOT proposed — rejected by the owner, or measured useless

Listed so nobody re-raises them (per `pwru-open-threads`: do not re-raise settled findings as new).

| Item | Why not |
|---|---|
| **`PublishReadyToRun`** | Measured **worse** (cold 6.4–10.7 s vs 3.9–8.9 s) and banned. **[REPORTED]** |
| **`EnableCompressionInSingleFile`** | Removed on purpose in v0.14.0: it cost ~110–118 MB of working set next to the game, which is the actual product requirement. Guarded by `tests/PWRUHelper.Tests/PublishFlagsTests.cs:44-60` and explained in all three build files (`release.yml:35-41`, `Build Portable EXE.bat:11-19`, `Build MSI Installer.bat:23-27`). **[CONFIRMED]** |
| **Trimming / NativeAOT** | Impossible with WPF — `error NETSDK1168`, verified not assumed. **[REPORTED]** |
| **Native rewrite (C/C++/Python)** | Capture is native GDI, OCR is native WinRT, translation is network; the only managed CPU in the hot path is ~0.4 % of a core. A rewrite buys memory and binary size, not startup time, at the price of ~5,900 lines and ~256 tests. **[REPORTED]** |
| **A splash screen** | **A splash cannot help, and the mechanism is why: it appears after the pre-process wait.** The delay is *pre-process* — 2163–2528 ms elapse before the process exists at all (`mesures-resultats-dev-box.md` §3, runs #0–#1) **[MEASURED]** — and Defender's BAFS hold is documented as potentially synchronous: "the file doesn't open until the cloud renders a verdict" (research A1.2) **[CONFIRMED]**. There is no process with which to draw anything. Even WPF's native `SplashScreen` (a PNG marked `<SplashScreen>`, painted by native code before any XAML) starts only *after* the loader has been allowed to map the image, i.e. after exactly the wait the user is complaining about. A managed splash `Window` is worse: it starts after the runtime and after WPF, so it would cover only the tail of a ~1.5 s in-process floor that has no variance in it anyway (`mesures-resultats-dev-box.md` §4.2). And a splash on an app that then takes another 6 s reads as *broken*, not as *loading*. |
| **MVVM / view-models / binding frameworks** | Deliberate architectural decision (`project-context.md`). Irrelevant to startup. |
| **Splitting `MainWindow.xaml` to shrink `InitializeComponent`** | The in-process half is a flat floor with no variance; shaving ~200 ms off a ~1.5 s constant does not touch a 6–10 s complaint. **[MEASURED]** |
| **Deferring `Load*` / `BuildSquadTab` / `ApplySettings`** | Measured at 5–17 ms each. Nothing to win. **[REPORTED]** |
| **Fewer releases, to reduce hash churn** | Optimising the wrong variable: it slows the product for every user to spare some users a one-off cost, and it does not remove that cost — it makes each occurrence rarer and *larger*. The answer to hash churn is signing (§4). |
| **Turning off Defender, BAFS or SmartScreen as a "fix"** | Never. Those are diagnostic toggles: once, with consent, restored in the same session. |
| **Azure Artifact Signing ($9.99/month)** | Not chosen — the owner's decision is SignPath Foundation (free). Recorded only so the alternative is not re-proposed. |

---

## 7. Validation plan — what numbers, on which machine, prove this worked

**The machine that counts is a Defender-default, personal, non-corporate Windows machine, running a copy of the exe that was actually downloaded from GitHub** (so it carries Mark-of-the-Web). A locally built file cannot exercise SmartScreen or BAFS at all — which is precisely why the dev box numbers are a floor and not a reproduction (`mesures-resultats-dev-box.md` §4.4, §5) **[MEASURED]**.

### 7.1 Before — baseline on the current unsigned v0.14.0

On ≥ 2 affected machines **and** ≥ 1 machine that starts *fast* (a fast machine is not a control unless it is documented the same way):

```powershell
powershell -ExecutionPolicy Bypass -File "<repo>\tools\diagnostics\Get-MachineSheet.ps1" -ExePath "C:\Tools\PWRU Helper\PWRUHelper.exe"
powershell -ExecutionPolicy Bypass -File "<repo>\tools\diagnostics\Measure-Startup.ps1"  -ExePath "C:\Tools\PWRU Helper\PWRUHelper.exe" -Runs 3 -Note "unsigned baseline, fresh download, first launch"
```

Run `Measure-Startup.ps1` **before** the user has opened the new version manually — the first launch of a new download is the whole point. Then repeat with `-LaunchMode Direct`, and again after `Unblock-File`.

### 7.2 After — the same machines, on the first signed release

Identical commands with `-Note "signed release <tag>, fresh download, first launch"`, plus the `Get-AuthenticodeSignature` checks of §4 step 9. Because reputation accrues over weeks (A4.4), **re-run once about four weeks after the first signed release** — a single post-release measurement will understate the benefit.

### 7.3 The machine-sheet fields to diff, before vs after

`DisableBlockAtFirstSeen` · `CloudBlockLevel` · `CloudExtendedTimeout` · `MAPSReporting` · `SubmitSamplesConsent` · `RealTimeProtectionEnabled` · `AntivirusSignatureVersion` (a signature update between runs invalidates Defender's per-file verdict cache — hypothesis **D1** — so a changed value invalidates a cold/warm comparison) · `VerifiedAndReputablePolicyState` (Smart App Control) · the third-party AV list from `root\SecurityCenter2` (must read *Defender only*) · **Mark-of-the-Web present** (`Zone.Identifier`) · exe full path, `Length`, `Attributes` (`Offline` / `ReparsePoint` / `SparseFile`) · whether the path is under `$env:OneDrive` · `%TEMP%\.net\PWRUHelper` file count and total size, captured *before* the timed launch · install kind (portable vs MSI) · OS build · **`Get-AuthenticodeSignature` status** (the after-run must read `Valid`).

### 7.4 Success thresholds (proposed)

| Metric | Today | Success |
|---|---|---|
| **`pre_process_ms`, first launch of a freshly downloaded release** — median of ≥ 3 runs, on ≥ 2 affected machines | [UNKNOWN] on those machines; **2163–2528 ms** on the dev box for a fresh hash **[MEASURED]** | **≤ 500 ms** |
| **`total_ms`, first launch of a freshly downloaded release** | reported 6–10 s | **≤ 3.0 s** |
| **`total_ms`, warm (second launch, same build)** | 1.54–1.82 s on the dev box **[MEASURED]** | **≤ 2.0 s** — i.e. no regression |
| **`in_process_ms`** | flat 1477–2256 ms **[MEASURED]** | unchanged — this is the .NET/WPF floor; a *rise* here would mean the packaging change broke something |
| SmartScreen on a fresh download | blue "Windows protected your PC" screen | absent — or acknowledged as still present but expected, during the reputation ramp |

**Why 500 ms and not zero:** the pre-process cost of a file the security stack already knows measured **15–42 ms** (`mesures-resultats-dev-box.md` §3, runs #2–#8). 500 ms leaves room for a cloud round trip on a domestic uplink while still sitting an order of magnitude below the complaint.

**What would falsify the whole line of reasoning:** an affected machine that is **equally slow offline**, **equally slow after `Unblock-File`**, and **equally slow when installed by MSI**. Any one of those three would clear D2/S1 and hand the case to D1 (local scan of the 178 MB image) or F1 (OneDrive hydration) — and signing would then be a far smaller win than assumed. Run those three experiments — two single commands and one install — **before** committing weeks to §4.

---

## 8. Open items

| # | Item | Owner | Blocks |
|---|---|---|---|
| 1 | **No affected machine has ever been measured.** Every number here comes from a dev box the measurement report itself calls unrepresentative. | Owner + a volunteer | Ranks 1–6 are all conditioned on it |
| 2 | **PR #49 (CC BY-NC) must be closed** before any SignPath work starts. | Owner | §4 entirely |
| 3 | ~~Sally's degraded-mode copy does not exist yet.~~ **Closed** — `docs/investigations/02-traduction/ux-mode-degrade.md` §3.8 landed during Phase 2 and its wording is now used verbatim in §3.1 and in `checklist-nouvelle-machine.md` §A4. Her third placement (a one-time in-app toast) is a code change and needs sizing with the Phase 2 UX work. | Sally / Winston | Nothing here; toast sizing only |
| 4 | **[UNKNOWN]** whether a signature measurably shortens the BAFS hold, or only the reputation ramp (A4.5 is `ASSUMED` on the latency effect). The four-week re-measurement of §7.2 is what answers it. | — | Sizing the §4 benefit |
| 5 | The MSI-vs-portable prediction (A1.5: no MOTW ⇒ no BAFS gate) is **untested**. It is the cheapest high-value experiment left, and it decides whether rank 3 is a real recommendation or a guess. | Owner | Rank 3 |
| 6 | Whether "6–10 s" is a *some machines* defect or the **universal cold cost of a fresh hash** (`hypotheses-matrice.md` §0). If it is universal, §3.1 becomes the main deliverable and the framing of P1 changes. | — | Framing |
| 7 | Housekeeping noticed during the investigation, unrelated to P1: `project-context.md` still says 142 tests (the suite has ~256 cases), and `Data/slang.json` has no `"version"` key so its editable copy is never refreshed. | Owner | Nothing |

---

_Companion documents: `hypotheses-matrice.md` (hypothesis IDs) · `recherche-environnement-et-profiling.md` (sources) · `mesures-resultats-dev-box.md` (the numbers) · `mesures-protocole.md` + `tools/diagnostics/` (how to reproduce) · `checklist-nouvelle-machine.md` (what to hand a user)._
