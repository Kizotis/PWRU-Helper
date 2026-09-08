# Free code signing via SignPath Foundation — application + CI wiring

PWRU Helper's `.exe`/`.msi` are unsigned, so Windows SmartScreen shows an "unknown
publisher" warning. The only **genuinely free** way to fix this is the
[SignPath Foundation](https://signpath.org) programme, which signs open-source projects
for free. This file is everything needed to get it running; it's split into the
**one-time action only Kizotis can do** (apply + set up the account) and the
**CI wiring** (already applied to `release.yml` — dormant, and switches itself on the
moment the secrets exist).

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

That's the whole one-time part. **Setting `SIGNPATH_API_TOKEN` is also the switch that
turns signing on** — the CI wiring is already in the workflow (Step 3). The six `vars.*`
values above are what the signing steps read once they start running, so set all of them
in the same sitting; a token with missing variables produces a failing signing request,
not an unsigned release.

## Step 3 — CI wiring (APPLIED — live but dormant)

`.github/workflows/release.yml` already contains the SignPath steps. Nothing further
needs editing: with no `SIGNPATH_API_TOKEN` secret they all skip, and a tagged release
ships exactly the unsigned `PWRUHelper.exe` + `PWRUHelper-<version>-setup.msi` it always
has. Set the secret (plus the variables above) and the same workflow starts signing.

How it's wired, in order:

1. **Publish portable single-file exe** — unchanged publish flags (notably *no*
   `-p:EnableCompressionInSingleFile`; see the comment above that step, it costs ~110 MB
   of RAM at runtime).
2. **Stage the portable exe** into `dist/PWRUHelper.exe`, so one path is both what gets
   signed and what the installer wraps.
3. **Upload exe for signing** → **Sign exe with SignPath** — writes the signed exe back
   over `dist/PWRUHelper.exe`. *(guarded)*
4. **Build MSI installer** from `dist/PWRUHelper.exe` — so the MSI wraps the signed exe
   when signing ran, and the same build as the portable download either way.
5. **Upload msi for signing** → **Sign msi with SignPath** — overwrites the MSI in
   `dist/`. *(guarded)*
6. **Publish GitHub Release** with both files from `dist/`, then the existing opt-in
   winget step.

**The guard.** `secrets` is not one of the contexts GitHub makes available to a
step-level `if:`, so `if: ${{ secrets.SIGNPATH_API_TOKEN != '' }}` on a step is not
trustworthy. The token is mapped once at **job** level (where `secrets` *is* available)
and each SignPath step tests the mapped string:

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

Notes:
- **Treat the FIRST tag after setting the secret as a test.** Confirm the release still
  produced both artifacts before assuming the signing requests behaved — and confirm the
  downloaded `.exe`/`.msi` show *Digital Signatures → SignPath Foundation* in their
  Windows file Properties (`gh release view <tag> --json name,assets` for the artifacts).
- The `connector-url` and artifact-configuration slugs come from the SignPath dashboard;
  they're repo **variables**, so no secret value ever appears in the YAML.
- Action pinned to `SignPath/github-action-submit-signing-request@v2`.
