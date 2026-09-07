---
stepsCompleted: [step-01-validate-prerequisites, step-02-design-epics, step-03-create-stories, step-04-final-validation]
inputDocuments:
  - docs/investigations/SYNTHESE.md
  - docs/investigations/README.md
  - docs/investigations/02-traduction/architecture-cible.md
  - docs/investigations/02-traduction/plan-migration.md
  - docs/investigations/02-traduction/ux-mode-degrade.md
  - docs/investigations/02-traduction/analyse-implementation-actuelle.md
  - docs/investigations/01-demarrage/recommandations.md
  - project-context.md
---

# PWRU Helper — Epic Breakdown (Phase 3)

_Phase 3 · author: **John** (BMAD Product Manager), menu item **CE — Create Epics & Stories** ·
baseline commit `4759712` = `main` v0.14.0 · 2026-09-06 · status: **proposed — no production code written**._

## Overview

This document decomposes the Phase-2 design into implementable stories. It has **no PRD** in the BMAD sense; the
PRD-equivalent is the trio the orchestrator named:

- **`SYNTHESE.md`** — the problem statement, the two problems P1/P2, the ranked changes and the validation plan;
- **`02-traduction/architecture-cible.md` §1–§2** — goals G1–G6, the non-goals, and the 16 invariants I1–I16;
- **`README.md`** — the owner's five Phase-2 decisions, his answers to OQ-A/OQ-B, and **architecture amendment A-1**
  (Bergamot: one-click install, kept loaded while LIVE runs, RAM cost accepted).

The UX design contract is **`02-traduction/ux-mode-degrade.md`** (an EXPERIENCE spec only; no companion DESIGN.md —
the visual identity is frozen in `Theme.xaml`). The technical contract is `architecture-cible.md` §3–§16 plus the
ordered increments of `plan-migration.md`.

**Reading rules for this document**

- Every story that touches production code is marked **⛔ Phase 4 — owner's go required**. Nothing here is
  implemented; Phase 4 starts only on the owner's explicit go (ground rule 2, `README.md`).
- **Size legend:** S ≈ half a day · M ≈ 1–2 days · L ≈ 3+ days, for an agent working with review.
- One story ≈ one PR wherever possible. Branch names `feature/…` / `fix/…` / `docs/…`; no direct pushes to `main`,
  no self-merging (`project-context.md`).
- `file:line` references are at the baseline commit and are copied from the Phase-1/Phase-2 documents; none are
  invented.
- **Owner tasks** (no code, cannot be delegated to an agent) are marked **👤 owner task**.
- **Spikes** are marked **🔬 spike** and every one carries a measurable exit criterion and an explicit go/no-go.

---

## Requirements Inventory

### Functional Requirements

Extracted from `SYNTHESE.md` §3.3–§3.5, `architecture-cible.md` §1.1 (G1–G6) and §4–§13, `plan-migration.md`
increments 0–7 and track P, and `README.md` (owner's decisions 1–5, OQ-A/OQ-B answers, amendment A-1).

```
FR1:  Every provider outcome (status code, content-type, body head, transport exception) is classified into one
      typed TranslationErrorKind in exactly one place.
FR2:  An HTML abuse/captcha page is detected before the body is parsed as JSON, on any status including 200.
FR3:  Every non-success or exceptional attempt emits a per-request diagnostic log line carrying provider, endpoint,
      direction, attempt, status, elapsed, Retry-After, content-type, length, ipv, burst60 and a de-tagged body head.
FR4:  The user-facing message is one sentence per error kind, plus a countdown when a retry time is known.
FR5:  Each provider has a circuit breaker that opens on RateLimited/Blocked/QuotaExhausted/AuthFailed, escalates
      x2 per strike, caps at 30 minutes, half-opens with exactly one probe, and closes on a successful probe.
FR6:  Each provider has a client-side rate ceiling (token bucket, capacity 2, one token per 500 ms) with one token
      and the half-open probe reserved for Interactive callers.
FR7:  Gate state is process-global, shared by both chains, and survives a process restart.
FR8:  A provider makes at most 2 attempts with full jitter, and never retries a RateLimited, Blocked or Network
      outcome.
FR9:  A Retry-After header (delta-seconds or HTTP-date) is parsed, clamped and overrides the computed window.
FR10: Every gate transition (open / half-open / closed) is logged once, never per skipped request.
FR11: A translation request walks an ordered chain of tiers, skips gate-open tiers without an HTTP request, and
      raises exactly one AllProvidersPaused outcome carrying the earliest retryAt when every tier is skipped.
FR12: clients5.google.com/translate_a/t?client=dict-chrome-ex is the default free provider.
FR13: edge.microsoft.com/translate/translatetext is an independent keyless second vendor.
FR14: The current gtx endpoint is kept as the last free tier, renamed and demoted.
FR15: The read chain and the write chain are built once, in the constructor, per the composition of
      architecture-cible.md 8.1; DeepL is unreachable from the read path by construction.
FR16: A batch count mismatch never pads; it falls through to per-line only while the batch is <= 8 lines, and is a
      BadResponse above that.
FR17: One shared translation cache store serves both the read and the write decorator, persists to disk, holds 2000
      entries MRU-ordered, and stores successes only.
FR18: Saving a provider key no longer discards the session's accumulated translations.
FR19: A LIVE tick that finds every read tier gate-open performs no capture, no OCR, no dedup and no request, and
      backs off x2 to a 5 s cap.
FR20: LIVE auto-stops on 5 consecutive translating failures OR 5 failures inside a 2-minute window; an empty tick
      and a gate-open tick are neither a success nor a failure.
FR21: Rows that failed during an outage are re-translated in place once a provider recovers, bounded and cleared on
      stop.
FR22: Read-once reports the real outcome (never an unconditional "Done") and runs under a cancellable 30 s budget.
FR23: Azure AI Translator is available as a provider over a raw HttpClient with native array batching.
FR24: The Azure key and region are persisted, restored re-entrancy-safely, and validated together.
FR25: "Use my key for screen reading" is an explicit opt-in, off by default.
FR26: Each surface (Translator tab, Screen OCR tab, feed rows, compact overlay, About tab) shows the current
      provider state per the eight-state model of ux-mode-degrade.md 2.1.
FR27: At most one countdown per window, ticking at 1 Hz, running only while something is paused.
FR28: A row awaiting retry renders as pending, not as a terminal failure.
FR29: An offline engine can be installed in one click from the About tab, is used only as the terminal fallback,
      stays loaded while LIVE is running and is unloaded after LIVE stops plus an idle timeout.
FR30: The offline engine ships only if four measurements (RAM, load time, post-glossary quality, cold-start impact)
      pass, and the decision is published either way.
FR31: Released artefacts (exe and MSI) are code-signed by SignPath Foundation, the exe signed before the MSI wraps it.
FR32: The README, every release note and a one-time in-app toast set the expectation that the first launch after an
      update is slower.
FR33: The release checklist covers the two new state files.
FR34: A key can be tested from the About tab without spending translation quota where the provider allows it.
FR35: The startup diagnostics are run on at least two affected personal machines and one fast control machine.
```

### NonFunctional Requirements

```
NFR1:  Nothing new touches the disk or the network before the window is visible; the gate file and the cache file
       load lazily, on first use, off the UI thread (I10, G6).
NFR2:  Nothing new is always-resident. The single exception is the offline engine while LIVE runs, accepted
       explicitly by the owner in amendment A-1 (+127-310 MiB while active).
NFR3:  The test suite stays headless-safe and runs on every PR; new gate tests live in one non-parallel xUnit
       collection.
NFR4:  No user text, no q=, no full URL and no API key ever reaches the log or the error report (I11).
NFR5:  Every increment is shippable on its own and reversible on its own (one PR, one revert).
NFR6:  No MVVM, no i18n/.resx, no DI container, no new NuGet package outside the Bergamot prototype branch, no
       multi-q= batching, no ReadyToRun / trimming / AOT / single-file compression, no WPF Clipboard.
NFR7:  Repaint budget: one 1 Hz timer for the whole app, stopped when idle, one text assignment per second at most,
       no per-row countdown, no new animation.
NFR8:  Zero HTTP requests are issued while a path is paused.
NFR9:  Every new persisted control follows the _restoringSettings pattern; changed defaults reach existing users
       only via SettingsVersion + Migrate (I12, I13).
NFR10: The application licence stays MIT (SignPath Foundation eligibility); MPL-2.0 enters only on the prototype
       branch and is declared in the About tab.
NFR11: Accessibility: never colour alone - every state carries glyph + word + colour; AutomationProperties.Name on
       the chip; contrast unchanged.
NFR12: A translation failure never opens a modal, steals focus, plays a sound or activates a window.
NFR13: Services/ stays free of UI types and unit-testable headless (I2).
```

### Additional Requirements

From `architecture-cible.md` §3.1, §11, §15 and §16, and `plan-migration.md`.

- **No starter template.** This is a brownfield repository at `4759712`; there is no scaffolding story. Epic 1
  Story 1 is a testability seam, not a project setup.
- The 16 invariants **I1–I16** (`architecture-cible.md` §2) bind every story; each epic below lists the ones it
  must guard.
- The new types of the glossary (§16) have fixed target files; a story that creates a type creates it there.
- `TranslationPolicy.cs` is the single home of every tunable number, each carrying its evidence grade in a comment.
- `ProviderGates`, `TranslationCacheStore` and the providers get the same test-seam discipline the repo already
  uses twice: `SettingsService.PathOverride` (`SettingsService.cs:95`) and `Logging.DirectoryOverride`
  (`Logging.cs:31-39`).
- `Services/FallbackTranslator.cs` is **deleted**; `Services/TranslationService.cs` is **renamed**; the
  "Translation Pipeline Rules" section of `project-context.md` must be updated in the same PR (R5).
- Nine **[UNKNOWN]s U1–U9** (§15.1) must be settled by a capture, a measurement or a field log — never by a guess.
- Track P touches no translation code and runs in parallel.

### UX Design Requirements

Extracted from `ux-mode-degrade.md` in full. Each is specific enough to carry testable acceptance criteria.

```
UX-DR1:  The eight provider-status states S1-S8 (2.1), each with its entry condition and its "must the user act?"
         answer; Cancelled is not a state and must render nothing at all.
UX-DR2:  The per-surface state table of 2.2 - Translator status line, Screen OCR status line, feed rows, compact
         overlay, About tab - rendered by the six existing setters listed there.
UX-DR3:  The provider chip: one TextBlock, glyph + name + optional countdown, in three existing containers
         (MainWindow.xaml:270-279, MainWindow.xaml:296-302, prefix of CompactOverlay.xaml:44).
UX-DR4:  The chip tooltip listing the whole chain, one line per provider, reusing the load-bearing dark ToolTip
         style in Theme.xaml.
UX-DR5:  The countdown pattern of 2.4: 1 Hz, one DispatcherTimer for the whole app, stopped when nothing is paused,
         m:ss under 90 s then "about N min", repaint guard, at most one per window, "about to retry" under 5 s.
UX-DR6:  The LIVE heartbeat freezes on the muted glyph while the path is paused, on both the main window
         (MainWindow.xaml:299) and the overlay (CompactOverlay.xaml.cs:77-83).
UX-DR7:  One sentence per typed error kind (3.1), replacing the ten ad-hoc strings E1-E10 and the five DeepL ones.
UX-DR8:  The seven LIVE status strings of 3.2, replacing MainWindow.Live.cs:235 and :238.
UX-DR9:  The five read-once status strings of 3.3, replacing MainWindow.Ocr.cs:243-244; "Done" only when every line
         has a translation.
UX-DR10: The four compact-overlay quick-reply strings of 3.4, replacing CompactOverlay.xaml.cs:142, keeping the
         "your text is kept" promise verbatim.
UX-DR11: The one-time "fallback active" notice of 3.5, shown once per switch, never per row and never as a toast.
UX-DR12: The offline-engine consent dialog of 3.6, user-initiated only, never raised by a background failure;
         download progress with Cancel; failure and completion copy.
UX-DR13: The nine key-validation result strings of 3.7 for DeepL and Azure, including the wrong-region case.
UX-DR14: The P1 expectation copy of 3.8 in three placements: README bullet, one release-note line every release,
         and a one-time in-app toast after a version change (suppressed in compact mode).
UX-DR15: The About-tab "Translation engines" block of 4.2: "In use now" line, "Chain" line, the two key blocks with
         Save and Test key, the read-path opt-in with its cost sentence, and the offline-engine block.
UX-DR16: The control-by-control specification of 4.3, including AzureRegionCombo seeded with nine regions and free
         text accepted, and the _restoringSettings contract for every new control.
UX-DR17: The six interaction flows of 5 (a)-(f), including "recovered rows re-translate in place, never appended
         twice" and "a persisted pause is visible before the user's first action".
UX-DR18: Accessibility and footprint (6): never colour alone, AutomationProperties.Name on the chip, no new fonts,
         images or colours, one repaint per second at most.
UX-DR19: Every user-facing string in the copy deck exists exactly once in the codebase - no ad-hoc re-phrasing at a
         call site.
```

### FR Coverage Map

| FR | Epic | Story |
|---|---|---|
| FR1 | E1 | E1.S2, E1.S3 |
| FR2 | E1 | E1.S4 |
| FR3 | E1 | E1.S5 |
| FR4 | E1 → E7 | E1.S6 (shape), E7.S1 (copy) |
| FR5 | E2 | E2.S1, E2.S2 |
| FR6 | E2 | E2.S3 |
| FR7 | E2 | E2.S4 |
| FR8 | E2 | E2.S5 |
| FR9 | E2 | E2.S5 |
| FR10 | E2 | E2.S6 |
| FR11 | E3 | E3.S3 |
| FR12 | E3 | E3.S4 |
| FR13 | E3 | E3.S5 |
| FR14 | E3 | E3.S6 |
| FR15 | E3 | E3.S7 |
| FR16 | E3 | E3.S8 |
| FR17 | E4 | E4.S1, E4.S2, E4.S4 |
| FR18 | E4 | E4.S4 |
| FR19 | E5 | E5.S1 |
| FR20 | E5 | E5.S2 |
| FR21 | E5 | E5.S3 |
| FR22 | E5 | E5.S4 |
| FR23 | E6 | E6.S2 |
| FR24 | E6 | E6.S3 |
| FR25 | E6 | E6.S4 |
| FR26 | E7 | E7.S1, E7.S3, E7.S4, E7.S5, E7.S7 |
| FR27 | E7 | E7.S2 |
| FR28 | E7 | E7.S6 |
| FR29 | E8 | E8.S2, E8.S3 |
| FR30 | E8 | E8.S4, E8.S5 |
| FR31 | E9 | E9.S1–E9.S8 |
| FR32 | E9 | E9.S9, E9.S10 |
| FR33 | E9 | E9.S11 |
| FR34 | E6 | E6.S5 |
| FR35 | E9 | E9.S12 |

---

## Epic List

The nine epics are **the nine of `plan-migration.md` §"Story cut for Phase 3"**, unchanged in scope and order. Only
the identifiers change: the letters A–H and P become **E1–E9** so story ids read `E#.S#` as the orchestrator's brief
requires. The mapping is one-to-one and is printed in every epic header, so nothing is lost.

**Why the epics were not re-cut.** BMAD's epic-design step asks for user-value epics and warns against technical
layers. Three of these epics (E1, E2, E4) would fail that test read literally. They are kept anyway, deliberately,
and the justification is recorded here rather than hidden:

1. The epics are **release and revert boundaries**, not architectural layers. `plan-migration.md` fixes each
   increment as "shippable on its own, reversible on its own, one PR, one revert". Regrouping the stories by user
   value would destroy exactly the property the owner is buying.
2. The design is **already validated** — architecture approved by Winston, UX written by Sally, both arbitrated by
   the owner. BMAD's own guidance ("when the outcome is certain and direction changes between epics are unlikely,
   prefer fewer but larger epics") applies in the other direction here: the risk boundaries are real (increment 1 is
   graded medium-high, increment 7 high) and each one is where the owner may stop.
3. The **file-churn check is acknowledged**: E1, E2, E3 all touch `Services/TranslationService.cs` (E3 renames it)
   and `MainWindow.xaml.cs`. Consolidation was considered and rejected: increment 1 shipped alone is *worse for the
   user* (R7), which is precisely why it must be a separate revertible PR that ships in the same release as
   increment 2. That is a genuine feedback boundary, not churn.

| Epic | Title | Increment | Goal |
|---|---|---|---|
| **E1** (was A) | Diagnosable translation pipeline | 0 | A translation failure produces evidence instead of a shrug. |
| **E2** (was B) | Provider gate | 1 | The app stops answering "stop" with more requests. |
| **E3** (was C) | Provider chain and the new default endpoint | 2 | A throttled provider stops being a dead end. |
| **E4** (was D) | Shared persistent cache | 3 | A line translated once is free forever, on both paths. |
| **E5** (was E) | Honest live translation | 4 | LIVE tells the truth, costs nothing while paused, and loses nothing. |
| **E6** (was F) | Bring your own key: Azure | 5 | A user with a free Azure key gets a better, metered tier under their control. |
| **E7** (was G) | Degraded-mode UX | 6 | A player can tell paused from broken from working, at a glance, on any surface. |
| **E8** (was H) | Offline fallback prototype | 7 | Translation keeps working with no internet at all — if the measurements allow it. |
| **E9** (was P) | Packaging, signing and expectations | parallel track P | The first launch after an update stops reading as a bug. |

**Epic dependencies** (an epic never requires a later epic to function):

```
E1 ──> E2 ──> E3 ──> E4
                ├──> E5 ──> E7
                ├──> E6 ──┘
                └──> E8
E9 is independent of all of them and can start immediately.
```

**The one release coupling, stated up front.** **E2 and E3 are separate PRs but a single release.** The owner's
decision 2 couples the endpoint switch to the hardening; `architecture-cible.md` R7 adds why it must not ship
*before* it either — a circuit breaker in front of an endpoint that returns 429 on request #1 is an app that is
correctly paused all the time. E1 may ship on its own. E4 through E8 may each ship on their own.

---

## Epic 1 (A): Diagnosable translation pipeline

**Increment 0. Zero behaviour change — this is the increment that makes every later one provable.**

**Goal.** When a player reports "Google is limiting translations", the About tab's "Copy error report" contains the
status code, the content type, the body head and the request cadence that prove what happened — instead of being
empty, which is what it is today for exactly that failure (`analyse…` §5.1 [CONFIRMED-ABSENT]).

**Scope.** `HttpMessageHandler` seams, `TranslationErrorKind` + typed `TranslationException`, `ProviderErrorMapper`,
HTML sniffing, the per-request log line, `Friendly()` as a `Kind` switch with **wording unchanged**, and
`TranslationPolicy` holding today's numbers.

**Out of scope.** Any behaviour change, any wording change, any new provider, the gate.

**Dependencies.** None. **Increment mapping:** `plan-migration.md` increment 0.

**Invariants this epic must keep:** I1 (interface shape), I2 (Services stays UI-free), **I3 (the OCE filter — the
mapper's `Cancelled` contract is I3 written down)**, I11 (nothing sensitive in the log), I16 (the LIVE loop is not
touched at all).

**Definition of done for the epic.** Suite green and still headless-safe. A fake handler can return 429, 403, a
200-with-HTML and a timeout, and each produces the right `Kind`. A manual translation failure now writes a log line.
**No user-visible string changed.**

---

### Story E1.S1: Inject an `HttpMessageHandler` into every HTTP provider ⛔ Phase 4

As the maintainer,
I want every HTTP provider to accept a test handler,
So that the retry policy, the breaker and every response parser can be proven headless instead of being unreachable.

**Acceptance Criteria:**

**Given** `Services/TranslationService.cs:32` holds a `private static readonly HttpClient` that no test can reach
**When** the class gains `internal TranslationService(HttpMessageHandler? handler = null)`
**Then** passing `null` uses the existing shared static client and passing a handler creates a private
`new HttpClient(handler)`
**And** the same overload exists on `Services/DeepLTranslator.cs:16`
**And** the production shared clients are built on a `SocketsHttpHandler` with `PooledConnectionLifetime = 2 min`
(`TranslationService.cs:37-44`, `DeepLTranslator.cs:80-110`).

**Given** the suite has no HTTP test double at all (`analyse…` §6.4 gap 3)
**When** a `FakeHandler : HttpMessageHandler` is added to `tests/PWRUHelper.Tests`
**Then** it can return an arbitrary status, content-type and body, can throw a transport exception, can delay, and
counts the attempts it received.

**Given** `tests/PWRUHelper.Tests/TranslationBackendTests.cs:78` constructs the existing exception type
**When** the seam lands
**Then** that call site still compiles and the whole suite is green.

**Technical notes.** Files: `Services/TranslationService.cs:32,37-44` · `Services/DeepLTranslator.cs:16,80-110` ·
new `tests/PWRUHelper.Tests/FakeHandler.cs`. `InternalsVisibleTo PWRUHelper.Tests` (`PWRUHelper.csproj:28`) and the
`DefaultItemExcludes` exclusion of `tests\**` must both stay intact.

**Test expectations.** Unit: a request routed through the fake handler reaches the provider's parser. No STA, no
network.

**Size:** S · **Depends on:** — · **Risk:** low. · **DoD:** the fake handler is used by at least one new test, the
suite is green and still headless-safe.

---

### Story E1.S2: Introduce `TranslationErrorKind` and a typed `TranslationException` ⛔ Phase 4

As a support-reading maintainer,
I want every translation failure to carry a machine-readable kind,
So that the message a player sees and the decision the code takes stop being derived from a string.

**Acceptance Criteria:**

**Given** `Services/TranslationErrors.cs` does not exist
**When** it is created
**Then** it declares the 11-value `TranslationErrorKind` of `architecture-cible.md` §4.1 verbatim
(`RateLimited, Blocked, Unavailable, Timeout, Network, BadResponse, QuotaExhausted, AuthFailed, Cancelled,
AllProvidersPaused, Unknown`)
**And** `TranslationException` carries a required `Kind` plus optional `RetryAt` and `ProviderId`
**And** **there is no message-only constructor**.

**Given** the twelve existing throw sites (`TranslationService.cs:143,145,148,157,175`;
`DeepLTranslator.cs:53,60,87,91,99,127`)
**When** the message-only constructor is removed
**Then** the compiler forces each of them to state a `Kind`, and each one states the `Kind` the §4.2 table assigns.

**Given** the `Cancelled` contract of §4.1
**When** a provider recognises a genuine cancellation
**Then** it **rethrows the original `OperationCanceledException`** and never wraps it; a `TranslationException` with
`Kind.Cancelled` is never constructed anywhere in the codebase.

**Technical notes.** New file `Services/TranslationErrors.cs` (glossary §16). One test line updates:
`TranslationBackendTests.cs:78`.

**Test expectations.** Unit: constructing each `Kind`; a compile-time-enforced absence is asserted by review, not by
a test. The existing DeepL code-mapping cases (`TranslationBackendTests.cs:9-46`) must pass with the new ctor.

**Size:** S · **Depends on:** — · **Risk:** low; a wrong `Kind` at a throw site is caught by E1.S3's table test. ·
**DoD:** no `TranslationException` in the codebase is constructed without a `Kind`.

---

### Story E1.S3: Classify every provider outcome in one `ProviderErrorMapper` ⛔ Phase 4

As the maintainer,
I want one function that turns a raw HTTP outcome into a kind,
So that a bot block stops reading like a bug and no second place can ever disagree about what a 403 means.

**Acceptance Criteria:**

**Given** the ordered rules of `architecture-cible.md` §4.2 (first match wins)
**When** `ProviderErrorMapper.Classify(resp, bodyHead, transport, keyWasSent, ct)` is called
**Then** it returns `Cancelled` only when `ct.IsCancellationRequested`; `Timeout` for an OCE with the token **not**
cancelled; `Network` for `HttpRequestException`; `RateLimited` for 429; `AuthFailed` for 401; `QuotaExhausted` for
403 with a key and a quota envelope; `AuthFailed` for 403 with a key; `Blocked` for 403 with no key;
`QuotaExhausted` for 456; `Unavailable` for ≥500; `BadResponse` for an unparseable success; `Unknown` for anything
else non-success.

**Given** today's `TranslationService.cs:138-143` folds 403 with 400/404 into
`Translation service error (HTTP {code})`
**When** the mapper lands
**Then** a 403 with no key classifies as `Blocked` and a 400 classifies as `Unknown` — they are no longer the same
outcome.

**Given** the mapper is a total function
**When** it is handed an outcome no rule matches
**Then** it returns `Unknown` and never throws, never guesses.

**Technical notes.** New file `Services/ProviderErrorMapper.cs` (glossary §16). Called only from
`HttpProviderCore` once E2 lands; in this increment it is called from the existing retry loop
(`TranslationService.cs:119-178`) and from `DeepLTranslator.cs:80-110,125-129`.

**Test expectations.** Unit, through the fake handler: T14 (403 → `Blocked`, not folded with 400/404), plus one case
per row of the §4.2 table, plus the `Cancelled`-vs-`Timeout` pair which is the I3 regression guard.

**Size:** M · **Depends on:** E1.S1, E1.S2 · **Risk:** medium — a mis-classification changes a user-visible string
by accident; mitigated by E1.S6 keeping today's wording per `Kind`. · **DoD:** every §4.2 row has a passing test.

---

### Story E1.S4: Detect the HTML abuse page before parsing, never after ⛔ Phase 4

As a player,
I want a Google block page to be reported as a block,
So that I stop being told the service "returned an unexpected response" when it plainly told us to stop.

**Acceptance Criteria:**

**Given** today an HTML block page served with a 200 becomes an "unexpected response" only by accident, through the
JSON parser at `TranslationService.cs:172-177`
**When** the §4.3 sniffing lands
**Then** the body is classified **before** it is parsed: content-type `text/html` or a non-JSON media type takes the
HTML path; otherwise the first non-whitespace character being `<` in the first 200 characters takes the HTML path;
only a body that survives both is handed to `JsonDocument.Parse`.

**Given** the HTML path
**When** the first ~400 characters are de-tagged and lower-cased
**Then** a marker from `TranslationPolicy.RateLimitMarkers` (`"automated queries"`, `"unusual traffic"`) yields
`RateLimited`; a marker from `BlockMarkers` (`"we're sorry"`, `"captcha"`, `"recaptcha"`) yields `Blocked`; no marker
yields `Blocked` with the de-tagged 120-character head recorded in the log line.

**Given** the body measured on 2026-09-06 (`benchmark…` §3.1): *"Sorry... We're sorry... but your computer or network
may be sending automated queries."*
**When** it arrives on **any** status, 200 included
**Then** it classifies as `RateLimited`.

**Technical notes.** `Services/ProviderErrorMapper.cs`; markers are `const` in `Services/TranslationPolicy.cs` so a
re-phrasing is one edit.

**Test expectations.** Unit T13: the verbatim 429 HTML body is a fixture in the suite; asserting it never reaches
`JsonDocument.Parse` is done by asserting the classification and by a parser that throws if called.

**Size:** S · **Depends on:** E1.S3, E1.S7 · **Risk:** low. · **DoD:** T13 passes; the fixture is the real captured
body, not a paraphrase.

---

### Story E1.S5: Emit the per-request diagnostic log line ⛔ Phase 4

As the owner reading a player's pasted error report,
I want one line per failed attempt with the status, the headers that matter and the request cadence,
So that the next P2 incident is a measurement instead of an argument.

**Acceptance Criteria:**

**Given** `TranslationService` logs nothing today
**When** an attempt ends non-success or exceptional
**Then** exactly one `Logging.Warn` line is emitted in the shape of `architecture-cible.md` §10.1, carrying
`provider`, `ep`, `dir`, `attempt=n/m`, `cid`, `status`, `elapsed`, `retry-after`, `ct`, `len`,
`hdrs=[via/srv/xrl/set-cookie]`, `bytes`, `lines`, `burst60`, `ipv`, and `body` **only** for a non-JSON body
(de-tagged, whitespace-collapsed, first 120 characters).

**Given** a successful attempt
**When** it completes
**Then** it is counted into `burst60` but **not** logged line by line (the log is capped at 1 MB with one rollover,
`Logging.cs:69,95-105`).

**Given** I11
**When** any line is written
**Then** it contains no user text, no `q=`, no full URL and no API key — asserted by a test that translates a
recognisable sentence with a recognisable key through the fake handler and greps the produced file.

**Given** `LogWriter.Write` (`Logging.cs:85`) writes second-resolution timestamps
**When** this story lands
**Then** the timestamp format gains milliseconds, so retry spacing is readable.

**Technical notes.** Emitted from the existing retry loop (`TranslationService.cs:132-153,172-177`) via
`Logging.Warn` (`Logging.cs:44`). `Logging` is otherwise unchanged. `ipv` is the address family actually used
(§10.1, from `mecanismes…` Q4).

**Test expectations.** Unit through `Logging.DirectoryOverride` (`Logging.cs:31-39`, pattern proven by
`LoggingTests.cs:89,101`): the field set, and the negative assertion above.

**Size:** M · **Depends on:** E1.S1, E1.S3 · **Risk:** low-medium — the I11 negative assertion is the load-bearing
test. · **DoD:** "Copy error report" (`MainWindow.xaml.cs:312-322`) is no longer empty for a P2-shaped failure.

---

### Story E1.S6: Map `Friendly()` onto `Kind` without changing any wording ⛔ Phase 4

As a player,
I want this increment to be invisible,
So that a refactor cannot be blamed for a message I have never seen before.

**Acceptance Criteria:**

**Given** `MainWindow.xaml.cs:404-410` builds its message from exception types
**When** it becomes a `Kind` switch in the shape of §4.4
**Then** each `Kind` returns **one of today's existing strings**, chosen so that every string a user can currently
reach is still reachable, and **no new string is introduced in this increment**.

**Given** the parenthesised feed-row wrapper `$"({Friendly(ex)})"` at `MainWindow.Live.cs:281` and
`MainWindow.Ocr.cs:298`
**When** this story lands
**Then** those two call sites are untouched.

**Given** a diff of the user-visible strings before and after
**When** it is taken
**Then** it is empty.

**Technical notes.** `MainWindow.xaml.cs:404-410` only. The `«Sally: …»` real copy arrives in E7.S1; this story is
the seam that makes that a one-file change.

**Test expectations.** Unit on the switch (it is `private static` — expose as `internal` or test through a thin
wrapper; do not restructure `MainWindow` for it).

**Size:** S · **Depends on:** E1.S2 · **Risk:** low — this is the mitigation for E1.S3's risk. ·
**DoD:** no user-visible string changed, verified by diff.

---

### Story E1.S7: Create `TranslationPolicy` holding today's numbers ⛔ Phase 4

As the maintainer,
I want every tunable number in one file before anything tunes one,
So that the constants that will be argued about in the field are already in the place the argument can be settled.

**Acceptance Criteria:**

**Given** `Services/TranslationPolicy.cs` does not exist
**When** it is created per `architecture-cible.md` §5.6
**Then** it is `internal static`, every member is `const` or `static readonly`, and **every member carries a comment
with its evidence grade** ([ASSUMED], [MEASURED], [CONFIRMED]).

**Given** this increment must be behaviour-neutral
**When** the file is created
**Then** it holds **today's** values where a value already exists in code (12 s timeout, 3 attempts, 300/600 ms
spacing, cache capacity 500) and the §5.6 target values are **not** yet wired to anything.

**Given** the HTML markers of §4.3
**When** they are needed by E1.S4
**Then** they live here as `RateLimitMarkers` and `BlockMarkers`.

**Technical notes.** New file `Services/TranslationPolicy.cs` (glossary §16). This story is **not** in
`plan-migration.md`'s story cut but it **is** in increment 0's scope, item (e); it is added here and the omission is
recorded in the readiness report.

**Test expectations.** None of its own; it is consumed by E1.S4's markers.

**Size:** S · **Depends on:** — · **Risk:** none. · **DoD:** the file exists, no behaviour changed.

---

## Epic 2 (B): Provider gate

**Increment 1. Risk medium-high — this epic must not reach a release without Epic 3.**

**Goal.** The app stops keeping its own block alive. Google's block page states the release condition — *"the block
will expire shortly after those requests stop"* — and today the app answers each 429 with two more requests inside
900 ms plus a LIVE tick 700 ms later (`analyse…` §1.5, A1–A4). After this epic, a 429 costs **one** request and the
provider is left alone until the window passes.

**Scope.** `ProviderGate` (circuit breaker + token bucket, injectable clock, `RequestPriority`), the `ProviderGates`
static registry, `ProviderStateStore` over `provider-state.json`, `HttpProviderCore`, the new retry policy,
`Retry-After`, gate-transition logging.

**Out of scope.** New providers, the chain, the cache, anything the user reads.

**Dependencies.** E1. **Increment mapping:** `plan-migration.md` increment 1. **Ships in the same release as E3.**

**Invariants this epic must keep:** **I9 (gate state is process-global and persisted)**, **I10 (nothing new touches
disk or network before the window is visible)**, I2, I3, I11, I16.

**Definition of done for the epic.** The gate opens on a simulated 429, refuses every subsequent call until the
window passes, admits exactly one probe, closes on success, and survives a simulated process restart. A 429 costs
one HTTP request, not three. The log shows the transitions. `ProviderGates` appears in **no** startup path.

---

### Story E2.S1: `ProviderGate` — circuit breaker with strikes, cap and half-open probe ⛔ Phase 4

As a player behind a shared ISP address,
I want the app to stop asking after it has been told to stop,
So that the block Google put on my connection actually expires.

**Acceptance Criteria:**

**Given** the state machine of `architecture-cible.md` §5.2 and the `Kind` table of §5.3
**When** `ReportFailure(RateLimited)` or `ReportFailure(Blocked)` arrives
**Then** `strikes` increments and `blockedUntil = now + OpenBaseSeconds × 2^(strikes−1)`, clamped at
`OpenCapMinutes` (60 s → 2 min → 4 min … 30 min).

**Given** an open gate
**When** `TryEnter` is called before `blockedUntil`
**Then** it returns `Open(retryAt)` and **no HTTP request is made**; when it is called at or after `blockedUntil`,
**exactly one** caller receives `Probe` and every concurrent caller receives `Open(now + probeTimeout)`.

**Given** a half-open probe
**When** it succeeds
**Then** the gate closes, `strikes = 0`, `blockedUntil = null`; when it fails with an opening kind, `strikes`
increments and the window doubles.

**Given** `QuotaExhausted`
**When** it is reported
**Then** the gate opens for `QuotaOpenMinutes` (60) with **no** strike escalation; given `AuthFailed`, it opens with
`blockedUntil = DateTimeOffset.MaxValue` until `ProviderGates.ClearAuthBlock(id)` is called.

**Given** `Unavailable`, `Timeout`, `Network` or `Unknown`
**Then** the gate takes **no strike** and applies a `SoftCooldownSecs` (5) cooldown — without it, a DNS blip means
the next LIVE tick re-hits the same dead provider 700 ms later.

**Given** `BadResponse`
**Then** three **consecutive** occurrences open the gate for `OpenBaseSeconds`.

**Given** `Cancelled`
**Then** the gate is **never** touched.

**Given** `CleanResetMinutes` (10) of clean operation
**When** the next failure arrives
**Then** it opens for 60 s, not for the escalated window.

**Technical notes.** New `Services/ProviderGate.cs` with `GateDecision { Allow | Wait(TimeSpan) | Open(retryAt) |
Probe }` and `RequestPriority { Interactive, Background }` (glossary §16). Clock is an injectable
`Func<DateTimeOffset>`.

**Test expectations.** Unit T1 (opens, stays open, half-opens exactly once), T2 (escalation and 30-min cap), T3
(clean reset). All with a fake clock — **no `Task.Delay` in a gate test**.

**Size:** M · **Depends on:** E1 · **Risk:** medium. · **DoD:** T1–T3 pass; the whole §5.3 table has a case.

---

### Story E2.S2: `ProviderGates` registry, process-global and reset-able ⛔ Phase 4

As the maintainer,
I want one gate per provider id shared by both chains,
So that the Translator tab cannot keep hammering a provider that LIVE has already been told to leave alone.

**Acceptance Criteria:**

**Given** the read chain is a field initializer (`MainWindow.xaml.cs:43`) and the write chain is rebuilt on every key
save (`MainWindow.Translate.cs:239`) — two independent instances today
**When** `ProviderGates.For(id)` is introduced
**Then** both chains obtain the **same** `ProviderGate` instance for the same id, and `ProviderIds` declares
`google-dict`, `edge`, `google-gtx`, `deepl`, `azure`, `bergamot`.

**Given** the discipline already used twice in this repo (`SettingsService.cs:95`, `Logging.cs:31-39`)
**When** the registry lands
**Then** it exposes `internal static string? PathOverride`, `internal static Func<DateTimeOffset> Clock`,
`internal static void ResetForTests()` and `internal static void ClearAuthBlock(string id)`.

**Given** R4 (static state leaks between parallel xUnit collections)
**When** the gate tests are written
**Then** they live in **one non-parallel xUnit collection** whose fixture calls `ResetForTests()`, and no gate test
touches the real `%AppData%`.

**Technical notes.** New `Services/ProviderGates.cs` (glossary §16).

**Test expectations.** Unit: two `For(id)` calls return the same instance; `ResetForTests` clears; the collection is
declared non-parallel.

**Size:** S · **Depends on:** E2.S1 · **Risk:** medium — this is R4. · **DoD:** the suite is green with the test
runner's default parallelism unchanged.

---

### Story E2.S3: Rate ceiling with an interactive-priority reserve ⛔ Phase 4

As a player typing a reply while LIVE is running,
I want my Enter key to be served,
So that a background loop cannot spend the whole budget on the feature I am not looking at.

**Acceptance Criteria:**

**Given** the token bucket of §5.4 — capacity 2, one token per `MinSpacingMs` (500 ms)
**When** `TryEnter` finds the bucket empty
**Then** it returns `Wait(t)` where `t` is the time to the next token, and it is consulted **before** the request,
never after.

**Given** a `Background` caller (LIVE, read-once)
**When** exactly one token remains
**Then** it is **refused that token** — one token of the capacity-2 bucket is reserved for `Interactive`.

**Given** a `Background` caller finds the gate probe-eligible
**When** it would take the probe
**Then** it defers once by `ProbeDeferMs` (1 s), so a user pressing Enter inside that second gets the probe instead.

**Given** a `Wait(t)` with `t > MaxSpacingWaitMs` (2 s)
**When** the caller is a chain
**Then** the tier is treated as unavailable and the chain moves on rather than waiting.

**Technical notes.** `Services/ProviderGate.cs`; this is "one enum and two `if`s, not a scheduler" (§5.4,
architect's concern #1).

**Test expectations.** Unit with a fake clock: a `Background` caller is refused the last token that an `Interactive`
caller then receives; the probe defer.

**Size:** S · **Depends on:** E2.S1 · **Risk:** low. · **DoD:** both priority behaviours have a test.

---

### Story E2.S4: Persist gate state to `provider-state.json`, lazily and atomically ⛔ Phase 4

As a player who restarted the app because it seemed stuck,
I want the pause to survive the restart,
So that the app does not immediately re-earn the block I was waiting out — the owner's own evidence is that
restarting does not clear it.

**Acceptance Criteria:**

**Given** the schema of §5.7
**When** a transition occurs (open, half-open → closed, strike reset)
**Then** the state is written to `%AppData%\PWRUHelper\provider-state.json` with the atomic pattern of
`SettingsService.Save` (`SettingsService.cs:186-197`: temp file, `File.Replace`, the whole method inside
`try { } catch { }`), debounced by 1 s and coalesced, plus once on `OnClosing`.

**Given** I10
**When** the app starts
**Then** the file is **not** read: it loads lazily on the **first `TryEnter` of the process**, off the UI thread —
verified by the absence of `ProviderGates` from any startup path.

**Given** a missing, corrupt, or future-`version` file
**When** it is loaded
**Then** the registry is empty, no exception is raised and the app is not blocked.

**Given** a `blockedUntil` more than `OpenCapMinutes` in the future (a machine whose clock jumped)
**When** the file is loaded
**Then** the value is clamped to the cap.

**Given** an unknown provider id in the file (a downgrade)
**When** the file is rewritten
**Then** that entry is preserved.

**Technical notes.** New `Services/ProviderStateStore.cs` (glossary §16); `ProviderGates` owns load/save.

**Test expectations.** Unit T4 (round-trip through `PathOverride`, then `ResetForTests`, then read back — still
open with `blockedUntil` preserved; corrupt file → empty registry, no throw) and T5 (clock-skew clamp). The
real-`%AppData%` guard pattern of `LoggingTests.cs:89` applies.

**Size:** M · **Depends on:** E2.S2 · **Risk:** medium — R3, the state outliving the condition it mirrors; mitigated
by the 30-minute cap and the half-open probe. · **DoD:** T4 and T5 pass; a startup-path grep for `ProviderGates`
returns nothing.

---

### Story E2.S5: `HttpProviderCore` — 2 attempts, jitter, no retry on 429/403, `Retry-After` honoured ⛔ Phase 4

As a player whose connection has been throttled,
I want a refusal to cost one request,
So that the app stops paying three times for being told no.

**Acceptance Criteria:**

**Given** today's loop retries three times with fixed 300/600 ms spacing and retries `HttpRequestException`
(`TranslationService.cs:126-153,151`)
**When** `HttpProviderCore` replaces it
**Then** `MaxAttempts = 2`; the delay is **full jitter**, `Random(0, BackoffBaseMs << attempt)`, so two instances
behind the same NAT stop retrying in lockstep; and **only `Unavailable` and `Timeout` are retried**.

**Given** a 429 or a 403
**When** it arrives
**Then** the provider raises immediately — exactly **one** HTTP request reaches the fake handler — and the gate owns
the wait.

**Given** the shared shape of §7.0
**When** any provider sends a request
**Then** `HttpProviderCore` calls `gate.TryEnter(priority)` **before** the request and `gate.ReportSuccess()` /
`gate.ReportFailure(kind, retryAfter)` **after** it; a provider never touches a gate directly; the per-request log
line of E1.S5 is emitted here; the timeout stays 12 s (`TranslationService.cs:39`); the existing plausible Chrome
User-Agent is kept and **not** rotated (`TranslationService.cs:41-42`).

**Given** a non-success response carrying `Retry-After`
**When** it is parsed
**Then** both forms (delta-seconds and HTTP-date) are accepted, the value is clamped to `[1 s, OpenCapMinutes]`, it
**overrides** the computed window, and it is logged verbatim — even though Google measurably sends none today
(`benchmark…` §3.1), because DeepL and Azure may.

**Given** I3
**When** an `OperationCanceledException` surfaces
**Then** it is filtered with `when (ct.IsCancellationRequested)` and a timeout is never mistaken for a user cancel.

**Technical notes.** New `Services/HttpProviderCore.cs` (glossary §16); `Services/TranslationService.cs:119-178`
retry loop replaced; `Services/DeepLTranslator.cs:56-111` moved onto it; the §5.6 real numbers land in
`Services/TranslationPolicy.cs`.

**Test expectations.** Unit T6 (**429 → exactly one attempt**, replacing the E1 pin), T7 (503 → exactly 2 attempts,
delay inside the jitter band), T8 (`Retry-After` both forms, clamped, overriding). The I3 guards
(`TranslationBackendTests.cs:84,98`) must still pass.

**Size:** L · **Depends on:** E2.S1, E2.S3, E1.S3, E1.S5 · **Risk:** **medium-high — this is the story that, alone,
makes the app "correctly paused almost all the time" against `gtx`. It must not reach a release without Epic 3.** ·
**DoD:** T6–T8 pass; a 429 costs one request in the log.

---

### Story E2.S6: Log gate transitions ⛔ Phase 4

As the owner reading a pasted error report,
I want the open/half-open/closed history with its strike escalation,
So that "it never clears" becomes a timeline instead of a feeling.

**Acceptance Criteria:**

**Given** §10.2
**When** a gate changes state
**Then** exactly one line is logged per **transition** — never per skipped request, or a 30-minute open window would
fill the log — in the shape
`gate google-dict OPEN kind=RateLimited strikes=1 for=60s until=13:30:02`, then `HALF-OPEN probe`, then
`CLOSED after probe ok, strikes reset`.

**Given** I11
**When** the lines are written
**Then** they contain a provider id and timings only: no user text, no URL, no key.

**Given** "Copy error report" (`MainWindow.xaml.cs:312-322`)
**When** it is copied after an incident
**Then** it contains the full gate history alongside the per-request lines (§10.3).

**Technical notes.** `Services/ProviderGates.cs` / `ProviderGate.cs` via `Logging.Warn`/`Info` (`Logging.cs:44`).

**Test expectations.** Unit through `Logging.DirectoryOverride`: one line per transition, zero lines for N skipped
requests.

**Size:** S · **Depends on:** E2.S1 · **Risk:** low. · **DoD:** V1.2 of the validation plan is evidenceable from one
paste.

---

### Story E2.S7: Review and tune the gate windows from field logs 🔬 spike (U9) ⛔ Phase 4 (constants only)

As the owner,
I want the assumed windows corrected by data rather than by argument,
So that a policy calibrated to a reported range stops being a guess after the first release that carries it.

**Acceptance Criteria:**

**Given** `OpenBaseSeconds = 60`, ×2 escalation, `OpenCapMinutes = 30` and `CleanResetMinutes = 10` are all
**[ASSUMED]**, calibrated to a REPORTED range and never measured (§5.6, U9)
**When** the E2+E3 release has been in the field and at least **three** independent "Copy error report" pastes from
distinct connections have been collected
**Then** the gate-open rate, the escalation depth reached and the observed time-to-recovery are tabulated in this
folder.

**Exit criterion (measurable).** Median open-window count per LIVE hour and the maximum strike level reached, over
≥ 3 reports covering ≥ 6 LIVE hours in total.

**Go / no-go.** **Go (tune):** if the gate reaches strike ≥ 3 in more than one report, or opens more than twice per
LIVE hour at the shipped defaults, the constants are edited in `TranslationPolicy.cs` and the change is justified by
the table. **No-go (leave alone):** anything else — the defaults stand and U9 is closed as settled.
**Forbidden either way:** widening the retry policy back out (`plan-migration.md` §V1, "a negative result is still a
result").

**Technical notes.** `Services/TranslationPolicy.cs` only; `AppSettings.ProviderGateOverrides` (§12) is the
runtime hatch used to trial a value with a volunteer before committing it.

**Test expectations.** No new test; existing gate tests must stay green with the new constants (they assert
relationships, not literals — if one asserts a literal, it is updated deliberately in the same commit).

**Size:** S · **Depends on:** the E2+E3 release being in the field · **Risk:** low. · **DoD:** U9 is closed in
`architecture-cible.md` §15.1 with either a tuned value or a recorded "defaults confirmed".

---

## Epic 3 (C): Provider chain and the new default endpoint

**Increment 2. Ships in the same release as Epic 2 — separate PRs, one release.**

**Goal.** A throttled provider stops being a dead end. Today the read path has exactly one provider
(`analyse…` §6.2); after this epic it has three free tiers from two vendors, and a paused tier costs zero requests
and zero milliseconds.

**Scope.** The U1 and U2 captures, `ChainTranslator` replacing `FallbackTranslator`, `GoogleDictTranslator`,
`EdgeTranslator`, the `gtx` rename plus `TextChunker`, both chains built in the constructor, the per-line cap, and
the soak that tells us what the switch is actually worth.

**Out of scope.** The cache (E4), the LIVE loop (E5), any new user-facing wording beyond the one string
`AllProvidersPaused` needs (see the note in E3.S3).

**Dependencies.** E2 (hard — same release). **Increment mapping:** `plan-migration.md` increment 2.

**Invariants this epic must keep:** I1 (`ITranslator` untouched), **I5 (never pad a batch mismatch)**, I3, I6
(slang expansion stays upstream), I7 (per-message `ru`/`auto`), **I8 (DeepL unreachable from the read path by
construction)**, I16.

**Definition of done for the epic.** A translation succeeds through `dict-chrome-ex` on a network where `gtx` 429s.
Killing tier 1 with a fake handler transparently produces a tier-2 result. Opening every gate produces exactly one
`AllProvidersPaused` message with a countdown and **zero** HTTP requests. `project-context.md` matches the code
again.

---

### Story E3.S1: Capture the `dict-chrome-ex` batch behaviour 🔬 spike (U1)

As the architect,
I want to know whether a `\n`-joined multi-line query comes back with its newlines intact,
So that the LIVE request volume of the whole design is a measured number and not a hope.

**Acceptance Criteria:**

**Given** U1 is "the single most important [UNKNOWN] in this document" (§7.1)
**When** **one** request is sent, on a branch and with the owner's explicit go, carrying a `\n`-joined `q` of three
known lines to `clients5.google.com/translate_a/t?client=dict-chrome-ex`
**Then** the raw response is frozen as a test fixture in `tests/PWRUHelper.Tests` and recorded in this folder with
its date and the network it came from.

**Exit criterion (measurable).** The response either contains **exactly two `\n` separators between three
translated segments**, or it does not.

**Go / no-go.**
- **Go — newlines survive:** batching stays one request per group, as today.
- **No-go — newlines lost:** **OQ-A is already settled by the owner: one request per line.** The per-line path is
  bounded by `PerLineCap` (E3.S8), by the rate ceiling and by the gate; the expected cost is ≈2× the LIVE request
  volume, still far below today's. **Multi-`q=` remains a declined non-feature and is not to be proposed.**

**Given** ground rule 2 and the Phase-1 incident (`SYNTHESE.md` §5)
**When** this spike runs
**Then** it sends **one** request, from a connection the owner nominates, and nothing else.

**Technical notes.** Branch only; no production file changes. The captured fixture is the input to E3.S4's parser
tests (T12 shapes A `["x"]` and B `[["x","ru"]]`).

**Test expectations.** The fixture itself; no runtime test.

**Size:** S · **Depends on:** the owner's go for the two requests · **Risk:** low, provided the request budget is
respected. · **DoD:** U1 is closed in §15.1 with the captured evidence.

---

### Story E3.S2: Capture the Edge request and response shapes 🔬 spike (U2)

As the architect,
I want the real Edge contract before a line of `EdgeTranslator` exists,
So that a "second vendor" does not become a second way to fail.

**Acceptance Criteria:**

**Given** the request body, the response shape and the required headers of
`edge.microsoft.com/translate/translatetext` are all **[UNKNOWN]** (§7.2)
**When** one successful live request/response pair is captured on a branch
**Then** the exact body, the exact response JSON and the exact required headers are recorded and frozen as a
fixture.

**Exit criterion (measurable).** One HTTP 200 with a usable translation, and a written statement of which headers
were necessary (established by removing one at a time within the same short session, or explicitly marked
"not narrowed").

**Go / no-go.** **Go:** the capture succeeds ⇒ E3.S5 may start. **No-go:** the route is dead, needs a token, or
cannot be made to answer ⇒ **`EdgeTranslator` is not written**, the chain ships as GoogleDict → GoogleGtx, and the
loss of the independent second vendor is recorded as an increase in risk R1.

**Given** §7.2 states "no `EdgeTranslator` code is written before that capture exists"
**When** this story is not done
**Then** E3.S5 is blocked and may not be started.

**Technical notes.** Branch only. `edge.microsoft.com/translate/auth` has been 404 since ~2026-07-28 and this route
is believed keyless; confirm that in the capture.

**Test expectations.** The fixture itself.

**Size:** S · **Depends on:** the owner's go · **Risk:** medium — the route is known-good for ~1 month only. ·
**DoD:** U2 is closed in §15.1, or E3.S5 is cancelled with a recorded reason.

---

### Story E3.S3: `ChainTranslator` replacing `FallbackTranslator` ⛔ Phase 4

As a player,
I want a paused provider to be skipped instead of called,
So that the app stops spending requests on servers that have already refused.

**Acceptance Criteria:**

**Given** the algorithm of §6.2, identical for `TranslateAsync` and `TranslateLinesAsync`
**When** a tier's gate returns `Open(retryAt)`
**Then** the tier is skipped, its `retryAt` is remembered, and **no HTTP request and no measurable delay** are spent
on it.

**Given** a tier's gate returns `Wait(t)`
**When** `t <= MaxSpacingWaitMs`
**Then** the chain awaits `t`; when `t` is larger, the tier is skipped with `now + t` remembered.

**Given** every tier was skipped
**When** the chain finishes
**Then** it throws exactly one `TranslationException(AllProvidersPaused, …)` whose `RetryAt` is the **earliest**
remembered `retryAt` — not the latest, not the first tier's.

**Given** at least one tier actually tried and failed
**When** the chain finishes
**Then** it throws that tier's most specific failure, not `AllProvidersPaused`.

**Given** I3
**When** a real user cancel occurs mid-chain
**Then** the `OperationCanceledException` propagates (`catch … when (ct.IsCancellationRequested) throw;`), and a
`TaskCanceledException` from an `HttpClient` timeout falls through to the next tier instead.

**Given** I1
**When** `ChainTranslator` is introduced
**Then** `ITranslator` is unchanged; `ChainTier` is `(ProviderGate Gate, ITranslator Translator)`;
`Services/FallbackTranslator.cs` is deleted.

**Technical notes.** New `Services/ChainTranslator.cs` (glossary §16). The two most valuable existing tests
(`TranslationBackendTests.cs:84` real-cancel-propagates, `:98` timeout-falls-through) are **re-pointed** at
`ChainTranslator` and **keep their names** — they are the I3 regression guards.
**Copy note:** `AllProvidersPaused` needs a user-visible sentence in this release, before Epic 7 lands. Use
Sally's finished line verbatim (`ux-mode-degrade.md` §3.1): `All engines are paused — next try in {t}. Nothing you
need to do.` It is written; it is not a placeholder.

**Test expectations.** Unit T9 (the open tier's fake handler is never called; the next tier answers), T10
(`AllProvidersPaused` with the earliest `retryAt`), T11 (the two re-pointed I3 guards).

**Size:** M · **Depends on:** E2.S1 · **Risk:** medium. · **DoD:** T9–T11 pass; `FallbackTranslator.cs` is gone.

---

### Story E3.S4: `GoogleDictTranslator` as the new default free provider ⛔ Phase 4

As a player on a connection where the old endpoint is throttled,
I want translations to work again today,
So that the feature is usable while the structural work continues.

**Acceptance Criteria:**

**Given** §7.1
**When** the provider sends a request
**Then** it is
`GET https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl={src}&tl={tgt}&q={UrlEncode(text)}` with the
existing Chrome User-Agent unchanged and `MaxQueryBytes = 1500` UTF-8.

**Given** the two response shapes
**When** the root array's first element is a string
**Then** shape A is parsed as `root[0]`; when it is an array, shape B is parsed as `root[0][0]`; anything else is
`BadResponse`.

**Given** E3.S1's answer
**When** a multi-line group is translated
**Then** it is sent as one `\n`-joined `q` **if** newlines survive, and as one request per line **if** they do not
(OQ-A, settled: per line).

**Given** the error mapping of §7.1
**Then** 429 → `RateLimited`; 403 → `Blocked`; HTML on any status → §4.3; 5xx → `Unavailable`; unparseable JSON →
`BadResponse`.

**Given** the provider id
**Then** it is `google-dict` and its gate is `ProviderGates.For("google-dict")`.

**Technical notes.** New `Services/GoogleDictTranslator.cs` (glossary §16), built on `HttpProviderCore`, using
`TextChunker` from E3.S6. ToS posture: `clients5.google.com/robots.txt` has no `Disallow: /translate_a/`, unlike
`translate.googleapis.com` — strictly better than today, still not clean.

**Test expectations.** Unit T12 with the E3.S1 fixtures: shape A, shape B, the verbatim 429 HTML body →
`RateLimited`.

**Size:** M · **Depends on:** E3.S1, E3.S3, E2.S5 · **Risk:** medium — R1, an undocumented and rented endpoint; the
client id is a `const` in one place, one edit from a change. · **DoD:** a translation succeeds through
`dict-chrome-ex` on a network where `gtx` 429s.

---

### Story E3.S5: `EdgeTranslator` as the independent second vendor ⛔ Phase 4

As a player,
I want a fallback that is not Google,
So that one vendor's decision cannot take the whole feature away.

**Acceptance Criteria:**

**Given** E3.S2 produced a capture
**When** `EdgeTranslator` is written
**Then** it matches the captured body, response shape and headers **exactly**, and the capture is the parser's test
fixture.

**Given** the endpoint of §7.2
**Then** it is
`POST https://edge.microsoft.com/translate/translatetext?from={src or empty}&to={tgt}&isEnterpriseClient=false`,
keyless, with auto-detect obtained by omitting `from`.

**Given** the error mapping of §7.2
**Then** 429 → `RateLimited`; 401/403 → `Blocked` (no key is sent, so never `AuthFailed`); 5xx → `Unavailable`;
non-JSON → §4.3 then `BadResponse`.

**Given** E3.S2 returned no-go
**When** this story is reached
**Then** it is **cancelled**, not guessed.

**Technical notes.** New `Services/EdgeTranslator.cs` (glossary §16), provider id `edge`, on `HttpProviderCore`.

**Test expectations.** Unit T12 (Edge fixture) plus the error-mapping cases.

**Size:** M · **Depends on:** E3.S2 (hard gate), E3.S3 · **Risk:** medium. · **DoD:** killing tier 1 with a fake
handler transparently produces a tier-2 result.

---

### Story E3.S6: Demote `gtx` — rename to `GoogleGtxTranslator`, extract `TextChunker` ⛔ Phase 4

As the maintainer,
I want the old endpoint kept but last,
So that a `client=` id that may come back costs nothing to keep while it stops being the default.

**Acceptance Criteria:**

**Given** `Services/TranslationService.cs`
**When** it is renamed to `Services/GoogleGtxTranslator.cs` with provider id `google-gtx`
**Then** the endpoint, the User-Agent, the chunking and the batch/per-line shape are otherwise untouched; only typed
errors, 2 attempts with jitter, gate admission and logging change.

**Given** two providers now need chunking
**When** `ChunkText` and `HardSplit` move to `Services/TextChunker.cs`
**Then** `tests/PWRUHelper.Tests/ServicesTests.cs:80,91` are re-pointed and the byte-budget and
content-preservation assertions pass unchanged.

**Given** R5 (the rename breaks docs and muscle memory)
**When** this story lands
**Then** the **same PR** updates the "Translation Pipeline Rules" section of `project-context.md`, which names
`TranslationService` today.

**Given** the existing `rateLimited` latch (`TranslationService.cs:99-105`)
**When** the provider is ported
**Then** the latch is **kept** (I16) and now latches specifically on a `RateLimited`/`Blocked` `Kind` rather than on
any `TranslationException`.

**Technical notes.** Rename + new `Services/TextChunker.cs` (glossary §16). Deletion implied by the design:
`Services/TranslationService.cs` ceases to exist.

**Test expectations.** `ServicesTests.cs:75-95` green after re-pointing; no new behaviour test.

**Size:** M · **Depends on:** E2.S5 · **Risk:** low-medium; the hazard is documentation drift, closed by the
`project-context.md` edit in the same PR. · **DoD:** `project-context.md` matches the code again.

---

### Story E3.S7: Build both chains once, in the constructor, per the target composition ⛔ Phase 4

As a player,
I want the free tiers behind my key and my key never behind the screen reader,
So that a metered allowance cannot be drained by a loop I did not ask to spend it.

**Acceptance Criteria:**

**Given** §8.1
**When** the `MainWindow` constructor runs, before `InitializeComponent()`
**Then** the read chain is
`[Azure if AzureApiKey set AND UseKeyForReading] → GoogleDict → Edge → GoogleGtx → [Bergamot if enabled and
present]` and the write chain is
`[DeepL if key set] → [Azure if key set] → GoogleDict → Edge → GoogleGtx → [Bergamot if enabled and present]`.

**Given** `_readTranslator` is a field initializer at `MainWindow.xaml.cs:43` while `_settings` is initialised at
`:50`
**When** the chains move into the constructor body
**Then** `_readTranslator` stays `readonly` and is simply assigned there, so it can see `_settings`.

**Given** I8
**When** any configuration is applied
**Then** **no setting can put DeepL in the read chain** — its absence is structural, asserted by a test on the built
chain, not by a runtime check.

**Given** `BuildTranslator()` at `MainWindow.Translate.cs:226-233`
**When** it becomes `BuildWriteChain()`
**Then** it is still called from the key-save handler at `:239`.

**Technical notes.** `MainWindow.xaml.cs:43,80-104` · `MainWindow.Translate.cs:226-233,239`. The cache is still one
per decorator at this point; the shared store is E4.

**Test expectations.** Unit on the built chain: DeepL absent from the read chain under every settings permutation;
tier order matches §8.1.

**Size:** M · **Depends on:** E3.S3–E3.S6 · **Risk:** medium — constructor ordering has produced bugs in this
codebase. · **DoD:** the I8 test exists and is named so a future reader cannot delete it by accident.

---

### Story E3.S8: Bound the batch-mismatch per-line fallback ⛔ Phase 4

As a player,
I want one bad response to cost one failure,
So that a 14-line tick cannot turn into thirty requests.

**Acceptance Criteria:**

**Given** I5 and §6.3
**When** a 1:1-contract provider (DeepL, Azure) returns the wrong number of results
**Then** it is a `BadResponse` and the result is **never padded** — padding once bypassed the fallback and poisoned
the cache (`DeepLTranslator.cs:49-53`).

**Given** a join/split provider (GoogleDict, Edge, GoogleGtx)
**When** the split count does not match the input count
**Then** it falls through to per-line **only while `lines.Count <= PerLineCap` (8)**, and is a `BadResponse` above
that.

**Given** the measured amplifier (`analyse…` S6, A11) where a mismatch on a 14-line tick produces up to 30 requests
inside one tick
**When** the cap lands
**Then** that tick produces one `BadResponse` instead.

**Technical notes.** `Services/ChainTranslator.cs` / the join-split providers; `PerLineCap` in
`Services/TranslationPolicy.cs`.

**Test expectations.** Unit T19: a batch mismatch on 14 lines raises `BadResponse` instead of issuing 14 requests —
asserted on the fake handler's call count.

**Size:** S · **Depends on:** E3.S4 · **Risk:** low. · **DoD:** T19 passes.

---

### Story E3.S9: Soak the new default endpoint under real sustained load 🔬 spike (U3)

As the owner,
I want to know whether the new endpoint survives an evening, not twenty requests,
So that "the switch works" is a measurement rather than a sample of one afternoon.

**Acceptance Criteria:**

**Given** U3 and V1.7
**When** an instrumented branch build issues ~2 requests/second against
`clients5.google.com/translate_a/t?client=dict-chrome-ex` for **≥ 2 hours**, on **≥ 2 different networks**
**Then** the per-request log (E1.S5) and the gate transition log (E2.S6) are archived in this folder with the
network, the date and the address family.

**Exit criterion (measurable).** Number of 429s per hour, the strike level reached, and the total requests the gate
absorbed versus the total the soak attempted.

**Go / no-go.** **Go:** zero 429s, or 429s the gate absorbs into a handful of probes ⇒ the switch is worth what
`benchmark…` §3.2 claims. **No-go:** sustained 429s at 2 req/s ⇒ the endpoint is not a relief, R1 has already
fired, and the finding goes to the owner **before** the E2+E3 release ships.

**Given** the incident of `SYNTHESE.md` §5
**When** the soak is scheduled
**Then** it runs from a network the owner nominates and **never** from the address recorded as 429 on 2026-09-06
until it has been confirmed clear.

**Technical notes.** Branch only; no production change. Uses the same build as E3.S4.

**Test expectations.** None; this is a field measurement.

**Size:** M (mostly waiting) · **Depends on:** E3.S4, E1.S5, E2.S6, the owner's go · **Risk:** medium — it is
deliberate traffic to a rented endpoint. · **DoD:** U3 is closed in §15.1 with numbers.

---

## Epic 4 (D): Shared persistent cache

**Increment 3.**

**Goal.** A line the LIVE feed translated is free when the player types it in the Translator tab — in the same
session **and after a restart**. Every cache hit is a request Google never counts.

**Scope.** `TranslationCacheStore`, lazy load, debounced atomic save, MRU-ordered file, one store behind both
decorators, and the measurement that sets the capacity.

**Out of scope.** Changing `CachingTranslator`'s decorator semantics — its 7 existing tests must pass **unchanged**.

**Dependencies.** E3 (it wraps the chains). **Increment mapping:** `plan-migration.md` increment 3.

**Invariants this epic must keep:** **I4 (only successes are cached; a `(`-prefixed value is never stored, in memory
or on disk)**, I10 (lazy load, off the UI thread), I2.

**Definition of done for the epic.** A line translated in the LIVE feed is served from cache when typed in the
Translator tab, in the same session and after a restart. Saving a DeepL key does not empty the cache. A
`(`-prefixed value never appears in the file. The measured load time is recorded in the PR.

---

### Story E4.S1: Extract the LRU into `TranslationCacheStore` ⛔ Phase 4

As the maintainer,
I want the cache data structure to live outside the decorator,
So that two decorators can share one, without changing what a decorator means.

**Acceptance Criteria:**

**Given** the LRU currently lives inside `Services/CachingTranslator.cs:15-29,103-126`
**When** it moves into `Services/TranslationCacheStore.cs`
**Then** the key stays **unchanged** — `source + "|" + target + "|" + text.Trim()` (`CachingTranslator.cs:73-78`) —
and the capacity becomes 2000 (was 500, memory only).

**Given** I4
**When** a value starting with `(` is offered
**Then** it is not stored, and **the `(`-prefix rule stays in `CachingTranslator`**, not in the store (§3.1
responsibility table).

**Given** `CachingTranslator` keeps its existing constructor and gains a store-taking one
**When** the change lands
**Then** `tests/PWRUHelper.Tests/CachingTranslatorTests.cs` (7 cases, `:45,57,69,83,97-109,112`) passes
**unchanged**.

**Technical notes.** New `Services/TranslationCacheStore.cs` (glossary §16); `Services/CachingTranslator.cs:15-29,
103-126`.

**Test expectations.** Unit: the 7 existing cases unchanged; one new case that the store enforces the capacity and
MRU order.

**Size:** S · **Depends on:** E3 · **Risk:** low. · **DoD:** `CachingTranslatorTests` green with zero edits.

---

### Story E4.S2: Persist the cache lazily and save it debounced ⛔ Phase 4

As a player who restarts the app,
I want yesterday's translations still there,
So that a fresh session does not re-earn the same rate limit translating the same lines.

**Acceptance Criteria:**

**Given** §8.2
**When** the first cache **miss** of the process occurs
**Then** `%AppData%\PWRUHelper\translation-cache.json` is loaded — off the UI thread, never at startup (I10).

**Given** a store operation
**When** it completes
**Then** a save is scheduled `CacheSaveDebounceMs` (5 s) later and coalesced with any other pending save; plus one
save on the existing `OnClosing` path.

**Given** the file format
**Then** it is `{"version":1,"entries":[{"k":…,"v":…,"p":…,"t":…}]}`, written **MRU-first** so the LRU order
survives a restart, using the atomic pattern of `SettingsService.Save` (`SettingsService.cs:186-197`), best-effort,
never throwing.

**Given** a corrupt or future-version file
**When** it is loaded
**Then** the cache is empty and no error surfaces.

**Given** I4
**When** the file is inspected after a session that produced failures
**Then** **no value in it starts with `(`**.

**Given** concern #2 of §8.2
**When** the file is loaded and `OfflineFallbackEnabled` is now false
**Then** entries whose `"p"` is `bergamot` are dropped.

**Technical notes.** `Services/TranslationCacheStore.cs`; the store takes an explicit path for tests (§11.1).

**Test expectations.** Unit T17: persistence round-trip, the `(`-prefix rule enforced **on disk**, MRU order
survives a reload, a corrupt file yields an empty cache.

**Size:** M · **Depends on:** E4.S1 · **Risk:** low-medium — a partially written file, mitigated by the atomic
pattern and by treating any parse failure as an empty cache. · **DoD:** T17 passes.

---

### Story E4.S3: Measure the cache lazy-load cost 🔬 spike (U8)

As the owner,
I want to know what a 2000-entry cache costs to load,
So that a convenience feature cannot quietly become a startup regression on the problem we are already fighting.

**Acceptance Criteria:**

**Given** U8 and G6 ("cost nothing at startup and nothing in RAM")
**When** a synthetic `translation-cache.json` of 2000 realistic entries (~300 KB, §8.2) is placed and the lazy load
is timed with a `Stopwatch` on the load path
**Then** the elapsed time and the resident-memory delta are recorded in the PR and in this folder, measured on a
personal Defender-only machine as well as the dev box.

**Exit criterion (measurable).** Milliseconds to load 2000 entries off the UI thread, and the working-set delta.

**Go / no-go.** **Go:** ≤ 50 ms off the UI thread and a working-set delta small against the ~150 MB budget ⇒
capacity 2000 stands. **No-go:** anything materially larger ⇒ **the capacity is the knob** (§8.2) — halve it and
re-measure; do not move the load onto the UI thread and do not load eagerly.

**Given** I10
**When** the measurement runs
**Then** it confirms that no load happens before the window is visible.

**Technical notes.** Branch measurement over E4.S2's code; `Services/TranslationPolicy.cs` holds `CacheCapacity`.

**Test expectations.** None; a measurement. The recorded number belongs in the PR description.

**Size:** S · **Depends on:** E4.S2 · **Risk:** low. · **DoD:** U8 is closed in §15.1 with a number and a capacity
decision.

---

### Story E4.S4: Share one store between the read and the write decorators ⛔ Phase 4

As a player,
I want a line I just watched being translated to be instant when I retype it,
So that the two halves of the app stop paying twice for the same sentence.

**Acceptance Criteria:**

**Given** §8.1
**When** the `MainWindow` constructor builds the pipeline
**Then** one `TranslationCacheStore` instance is created and passed to **both** `CachingTranslator` decorators.

**Given** `MainWindow.Translate.cs:239` currently throws the write cache away on every key save (amplifier A5)
**When** this story lands
**Then** rebuilding the write chain **keeps** the store, and the session's accumulated translations survive a key
save.

**Given** the trade-off recorded in §8.2
**When** a value produced by any tier is stored
**Then** the key stays provider-agnostic — a value produced by a lower tier may later be served while a higher tier
is healthy, which is **accepted**; the producing provider is recorded in `"p"` for the log and for the Bergamot drop
rule only.

**Technical notes.** `MainWindow.xaml.cs` (ctor) · `MainWindow.Translate.cs:239` · a `SaveNow()` call in the
existing `OnClosing` path.

**Test expectations.** Unit T18: the write decorator serves a value stored by the read decorator, and
`BuildWriteChain()` on key save does not lose it.

**Size:** S · **Depends on:** E4.S2, E3.S7 · **Risk:** low. · **DoD:** T18 passes; the DeepL key-save path no longer
empties the cache.

---

### Story E4.S5: Write the cache file as UTF-8 instead of `\uXXXX` ⛔ Phase 4

_Added 2026-09-07 as the follow-up E4.S3 / U8 §4 named ("worth its own story"), on the architect's ruling that the
file format is free to change while A.2 is unreleased._

As a player with a full cache,
I want the file that holds my translations to be the size it should be,
So that the cache stays comfortably inside the guard that exists to refuse a corrupt one.

**Acceptance Criteria:**

**Given** a store that has cached a Russian line
**When** the file is written
**Then** the Cyrillic is in it as UTF-8 text, with no `\uXXXX` sequence anywhere in the file, and the file is still
strictly valid JSON that this build reads back.

**Given** a `translation-cache.json` written in the old escaped form
**When** it is loaded
**Then** every entry is read exactly as before, and the next save rewrites it in the new form — no migration, no
`SchemaVersion` bump.

**Given** the U8 harness's 2000 realistic entries
**When** the full file is written through the store
**Then** it costs under 300 B an entry and under 1 MB, and CI defends both.

**Technical notes.** `TranslationCacheStore.Options` only (`Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`,
in-box, no new package). `ProviderStateStore` and `SettingsService` are untouched — ASCII files a human reads.
`MaxBytes` stays 4 MB: it guards a corrupt file, not the cache.

**Test expectations.** Three cases in `TranslationCachePersistenceTests`, one per AC; the size case runs over the
spike's own `Entries(2000)`.

**Size:** XS · **Depends on:** E4.S3 · **Risk:** low. · **DoD:** the three cases pass, the suite is green, and U8's
table is re-measured (502 → 277 B/entry).

---

## Epic 5 (E): Honest live translation

**Increment 4.**

**Goal.** LIVE stops lying and stops costing. A paused loop uses no CPU next to the game and issues no requests; a
row that failed during a blip gets its translation when the provider comes back; a read-once that failed says so.

**Scope.** §9 in full: gate-open ticks skip the whole tick body and back off; the honest auto-stop; the pending-retry
queue; the cancellable, truthful read-once.

**Out of scope.** The final wording (E7) — this epic writes the status strings in Sally's already-published form
where they exist, and provisional text otherwise.

**Dependencies.** E3 (needs `AllProvidersPaused` and `RetryAt`). **Increment mapping:** `plan-migration.md`
increment 4.

**Invariants this epic must keep:** **I16 (the eleven LIVE behaviours that must not move — the three dedup layers,
the batch join, the `rateLimited` latch, the strictly sequential loop, the token reaching `HttpClient`)**, I3, I6,
I7, I4.

**Definition of done for the epic.** With every gate forced open, a LIVE session issues **zero** requests, does no
OCR, and shows a countdown. Rows that failed during a blip are re-translated after recovery instead of staying
`(…)`. A read-once that failed says so instead of `Done`. A calm chat under a persistent failure now auto-stops,
where today it never does.

---

### Story E5.S1: Skip the whole tick and back off while every read tier is paused ⛔ Phase 4

As a player mid-raid,
I want a paused app to cost nothing,
So that a translation outage never becomes a frame-rate problem.

**Acceptance Criteria:**

**Given** the LIVE loop (`MainWindow.Live.cs:179-245`) and the capture at `:187`
**When** every tier of the read chain reports `Open`
**Then** the **entire tick body is skipped** — no capture, no OCR, **no `LiveDedup.Next`**, no request — the status
shows a countdown on the main window **and** the overlay, `backoffSteps` increments, and the wait becomes
`min(CurrentLiveIntervalMs() << backoffSteps, LiveBackoffCapMs)` (×2, capped at 5 s).

**Given** **OQ-B, settled by the owner: "No — full pause"**
**When** a tick is skipped
**Then** nothing is captured and nothing is OCR'd — the feed freezes with a countdown, at zero CPU, and there is no
retry queue of un-OCR'd frames to bound.

**Given** `LiveDedup.Next` is never called during the pause (`LiveDedup.cs:91-97`)
**When** the gate closes
**Then** a message still on screen is genuinely fresh and gets translated, and `LiveDedup`'s internal `_tick` has
not advanced, so `ReappearAfterFrames` did not expire during the pause.

**Given** the first tick that **translated successfully**
**When** it completes
**Then** `backoffSteps` resets to 0.

**Given** `DefaultsAndResizeTests.cs:16,28-32` pins `LiveSpeedPercent = 92` and the 700/3000/500 mapping
**When** the back-off lands
**Then** it changes the **wait between ticks**, not the pure function `LiveIntervalMs(double)`, and those pins stay
green.

**Technical notes.** `MainWindow.Live.cs:179-245`, `:242` · `Services/TranslationPolicy.cs`
(`LiveBackoffCapMs = 5000`). `LiveDedup` is **not** touched.

**Test expectations.** Unit on the tick-decision function extracted for testability; the existing `LiveDedupTests`
(8 cases) and `LiveDefaultsTests` (7 cases) pass **unchanged**. The `CountingTranslator` double
(`CachingTranslatorTests.cs:10-28`) is the tool for "zero requests while paused".

**Size:** M · **Depends on:** E3.S3 · **Risk:** medium — this is the subtlest loop in the app. ·
**DoD:** zero requests, zero OCR calls and a visible countdown under a forced-open gate.

---

### Story E5.S2: Fix the auto-stop counter and add the time-window rule ⛔ Phase 4

As a player who walked away from a calm chat,
I want LIVE to stop when it is genuinely broken,
So that it does not trickle failing requests all evening while I am not looking.

**Acceptance Criteria:**

**Given** `MainWindow.Live.cs:219` resets `consecutiveErrors = 0` at the end of every non-throwing tick — including
an empty one — which is why the auto-stop never fires in a calm chat (`analyse…` A2, S4c)
**When** the §9.2 table lands
**Then**: a tick that translated ≥ 1 line successfully resets the counter and clears the window; an **empty** tick
leaves both unchanged; a **gate-open** tick leaves both unchanged (it is neither a success nor a failure); a tick
that threw increments the counter and stamps `DateTime.UtcNow` into a bounded queue trimmed to
`AutoStopWindowMinutes` (2).

**Given** the two triggers
**When** `consecutiveErrors >= 5` **or** the window holds ≥ 5 error stamps
**Then** LIVE auto-stops, and the existing 5-consecutive rule is kept because it is what users already know.

**Given** §9.2's closing rule
**When** every provider is paused
**Then** LIVE **must not** auto-stop for that reason — pausing is the system working correctly.

**Given** the auto-stop message (`Live.cs:231-238`)
**When** LIVE stops
**Then** the sentence carries both the reason and the way back, per `ux-mode-degrade.md` §3.2
(`Live stopped — {n} reads in a row failed. Press ▶ to try again.`).

**Technical notes.** `MainWindow.Live.cs:219,231-238` · `Services/TranslationPolicy.cs`.

**Test expectations.** Unit T15: the four tick outcomes and the 2-minute window rule.

**Size:** S · **Depends on:** E5.S1 · **Risk:** medium — changing when LIVE stops is user-visible behaviour. ·
**DoD:** T15 passes; a calm chat under a persistent failure auto-stops.

---

### Story E5.S3: Retry failed rows once the providers recover ⛔ Phase 4

As a player,
I want a row that failed during a 30-second blip to fill in afterwards,
So that a temporary outage does not permanently burn the messages that arrived during it.

**Acceptance Criteria:**

**Given** `LiveDedup` marks a failed line emitted, so the row is burned for the session (`analyse…` §3.1,
`LiveDedup.cs:91-97`)
**When** the §9.3 queue lands
**Then** it lives in `MainWindow.Live.cs` as
`List<(OcrResultItem Row, string Body, string Target)> _pendingRetry` and **`LiveDedup` is not touched at all** —
which is the strongest possible guarantee that it cannot swallow anything.

**Given** a failure at `MainWindow.Live.cs:277-283`
**When** the row is enqueued
**Then** its `TranslationBody` is set to a value that is deliberately **not** `(`-prefixed, so it reads as pending
rather than terminal, and so I4 keeps it out of the cache.

**Given** the queue's bounds
**Then** it is bounded at `MaxHistory` (50), oldest dropped; an entry is discarded when its row is no longer in
`_ocrItems` (the `MaxHistory` trim at `Live.cs:268`); and `StopLive()` clears it.

**Given** the first tick after a successful translation
**When** it runs
**Then** the queue is drained **before** new lines are processed, in one batch through the same `_readTranslator`,
so cache hits make most of it free.

**Given** a drain that fails
**When** it is retried
**Then** the rows go back on the queue, and after `PendingRetryMaxAttempts` (2) they become a terminal `(…)` row.

**Given** UX-DR17 / flow (a) step 5
**When** rows are re-translated
**Then** they are updated **in place**, keeping their position and their 🔑 glossary line — never appended a second
time.

**Technical notes.** `MainWindow.Live.cs:250-288,277-283,268` · `Services/TranslationPolicy.cs`. The row's
`TranslationBody` is already bound through the existing feed templates, so no new binding is needed here; any new
`Run.Text` added for a retry badge belongs to E7.S6 with its `Mode=OneWay` and its render-test case (I15).

**Test expectations.** Unit T16: a failed row is re-translated after recovery; a row evicted by `MaxHistory` is
dropped; the queue is bounded and cleared on stop.

**Size:** M · **Depends on:** E5.S1 · **Risk:** medium — duplicated feed rows would be worse than the failure. ·
**DoD:** T16 passes; V1.3 holds (zero permanently-parenthesised rows in a session that recovered).

---

### Story E5.S4: Make read-once report the truth, and make it cancellable ⛔ Phase 4

As a player who pressed "read the screen once",
I want to be told when nothing was translated,
So that I stop retrying a button that already told me it was Done.

**Acceptance Criteria:**

**Given** `TranslateSentencesInto` swallows the failure and returns (`MainWindow.Ocr.cs:296-299`) and
`ReadRegionOnceAsync` then writes `Done — N line(s) translated` unconditionally (`:243-244`)
**When** the fix lands
**Then** the method returns `(int Translated, TranslationException? Error)` and the caller writes `Done — N of M …`
on partial success and the `Friendly(error)` text otherwise. **"Done" is unreachable when any line lacks a
translation.**

**Given** `TranslateBodiesAsync(..., default)` at `MainWindow.Ocr.cs:295` passes no cancellation token — today's
worst case is ≈36.9 s of uncancellable UI
**When** the fix lands
**Then** a per-read `CancellationTokenSource` capped at `ReadOnceBudgetSeconds` (30) is used, cancelled by
`StopLive`, by a second read-once and by window close; `_readingOnce` (`:212,219,254`) stays the re-entrancy guard.

**Given** every read tier is gate-open
**When** read-once is pressed
**Then** it issues **no request**, reports the pause and the countdown, and **creates no rows at all** — nothing
invites a manual retry more effectively than a false "Done" (amplifier A7).

**Technical notes.** `MainWindow.Ocr.cs:207-256,268-303` (`:243-244`, `:295`, `:296-299`).

**Test expectations.** Unit at the read-once status seam (UX acceptance hint 4). Manual: press read-once with the
network down and confirm the status is not `Done`.

**Size:** M · **Depends on:** E3.S3 · **Risk:** low-medium. · **DoD:** V1.5 holds.

---

## Epic 6 (F): Bring your own key — Azure

**Increment 5.**

**Goal.** A player who wants better translations and their own quota can paste a free Azure key and a region, and
know exactly what it will and will not be spent on.

**Scope.** The U4 and U5 answers, `AzureTranslator`, the five new settings fields, the key/region UI, the read-path
opt-in, and a key test that does not spend quota.

**Out of scope.** The chip, the chain line and the About-tab restructure (E7).

**Dependencies.** E3. **Increment mapping:** `plan-migration.md` increment 5.

**Invariants this epic must keep:** **I12 (settings restore stays re-entrancy-safe)**, **I13 (changed defaults reach
existing users only via `SettingsVersion` + `Migrate`)**, I5 (Azure is a 1:1 batch contract — never padded), I8
(Azure in the read chain only behind the explicit opt-in; DeepL never), I11 (keys never logged).

**Definition of done for the epic.** A pasted F0 key + region translates through Azure on the write path.
`UseKeyForReading` off ⇒ Azure is **not** in the read chain, verified by a test on the built chain. Restarting the
app keeps both fields (the v0.12.3 clobber test). U4 is answered before the copy promises anything about "free".

---

### Story E6.S1: Verify the Azure F0 free tier on a real resource 🔬 spike (U4)

As the owner,
I want to know whether "2 million characters a month, free" is permanent,
So that the app never promises a user something Microsoft can withdraw next quarter.

**Acceptance Criteria:**

**Given** U4 — F0's permanence is CONFIRMED from the pricing page's wording and REPORTED from a Microsoft Q&A, but
never verified on a real resource
**When** an F0 Translator resource is created and its portal quota page is read
**Then** the exact wording of the quota, its reset date and any trial expiry are recorded in this folder with a
screenshot reference and the date.

**Exit criterion (measurable).** A yes/no on "2 M characters per month, no expiry", plus the reset day of the month.

**Go / no-go.** **Go:** permanent ⇒ the settings copy may say *"Azure gives you 2 million characters a month for
free"* (`ux-mode-degrade.md` §4.2) and *"resets on the 1st"* (§3.7). **No-go:** trial-limited ⇒ the copy is rewritten
to state the limit and its expiry **before** E6.S3 ships; the provider still ships.

**Technical notes.** No code. Blocks the copy in E6.S3 and E7.S8, not the provider in E6.S2.

**Test expectations.** None.

**Size:** S · **Depends on:** — · **Risk:** none. · **DoD:** U4 is closed in §15.1.

---

### Story E6.S2: `AzureTranslator` over raw `HttpClient` ⛔ Phase 4

As a player with a key,
I want the fastest and most reliable tier when I have paid nothing for it,
So that my own quota gives me a better result than the shared free door.

**Acceptance Criteria:**

**Given** §7.5
**When** the provider sends a request
**Then** it is
`POST https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from={src}&to={tgt}` with
`Ocp-Apim-Subscription-Key`, `Ocp-Apim-Subscription-Region` and `Content-Type: application/json; charset=utf-8`,
body `[{"Text":"line 1"},{"Text":"line 2"}]`, and `from` omitted for auto-detect.

**Given** the response `[{"translations":[{"text":…,"to":…}]}, …]`
**When** the element count does not match the input count
**Then** it is a `BadResponse` and **never padded** (I5) — Azure is the only tier with a true 1:1 array contract and
is therefore the safest, not merely the fastest.

**Given** the error mapping of §7.5
**Then** 401 → `AuthFailed`; 403 with a quota/limit envelope → `QuotaExhausted`, else `AuthFailed`; 429 →
`RateLimited`; 5xx → `Unavailable`. The Azure error `code` is logged; the `message` is never shown raw.

**Given** NFR6
**When** the provider is written
**Then** it uses a raw `HttpClient` and **not** `Azure.AI.Translation.Text` — the SDK brings its own retries and
timeouts that would fight `HttpProviderCore`, plus ~3.0–3.2 MB of assemblies of which ~1.3 MB is an MSAL stack this
app never calls (§7.5, A5).

**Technical notes.** New `Services/AzureTranslator.cs` (glossary §16), provider id `azure`, on `HttpProviderCore`
with the `HttpMessageHandler` seam.

**Test expectations.** Unit T12 (Azure array fixture) plus the four error-mapping cases and the never-padded case.

**Size:** M · **Depends on:** E2.S5, E3.S3 · **Risk:** low — the endpoint is contracted and documented. ·
**DoD:** a key + region translates through Azure with the fake handler and, once, for real.

---

### Story E6.S3: Azure key and region settings, re-entrancy-safe ⛔ Phase 4

As a player,
I want my key and region to still be there tomorrow,
So that the settings bug class that already cost this project two migrations does not claim a third.

**Acceptance Criteria:**

**Given** §12
**When** the five fields are added to `AppSettings` (`Services/SettingsService.cs:7-81`)
**Then** they are `AzureApiKey` (`""`), `AzureRegion` (`""`), `UseKeyForReading` (**`false`**),
`OfflineFallbackEnabled` (**`false`**) and `ProviderGateOverrides` (`null`, **no UI**), and `Sanitize`
(`:165-182`) null-guards the two strings exactly as `DeepLApiKey ??= ""` does at `:175`, trimming and lower-casing
`AzureRegion`.

**Given** I13 and §12's explicit ruling
**When** these five fields land
**Then** **`SettingsVersion` is NOT bumped and `Migrate` gains no step** — the fields are new, so an old
`settings.json` deserialises them to their defaults, which is the intended behaviour.

**Given** I12 and the v0.12.3/v0.13.0 bug class
**When** `AzureKeyBox`, `AzureRegionCombo` and the two check boxes are added
**Then** every change handler starts with `if (_restoringSettings) return;` and `ApplySettings`
(`MainWindow.xaml.cs:173-193`) applies their UI side effects **explicitly**, through a new
`UpdateEngineStatusUi()` called where `UpdateOcrFilterUi` is called today (`:193`).

**Given** the region control (UX-DR16)
**When** it is built
**Then** it is a `ComboBox IsEditable="True"` seeded with `global, westeurope, francecentral, northeurope, eastus,
eastus2, westus2, uksouth, swedencentral`, free text accepted, using the existing `SelectTag`/`SelectedTag` helpers
(`MainWindow.xaml.cs:359-370`) — **not** a re-rolled loop.

**Given** a key without a region is a guaranteed 401
**When** Save is pressed with one of the two empty
**Then** the status line says so and no request is sent.

**Given** saving a key
**When** the handler runs
**Then** it calls `ProviderGates.ClearAuthBlock("azure")` and rebuilds **both** chains (the read chain too, since
`UseKeyForReading` can put Azure in it), so a corrected key takes effect immediately instead of waiting out an
`AuthFailed` window.

**Given** I11
**When** the error report is copied
**Then** no key appears in it (`MainWindow.xaml.cs:312-322`).

**Technical notes.** `Services/SettingsService.cs:7-81,165-182` · `MainWindow.xaml` (the DeepL settings block near
`:611-626`, mirroring the `PasswordBox` at `:621` and the Save button at `:622`) · `MainWindow.xaml.cs`
(`ApplySettings`) · `MainWindow.Translate.cs:226-252`. Keys stay plain in `settings.json` exactly like
`DeepLApiKey`; DPAPI is explicitly **not now** (§12).

**Test expectations.** Unit: a settings round-trip through `SettingsService.PathOverride` proving the new fields
survive save/load; the `_restoringSettings` regression test in the `StartupSettingsTests` shape — a load → apply →
save cycle with **no user interaction** leaves `settings.json` unchanged (UX acceptance hint 8).

**Size:** M · **Depends on:** E6.S2 · **Risk:** **medium — the settings surface is the code path with the most
expensive prior bugs in this repo.** · **DoD:** the clobber test exists and passes.

---

### Story E6.S4: "Use my key for screen reading" opt-in, off by default ⛔ Phase 4

As a player with a free monthly quota,
I want to decide whether the screen reader may spend it,
So that a free allowance cannot disappear in three evenings without my having chosen it.

**Acceptance Criteria:**

**Given** `UseKeyForReading` defaults to **false** (§12)
**When** a key is saved but the box is unticked
**Then** Azure is **not constructed into the read chain** — asserted by a test on the built chain, not by a runtime
guard.

**Given** the box is ticked
**When** the handler runs
**Then** the read chain is rebuilt immediately, exactly as `DeepLSaveKey_Click` rebuilds the write chain
(`MainWindow.Translate.cs:239`), and the checkbox obeys `_restoringSettings` (I12).

**Given** UX-DR15 and the arithmetic of `ux-mode-degrade.md` §4.3
**When** the option is rendered
**Then** it carries its cost sentence: *"Azure gives you 2 million characters a month for free — roughly 20 to 40
hours of busy chat. Screen reading is off by default because live mode reads every new line and can use it up in a
few evenings."* — subject to E6.S1's answer, and re-derived if the shared cache measurably changes the multiplier.

**Given** OQ-12 (resolved by John): an opt-in that arrives pre-ticked is not an opt-in
**When** an existing user upgrades with a key already saved
**Then** the box is off.

**Given** R9
**When** Azure returns `QuotaExhausted`
**Then** the gate opens for an hour rather than retrying, and the chain falls to the free tiers.

**Technical notes.** `MainWindow.xaml` · `MainWindow.xaml.cs` (`ApplySettings`, `UpdateEngineStatusUi`) ·
`MainWindow.Translate.cs`.

**Test expectations.** Unit on the built read chain under both settings; the `_restoringSettings` case.

**Size:** S · **Depends on:** E6.S3, E3.S7 · **Risk:** low-medium. · **DoD:** the read chain test passes both ways.

---

### Story E6.S5: Test a key without spending quota ⛔ Phase 4

As a player who just pasted a key,
I want to know immediately whether it works,
So that I find out now rather than during a raid.

**Acceptance Criteria:**

**Given** UX-DR13 and OQ-6
**When** `Test key` is pressed
**Then** the button is disabled and reads `Testing…` while in flight, the result lands **in the status line and
never in a `MessageBox`**, and a failed test **does not clear the key**.

**Given** the nine result strings of `ux-mode-degrade.md` §3.7
**When** each outcome occurs
**Then** the exact sentence is used, including the distinct wrong-region case naming the region the user typed, and
the `no internet` case.

**Given** OQ-6 is unanswered in the architecture (see the readiness report, gap R-1)
**When** this story is implemented
**Then** the validation uses a **non-billing** endpoint where one exists (DeepL `/usage`, Azure `/languages` or an
equivalent) and, where none exists, the button label becomes `Test key (uses a few characters)` — the copy must not
claim a free check that is not free.

**Given** I11
**When** the test runs
**Then** the key never reaches the log.

**Technical notes.** `MainWindow.xaml` (two `GhostButton`s next to the two Save buttons) ·
`MainWindow.Translate.cs:246-252` (`UpdateDeepLStatus` is the pattern) · the `DeepLStatus` control at
`MainWindow.xaml:625` plus a new `AzureStatus`.

**Test expectations.** Unit through the fake handler: each outcome maps to the right sentence. Manual: a real key
and a deliberately wrong region.

**Size:** M · **Depends on:** E6.S3 · **Risk:** low-medium — the honesty of the label depends on OQ-6. ·
**DoD:** every §3.7 string is reachable and exists exactly once in the codebase (UX-DR19).

---

### Story E6.S6: Establish whether existing DeepL `:fx` keys still work 🔬 spike (U5)

As a player who set up DeepL last year,
I want the app to tell me the truth about my old key,
So that I am not left guessing why the tier I paid attention to went quiet.

**Acceptance Criteria:**

**Given** U5 — DeepL's API Free/Pro plans stopped being purchasable in July 2026 and the current free plan is a
one-time 1 M-character "Developer" allowance
**When** an existing `:fx` key is tested (one volunteer, or a DeepL support answer)
**Then** the result is recorded in this folder with its date.

**Exit criterion (measurable).** A yes/no on "an existing `:fx` key still authenticates and translates".

**Go / no-go.** **Go — old keys work:** the DeepL settings row keeps today's copy. **No-go — old keys are dead:** an
existing user's DeepL tier becomes an `AuthFailed` gate-open on first use — which is exactly the right behaviour —
and the settings row must say so, with the `✕ DeepL refused this key…` sentence of §3.7 extended to name the plan
change.

**Technical notes.** No code. Feeds the copy in E6.S5 and E7.S8.

**Test expectations.** None.

**Size:** S · **Depends on:** — · **Risk:** none. · **DoD:** U5 is closed in §15.1.

---

## Epic 7 (G): Degraded-mode UX

**Increment 6.**

**Goal.** A player watching only the compact overlay can tell **paused** from **broken** from **working**, at a
glance, without reading an HTTP code and without a modal ever appearing over the game.

**Scope.** Sally's surfaces in full: the real copy per `Kind`, the countdown mechanism, the provider chip and its
tooltip, the paused state and the frozen heartbeat on both windows, the LIVE / read-once / overlay strings, the
pending-retry row treatment, and the About-tab "Translation engines" block.

**Out of scope.** The P1 expectation copy and the update toast — those are E9 (track P).

**Dependencies.** E5 (and E6 for the key-slot copy). **Increment mapping:** `plan-migration.md` increment 6.

**Invariants this epic must keep:** **I15 (every new `Run.Text` binding is `Mode=OneWay` and gains an STA
`TemplateRenderTests` case)**, I2 (`Services/` never formats a countdown — `MainWindow` does), I12, I14 (no WPF
`Clipboard`), NFR7 (repaint budget), NFR11 (never colour alone), NFR12 (no modal from a background path).

**Definition of done for the epic.** Every `«Sally: …»` placeholder in `architecture-cible.md` §4.4 and §9 has real
copy. A user watching only the compact overlay can tell paused from broken from working. All STA render tests pass.
The dark `ToolTip` style in `Theme.xaml` is reused, not restyled, and `MainTabs_SelectionChanged` keeps its
`e.Source is TabControl` filter.

---

### Story E7.S1: One sentence per error kind, with a countdown ⛔ Phase 4

As a player,
I want one plain sentence that says what happened, when it ends and whether I must do something,
So that I stop reading developer-speak about HTTP responses.

**Acceptance Criteria:**

**Given** the `Kind` switch shape landed in E1.S6 (`MainWindow.xaml.cs:404-410`)
**When** the real copy replaces the placeholders
**Then** each `Kind` returns exactly the sentence of `ux-mode-degrade.md` §3.1 — `RateLimited`
(`{P} asked us to slow down — paused for {t}.`), `Blocked`, `Unavailable`, `Timeout`, `Network`, `BadResponse`,
`QuotaExhausted`, `AuthFailed`, `AllProvidersPaused`
(`All engines are paused — next try in {t}. Nothing you need to do.`), and **`Cancelled` renders nothing at all**.

**Given** the copy rules of §3
**When** any of those strings is reviewed
**Then** it contains no HTTP status code, no provider internal (`gtx`, `dict-chrome-ex`, `429`), no exclamation
mark, and it is ≤ 80 characters wherever the overlay can show it.

**Given** UX-DR19
**When** the codebase is searched
**Then** each user-facing string exists **exactly once** — no ad-hoc re-phrasing at a call site; that is what the ten
strings E1–E10 cost this project.

**Given** a successful fallback
**When** a lower tier answered
**Then** **no error text appears anywhere** — the user got their translation; only the chip changes plus the
one-time notice of §3.5 (UX acceptance hint 5).

**Given** I2
**When** `{t}` is rendered
**Then** `MainWindow` formats the countdown from `RetryAt`; `Services/` never formats a string for display.

**Technical notes.** `MainWindow.xaml.cs:404-410` and one central strings location. `{P}` is the stable user-facing
provider name of OQ-2 — `Google`, `Edge`, `Google (old)`, `DeepL`, `Azure`, `Offline engine`.

**Test expectations.** Unit on the switch: one case per `Kind`, plus the negative case that `Cancelled` produces an
empty result, plus a test that asserts no string contains a digit-triplet status code.

**Size:** M · **Depends on:** E1.S6, E5, E7.S2 · **Risk:** low. · **DoD:** every §3.1 row is reachable and unique.

---

### Story E7.S2: The one-per-window 1 Hz countdown ⛔ Phase 4

As a player mid-raid,
I want the countdown to be quiet,
So that a status line cannot cost me frames.

**Acceptance Criteria:**

**Given** UX-DR5 / §2.4
**When** a pause begins
**Then** **one** `DispatcherTimer` for the whole app starts at **1 Hz**, and it is **stopped** the moment nothing is
paused — it never runs idle.

**Given** the granularity rule
**When** the remaining time is ≤ 90 s
**Then** it renders `m:ss` updated every second; above 90 s it renders `about N min` updated only when N changes; at
the 30-minute cap it renders `about 30 min`; under 5 s it renders `about to retry` rather than counting to zero.

**Given** the repaint guard
**When** the timer ticks
**Then** `.Text` is assigned **only when the rendered string differs from the last one** — so above 90 s that is
once a minute (UX acceptance hint 7).

**Given** the placement rule
**When** a window is in a paused state
**Then** **at most one countdown is visible per window** — chip **or** status line, never both showing the same
clock (UX acceptance hint 2), and **no row ever carries a countdown**.

**Technical notes.** `MainWindow.xaml.cs` owns the timer; `CompactOverlay` receives formatted strings through the
existing `SetStatus` (`CompactOverlay.xaml.cs:67-72`). This is the consumer of gap R-2's state-changed notification
— see the readiness report.

**Test expectations.** Unit on the formatter (pure function: seconds → string) including the four granularity bands
and the repaint-guard rule.

**Size:** S · **Depends on:** E5.S1 · **Risk:** low. · **DoD:** the formatter has a test per band; the timer is
provably stopped when nothing is paused.

---

### Story E7.S3: The provider chip and its tooltip ⛔ Phase 4

As a player,
I want one small always-on indicator of which engine is serving me,
So that I never have to open a tab to find out whether anything is wrong.

**Acceptance Criteria:**

**Given** UX-DR3
**When** the chip is added
**Then** it is **one `TextBlock`** — glyph + name + optional countdown, no border, no background, no animation — in
three existing containers: the write path at `MainWindow.xaml:270-279` (right of `TranslateStatus`), the read path
at `MainWindow.xaml:296-302` (left of `LiveIndicator`), and as a prefix of `OverlayStatus`
(`CompactOverlay.xaml:44`) shown **only when not healthy**.

**Given** the vocabulary of §2.3
**Then** `●` means "this engine is serving you" (`TealBrush` `Theme.xaml:22`, or `GoldBrush` `:23` when it is a
**backup**), `○` means paused/not sending (`TextMutedBrush` `:25`), `⚠` means the user may need to act
(`AccentBrush` `:20`) — all glyphs already ship, so there is no font risk.

**Given** NFR11
**When** any state renders
**Then** the glyph **and** the word carry the information; colour is never the only signal — `○ Google paused 0:58`
must read correctly with the palette stripped out.

**Given** UX-DR4
**When** the chip is hovered
**Then** the tooltip lists the whole chain, one line per provider, aligned, no jargon, no HTTP codes, in the shape of
§2.3 — and it **reuses the dark `ToolTip` style in `Theme.xaml`** (load-bearing; do not restyle).

**Given** NFR11
**Then** the chip carries `AutomationProperties.Name = "Translation engine status"`, matching the icon-only buttons
(`MainWindow.xaml:259-261`, `CompactOverlay.xaml:31-38`).

**Technical notes.** `MainWindow.xaml`, `CompactOverlay.xaml(.cs)`; **no new brush, font, image or colour**
(UX-DR18). The chip needs "which provider answered and why a higher one was skipped" — see readiness-report gap
R-3.

**Test expectations.** STA render test on the three placements with an injected state; a unit test that each of the
eight states produces a distinct glyph+word pair.

**Size:** M · **Depends on:** E7.S1, E7.S2 · **Risk:** low-medium, entirely in the XAML. · **DoD:** all eight states
of §2.1 render on all three surfaces.

---

### Story E7.S4: Paused state and a frozen heartbeat on both windows ⛔ Phase 4

As a player,
I want the blinking dot to stop when nothing is being sent,
So that the app stops telling me it is working while it is waiting.

**Acceptance Criteria:**

**Given** UX-DR6 and the confirmed behaviour that the dot keeps blinking through a whole 429 storm
(`analyse…` §1.5)
**When** the path is paused (S3 with nothing below serving, S5, S6)
**Then** `UpdateLiveIndicator` (`CompactOverlay.xaml.cs:77-83`) **freezes on `○`** instead of blinking, and the main
window's `LiveIndicator` (`MainWindow.xaml:299`) switches from `●  LIVE` to `○  LIVE (paused)`.

**Given** the 600 ms heartbeat (`CompactOverlay.xaml.cs:20`)
**When** the pause begins
**Then** it **stops**, so the degraded state costs *less* CPU than the healthy one (UX-DR18).

**Given** the per-surface table of §2.2
**When** each of S1–S8 is entered
**Then** the Translator status line (`MainWindow.xaml:278`), the Screen-OCR status line (`MainWindow.xaml:394`), the
read-feed header (`MainWindow.xaml:308`) and the overlay status (`CompactOverlay.xaml:44`) show exactly the strings
of that table, through their existing setters (`MainWindow.Translate.cs:106-109`, `MainWindow.Live.cs:157-161`,
`:163-175`, `CompactOverlay.xaml.cs:67-72`).

**Given** principle 1, "one state, one voice"
**When** a state is entered
**Then** the sentence is written **once** into the status line and left there until the next change — never repeated
per message, never phrased two different ways on two surfaces.

**Given** UX-DR17 flow (f) step 4
**When** the app starts with a persisted pause still active
**Then** the chip shows it **before the user presses anything** — this is the fix for the owner's original
"it appears at launch" complaint.

**Technical notes.** `MainWindow.xaml`, `MainWindow.xaml.cs`, `CompactOverlay.xaml(.cs)`. `MainTabs_SelectionChanged`
must keep its `e.Source is TabControl` filter.

**Test expectations.** STA render assertions on `LiveIndicator.Text` / the overlay dot (UX acceptance hint 3);
manual: force a pause and watch both windows.

**Size:** M · **Depends on:** E7.S2, E5.S1 · **Risk:** low-medium. · **DoD:** the heartbeat provably does not blink
while paused.

---

### Story E7.S5: LIVE, read-once and overlay-reply copy ⛔ Phase 4

As a player,
I want every status line to tell me the reason and the way back,
So that a stopped feature never leaves me guessing what to press.

**Acceptance Criteria:**

**Given** UX-DR8
**When** LIVE changes state
**Then** the seven strings of §3.2 replace `MainWindow.Live.cs:235` and `:238`, including
`🔴 Live — back on. Catching up on {n} message(s).` on recovery and the two honest auto-stop sentences.

**Given** UX-DR9
**When** a read-once completes
**Then** the five strings of §3.3 replace `MainWindow.Ocr.cs:243-244`, and `Done — {n} line(s) translated.` appears
**only** when every line has a translation; `Could not read the screen: {reason}` replaces today's developer-speak
`OCR failed:` prefix.

**Given** UX-DR10
**When** the compact overlay's quick reply fails
**Then** the four strings of §3.4 replace `CompactOverlay.xaml.cs:142`, each under ~60 characters, and the existing
promise **"your text is kept"** is preserved **verbatim** wherever it still applies.

**Given** UX-DR11
**When** the chain switches tiers
**Then** the notice (`Translated by Edge — Google is paused.` / `Translated by the offline engine — no internet
needed.` / `Back on Google.`) is shown **once per switch** in the status line — never per row, never per message,
never as a toast.

**Given** `{reason}` in §3.3
**When** it is joined into a sentence
**Then** it is the §3.1 sentence lower-cased at the join.

**Technical notes.** `MainWindow.Live.cs`, `MainWindow.Ocr.cs`, `CompactOverlay.xaml.cs:142`. These replace the
provisional strings written in E3 and E5.

**Test expectations.** Unit at the status seams; UX acceptance hint 4 (Done unreachable) is already pinned by E5.S4
and must stay green.

**Size:** M · **Depends on:** E5, E7.S1 · **Risk:** low. · **DoD:** no `«Sally: …»` placeholder remains anywhere.

---

### Story E7.S6: Pending-retry row treatment, with a render test ⛔ Phase 4

As a player,
I want a row that is waiting to look different from a row that gave up,
So that I can tell "not yet" from "never".

**Acceptance Criteria:**

**Given** E5.S3's queue
**When** a row is awaiting retry
**Then** it renders as pending — the existing `…` placeholder treatment of `MainWindow.Live.cs:263`, **not** a
`(`-prefixed terminal string — and **carries no countdown** (UX-DR5).

**Given** a row that exhausted `PendingRetryMaxAttempts`
**When** it renders
**Then** it shows the terminal form of §2.2 S5: `(not translated — all engines were paused)`.

**Given** I15
**When** any new `Run.Text` binding is added to a feed template
**Then** it is `Mode=OneWay` — a get-only property bound TwoWay throws once per rendered item — and it gains a case
in the STA `TemplateRenderTests`, rendered through a `ContentControl` with a **real injected `OcrResultItem`**; an
`ItemsControl` defers container generation headless and renders nothing.

**Given** `OcrResultItem` lives at the repository root with namespace `PWRUHelper` (not `.Models`)
**When** this story lands
**Then** it is **not moved** — the render test depends on it.

**Technical notes.** `MainWindow.xaml` feed templates, `MainWindow.Live.cs:263,277-283`,
`tests/PWRUHelper.Tests/TemplateRenderTests.cs`.

**Test expectations.** STA `TemplateRenderTests` case per new/changed template; the existing
`TranslatorOutputRenderTests` must stay green.

**Size:** S · **Depends on:** E5.S3 · **Risk:** low-medium — this is the `Run.Text` TwoWay trap. ·
**DoD:** every new binding has a render test.

---

### Story E7.S7: About tab — the "Translation engines" block ⛔ Phase 4

As a curious player,
I want one place that says which engine is being used and what the order is,
So that the chip's glyph has somewhere to be explained in full.

**Acceptance Criteria:**

**Given** UX-DR15 and §4.1
**When** the About tab is restructured
**Then** **everything stays in the About tab** — no settings window, no sixth tab; the tab order fixed by
`project-context.md` and referenced by `MainTabs_SelectionChanged` is unchanged; the existing "Better translations
(optional)" heading becomes **"Translation engines"** with three blocks: *what is being used* (read-only),
*your keys*, *offline engine*.

**Given** the mockup of §4.2
**When** the block renders
**Then** it shows an `In use now` line (chip + reason) and a read-only `Chain` line
(`Google → Edge → Google (old) → Offline (not installed)`), both refreshed by `UpdateEngineStatusUi()`.

**Given** the opening sentence
**Then** it reads *"By default everything runs on free engines — no key, no signup, nothing to set up."* and the
keys block states *"Keys are stored only on your PC."*

**Given** principle 2
**When** any degraded-mode flow runs
**Then** **none of them requires the user to open this tab** — many users never will.

**Technical notes.** `MainWindow.xaml:611-626` region; `EngineChainText` is a read-only `TextBlock`.

**Test expectations.** STA render of the block; a unit test that `UpdateEngineStatusUi` is called from
`ApplySettings` at the same place `UpdateOcrFilterUi` is (`MainWindow.xaml.cs:193`).

**Size:** M · **Depends on:** E7.S3, E6.S3 · **Risk:** low-medium. · **DoD:** the block matches §4.2.

---

### Story E7.S8: README and About copy for the new key slot and the offline option ⛔ Phase 4

As a prospective user reading the README,
I want to know that keys are optional and what each one buys,
So that the free path stays obviously the default.

**Acceptance Criteria:**

**Given** `plan-migration.md` increment 6 lists `README.md` among the files touched
**When** the copy lands
**Then** the README describes the free-by-default chain, the two optional key slots and the optional offline engine,
in Sally's register — no HTTP codes, no provider internals.

**Given** E6.S1 (U4) and E6.S6 (U5)
**When** the copy states a quota
**Then** it states what those spikes established and **nothing more** — in particular it does not say "free forever"
unless U4 came back permanent.

**Given** NFR10
**When** the offline option is described
**Then** the About tab declares MPL-2.0 for the engine, the models and the wrapper, and the application's own
licence is stated as MIT.

**Technical notes.** `README.md`, About tab text. No code.

**Test expectations.** None automated; review against §4.2 and §3.6.

**Size:** S · **Depends on:** E6.S1, E6.S6, E8.S3 (for the offline wording) · **Risk:** none. ·
**DoD:** no claim in the copy outruns its evidence grade.

---

## Epic 8 (H): Offline fallback prototype

**Increment 7. Prototype branch. It does not merge unless the measurements say so.**

**Goal.** The owner asked for *"a small, fast, one-click, offline local translator that takes over when internet
requests fail, even at a RAM cost"* (`README.md`, OQ-C answer). That is Bergamot, under **architecture amendment
A-1**.

**Amendment A-1, in force for this epic** (`README.md`, owner's answers to OQ-A–C, 2026-09-06). Bergamot stays the
**last tier of both chains**, and:
- **(a)** it installs in **one click from the About tab**, downloading on consent, per `ux-mode-degrade.md` §4;
- **(b)** once installed it is **kept loaded while LIVE is running** — unloaded only after LIVE stops **plus** an
  idle timeout — which supersedes the plain "unload after `IdleUnloadMinutes`" of `architecture-cible.md` §7.6
  constraint 2;
- **(c)** the RAM budget line of `architecture-cible.md` §7 is **relaxed by the owner's explicit acceptance**
  (+127–310 MiB while active).
True LLMs (Qwen/Gemma/Phi, 1–3 GB, seconds per line on CPU) remain rejected. The LLM **API** tier is parked (OQ-C).

**Scope.** The U7 packaging spike, the provider itself, the one-click install flow, the four measurements, and the
published decision.

**Dependencies.** E3 (needs the chain), and preferably E5. **Increment mapping:** `plan-migration.md` increment 7.

**Invariants this epic must keep:** **I6 (downstream of `SlangGlossary.Expand`, always — measured: raw `данж` →
"dangling")**, I4 (its two failure modes return `(`-prefixed placeholders so nothing is cached), I10 (never at
startup), NFR6 (one new NuGet, prototype branch only), NFR10 (MPL-2.0 declared).

**Definition of done for the epic.** Either a merged, opt-in, lazily-loaded terminal fallback with all four
measurements published — **or** a closed branch with a documented no-go and the numbers that produced it.
**Both outcomes are successes.**

---

### Story E8.S1: Does shipping `bergamot.dll` beside the exe avoid the `%TEMP%` extraction? 🔬 spike (U7)

As the owner,
I want to know whether the offline option costs a startup regression,
So that fixing P2 cannot quietly make P1 worse.

**Acceptance Criteria:**

**Given** U7 and §7.6 constraint 4 — bundling the native DLL re-arms `IncludeNativeLibrariesForSelfExtract`
extraction to `%TEMP%\.net\…`, the exact mechanism implicated in P1
**When** the prototype is published **both ways** (bundled, and beside the exe)
**Then** `tools/diagnostics/Measure-Startup.ps1` is run on both, on a personal Defender-only machine, reporting run
#0 (fresh hash) and warm runs per `mesures-protocole.md`.

**Exit criterion (measurable).** `pre_process_ms` and `in_process_ms` for both layouts, and the file count and total
size of `%TEMP%\.net\PWRUHelper\<id>` after each first launch (baseline: **5 WPF native DLLs, 8.2 MB**).

**Go / no-go.** **Go:** beside-the-exe shows no measurable cold-start regression against today's baseline ⇒ that is
the layout. **No-go:** either layout regresses cold start materially ⇒ the offline engine does not ship in the
portable build, and the finding goes to the owner. **Hard constraint either way:** the portable exe's "one file, put
it anywhere" promise is not negotiable, and `PublishFlagsTests.cs:62-70` (flag parity across the three build paths)
is not weakened without a measurement that says otherwise.

**Technical notes.** `PWRUHelper.csproj`, and possibly `Build Portable EXE.bat` / `Build MSI Installer.bat` /
`.github/workflows/release.yml` — **on the prototype branch only**.

**Test expectations.** `PublishFlagsTests.cs:44-70` must stay green on `main`; any change to it is part of the
merge decision, not of the spike.

**Size:** M · **Depends on:** a machine with the diagnostics scripts (E9.S12) · **Risk:** medium. ·
**DoD:** U7 is closed in §15.1 with both sets of numbers.

---

### Story E8.S2: Prototype `BergamotTranslator`, lazily loaded and LIVE-aware ⛔ Phase 4 (prototype branch)

As a player with no internet,
I want translations to keep working,
So that an outage does not end my evening.

**Acceptance Criteria:**

**Given** §7.6 and amendment A-1
**When** the provider is written
**Then** it sits **last** in both chains, behind `OfflineFallbackEnabled` (default false — false means the tier is
**not even constructed**), references `BergamotTranslatorSharp` (MPL-2.0, NuGet 0.5.1) **on this branch only**, and
runs the synchronous `BlockingService.Translate` inside `Task.Run` — never on the UI thread.

**Given** I6
**When** a line is translated
**Then** it has already been through `SlangGlossary.Expand`; the displayed original and the 🔑 line stay raw. A
local engine placed *instead of* the glossary is unusable (measured: `данж` → "dangling", `хил` → "heel",
`спс` → "ps").

**Given** amendment A-1(b)
**When** the model has been loaded and LIVE is running
**Then** it **stays loaded** for the whole LIVE session; it is unloaded only after LIVE stops **and** an idle
timeout elapses. The plain idle-unload of §7.6 constraint 2 applies when LIVE is not running.

**Given** I10
**When** the app starts
**Then** nothing is loaded — the first load happens on the first fallback use, or on the first LIVE tick after the
user enabled it, never at startup.

**Given** §7.6 constraint 7
**When** the model is missing or initialisation fails
**Then** the two failure modes return a `(`-prefixed placeholder, so nothing is cached (I4); the engine cannot time
out, so I3 does not apply to this leg.

**Given** §5 of the model download
**Then** models are fetched from a **`github.com` mirror**; the `UpdateService` allowlist
(`github.com` / `githubusercontent.com`) is **not widened**.

**Technical notes.** New `Services/BergamotTranslator.cs` (glossary §16), provider id `bergamot`;
`PWRUHelper.csproj` gains the one `PackageReference` on the branch.

**Test expectations.** Unit with a stubbed engine: the tier is absent when the setting is off; the placeholder is
never cached; the load is not triggered at construction.

**Size:** L · **Depends on:** E3.S7 · **Risk:** high on footprint — one model is ~85 % of the app's current working
set — accepted by the owner under A-1(c). · **DoD:** the tier works end-to-end on the branch with the network
disabled.

---

### Story E8.S3: One-click install of the offline engine from the About tab ⛔ Phase 4 (prototype branch)

As a player,
I want to add the offline engine with one click and remove it with another,
So that a 50 MB download is a decision I make, not something an error message does to me.

**Acceptance Criteria:**

**Given** amendment A-1(a) and UX-DR12 / §3.6
**When** the user clicks **Download the offline engine** in About
**Then** and **only then** a consent dialog appears — `MessageBox.Show(this, …)` with an owner, title
`Add the offline engine?` — stating the download size (~22 MB engine + ~30 MB per language pair), the memory cost
(130–310 MB while translating, freed when idle), the storage location (`%AppData%\PWRUHelper\models`) and how to
remove it. `Not now` is the safe default and closes with no trace.

**Given** principle 3 and NFR12
**When** any background failure occurs
**Then** **no dialog is ever raised** — a `MessageBox` over a fullscreen game opened by a LIVE loop is the single
worst thing this app could do. The failure path may only **nudge**, in the status line:
`All engines are paused — you can add an offline engine in About.`

**Given** the download
**When** it runs
**Then** the About row shows `Downloading the offline engine… {p}%` with a `Cancel` button and the app stays fully
usable; on completion it shows
`● Offline engine ready — used only when everything else is unavailable.`; on failure,
`Download failed — {reason}. Nothing was installed.`

**Given** OQ-9 (is the download progress observable?) is unanswered
**When** the mirror does not report a total size
**Then** the copy degrades to `Downloading…` without a percentage rather than inventing one.

**Given** OQ-11, resolved here: **downloaded ⇒ enabled**
**When** the install completes
**Then** `OfflineFallbackEnabled` becomes true and **Remove** is the only off switch — there is no second checkbox.
`Remove` asks for confirmation with `MessageBox.Show(this, …)` and states that it deletes the files.

**Given** I12
**When** the About controls are added
**Then** every one of them obeys `_restoringSettings` and `ApplySettings` applies their side effects explicitly.

**Technical notes.** `MainWindow.xaml` (the offline block of §4.2), `MainWindow.xaml.cs`;
`Services/SettingsService.cs` for `OfflineFallbackEnabled`. **OQ-11's resolution changes what a persisted setting
means and is flagged for the owner in the readiness report (gap R-4).**

**Test expectations.** Unit: `OfflineFallbackEnabled` round-trips; the nudge string is used and no dialog API is
reachable from the LIVE path. Manual: install, translate offline, remove.

**Size:** M · **Depends on:** E8.S2 · **Risk:** medium — a modal on the wrong thread is the failure this story
exists to prevent. · **DoD:** no background path can open a dialog.

---

### Story E8.S4: Measure RAM, load time, slang quality and cold-start impact 🔬 spike (U6)

As the owner,
I want four numbers before this ships,
So that the app's stated virtue — not lagging the game — is defended by measurement rather than by hope.

**Acceptance Criteria:**

**Given** the four measurements of `plan-migration.md` increment 7
**When** they are taken
**Then**: **(1) RAM** — the resident delta while active, measured **on the P1-affected machines, not the dev box**
(U6); **(2) load time** — whether the 103–119 ms measured on the dev box holds on target hardware; **(3) quality** —
the output on **≥ 30 real PW-RU chat lines after `SlangGlossary.Expand`**, side by side with the current cloud
output; **(4) P1 regression** — cold start with the DLL bundled versus beside the exe (E8.S1's numbers).

**Exit criterion (measurable).** Four recorded figures with the machine sheet of each machine they came from.

**Go / no-go — all four must pass** (`plan-migration.md` increment 7 "Go criteria"): RAM delta **≤ 150 MiB while
active** **and** it unloads when idle (after LIVE stops, per A-1(b)) · **no measurable cold-start regression** ·
post-glossary quality judged acceptable **by the owner on his own chat lines**. **Any one failing ⇒ no-go**, and the
branch is closed with its numbers recorded in this folder.

**Given** the reference figures
**Then** they are stated for comparison: `tiny` ru→en init 103–119 ms, 6.5–12.1 ms/line, 64–80 lines/s, **+127 MiB
USS and not tunable**; COMET-22 ru→en 0.8497 vs Google 0.8785.

**Technical notes.** Same harness as `benchmark…` §10; run during the P1 measurement campaign (E9.S12) so the
machines are already instrumented.

**Test expectations.** None; a measurement campaign.

**Size:** M · **Depends on:** E8.S2, E8.S1, E9.S12 · **Risk:** medium. · **DoD:** U6 is closed in §15.1; the four
numbers are published.

---

### Story E8.S5: Record the go/no-go decision, either way 👤 owner task

As the owner,
I want the decision and its numbers in the repository,
So that nobody re-opens it in six months without new evidence.

**Acceptance Criteria:**

**Given** E8.S4's four figures
**When** the decision is taken
**Then** it is written into `docs/investigations/02-traduction/` with the numbers, the date, the machines and the
verdict — **go** (the branch is merged as an opt-in, lazily-loaded terminal fallback) or **no-go** (the branch is
closed).

**Given** R8
**When** the verdict is no-go
**Then** nothing else in the design is affected — Bergamot is last and nothing depends on it.

**Given** `plan-migration.md` increment 7
**Then** **both outcomes are successes** and the document says so.

**Technical notes.** Documentation only.

**Size:** S · **Depends on:** E8.S4 · **Risk:** none. · **DoD:** the decision is recorded with its evidence.

---

## Epic 9 (P): Packaging, signing and expectations

**Parallel track P. Touches no translation code and can start immediately.**

**Goal.** The first launch after an update stops reading as a bug — through a stable publisher identity that lets
reputation accumulate across releases, and through telling users the truth before they hit it.

**Scope.** The nine SignPath steps, the expectation copy in three placements, the release-checklist update, and the
diagnostics campaign that is the prerequisite for every P1 claim.

**Out of scope.** Any app-code startup fix. `architecture-cible.md` §13 closes splash screens, ReadyToRun,
compression, trimming/AOT and "ship fewer releases" as **decisions**, so that **no story re-opens them**. Also out:
the MSI native-DLL option (§13 item 2 — optional, low value, and it would require deliberately weakening
`PublishFlagsTests.cs:62-70`).

**Dependencies.** None on E1–E8. **Increment mapping:** `plan-migration.md` track P, expanded from
`recommandations.md` §4 (nine steps) and §7 (validation).

**Invariants this epic must keep:** **NFR10 (the licence stays MIT)**, NFR5, and the publish-flag parity guarded by
`PublishFlagsTests.cs:44-70` — **`-p:EnableCompressionInSingleFile` must never be written into
`packaging/signpath-signing.md`, even as an example**, because `PublishFlagsTests.cs:44-60` scans that file.

**Definition of done for the epic.** Both artefacts of a tagged release are signed by SignPath Foundation, verified
with `Get-AuthenticodeSignature` on a **downloaded** copy including the exe *inside* an MSI install; the README and
every release note carry the expectation line; the release checklist covers the new state files; and at least two
affected machines plus one fast control have been measured before and after.

---

### Story E9.S1: Close PR #49 and confirm the MIT licence 👤 owner task

As the owner,
I want the licence question settled first,
So that no signing work is wasted and the certificate is never put at risk.

**Acceptance Criteria:**

**Given** SignPath Foundation requires an **OSI-approved licence**, and open PR #49 proposes CC BY-NC 4.0, which is
not OSI-approved and would disqualify the project (`packaging/signpath-signing.md:23,33`)
**When** the owner closes PR #49
**Then** the repository licence remains **MIT** (`README.md:230`) and **OQ-D is answered**.

**Given** `recommandations.md` §4 step 1 — *"this is a gate, not a preference"*
**When** any later story in this epic is started
**Then** it is blocked until this one is done: doing the signing work first and merging PR #49 later would waste all
of it.

**Technical notes.** No code. GitHub only.

**Test expectations.** None.

**Size:** S · **Depends on:** — · **Risk:** none. · **DoD:** PR #49 is closed; OQ-D is marked answered in
`architecture-cible.md` §15.3.

---

### Story E9.S2: Apply to SignPath Foundation and wait for approval 👤 owner task

As the owner,
I want the free certificate application submitted,
So that the weeks of waiting start now rather than after the code work.

**Acceptance Criteria:**

**Given** the prerequisites table of `recommandations.md` §4 all hold (OSI licence after E9.S1; public repository;
at least one published release; documented functionality; the signing team is the maintaining team)
**When** the application is submitted at `https://about.signpath.io/product/open-source`
**Then** every field is copied from the pre-answered table at `packaging/signpath-signing.md:26-38` — project name,
repository URL, download page, licence, description, why signing is needed, build system, artefacts, contacts.

**Given** the expected timeline of *"a few days to a few weeks"* (`packaging/signpath-signing.md:40`)
**When** the application is pending
**Then** **nothing in E9.S3–E9.S8 is started** — the connector URL and the artefact-configuration slugs do not exist
until approval arrives.

**Given** the product consequence
**Then** the owner has accepted that the certificate names **"SignPath Foundation"** as the publisher, not
"Kizotis" (`packaging/signpath-signing.md:10-13`).

**Size:** M (mostly waiting) · **Depends on:** E9.S1 · **Risk:** medium — an indefinite external approval delay. ·
**DoD:** an application reference exists; approval is recorded when it arrives.

---

### Story E9.S3: Create the SignPath project and note the five identifiers 👤 owner task

As the owner,
I want the dashboard configured,
So that the CI has something to point at.

**Acceptance Criteria:**

**Given** approval has arrived
**When** the dashboard is configured per `packaging/signpath-signing.md:44-54`
**Then** the following exist and are noted: the **Organization ID**; a **Project** (slug e.g. `pwru-helper`) linked
to the GitHub repository; a **Signing policy** (slug e.g. `release-signing` — **release**, not test, for published
binaries); **two artefact configurations**, one for the PE (`.exe`) and one for the MSI, each a simple single-file
configuration because one file is uploaded per request; and a **CI user API token**.

**Size:** S · **Depends on:** E9.S2 · **Risk:** low. · **DoD:** the five identifiers are recorded where E9.S4 can
use them.

---

### Story E9.S4: Add one secret and six variables to GitHub 👤 owner task

As the owner,
I want the CI credentials in place,
So that the workflow change can be applied and stay dormant until it is wanted.

**Acceptance Criteria:**

**Given** `packaging/signpath-signing.md:55-66`
**When** *Settings → Secrets and variables → Actions* is configured
**Then** the secret `SIGNPATH_API_TOKEN` and the six variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`,
`SIGNPATH_POLICY_SLUG`, `SIGNPATH_EXE_ARTIFACT_CONFIG`, `SIGNPATH_MSI_ARTIFACT_CONFIG`, `SIGNPATH_CONNECTOR_URL`
exist.

**Given** the verified fact that `.github/workflows/release.yml` contains **zero** references to SignPath today
**When** these are set
**Then** **nothing changes** — a tagged release keeps shipping unsigned until E9.S5 is applied. The story must not
be reported as "signing is on".

**Size:** S · **Depends on:** E9.S3 · **Risk:** low. · **DoD:** the seven entries exist and the "not live yet" fact
is stated in the PR/issue that closes this story.

---

### Story E9.S5: Wire signing into the release workflow, with job-level `env` guards ⛔ Phase 4

As a user downloading a release,
I want the binary to carry a stable publisher identity,
So that Windows stops treating every version as a file it has never seen from anyone.

**Acceptance Criteria:**

**Given** `.github/workflows/release.yml` has no job-level `env:` today (`release.yml:16-19`)
**When** signing is wired
**Then** a job-level `env: SIGNING_ENABLED: ${{ secrets.SIGNPATH_API_TOKEN != '' }}` is added and **every SignPath
step tests `env.SIGNING_ENABLED == 'true'`**.

**Given** the trap — `secrets` is **not** in GitHub's documented list of contexts available to a step-level `if:`
(`github, needs, strategy, matrix, job, runner, env, vars, steps, inputs`), so the drafted
`if: ${{ secrets.SIGNPATH_API_TOKEN != '' }}` guards at `packaging/signpath-signing.md:106,113,141,148` may evaluate
false or error
**When** the block is applied
**Then** **none of those step-level `secrets` guards survives** — this is the one substantive correction to the
draft.

**Given** the order matters
**When** the single step at `release.yml:49-68` is split
**Then** it becomes: publish → stage the exe → **sign the exe** → build the MSI **from `dist/PWRUHelper.exe`** →
**sign the MSI** → publish the release. `installer/Product.wxs:40-59` wraps exactly one file with `KeyPath="yes"`,
so signing the exe after the MSI is built would ship an **unsigned** binary inside every MSI. `Product.wxs:8,42`
accepts the new path unchanged because it arrives as `-d ExeFile=`.

**Given** `PublishFlagsTests.cs:62-70` compares the `-p:` flags of `Build Portable EXE.bat:20-23`,
`Build MSI Installer.bat:29-32` and `release.yml:42-47`
**When** this change lands
**Then** **not one `-p:` flag is added, removed or altered** (`release.yml:42-47` untouched).

**Given** the winget step (`release.yml:87-103`)
**Then** it is unchanged and stays last.

**Technical notes.** The exact YAML is written out in `recommandations.md` §4 step 6 and is to be applied as
written.

**Test expectations.** `PublishFlagsTests` green. The workflow itself is proven by E9.S7.

**Size:** M · **Depends on:** E9.S4 · **Risk:** medium — a per-release CI step that can fail a release. ·
**DoD:** a dry tag on a branch produces both artefacts with signing dormant.

---

### Story E9.S6: Keep `packaging/signpath-signing.md` in sync, in the same PR ⛔ Phase 4

As the maintainer,
I want the drafted document and the live workflow to agree,
So that a docs mismatch cannot turn the build red.

**Acceptance Criteria:**

**Given** `PublishFlagsTests.cs:72-79` (`The_drafted_signing_workflow_would_not_change_the_shipped_build`) compares
`release.yml`'s flags with `packaging/signpath-signing.md`'s
**When** E9.S5 edits the workflow
**Then** the markdown is edited **in the same PR**, or the test goes red on a docs mismatch.

**Given** `PublishFlagsTests.cs:44-60` (`The_single_file_bundle_is_never_compressed`) also scans that markdown
**When** the file is edited
**Then** **`-p:EnableCompressionInSingleFile` is never written into it, even as an example** — the flag cost
~110–118 MB of working set and was removed on purpose in v0.14.0.

**Given** `BuildFiles` (`PublishFlagsTests.cs:37-42`) does **not** include `docs/investigations/**`
**When** the YAML block is moved from `recommandations.md` into `packaging/`
**Then** it becomes scanned by the suite — which is the intended effect, and the reason the two files must move
together.

**Size:** S · **Depends on:** E9.S5 (same PR) · **Risk:** low. · **DoD:** `PublishFlagsTests` green.

---

### Story E9.S7: Treat the first signed tag as a test 👤 owner task

As the owner,
I want the first signed release verified rather than assumed,
So that a guard that silently did nothing is caught immediately.

**Acceptance Criteria:**

**Given** `recommandations.md` §4 step 8
**When** the first signed tag is pushed
**Then** `gh release view v<x.y.z> --json name,assets` shows **both** `PWRUHelper.exe` and
`PWRUHelper-<version>-setup.msi` attached — the same check the existing release checklist already mandates.

**Given** the guards were the known trap
**When** the release completes
**Then** the job log is read to confirm the SignPath steps actually **ran**, rather than being skipped by a guard
that evaluated false.

**Size:** S · **Depends on:** E9.S5, E9.S6 · **Risk:** low. · **DoD:** both artefacts verified and the guard
behaviour confirmed from the log.

---

### Story E9.S8: Verify the signature and re-measure on a real downloaded copy 👤 owner task

As the owner,
I want the value of signing to be falsifiable,
So that "signing helped" is a measured pair and not a feeling.

**Acceptance Criteria:**

**Given** `recommandations.md` §4 step 9 and §7.2
**When** the release is **downloaded from GitHub** on a fresh machine — never a local build, which carries no
Mark-of-the-Web and is exactly why the dev box could not exercise SmartScreen at all
**Then** `Get-AuthenticodeSignature` reports `Status = Valid` with subject **SignPath Foundation** for the exe, for
the MSI, **and for the exe inside a `C:\Program Files\PWRU Helper\` MSI install** — that last one is what proves the
sign-then-build order worked.

**Given** V2.1 and V2.2
**When** the measurement runs
**Then** `Measure-Startup.ps1 -Runs 3` is run on the **signed** release and on an **unsigned build of the same
commit**, on the **same machine, in the same session** — the delta between them *is* the value of signing, and
without that pair the claim is unfalsifiable.

**Given** reputation accrues over weeks (a signed-but-low-reputation file still shows the blue screen)
**When** the first measurement is taken
**Then** it is **re-run about four weeks later**, because a single post-release measurement understates the benefit.

**Given** the success thresholds of `recommandations.md` §7.4
**Then** they are the yardstick: `pre_process_ms` first launch **≤ 500 ms** (dev-box fresh-hash baseline
2163–2528 ms), `total_ms` first launch **≤ 3.0 s** (reported 6–10 s), `total_ms` warm **≤ 2.0 s** (no regression),
`in_process_ms` **unchanged** — a *rise* there would mean the packaging change broke something.

**Given** what signing does **not** fix (§4 "What signing does not fix")
**Then** the report states it plainly: no first-party source says a signature short-circuits Block at First Sight;
EV would not help; and it does nothing for the local scan of a 178 MB image.

**Size:** M · **Depends on:** E9.S7, E9.S12 (the before-baseline) · **Risk:** low. · **DoD:** the signed/unsigned
pair exists on the same machine, and the four-week re-run is scheduled.

---

### Story E9.S9: README and release-note expectation text ⛔ Phase 4 (docs)

As a first-time user,
I want to be told that the first launch is slow before I experience it,
So that I read a wait as normal instead of as a crash.

**Acceptance Criteria:**

**Given** UX-DR14 and `recommandations.md` §3.1, which says to take Sally's copy **verbatim**
**When** the README gains a new bullet under `⬇️ Download & use`
**Then** it reads: *"**The first launch after downloading — and after every update — can take up to about 10
seconds, with nothing on screen.** Windows checks a file it has never seen before. Later launches are fast (about a
second). Every update is a brand-new file as far as Windows is concerned, so the check happens again after each
one."* — and the same words go into `checklist-nouvelle-machine.md` §A4.

**Given** the release-note placement
**When** any release is cut
**Then** one line appears at the top, verbatim, **every time**: *"First launch after this update can take a few
seconds while Windows checks the new file. Launches after that are back to normal."*

**Given** the existing README note covers only the SmartScreen dialog (`README.md:86-88`) and says nothing about the
silent wait that precedes it
**When** the bullet lands
**Then** it complements that note rather than replacing it.

**Given** the placement guidance of `recommandations.md` §3.4
**Then** the README also says: put the portable exe in a plain local folder (not a OneDrive-synced Desktop or
Downloads, where an evicted file must re-download ~180 MB before it can start), and unblock it after downloading
(right-click → Properties → **Unblock**, or `Unblock-File`).

**Given** rank 2's condition
**Then** this text is **never** presented as a substitute for signing.

**Technical notes.** `README.md`, `docs/investigations/01-demarrage/checklist-nouvelle-machine.md`. No code. This is
the **only P1 deliverable that ships immediately** (V2.3).

**Size:** S · **Depends on:** — · **Risk:** none. · **DoD:** the exact sentences are in place, unedited.

---

### Story E9.S10: One-time in-app "first launch after an update" toast ⛔ Phase 4

As a user who never reads the README,
I want the app itself to explain the wait I just had,
So that a known behaviour stops arriving as a bug report.

**Acceptance Criteria:**

**Given** UX-DR14's third placement (`ux-mode-degrade.md` §3.8)
**When** the app starts for the first time after the version string changed
**Then** it shows **once**:
`Updated to v{X.Y.Z}. The first launch after an update is slower — Windows checks the new file. The next ones are
fast again.`

**Given** the "fire exactly once per version" rule
**When** it is implemented
**Then** a new `LastRunVersion` field on `AppSettings` is **seeded by `Migrate` with the *current* version**, so no
existing user gets a spurious toast on the release that ships this feature.

**Given** **I13**
**When** `LastRunVersion` is seeded through `Migrate`
**Then** `SettingsVersion` **is** bumped in this story — and only in this story. (`architecture-cible.md` §12 rules
that the five *translation* fields need no bump; this is a different field with a different requirement. The
interaction is flagged in the readiness report, gap R-5.)

**Given** the routing hazard, CONFIRMED in code
**When** the app opens straight into compact mode
**Then** the toast is **suppressed** — `ShowToast` routes to `_overlay.SetStatus`
(`MainWindow.xaml.cs:400-403`) and would overwrite the LIVE status line mid-raid.

**Given** the existing toast mechanism
**Then** it is reused (`MainWindow.xaml.cs:400`, which already gives >40-character messages 3.5 s) — no new window,
no new control.

**Technical notes.** `Services/SettingsService.cs:102,133-159` (`Migrate`), `MainWindow.xaml.cs:400-403`.

**Test expectations.** Unit: the toast fires exactly once per version change and never in compact mode (UX
acceptance hint 9); a `Migrate` test proving an existing `settings.json` is seeded with the current version and
therefore silent.

**Size:** S · **Depends on:** E9.S9 (same copy family) · **Risk:** medium — this touches `Migrate`, the machinery
that has already produced two shipped bugs. · **DoD:** an upgrading user sees nothing; a user on the *next* version
sees it once.

---

### Story E9.S11: Extend the release checklist for the new state files ⛔ Phase 4 (docs)

As the maintainer,
I want the checklist to cover what the new architecture writes to disk,
So that a lazily-created file cannot quietly become a startup cost.

**Acceptance Criteria:**

**Given** the existing checklist in `project-context.md` (bump `<Version>` in `PWRUHelper.csproj`; sync `VERSION=`
in `Build MSI Installer.bat`; land on `main` via PR; annotated tag; verify both artefacts with
`gh release view vX.Y.Z --json name,assets`)
**When** it is extended
**Then** it gains one line: **after a release that touches the translation path, confirm `provider-state.json` and
`translation-cache.json` are created lazily and not at startup** (I10).

**Given** E9.S5
**When** signing is live
**Then** the checklist also names the `Get-AuthenticodeSignature` verification of E9.S8 as a release step.

**Size:** S · **Depends on:** E2.S4, E4.S2 · **Risk:** none. · **DoD:** the checklist in `project-context.md`
matches what the app now writes.

---

### Story E9.S12: Run the startup diagnostics campaign on the affected machines 👤 owner task

As the owner,
I want at least one slow machine actually measured,
So that every P1 recommendation stops resting on a dev box its own report calls unrepresentative.

**Acceptance Criteria:**

**Given** `recommandations.md` §8 item 1 — **no affected machine has ever been measured**, and ranks 1–6 are all
conditioned on it
**When** the campaign runs
**Then** it covers **≥ 2 affected personal machines** (Defender only, non-corporate) **and ≥ 1 machine that starts
fast** as a control — a fast machine is not a control unless it is documented the same way.

**Given** §7.1
**When** each machine is measured
**Then** the exe is one **actually downloaded from GitHub** (so it carries Mark-of-the-Web — a local build cannot
exercise SmartScreen or BAFS at all), and the sequence is:
`Get-MachineSheet.ps1`, then `Measure-Startup.ps1 -Runs 3` **before the user has opened the new version manually**,
then again with `-LaunchMode Direct`, then again after `Unblock-File`.

**Given** §7.3
**When** the machine sheets are collected
**Then** they carry the fields to diff: `DisableBlockAtFirstSeen`, `CloudBlockLevel`, `CloudExtendedTimeout`,
`MAPSReporting`, `SubmitSamplesConsent`, `RealTimeProtectionEnabled`, `AntivirusSignatureVersion` (a signature update
between runs invalidates Defender's per-file verdict cache and therefore a cold/warm comparison),
`VerifiedAndReputablePolicyState`, the third-party AV list (must read *Defender only*), Mark-of-the-Web presence,
the exe path/length/attributes, whether the path is under `$env:OneDrive`, the `%TEMP%\.net\PWRUHelper` file count
and size **captured before the timed launch**, install kind (portable vs MSI) and OS build.

**Given** the three experiments that split the matrix (§7.4)
**When** they are run
**Then** the machine is measured **offline (airplane mode)**, **after `Unblock-File`**, and **when installed by
MSI**. **Any one of the three coming back "equally slow" falsifies the whole line of reasoning** and hands the case
to the local-scan or OneDrive-hydration hypotheses — and signing would then be a far smaller win than assumed.
**These three run before weeks are committed to the SignPath work.**

**Given** the success thresholds of §7.4
**Then** they are the yardstick for E9.S8's "after" run: `pre_process_ms` ≤ 500 ms, `total_ms` first launch ≤ 3.0 s,
warm ≤ 2.0 s, `in_process_ms` unchanged.

**Given** the privacy statement in `tools/diagnostics/README.md`
**Then** the volunteer is told the scripts are read-only, need no admin, upload nothing, and take ~15 minutes.

**Technical notes.** `tools/diagnostics/Get-MachineSheet.ps1`, `Measure-Startup.ps1`; protocol in
`mesures-protocole.md`; volunteer instructions in `checklist-nouvelle-machine.md` Part A. Results are written to a
new `01-demarrage/mesures-resultats-machines-lentes.md`.

**Size:** M (mostly coordination) · **Depends on:** a volunteer · **Risk:** the only real blocker in this epic — it
has no code and no owner but the owner. · **DoD:** ≥ 2 affected machines and ≥ 1 control measured, and
`recommandations.md` §8 item 1 is closed.

---

## Final validation (step 4)

**FR coverage.** All 35 FRs appear in at least one story; the map above is the trace. No FR is orphaned and no story
implements a requirement that is not in the inventory.

**UX-DR coverage.** All 19 UX-DRs are covered: UX-DR1/2 → E7.S4 · UX-DR3/4 → E7.S3 · UX-DR5 → E7.S2 · UX-DR6 →
E7.S4 · UX-DR7 → E7.S1 · UX-DR8/9/10/11 → E7.S5 · UX-DR12 → E8.S3 · UX-DR13 → E6.S5 · UX-DR14 → E9.S9, E9.S10 ·
UX-DR15 → E7.S7, E6.S4 · UX-DR16 → E6.S3 · UX-DR17 → E5.S3, E7.S4 · UX-DR18 → E7.S3, E7.S2 · UX-DR19 → E7.S1.

**Starter template.** None — brownfield at `4759712`. No scaffolding story exists and none is needed.

**Story sizing.** 62 stories: **31 S, 29 M, 2 L** (E2.S5 `HttpProviderCore`, E8.S2 the Bergamot prototype). Each is
scoped to one PR. The two L stories are the two the architecture itself grades as the riskiest; the increment-2
chain work, an L in `plan-migration.md`, is split here into seven smaller stories so no single PR carries it.

**Forward dependencies.** Checked story by story. Every `Depends on` points **backwards** — to an earlier story in
the same epic, to a completed earlier epic, to an owner decision, or to a field measurement. The two structural
exceptions are declared, not hidden:
- **E2.S7** depends on the E2+E3 release being in the field. It is deliberately post-release and is not scheduled in
  the same sprint as the rest of E2.
- **E8.S1 and E8.S4** depend on E9.S12's instrumented machines — a *parallel* epic, not a later one, and E9 can
  start immediately.

**Epic independence.** E1 stands alone. E2 works on E1 alone but **must not be released without E3** — a release
coupling, not a functional dependency (E2 is functionally complete and correct on its own; it is simply worse for
the user in front of `gtx`). E3 works on E1+E2. E4, E5, E6 and E8 each work on E3. E7 works on E5 (+E6 for one
story's copy). E9 depends on none of them.

**Entity/state-file creation.** `provider-state.json` is created by the story that needs it (E2.S4);
`translation-cache.json` by E4.S2; the five settings fields by E6.S3; `LastRunVersion` by E9.S10. Nothing is created
upfront.

**Phase-4 gate.** **46 of the 62 stories touch production code and carry ⛔.** **9 are spikes or unknown-settling
measurements** (E3.S1 → U1, E3.S2 → U2, E3.S9 → U3, E6.S1 → U4, E6.S6 → U5, E8.S4 → U6, E8.S1 → U7, E4.S3 → U8,
E2.S7 → U9 — exactly one per [UNKNOWN]). **8 are 👤 owner tasks** (E9.S1–E9.S4, E9.S7, E9.S8, E9.S12, and E8.S5,
the go/no-go decision). Two stories carry both markers, because they measure on a branch and then change constants
or packaging (E2.S7, E8.S1). **Phase 4 starts only on the owner's explicit go.**

---

_Companions: `readiness-report.md` (the implementation-readiness assessment for these epics) ·
`../02-traduction/architecture-cible.md` (the design) · `../02-traduction/plan-migration.md` (the increments) ·
`../02-traduction/ux-mode-degrade.md` (the copy). Nothing in this document is implemented._
