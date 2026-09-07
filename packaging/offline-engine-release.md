# Publishing the `offline-engine-v1` release (owner task)

The optional offline engine is **downloaded on request**, never bundled. The app ships a table of
the exact file names, sizes and SHA-256 digests it will accept — `OfflineModelManifest.Shipping` in
[`Services/OfflineModelManifest.cs`](../Services/OfflineModelManifest.cs) — and refuses anything
else. Four of those digests are still `TODO-owner`, so **today the feature is inert**: the About row
says "not installed", a download attempt fails at verification before a single request, and the
native library will not load.

This guide is the one-time task that lifts that: create a GitHub release called **`offline-engine-v1`**
on `Kizotis/PWRU-Helper`, upload six files to it, and paste four sizes and four digests back into the
manifest. **It needs you** — it publishes under your account and it is the trust anchor for a
21.4 MB native library the app will P/Invoke.

Budget: about 30 minutes, most of it a download. Everything below is PowerShell you can paste.

> **Why a release tag of its own and not `vX.Y.Z`.** The engine's bytes change when Mozilla ships a
> new model, which has nothing to do with when the app ships. A separate tag also keeps the release
> checklist's step 5 (`gh release view vX.Y.Z --json name,assets` shows exactly the exe and the MSI)
> true, unchanged.

---

## 1. What the release must contain

Exactly six assets, with **exactly** these file names — the app composes each download URL as
`https://github.com/Kizotis/PWRU-Helper/releases/download/offline-engine-v1/<name>` and the name on
the release is also the name on disk:

| Asset | Bytes | What it is |
|---|---|---|
| `bergamot.dll` | 22,460,928 | The native translation engine (win-x64) |
| `model.ruen.intgemm.alphas.bin` | *(from the registry)* | Mozilla `tiny` ru→en v3.0 model |
| `vocab.ruen.spm` | *(from the registry)* | its SentencePiece vocabulary |
| `lex.50.50.ruen.s2t.bin` | *(from the registry)* | its lexical shortlist |
| `LICENSE-MPL-2.0.txt` | — | The full MPL-2.0 text — [in this folder](LICENSE-MPL-2.0.txt), upload it as-is |
| `NOTICE-offline-engine.md` | — | Which files the MPL covers and where their source is — [in this folder](NOTICE-offline-engine.md), upload it as-is |

The last two are the **licence obligation**, and they are not optional. MPL-2.0 asks that the licence
text accompany the distribution, that the covered files be identified, and that their source form be
reachable. The engine is distributed *from this release*, so that is where those two files belong —
physically beside the binaries they cover. The About tab already carries the one-sentence notice for
users who never see a release page.

The app only ever downloads the **first four**. The other two are for the human who opens the page.

> **SignPath is unaffected.** Its Foundation plan requires the *application* to stay OSI-licensed,
> and PWRU Helper stays MIT — MPL-2.0 is file-level copyleft on files that ship separately, and it
> does not reach the app. Nothing in [`signpath-signing.md`](signpath-signing.md) changes.

---

## 2. Get `bergamot.dll`

It is already on your disk: it is the native asset of the pinned NuGet package. The app references
that package with `ExcludeAssets="native"` — which is *why* the exe does not grow — but the package
itself is still restored whole into the NuGet cache.

```powershell
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { "$env:USERPROFILE\.nuget\packages" }
$dll = Join-Path $cache "bergamottranslatorsharp\0.5.1\runtimes\win-x64\native\bergamot.dll"
if (-not (Test-Path $dll)) { dotnet restore "PWRUHelper.csproj" }   # run this from the repo root
Get-Item $dll | Select-Object Length, FullName
```

`Length` must be **22,460,928**. Anything else is a different package version and the manifest's
pinned size will reject it.

```powershell
$stage = "$env:USERPROFILE\Downloads\offline-engine-v1"
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $dll $stage
```

---

## 3. Get the three model files

The bytes and the integrity values come from **two different Mozilla endpoints**, on purpose:

- **Remote Settings** publishes `decompressedSize` and `decompressedHash` — so a download can be
  *verified*, not merely fetched — but serves its attachments **zstd**-compressed, which .NET 8
  cannot read without another dependency this project will not add.
- The **gzip GCS mirror** named by `BergamotTranslatorSharp`'s own README serves the same files
  gzip-compressed, which Windows and .NET handle in the box.

So: **bytes from the mirror, integrity from Remote Settings.** The E8.S1 spike confirmed the two
agree byte-for-byte; the script below re-checks that agreement rather than trusting it.

`mozilla/firefox-translations-models` was archived on 2025-12-15 — do **not** take the files from
there.

```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'
$stage = "$env:USERPROFILE\Downloads\offline-engine-v1"

# (a) integrity — Remote Settings
$rs = Invoke-RestMethod "https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/translations-models-v2/records"
$want = @{}
foreach ($r in $rs.data) {
  if ($r.sourceLanguage -ne 'ru' -or $r.targetLanguage -ne 'en' -or $r.architecture -ne 'tiny') { continue }
  $want[$r.name] = @{ Size = [int64]$r.decompressedSize; Hash = $r.decompressedHash }
}
if ($want.Count -ne 3) { throw "expected 3 tiny ru-en records, got $($want.Count)" }

# (b) bytes — the gzip mirror the binding's README names
$reg  = Invoke-RestMethod "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json"
$base = $reg.baseUrl
$cand = @($reg.models.'ru-en' | Where-Object { $_.architecture -eq 'tiny' })[0]

foreach ($key in 'model','vocab','lexicalShortlist') {
  $path = $cand.files.$key.path
  $name = [IO.Path]::GetFileName($path) -replace '\.gz$', ''
  $gzTmp = Join-Path $env:TEMP "$name.gz"
  Invoke-WebRequest "$base/$path" -OutFile $gzTmp -UseBasicParsing

  $in  = [IO.File]::OpenRead($gzTmp)
  $gz  = New-Object System.IO.Compression.GZipStream -ArgumentList $in, ([IO.Compression.CompressionMode]::Decompress)
  $out = [IO.File]::Create((Join-Path $stage $name))
  $gz.CopyTo($out)
  $out.Dispose(); $gz.Dispose(); $in.Dispose()
  Remove-Item $gzTmp

  # the two registries must agree — this is the check, not a formality
  $local = Join-Path $stage $name
  $size  = (Get-Item $local).Length
  $hash  = (Get-FileHash $local -Algorithm SHA256).Hash.ToLower()
  if ($size -ne $want[$name].Size) { throw "$name : size $size, registry says $($want[$name].Size)" }
  if ($hash -ne $want[$name].Hash) { throw "$name : hash mismatch against Remote Settings" }
  "OK  $name  $size"
}
```

The three names that come out must be `model.ruen.intgemm.alphas.bin`, `vocab.ruen.spm` and
`lex.50.50.ruen.s2t.bin`. If Mozilla has renamed a file, **stop**: the Marian config the app writes
(`OfflineModelManifest.ConfigText`) names these three literally, so a rename is a code change, not a
paste.

Then add the two licence files to the staging folder:

```powershell
Copy-Item "packaging\LICENSE-MPL-2.0.txt", "packaging\NOTICE-offline-engine.md" $stage   # from the repo root
Get-ChildItem $stage | Select-Object Name, Length
```

Six files.

---

## 4. Compute the four sizes and digests

**Lower-case hex.** `Get-FileHash` returns upper case; the manifest's validity check accepts `0-9`
and `a-f` only, so an upper-case digest is rejected as structurally invalid — which is a safe
failure, but a confusing one. `.ToLower()` is in the line below for that reason.

```powershell
$stage = "$env:USERPROFILE\Downloads\offline-engine-v1"
'bergamot.dll','model.ruen.intgemm.alphas.bin','vocab.ruen.spm','lex.50.50.ruen.s2t.bin' | ForEach-Object {
  $f = Join-Path $stage $_
  '{0,-32} {1,12}  "{2}"' -f $_, (Get-Item $f).Length, (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
}
```

---

## 5. Create the release and upload

PowerShell does **not** expand a wildcard for a native command such as `gh`, so the six paths are
listed explicitly:

```powershell
$stage  = "$env:USERPROFILE\Downloads\offline-engine-v1"
$assets = @(Get-ChildItem $stage -File | ForEach-Object { $_.FullName })
if ($assets.Count -ne 6) { throw "expected 6 assets in $stage, found $($assets.Count)" }

gh release create offline-engine-v1 `
  --repo Kizotis/PWRU-Helper `
  --title "PWRU Helper — offline engine v1 (Bergamot tiny ru-en)" `
  --notes "Optional offline translation engine for PWRU Helper. bergamot.dll (BergamotTranslatorSharp 0.5.1) and the Mozilla tiny ru-en v3.0 model. All files are MPL-2.0 — see LICENSE-MPL-2.0.txt and NOTICE-offline-engine.md in this release. PWRU Helper itself remains MIT." `
  @assets
```

Then confirm all six landed, the way the release checklist confirms the app's own artefacts:

```powershell
gh release view offline-engine-v1 --repo Kizotis/PWRU-Helper --json name,assets
```

> The release must **not** be a draft and **not** a pre-release: the app downloads by direct asset
> URL and a draft's assets are not publicly reachable.

---

## 6. Paste the numbers into the manifest

Open [`Services/OfflineModelManifest.cs`](../Services/OfflineModelManifest.cs) and find
`Shipping`. It reads:

```csharp
private static readonly OfflineModelManifest Shipping = new("offline-engine-v1", new[]
{
    new OfflineFile(NativeFileName, 22_460_928, Todo, null),
    new OfflineFile("model.ruen.intgemm.alphas.bin", 0, Todo, RuEn),
    new OfflineFile("vocab.ruen.spm", 0, Todo, RuEn),
    new OfflineFile("lex.50.50.ruen.s2t.bin", 0, Todo, RuEn),
});
```

Replace each `0` with the size from § 4 and each `Todo` with that file's digest **in double quotes**.
`bergamot.dll`'s size is already correct — leave the `22_460_928` alone; if § 4 printed a different
number for it you have the wrong package, and § 2 is where to look.

Change **nothing else**: not the tag, not the file names, not `ReleaseBase`. The manifest is the one
place the app's download host is spelled, and it is handed straight to
`UpdateService.IsTrustedDownload` — a change there is a change to the app's allow-list.

---

## 7. The check that proves it

Half a paste is the dangerous state — three real digests and one leftover `TODO-owner` reads as a
manifest somebody meant to finish. There is a test for exactly that, and it fails on the leftover:

```
dotnet test tests/PWRUHelper.Tests --filter "FullyQualifiedName~PackagingTests.The_shipping_manifest_is_wholly_the_owners_placeholder_or_wholly_populated"
```

Green means the table is internally consistent: either all four rows are still placeholders, or all
four carry a size and a 64-character lower-case digest. Red names the row that does not match its
neighbours.

To see **which** of the two states you are in — the test passes in both — grep for the placeholder:

```powershell
Select-String -Path "Services\OfflineModelManifest.cs" -Pattern 'Todo,' -SimpleMatch
```

No output means every digest is pasted.

Then run the offline suite as a whole, which exercises the download, verification and load paths
against its own fixtures:

```
dotnet test tests/PWRUHelper.Tests --filter "FullyQualifiedName~PublishFlags|FullyQualifiedName~Packaging|FullyQualifiedName~OfflineModel|FullyQualifiedName~Bergamot"
```

Nothing in there downloads anything — CI never fetches the model or loads the native DLL, and that
is deliberate. The real proof is the last step.

---

## 8. Prove it end to end, once, by hand

On a machine with the built app:

1. **About → Offline engine (optional) → Download.** It fetches ~45 MB into
   `%LocalAppData%\PWRUHelper\models\`, verifying every file's size and SHA-256 before anything is
   renamed into place. Any mismatch deletes the whole folder and reports "could not be verified" —
   which is the correct outcome if a digest was mistyped.
2. **Translate something Russian with the network off.** The offline rung is last in both chains, so
   it only answers once the cloud tiers cannot. The chip reads `● Offline`.
3. **Remove** it and watch the row go back to not installed.

If step 1 fails with "could not be verified" while step 7 is green, the bytes on the release differ
from the bytes you hashed — re-upload from the staging folder rather than re-hashing.

---

## What this task does not change

- **No build file, no publish flag, no `PublishFlagsTests`.** The engine is downloaded, so the
  portable exe stays one file at its current size and `%TEMP%\.net\PWRUHelper\<id>` stays at its
  five WPF files. That is the whole point of the packaging decision (E8.S6); see
  [`../docs/investigations/03-stories/spikes/U6-U7-bergamot.md`](../docs/investigations/03-stories/spikes/U6-U7-bergamot.md).
- **No change to the app's release checklist.** `offline-engine-v1` is its own tag; `vX.Y.Z` still
  carries exactly the exe and the MSI.
- **No change to the download allow-list.** The engine comes from a GitHub release precisely so that
  `github.com` / `*.githubusercontent.com` did not have to be widened.
- **No change to the app's licence.** MIT, and SignPath's condition with it.
