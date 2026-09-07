# 03 — Test plan (system level) for the translation rebuild and the P1 campaign

_Phase 3 · author: **Murat** (BMAD Master Test Architect, TEA), workflow **TD — Test Design**, system-level mode,
run non-interactively · baseline commit `4759712` = `main` v0.14.0 · 2026-09-06 · status: **proposed — no test code
written**._

**Inputs.** `project-context.md` (testing rules) · `docs/investigations/README.md` (consolidations, owner's
decisions, OQ-A–D answers, **amendment A-1**) · `SYNTHESE.md` §2.5, §3.6, §6 ·
`02-traduction/architecture-cible.md` (§2 invariants I1–I16, §4 error model, §5 gate, §6 chain, §7 providers,
§8 cache, §9 LIVE, §10 observability, §11 testability, §12 settings, §15 U1–U9 + R1–R9) ·
`02-traduction/plan-migration.md` (increments 0–7, track P, "Validation" V1.1–V1.7 / V2.1–V2.3) ·
`02-traduction/ux-mode-degrade.md` §2 (S1–S8), §5 (flows a–f), §7 (OQ-1–12 + 11 acceptance hints) ·
`01-demarrage/recommandations.md` §7 (thresholds) · `01-demarrage/mesures-protocole.md` (C1–C5, E1–E9) ·
`02-traduction/analyse-implementation-actuelle.md` §6.4 (existing suite, gaps 1–4) · the real suite
(`tests/PWRUHelper.Tests/*.cs`, 26 files, ≈256 cases) and the code seams at the baseline.

**This is a C#/WPF .NET 8 desktop app with xUnit.** There is no browser, no Playwright, no Cypress, no k6, no Pact.
"Integration" here means *a real `Services/` object graph driven by a fake `HttpMessageHandler`*, and "E2E" means
*a human on a real machine with a real network* — there is no automatable substitute, which is why §4 exists.

**No test code and no production code was written to produce this document.** Every `file:line` is at the baseline.

---

## 0. Executive summary

| | |
|---|---|
| **Scope** | Increments 0–7 of `plan-migration.md` (Epics A–H) plus parallel track P, and the P1 field campaign. |
| **Automated cases designed** | **145** across 12 component groups (§3), of which 7 are STA render cases. |
| **Manual / field cases** | **2 campaigns** (§4): P1 startup (≥3 machines, 5 core + 4 extended conditions), P2 field + soak; plus the **Bergamot prototype harness** (10 measured checks, never in CI). |
| **Existing cases that must keep passing unchanged** | `CachingTranslatorTests` (7), `LiveDedupTests` (8), `LiveDefaultsTests` (7), `DefaultsAndResizeTests.cs:16,28-32`, `PublishFlagsTests` (3), `TemplateRenderTests` (2), `StartupSettingsTests` (2). |
| **Highest-risk scenarios** | R-01 permanently-paused app · R-02 zombie LIVE indicator · R-03 timeout mistaken for a cancel (the OCE trap) · R-04 settings clobbered on restore · R-05 failure cached / burned row · R-06 `Run.Text` TwoWay throwing per row. |
| **Hard prerequisite** | The isolation requirements of §2. Without them the suite writes into the developer's real `%AppData%` and the static `ProviderGates` leaks across xUnit collections. **These are story acceptance criteria, not test-author conveniences.** |

**Not in scope of this plan.** The provider endpoints themselves (rented third-party services — covered by the
gate's *behaviour under failure*, never by asserting a vendor is up); the Windows security stack (measured in §4.1,
not tested); WiX/MSI packaging beyond `PublishFlagsTests`; OCR accuracy, dedup semantics, phrasebook, squad and
overlay geometry (all already covered and explicitly untouched by I16 and by increments 0–7).

---

## 1. Test strategy

### 1.1 Risk register — what breaks the user most

Score = probability (1–3) × impact (1–3). Categories per the BMad TEA taxonomy: TECH, BUS, OPS, DATA, PERF, SEC.

| ID | Cat | Risk | P | I | Score | Where it comes from | Primary mitigation (tests) |
|---|---|---|---|---|---|---|---|
| **R-01** | BUS | **Permanently paused app.** The gate opens, a wrong window / a persisted `blockedUntil` / a clock jump / a missing half-open probe keeps it open, and translation never comes back. Increment 1 alone against `gtx` produces exactly this (`plan-migration.md` R7). | 3 | 3 | **9** | `architecture-cible.md` R2, R3, R7; U9 constants are all [ASSUMED] | TP-GATE-03/04/06/07/19/21, TP-CHN-04, TP-LIVE-03, §4.2 field logs |
| **R-02** | BUS | **Zombie LIVE indicator.** The heartbeat blinks, the loop is paused or dead, the user waits forever. Today's confirmed behaviour through a whole 429 storm. | 3 | 3 | **9** | `analyse…` §1.5; ux §2.2 reading rules; R6 | TP-LIVE-01/09/17, TP-RENDER-01, ux hint 3 |
| **R-03** | TECH | **A timeout mistaken for a cancel (the OCE trap).** A new decorator (`ChainTranslator`, `HttpProviderCore`, the pending-retry drain) catches `OperationCanceledException` broadly and turns every 12 s timeout into a phantom user-cancel — silently disabling the fallback and leaving a zombie indicator. Cost the project 3 releases once already. | 3 | 3 | **9** | I3; `FallbackTranslator.cs:26,38`; `Live.cs:227`; existing guards `TranslationBackendTests.cs:84,98` | TP-MAP-01/02/17, TP-RET-04, TP-CHN-07/08, TP-LIVE-16 |
| **R-04** | DATA | **Settings clobbered on restore.** A new Azure/region/checkbox handler fires during `InitializeComponent()` and overwrites the saved value. The v0.12.3/v0.13.0 bug class; two migration steps exist only to undo one. | 3 | 3 | **9** | I12; `SettingsService.cs:137-149`; `StartupSettingsTests` | TP-SET-05/06/07, one case **per new control** |
| **R-05** | DATA | **Cached failures / burned rows.** A `(`-prefixed placeholder reaches the on-disk cache, or a row that failed during a blip stays `(…)` for the session and is never retried. | 2 | 3 | **6** | I4; `CachingTranslator.cs:82-85`; `analyse…` §3.1 burned row; V1.3 | TP-CACHE-02/03, TP-LIVE-10/13/14 |
| **R-06** | TECH | **A `Run.Text` TwoWay binding throwing once per rendered row.** The v0.11.2 crash. Every new feed-row state (paused, pending-retry, given-up, chip) is a fresh occasion. | 2 | 3 | **6** | I15; `TemplateRenderTests.cs:14-24` | TP-RENDER-01..07 |
| **R-07** | SEC | **User text, a `q=`, a full URL or an API key reaching the log**, which is pasted to Discord by design. | 2 | 3 | **6** | I11; `MainWindow.xaml.cs:312-322` | TP-LOG-01/02/03/05 |
| **R-08** | TECH | **Static `ProviderGates` state leaking between xUnit parallel collections** → flaky, order-dependent gate tests. | 3 | 2 | **6** | R4; the discipline `StartupSettingsTests` already needs | §2 isolation, TP-GATE-19/20 |
| **R-09** | PERF | **A request storm survives the rebuild** — a batch mismatch fans out to per-line, or the LIVE loop keeps ticking while paused. | 2 | 3 | **6** | I5, §6.3 `PerLineCap`, S4/S4c in `analyse…` §2.3 | TP-CHN-10/11/12, TP-LIVE-01 |
| **R-10** | PERF | **Startup regression.** The gate file or the cache file is touched before first paint, or a 2000-entry cache load costs measurable milliseconds on the UI thread. | 2 | 3 | **6** | I10, G6, U8 | TP-START-01/02/03 |
| **R-11** | BUS | **A false "Done"** over a total read-once failure, inviting a manual retry that feeds the block (amplifier A7). | 3 | 2 | **6** | `Ocr.cs:243-244,296-299`; ux §3.3 | TP-ONCE-01/02/03/04 |
| **R-12** | DATA | **Padding a batch count mismatch**, which once bypassed the fallback and poisoned the cache. | 1 | 3 | **3** | I5; `DeepLTranslator.cs:49-53` | TP-CHN-09, TP-PRV-08 |
| **R-13** | OPS | **The Bergamot prototype ships a P1 regression** — +127 MiB resident, or the native DLL re-arms `%TEMP%` self-extraction. | 2 | 2 | **4** | §7.6, U6, U7, R8 | §4.3, TP-BRG-02/08 |
| **R-14** | OPS | **`PublishFlagsTests` weakened by accident** while wiring SignPath or shipping the Bergamot DLL. | 1 | 3 | **3** | `PublishFlagsTests.cs:44-79`; `recommandations.md` §4 step 7 | §5, TP-BRG-08 |
| **R-15** | BUS | **A user's Azure F0 quota drained by LIVE** because the opt-in arrived pre-ticked or defaulted on. | 1 | 3 | **3** | R9, ux OQ-12 | TP-SET-04/08 |

**Gate rule.** Every score-9 risk needs a passing P0 test before the increment that introduces it may ship. A
score-9 risk with no test is a blocked release, not a waiver.

### 1.2 The pyramid for a code-behind WPF app

There is no MVVM and there will not be (`project-context.md`). That decides the shape of the pyramid: everything
worth asserting must live in `Services/` (I2), and what unavoidably lives in `MainWindow.*.cs` is either extracted
to a pure static helper or verified through the STA host.

| Level | Definition here | Where it runs | Count | Speed budget |
|---|---|---|---|---|
| **L1 — Unit** | One `Services/` class, no I/O, injected clock. Mapper, gate arithmetic, policy constants, parsers, chunker, cache LRU, status/countdown formatters. | headless, parallel | 96 | < 2 s total |
| **L2 — Integration (fake transport)** | A real object graph — `ChainTranslator` over real providers over `HttpProviderCore` over a **fake `HttpMessageHandler`** — plus real gates and a real store pointed at a temp directory. Asserts request counts, attempt counts, ordering, persistence round-trips. | headless, **non-parallel** for anything touching `ProviderGates` | 34 | < 5 s total |
| **L3 — STA render** | The real `ItemTemplate`s rendered through a `ContentControl` with a genuinely injected `OcrResultItem`, plus a real `MainWindow` built against `TempSettings`. | one shared STA thread, `[Collection("WPF")]` | 15 | < 15 s total |
| **L4 — Manual / field** | Multi-machine startup measurement, an instrumented user under a real throttle, the `dict-chrome-ex` soak, the Bergamot harness. | human, real network | §4 | days–weeks |

Two rules that follow, and that the stories must respect:

1. **Push down.** A behaviour that can be asserted at L1 must not be asserted at L3. The LIVE auto-stop counter, the
   countdown granularity and the read-once status string are all *pure functions of an outcome* — extract them
   (`internal static`) rather than driving a window.
2. **An `ItemsControl` renders nothing headless.** Container generation is deferred to the dispatcher; only a
   `ContentControl` applies the template synchronously during `Measure`. `TemplateRenderTests.cs:118-121` already
   says so — every new feed-row case follows that pattern or it is a silent pass.

### 1.3 Priorities

| Priority | Criteria | Runs | Groups |
|---|---|---|---|
| **P0** | Blocks the core journey **and** maps to a score-9 risk **and** has no workaround | every commit | MAP, GATE (breaker + persistence), RET, CHN, LIVE (pause + auto-stop), SET (clobber), RENDER, START |
| **P1** | Important behaviour, score 4–6 risk | every PR | GATE (ceiling), CACHE, PRV, ONCE, LOG, remaining SET |
| **P2** | Edge cases, parser shapes, corrupt-file paths | every PR (they are cheap and headless) | the rest |
| **P3** | Measurement, exploratory, prototype | on demand / per campaign | BRG, §4 |

Because the whole suite is headless and runs in seconds, P0/P1/P2 all execute on every PR — the split exists to
decide **what blocks a merge**, not to build a slow nightly lane this project does not need.

---

## 2. Test isolation requirements — story acceptance criteria

These are not suggestions to the test author. Each one is a **production-code change** a story must land, and a
story that lands the feature without it is not done.

| # | Requirement | Why | Pattern to copy | Story |
|---|---|---|---|---|
| **IS-1** | `ProviderStateStore` / `ProviderGates` must expose `internal static string? PathOverride`. | Otherwise every gate test writes `%AppData%\PWRUHelper\provider-state.json` on the developer's machine and the suite's own runs poison each other. This is the same trap that made the test suite rewrite the developer's `settings.json`. | `SettingsService.cs:95`; `Logging.cs:31` | Epic B / story 9 |
| **IS-2** | `TranslationCacheStore` must take an **explicit path** (ctor arg or a static override). | Same, for `translation-cache.json`. | as IS-1 | Epic D / story 21 |
| **IS-3** | A `TempGateState` / `TempCache` disposable in the suite, mirroring `TempSettings` (`StaTestHost.cs:82-101`): temp subdirectory, set the override in the ctor, null it and delete in `Dispose`. | One place that can never be forgotten. | `StaTestHost.cs:82-101` | test-side, Epic B |
| **IS-4** | `ProviderGates.ResetForTests()` — clears the registry **and** any pending debounced save. | Static state survives between cases. Without the second half, a test's queued save lands during the next test. | new | Epic B / story 7 |
| **IS-5** | A dedicated **non-parallel** xUnit collection (`[CollectionDefinition("Gates")]`) whose fixture calls `ResetForTests()` before and after every case. Anything touching `ProviderGates` joins it. | R-08 / R4. xUnit parallelises collections by default; static state does not tolerate it. | `WpfCollection` at `StaTestHost.cs:73-74` | Epic B |
| **IS-6** | `internal static Func<DateTimeOffset> Clock` on `ProviderGate` **and** on `ProviderGates` (they must share one clock, or a persisted `blockedUntil` will be compared against a different now). | Every window, escalation, clean-reset and half-open assertion. **No test may assert timing with `Task.Delay`.** | new | Epic B / story 7 |
| **IS-7** | An injectable delay for the retry back-off (`Func<TimeSpan, CancellationToken, Task>`, default `Task.Delay`), so the jitter band is asserted on the *requested* delay, not on wall-clock. | A jitter test that sleeps is a flaky test that also costs seconds. | new | Epic B / story 10 |
| **IS-8** | `HttpMessageHandler` ctor overload on **every** HTTP provider (`GoogleDict`, `Edge`, `GoogleGtx`, `Azure`, `DeepL`), `internal`, `handler == null` ⇒ the shared static client. | The suite has **no fake handler at all today** (`analyse…` §6.4 gap 3); the static client at `TranslationService.cs:32` makes 429 simulation impossible. Unlocks gaps 1–3. | new; `InternalsVisibleTo` already granted, `PWRUHelper.csproj:28` | Epic A / story 1 |
| **IS-9** | Recorded HTTP fixtures under **`tests/PWRUHelper.Tests/Fixtures/`**, as embedded resources or `CopyToOutputDirectory` files, one per shape: `google-dict-single.json` (`["Hello"]`), `google-dict-auto.json` (`[["Hello","ru"]]`), `google-dict-batch.txt` (the U1 capture), `google-429.html` (verbatim from `benchmark…` §3.1), `google-captcha.html`, `azure-batch.json`, `azure-error-quota.json`, `azure-error-auth.json`, `edge-request.json` + `edge-response.json` (**U2 capture — until it exists, no `EdgeTranslator` and no Edge tests**), `deepl-batch.json`, `gtx-single.json`. | Frozen evidence beats a hand-typed string, and it is the only way an [UNKNOWN] shape becomes a regression guard. Total budget **≤ 64 KB**. | new | Epics A, C, F |
| **IS-10** | **No real network in CI, ever.** No test may construct a provider without a handler. A guard test enumerates the provider types and asserts each has the `internal` handler overload. | A single forgotten `new GoogleDictTranslator()` turns CI into a client of a rented endpoint — and, given the Phase-1 incident, into a source of 429s. | new | Epic A |
| **IS-11** | The fake handler must record: request count, method, URI (for `q=`-absence assertions), headers, and per-request the response it returned; and it must support a scripted sequence (429, then 200) and a synthetic `TaskCanceledException` **with the caller's token not cancelled**. | R-03 cannot be tested without the last one. | new | Epic A |
| **IS-12** | `ProviderGateOverrides` parsing must be pure and `internal` so a test can drive short windows without waiting. | Otherwise the escalation test needs 30 real minutes. | new | Epic B |

---

## 3. Test design per component

Legend for **Type**: L1 unit · L2 integration (fake transport) · L3 STA render. **Maps** cites the invariant
(I#), the architecture case (T#), the story number from `plan-migration.md`, the UX state (S#) or hint (H#), and
the unknown (U#).

### 3.1 `ProviderErrorMapper` — the single classification point (increment 0, stories 3–4)

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-MAP-01 | Genuine cancel | L1 | ct cancelled / `Classify(...)` / returns `Cancelled` | enum value | I3, §4.2#1 |
| TP-MAP-02 | **Timeout is not a cancel** | L1 | `TaskCanceledException`, ct **not** cancelled / classify / `Timeout` | enum value | **R-03**, I3, §4.2#2 |
| TP-MAP-03 | Transport failure | L1 | `HttpRequestException` (DNS/TLS/proxy) / classify / `Network` | enum | §4.2#3 |
| TP-MAP-04 | 429 | L1 | status 429 / classify / `RateLimited` | enum | T6, S3 |
| TP-MAP-05 | 401 | L1 | status 401 / classify / `AuthFailed` | enum | S7 |
| TP-MAP-06 | 403 + key + quota envelope | L1 | Azure `{"error":{"code":403…"quota"}}` fixture / classify / `QuotaExhausted` | enum | S8 |
| TP-MAP-07 | 403 + key, no quota wording | L1 | classify / `AuthFailed` | enum | S7 |
| TP-MAP-08 | 403, no key | L1 | classify / `Blocked` | enum | §4.2#8 |
| TP-MAP-09 | DeepL 456 | L1 | status 456 / classify / `QuotaExhausted` | enum | S8 |
| TP-MAP-10 | 5xx | L1 | 500/502/503 / classify / `Unavailable` | enum | T7 |
| TP-MAP-11 | **HTML abuse page on a 200** | L1 | `google-429.html` fixture served with status **200** / classify / `RateLimited`, and `JsonDocument.Parse` is **never** reached | enum + a parse-counting spy | **T13**, §4.3, `benchmark…` §3.1 |
| TP-MAP-12 | Captcha page | L1 | `google-captcha.html` / classify / `Blocked` | enum | §4.3 |
| TP-MAP-13 | HTML, no marker | L1 | `<html>…</html>` with no marker / classify / `Blocked`, and the de-tagged 120-char head is available for the log | enum + head string | §4.3, §10.1 |
| TP-MAP-14 | Content-type says JSON, body starts `<` | L1 | classify / HTML path taken (sniff wins) | enum | §4.3 step 2 |
| TP-MAP-15 | Success, unparseable body | L1 | 200 + `not json` / classify / `BadResponse` | enum | §4.2#12 |
| TP-MAP-16 | **403 no longer folded with 400/404** | L1 | 403 / `Blocked`; 400 and 404 / `Unknown` — not the same message | enum pair | **T14**, `00-annexe…` E1 |
| TP-MAP-17 | `Kind.Cancelled` is never constructed | L1 | scan every `throw new TranslationException(` site (source-scan test, like `PublishFlagsTests` reads files) / none passes `Cancelled` | source assertion | I3 contract, §4.1 |

### 3.2 `ProviderGate` + `ProviderGates` (increment 1, stories 7–9, 11–12)

All cases drive `IS-6`'s injected clock. **No `Task.Delay`.**

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-GATE-01 | Opens on `RateLimited` | L1 | closed gate / `ReportFailure(RateLimited)` / state Open, window = `OpenBaseSeconds` (60 s) | `TryEnter` → `Open(retryAt)` | T1, §5.3 |
| TP-GATE-02 | Stays open | L1 | open / `TryEnter` at now+30 s / `Open(retryAt = t0+60 s)` | decision + retryAt | T1 |
| TP-GATE-03 | **Half-opens exactly once** | L1 | open, clock at `blockedUntil` / two concurrent `TryEnter` / first `Probe`, second `Open(retryAt = now + probeTimeout)` | decision pair | **T1, R-01** |
| TP-GATE-04 | Probe success closes | L1 | half-open / `ReportSuccess` / Closed, strikes 0, `blockedUntil` null | state | T1, R-01 |
| TP-GATE-05 | Probe failure re-opens, doubled | L1 | half-open / `ReportFailure(RateLimited)` / Open, window 120 s | retryAt delta | T2 |
| TP-GATE-06 | **Escalation and cap** | L1 | 6 successive opening failures / windows 60 s, 2, 4, 8, 16, **30 min (clamped)** | retryAt sequence | **T2**, R-01 |
| TP-GATE-07 | **Clean reset** | L1 | strikes 3, then `CleanResetMinutes` (10) of clock with successes / next failure opens for **60 s**, not 8 min | retryAt | **T3**, R-01 |
| TP-GATE-08 | `QuotaExhausted` | L1 | / open for `QuotaOpenMinutes` (60), **strikes not escalated**; `ClearAuthBlock`-style clear on key re-save | state + strikes | §5.3, S8, R9 |
| TP-GATE-09 | `AuthFailed` blocks until the key changes | L1 | / `blockedUntil = MaxValue`; `ProviderGates.ClearAuthBlock(id)` closes it | state | §5.3, S7 |
| TP-GATE-10 | Soft cooldowns | L1 | `Unavailable`, `Timeout`, `Network`, `Unknown` / 5 s cooldown, **no strike** | retryAt + strikes | §5.3 |
| TP-GATE-11 | `BadResponse` needs 3 | L1 | 2 `BadResponse` / still closed; the 3rd / open. A success in between resets the soft count | state | §5.3 |
| TP-GATE-12 | `Cancelled` never touches the gate | L1 | / strikes, `blockedUntil`, `cleanSince` unchanged | state snapshot equality | I3, §5.3 |
| TP-GATE-13 | `Retry-After` delta-seconds | L1 | `Retry-After: 120` on a 429 / window = 120 s, **overrides** the computed 60 s | retryAt | **T8**, §5.5 |
| TP-GATE-14 | `Retry-After` HTTP-date | L1 | an RFC-1123 date / same | retryAt | T8 |
| TP-GATE-15 | `Retry-After` clamped | L1 | `Retry-After: 0` and `Retry-After: 86400` / clamped to `[1 s, OpenCapMinutes]` | retryAt bounds | T8, R-01 |
| TP-GATE-16 | Rate ceiling basics | L1 | capacity 2, refill 1 per `MinSpacingMs` (500 ms) / 3rd immediate `TryEnter` / `Wait(t)` with t ≤ 500 ms | decision + t | §5.4 |
| TP-GATE-17 | **Interactive reserve** | L1 | bucket at 1 token / `TryEnter(Background)` / `Wait`; `TryEnter(Interactive)` / `Allow` | decision pair | §5.4 concern #1 |
| TP-GATE-18 | Probe preference | L1 | probe-eligible gate / a `Background` caller defers `ProbeDeferMs` (1 s); an `Interactive` caller inside that second takes the probe | decision + deferral | §5.4 |
| TP-GATE-19 | **Persistence round-trip** | L2 | gate opened / save → `ResetForTests()` → load (via `PathOverride`) / still Open, `blockedUntil` preserved to the ms | file + state | **T4**, I9, R-01 |
| TP-GATE-20 | Corrupt / missing / future-version file | L2 | truncated JSON, absent file, `"version": 99` / empty registry, **no throw**, app usable | no exception + empty state | T4, §5.7 |
| TP-GATE-21 | **Clock-skew clamp** | L2 | a saved `blockedUntil` one week ahead / on load it is clamped to `OpenCapMinutes` | retryAt | **T5**, R-01, R3 |
| TP-GATE-22 | Unknown provider ids preserved | L2 | file contains `"future-provider"` / after a rewrite the entry is still there | file content | §5.7 |
| TP-GATE-23 | Saves are transition-only and coalesced | L2 | 50 `TryEnter` on an open gate / **zero** writes; one open + one close / one coalesced write per transition after the 1 s debounce; `OnClosing` flushes | write counter | §5.7, §10.2 |

### 3.3 Retry policy / `HttpProviderCore` (increment 1, story 10)

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-RET-01 | **429 → no retry** | L2 | fake handler always 429 / one call / **exactly one** request reaches the handler | request count == 1 | **T6**, V1.4, R-09 |
| TP-RET-02 | 403 → no retry | L2 | / exactly one request | count == 1 | §7.0 |
| TP-RET-03 | 503 → 2 attempts | L2 | / exactly two requests, then `Unavailable` | count == 2 | **T7** |
| TP-RET-04 | Timeout → 2 attempts | L2 | handler throws `TaskCanceledException` with ct **not** cancelled / two requests, then `Timeout` — never a cancel | count + Kind | **R-03**, I3 |
| TP-RET-05 | `Network` → no retry | L2 | `HttpRequestException` / **one** request (change from `TranslationService.cs:151`, which retried twice) | count == 1 | §7.0 |
| TP-RET-06 | Jitter band | L1 | via IS-7's injectable delay / the requested delay for attempt *n* lies in `[0, BackoffBaseMs << n]`, and 20 samples are not all equal | bounds + variance | T7, §7.0 |
| TP-RET-07 | Admission before, outcome after | L2 | a gate spy / `TryEnter` is called before the first send; `ReportSuccess`/`ReportFailure(kind, retryAfter)` exactly once after | call order | §7.0 |
| TP-RET-08 | One `cid` per logical call | L2 | a 503 then a 503 / both log lines share the correlation id | log parse | §10.1 |
| TP-RET-09 | Per-request timeout is 12 s | L1 | / the constructed client's `Timeout` is 12 s and `MaxAttempts` is 2 | property assertion | §7.0 |

### 3.4 `ChainTranslator` (increment 2, stories 14, 18, 19)

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-CHN-01 | **Skips open gates at zero cost** | L2 | tier 1 gate open, tier 2 healthy / translate / tier 1's handler **never called**, tier 2's result returned | tier-1 request count == 0 | **T9**, G1 |
| TP-CHN-02 | Order respected | L2 | 3 healthy tiers / tier 1 answers; tiers 2–3 uncalled | counts | §8.1 |
| TP-CHN-03 | Failure moves on | L2 | tier 1 → 503 / tier 2 answers, one result | value + counts | §6.2 |
| TP-CHN-04 | **All paused** | L2 | every tier gate open / one `TranslationException(AllProvidersPaused)` with `RetryAt` = **earliest** skipped retryAt, and **zero** HTTP requests | Kind + RetryAt + total count == 0 | **T10**, S5, H6, R-01 |
| TP-CHN-05 | One honest failure, not a cascade | L2 | tier 1 open, tier 2 → 403 / the thrown Kind is `Blocked` (the real reason), not `AllProvidersPaused` | Kind | §6.2 |
| TP-CHN-06 | Ceiling wait vs skip | L2 | gate returns `Wait(300 ms)` / awaited and the tier is used; `Wait(5 s)` / tier skipped, `now+5 s` joins the skipped set | counts + retryAt | §5.4, §6.2 |
| TP-CHN-07 | **Real cancel propagates** | L2 | ct cancelled, tier 1 throws an OCE bound to it / propagates, tier 2 **never called** | exception type + count | **T11**, I3, inherited `TranslationBackendTests.cs:84` |
| TP-CHN-08 | **Timeout falls through** | L2 | `TaskCanceledException`, ct not cancelled / tier 2 answers | value + count | **T11**, **R-03**, inherited `:98` |
| TP-CHN-09 | 1:1 mismatch is never padded | L2 | Azure/DeepL returns 3 for 4 inputs / `BadResponse`, result list **not** padded | Kind + no padded value | **I5**, R-12 |
| TP-CHN-10 | Join/split mismatch ≤ cap | L2 | Google/Edge returns 2 lines for 5 / per-line fallback, **exactly 5** requests, successes kept when line 4 fails | counts + values | §6.3, OQ-A |
| TP-CHN-11 | **Per-line cap** | L2 | mismatch on **14** lines (> `PerLineCap` 8) / `BadResponse`, **zero** per-line requests | Kind + count == 0 | **T19**, R-09, `analyse…` A11 |
| TP-CHN-12 | `rateLimited` latch keeps its shape | L2 | per-line path, line 3 → 429 / lines 4+ get the skipped placeholder, earlier successes kept | values | I16, `TranslationService.cs:99-105` |
| TP-CHN-13 | Foreign exception classified | L2 | a tier throws `InvalidOperationException` / it is mapped and the chain continues | next tier called | §6.2 |
| TP-CHN-14 | **DeepL is not in the read chain** | L1 | build the read chain with a DeepL key set and `UseKeyForReading` on / no DeepL tier present | chain composition | **I8**, §8.1 |

### 3.5 Provider parsers (increments 2 and 5, stories 15–17, 27)

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-PRV-01 | dict shape A | L1 | `google-dict-single.json` `["Hello"]` (fixed `sl`) / parse / `"Hello"` | value | **T12**, §7.1 |
| TP-PRV-02 | dict shape B | L1 | `google-dict-auto.json` `[["Hello","ru"]]` (`sl=auto`) / parse / `"Hello"` | value | **T12**, §7.1 |
| TP-PRV-03 | dict any other shape | L1 | `{"x":1}` / `BadResponse` | Kind | §7.1 |
| TP-PRV-04 | **dict batch newlines (U1)** | L1 | `google-dict-batch.txt` (the captured 3-line response) / split / **3** lines, in order, content preserved | count + values | **U1**, §7.1, OQ-A |
| TP-PRV-05 | dict 429 body verbatim | L1 | `google-429.html` / `RateLimited` | Kind | T12, `benchmark…` §3.1 |
| TP-PRV-06 | **Edge (U2)** | L1 | `edge-request.json` / `edge-response.json` — **this case and `EdgeTranslator` do not exist until the capture does** | fixture presence is itself the gate | **U2**, §7.2 |
| TP-PRV-07 | Azure array | L1 | `azure-batch.json` / one element per input, order preserved | values | T12, §7.5 |
| TP-PRV-08 | Azure count mismatch | L1 | 2 results for 3 inputs / `BadResponse`, never padded | Kind | I5, R-12 |
| TP-PRV-09 | Azure error envelope | L1 | `azure-error-auth.json` / code logged, the vendor message **never** surfaced raw to the user | log + Friendly output | §7.5, I11 |
| TP-PRV-10 | Azure auto-detect | L1 | `from` omitted / `detectedLanguage` read, still 1:1 | values | §7.5 |
| TP-PRV-11 | gtx parser regression | L1 | `gtx-single.json` / the segment-concatenating shape still parses identically after the rename | value | §7.3, I16 |
| TP-PRV-12 | DeepL parse unchanged | L1 | the three existing cases at `TranslationBackendTests.cs:33-46` / pass, with the ctor updated to carry a `Kind` (`:78`) | existing asserts | §7.4, increment 0 |
| TP-PRV-13 | `TextChunker` moved, not changed | L1 | the two cases at `ServicesTests.cs:80,91` re-pointed / byte budget and content preservation identical | existing asserts | §7.3, story 17 |

### 3.6 Shared persistent cache (increment 3, stories 20–22)

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-CACHE-01 | Decorator contract unmoved | L1 | the 7 `CachingTranslatorTests` cases / **pass unchanged** | existing asserts | §11.3 |
| TP-CACHE-02 | **`(`-prefix rule on disk** | L2 | store a `(rate-limited …)` value / it never appears in `translation-cache.json` | file content | **T17**, **I4**, R-05 |
| TP-CACHE-03 | Successes only, both layers | L2 | mixed successes and placeholders / memory and file hold only successes | map + file | I4 |
| TP-CACHE-04 | Provider recorded | L2 | a value produced by `google-dict` / entry carries `"p":"google-dict"` | file field | §8.2 |
| TP-CACHE-05 | Bergamot entries dropped when disabled | L2 | file has `"p":"bergamot"` entries, `OfflineFallbackEnabled` false / they are not loaded | map contents | §8.2 concern #2 |
| TP-CACHE-06 | Capacity 2000 | L1 | 2001 stores / the LRU tail is evicted | count + absence | §8.2 |
| TP-CACHE-07 | MRU order survives reload | L2 | touch entry A, reload / A is nearest the head and survives the next eviction | eviction order | T17 |
| TP-CACHE-08 | Corrupt file | L2 | truncated JSON / empty cache, no throw | no exception | T17 |
| TP-CACHE-09 | Future version | L2 | `"version": 99` / empty cache, no throw | no exception | §8.2 |
| TP-CACHE-10 | **Lazy load** | L2 | construct the store, never query it / **no file read**, no directory created | file-access spy | **I10**, R-10 |
| TP-CACHE-11 | Load is off the UI thread | L3 | build `MainWindow` under `TempSettings` + `TempCache`, force a miss / the load does not run on the dispatcher | thread id assertion | I10 |
| TP-CACHE-12 | Debounced save | L2 | 20 stores inside `CacheSaveDebounceMs` / **one** write; `SaveNow()` on close flushes immediately | write counter | §8.2 |
| TP-CACHE-13 | **Shared store across decorators** | L2 | the read decorator stores X / the write decorator serves X with **zero** inner calls | inner call count == 0 | **T18** |
| TP-CACHE-14 | Key save does not empty the cache | L2 | `BuildWriteChain()` re-run (as `Translate.cs:239` does) / the store's contents are intact | map size | T18, amplifier A5 |

### 3.7 LIVE loop (increment 4, stories 23–25)

The tick outcome must be extracted as a pure `internal` decision so TP-LIVE-04..08 are L1. Everything that still
needs the window is L2 with a fake capture/OCR/translator triple.

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-LIVE-01 | **Full pause (OQ-B)** | L2 | every read tier gate-open / run 10 ticks / **zero** captures, **zero** OCR calls, **zero** `LiveDedup.Next` calls, **zero** HTTP | four counters all 0 | **OQ-B**, §9.1, **R-09**, H6 |
| TP-LIVE-02 | Back-off ×2, capped | L1 | paused ticks 1..8 / waits are `interval<<k` clamped to `LiveBackoffCapMs` (5000) | wait sequence | §9.1 |
| TP-LIVE-03 | Back-off resets on success | L1 | 5 paused ticks then a tick that translated / `backoffSteps` == 0 | field | §9.1, R-01 |
| TP-LIVE-04 | Counter: success | L1 | tick translated ≥1 line / `consecutiveErrors` → 0, window cleared | value | **T15** |
| TP-LIVE-05 | **Counter: empty tick** | L1 | nothing new to translate / `consecutiveErrors` **unchanged** (today it resets — `Live.cs:219`) | value | **T15**, `analyse…` A2/S4c |
| TP-LIVE-06 | Counter: gate-open tick | L1 | / unchanged — neither success nor failure | value | T15, §9.2 |
| TP-LIVE-07 | Counter: throwing tick | L1 | / `++` and a UTC stamp enqueued, trimmed to `AutoStopWindowMinutes` (2) | value + queue | T15 |
| TP-LIVE-08 | **Auto-stop, both rules** | L1 | 5 consecutive errors → stop; **and** 5 stamps inside 2 min with successes interleaved → stop | stop flag | **T15**, the calm-chat case |
| TP-LIVE-09 | **A pause never auto-stops** | L1 | 100 gate-open ticks / LIVE still running | stop flag false | §9.2, **R-02**, R6 |
| TP-LIVE-10 | **Pending retry after recovery** | L2 | rows fail during a blip, gate closes / on the next tick they are re-translated **in place** — same index, `Glossary` (🔑) preserved, no duplicate row appended | item identity + collection length | **T16**, V1.3, ux OQ-7, flow (a).5 |
| TP-LIVE-11 | Queue bounded and evicted | L2 | > `MaxHistory` (50) failures; a queued row falls out of `_ocrItems` / dropped from the queue | queue size + membership | T16 |
| TP-LIVE-12 | Cleared on stop | L2 | `StopLive()` / queue empty | queue size | T16 |
| TP-LIVE-13 | Terminal after 2 attempts | L2 | drain fails twice / row becomes a terminal `(…)` | row text prefix | §9.3 |
| TP-LIVE-14 | **Pending text is not `(`-prefixed** | L1 | a failed row's body is the pending wording / `IsCacheable` would accept it, so it must **never** be stored — assert the pending body is not `(`-prefixed **and** that no cache store happens on the failure path | string prefix + store counter | §9.3, **I4**, R-05 |
| TP-LIVE-15 | Dedup clock frozen while paused | L2 | pause for 20 ticks, then resume / `LiveDedup`'s internal `_tick` did not advance, so `ReappearAfterFrames` did not expire and an on-screen message is still suppressed | dedup emission | §9.1.3, `LiveDedup.cs:76,145` |
| TP-LIVE-16 | **Timeout ≠ cancel inside the loop** | L2 | the translator throws `TaskCanceledException`, ct not cancelled / the loop counts an error and **keeps running** (does not break out leaving the indicator stuck) | loop alive + counter | **R-03**, `Live.cs:221-227` |
| TP-LIVE-17 | **Heartbeat frozen while paused** | L3 | paused state applied / `LiveIndicator.Text` is the paused form and does not alternate across ticks; the overlay's `UpdateLiveIndicator` freezes on `○` | text across N ticks | **R-02**, ux §2.2, hint 3 |

### 3.8 Read-once honesty (increment 4, story 26)

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-ONCE-01 | All translated | L1 | 5 of 5 / `Done — 5 line(s) translated.` | string | ux §3.3 |
| TP-ONCE-02 | **Partial** | L1 | 3 of 5 / the partial string; **`Done` is unreachable** | string + a negative assert on "Done" | **H4**, R-11 |
| TP-ONCE-03 | None | L1 | 0 of 5 / the none-string with `{reason}` | string | ux §3.3 |
| TP-ONCE-04 | **All paused: no request, no rows** | L2 | every tier open / **zero** HTTP, **zero** `OcrResultItem` created, the paused string shown | counts + collection | §9.4, amplifier A7, S5 |
| TP-ONCE-05 | Real cancellation token | L2 | `TranslateSentencesInto` / receives a real `ct` (not `default` as at `Ocr.cs:295`) with a `ReadOnceBudgetSeconds` (30) cap; `StopLive`, a second read-once and window close each cancel it | cancellation observed | §9.4 |
| TP-ONCE-06 | Re-entrancy guard preserved | L2 | two concurrent read-onces / the second returns immediately, `_readingOnce` respected | call count | `Ocr.cs:212,219,254` |
| TP-ONCE-07 | Outcome is returned, not swallowed | L1 | the method returns `(int Translated, TranslationException? Error)` and the caller branches on it | signature + branch | §9.4, `Ocr.cs:296-299` |

### 3.9 Settings (increment 5, stories 28–29; increment 6, the update toast)

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-SET-01 | New fields round-trip | L1 | `SettingsService.PathOverride` → save → load / `AzureApiKey`, `AzureRegion`, `UseKeyForReading`, `OfflineFallbackEnabled`, `ProviderGateOverrides` survive | values | §12 |
| TP-SET-02 | **No `SettingsVersion` bump** | L1 | an old file at version 3 / loads with the new fields at their defaults and the stored version **unchanged** — `Migrate` gains no step for these | version + values | **I13**, §12 |
| TP-SET-03 | `Sanitize` null-guards | L1 | a hand-edited file with `null` key/region / `""`; region trimmed and lower-cased | values | §12, `SettingsService.cs:165-182` |
| TP-SET-04 | Defaults are off | L1 | fresh install / `UseKeyForReading == false`, `OfflineFallbackEnabled == false` | values | R9, R-15 |
| TP-SET-05 | **Startup does not clobber — one case per new control** | L3 | `TempSettings` holding non-default values for each new persisted control / construct `MainWindow` / the file on disk is byte-identical for those keys | file re-read | **R-04**, **I12**, `StartupSettingsTests.cs:36-53` |
| TP-SET-06 | `_restoringSettings` guard present | L3 | during `ApplySettings`, each new change-handler is entered / it returns before writing | write spy | I12, ux §4.3 |
| TP-SET-07 | UI side effects applied explicitly | L3 | after `ApplySettings` / `UpdateEngineStatusUi()` has run, exactly where `UpdateOcrFilterUi` is called (`MainWindow.xaml.cs:193`) | UI state | I12 |
| TP-SET-08 | **Opt-in gates the read chain** | L1 | Azure key set, `UseKeyForReading` **off** / Azure is **not** a tier of the built read chain; **on** / it is the first tier | chain composition | R-15, story 29 |
| TP-SET-09 | Key save clears the auth block | L2 | gate `azure` open with `AuthFailed`, key re-saved / `ClearAuthBlock("azure")` called and **both** chains rebuilt | gate state + chain identity | §12 |
| TP-SET-10 | **`LastRunVersion` toast fires once** | L1 + L3 | `Migrate` seeds `LastRunVersion` with the **current** version (no spurious toast on the release that ships it); a version change / exactly one toast; the next launch / none; compact mode / **suppressed** | flag + toast count | ux §3.8, H9 |
| TP-SET-11 | `ProviderGateOverrides` is a safe hatch | L1 | unparseable JSON / ignored, `TranslationPolicy` defaults used, no throw | policy values | §12, R2 |
| TP-SET-12 | **`Test key` — DeepL spends nothing** | L2 | a `:fx` key / exactly one `GET api-free.deepl.com/v2/usage`, the key in the `Authorization` header and nowhere in the URI; a paid key / `api.deepl.com` | recorded request | **E6-b**, E6.S5 AC 3 |
| TP-SET-13 | **`Test key` — Azure spends five characters, once** | L2 | / exactly one `POST …/translate?api-version=3.0&from=en&to=ru` with body `[{"Text":"hello"}]` and both headers | recorded request | E6-b, §7.5 |
| TP-SET-14 | **A spent DeepL allowance is read off `/usage`** | L2 | `character_count == character_limit` on a 200 / the quota row, carrying the counts it was read from; a non-positive `character_limit` / no limit claimed at all | result Kind + string | E6-b, §5.3 |
| TP-SET-15 | **A paused provider's test says it is paused** | L2 | the gate is open / zero requests for both providers, and the pause sentence (asserted on DeepL) | request count + string | E6-b, §5.4 |
| TP-SET-16 | **A key test never clears a gate** | L2 | a 401 test, then a second press / the `AuthFailed` row is still there and nothing was sent | gate snapshot | **E2-i**, E2-a |
| TP-SET-17 | **The §3.7 outcomes that SHIP, one case each** | L1 | each shipped outcome / the deck's sentence verbatim, `{region}` interpolated from a value the app never offers. The wrong-region and `cleared` rows are deliberately absent — see E6.S5's Completion Notes | string equality | AC 2, UX-DR19 |
| TP-SET-18 | **The in-flight state always comes back** | L3 | a probe that throws, and one cut by the budget / label and `IsEnabled` restored, the sentence in the status line, no `MessageBox` named in the file | UI state + source scan | AC 1, I3 |
| TP-SET-19 | **A key test is bounded by a whole logical call, and a save supersedes it** | L1 + L3 | the budget vs `RequestTimeoutSeconds × MaxAttempts`; a save landing mid-test / the late answer writes nothing and the button still comes back | policy value + UI state | E6.S5 review, §5.4 |

### 3.10 Observability (increment 0, story 5; increment 1, story 12)

All through `Logging.DirectoryOverride` (`Logging.cs:31`) — the suite already has that discipline
(`LoggingTests.cs:89,101`).

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-LOG-01 | **No user text** | L2 | translate a distinctive sentinel string that fails / the sentinel appears **nowhere** in the log | substring absence | **I11**, **R-07** |
| TP-LOG-02 | **No `q=`, no full URL** | L2 | / the log contains the host+path (`clients5.google.com/translate_a/t`) but no query string | regex | I11 |
| TP-LOG-03 | **No API key** | L2 | a DeepL and an Azure key are set and sent / neither value appears in the log or in "Copy error report" | substring absence | I11, R-07 |
| TP-LOG-04 | Field set complete | L2 | a 429 / the line carries `provider ep dir attempt cid status elapsed retry-after ct len ipv hdrs bytes lines burst60` | field parse | §10.1 |
| TP-LOG-05 | `body` only for non-JSON, de-tagged, ≤120 chars | L2 | the HTML fixture / `body="…"` present, no tags, length ≤ 120; a JSON error / no `body` field | field parse | §10.1, R-07 |
| TP-LOG-06 | Successes are counted, not logged | L2 | 50 successful requests / zero per-request lines | line count | §10.1, log 1 MB cap |
| TP-LOG-07 | Gate transitions only | L2 | one open + 200 skipped calls + one close / **3** gate lines (OPEN, HALF-OPEN, CLOSED), not 202 | line count | §10.2 |
| TP-LOG-08 | Millisecond timestamps | L1 | `LogWriter.Write` (`Logging.cs:85`) / the timestamp format carries `.fff` | regex | §10.1 |
| TP-LOG-09 | The error report evidences P2 | L2 | a scripted 429 episode / "Copy error report" contains both attempt lines, the status, `retry-after`, the content-type, the body head, `burst60`, `ipv` and the full gate history | report content | **V1.6**, §10.3 |

### 3.11 STA render — the new feed-row and status states (increment 6, story 32)

Every case uses a **real injected `OcrResultItem`** through a `ContentControl`, per `TemplateRenderTests.cs:118-121`.

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-RENDER-01 | Pending-retry row | L3 | an item whose `TranslationBody` is the pending wording / both templates (`window.OcrResults.ItemTemplate`, `compact.FeedItems.ItemTemplate`) render with **zero** binding errors and the text reaches the visual tree | `BindingErrorListener` + text | **I15**, **R-06**, T16 |
| TP-RENDER-02 | Given-up row | L3 | a terminal `(…)` row (and its own template, if Sally's spec gives it one) / same | same | I15, S5 |
| TP-RENDER-03 | Provider chip | L3 | the chip `TextBlock`/`Run` in each of its three placements / renders in every S1–S8 state with no binding error | same | ux §2.3, S1–S8 |
| TP-RENDER-04 | **Every new `Run.Text` is `Mode=OneWay`** | L3 + source | a source scan of the feed templates asserts no `Run.Text` binding lacks `Mode=OneWay` | XAML scan | **I15**, **R-06** |
| TP-RENDER-05 | Existing templates unchanged | L3 | the two existing cases (`TemplateRenderTests.cs:28,71`) / pass unchanged, read-once still framed more heavily | existing asserts | §11.3 |
| TP-RENDER-06 | **No countdown in a row** | L3 + source | render 50 rows in a paused state / no row contains a countdown string; the countdown lives only in the chip/status | text scan | ux H2, principle 5 |
| TP-RENDER-07 | Countdown formatter | L1 | the pure formatter: ≤ 90 s → `m:ss` per second; > 90 s → `about N min`, a new string only when N changes; < 5 s → `about to retry`; at the cap → `about 30 min` | string sequence | ux §2.4, H7 |

### 3.12 Startup guard (I10 / G6 — every increment)

| ID | Scenario | Type | Given / When / Then | Oracle | Maps |
|---|---|---|---|---|---|
| TP-START-01 | **Nothing new before first paint** | L3 | construct `MainWindow` with `TempSettings` + `TempGateState` + `TempCache` / **before** `Loaded` fires, neither `provider-state.json` nor `translation-cache.json` exists and neither directory was created | file existence + a directory-creation spy | **I10**, **R-10**, G6, plan track P.3 |
| TP-START-02 | No gate on the startup path | L3 + source | / `ProviderGates`' static initialiser has not run at first paint; a source scan finds no `ProviderGates` reference in the ctor/`ApplySettings`/`OnWindowLoaded` chain | static flag + source scan | increment 1 DoD |
| TP-START-03 | Cache load cost (U8) | L2 | a 2000-entry file / the lazy load is timed and the number is recorded in the PR; capacity is the knob if it is not a few ms | stopwatch, reported not asserted | **U8**, G6 |
| TP-START-04 | A persisted pause is visible before any user action | L3 | a saved open gate / the chip shows the paused state at first paint, with **zero** requests issued | UI text + request count == 0 | ux OQ-8, flow (f).4 |

### 3.13 Bergamot prototype harness (increment 7 — **never in CI**, §4.3)

| ID | Measurement | Threshold / oracle | Maps |
|---|---|---|---|
| TP-BRG-01 | Model init time, on ≥2 **P1-affected** machines | the 103–119 ms measured on the dev box must hold on target hardware | U6, go-criterion 2 |
| TP-BRG-02 | **Resident RAM delta while active** | **≤ 150 MiB**, and it must be released on unload | U6, go-criterion 1, R-13 |
| TP-BRG-03 | Per-line latency | comparable to the 6.5–12.1 ms/line measured | U6 |
| TP-BRG-04 | Quality on **≥ 30** real PW-RU slang lines, **with and without** `SlangGlossary.Expand` | the un-expanded set is expected to be unusable (`данж`→"dangling"); the expanded set must be judged acceptable **by the owner on his own chat lines** | **I6**, go-criterion 3 |
| TP-BRG-05 | **Kept loaded while LIVE runs** (amendment A-1) | the model is not unloaded between ticks while LIVE is on | README A-1 |
| TP-BRG-06 | Unload timing | unloaded after `IdleUnloadMinutes` (10) once LIVE has stopped; RSS returns to baseline | §7.6.2, A-1 |
| TP-BRG-07 | One-click install / remove | consent dialog → download with progress → `Remove` deletes every file; the download URL is on the `github.com` allowlist and the allowlist is **not** widened | §7.6.5-6, ux §3.6, OQ-9 |
| TP-BRG-08 | **U7 — `%TEMP%` extraction** | publish with the DLL bundled and beside the exe; compare `%TEMP%\.net\PWRUHelper` contents and cold start. `PublishFlagsTests.cs:62-70` must not be weakened silently | **U7**, R-13, R-14 |
| TP-BRG-09 | Failure modes | *model not downloaded* and *init failed* both return a `(`-prefixed placeholder ⇒ nothing is cached | I4, §7.6.7 |
| TP-BRG-10 | Offline output not served when disabled | entries with `"p":"bergamot"` are dropped on load when the setting is off | §8.2 concern #2, TP-CACHE-05 |

---

## 4. Manual and field validation campaigns

Nothing in this section can run in CI. Each campaign states the protocol, the machines, the thresholds and what to
collect, so a result is comparable rather than anecdotal.

### 4.1 Campaign P1 — startup, signing, expectations

**Protocol.** `tools/diagnostics/` exactly as written: `Get-MachineSheet.ps1` first, then `Measure-Startup.ps1`.
Conditions **C1–C5** on every machine; **E1** (`Unblock-File`), **E2** (portable vs MSI), **E4** (airplane mode)
and **E3** (plain local folder vs OneDrive-synced) where they apply (`mesures-protocole.md` §3).

**Machines.** ≥ 2 **affected** machines and ≥ 1 fast machine as a documented control — all **personal,
Defender-only, non-corporate**, each running a copy **downloaded from GitHub** so it carries Mark-of-the-Web. A
locally built exe cannot exercise SmartScreen or BAFS and is not a reproduction
(`recommandations.md` §7; `mesures-resultats-dev-box.md` §4.4).

| ID | Run | Success threshold | Source |
|---|---|---|---|
| **FV-P1-01** | `pre_process_ms`, first launch of a freshly downloaded release, median of ≥ 3 runs, on ≥ 2 affected machines — **after signing** | **≤ 500 ms** (today: [UNKNOWN] there; 2163–2528 ms on the dev box for a fresh hash) | `recommandations.md` §7.4 |
| **FV-P1-02** | `total_ms`, same run | **≤ 3.0 s** (reported today: 6–10 s) | §7.4 |
| **FV-P1-03** | `total_ms`, warm second launch of the same build | **≤ 2.0 s** — i.e. no regression | §7.4 |
| **FV-P1-04** | `in_process_ms` | **unchanged** (1477–2256 ms). A *rise* means packaging broke something. Do **not** present this as a number that should improve | §7.4 |
| **FV-P1-05** | SmartScreen on a fresh download | absent, **or** acknowledged as still present and expected during the reputation ramp | §7.4 |
| **FV-P1-06** | **The signed/unsigned pair** — the same commit, the same machine, the same session | the delta **is** the value of signing; without the pair the claim is unfalsifiable | V2.1–V2.2 |
| **FV-P1-07** | Re-run ≈ **4 weeks** after the first signed release | reputation accrues over weeks; a single post-release measurement understates it | §7.2, A4.4 |
| **FV-P1-08** | Before/after `Unblock-File`; portable vs MSI; airplane mode | any of the three showing **no** difference falsifies the BAFS/SmartScreen line of reasoning and hands the case to D1 or F1. **Run these three before committing weeks to signing** | §7.4, `mesures-protocole.md` E1/E2/E4 |
| **FV-P1-09** | `Get-AuthenticodeSignature` on the downloaded exe, the MSI, **and the exe inside an MSI install** | `Status = Valid`, subject **SignPath Foundation** — the third one is what proves the sign-then-build order worked | `recommandations.md` §4 step 9 |

**Collect, per run:** the CSV row (`pre_process_ms`, `in_process_ms`, `total_ms`, extraction-dir state, result) plus
the machine-sheet diff fields of `recommandations.md` §7.3 — in particular `AntivirusSignatureVersion` (a signature
update between runs invalidates Defender's per-file verdict cache and therefore invalidates a cold/warm
comparison), MOTW presence, install kind, and `%TEMP%\.net\PWRUHelper` size captured **before** the timed launch.

**Third deliverable, immediate and non-numeric (V2.3):** support messages moving from *"it hangs for 10 seconds"*
to *"the first start after an update takes a few seconds"*. That is what the expectation copy buys, and it is the
only P1 deliverable that ships without waiting for SignPath.

### 4.2 Campaign P2 — instrumented field logs and the soak

| ID | Run | Protocol | Passing looks like | Source |
|---|---|---|---|---|
| **FV-P2-01** | **Request count under throttle** | one affected user runs the instrumented build; during a real 429 episode they use "Copy error report" | `burst60` **≤ ~4** during an open window. Today's model predicts ≈128 rejected req/min (S4) and an indefinite ≈3/min trickle (S4c). A `burst60` above ~4 while a gate is open **is a bug** | V1.1 |
| **FV-P2-02** | **Requests while paused = 0** | same paste, cross-read with the gate transition lines | zero per-request lines between OPEN and HALF-OPEN | V1.1, OQ-B |
| **FV-P2-03** | **The block clears** | same paste | `OPEN → HALF-OPEN → CLOSED` with elapsed time; the reported "1 min / 10 min / never" spread collapses toward the short end. **A user still reporting "never clears" with a log proving the app was silent throughout is a *different* finding** — a genuinely long server-side block — and must be recorded as such, never re-attributed to the app | V1.2 |
| **FV-P2-04** | **No `(Google is limiting…)` row survives recovery** | watch a feed through a blip and a recovery | zero permanently-parenthesised rows in a session that recovered | V1.3, TP-LIVE-10 |
| **FV-P2-05** | A 429 costs one request | the log shows `attempt=1/2` with **no** second attempt on a 429 | pinned by TP-RET-01 in CI, confirmed in the wild | V1.4 |
| **FV-P2-06** | Read-once never claims a false success | the status line and the log | `Done — N of M`, or the real error | V1.5 |
| **FV-P2-07** | **U3 soak on `dict-chrome-ex`** | ~**2 req/s for ≥ 2 h**, on a branch, on **≥ 2 networks** — **only from a connection the owner designates**. **Never from the dev box, which is currently 429 and must send nothing further to Google** (Phase-1 incident, README) | zero 429s, or 429s the gate absorbs into a handful of probes | U3, V1.7 |
| **FV-P2-08** | **Release-note expectation copy present** | check the release body and the README install section | the two sentences of `ux-mode-degrade.md` §3.8 present, verbatim, on **every** release | P.2, story 38 |

**A negative result is still a result.** If the field logs show the gate opening constantly at the shipped
defaults, the §5.6 constants are wrong (U9) and the answer is to **tune them from the data** — never to widen the
retry policy back out. That is precisely why they are `const`s in one file with the `ProviderGateOverrides` hatch.

### 4.3 Campaign — Bergamot go/no-go

Run TP-BRG-01..10 on the **P1-affected machines** during the P1 campaign (one visit, two datasets). **Any single
go-criterion failing is a no-go**, and the branch is closed with its numbers recorded in this folder. Both outcomes
are successes (`plan-migration.md` increment 7 DoD).

---

## 5. CI implications

| # | Concern | Position |
|---|---|---|
| **CI-1** | **Headless safety** | Non-negotiable. `.github/workflows/ci.yml:24` runs `dotnet test tests/PWRUHelper.Tests -c Release` on `windows-latest` on every PR. Every new case must pass there with no display, no network and no `%AppData%` write. |
| **CI-2** | **No network, ever** | IS-10. A provider constructed without a handler is a CI-breaking defect, not a slow test. The guard test enumerates provider types and asserts the `internal` handler overload exists. |
| **CI-3** | **No `Task.Delay` timing assertions** | Every window, escalation, cooldown and jitter assertion uses the injected clock (IS-6) or the injected delay (IS-7). A test that sleeps is both flaky and slow; flakiness is the tech debt this project can least afford. The single exception — TP-START-03 — is a **measurement that reports**, not an assertion that fails. |
| **CI-4** | **STA runtime** | The `WPF` collection is serialised on one STA thread by construction (`StaTestHost.cs:73-74`). Adding 8 render cases to the existing 4-ish is roughly **+3–6 s**; budget **< 15 s** for the whole collection. If it grows past that, extract more logic to L1 rather than parallelising — a second `Application` throws. |
| **CI-5** | **Static state** | The new `Gates` collection (IS-5) must be non-parallel and reset in its fixture. Two non-parallel collections (`WPF`, `Gates`) is the cost of two static facades; it is accepted and bounded. |
| **CI-6** | **Fixture size** | ≤ 64 KB total under `tests/PWRUHelper.Tests/Fixtures/`. Fixtures are checked in as source (they are evidence). Nothing binary, nothing generated at test time. |
| **CI-7** | **`PublishFlagsTests` is untouched unless packaging changes** | `PublishFlagsTests.cs:44-79`. Two rules carry into this work: the SignPath wiring must edit `.github/workflows/release.yml` **and** `packaging/signpath-signing.md` in the same PR (`:72-79`), and `-p:EnableCompressionInSingleFile` must never be written into either file **even as an example** (`:44-60`). The Bergamot DLL-beside-the-exe option is the only thing in scope that could legitimately weaken `:62-70`, and only with a measurement behind it. |
| **CI-8** | **Never in CI** | Real HTTP to any provider; the Bergamot **model download** and the native DLL; the soak (FV-P2-07); anything reading the developer's or the runner's real `%AppData%`. |
| **CI-9** | **Suite runtime budget** | The whole suite stays **under ~30 s** on the runner. It is ≈256 cases today and lands at ≈400; there is no reason for it to become slow, and no nightly lane is proposed. |
| **CI-10** | **No new test dependency** | xUnit 2.9.2, Test SDK 17.11.1, runner 2.8.2 (`PWRUHelper.Tests.csproj:13-15`). No Moq, no FluentAssertions, no Polly, no WireMock — the fake handler is 30 lines and the existing `Fake`/`CountingTranslator` doubles (`TranslationBackendTests.cs:51-62`, `CachingTranslatorTests.cs:10-28`) are the templates. |

---

## 6. Traceability

### 6.1 Invariants I1–I16 → tests

| Invariant | Covered by | Gap? |
|---|---|---|
| **I1** `ITranslator` shape unchanged | TP-CHN-01..14 (all drive the two methods), TP-CACHE-01 | a source-scan case asserting the interface has exactly two members would make it explicit — **recommended** |
| **I2** `Services/` UI-free | every L1/L2 case runs headless with no WPF type; TP-RENDER-07 keeps the countdown formatter pure | covered structurally |
| **I3** OCE filtered on `ct.IsCancellationRequested` | **TP-MAP-01, TP-MAP-02, TP-MAP-17, TP-RET-04, TP-CHN-07, TP-CHN-08, TP-LIVE-16** | covered — the densest cluster in the plan, by design (R-03) |
| **I4** only successes cached | TP-CACHE-02, TP-CACHE-03, TP-LIVE-14, TP-BRG-09 | covered |
| **I5** batch mismatch never padded | TP-CHN-09, TP-CHN-11, TP-PRV-08 | covered |
| **I6** slang expansion upstream | TP-BRG-04; **no automated case exists** for "the displayed original and the 🔑 line stay raw" on the new chain | **GAP-1** — add an L2 case asserting the cache key is the expanded text while `OriginalBody`/`Glossary` are raw |
| **I7** per-message source selection | **no new case**; `Live.cs:306-310` is untouched by design and §9.5 rejects merging | **GAP-2** — add a cheap L1 pin so a future story cannot merge the groups silently |
| **I8** DeepL unreachable from the read path | TP-CHN-14 | covered |
| **I9** gate state process-global and persisted | TP-GATE-19, TP-GATE-21, TP-CACHE-13 (shared-store analogue), TP-START-04 | covered |
| **I10** nothing new before the window | TP-START-01, TP-START-02, TP-CACHE-10, TP-CACHE-11 | covered |
| **I11** no user text / key / `q=` in the log | TP-LOG-01, TP-LOG-02, TP-LOG-03, TP-LOG-05, TP-PRV-09 | covered |
| **I12** settings restore re-entrancy | TP-SET-05, TP-SET-06, TP-SET-07 | covered — **one case per new control**, not one for all of them |
| **I13** changed defaults need `SettingsVersion` + `Migrate` | TP-SET-02, TP-SET-10 | covered |
| **I14** no WPF `Clipboard` | not touched by this work; the existing suite guards it | out of scope, stated |
| **I15** new `Run.Text` is `Mode=OneWay` + a render case | TP-RENDER-01..06 | covered |
| **I16** the eleven LIVE behaviours kept | TP-LIVE-15, TP-CHN-12, TP-PRV-11, plus `LiveDedupTests`/`LiveDefaultsTests`/`DefaultsAndResizeTests` passing **unchanged** | covered |

### 6.2 Unknowns U1–U9 → exit criteria

| U# | Settled by | Exit criterion |
|---|---|---|
| **U1** dict newline batching | the increment-2 spike capture, frozen as `google-dict-batch.txt` | **TP-PRV-04 exists and passes before any batching code ships.** If it cannot, OQ-A's per-line path (TP-CHN-10) is the shipped path |
| **U2** Edge shapes | the increment-2 spike capture | **`EdgeTranslator` and TP-PRV-06 do not exist until the fixture does.** No code against a guessed body |
| **U3** dict under sustained load | FV-P2-07 soak | ≥ 2 h at ~2 req/s on ≥ 2 designated networks; zero 429s or gate-absorbed 429s |
| **U4** Azure F0 permanence | check a real F0 resource | the settings copy must not say "free forever" until it is verified (ux §3.7) |
| **U5** DeepL `:fx` keys after July 2026 | one user or a support ticket | if dead, the DeepL row must say so; the behaviour (`AuthFailed` → gate open) is already correct and covered by TP-GATE-09 |
| **U6** Bergamot on slow machines | TP-BRG-01..03 during the P1 visit | go-criteria 1–2 |
| **U7** DLL beside the exe vs `%TEMP%` | TP-BRG-08 | no measurable cold-start regression, and `PublishFlagsTests` not silently weakened |
| **U8** cache load cost | TP-START-03 | measured and recorded in the PR; capacity is the knob |
| **U9** policy windows | FV-P2-01..03 field logs | they ship instrumented; tuned from data, never by widening the retry policy |

### 6.3 UX states S1–S8 → tests

| State | Automated | Manual |
|---|---|---|
| **S1** Healthy | TP-CHN-02, TP-RENDER-03 | flow (a).1 |
| **S2** Degraded — fallback active | TP-CHN-01, TP-CHN-03, TP-RENDER-03; **H5**: a successful fallback produces no error text anywhere | flow (a).1 |
| **S3** Paused until | TP-GATE-02, TP-RENDER-03, TP-RENDER-07 | flow (a).2–3 |
| **S4** Offline model active | TP-BRG-05, TP-CACHE-05 | flow (d).6 |
| **S5** All paused | **TP-CHN-04, TP-LIVE-01, TP-ONCE-04**, TP-RENDER-02 | flow (b) |
| **S6** No network | TP-MAP-03, TP-GATE-10; **no automated case yet** for "LIVE does not auto-stop on `Network` and keeps OCR-ing" — that is a deliberate divergence from a provider pause (ux flow (e).3) | **GAP-3** |
| **S7** Key invalid | TP-MAP-05, TP-MAP-07, TP-GATE-09, TP-SET-09 | flow (c).4 |
| **S8** Quota exhausted | TP-MAP-06, TP-MAP-09, TP-GATE-08 | flow (c) |

UX acceptance hints (`ux-mode-degrade.md` §7): H1→TP-MAP-01/TP-MAP-17 · H2→TP-RENDER-06 · H3→TP-LIVE-17 ·
H4→TP-ONCE-02 · H5→S2 row above · H6→TP-CHN-04/TP-LIVE-01 · H7→TP-RENDER-07 · H8→TP-SET-05 · H9→TP-SET-10 ·
H10/H11 (no HTTP codes in copy; each string exists exactly once) → **GAP-4**, a source-scan case is the cheap way.

---

## 7. Gaps, and questions for John and Winston

| # | Gap / question | Owner | Why it matters |
|---|---|---|---|
| **GAP-1** | I6 has no automated guard on the new chain: nothing asserts that the cache key is the *expanded* text while the displayed original and the 🔑 line stay raw. | John (story 20/22 AC) | A future story could cache the raw text and silently halve the hit rate, or show expanded text as the original. |
| **GAP-2** | I7 has no pin. §9.5 rejects merging the `ru`/`auto` groups with arithmetic, but nothing fails if someone merges them. | John | The rejection is reasoned; without a test it is a comment. |
| **GAP-3** | S6 says LIVE keeps reading the screen on `Network` and retries on a slow cadence — the opposite of the gate-open full pause (OQ-B). **The architecture does not specify that split.** §9.1 says a gate-open tick skips capture; `Network` gives a 5 s soft cooldown, not an open gate, so the two documents may already agree — but no one has said so. | **Winston** | It decides whether TP-LIVE-01's "zero captures" assertion is universal or conditional. As specified today I cannot write it unambiguously. |
| **GAP-4** | UX hints H10/H11 ("no HTTP code, no provider internal, no bare 'error' in any user string"; "every string exists exactly once") are testable only as a source scan over the copy deck. That means the copy deck must exist as a **single `const` table**, not as inline literals. | **Winston / John** | Sally's §3 is explicit that ten ad-hoc strings is what E1–E10 cost. A one-table shape makes it a test; scattered literals make it a wish. |
| **OQ-a** | **`RequestPriority` at the call sites.** §5.4 defines the enum, but no document says which caller passes `Interactive`. The Translator tab and the overlay quick reply are obviously interactive; **is read-once `Interactive` or `Background`?** It is user-initiated but it is on the read path. | **Winston** | TP-ONCE-04 and TP-GATE-17 assert opposite things depending on the answer. |
| **OQ-b** | **Half-open probe accounting.** When a probe fails, does it consume a rate-ceiling token? And can a probe be issued while the bucket is empty? | Winston | TP-GATE-18 and TP-GATE-16 interact; unspecified, they are two plausible test suites. |
| **OQ-c** | **`ProviderGates` state-change notification** (ux OQ-4 asks for an event, not polling). If it does not exist, the chip must poll — which is the cost this rebuild removes. It is also the seam that makes TP-RENDER-03 and TP-START-04 writable without a timer. | **Winston** | Currently unanswered in `architecture-cible.md`. |
| **OQ-d** | **Key validation without spending quota** (ux OQ-6). If DeepL `/usage` and an Azure `/languages`-style probe are not usable, `Test key` must say `Test key (uses a few characters)` — and its test asserts a different thing. | Winston | Blocks the TP-SET-09 family's wording. |
| **OQ-e** | **`LastRunVersion` seeding.** Sally specifies `Migrate` seeds it with the *current* version, which **is** a `Migrate` step — but §12 states `SettingsVersion` is **not** bumped and `Migrate` gains no step. These two are in direct conflict. | **Winston / John** | TP-SET-02 and TP-SET-10 cannot both pass as written. Resolve before Epic G. |
| **OQ-f** | **Test-key requests and the gate.** Does a `Test key` request go through `HttpProviderCore` (and therefore report to the gate, and therefore possibly open it on a bad key), or around it? | Winston | Decides whether a user testing a wrong key three times pauses their own provider. |
| **GAP-5** | **No affected machine has ever been measured.** Every P1 number in this plan comes from a dev box its own report calls unrepresentative. Campaign P1 is blocked on one volunteer with one downloaded exe. | Owner | `recommandations.md` §8 item 1; it conditions FV-P1-01..09 and TP-BRG-01..03. |
| **GAP-6** | **The dev box must send nothing further to Google** (Phase-1 incident). FV-P2-07's soak therefore needs a **designated** connection that is not this one, and the U1/U2 captures need the owner's explicit go for those two requests. | Owner | Ground rule 2; `plan-migration.md` increment 2 prerequisite spike. |

---

## 8. Entry and exit criteria

**Entry (before any test authoring starts on an increment).**

- [ ] The isolation requirements of §2 that the increment needs are landed (IS-8 and IS-11 for increment 0; IS-1, IS-4, IS-5, IS-6, IS-7, IS-12 for increment 1; IS-2 for increment 3; IS-9 fixtures per increment).
- [ ] For increment 2: the **U1 and U2 captures exist as fixtures**, with the owner's explicit go for those two requests.
- [ ] OQ-a, OQ-b, OQ-e answered by Winston (they change what the tests assert).
- [ ] GAP-4 resolved into a single copy-deck table, or H10/H11 explicitly waived.

**Exit (per increment).**

- [ ] Every P0 case for the increment passes; P1 ≥ 95 % with any failure triaged and owned.
- [ ] The whole suite is green, headless-safe, and runs in under ~30 s on `windows-latest`.
- [ ] `CachingTranslatorTests`, `LiveDedupTests`, `LiveDefaultsTests`, `DefaultsAndResizeTests.cs:16,28-32`, `PublishFlagsTests` and the two existing `TemplateRenderTests` cases pass **unchanged**.
- [ ] No score-9 risk is left without a passing test.
- [ ] The increment's own "Definition of done" in `plan-migration.md` is met.
- [ ] For increments 1–4: `project-context.md` still matches the code (the rename in increment 2 is the trap).

**Release gate (increments 1 + 2 as one release, per the owner's decision 2 and R7).**

- [ ] TP-RET-01 (a 429 costs one request) and TP-CHN-04 (all paused ⇒ zero requests) both green.
- [ ] TP-START-01 green — no new file is created before first paint.
- [ ] A manual smoke on one real machine: a translation succeeds through `dict-chrome-ex`, and forcing every gate open produces one paused message with a countdown and **zero** requests.
- [ ] The release note carries the P1 expectation sentence (FV-P2-08).

---

_Companions: `02-traduction/architecture-cible.md` (§11 is the seed this plan expands) ·
`02-traduction/plan-migration.md` (increments and V1/V2) · `02-traduction/ux-mode-degrade.md` (§7 acceptance hints)
· `01-demarrage/recommandations.md` §7 + `mesures-protocole.md` (campaign P1). Nothing here is implemented; test
authoring starts with the stories, story by story, on the owner's go._
