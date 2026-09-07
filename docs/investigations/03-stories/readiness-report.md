# Implementation Readiness Assessment Report

**Date:** 2026-09-06
**Project:** PWRU Helper — translation-path rebuild (P2) and startup expectations (P1)
**Assessor:** **John** (BMAD Product Manager), menu item **IR — Check Implementation Readiness**
**Baseline:** commit `4759712` = `main` v0.14.0 · branch `docs/investigations-diagnostic`, PR #54
**Scope of the check:** PRD-equivalent ↔ UX spec ↔ architecture ↔ epics & stories

> **Verdict up front: READY WITH NOTES.** Coverage is complete, no story has a forward dependency, and Epics 1–3 —
> the first release — can start the moment the owner says go. **Eighteen findings** are recorded below; three of
> them (**R-2**, **R-3**, **R-6**) must be closed before **Epic 7** is started, and one (**R-12**) is the standing
> blocker on every P1 claim. None of them blocks Epics 1, 2 or 3.

---

## 1. Document discovery

There is **no `PRD.md`** and no `_bmad-output/planning-artifacts/` in this worktree (it is gitignored and absent).
The PRD-equivalent was nominated by the orchestrator and is treated as authoritative for this assessment.

| Role | Document | Lines | Status |
|---|---|---|---|
| **PRD-equivalent (problem, scope, ranked changes, validation)** | `docs/investigations/SYNTHESE.md` | 507 | Complete, validated by Paige (28 claims spot-checked, 23/23 links) |
| **PRD-equivalent (goals, non-goals, invariants)** | `docs/investigations/02-traduction/architecture-cible.md` §1–§2 | — | Complete, approved by Winston |
| **PRD-equivalent (owner's decisions, OQ answers, amendment A-1)** | `docs/investigations/README.md` | 177 | Complete |
| **Architecture** | `docs/investigations/02-traduction/architecture-cible.md` | 1232 | Complete; 16 invariants, 9 [UNKNOWN]s, OQ-A–D |
| **Migration plan (increment order, reversibility, validation)** | `docs/investigations/02-traduction/plan-migration.md` | 257 | Complete; 8 increments + track P |
| **UX design contract (EXPERIENCE only)** | `docs/investigations/02-traduction/ux-mode-degrade.md` | 555 | Complete; **12 open questions, 3 of them unanswered hard dependencies** |
| **Current-state analysis (seams, tests)** | `docs/investigations/02-traduction/analyse-implementation-actuelle.md` | 463 | Complete |
| **P1 recommendations (SignPath 9 steps, validation)** | `docs/investigations/01-demarrage/recommandations.md` | 403 | FINAL |
| **Epics & stories** | `docs/investigations/03-stories/epics.md` | this run | 9 epics, 62 stories |
| **Project rules** | `project-context.md` | — | Authoritative; **contains one stale figure, see R-16** |

**Duplicates:** none. There is exactly one architecture document, one UX contract, one migration plan and one epics
document. No sharded/whole pair exists, so no version had to be chosen.

**Missing but not required:** a companion `DESIGN.md` for the UX spec. Sally states the reason explicitly — the
visual identity is frozen in `Theme.xaml` and every control named is one that exists today or is one `TextBlock`
next to one that does. **This is a correct omission, not a gap.**

**Output-location deviation:** BMAD convention puts these artefacts in `_bmad-output/planning-artifacts/`, which is
gitignored and absent here. They are written to `docs/investigations/03-stories/` so they land in PR #54 and are
mirrored to `_bmad-output/` by the orchestrator. Recorded so the deviation is deliberate.

---

## 2. PRD analysis (PRD-equivalent)

### Functional requirements

**35 FRs extracted** (FR1–FR35, listed in full in `epics.md` → Requirements Inventory). They were not numbered in
the source documents; they are derived from `SYNTHESE.md` §3.3 (the nine ranked changes), §3.4 (the fifteen-line
architecture), §3.5 (the twelve-line migration path), §2.4 (the nine ranked P1 actions), and the owner's decisions
1–5 plus amendment A-1 in `README.md`.

**Derivation risk, stated:** because the FRs are *derived* rather than read off a numbered PRD, a reviewer could
number them differently. The mitigation is that every FR points at a source paragraph, and the coverage check in §3
is run against the **architecture's own component list and the UX's own state list**, not only against my FR
numbering — so a numbering disagreement cannot hide a missing capability.

### Non-functional requirements

**13 NFRs extracted** (NFR1–NFR13). Sources: `architecture-cible.md` §1.1 G6 and §2 (I2, I10, I11, I12, I13, I14,
I15), §1.2 (the non-goals), `ux-mode-degrade.md` §6 (accessibility and footprint), `plan-migration.md` (one PR one
revert), `project-context.md` (English-only UI, no MVVM, no WPF `Clipboard`, tiny footprint as a product
requirement), and `recommandations.md` §4 (MIT for SignPath eligibility).

### Additional requirements

The 16 invariants I1–I16 are treated as **binding constraints on every story**, not as requirements to be
"implemented" — each epic lists the ones it must guard, and §5 below traces every invariant to the stories that
guard it. There is **no starter template**: this is a brownfield repository at `4759712`, so BMAD's
"Epic 1 Story 1 = set up from starter template" rule does not apply and correctly produces no story.

### PRD-equivalent completeness

**Strong.** The problem statement is evidence-graded throughout, the ranked changes carry effort, gain, risk and a
decision status, and the validation plan (V1.1–V1.7 for P2, V2.1–V2.3 for P1) is falsifiable — including the
explicit statement that *a negative result is still a result*. Two structural weaknesses, both already declared by
the documents themselves and both carried into this report: **every P1 number comes from one machine its own report
calls unrepresentative** (R-12), and **the P2 429 characterisation was obtained outside the process**, one network,
one afternoon.

---

## 3. Epic coverage validation

### 3.1 FR coverage matrix

| FR | Requirement (short) | Epic coverage | Status |
|---|---|---|---|
| FR1 | Typed classification, one place | E1.S2, E1.S3 | ✓ |
| FR2 | HTML sniffed before parsing | E1.S4 | ✓ |
| FR3 | Per-request diagnostic log line | E1.S5 | ✓ |
| FR4 | One message per kind + countdown | E1.S6 (shape) → E7.S1 (copy) | ✓ |
| FR5 | Circuit breaker with escalation and probe | E2.S1, E2.S2 | ✓ |
| FR6 | Rate ceiling with interactive reserve | E2.S3 | ✓ |
| FR7 | Gate state process-global and persisted | E2.S2, E2.S4 | ✓ |
| FR8 | 2 attempts, jitter, no retry on 429/403 | E2.S5 | ✓ |
| FR9 | `Retry-After` honoured | E2.S5 | ✓ |
| FR10 | Gate transitions logged | E2.S6 | ✓ |
| FR11 | Chain skips open gates, one `AllProvidersPaused` | E3.S3 | ✓ |
| FR12 | `dict-chrome-ex` default | E3.S4 | ✓ (gated on E3.S1) |
| FR13 | Edge second vendor | E3.S5 | ✓ (gated on E3.S2) |
| FR14 | `gtx` renamed and demoted | E3.S6 | ✓ |
| FR15 | Chains built once; DeepL unreachable from read | E3.S7 | ✓ |
| FR16 | Never pad; per-line cap 8 | E3.S8 | ✓ |
| FR17 | One shared persistent cache | E4.S1, E4.S2, E4.S4 | ✓ |
| FR18 | Key save keeps the cache | E4.S4 | ✓ |
| FR19 | Gate-open tick does nothing and backs off | E5.S1 | ✓ |
| FR20 | Honest auto-stop with a time window | E5.S2 | ✓ |
| FR21 | Failed rows retried after recovery | E5.S3 | ✓ |
| FR22 | Read-once truthful and cancellable | E5.S4 | ✓ |
| FR23 | Azure provider | E6.S2 | ✓ |
| FR24 | Azure key/region settings, re-entrancy-safe | E6.S3 | ✓ |
| FR25 | Read-path opt-in, off by default | E6.S4 | ✓ |
| FR26 | Eight-state model on every surface | E7.S1, E7.S3, E7.S4, E7.S5, E7.S7 | ✓ |
| FR27 | One 1 Hz countdown per window | E7.S2 | ✓ |
| FR28 | Pending-retry row treatment | E7.S6 | ✓ |
| FR29 | One-click offline engine, LIVE-aware | E8.S2, E8.S3 | ✓ (A-1 applied) |
| FR30 | Four measurements + published decision | E8.S4, E8.S5 | ✓ |
| FR31 | Signed artefacts, exe before MSI | E9.S1–E9.S8 | ✓ |
| FR32 | Expectation copy in three placements | E9.S9, E9.S10 | ✓ |
| FR33 | Release checklist covers the state files | E9.S11 | ✓ |
| FR34 | Key test without spending quota | E6.S5 | ✓ **closed 2026-09-07** — R-1 settled: DeepL is free, Azure is honestly labelled |
| FR35 | Diagnostics on ≥2 affected + 1 control | E9.S12 | ⚠ blocked on a volunteer — see **R-12** |

**Coverage statistics.** Total FRs: **35**. Covered by at least one story: **35**. Coverage: **100 %**. Two are
conditional on an unanswered question (FR34) or an unavailable resource (FR35); neither is uncovered.

**Reverse check — stories with no FR.** None. Every story traces to an FR, an NFR, a UX-DR, an invariant, or a
named [UNKNOWN].

### 3.2 Architecture component coverage (`architecture-cible.md` §16 glossary → stories)

| Type | Target file | Story |
|---|---|---|
| `TranslationErrorKind` | `Services/TranslationErrors.cs` | E1.S2 |
| `TranslationException` (extended) | `Services/TranslationErrors.cs` | E1.S2 |
| `ProviderErrorMapper` | `Services/ProviderErrorMapper.cs` | E1.S3, E1.S4 |
| `TranslationPolicy` | `Services/TranslationPolicy.cs` | **E1.S7** (added — see **R-11**), tuned by E2.S7 |
| `ProviderGate` | `Services/ProviderGate.cs` | E2.S1, E2.S3 |
| `GateDecision` | `Services/ProviderGate.cs` | E2.S1 |
| `RequestPriority` | `Services/ProviderGate.cs` | E2.S3 |
| `ProviderGates` | `Services/ProviderGates.cs` | E2.S2 |
| `ProviderStateStore` | `Services/ProviderStateStore.cs` | E2.S4 |
| `ProviderIds` | `Services/ProviderGates.cs` | E2.S2 |
| `HttpProviderCore` | `Services/HttpProviderCore.cs` | E2.S5 |
| `ChainTranslator` | `Services/ChainTranslator.cs` | E3.S3 |
| `ChainTier` | `Services/ChainTranslator.cs` | E3.S3 |
| `TranslationCacheStore` | `Services/TranslationCacheStore.cs` | E4.S1, E4.S2 |
| `GoogleDictTranslator` | `Services/GoogleDictTranslator.cs` | E3.S4 |
| `EdgeTranslator` | `Services/EdgeTranslator.cs` | E3.S5 |
| `GoogleGtxTranslator` | `Services/GoogleGtxTranslator.cs` | E3.S6 |
| `AzureTranslator` | `Services/AzureTranslator.cs` | E6.S2 |
| `BergamotTranslator` | `Services/BergamotTranslator.cs` | E8.S2 |
| `TextChunker` | `Services/TextChunker.cs` | E3.S6 |
| **Deletion:** `Services/FallbackTranslator.cs` | — | E3.S3 |
| **Rename:** `Services/TranslationService.cs` | — | E3.S6 |
| **Doc update:** `project-context.md` pipeline paragraph | — | E3.S6 (same PR) |

**All 20 glossary types plus the two deletions/renames and the doc update are covered. No orphan, no invention.**
One component in §3.1 is deliberately *not* a story of its own — `CachingTranslator` (extended) — because its
change is the store-taking constructor in E4.S1 and its 7 tests must pass unchanged.

### 3.3 [UNKNOWN] coverage — one spike per U1–U9

| # | Unknown | Spike story | Measurable exit criterion | Go/no-go present |
|---|---|---|---|---|
| U1 | `\n` survival on `translate_a/t` | **E3.S1** | Exactly two `\n` between three segments, or not | Yes — no-go ⇒ **OQ-A settled: one request per line** |
| U2 | Edge body / response / headers | **E3.S2** | One HTTP 200 + the necessary header set | Yes — no-go ⇒ `EdgeTranslator` is **not written** |
| U3 | Does `dict-chrome-ex` survive real load | **E3.S9** | 429s/hour and strike depth over ≥2 h × ≥2 networks | Yes — no-go ⇒ escalate before the release |
| U4 | Azure F0 permanence | **E6.S1** | Yes/no on "2 M chars/month, no expiry" + reset day | Yes — no-go ⇒ rewrite the copy first |
| U5 | Do old DeepL `:fx` keys still work | **E6.S6** | Yes/no on authenticate-and-translate | Yes — no-go ⇒ the settings row says so |
| U6 | Bergamot RAM/latency on slow machines | **E8.S4** | RAM delta, ms/line, quality on ≥30 lines | Yes — all four must pass |
| U7 | Beside-the-exe avoids `%TEMP%` extraction | **E8.S1** | `pre_process_ms` both layouts + `%TEMP%` payload | Yes — no-go ⇒ not in the portable build |
| U8 | Cost of loading a 2000-entry cache | **E4.S3** | ms off the UI thread + working-set delta | Yes — no-go ⇒ **capacity is the knob** |
| U9 | Are the §5.6 windows right | **E2.S7** | Open-windows/LIVE-hour and max strike over ≥3 reports | Yes — tune, or record "defaults confirmed" |

**9 of 9 covered.** U9 is correctly not a pre-release spike: `architecture-cible.md` §15.1 says it "ships,
instrumented" and is tuned from field logs. That is preserved, and the story is explicitly scheduled after the
E2+E3 release.

### 3.4 Increment ↔ epic mapping

| Increment | Epic | Release |
|---|---|---|
| 0 | E1 | may ship alone |
| 1 | E2 | **one release with increment 2** |
| 2 | E3 | **one release with increment 1** |
| 3 | E4 | may ship alone |
| 4 | E5 | may ship alone |
| 5 | E6 | may ship alone |
| 6 | E7 | may ship alone |
| 7 | E8 | prototype branch; merges only on go |
| track P | E9 | parallel, independent |

**One-to-one, order preserved, the coupling stated in the epic list, in the E2 and E3 headers and in E2.S5's risk
line.**

---

## 4. UX alignment

### 4.1 UX state coverage (`ux-mode-degrade.md` §2.1 → stories)

| State | Entered when | Stories |
|---|---|---|
| **S1** Healthy | The first provider of the path answered | E7.S3 (chip), E7.S4 (surfaces) |
| **S2** Degraded — fallback active | A lower provider answered | E7.S3, E7.S5 (the one-time notice) |
| **S3** Paused until | The preferred provider is gated, something below serves | E7.S2 (countdown), E7.S3, E7.S4 |
| **S4** Offline model active | Bergamot answered | E8.S2, E8.S3, E7.S3 |
| **S5** All paused | `AllProvidersPaused(retryAt)` | E3.S3 (the outcome), E5.S1 (LIVE), E7.S1, E7.S4 |
| **S6** No network | `Network` from every provider | E7.S1, E7.S4 — ⚠ **behaviour contradiction, R-6** |
| **S7** Key invalid | `AuthFailed` on DeepL or Azure | E2.S1 (auth block), E6.S5, E7.S1 |
| **S8** Quota exhausted | `QuotaExhausted` | E2.S1, E6.S5, E7.S1 |
| *(not a state)* `Cancelled` | The user pressed Stop | E7.S1 (renders nothing — asserted) |

**8 of 8 states covered**, plus the explicit "`Cancelled` is not a state" assertion, which is the OCE trap wearing a
UX hat.

### 4.2 UX ↔ architecture alignment — Sally's §7 hard dependencies

`ux-mode-degrade.md` §7 lists twelve questions and states that each is **"a hard dependency, not a nice-to-have"**.
This is the sharpest alignment finding of the assessment.

| OQ | Requirement | Does the architecture provide it? | Finding |
|---|---|---|---|
| OQ-1 | `retryAt` per gate **and** for the whole path | **Yes** — `GateDecision.Open(retryAt)` (§5.2) and `AllProvidersPaused` carrying `skippedRetryAts.Min()` (§6.2) | ✓ |
| OQ-2 | A stable user-facing provider name | **No, but trivially closable** — the gate has an `Id` for logging; the display names are named in §7 itself | ✓ resolved in E7.S1 (`{P}`) |
| OQ-3 | Which provider answered + why a higher one was skipped | **No.** `ChainTranslator` returns a bare `string`/`List<string>` (§6.1–6.2); nothing carries the answering tier or the skip reason | ❌ **R-3** |
| OQ-4 | A state-changed notification (plain C# event, no WPF types) | **No.** `ProviderGate` exposes only `TryEnter` / `ReportSuccess` / `ReportFailure` | ❌ **R-2** |
| OQ-5 | Is gate state shared between the chains? | **Yes** — I9 and §5.1, explicitly | ✓ (and the copy must therefore say "Google paused" on the Translator tab even when only LIVE ran) |
| OQ-6 | Can a key be validated without spending quota? | **Answered 2026-09-07 (ruling E6-b, shipped in E6.S5).** **DeepL: yes** — `GET {host}/v2/usage` is authenticated, spends nothing and returns `character_count`/`character_limit`, which settles the key AND the quota row in one call. **Azure: no** — `GET /languages?api-version=3.0` is the **public** metadata endpoint and takes no subscription key, so a 200 from it proves the internet works and nothing about the user's key or region; there is no documented free authenticated probe on the Translator plane, so the check is one five-character real translation and the button says so | ✓ **R-1 closed** |
| OQ-7 | Do recovered rows re-translate in place, keeping the 🔑 line? | **Yes** — §9.3 holds references to the `OcrResultItem` rows themselves | ✓ (asserted in E5.S3) |
| OQ-8 | Does the pause survive restart, readable before the first user action? | **Yes, with a caveat** — §5.7 persists it, but I10 forbids reading the file before the window is visible. It is readable on the first *use*, and the chip can render it as soon as the first gate query happens | ⚠ minor: E7.S4's AC must say "on the first state query after the window is visible", not "at startup" — **already worded that way** |
| OQ-9 | Is the Bergamot download progress observable? | **Unspecified** | ⚠ **R-14**, resolved inside E8.S3 (degrade the copy) |
| OQ-10 | Can characters sent per key be counted locally? | **Not designed.** Marked optional by Sally | ⚠ **R-15**, parked |
| OQ-11 | Offline engine: enabled-if-downloaded, or a separate toggle? | **Conflict.** §12 has `OfflineFallbackEnabled` (bool, default false); §4.2 assumes downloaded ⇒ enabled with `Remove` as the only off switch | ❌ **R-4** |
| OQ-12 | Read-path opt-in off for migrating users with a key | **Moot** (Azure is new) — the rule is kept anyway | ✓ resolved: an opt-in that arrives pre-ticked is not an opt-in |

### 4.3 UX-DR coverage

All **19 UX-DRs** map to at least one story; the trace is printed at the end of `epics.md`. The UX spec's own
eleven **acceptance-criteria hints** (§"Acceptance-criteria hints for the stories") are all carried into story ACs:
hint 1 → E7.S1 · 2 → E7.S2, E7.S6 · 3 → E7.S4 · 4 → E5.S4 · 5 → E7.S1 · 6 → E5.S1 · 7 → E7.S2 · 8 → E6.S3 ·
9 → E9.S10 · 10 → E7.S1 · 11 → E7.S1 (UX-DR19).

---

## 5. Invariant guard matrix

Every invariant is traced to the stories that must guard it. A story that breaks one is wrong **even if it passes
its own tests** (`architecture-cible.md` §2).

| # | Invariant | Guarded by |
|---|---|---|
| **I1** | `ITranslator` keeps its exact shape | E3.S3 (explicit AC), E4.S1, E6.S2, E8.S2 |
| **I2** | `Services/` stays UI-free and headless-testable | E1.S3, E2.S1, E3.S3, E4.S1, E7.S1 (countdown formatted by `MainWindow`, never `Services/`) |
| **I3** | Every OCE catch filters `when (ct.IsCancellationRequested)` | E1.S2 (the `Cancelled` contract), E1.S3, E2.S5, **E3.S3 (the two re-pointed regression guards)**, E5 |
| **I4** | Only successes are cached | E4.S1 (rule stays in `CachingTranslator`), E4.S2 (enforced on disk), E5.S3 (pending value is not `(`-prefixed), E8.S2 |
| **I5** | A batch count mismatch is never padded | E3.S8, E6.S2 |
| **I6** | Slang expansion stays upstream of every engine | E8.S2 (explicit AC), E3.S4 (the cache key is the expanded text) |
| **I7** | The OCR path picks `ru`/`auto` per message | E3.S7, E5.S1 (§9.5's merge is rejected — no story may re-open it) |
| **I8** | DeepL is never reachable from the read path | **E3.S7 (structural, asserted by a named test)**, E6.S4 |
| **I9** | Gate state is process-global and persisted | E2.S2, E2.S4 |
| **I10** | Nothing new touches disk or network before the window is visible | E2.S4, E4.S2, E4.S3, E8.S2, E9.S11 (release-checklist line) |
| **I11** | No user text, `q=`, URL or key in the log | E1.S5 (negative assertion test), E2.S6, E6.S3, E6.S5 |
| **I12** | Settings restore stays re-entrancy-safe | E6.S3 (clobber test), E6.S4, E8.S3 |
| **I13** | Changed defaults reach existing users only via `SettingsVersion` + `Migrate` | E6.S3 (**no bump** — correct), E9.S10 (**bump** — see **R-5**) |
| **I14** | No WPF `Clipboard` | E7 (stated in the epic header; no new copy path is introduced) |
| **I15** | New `Run.Text` is `Mode=OneWay` + an STA render case | E7.S6 (explicit), E7.S3, E7.S4 |
| **I16** | The eleven LIVE behaviours are kept as they are | **E5 epic header**, E5.S1 (`LiveDedup` untouched), E5.S3 (`LiveDedup` untouched), E3.S6 (the `rateLimited` latch kept) |

**16 of 16 invariants have a named guardian story.** The two with the greatest blast radius — I3 and I16 — are
guarded by *existing* tests that must keep passing, which is stronger than a new assertion.

---

## 6. Epic quality review

Applied against the standards of `bmad-create-epics-and-stories` step 2 and step 4.

### 🔴 Critical violations

**None found.** Specifically: no epic requires a *later* epic to function; no story depends on a future story; no
story creates state files or settings it does not need; no epic is so large that it cannot be completed by a single
developer agent in a sequence of PRs.

### 🟠 Major issues

| # | Issue | Assessment |
|---|---|---|
| Q-1 | **Three epics (E1, E2, E4) are not user-value epics by BMAD's literal test.** "Diagnosable translation pipeline", "Provider gate" and "Shared persistent cache" describe capabilities of the machine, not of the player. | **Accepted deviation, justified in writing.** The epics are release/revert boundaries fixed by an approved migration plan whose central promise is "shippable on its own, reversible on its own, one PR one revert". Re-cutting them by user value would destroy that property. Each epic header therefore states a *player-facing* goal sentence, which is the closest honest compromise. Recorded in the Epic List section of `epics.md`. |
| Q-2 | **File churn across E1, E2 and E3** — all three touch `Services/TranslationService.cs` (E3 renames it) and `MainWindow.xaml.cs`. BMAD step 2C would ask for consolidation. | **Consolidation considered and rejected, with rationale** — the exception the method allows. E2 shipped alone is *correct and worse for the user* (R7); that is a genuine feedback/risk boundary, not incidental churn. The mitigation is the release coupling: two PRs, one release, two independent reverts. |
| Q-3 | **E2 has a functionally complete but commercially unshippable state.** It is the only epic in the plan that must not reach users on its own. | Correctly handled: stated in the epic list, in the E2 header, in E3's header and in E2.S5's risk line. A sprint plan that schedules E2 and E3 in different releases would be wrong, and the documents now say so in four places. |
| Q-4 | **E7 depends on three unanswered architecture questions** (OQ-3, OQ-4, OQ-6 → R-3, R-2, R-1). | E7 must not be started until R-2 and R-3 are closed by Winston. E1–E6 are unaffected. |

### 🟡 Minor concerns

| # | Concern | Assessment |
|---|---|---|
| Q-5 | Two stories carry both a spike marker and a Phase-4 marker (E2.S7, E8.S1) because they measure on a branch and then change constants or packaging. | Deliberate; both are labelled and both carry a go/no-go. |
| Q-6 | E9 mixes owner tasks, docs and code in one epic. | Correct — it *is* track P, and splitting it would obscure the fact that E9.S1 gates E9.S2–E9.S8. |
| Q-7 | E6.S5 (`Test key` for **both** DeepL and Azure) adds a DeepL affordance that `plan-migration.md` increment 5 does not mention. | A genuine scope addition, driven by UX-DR13 which specifies DeepL result strings. Small, and it belongs with the Azure key work. **Flagged as R-17** so the owner can drop it if he prefers Azure-only. |
| Q-8 | The FR numbering is derived rather than read from a numbered PRD. | Mitigated by cross-checking coverage against the architecture's glossary and the UX's state list, not only against the FR numbers. |

### Dependency validation

**Within-epic:** checked story by story. Every `Depends on` points backwards. The two forward-looking exceptions are
declared: **E2.S7** waits for the E2+E3 release to be in the field (deliberately post-release), and **E8.S1/E8.S4**
wait for **E9.S12**, which is a *parallel* epic that can start immediately, not a later one.

**Cross-epic:** E1 → E2 → E3 → {E4, E5, E6, E8}; E5 (+E6) → E7; E9 independent. No cycle, no back-reference.

**State-file and settings creation:** `provider-state.json` in the story that needs it (E2.S4);
`translation-cache.json` in E4.S2; the five settings fields in E6.S3; `LastRunVersion` in E9.S10. **Nothing is
created upfront** — the anti-pattern BMAD warns about is absent.

---

## 7. Findings — gaps, ambiguities and contradictions

Eighteen findings, each with a proposed resolution and the person who decides. **W** = Winston (architect) ·
**S** = Sally (UX) · **O** = owner · **J** = John (resolved here).

| # | Kind | Finding | Proposed resolution | Decides | Blocks |
|---|---|---|---|---|---|
| **R-1** | Gap — **CLOSED 2026-09-07 (E6.S5)** | **OQ-6 unanswered:** can a key be validated without spending quota? Sally's `Test key` copy (§3.7) implies a free check; the architecture specifies no validation endpoint. | **Settled by ruling E6-b and shipped.** DeepL has a real non-billing probe (`GET /v2/usage`); Azure has none — `/languages` is PUBLIC and validates nothing, so testing against it would be the exact lie the AC forbids, told the other way round. The labels are therefore **asymmetric on purpose**: `Test key` for DeepL, `Test key (uses a few characters)` for Azure, both written once in `Services/UserMessages.cs`. Both probes go **through `HttpProviderCore`**, so the gate is consulted and a paused provider's test says it is paused; a test never clears a gate (E2-i). | **W** (design), **S** (label) | E6.S5 |
| **R-2** | **Gap — hard dependency** | **OQ-4 unanswered:** Sally requires a **state-changed notification** (a plain C# event, no WPF types) so the UI does not poll. `ProviderGate` exposes only `TryEnter` / `ReportSuccess` / `ReportFailure`. Polling a breaker every 250 ms is exactly the cost this work removes (principle 5). | Add `ProviderGates.StateChanged` — a plain `EventHandler<GateStateChangedEventArgs>` raised on transition, marshalled to the dispatcher by `MainWindow`, keeping `Services/` UI-free (I2). One event, ~10 lines. Add it to E2.S1/E2.S2's scope when approved. | **W** | **E7 entirely** |
| **R-3** | **Gap — hard dependency** | **OQ-3 unanswered:** the chip needs *which provider answered* and *why a higher one was skipped* (paused / failed-now / not configured) — S2 and S3 are different sentences. `ChainTranslator` (§6.1–6.2) returns a bare string and carries neither. | Do **not** change `ITranslator` (I1). Add an out-of-band outcome on the chain — e.g. a `LastOutcome` record or a `TierResolved` event carrying `(providerId, skippedReasons[])` — read by `MainWindow` after each call. | **W** | **E7.S3, E7.S5** |
| **R-4** | Contradiction | **OQ-11:** `architecture-cible.md` §12 defines `OfflineFallbackEnabled` (bool, default false, "false = the tier is not even constructed"); `ux-mode-degrade.md` §4.2 assumes **downloaded ⇒ enabled**, with `Remove` as the only off switch. Amendment A-1(a) ("one-click install") leans to the UX position. | **Downloaded ⇒ enabled**: the install flow sets `OfflineFallbackEnabled = true`, `Remove` sets it false and deletes the files. **No second checkbox** — two controls for one decision is noise. Written into E8.S3; it changes what a persisted setting means, so the owner should confirm. | **O** (confirm), **W** (record) | E8.S3 |
| **R-5** | Contradiction | **`SettingsVersion`:** `architecture-cible.md` §12 states flatly *"`SettingsVersion` is NOT bumped and `Migrate` gains no step"*; `ux-mode-degrade.md` §3.8 requires `LastRunVersion` **seeded by `Migrate`** with the current version — which requires a bump. | Both are right about different fields. §12's ruling governs the **five translation fields** (new fields, defaults are correct on deserialise). `LastRunVersion` is a different field with a seeding requirement, so **E9.S10 owns the bump and is the only story that bumps.** If E9.S10 ships before E6.S3, the ordering must be re-checked so two stories do not each think they own `SettingsVersion`. | **W** | E9.S10 vs E6.S3 ordering |
| **R-6** | **Contradiction — behavioural** | **The no-network case.** `ux-mode-degrade.md` §2.2 S6 and flow (e) say LIVE **keeps reading the screen** during a network outage ("OCR is local and free") and retries on a slow cadence. `architecture-cible.md` §9.1 says a tick where every tier reports `Open` does **no capture, no OCR, no dedup** — and the owner answered **OQ-B: "No — full pause"**. A `Network` failure produces a 5 s soft cooldown, so the gates *are* open and §9.1 applies. | **Follow §9.1 uniformly (full pause), including the no-network case** — OQ-B is settled, and "no CPU next to the game" is the product's first requirement. Sally's sentence *"⚠ Live — no internet connection. It retries by itself."* stays true, because the half-open probe still happens; only the OCR between probes stops. **The owner should confirm, because Sally argued the network case differs on purpose** ("a network outage costs Google nothing and the user everything"). | **O** (confirm), **S** (re-word if he chooses her version) | E5.S1, E7.S4 |
| **R-7** | Contradiction (naming) | Setting names disagree between the documents: architecture §12 says `UseKeyForReading` and `OfflineFallbackEnabled`; UX §4.3 says `AzureForReading` and `OfflineEngineEnabled`. | **The architecture's names win** — they are the ones that reach `settings.json` and therefore users' disks. UX control names (`AzureForReadingCheck`, `OfflineEngineToggle`) are XAML element names and may stay. Written into E6.S3/E6.S4. | **W** | none (cosmetic if caught) |
| **R-8** | Stale document | **Amendment A-1 is recorded only in `README.md`.** `architecture-cible.md` §7.6 constraint 2 still says "unload after `IdleUnloadMinutes`", §7.6 constraint 2 still says "never always-on", and the §7 RAM budget line is un-amended. A reader of §7.6 alone gets the superseded rule. | Winston edits `architecture-cible.md` §7 and §7.6 to carry A-1 (a)(b)(c) inline, with a pointer to the README entry. Until then, **`epics.md` Epic 8's header is the authoritative statement of A-1** and says so. | **W** | nothing, but it will mislead |
| **R-9** | Shipping-order risk | **Provisional copy in the E2+E3 release.** Increment 6 (E7) lands the real wording, but E2+E3 ships first and users will meet `AllProvidersPaused` and a paused LIVE loop in that release. `plan-migration.md` increment 4 already anticipates this ("the status string is written here even in provisional wording"). | Pull the two strings that already exist in finished form forward into E3 and E5 — `All engines are paused — next try in {t}. Nothing you need to do.` (§3.1) and `○ Live — paused, next try in {t}. It resumes on its own; nothing is lost.` (§3.2). They are **written**, not designed-later; using them costs nothing. Already written into E3.S3's copy note and E5.S1. | **S** (approve early use), **O** (accept) | quality of the first release |
| **R-10** | Known interim state | **Burned rows persist between the E2+E3 release and the E5 release.** The gate stops the requests (G1 achieved at 1+2), but until E5 lands, a gate-open tick still captures, OCRs and calls `LiveDedup.Next`, so lines that arrive during a pause are still consumed and marked emitted. | Accept and state it in the E2+E3 release notes: *"rows that fail during a pause are not yet re-translated automatically; that arrives in the next release"*. Alternatively schedule E5 into the same release — but that breaks the one-increment-one-release discipline and is **not** recommended. | **O** | release-note honesty |
| **R-11** | Gap (closed) | `plan-migration.md`'s story cut has **no story for `TranslationPolicy`**, although increment 0's scope item (e) requires it ("created, holding today's numbers so the file exists before anything tunes it"). | Closed here: **E1.S7** added. The plan's 39-title cut is otherwise reproduced faithfully. | **J** (done) | — |
| **R-12** | **Blocking gap** | **No affected machine has ever been measured.** `recommandations.md` §8 item 1 states every P1 number comes from a dev box its own report calls unrepresentative, and that ranks 1–6 are all conditioned on it. This also blocks U6 and U7 (Bergamot RAM and packaging on the *slow* machines). | **E9.S12** is the story, and it needs a volunteer — the only thing in the whole plan that cannot be delegated to an agent. Run the **three falsifying experiments first** (offline, after `Unblock-File`, MSI install): any one coming back "equally slow" redirects the whole P1 line of reasoning **before** weeks are committed to SignPath. | **O** | E8.S1, E8.S4, E9.S8, and every P1 threshold claim |
| **R-13** | Ambiguity | **What does the chip show on a cache hit?** No provider answered the call, yet the cache records a producing provider (`"p"`). Neither document says. | A cache hit **leaves the chip unchanged** — it is not evidence about any provider's current state. One sentence in E7.S3's implementation notes when Sally confirms. | **S** | E7.S3 (minor) |
| **R-14** | Ambiguity | **OQ-9:** is the Bergamot download progress observable (bytes/total)? §3.6's copy shows a percentage. | Resolved inside E8.S3: if the GitHub mirror reports no total size, the copy degrades to `Downloading…` without a percentage rather than inventing one. | **J** (done) | — |
| **R-15** | Deferred | **OQ-10:** counting characters sent per key locally would let About show `about 340k of 2M used this month` instead of the [ASSUMED] "20 to 40 hours". Not designed; Sally marks it optional. | **Park.** It is a real improvement and a real scope increase. Revisit after E6 ships and U4 is answered. | **O** | nothing |
| **R-16** | Housekeeping | Stale facts, all pre-existing and unrelated to P1/P2: `project-context.md` says **142 tests** (the suite has ≈256 cases); `README.md`'s stated line counts are stale for three documents; `Data/slang.json` has no `"version"` key, so its editable copy is never refreshed (unlike phrases and squad). | Fix the test count in the same PR as E3.S6, which already edits `project-context.md`. The slang `"version"` key and the README counts are separate one-line housekeeping items with no story. | **O** | nothing |
| **R-17** | Scope addition | **E6.S5 adds a `Test key` button for DeepL as well as Azure**, which `plan-migration.md` increment 5 does not list. It is driven by UX-DR13, which specifies DeepL result strings. | Keep it — the two buttons share one implementation and the DeepL strings are already written. The owner may cut it to Azure-only if increment 5 must stay minimal. | **O** | nothing |
| **R-18** | Resolved | **OQ-12:** should the read-path Azure opt-in be off for users migrating with a key already saved? | **Yes, always off.** An opt-in that arrives pre-ticked is not an opt-in. Moot in practice (Azure is a new field), but written into E6.S4 so a future migration cannot get it wrong. | **J** (done) | — |

### Where the source documents contradict each other

Three genuine contradictions, all between two *approved* Phase-2 documents:

1. **R-6 — the no-network case.** UX §2.2 S6 / flow (e) says LIVE keeps OCR-ing; architecture §9.1 plus the owner's
   OQ-B answer says a paused tick does nothing at all. **Owner decides.**
2. **R-5 — `SettingsVersion`.** Architecture §12 says never bump; UX §3.8 requires a `Migrate` seed, which requires
   a bump. **Winston decides** (proposed: both are right about different fields, and only E9.S10 bumps).
3. **R-4 — the offline engine's on/off model.** Architecture §12 has an explicit `OfflineFallbackEnabled` setting;
   UX §4.2 assumes downloaded ⇒ enabled with no toggle. **Owner confirms** (proposed: downloaded ⇒ enabled).

Plus two lesser ones: **R-7** (setting names disagree) and **R-8** (amendment A-1 lives only in `README.md`, so
`architecture-cible.md` §7.6 still reads as superseded).

---

## 8. Summary and recommendations

### Overall readiness status

# READY WITH NOTES

**Ready:** 100 % FR coverage · 8/8 UX states · 19/19 UX-DRs · 20/20 architecture components · 16/16 invariants with
a named guardian · 9/9 [UNKNOWN]s with a spike carrying a measurable exit criterion and a go/no-go · no forward
dependencies · no epic requiring a later epic · every state file created by the story that needs it · the E2+E3
release coupling stated in four places · every production-code story gated behind the owner's Phase-4 go.

**Notes:** eighteen findings, three of which must be closed before Epic 7 and one of which (a volunteer machine) has
been the standing blocker on P1 since Phase 1.

### Critical issues requiring action before work starts

1. **R-2 — the state-changed notification does not exist in the architecture.** Sally calls it a hard dependency;
   without it the UI must poll a circuit breaker, which is the cost this whole project is removing. **Winston**,
   before Epic 7. *(Not before Epic 1, 2 or 3.)*
2. **R-3 — nothing carries "which provider answered" or "why a higher one was skipped".** S2 and S3 are different
   sentences and the chain cannot currently tell them apart. **Winston**, before Epic 7. Must not change
   `ITranslator` (I1).
3. **R-6 — the no-network contradiction.** Sally's spec keeps OCR running; the architecture and the owner's OQ-B
   answer stop the whole tick. This changes an acceptance criterion in E5.S1 and a status line in E7.S4.
   **The owner**, before Epic 5.
4. **R-12 — no affected machine has ever been measured.** It blocks E8.S1, E8.S4, E9.S8 and every P1 success
   threshold. **The owner**, and it should start now because it is pure waiting on a volunteer. Run the three
   falsifying experiments *before* committing weeks to SignPath.

### Recommended next steps, in order

1. **The owner says go for Phase 4 on Epics 1–3 only.** They are unblocked, they are the release that fixes P2, and
   nothing in the findings touches them.
2. **In parallel, the owner starts E9.S1 (close PR #49) and E9.S12 (find a volunteer machine).** Both are pure
   waiting; both gate weeks of later work.
3. **The owner answers R-4, R-6, R-10 and R-17** — four short decisions, all listed with a proposed answer.
4. **Winston closes R-2, R-3, R-5, R-7 and R-8** — one small design addition (an event), one small design addition
   (a chain outcome), one ordering ruling, one naming ruling, and one document edit.
5. **Sally confirms R-9 (early use of two finished strings) and R-13 (the chip on a cache hit).**
6. **Amelia runs SP (sprint planning) and CS (create story) from `epics.md`**, ordered by the increments, with the
   E2+E3 coupling made explicit in the sprint plan.
7. **Murat / TEA writes the test plan** — the fake `HttpMessageHandler` suite T1–T19, the non-parallel gate
   collection, and the STA render cases for every new feed template.

### Final note

This assessment examined 4 source documents (≈2,900 lines), 1 UX contract, 9 epics and 62 stories, and found
**18 issues across 4 categories** — 0 critical epic-structure violations, 4 major, 4 minor, and 10 alignment gaps or
contradictions between documents. **None of them blocks the first release.** Three of them block the UX epic, and
one of them — a volunteer with a slow machine — has blocked the P1 half of this project since Phase 1 and will keep
blocking it until somebody presses a button on a laptop that is not the dev box.

---

_Companion: `epics.md` (the 9 epics and 62 stories this report assesses). Nothing in either document is
implemented; Phase 4 starts only on the owner's explicit go._
