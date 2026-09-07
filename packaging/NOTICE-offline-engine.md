# Third-party notice — PWRU Helper's optional offline translation engine

PWRU Helper itself is **MIT** (see [`LICENSE`](../LICENSE)) and that does not change.

This notice covers the **optional offline engine**, which is a separate set of files under the
**Mozilla Public License, Version 2.0 (MPL-2.0)**. The full licence text ships beside this file as
[`LICENSE-MPL-2.0.txt`](LICENSE-MPL-2.0.txt) and is also uploaded to the GitHub release the app
downloads the engine from.

MPL-2.0 is **file-level** copyleft: it applies to the files listed below and does **not** reach the
MIT-licensed application that loads them. That is why the app can stay MIT — and why SignPath
Foundation's "the app itself must stay OSI-licensed" condition is still met.

## Covered artefacts

| Artefact | Where it lives | Licence | Upstream source form |
|---|---|---|---|
| `BergamotTranslatorSharp.dll` (9,728 B) — the managed .NET binding | **Inside `PWRUHelper.exe`** (NuGet `BergamotTranslatorSharp` **0.5.1**) | MPL-2.0 | <https://github.com/Freeesia/BergamotTranslatorSharp> (commit `da44aeb6c9a8b110c06aea572e7dd1432cdf7f53`) |
| `bergamot.dll` (22,460,928 B) — the native translation engine | **Downloaded on request** into `%LocalAppData%\PWRUHelper\models\`; the exact binary shipped in the same package's `runtimes/win-x64/native/` | MPL-2.0 | Built from <https://github.com/browsermt/bergamot-translator> (project site <https://browser.mt/>); the build recipe is in the binding's own README |
| `model.ruen.intgemm.alphas.bin` · `vocab.ruen.spm` · `lex.50.50.ruen.s2t.bin` — the Mozilla `tiny` ru→en v3.0 model (22,530,152 B total) | **Downloaded on request** into `%LocalAppData%\PWRUHelper\models\ru-en\` | MPL-2.0 | <https://github.com/mozilla/firefox-translations-models> (archived 2025-12-15). The live publication points are Mozilla's model registry <https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json> and Remote Settings <https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/translations-models-v2/records> |

`config.txt` is **not** covered: it is a few lines of Marian configuration written by PWRU Helper
itself (`Services/OfflineModelManifest.ConfigText`), not downloaded.

## What MPL-2.0 asks of us, and where each obligation is discharged

| Obligation | Discharged by |
|---|---|
| The licence text accompanies the distribution | `LICENSE-MPL-2.0.txt`, uploaded as an asset of the `offline-engine-v1` release the app downloads from — so a user who gets the binaries gets the licence in the same place |
| The covered files are identified | This file, uploaded to the same release |
| The **source form** is available to recipients | The three upstream URLs above. All are public repositories; nothing here is a modified build — the binaries are the upstream ones, byte for byte, and their SHA-256 is pinned in `Services/OfflineModelManifest.cs` |
| The notice is visible to a user who never reads a release page | The **About** tab: *"The optional offline translation engine (Bergamot), its .NET wrapper and its model files are licensed under the MPL-2.0."* |

## Nothing here is modified

PWRU Helper does not patch, recompile or re-link any of the covered files. It downloads the upstream
binaries, verifies their size and SHA-256 against a table compiled into the exe, and loads
`bergamot.dll` through `NativeLibrary.SetDllImportResolver`. There is therefore no "Modified
Covered Software" to publish under MPL-2.0 § 3.2 — but if that ever changes, that section is what
would apply, and this notice is where it would have to be said.
