# 02 — P2 · Migration plan

_Phase 2 · author: **Winston** (BMAD System Architect) · companion to `architecture-cible.md` ·
baseline commit `4759712` = `main` v0.14.0 · 2026-09-06 · status: **executed** — every increment below landed;
increments 1–6 shipped as v0.15.0, increment 7 (the offline tier) as v0.15.2. Kept as the plan
of record, with dated outcome notes where reality differed from the proposal._

**Rule for every increment below:** it must be **shippable on its own** (the app works, the suite is green, the
release could be cut) and **reversible on its own** (one PR, one revert). There is no big-bang. Every change lands
on `main` via PR, branch names `feature/…` / `fix/…` / `docs/…`, no direct pushes, no self-merging.

**Size legend:** S ≈ half a day · M ≈ 1–2 days · L ≈ 3+ days, for an agent working with review.

**One coupling, stated up front.** Increments **1 and 2 are separate PRs but a single release.** The owner's
decision 2 says the endpoint switch ships *coupled with the hardening*; the architecture adds the reason it must
also not ship *before* it (`architecture-cible.md` R7): a circuit breaker in front of an endpoint that returns 429
on request #1 is an app that is correctly paused all the time. Increment 0 may ship on its own; 3 through 7 may each
ship on their own.

---

## Increment 0 — Prerequisites: testability seam, typed errors, logging

**Zero behaviour change.** This is the increment that makes every later one provable.

| | |
|---|---|
| **Scope** | (a) `HttpMessageHandler` ctor overload on `TranslationService` and `DeepLTranslator`; a shared `SocketsHttpHandler` with `PooledConnectionLifetime = 2 min` for the production clients. (b) `TranslationErrorKind` + `TranslationException(kind, message, retryAt, providerId)`, no message-only ctor; `ProviderErrorMapper` with the §4.2 table and the §4.3 HTML sniffing; every existing throw site classified. (c) `Friendly()` becomes a `Kind` switch — **wording unchanged for now**, one existing string per `Kind`, so this increment is invisible to users. (d) The `analyse…` §5.2 per-request log line, emitted from the existing retry loop; `LogWriter` timestamp gains milliseconds. (e) `TranslationPolicy` created, holding today's numbers so the file exists before anything tunes it. |
| **Files touched** | `Services/TranslationService.cs:32,37-44,119-178` · `Services/DeepLTranslator.cs:16,80-110,125-129` · `Services/Logging.cs:85` · `MainWindow.xaml.cs:404-410` · **new:** `Services/TranslationErrors.cs`, `Services/ProviderErrorMapper.cs`, `Services/TranslationPolicy.cs` · `tests/PWRUHelper.Tests/TranslationBackendTests.cs:78` (one ctor call) |
| **Tests added** | A `FakeHandler : HttpMessageHandler` test double (the suite has none — `analyse…` §6.4 gap 3). T6 (429 → today's 3 attempts, pinned *before* it changes), T7 (503 attempt count), T12 partial (the Google 429 HTML body from `benchmark…` §3.1 → `RateLimited`), T13 (HTML on a 200 never reaches `JsonDocument.Parse`), T14 (403 classified `Blocked`, not folded with 400/404). Log-line test through `Logging.DirectoryOverride`, asserting **no user text and no key** appear (I11). |
| **Risk** | **Low.** The only real hazard is a mis-classification that changes a user-visible string by accident; mitigated by keeping `Friendly()`'s existing wording per `Kind` in this increment. |
| **Rollback** | Single revert. Nothing else depends on it yet. |
| **Size** | **M** |
| **Depends on** | — |
| **Definition of done** | Suite green and still headless-safe. A fake handler can return 429, 403, a 200-with-HTML and a timeout, and each produces the right `Kind`. A manual translation failure now writes a log line, and "Copy error report" is no longer empty for a P2-shaped failure. No user-visible string changed. |

---

## Increment 1 — `ProviderGate`, the retry policy, and persisted state

| | |
|---|---|
| **Scope** | `ProviderGate` (circuit breaker per §5.2/§5.3 + token bucket per §5.4, injectable clock, `RequestPriority`), `ProviderGates` static registry with `PathOverride`/`Clock`/`ResetForTests`/`ClearAuthBlock`, `ProviderStateStore` (`provider-state.json`, atomic write, lazy load, clock-skew clamp), `HttpProviderCore` (gate admission → ≤2 attempts with full jitter → mapping → log → outcome reporting), `Retry-After` parsing. The two existing providers are moved onto `HttpProviderCore`. **The 300/600 ms fixed retry and the third attempt are removed here.** Gate-transition logging (§10.2). |
| **Files touched** | **New:** `Services/ProviderGate.cs`, `Services/ProviderGates.cs`, `Services/ProviderStateStore.cs`, `Services/HttpProviderCore.cs` · `Services/TranslationService.cs:119-178` (retry loop replaced) · `Services/DeepLTranslator.cs:56-111` · `Services/TranslationPolicy.cs` (the real numbers) |
| **Tests added** | T1 gate opens/half-opens once · T2 escalation and 30-min cap · T3 clean reset · T4 persistence round-trip + corrupt file → empty registry · T5 clock-skew clamp · T6 **429 → exactly one attempt** (replacing the increment-0 pin) · T7 503 → exactly 2 attempts within the jitter band · T8 `Retry-After` both forms, clamped, overriding. A dedicated **non-parallel** xUnit collection with `ResetForTests()` in the fixture (risk R4). |
| **Risk** | **Medium-high, and it is the reason for the coupling.** Against `client=gtx` — which 429s on request #1 — this increment on its own turns the app into "paused" almost permanently. It is *correct* and it is *worse for the user*. It must not reach a release without increment 2. |
| **Rollback** | Single revert. `provider-state.json` becomes an orphan file; harmless and ignored by the older code. |
| **Size** | **L** |
| **Depends on** | 0 |
| **Definition of done** | The gate opens on a simulated 429, refuses every subsequent call until the window passes, admits exactly one probe, closes on success, and survives a simulated process restart. A 429 costs **one** HTTP request, not three. The log shows the open/half-open/closed transitions. Nothing runs before the window is visible (I10) — verified by the absence of `ProviderGates` from any startup path. |

---

## Increment 2 — `GoogleDict` default, `Edge` tier, `ChainTranslator`

**Ships in the same release as increment 1.**

| | |
|---|---|
| **Scope** | `ChainTranslator` (§6) replacing `FallbackTranslator`, which is deleted; `TextChunker` extracted; `TranslationService` renamed to `GoogleGtxTranslator`; **new** `GoogleDictTranslator` and `EdgeTranslator`; both chains rebuilt in the `MainWindow` ctor per §8.1 (still one cache each at this point — the shared store is increment 3); the `PerLineCap` bound of §6.3. |
| **Prerequisite spike, before any provider code** | Capture, on a branch and with the owner's explicit go for the two requests: **(U1)** one `\n`-joined 3-line `q` against `clients5.google.com/translate_a/t` to prove newlines survive; **(U2)** one successful `edge.microsoft.com/translate/translatetext` request/response pair. Both are frozen as test fixtures. **No `EdgeTranslator` code exists before U2 is captured.** If U1 fails → OQ-A goes to the owner before this increment continues. |
| **Files touched** | **New:** `Services/ChainTranslator.cs`, `Services/GoogleDictTranslator.cs`, `Services/EdgeTranslator.cs`, `Services/TextChunker.cs` · **renamed:** `Services/TranslationService.cs` → `Services/GoogleGtxTranslator.cs` · **deleted:** `Services/FallbackTranslator.cs` · `MainWindow.xaml.cs:43,80-104` · `MainWindow.Translate.cs:226-233,239` · `tests/PWRUHelper.Tests/ServicesTests.cs:80,91` · `tests/PWRUHelper.Tests/TranslationBackendTests.cs:49-109` (re-pointed at `ChainTranslator`) · **`project-context.md`** (the pipeline-shape paragraph names `TranslationService`) |
| **Tests added** | T9 chain skips open gates without calling them · T10 `AllProvidersPaused` with the earliest `retryAt` · T11 real-cancel-propagates and timeout-falls-through, inherited from `TranslationBackendTests.cs:84,98` · T12 parser fixtures: `dict-chrome-ex` shapes A and B, Edge · T19 per-line cap. |
| **Risk** | **Medium.** Two undocumented endpoints, one of them only ~1 month proven. The rename touches docs. The mitigation is structural: a chain of four free tiers where today there is one, behind a gate that fails legibly. |
| **Rollback** | Single revert restores `gtx` as the sole free provider. The gate from increment 1 survives the revert and keeps working — that is the point of shipping them as two PRs in one release. |
| **Size** | **L** |
| **Depends on** | 1 (hard: must be in the same release), and the U1/U2 spike |
| **Definition of done** | A translation succeeds through `dict-chrome-ex` on a network where `gtx` 429s. Killing tier 1 with a fake handler transparently produces a tier-2 result. Opening every gate produces exactly one `AllProvidersPaused` message with a countdown, and **zero HTTP requests**. `project-context.md` matches the code again. |

---

## Increment 3 — One shared, persistent cache

| | |
|---|---|
| **Scope** | `TranslationCacheStore` (§8.2): the LRU moves into the store, capacity 2000, lazy load on first miss off the UI thread, debounced atomic save plus a save on `OnClosing`, MRU-ordered file, provider recorded per entry. `CachingTranslator` gains the store-taking ctor and keeps its existing one. Both decorators share one store; `Translate.cs:239` stops discarding the cache on key save (amplifier A5). |
| **Files touched** | **New:** `Services/TranslationCacheStore.cs` · `Services/CachingTranslator.cs:15-29,103-126` · `MainWindow.xaml.cs` (ctor) · `MainWindow.Translate.cs:239` · a `SaveNow()` call in the existing `OnClosing` path |
| **Tests added** | T17 persistence + the `(`-prefix rule enforced on disk + corrupt file → empty cache · T18 the write decorator serves a value stored by the read decorator, and a key save does not lose it. `CachingTranslatorTests` (7 cases) must pass **unchanged**. |
| **Risk** | **Low-medium.** Two hazards: a slow load hurting G6 (measure U8; capacity is the knob) and a partially-written file (mitigated by the `SettingsService` atomic pattern and by treating any parse failure as an empty cache). |
| **Rollback** | Single revert; `translation-cache.json` becomes an orphan. |
| **Size** | **M** |
| **Depends on** | 2 (it wraps the chains) |
| **Definition of done** | A line translated in the LIVE feed is served from cache when typed in the Translator tab, in the same session **and after a restart**. Saving a DeepL key does not empty the cache. A `(`-prefixed value never appears in the file. Measured load time recorded in the PR. |

---

## Increment 4 — LIVE back-off, honest auto-stop, retry of failed rows, honest read-once

| | |
|---|---|
| **Scope** | §9 in full: (a) a tick that finds every read tier gate-open does **no** capture, OCR, dedup or request, and the interval backs off ×2 to a 5 s cap; (b) `consecutiveErrors` resets only on a tick that translated successfully, plus a 5-errors-in-2-minutes window rule, with gate-open ticks counting as neither; (c) the `_pendingRetry` queue of failed rows, drained after recovery, bounded, cleared on stop; (d) read-once returns a real outcome and gets a real cancellation token with a 30 s budget. |
| **Files touched** | `MainWindow.Live.cs:179-245` (loop), `:219` (the reset), `:231-238` (auto-stop), `:250-288` (`AppendLinesToHistory`), `:277-283` (the failure branch) · `MainWindow.Ocr.cs:207-256,268-303` (`:243-244`, `:295`, `:296-299`) · `Services/TranslationPolicy.cs` |
| **Tests added** | T15 auto-stop semantics — the four tick outcomes and the time-window rule · T16 pending-retry queue: re-translation after recovery, eviction with `MaxHistory`, bounded, cleared on stop. `LiveDedupTests` and `LiveDefaultsTests` must pass **unchanged** — `LiveDedup` is not touched. `DefaultsAndResizeTests.cs:28-32` must stay green: the back-off changes the *wait*, not `LiveIntervalMs(double)`. |
| **Risk** | **Medium.** This is the increment closest to the feature users actually watch, and the LIVE loop has the subtlest existing behaviour in the app (I16 lists eleven things that must not move). A paused loop that *looks* dead is a support problem until increment 6 lands the UI — which is why the status string is written here even in provisional wording. |
| **Rollback** | Single revert. The gate keeps protecting the endpoints; only the loop's politeness and honesty regress. |
| **Size** | **M** |
| **Depends on** | 2 (needs `AllProvidersPaused` and `RetryAt`) |
| **Definition of done** | With every gate forced open, a LIVE session issues **zero** requests, does no OCR, and its status shows a countdown. Rows that failed during a blip are re-translated after recovery instead of staying `(…)`. A read-once that failed says so instead of `Done`. A calm chat under a persistent failure now auto-stops, where today it never does. |

---

## Increment 5 — Azure provider and the settings UI

| | |
|---|---|
| **Scope** | `AzureTranslator` (§7.5) over raw `HttpClient`; the five new `AppSettings` fields (§12) with `Sanitize` null-guards and **no** `SettingsVersion` bump; the settings controls for the Azure key, the region and the `UseKeyForReading` opt-in, each following the `_restoringSettings` pattern (I12); both chains rebuilt on key save; `ClearAuthBlock("azure")`. Sally's spec supplies the labels and the "what this costs you" copy. |
| **Files touched** | **New:** `Services/AzureTranslator.cs` · `Services/SettingsService.cs:7-81,165-182` · `MainWindow.xaml` (the DeepL settings block, near the existing key row) · `MainWindow.Translate.cs:226-252` · `MainWindow.xaml.cs` (`ApplySettings`) |
| **Tests added** | T12 Azure array fixture · Azure error mapping: 401 → `AuthFailed`, 403+quota envelope → `QuotaExhausted`, 429 → `RateLimited`, count mismatch → `BadResponse` and **never padded** (I5) · a settings round-trip test through `SettingsService.PathOverride` proving the new fields survive save/load and that `_restoringSettings` prevents the XAML-load clobber (the `StartupSettingsTests` pattern). |
| **Risk** | **Medium.** Not the provider — the endpoint is contracted and documented — but the **settings surface**, which is the code path with the most expensive prior bugs in this repo (two migrations exist solely to undo one). A key without a region is a guaranteed 401, so the UI must validate both together. |
| **Rollback** | Single revert. Saved `AzureApiKey`/`AzureRegion` values become inert but are preserved in `settings.json`. |
| **Size** | **M** |
| **Depends on** | 2 |
| **Definition of done** | A pasted F0 key + region translates through Azure on the write path. `UseKeyForReading` off ⇒ Azure is **not** in the read chain, verified by a test on the built chain. Restarting the app keeps both fields (the v0.12.3 clobber test). U4 answered before the copy promises anything about "free". |

---

## Increment 6 — UX: status, degraded mode, provider indicator

| | |
|---|---|
| **Scope** | Sally's surfaces: the per-`Kind` wording replacing every `«Sally: …»` placeholder; the paused/countdown state on the main window **and** the compact overlay; a provider/degraded indicator; the `Pending retry` row treatment; the "which provider answered" affordance if her spec calls for one; the README/About copy for the new key slot and the offline option. |
| **Files touched** | `MainWindow.xaml`, `Theme.xaml` (only if a new visual state needs a brush), `CompactOverlay.xaml(.cs)`, `MainWindow.xaml.cs:404-410` (wording only), `MainWindow.Live.cs`/`Ocr.cs` status strings, `README.md` |
| **Tests added** | A `TemplateRenderTests` case per new/changed feed template, with a real injected `OcrResultItem` — an `ItemsControl` defers container generation headless and renders nothing. Every new `Run.Text` binding is `Mode=OneWay` (I15); a get-only property bound TwoWay throws once per rendered item. |
| **Risk** | **Low-medium**, and entirely in the XAML: the dark `ToolTip` style in `Theme.xaml` is load-bearing, and `MainTabs_SelectionChanged` must keep its `e.Source is TabControl` filter. |
| **Rollback** | Single revert; the provisional strings from increments 2 and 4 come back. |
| **Size** | **S–M** |
| **Depends on** | 4 (and 5 for the key-slot copy) |
| **Definition of done** | Every `«Sally: …»` placeholder in `architecture-cible.md` §4.4 and §9 has real copy. A user watching only the compact overlay can tell paused from broken from working. All STA render tests pass. |

---

## Increment 7 — Bergamot prototype, with a measured go/no-go

> **Outcome, recorded 2026-09-09 — this increment SHIPPED as v0.15.2.** The plan below is left as written; it is the
> proposal, not a description of the app. What actually happened: E8.S1's measurements returned **GO**, epic E8
> (stories S1–S6) landed on `feature/p2-c-offline` and merged as PR #59, and the owner **waived** the
> target-machine field run (E8.S7) and ordered the release — **ruling E8-h**, `../README.md`. The shipped shape:
> one language pair (**ru→en**, the `tiny` model), a **150 MiB** RAM ceiling while active (**ruling E8-a** — the
> 310 MiB below was the two-model RU↔FR pivot budget), the tier appended **last** in both chains behind
> `OfflineTierIsAvailable`, files downloaded on consent from the `offline-engine-v1` GitHub pre-release and
> verified by size then SHA-256 against `OfflineModelManifest.Shipping` (inside the exe), idle unload after
> 10 minutes. Two guesses below did **not** hold: no build file was touched and `PublishFlagsTests` is unchanged
> (layout C — `ExcludeAssets="native"`, `architecture-cible.md` §7.6 constraint 4), and the download is triggered
> by an explicit About-tab click rather than "on first use".

**Prototype branch. It does not merge unless the measurements say so.**

| | |
|---|---|
| **Scope** | `BergamotTranslator` behind `OfflineFallbackEnabled` (default false), `BergamotTranslatorSharp` referenced **on this branch only**, lazy model load on first fallback use, idle unload, `Task.Run` around the synchronous native call, model download on first use behind explicit consent from a `github.com` mirror (the `UpdateService` allowlist is **not** widened), a RAM-budget check, MPL-2.0 in the About tab. |
| **Files touched** | **New:** `Services/BergamotTranslator.cs` · `PWRUHelper.csproj` (the one new `PackageReference`) · `MainWindow.xaml` (the opt-in + consent dialog) · About tab licence text · possibly `Build Portable EXE.bat` / `Build MSI Installer.bat` / `release.yml` and `tests/PWRUHelper.Tests/PublishFlagsTests.cs:62-70` **if** the DLL must ship beside the exe |
| **Measurements that decide it** | **(1) RAM** — resident delta on the P1-affected machines, not the dev box (U6). Budget: the app's working set is ~150 MB and one model measured **+127 MiB USS, not tunable**. **(2) Load time** — the 103 ms measured must hold on target hardware. **(3) Quality on slang after `SlangGlossary.Expand`** on ≥ 30 real PW-RU lines, compared against the current cloud output. **(4) P1 regression** — cold start with the native DLL bundled vs beside the exe (U7). |
| **Go criteria** | RAM delta ≤ 150 MiB while active **and** it unloads on idle · no measurable cold-start regression · post-glossary quality judged acceptable by the owner on his own chat lines. Any one failing ⇒ **no-go**, and the branch is closed with its numbers recorded in this folder. |
| **Risk** | **High on footprint, low on the rest.** The app's stated virtue is not lagging the game; a 127 MiB resident model is 85 % of the current working set. The native DLL also re-arms the `%TEMP%` extraction mechanism implicated in P1, and MPL-2.0 enters the licence tree. |
| **Rollback** | The branch is simply not merged. Nothing else depends on it. |
| **Size** | **L** |
| **Depends on** | 2 (needs the chain), and preferably 4 |
| **Definition of done** | Either a merged, opt-in, lazily-loaded terminal fallback with all four measurements published — or a closed branch with a documented no-go and the numbers that produced it. Both outcomes are successes. |

---

## Parallel track P — Signing, expectations, checklist (P1)

Runs alongside everything above; it touches no translation code.

| | |
|---|---|
| **P.1 — SignPath Foundation** | Close **PR #49** (CC BY-NC) first — SignPath Foundation requires an OSI licence and the owner has decided the app stays MIT. Then: submit the application (approval takes days to weeks), set up the dashboard, and wire the CI. **Two known traps, both [CONFIRMED]:** the signing block is *not live* — `.github/workflows/release.yml` has zero SignPath steps and the YAML exists only inside `packaging/signpath-signing.md:103-156` — and its `if: ${{ secrets.X != '' }}` step guards are invalid, because `secrets` is not a documented context for `steps.<id>.if`; map them to a **job-level `env:`**. The certificate names *SignPath Foundation* as the publisher, not *Kizotis*. **Size: M** (mostly waiting). **Risk:** an external approval delay, and a per-release CI step that can fail the release pipeline. |
| **P.2 — Expectation-setting text** | Two sentences in the README install section and one line in each release note: the first launch after each update is slower because every release is a new file Windows has never seen. Plus: put the portable exe in a plain local folder, and unblock it after downloading. Sally writes, Paige places. **Size: S. Risk: none** — provided it is never used as a substitute for signing. |
| **P.3 — Release checklist** | Fold into the existing checklist: bump `<Version>` in `PWRUHelper.csproj`, sync `VERSION=` in `Build MSI Installer.bat`, land on `main` via PR, annotated tag, then **verify both artefacts** with `gh release view vX.Y.Z --json name,assets`. Add one line: after a release that touches the translation path, confirm `provider-state.json` and `translation-cache.json` are created lazily and not at startup. **Size: S.** |
| **Not in this track** | The MSI native-DLL option (`architecture-cible.md` §13, item 2) — *optional, low value*, and it would require deliberately weakening `PublishFlagsTests.cs:62-70`. Do not start it without a measurement on an affected machine. |

---

## Validation — how we prove it worked

### V1. Proving P2 is fixed

The point of increment 0 is that this stops being an argument. Evidence, in order of strength:

| # | Evidence | Source | Passing looks like |
|---|---|---|---|
| **V1.1** | **Request count under throttle.** The `burst60` field of the per-request log line during a real 429 episode. | One affected user's "Copy error report" | Today's model predicts ≈ 128 rejected requests/minute in scenario S4 and an indefinite ≈ 3/minute trickle in S4c (`analyse…` §2.3). After: **at most one probe per open window** — 1/60 s, then 1/2 min, 1/4 min, capped at 1/30 min. A `burst60` above ~4 during an open window is a bug. |
| **V1.2** | **The block clears.** The gate transition log showing `OPEN → HALF-OPEN → CLOSED`, with the elapsed time. | Same paste | Google's own release condition is "shortly after those requests stop"; if the app now genuinely stops, the reported "1 min / 10 min / never" spread should collapse toward the short end. A user reporting "never clears" *after* this ships, with a log showing the app was silent throughout, is a **different** finding — most likely a genuinely long server-side block — and must be recorded as such rather than re-attributed to the app. |
| **V1.3** | **No `(Google is limiting…)` row survives recovery.** | The feed, and T16 | Every row that failed during a blip carries a translation once the gate closes. Zero permanently-parenthesised rows in a session that recovered. |
| **V1.4** | **A 429 costs one request, not three.** | The log: `attempt=1/2` with no second attempt on a 429 | T6 pins it in CI; the field log confirms it in the wild. |
| **V1.5** | **Read-once never claims a false success.** | The status line, and the log | `Done — N of M` or the real error, never an unconditional `Done`. |
| **V1.6** | **The About tab can evidence an incident.** | "Copy error report" on a machine that has just hit P2 | Non-empty, and containing status, content-type, `Retry-After` presence, body head, `burst60`, `ipv` and the gate history — with **no user text, no `q=`, no key** (I11). |
| **V1.7** | **Soak (U3).** ~2 req/s for ≥ 2 h on ≥ 2 networks against `dict-chrome-ex`. | Instrumented branch | Zero 429s, or 429s that the gate absorbs into a handful of probes. |

**A negative result is still a result.** If the field logs show the gate opening constantly at the shipped
defaults, the §5.6 constants are wrong (U9) and the answer is to tune them from the data — not to widen the retry
policy back out. That is why they are `const`s in one file with a runtime override hatch.

### V2. Proving the P1 expectation work

P1 has **no code fix** (`architecture-cible.md` §13). What can be proven:

| # | Evidence | Passing looks like |
|---|---|---|
| **V2.1** | **Post-signing cold start on a downloaded exe, on a Defender-default personal machine.** Use `tools/diagnostics/Measure-Startup.ps1`, which already splits pre-process from in-process, and report run #0 (fresh hash) plus warm runs — the protocol in `mesures-protocole.md`. | The *second and subsequent* releases from the same publisher should stop paying the full unknown-file penalty, because reputation now accumulates instead of resetting at every tag. The in-process ~1.5–1.8 s floor will **not** move; do not present it as if it should. |
| **V2.2** | **The same measurement on an unsigned build of the same commit**, on the same machine, same session. | The delta between V2.1 and V2.2 *is* the value of signing. Without this pair the claim is unfalsifiable. |
| **V2.3** | **Reported severity.** | Support messages moving from "it hangs for 10 seconds" to "the first start after an update takes a few seconds" — the same event, a very different support cost. That is what P.2 buys, and it is the only P1 deliverable that ships immediately. |

---

## Story cut for Phase 3 (John)

Epics and story **titles only** — sizing, acceptance criteria and context files are John's to write, from
`architecture-cible.md`.

**Epic A — Diagnosable translation pipeline** *(increment 0)*
1. Inject an `HttpMessageHandler` into every HTTP provider
2. Introduce `TranslationErrorKind` and a typed `TranslationException`
3. Classify every provider outcome in one `ProviderErrorMapper`
4. Detect the HTML abuse page before parsing, never after
5. Emit the per-request diagnostic log line (no user text, no keys)
6. Map `Friendly()` onto `Kind` without changing any wording

**Epic B — Provider gate** *(increment 1)*
7. `ProviderGate`: circuit breaker with strikes, cap and half-open probe
8. `ProviderGate`: rate ceiling with an interactive-priority reserve
9. Persist gate state to `provider-state.json`, lazily and atomically
10. Replace the retry policy: 2 attempts, jitter, no retry on 429/403
11. Honour `Retry-After` when a provider sends one
12. Log gate transitions

**Epic C — Provider chain and the new default endpoint** *(increment 2)*
13. Spike: capture the `dict-chrome-ex` batch and Edge request/response fixtures
14. `ChainTranslator` replacing `FallbackTranslator`
15. `GoogleDictTranslator` as the new default free provider
16. `EdgeTranslator` as the independent second vendor
17. Demote `gtx`: rename to `GoogleGtxTranslator`, extract `TextChunker`
18. Build both chains once, in the constructor, per the target composition
19. Bound the batch-mismatch per-line fallback

**Epic D — Shared persistent cache** *(increment 3)*
20. Extract the LRU into `TranslationCacheStore`
21. Persist the cache lazily and save it debounced
22. Share one store between the read and write decorators

**Epic E — Honest live translation** *(increment 4)*
23. Skip the whole tick and back off while every read tier is paused
24. Fix the auto-stop counter and add the time-window rule
25. Retry failed rows once the providers recover
26. Make read-once report the truth, and make it cancellable

**Epic F — Bring your own key: Azure** *(increment 5)*
27. `AzureTranslator` over raw `HttpClient`
28. Azure key and region settings, re-entrancy-safe
29. "Use my key for screen reading" opt-in, off by default

**Epic G — Degraded-mode UX** *(increment 6)*
30. One message per error kind, with a countdown
31. Paused state on the main window and the compact overlay
32. Pending-retry row treatment, with a render test

**Epic H — Offline fallback prototype** *(increment 7)*
33. Prototype `BergamotTranslator`, lazily loaded and idle-unloaded
34. Measure RAM, load time, slang quality and cold-start impact
35. Go/no-go decision, recorded either way

**Epic P — Packaging and expectations** *(parallel track)*
36. Close PR #49 and confirm the MIT licence
37. Wire SignPath signing into the release workflow, job-level `env` guards
38. README and release-note expectation text
39. Extend the release checklist for the new state files

---

_Companion: `architecture-cible.md`. Nothing in this plan is implemented; Phase 4 starts on the owner's go._
