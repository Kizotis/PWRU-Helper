# Free code signing via SignPath Foundation — application + CI wiring

PWRU Helper's `.exe`/`.msi` are unsigned, so Windows SmartScreen shows an "unknown
publisher" warning. The only **genuinely free** way to fix this is the
[SignPath Foundation](https://signpath.org) programme, which signs open-source projects
for free. This file is everything needed to get it running; it's split into the
**one-time action only Kizotis can do** (apply + set up the account) and the
**CI wiring** (already drafted below — activates itself once the secrets exist).

> Trade-offs to accept up front:
> - The certificate is issued to **"SignPath Foundation"**, so Windows will show
>   *publisher: SignPath Foundation* — not "Kizotis" / "PWRU Helper".
> - It's an **OV** cert, so SmartScreen quietens **gradually** as downloads build
>   reputation — not instantly (only paid EV certs give instant reputation).
> - Azure Trusted/Artifact Signing is **not** free ($9.99/mo, US/Canada individuals
>   only) and a self-signed cert does nothing for SmartScreen. SignPath is the path.

---

## Step 1 — Apply (Kizotis, one-time)

Apply at **https://about.signpath.io/product/open-source** (or https://signpath.org).
Eligibility is met: OSI license (**MIT**), public repo, actively maintained, already
released, functionality documented in the README.

Ready-to-paste answers:

| Field | Answer |
|---|---|
| Project name | PWRU Helper |
| Repository | https://github.com/Kizotis/PWRU-Helper |
| Website / download page | https://github.com/Kizotis/PWRU-Helper/releases |
| License | MIT |
| Short description | A free Windows helper for English/French speakers playing on the Perfect World Russia server (pwonline.ru): in-game phrasebook, on-device Russian OCR with live screen translation, and a translator — packaged as a portable `.exe` and a WiX `.msi`. |
| Why signing is needed | The unsigned installer trips Windows SmartScreen's "unknown publisher" warning, which scares non-technical players away from a legitimate free tool. |
| Build system | GitHub Actions (`.github/workflows/release.yml`), triggered by `v*` tags. |
| Artifacts to sign | `PWRUHelper.exe` (portable single-file) and `PWRUHelper-<version>-setup.msi`. |
| Maintainer | Kizotis — github.com/Kizotis · twitch.tv/kizotis · discord `kizotis` |

Approval typically takes a few days to a few weeks.

## Step 2 — Set up the SignPath project (Kizotis, after approval)

In the SignPath dashboard you'll create/note four values and one token:

1. **Organization ID** — shown in the org settings.
2. **Project** — create one (e.g. slug `pwru-helper`) linked to the GitHub repo.
3. **Signing policy** — e.g. slug `release-signing` (the Foundation "test-signing" vs
   "release-signing" distinction; use release for published binaries).
4. **Artifact configuration** — describes what's inside the uploaded artifact so
   SignPath knows which files to Authenticode-sign. We upload one file per request
   (the `.exe`, then the `.msi`), so a simple single-file PE/MSI config works.
5. **API token** — generate a CI user API token.

Then add these to the GitHub repo (**Settings → Secrets and variables → Actions**):

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | the CI user API token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | your org ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | `pwru-helper` |
| Variable | `SIGNPATH_POLICY_SLUG` | `release-signing` |
| Variable | `SIGNPATH_EXE_ARTIFACT_CONFIG` | artifact-config slug for the exe |
| Variable | `SIGNPATH_MSI_ARTIFACT_CONFIG` | artifact-config slug for the msi |
| Variable | `SIGNPATH_CONNECTOR_URL` | the GitHub connector URL from SignPath's dashboard |

That's the whole one-time part **on the SignPath side**.

> ⚠️ **Setting these secrets alone does NOT switch signing on.** The signing steps are
> only *drafted* in Step 3 below — `.github/workflows/release.yml` does not contain a
> single reference to SignPath today (`grep -i signpath .github/workflows/release.yml`
> returns nothing). Until that block is actually applied to the workflow, a tagged
> release keeps shipping unsigned no matter what secrets exist. Step 3 is a real
> to-do, not a description of the current pipeline.

## Step 3 — CI wiring (already drafted; apply when secrets exist)

Replace the steps in `.github/workflows/release.yml` from *"Publish portable single-file
exe"* onward with the block below. Key idea: the MSI is always built from
`dist/PWRUHelper.exe`, and when signing is enabled that file has already been replaced
by its **signed** copy — so the installer wraps a signed exe, and the MSI itself is
signed afterwards. Every SignPath step is gated on the token existing.

```yaml
      # No -p:EnableCompressionInSingleFile — see the comment in release.yml. These flags must stay
      # identical to the ones live in release.yml, or applying this block would quietly change the
      # shipped build (compressing it again costs ~110 MB of RAM at runtime).
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

      # ---- sign the exe (dormant until SIGNPATH_API_TOKEN is set) ----
      - name: Upload exe for signing
        id: upload-exe
        if: ${{ secrets.SIGNPATH_API_TOKEN != '' }}
        uses: actions/upload-artifact@v4
        with:
          name: unsigned-exe
          path: dist/PWRUHelper.exe

      - name: Sign exe with SignPath
        if: ${{ secrets.SIGNPATH_API_TOKEN != '' }}
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
          output-artifact-directory: dist          # overwrites dist/PWRUHelper.exe with the signed one

      - name: Build MSI installer (wraps the exe from dist, signed if signing ran)
        shell: pwsh
        run: |
          $version = "${{ github.ref_name }}".TrimStart('v')
          wix build installer/Product.wxs -ext WixToolset.UI.wixext -arch x64 `
            -d Version="$version.0" `
            -d ExeFile="dist/PWRUHelper.exe" `
            -d IconFile="assets/icon.ico" `
            -d LicenseFile="installer/license.rtf" `
            -o "dist/PWRUHelper-$version-setup.msi"
          Get-ChildItem dist

      # ---- sign the msi (dormant until SIGNPATH_API_TOKEN is set) ----
      - name: Upload msi for signing
        id: upload-msi
        if: ${{ secrets.SIGNPATH_API_TOKEN != '' }}
        uses: actions/upload-artifact@v4
        with:
          name: unsigned-msi
          path: dist/PWRUHelper-*-setup.msi

      - name: Sign msi with SignPath
        if: ${{ secrets.SIGNPATH_API_TOKEN != '' }}
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
          output-artifact-directory: dist          # overwrites the msi with the signed one

      - name: Publish GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          name: PWRU Helper ${{ github.ref_name }}
          files: |
            dist/PWRUHelper.exe
            dist/PWRUHelper-*-setup.msi
          generate_release_notes: true
```

Notes:
- ⚠️ **Verify the `if:` guards before trusting them.** The `secrets` context is *not* in
  GitHub's documented list of contexts available to a step-level `if:`
  (`github, needs, strategy, matrix, job, runner, env, vars, steps, inputs`), so
  `if: ${{ secrets.SIGNPATH_API_TOKEN != '' }}` may evaluate as empty/false — or error —
  rather than doing what it looks like it does. `secrets` *is* available in a job-level
  `env:`, so the unambiguous form is to map it once and test the mapped value:

  ```yaml
  jobs:
    release:
      runs-on: windows-latest
      env:
        SIGNING_ENABLED: ${{ secrets.SIGNPATH_API_TOKEN != '' }}
      steps:
        - name: Sign exe with SignPath
          if: env.SIGNING_ENABLED == 'true'
          ...
  ```

  Either way, the FIRST tag after wiring this up should be treated as a test: confirm the
  release still produced both artifacts before assuming the guards behaved.
- The exact `connector-url` and the artifact-configuration slugs come from the SignPath
  dashboard once the project is created; they're wired as repo variables above so no
  secret ever appears in the YAML.
- Action pinned to `SignPath/github-action-submit-signing-request@v2` (latest, 2025-10).
- First signed release: after tagging, check `gh release view <tag>` and confirm the
  downloaded `.exe`/`.msi` show *Digital Signatures → SignPath Foundation* in their
  Windows file Properties.
