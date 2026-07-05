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

1. Apply for the OSS plan: https://about.signpath.io/product/open-source
2. Connect the `Kizotis/PWRU-Helper` repo and define a signing policy.
3. In your release build, upload `PWRUHelper.exe` and the `.msi` to SignPath (they
   have a GitHub Action) and publish the **signed** artifacts to the GitHub release.

> A **self-signed** certificate does NOT help against SmartScreen — don't bother with one.

## 2. Publish the SHA-256 of every artifact (zero cost, do this now)
Even unsigned, listing the hash lets careful users verify the download. Add the output
of this to each release's notes:

```powershell
Get-FileHash .\PWRUHelper.exe, .\PWRUHelper-0.7.0-setup.msi -Algorithm SHA256 |
  Format-Table Hash, Path -AutoSize
```

## 3. Submit to winget (free) — a trusted install channel
`winget install Kizotis.PWRUHelper` is a clean path for technical players. The MSI
(perMachine, stable `UpgradeCode`) is a good fit.

Easiest is **wingetcreate**, which fills in the SHA-256 and MSI ProductCode for you
from the release URL:

```powershell
winget install Microsoft.WingetCreate
wingetcreate update Kizotis.PWRUHelper `
  --version 0.7.0 `
  --urls "https://github.com/Kizotis/PWRU-Helper/releases/download/v0.7.0/PWRUHelper-0.7.0-setup.msi" `
  --submit    # opens a PR to microsoft/winget-pkgs
```

For the very first submission use `wingetcreate new` instead of `update`. Hand-authored
manifest templates are in `packaging/winget/` if you prefer to edit them directly — but
note the MSI `ProductCode` changes on every WiX build (`Product Id="*"`), so let
wingetcreate read it from the actual released file rather than hard-coding it.

To read the ProductCode of a built MSI yourself:

```powershell
$i = New-Object -ComObject WindowsInstaller.Installer
$db = $i.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$i,@('PWRUHelper-0.7.0-setup.msi',0))
$v  = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@("SELECT Value FROM Property WHERE Property='ProductCode'"))
$v.GetType().InvokeMember('Execute','InvokeMethod',$null,$v,$null)
$r = $v.GetType().InvokeMember('Fetch','InvokeMethod',$null,$v,$null)
$r.GetType().InvokeMember('StringData','GetProperty',$null,$r,@(1))
```
