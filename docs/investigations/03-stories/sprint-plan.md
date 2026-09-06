# PWRU Helper — Sprint plan (Phase 3)

_Phase 3 · author: **Amelia** (BMAD Senior Software Engineer), menu items **SP — Sprint Planning** then
**CS — Create Story**, run non-interactively · baseline commit `4759712` = `main` v0.14.0 · 2026-09-06 ·
status: **proposed — no production code and no test code written**._

**Inputs.** `docs/investigations/README.md` (owner's decisions, OQ-A–D answers, **amendment A-1**, and the two
tables of **architect's rulings** — binding) · `03-stories/epics.md` (9 epics, 62 stories) ·
`03-stories/readiness-report.md` (18 findings R-1…R-18) · `03-stories/test-plan.md` (IS-1…IS-12, TP-* cases,
FV-* campaigns) · `02-traduction/architecture-cible.md` (I1–I16, §4–§16) · `02-traduction/plan-migration.md`
(increments 0–7 + track P, V1/V2) · `02-traduction/analyse-implementation-actuelle.md` §6.3–6.4 ·
`project-context.md` · the code at the baseline.

**Two rules that govern everything below.**

1. **Nothing here is implemented.** Every story that touches production code is ⛔ **Phase 4 — owner's go
   required** (ground rule 2, `README.md`). The plan schedules work; it does not start it.
2. **Where `epics.md` and the architect's rulings in `README.md` disagree, the rulings win.** Every such case is
   listed in §8 and repeated in the affected story's dev notes.

**Output-location deviation.** BMAD convention puts sprint artefacts in `_bmad-output/implementation-artifacts/`,
which is gitignored and absent in this worktree. This plan, `sprint-status.yaml` and `stories/` are written under
`docs/investigations/03-stories/` so they land in PR #54, exactly as `epics.md` / `readiness-report.md` /
`test-plan.md` did. The deviation is deliberate.

---

## 1. Sprint goals

| # | Goal | Done when |
|---|---|---|
| **G-A** | **A translation failure produces evidence.** | A P2-shaped failure writes a per-request line and "Copy error report" is no longer empty (E1). |
| **G-B** | **The app stops answering "stop" with more requests, and a throttled provider stops being a dead end.** | A 429 costs one request, an open gate costs zero, and a translation succeeds through `dict-chrome-ex` on a network where `gtx` 429s (E2 + E3, one release). |
| **G-C** | **LIVE tells the truth and loses nothing.** | Zero requests and zero OCR while paused; failed rows re-translate after recovery; read-once never claims a false `Done` (E4 + E5). |
| **G-D** | **A user with a free key gets a better tier, and any player can tell paused from broken at a glance.** | Azure key slot + the eight-state model on every surface (E6 + E7). |
| **G-E** | **Offline translation ships only if it earns its way in.** | Four measurements published and a recorded go **or** no-go (E8). |
| **G-P** | **The first launch after an update stops reading as a bug.** | Signed artefacts + expectation copy in three placements (E9, parallel). |

**Non-goals of this sprint** (do not propose, do not implement): MVVM, i18n/.resx, a DI container, multi-`q=`
batching, `ReadyToRun` / trimming / AOT / single-file compression, WPF `Clipboard`, a new NuGet package outside the
Bergamot prototype branch (NFR6, `project-context.md`).

---

## 2. The release cut

`plan-migration.md` fixes each increment as "shippable on its own, reversible on its own, one PR, one revert".
The **one hard coupling** is that increments 1 and 2 (E2 and E3) are separate PRs but **a single release** — the
owner's decision 2 plus `architecture-cible.md` R7: a circuit breaker in front of an endpoint that 429s on request
#1 is an app that is correctly paused all the time.

| Release | Cut | Stories | Size | Why this boundary |
|---|---|---|---|---|
| **Release A — P2 fix** | **A.0** (optional, may ship alone) | **E1.S1–E1.S7** (7) | 5.5 d | Invisible to users; it is what makes every later increment provable. Shipping it early buys field evidence from the next real P2 incident. |
| | **A.1 — the coupled pair, mandatory single release** | **E2.S1–E2.S6 + E3.S1–E3.S8** (14) | 13.5 d | E2 alone is *correct and worse for the user* (R7/R-10). E3 alone has no protection behind it. Two PR trains, two independent reverts, **one tag**. |
| | **A.2** | **E4.S1–E4.S4 + E5.S1–E5.S4** (8) | 8 d | Cache + honest LIVE. Each may ship alone; they close V1.3 and V1.5. |
| **Release B — Azure + UX** | B.1 | **E6.S1–E6.S6** (6) | 7.5 d | Increment 5. May ship alone. |
| | B.2 | **E7.S1–E7.S8** (8) | 7.5 d | Increment 6. Depends on E5 and (for the key-slot copy) E6. |
| **Release C — Bergamot** | C — **prototype branch, merges only on go** | **E8.S1–E8.S5** (5) | 8.5 d | Increment 7. Any single go-criterion failing ⇒ no-go, branch closed with its numbers. Both outcomes are successes. |
| **Track P — signing & expectations** | continuous, parallel, independent of A/B/C | **E9.S1–E9.S12** (12) | 10 d (mostly waiting) | Touches no translation code. **Start now** — E9.S1 and E9.S12 are pure waiting and gate weeks of later work. |
| **Post-release** | — | **E2.S7** (spike U9) | 0.5 d | Deliberately scheduled *after* the A.1 release is in the field; it is tuned from ≥ 3 field reports. |

**Release A totals: 31 stories · 16 S + 14 M + 1 L ≈ 32.5 agent-days** (S ≈ 0.5 d, M ≈ 1.5 d, L ≈ 3.5 d, for an
agent working with review). Whole backlog: 62 stories ≈ 74 agent-days, of which ≈ 10 days are owner tasks and
external waiting that no agent can shorten.

**Release-note obligations, decided here so they are not forgotten**

- **A.1 must carry** the R-10 sentence: *"rows that fail during a pause are not yet re-translated automatically;
  that arrives in the next release"* — true only until A.2 ships. **Owner's confirmation still open (R-10).**
- **Every** release from now on carries the P1 expectation line (FV-P2-08, `ux-mode-degrade.md` §3.8).
- A.1 and A.2 touch the translation path ⇒ the E9.S11 checklist line applies (confirm `provider-state.json` and
  `translation-cache.json` are created **lazily**, not at startup).

---

## 3. Definition of Ready

A story may be pulled into implementation only when **all** of these hold. The nine stories written in
`stories/` satisfy them today.

- [ ] The owner has said **go for Phase 4** on this story's epic. (Ground rule 2. No exception.)
- [ ] A story file exists in `docs/investigations/03-stories/stories/` with acceptance criteria copied from
      `epics.md` **unweakened**, tasks in implementation order with exact files, dev notes, TP-IDs and a file list.
- [ ] Every `Depends on` story is **done** (or explicitly waived by the owner, recorded in the story).
- [ ] Every architect's ruling that touches it is quoted in its dev notes (`README.md`, the two ruling tables).
- [ ] Its isolation requirements (IS-n) are either landed by an earlier story or are in this story's own ACs.
- [ ] For a story that needs a captured shape (U1/U2), **the fixture exists** — no code against a guessed body.
- [ ] For a story that sends a request to a third party, the owner's **explicit go for those requests** is on file
      (GAP-6; the dev box must send nothing further to Google).
- [ ] It is implementable in **one PR** on a `feature/…` or `fix/…` branch, and revertible on its own.

**Definition of Done (every story, in addition to its own DoD line)**

- `dotnet test tests/PWRUHelper.Tests` green, headless-safe, under ~30 s on `windows-latest`.
- `CachingTranslatorTests`, `LiveDedupTests`, `LiveDefaultsTests`, `DefaultsAndResizeTests.cs:16,28-32`,
  `PublishFlagsTests`, `StartupSettingsTests` and the two existing `TemplateRenderTests` cases pass **unchanged**.
- No invariant I1–I16 broken — *a story that breaks one is wrong even if it passes its own tests*.
- `InternalsVisibleTo PWRUHelper.Tests` (`PWRUHelper.csproj:28`) and the `DefaultItemExcludes` exclusion of
  `tests\**` (`:18`) both intact. **No `.sln` at the repo root.**
- Landed on `main` via PR. No direct push, no force-push, no self-merge.

---

## 4. Sprint backlog — all 62 stories, ordered by increment

**Status vocabulary.** `ready` = story file written, dev-ready · `backlog` = in `epics.md`, not yet contexted ·
`spike` = measurement/capture with a measurable exit criterion and a go/no-go · `owner-task` = no code, cannot be
delegated to an agent · `blocked-by-owner-go` = additionally blocked by a decision or a resource the owner owns
(over and above the blanket Phase-4 go, which gates **every** ⛔ story).

### Epic E1 (A) — Diagnosable translation pipeline · increment 0 · Release A.0

Goal: a translation failure produces evidence instead of a shrug. **Zero behaviour change.**
Invariants guarded: I1, I2, **I3**, I11, I16.

| # | Story | Status | Size | Depends on | Blocked by |
|---|---|---|---|---|---|
| E1.S1 | Inject an `HttpMessageHandler` into every HTTP provider | **ready** | S | — | Phase-4 go |
| E1.S2 | Introduce `TranslationErrorKind` and a typed `TranslationException` | **ready** | S | — | Phase-4 go |
| E1.S7 | Create `TranslationPolicy` holding today's numbers | **ready** | S | — | Phase-4 go |
| E1.S3 | Classify every provider outcome in one `ProviderErrorMapper` | **ready** | M | E1.S1, E1.S2 | Phase-4 go |
| E1.S4 | Detect the HTML abuse page before parsing, never after | **ready** | S | E1.S3, E1.S7 | Phase-4 go |
| E1.S5 | Emit the per-request diagnostic log line | **ready** | M | E1.S1, E1.S3 | Phase-4 go |
| E1.S6 | Map `Friendly()` onto `Kind` without changing any wording | **ready** | S | E1.S2 | Phase-4 go |

**Recommended implementation order: S1 → S2 → S7 → S3 → S4 → S5 → S6.** `epics.md` lists S7 last; it has no
dependencies and S4 depends on it, so it is pulled forward. Epic DoD: suite green and headless; a fake handler can
return 429 / 403 / 200-with-HTML / a timeout and each produces the right `Kind`; **no user-visible string changed**.

### Epic E2 (B) — Provider gate · increment 1 · Release A.1 (with E3)

Goal: the app stops keeping its own block alive. Invariants: **I9**, **I10**, I2, I3, I11, I16.

| # | Story | Status | Size | Depends on | Blocked by |
|---|---|---|---|---|---|
| E2.S1 | `ProviderGate` — circuit breaker with strikes, cap and half-open probe | **ready** | M | E1 | Phase-4 go |
| E2.S2 | `ProviderGates` registry, process-global and reset-able | **ready** | S | E2.S1 | Phase-4 go |
| E2.S3 | Rate ceiling with an interactive-priority reserve | backlog | S | E2.S1 | — |
| E2.S4 | Persist gate state to `provider-state.json`, lazily and atomically | backlog | M | E2.S2 | — |
| E2.S5 | `HttpProviderCore` — 2 attempts, jitter, no retry on 429/403, `Retry-After` | backlog | **L** | E2.S1, E2.S3, E1.S3, E1.S5 | **must not reach a release without E3** |
| E2.S6 | Log gate transitions | backlog | S | E2.S1 | — |
| E2.S7 | Review and tune the gate windows from field logs 🔬 U9 | **spike** | S | the A.1 release being in the field | ≥ 3 field reports |

### Epic E3 (C) — Provider chain and the new default endpoint · increment 2 · Release A.1 (with E2)

Goal: a throttled provider stops being a dead end. **Ships in the same release as E2 — separate PRs, one tag.**

| # | Story | Status | Size | Depends on | Blocked by |
|---|---|---|---|---|---|
| E3.S1 | Capture the `dict-chrome-ex` batch behaviour 🔬 U1 | **blocked-by-owner-go** | S | — | owner's go for **two** requests; **not from the dev box** (GAP-6) |
| E3.S2 | Capture the Edge request and response shapes 🔬 U2 | **blocked-by-owner-go** | S | — | same; **no `EdgeTranslator` code before the fixture exists** |
| E3.S3 | `ChainTranslator` replacing `FallbackTranslator` | backlog | M | E2.S1 | — |
| E3.S4 | `GoogleDictTranslator` as the new default free provider | backlog | M | E3.S1, E3.S3, E2.S5 | E3.S1 fixture |
| E3.S5 | `EdgeTranslator` as the independent second vendor | backlog | M | E3.S2 (hard gate), E3.S3 | E3.S2 fixture |
| E3.S6 | Demote `gtx` — rename to `GoogleGtxTranslator`, extract `TextChunker` | backlog | M | E2.S5 | — |
| E3.S7 | Build both chains once, in the constructor, per the target composition | backlog | M | E3.S3–E3.S6 | — |
| E3.S8 | Bound the batch-mismatch per-line fallback | backlog | S | E3.S4 | — |
| E3.S9 | Soak the new default endpoint under sustained load 🔬 U3 | **blocked-by-owner-go** | M (waiting) | E3.S4, E1.S5, E2.S6 | a **designated** connection that is not the dev box |

### Epic E4 (D) — Shared persistent cache · increment 3 · Release A.2

| # | Story | Status | Size | Depends on |
|---|---|---|---|---|
| E4.S1 | Extract the LRU into `TranslationCacheStore` | backlog | S | E3 |
| E4.S2 | Persist the cache lazily and save it debounced | backlog | M | E4.S1 |
| E4.S3 | Measure the cache lazy-load cost 🔬 U8 | **spike** | S | E4.S2 |
| E4.S4 | Share one store between the read and the write decorators | backlog | S | E4.S2, E3.S7 |

### Epic E5 (E) — Honest live translation · increment 4 · Release A.2

| # | Story | Status | Size | Depends on | Note |
|---|---|---|---|---|---|
| E5.S1 | Skip the whole tick and back off while every read tier is paused | backlog | M | E3.S3 | **GAP-3/R-6 ruled: full pause is universal**, including no-network |
| E5.S2 | Fix the auto-stop counter and add the time-window rule | backlog | S | E5.S1 | user-visible behaviour change |
| E5.S3 | Retry failed rows once the providers recover | backlog | M | E5.S1 | duplicated rows would be worse than the failure |
| E5.S4 | Make read-once report the truth, and make it cancellable | backlog | M | E3.S3 | read-once is **`Interactive`** (ruling OQ-a) |

### Epic E6 (F) — Bring your own key: Azure · increment 5 · Release B.1

| # | Story | Status | Size | Depends on | Note |
|---|---|---|---|---|---|
| E6.S1 | Verify the Azure F0 free tier on a real resource 🔬 U4 | **spike** | S | — | the copy must not promise "free forever" first |
| E6.S2 | `AzureTranslator` over raw `HttpClient` | backlog | M | E2.S5, E3.S3 | — |
| E6.S3 | Azure key and region settings, re-entrancy-safe | backlog | M | E6.S2 | **R-7: architecture §12 names win** (`AzureApiKey`, `AzureRegion`, `UseKeyForReading`, `OfflineFallbackEnabled`); **no `SettingsVersion` bump** |
| E6.S4 | "Use my key for screen reading" opt-in, off by default | backlog | S | E6.S3, E3.S7 | R-18: always off, even for a migrating user |
| E6.S5 | Test a key without spending quota | backlog | M | E6.S3 | **R-1/OQ-d open**; label degrades to `Test key (uses a few characters)` if no free probe exists |
| E6.S6 | Establish whether existing DeepL `:fx` keys still work 🔬 U5 | **spike** | S | — | — |

### Epic E7 (G) — Degraded-mode UX · increment 6 · Release B.2

Blocking gaps R-2 and R-3 are **now closed by the architect's rulings** (poll at 1 Hz + `ProviderGate.Snapshot()`;
`ChainTranslator.LastOutcome`). E7 is unblocked provided those two land in E2.S1 and E3.S3 respectively.

| # | Story | Status | Size | Depends on | Note |
|---|---|---|---|---|---|
| E7.S1 | One sentence per error kind, with a countdown | backlog | M | E1.S6, E5, E7.S2 | **GAP-4 ruled: `Services/UserMessages.cs`**, a UI-free constant table; add it to this story's scope |
| E7.S2 | The one-per-window 1 Hz countdown | backlog | S | E5.S1 | the same 1 Hz timer polls the gate (ruling OQ-c/R-2) |
| E7.S3 | The provider chip and its tooltip | backlog | M | E7.S1, E7.S2 | reads `ChainTranslator.LastOutcome` (R-3); a cache hit leaves the chip unchanged (R-13, Sally to confirm) |
| E7.S4 | Paused state and a frozen heartbeat on both windows | backlog | M | E7.S2, E5.S1 | — |
| E7.S5 | LIVE, read-once and overlay-reply copy | backlog | M | E5, E7.S1 | no `«Sally: …»` placeholder may remain |
| E7.S6 | Pending-retry row treatment, with a render test | backlog | S | E5.S3 | **I15** — the `Run.Text` TwoWay trap |
| E7.S7 | About tab — the "Translation engines" block | backlog | M | E7.S3, E6.S3 | — |
| E7.S8 | README and About copy for the new key slot and the offline option | backlog | S | E6.S1, E6.S6, E8.S3 | — |

### Epic E8 (H) — Offline fallback prototype · increment 7 · Release C (prototype branch)

Amendment **A-1** governs: one-click install from the About tab, **kept loaded while LIVE runs**, unloaded after
LIVE stops + idle timeout, RAM cost explicitly accepted by the owner.

| # | Story | Status | Size | Depends on | Blocked by |
|---|---|---|---|---|---|
| E8.S1 | Does `bergamot.dll` beside the exe avoid the `%TEMP%` extraction? 🔬 U7 | **spike** | M | E9.S12 | a measured affected machine (**R-12**) |
| E8.S2 | Prototype `BergamotTranslator`, lazily loaded and LIVE-aware | backlog | **L** | E3.S7 | prototype branch only |
| E8.S3 | One-click install of the offline engine from the About tab | backlog | M | E8.S2 | **R-4 owner confirmation**: downloaded ⇒ enabled, no second checkbox |
| E8.S4 | Measure RAM, load time, slang quality and cold-start impact 🔬 U6 | **spike** | M | E8.S2, E8.S1, E9.S12 | a measured affected machine (**R-12**) |
| E8.S5 | Record the go/no-go decision, either way | **owner-task** | S | E8.S4 | — |

### Epic E9 (P) — Packaging, signing and expectations · parallel track P · start now

| # | Story | Status | Size | Depends on | Note |
|---|---|---|---|---|---|
| E9.S1 | Close PR #49 and confirm the MIT licence | **owner-task** | S | — | **gates E9.S2–E9.S8**; pure waiting — start today (OQ-D) |
| E9.S12 | Run the startup diagnostics campaign on the affected machines | **owner-task** | M | a volunteer | **R-12 — the standing blocker on every P1 claim**; run the three falsifying experiments (`Unblock-File`, portable vs MSI, airplane mode) **before** committing weeks to signing |
| E9.S2 | Apply to SignPath Foundation and wait for approval | **owner-task** | M (waiting) | E9.S1 | external approval, days to weeks |
| E9.S3 | Create the SignPath project and note the five identifiers | **owner-task** | S | E9.S2 | — |
| E9.S4 | Add one secret and six variables to GitHub | **owner-task** | S | E9.S3 | — |
| E9.S5 | Wire signing into the release workflow, job-level `env` guards | backlog | M | E9.S4 | the `if: ${{ secrets.X != '' }}` step guards are **invalid** — job-level `env:` instead |
| E9.S6 | Keep `packaging/signpath-signing.md` in sync, in the same PR | backlog | S | E9.S5 (same PR) | `PublishFlagsTests.cs:72-79` enforces the same-PR rule |
| E9.S7 | Treat the first signed tag as a test | **owner-task** | S | E9.S5, E9.S6 | — |
| E9.S8 | Verify the signature and re-measure on a real downloaded copy | **owner-task** | M | E9.S7, E9.S12 | the signed/unsigned **pair** is the whole claim (FV-P1-06) |
| E9.S9 | README and release-note expectation text | backlog (docs) | S | — | **shippable immediately**, independent of signing (V2.3) |
| E9.S10 | One-time in-app "first launch after an update" toast | backlog | S | E9.S9 | **ruling OQ-e/R-5: no `Migrate` step.** Empty `LastRunVersion` ⇒ "changed" ⇒ toast once. **E9.S10 is the only story that may bump `SettingsVersion`** — and per the ruling it does not need to |
| E9.S11 | Extend the release checklist for the new state files | backlog (docs) | S | E2.S4, E4.S2 | — |

---

## 5. Dependency graph

```
E1 ──> E2 ──> E3 ──┬──> E4 ──────────────┐
                   ├──> E5 ──> E7 <──────┤
                   ├──> E6 ──────────────┘
                   └──> E8 (prototype branch, merges only on go)

E9 (track P) is independent of E1–E8 and starts immediately.
E8.S1 / E8.S4 wait on E9.S12 — a PARALLEL epic, not a later one.
E2.S7 waits on the A.1 release being in the field — deliberately post-release.
```

No cycle, no forward dependency. The only cross-track edges are E8 → E9.S12 (a measured machine) and
E7.S8 → E8.S3 (offline wording).

**Critical path to the P2 fix:** E1.S1 → E1.S2 → E1.S3 → E1.S5 → E2.S1 → E2.S3 → **E2.S5 (L)** → E3.S3 → E3.S4 →
E3.S7 ≈ 15 agent-days, with E3.S1's capture (owner's go) as the only external gate on it.

---

## 6. Test obligations carried by this sprint

The isolation requirements of `test-plan.md` §2 are **production-code acceptance criteria**, not test-author
conveniences. Their scheduling:

| IS | Requirement | Lands in |
|---|---|---|
| IS-8 | `HttpMessageHandler` ctor overload on every HTTP provider, `internal`, `null` ⇒ shared static client | **E1.S1** (existing two providers); each new provider repeats it (E3.S4, E3.S5, E3.S6, E6.S2) |
| IS-10 | No real network in CI — a guard test enumerates provider types and asserts the overload exists | **E1.S1** (extended by each new provider story) |
| IS-11 | The fake handler records count/method/URI/headers, supports a scripted sequence and a synthetic `TaskCanceledException` **with the caller's token not cancelled** | **E1.S1** |
| IS-9 | Recorded fixtures under `tests/PWRUHelper.Tests/Fixtures/`, ≤ 64 KB total, checked in as source | **E1.S4** (`google-429.html`, `google-captcha.html`), then E3.S1/E3.S2/E6.S2 |
| IS-1 | `PathOverride` on `ProviderGates` / `ProviderStateStore` | **E2.S2** (declared), **E2.S4** (honoured by the store) |
| IS-4 | `ProviderGates.ResetForTests()` — clears the registry **and** any pending debounced save | **E2.S2** (registry), completed in **E2.S4** (pending save) |
| IS-5 | A non-parallel `[CollectionDefinition("Gates")]` whose fixture resets before and after every case | **E2.S2** |
| IS-6 | One shared injectable `Clock` on `ProviderGate` **and** `ProviderGates` | **E2.S1** + **E2.S2** |
| IS-3 | A `TempGateState` / `TempCache` disposable mirroring `TempSettings` (`StaTestHost.cs:82-101`) | **E2.S2** (gate), **E4.S2** (cache) |
| IS-12 | `ProviderGateOverrides` parsing pure and `internal` so a test can drive short windows | **E2.S1** (the pure parser); the `AppSettings` field it reads lands in **E6.S3** — see §8 note 6 |
| IS-7 | Injectable retry delay (`Func<TimeSpan, CancellationToken, Task>`) | **E2.S5** |
| IS-2 | `TranslationCacheStore` takes an explicit path | **E4.S1/E4.S2** |

**Score-9 risks and the stories that must carry their P0 tests.** R-01 permanently-paused app → E2.S1, E2.S4,
E3.S3, E5.S1 · R-02 zombie LIVE indicator → E5.S1, E7.S4 · **R-03 the OCE trap** → E1.S2, E1.S3, E2.S5, E3.S3,
E5.S1 · R-04 settings clobbered on restore → E6.S3, E6.S4 (one case **per new control**). *A score-9 risk with no
test is a blocked release, not a waiver.*

**CI budget.** Whole suite under ~30 s; the `WPF` STA collection under 15 s; two non-parallel collections (`WPF`,
`Gates`) is the accepted cost of two static facades. No new test dependency — xUnit 2.9.2, Test SDK 17.11.1,
runner 2.8.2 only. The fake handler is ~30 lines, modelled on the existing `Fake`/`CountingTranslator` doubles
(`TranslationBackendTests.cs:51-62`, `CachingTranslatorTests.cs:10-28`).

---

## 7. Sprint status table

Machine-readable companion: **`sprint-status.yaml`** (same directory, BMAD sprint-planning format).

| Item | Status |
|---|---|
| epic-1 | in-progress *(first stories contexted)* |
| e1-s1-http-handler-seam | ready-for-dev |
| e1-s2-typed-translation-errors | ready-for-dev |
| e1-s3-provider-error-mapper | ready-for-dev |
| e1-s4-html-abuse-page-sniffing | ready-for-dev |
| e1-s5-per-request-diagnostic-log | ready-for-dev |
| e1-s6-friendly-kind-switch | ready-for-dev |
| e1-s7-translation-policy | ready-for-dev |
| epic-1-retrospective | optional |
| epic-2 | in-progress *(first stories contexted)* |
| e2-s1-provider-gate-breaker | ready-for-dev |
| e2-s2-provider-gates-registry | ready-for-dev |
| e2-s3 … e2-s7 | backlog |
| epic-2-retrospective | optional |
| epic-3 … epic-9 and all their stories | backlog *(see `sprint-status.yaml` for the full enumeration)* |

**Reading rule.** `ready-for-dev` in the YAML means *the story file exists and is complete*. It does **not** mean
work may start: every ⛔ story additionally requires the owner's Phase-4 go, which is why every story file carries
the status line `ready-for-dev (Phase 4 — owner's go required)`.

---

## 8. Notes for John, Winston and Murat — discrepancies resolved while planning

Recorded here rather than by editing `epics.md`, `readiness-report.md` or `test-plan.md`, which this run must not
touch. Each note says which way the story files were written.

1. **R-2 / OQ-c — the state-change notification is *not* an event.** `readiness-report.md` R-2 proposes
   `ProviderGates.StateChanged` and says *"Add it to E2.S1/E2.S2's scope when approved"*. The architect's ruling in
   `README.md` says the opposite: **poll at 1 Hz** from the countdown timer, `ProviderGate.Snapshot()` returns an
   immutable `{State, BlockedUntil, Strikes, LastKind}`, **no events out of `Services/`**. The ruling wins. E2.S1 is
   written with `Snapshot()` and an explicit "do not add an event" AC; E7.S2 owns the poll. **Winston: `Snapshot()`
   is not yet in the §16 glossary.**
2. **R-3 — `ChainTranslator.LastOutcome`.** The ruling adds an immutable
   `{ProviderId, Skipped: [(ProviderId, Reason)], RetryAt?, Kind?}` set after every call, read by the code-behind.
   `ITranslator` (I1) is unchanged. It belongs to **E3.S3**, whose ACs in `epics.md` do not mention it. **Winston:
   add it to §6.1 and §16.**
3. **GAP-4 — `Services/UserMessages.cs`.** The ruling creates a UI-free constant copy-deck table. It appears in
   neither `architecture-cible.md` §16 nor `readiness-report.md` §3.2's component coverage. Scheduled into
   **E7.S1**; E1.S6 deliberately does *not* create it (increment 0 introduces no new string), but E1.S6 keeps the
   switch a pure function of `Kind` so the later move is mechanical.
4. **E1.S1's `SocketsHttpHandler` AC is the one non-neutral item in a "zero behaviour change" increment.** Today
   `TranslationService.CreateClient()` (`TranslationService.cs:37-44`) returns a plain
   `new HttpClient { Timeout = 12 s }` and `DeepLTranslator` a plain `new()` at `:16` — there is no
   `SocketsHttpHandler` and no `PooledConnectionLifetime` anywhere. Adding it is justified (Phase 0 F6 / Q2.6: a
   stale pooled connection on a process-lifetime static client), and it is kept — but the PR must call it out, and
   the DoD line "no user-visible string changed" is what makes the rest of the increment invisible.
5. **`epics.md` E1.S1 cites `DeepLTranslator.cs:80-110` for the client construction.** That range is the
   send/OCE-filter/status-mapping region; the DeepL client is constructed at **`DeepLTranslator.cs:16`**. The story
   cites `:16` for the client and `:80-110` for the mapping call site (which is what E1.S3 needs).
6. **IS-12 vs `AppSettings.ProviderGateOverrides`.** `test-plan.md` assigns IS-12 to Epic B, but the settings field
   it parses is created in **E6.S3** (increment 5, §12). Resolution written into the stories: **E2.S1 lands the
   pure `internal` parser** (unparseable ⇒ defaults, never throws — the L1 half of TP-SET-11) so gate tests can
   drive short windows; **E6.S3 wires the `AppSettings` field into it**; **E2.S7** uses it as the field hatch.
   Winston/John may prefer to move the whole thing forward into E2 — say so and the stories follow.
7. **`TranslationException` must be *moved*, not re-created.** It lives today at
   `Services/TranslationService.cs:10-13`. `epics.md` E1.S2 says "create `Services/TranslationErrors.cs`" without
   saying the old declaration is deleted in the same commit. Two declarations would not compile; the story says
   **move**.
8. **Line-number drift, minor.** `SettingsService.Save` is at **`:184-196`**, not `:186-197` as cited in
   `architecture-cible.md` §5.7 and `epics.md` E2.S4. The `PathOverride` seam at `:95` and `Logging` at `:31-39`,
   `:44`, `:85` are exact. Every other `file:line` in the nine written stories was re-verified against the baseline.
9. **Test fixtures need a csproj item group.** `tests/PWRUHelper.Tests/PWRUHelper.Tests.csproj` has **no** content
   item group today, so IS-9's `Fixtures/` folder needs either `CopyToOutputDirectory` `None` items or
   `EmbeddedResource` entries. **E1.S4** adds it (budget ≤ 64 KB total, CI-6).
10. **`TestLogRedirect` already sets `Logging.DirectoryOverride` process-wide** (a `[ModuleInitializer]`,
    `TestLogRedirect.cs:24-26`) and `LoggingTests.cs:89` asserts it. E1.S5's log tests must set **their own**
    override and restore the previous value in a `finally`, exactly like `LoggingTests.cs:101-110` — not assume a
    clean file.
11. **GAP-3 supersedes `test-plan.md` §6.3's S6 row.** The ruling makes the full pause universal, so TP-LIVE-01's
    "zero captures" assertion is **unconditional** and the S6 row's "deliberate divergence" note is obsolete.
    Murat: the row can be closed when the plan is next revised. Sally's S6 sentence is amended in E5.S1/E7.S4.
12. **Numbering mismatch across documents.** `test-plan.md` §2 and §3 refer to stories by the
    `plan-migration.md` 39-title cut ("Epic B / story 9", "increment 0, stories 3–4"); `epics.md` uses `E#.S#`
    (62 stories). The mapping used here: increment 0 stories 1–6 → E1.S1–E1.S6 (**E1.S7 is new**, R-11);
    increment 1 stories 7–12 → E2.S1 (7), E2.S3 (8), E2.S4 (9), E2.S5 (10 + 11), E2.S6 (12); increment 2 stories
    13–19 → E3.S1/S2 (13), E3.S3 (14), E3.S4 (15), E3.S5 (16), E3.S6 (17), E3.S7 (18), E3.S8 (19); increment 3
    stories 20–22 → E4.S1/S2/S4; increment 4 stories 23–26 → E5.S1–S4; increment 5 stories 27–29 → E6.S2–S4.
13. **Still open, and they block nothing in Release A.0/A.1:** R-1/OQ-d (free key validation → E6.S5 wording),
    R-4 (offline on/off model → E8.S3), R-9 (Sally's approval to use two finished strings early in E3.S3/E5.S1),
    R-10 (the A.1 release-note sentence), R-13 (the chip on a cache hit), R-17 (drop the DeepL `Test key`?),
    OQ-f (does a `Test key` request report to the gate?). **R-12 (a measured affected machine) remains the
    standing blocker on every P1 claim and on E8.S1/E8.S4 — it is pure waiting and should start now.**
14. **Housekeeping, R-16.** `project-context.md` still says 142 tests (the suite has ≈ 256 cases). The fix belongs
    in **E3.S6's PR**, which already edits that file for the rename. Not a blocker for anything.

---

## 9. What happens next

1. **The owner says go for Phase 4 on Epics 1–3 only.** They are unblocked and they are the release that fixes P2.
2. **In parallel, the owner starts E9.S1 (close PR #49) and E9.S12 (find a volunteer machine).** Both are pure
   waiting; both gate weeks of later work. E9.S9 (expectation copy) can ship in the very next release with no
   dependency on anything.
3. **The owner's explicit go for the two capture requests of E3.S1/E3.S2**, from a connection that is **not** the
   dev box (Phase-1 incident, ground rule 2).
4. Implementation proceeds story by story: `bmad-dev-story` on a story file, then `code-review` in a fresh context,
   then the next story — so each one's learnings reach the following one.
5. `bmad-sprint-planning` is re-run to refresh `sprint-status.yaml` whenever a story lands, and
   `bmad-create-story` writes the next batch (E2.S3–E2.S7, then E3) once the epic is under way.

---

_Companions: `epics.md` (the 62 stories this plan schedules) · `readiness-report.md` (the 18 findings) ·
`test-plan.md` (IS-1…IS-12, TP-*, FV-*) · `stories/` (the nine contexted stories). Nothing here is implemented;
Phase 4 starts only on the owner's explicit go._
