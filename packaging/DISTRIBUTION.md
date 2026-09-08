# Distribution notes — reducing SmartScreen friction

The `.exe` and `.msi` aren't code-signed, so Windows SmartScreen shows an
"unknown publisher" warning and some antivirus engines may flag the fresh binary.
None of this is a security problem, but it scares non-technical players. Here are
the realistic, mostly-free ways to reduce it, cheapest first. **These need a one-time
action from you (accounts/setup); they can't be done from the code alone.**

## 1. Code signing via SignPath (free for open source) — best value
PWRU Helper is public on GitHub, so it's eligible for SignPath's free OSS plan, which
signs your binaries with a trusted certificate (kills most of the SmartScreen warning
over time).

**→ Full walkthrough (ready-to-paste application answers + the exact, already-drafted
release-workflow signing steps) is in [`signpath-signing.md`](signpath-signing.md).**

Short version:
1. Apply for the OSS plan: https://about.signpath.io/product/open-source
2. Connect the `Kizotis/PWRU-Helper` repo and define a signing policy.
3. Add the SignPath secrets/variables — the drafted workflow steps activate automatically
   and publish the **signed** exe + `.msi` to the GitHub release.

> A **self-signed** certificate does NOT help against SmartScreen — don't bother with one.

## 2. Publish the SHA-256 of every artifact (zero cost, do this now)
Even unsigned, listing the hash lets careful users verify the download. Add the output
of this to each release's notes:

```powershell
Get-FileHash .\PWRUHelper.exe, .\PWRUHelper-0.15.2-setup.msi -Algorithm SHA256 |
  Format-Table Hash, Path -AutoSize
```

## 3. Submit to winget (free) — a trusted install channel
`winget install Kizotis.PWRUHelper` is a clean path for technical players. The MSI
(perMachine, stable `UpgradeCode`, MIT-licensed) is a good fit.

> ### ⚠️ PWRU Helper has NEVER been submitted to winget-pkgs
> `Kizotis.PWRUHelper` does not exist in the `microsoft/winget-pkgs` repository — no
> version of it has ever been published. Everything below that says *update* only works
> on a package winget already knows about, so **the very first submission must be done
> by hand with `wingetcreate new`.** That includes the "Submit to winget" release-workflow
> step: it runs `wingetcreate update` and will fail until the package exists.

**Step 1 — the one-time first submission (manual, by you):**

```powershell
winget install Microsoft.WingetCreate
wingetcreate new "https://github.com/Kizotis/PWRU-Helper/releases/download/v0.15.2/PWRUHelper-0.15.2-setup.msi"
# It prompts for the metadata, then opens the PR to microsoft/winget-pkgs.
```

The hand-written manifests in `packaging/winget/` are kept current (they match v0.15.2)
and can be pasted straight into `wingetcreate new`'s prompts — or submitted as-is by
forking `microsoft/winget-pkgs` and dropping them in
`manifests/k/Kizotis/PWRUHelper/0.15.2/`. Check them first with:

```powershell
winget validate --manifest packaging\winget
```

**Step 2 — every release after that** is a plain `update`, which fills in the SHA-256
and the MSI ProductCode for you from the release URL:

```powershell
wingetcreate update Kizotis.PWRUHelper `
  --version 0.15.2 `
  --urls "https://github.com/Kizotis/PWRU-Helper/releases/download/v0.15.2/PWRUHelper-0.15.2-setup.msi" `
  --submit    # opens a PR to microsoft/winget-pkgs
```

### Automated on every release
The Release workflow has a **"Submit to winget"** step that runs `wingetcreate update … --submit`
for each tagged release. It's **opt-in and safe**:

1. Create a GitHub **classic PAT** with `public_repo` scope (it needs to fork/PR
   `microsoft/winget-pkgs` on your behalf).
2. Add it as the repository secret **`WINGET_TOKEN`** (Settings → Secrets → Actions).

With the secret set, tagging `vX.Y.Z` publishes the release **and** opens the winget PR
automatically. Without it, the step just prints these instructions and the release still
succeeds. **This automation cannot bootstrap the package**: until the manual
`wingetcreate new` submission of Step 1 has been merged into `microsoft/winget-pkgs`,
the step has nothing to update and will fail.

The manifests in `packaging/winget/` are hand-maintained and currently pinned to **0.15.2**
(the values were read from the real released MSI). Two fields are version-specific and must
be refreshed for every new version:

- `InstallerSha256` — `Get-FileHash <msi> -Algorithm SHA256`
- `ProductCode` — WiX regenerates it on **every** build (the `<Package>` element has no fixed
  `ProductCode`), so it must be read from the actual released `.msi`, never guessed.

The `UpgradeCode` `{B7D1F3A2-6E54-4C9B-8A1D-2F0C7E5A9B34}` is the opposite: fixed forever in
`installer/Product.wxs`, so it stays the same in every manifest.
`wingetcreate update` refreshes both version-specific fields for you.

To read the ProductCode of a built MSI yourself:

```powershell
$i = New-Object -ComObject WindowsInstaller.Installer
$db = $i.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$i,@('PWRUHelper-0.15.2-setup.msi',0))
$v  = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@("SELECT Value FROM Property WHERE Property='ProductCode'"))
$v.GetType().InvokeMember('Execute','InvokeMethod',$null,$v,$null)
$r = $v.GetType().InvokeMember('Fetch','InvokeMethod',$null,$v,$null)
$r.GetType().InvokeMember('StringData','GetProperty',$null,$r,@(1))
```
