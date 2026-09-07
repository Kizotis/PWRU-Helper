# U6 / U7 — what Bergamot `tiny` ru→en actually costs, and where its DLL can live

_Spike E8.S1 (release C, epic E8) · measured 2026-09-07 on the dev box by Amelia · harness:
`tests/PWRUHelper.Tests/BergamotSpike.cs`, opt-in, invisible to a normal `dotnet test`._

**Verdict in one sentence: GO — all five thresholds hold (init 82 ms · 8.3 ms/line · +121 MiB
resident · the memory comes back on `Dispose` · 0 MB of exe growth) — but two things the go/no-go
table never asked about came back: the process *commits* +367 MiB while the engine is live, and the
shipping `Data/slang.json` recovers far less than `benchmark…` §10's table implies, because only 4 of
its 46 entries carry a `full` form.**

## 1. Machine

| Field | Value |
|---|---|
| Host / OS / CPU | `GSIN-WS-DT004` · Windows 11 build 26200, x64 · Intel Core i7-9700 (8 logical cores), 16 GB |
| Disk / runtime | `%TEMP%` on the KIOXIA KBG30ZMV512G NVMe SSD (system disk) · .NET 8.0.30, **Debug** test assembly |
| AV posture | Microsoft Defender running, no `%TEMP%` exclusion; **the corporate dev box, not the Defender-only personal machine** |
| Warmth | **warm** — the DLL and the three model files were read by the same process seconds earlier |

## 2. Method

`BergamotTranslatorSharp` **0.5.1**, pinned, referenced **only by the test project**, only under
`PWRU_SPIKE=1`, with `ExcludeAssets="native"`. The `tiny` ru→en model is downloaded on demand into
`%TEMP%\pwru-bergamot-spike\` and **verified, not merely fetched**: bytes gzipped from the GCS mirror
the binding's README names, integrity (`decompressedSize` + `decompressedHash`) from Remote Settings,
which serves **zstd** that .NET 8 cannot read without a second NuGet this spike may not add. The two
registries agree byte-for-byte. Config is the README's, unchanged. Every timing is the **median of 7
runs after a discarded warm-up**; nothing asserts on a duration (CI-3). **No translation API was
called** — the cloud column is E8.S7's.

## 3. The numbers

| number | median | min | max | `benchmark…` §10 reference |
|---|---|---|---|---|
| init (construct `BlockingService`) | **82.5 ms** | 76.2 | 88.6 | 103–119 ms |
| first translate after init | **108.8 ms** | 97.5 | 125.5 | — (the pool is allocated **here**) |
| per line, one native call per line | **8.34 ms** | 7.88 | 10.23 | 6.5–12.1 ms |
| per line, HTML-batched frame (`Translate(IEnumerable<string>)`) | **3.75 ms** | 3.14 | 4.08 | — |
| throughput, one call per line | **120 lines/s** | | | 64–80 lines/s |
| throughput, batched | **266 lines/s** | | | — |

The resident cost, sampled at the four points that matter — **the order is the measurement**:

| sample point | working set | private bytes | Δ ws | Δ private |
|---|---|---|---|---|
| baseline (nothing loaded) | 76.0 MiB | 25.5 MiB | +0.0 | +0.0 |
| after **init** | 106.6 MiB | 55.3 MiB | **+30.5 MiB** | +29.8 MiB |
| after the **first translate** | 197.1 MiB | 392.9 MiB | **+121.0 MiB** | **+367.4 MiB** |
| after 400 more translations | 198.7 MiB | 392.0 MiB | +122.7 MiB | +366.5 MiB |
| after `Dispose` + 2 collections | 82.0 MiB | 25.9 MiB | **+6.0 MiB** | **+0.4 MiB** |

**At rest is genuinely 0.** With all 21 MB of model on disk and the engine never constructed, the
working set does not move (−1.5 MiB, i.e. noise) — nothing is read until something asks (I10).
Provenance re-checked rather than assumed: `bergamot.dll` = **22,460,928 B**, imports still
**`KERNEL32` / `SHELL32` / `ole32` / `dbghelp` only** (no MKL, **no MSVC redistributable**); model
total **22,530,152 B**, matching §6.1 exactly (`tiny` v3.0).

## 4. The twenty lines, raw and after `SlangGlossary.Expand`

Glossary: the shipping `Data/slang.json` — **46 entries, 4 of them with a `full` form**, so only
8 of the 20 lines change at all.

| # | chat line | offline, raw | offline, after `Expand` |
|---|---|---|---|
| 1 | Всем привет, кто идет в данж? | Hello everyone, who's going on the subject? | _(unchanged)_ |
| 2 | го пати на босса, нужен хил | GO party for the boss, need a thile | GO party for the boss, need a **healer** |
| 3 | нид на дроп, я хил | Dneed on a Drop, I'm ahhhhhhhh | A drop node, I'm a **doctor** |
| 4 | спс за пати, было весело | App for the party, it was fun | _(unchanged)_ |
| 5 | лфг сложка, есть хил и танк | lfg salk, there is a ham and a tank | **looking for a group of complex mode, there is a doctor and a tank** |
| 6 | стук в гильдию, я прист 105 лвл | knocking in the guild, I will be a 105vl | **write me for an invitation** to the guild, I am a ce. |
| 7 | в пп нужен дд и хил, стук | in the pp need d and chil, knocking | in pp need dD and a **doctor**, **write to me for an invitation** |
| 8 | кто в 5-3 лега? нужен вар и лук | Who's in 5-3 years? Need a Buckt and Buck | _(unchanged)_ |
| 9 | сбор на гвг в восемь, все в бд | collection for Gwg at eight, all on bbd | _(unchanged)_ |
| 10 | ищем мист на ара, стук в лс | looking for a saist on the ara, knocking in the l | looking for a ouss at the ara, **write to me for an invitation** to LS |
| 11 | есть места на тс? я син 100 | Have a place for T? I'm Sin 100 | _(unchanged)_ |
| 12 | нужен ганер на адепты, лфг | need a ganner for adheres, lfg | need a panel of adherents, **looking for a group** |
| 13 | в мбг идем через десять минут | in MBg go in ten minutes | _(unchanged)_ |
| 14 | дру и шам в пати на хс, стук | Dru and Sham in the Hs party, knocking | Drou and shama in the party on x, **write me for an invitation** |
| 15 | перезайду, вылетело из игры | I'd re-loo, we're out of the game | _(unchanged)_ |
| 16 | подскажите, где взять задание | Tell me where to get the task | _(unchanged)_ |
| 17 | продам меч плюс десять недорого | sell a sword plus ten inexpensive | _(unchanged)_ |
| 18 | у меня лаги, подождите немного | I have lags, wait a little | _(unchanged)_ |
| 19 | встречаемся на западном мосту | Meeting on a Western Bridge | _(unchanged)_ |
| 20 | поздравляю с новым уровнем | Congratulations on the new level | _(unchanged)_ |

**The agent does not grade this table** — go-criterion 3 is the owner's, on his own lines (E8.S7).

## 5. The five thresholds

| # | criterion | threshold | measured | verdict |
|---|---|---|---|---|
| 1 | init on first fallback use | ≤ 500 ms | **82.5 ms** — *faster* than §10's 103–119 ms | **PASS** |
| 2 | per-line latency | ≤ 15 ms | **8.34 ms** (3.75 batched) | **PASS** |
| 3 | resident delta while active | ≤ 150 MiB | **+121.0 MiB** working set (but see §6) | **PASS** |
| 4 | resident delta after unload | ≈ 0 MiB | **+6.0 MiB** ws, **+0.4 MiB** private | **PASS** |
| 5 | exe growth | ≤ 25 MB bundled, or **0 MB** | **0 B** — exe 187,631,934 B, unchanged | **PASS** |

## 6. Three things nobody was looking for

1. **The process *commits* +367 MiB, not +127.** `WorkingSet64` (§10's RSS) goes up 121 MiB;
   `PrivateMemorySize64` — committed private bytes, i.e. commit charge and pagefile reservation —
   goes up **367 MiB**, tripling the process. **Not tunable**: one extra point at `workspace: 8`
   (a single point, deliberately **not** §10's sweep, on a metric §10 never reported) came back at
   **+368.5 MiB**. Criterion 3 says *resident* and resident passes — but **E8.S7 must read the
   private-bytes column on the low-RAM machines**, not only the working set.
2. **§10's slang table does not reproduce against the shipping glossary.** §10 shows `данж` →
   "dungeon" and `спс` → "Thanks" after expansion. Neither happens: **`данж`, `спс`, `нид`, `дроп`,
   `пати` and `го` are not in `Data/slang.json` at all**, and of the 46 entries that are, only
   **лфг · стук · сложка · хил** carry a `full` form — the only field `Expand` acts on. It works
   where it exists (lines 2, 5, 6, 7, 10, 12, 14), but "the existing expansion layer recovers most of
   the gap" describes a glossary this repo does not ship. **The offline path's quality is gated on
   `full` forms, not on the engine** — cheap to fix, and it needs the owner's Russian (project rule:
   only add `full` when sure). Input for E8.S2/E8.S7.
3. **The model registry is zstd, and .NET 8 is not.** **E8.S3 inherits it**: a shipping model store
   uses the gzip mirror, mirrors the files on a GitHub release (already in `UpdateService`'s
   allowlist), or takes a zstd package — a second NuGet, which needs a ruling.

## 7. Verdict and the packaging conclusion

**GO.** All five thresholds hold, with margin on four (init 6× under, latency 1.8× under and 4× under
batched, and the memory genuinely comes back). E8.S2 may be written. **U7 is answered, and it is the
good answer.** With `ExcludeAssets="native"` the managed binding still
built and still ran, and every number above went through a `bergamot.dll` in a plain downloaded
directory, resolved by `NativeLibrary.SetDllImportResolver`. Confirmed in the same run: the DLL is
**not** in the build output; the portable exe is **187,631,934 B — unchanged, 0 B of growth**; and
`%TEMP%\.net\PWRUHelper\<id>` is still **5 files, 8,214,968 B**, so §7.6 constraint 4's worry is
answered — the P1 self-extraction mechanism is **not** re-armed. `PublishFlagsTests.cs` and the three
build files were untouched (CI-7). The recommendation stands and is now measured: **download the DLL
with the models.** The bundled layout was not published — with 0 MB measured on the beside-the-exe
layout there is nothing to learn from confirming the worse option; the A/B cold-start pair, if wanted,
is **E8.S6**'s call and its measurement.

## 8. The owner's half — the slow machine (S7), and it gates nothing here

From a checkout of this branch, one line in `cmd`:

```
set PWRU_SPIKE=1 && dotnet test tests/PWRUHelper.Tests --filter "FullyQualifiedName~BergamotSpike" --logger "console;verbosity=detailed"
```

It downloads ~15 MB once into `%TEMP%\pwru-bergamot-spike\` (and skips it on every later run), then
prints all of the above. Run it **once right after a reboot** on the Defender-only personal machine
and on a P1-affected machine, and paste §3's two tables here. Five numbers to read back: **init ms ·
ms/line · Δ working set after the first translate · Δ private bytes after the first translate ·
Δ working set after Dispose**. The fourth is new and is the one §6 asks for.

**This spike gates E8.S2 and nothing else.** It does not gate release C's other epics, it changes no
production code, and a normal `dotnet test` is unchanged — **1213 tests**, nothing downloaded, no
case discovered in `BergamotSpike`.
