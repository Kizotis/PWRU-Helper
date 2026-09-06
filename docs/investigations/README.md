# PWRU Helper — Deep diagnostic investigation (index)

_Mission started 2026-09-06 · baseline commit `4759712` (main, v0.14.0) · orchestrated by Winston (BMAD Architect) · all sub-agents run on Claude Opus._

## Why this folder exists

The code works. Two problems remain that are **not logic bugs** but deep behavioural issues — intermittent and machine-dependent (all machines have fast CPU/RAM/SSD, so hardware is ruled out):

| # | Problem | Symptom |
|---|---------|---------|
| **P1** | Slow, variable startup | 6–10 s from double-click to window on some machines; near-instant on others. Suspects: antivirus/EDR, network, OS, accounts, sync folders, logs — search wide. |
| **P2** | "Google translation limited… retry in some minutes" | Appears at launch or while typing; lasts 1 min, 10 min, or never clears. Google reported "not available" even for manual input. |

## Ground rules (non-negotiable)

1. **Discovery first** — nothing is investigated before the stack is inventoried (`00-*`).
2. **No production code change without the owner's explicit go.** Instrumentation scripts and prototypes live in `tools/diagnostics/` or a dedicated branch.
3. **Everything is documented here** — one file per topic, this index, and a final `SYNTHESE.md` validated by the tech writer.
4. **Hypotheses, not assertions** — every candidate cause carries a probability, a verification method, a fix, and a cost/risk.
5. **Web research cites dated URLs** and separates confirmed from assumed.
6. **BMAD workflows are respected** — documents follow BMAD templates; final stories go to `_bmad-output/`.
7. **A synthesis checkpoint closes every phase**; the next phase starts only on the owner's "go".

Evidence convention used throughout: **[CONFIRMED]** = read in code/config with `file:line` · **[INFERRED]** = derived, reasoning stated · **[UNKNOWN]** = needs measurement or research · **[REPORTED]** = prior measurement from an earlier session, to re-verify.

## Documents

### Phase 0 — Discovery
| File | Owner | Status |
|------|-------|--------|
| [`00-inventaire-stack.md`](00-inventaire-stack.md) | Mary (Analyst, DP workflow) — stack, packaging, dependencies, config, logging, network surface, repo map | **done** (694 lines, 20 open questions) |
| [`00-annexe-demarrage-et-reseau.md`](00-annexe-demarrage-et-reseau.md) | Amelia (Dev, forensic method) — code-level startup path + Google call path | **done** (736 lines, 16 open questions) |

### Phase 1 — Parallel investigations (planned)
`01-demarrage/` — `mesures-protocole.md` (+ script under `tools/diagnostics/`), `trace-instrumentation.md`, `hypotheses-matrice.md`, `recommandations.md`, new-machine checklist.
`02-traduction/` — `analyse-implementation-actuelle.md`, `mecanismes-de-blocage-google.md`, `benchmark-fournisseurs.md`, `architecture-cible.md`, `plan-migration.md`.

### Phase 2 — Synthesis & arbitration (planned)
`SYNTHESE.md` (Paige) · `02-traduction/architecture-cible.md` (Winston, CA).

### Phase 3 — Stories (planned) · Phase 4 — Implementation (owner's go only)

## Environment facts recorded by the orchestrator
- Dev/owner machine account is Azure AD-joined (`AzureAD+…` owner on files) → corporate GPO/EDR/proxy policies are plausible on this box; to be captured in the per-machine sheet.
- Prior startup benchmarks exist (2026-08-04, owner's machine, Defender on): cold 3.9–8.9 s, warm ~1.1–1.25 s; `PublishReadyToRun` measured worse and banned; single-file compression removed in v0.14.0 for RAM (−118 MB). Treated as **[REPORTED]** until re-measured on the slow machines.

---

## Phase 0 — Consolidation by the orchestrator (Winston, 2026-09-06)

Both Phase 0 documents were read in full and cross-checked against each other and against `project-context.md`. **No contradiction** between them; 12 claims of `project-context.md` re-verified by Amelia.

### Reconciled points
- **Mary's P1 question 2** ("does the first frame paint before the GitHub call?") is answered by Amelia's timeline (§1.5–1.6): `OnWindowLoaded` first `await`s a `Task.Run` (OCR engine probe), which yields the dispatcher and lets the first frame render; the update check runs after that. The check can therefore **freeze an already-visible window** (system-proxy/WPAD resolution on the first request, 8 s timeout) but cannot delay the window's appearance. Both agents converge: **zero network before the window is visible**.
- **Mary's [ASSUMED] note in §6.3** ("for a single-file publish `AppContext.BaseDirectory` is the extraction directory") is **corrected**: since .NET 5, single-file apps report `AppContext.BaseDirectory` as the directory of the apphost (the exe itself); only the native libraries go to the `%TEMP%\.net\…` extraction folder. So the portable exe's "next to the exe" data-file candidate really is next to the exe. **[INFERRED — documented .NET host behaviour; trivial to verify in Phase 1 by checking where the portable build creates `Data\`.]**
- Two stale facts in the repo, both corrected in the inventory: `project-context.md` says 142 tests (the suite has ≈256 cases); `Data/slang.json` has no `"version"` key, so its editable copy is never refreshed (unlike phrases/squad). Neither is a P1/P2 cause — recorded for later housekeeping.

### What Phase 0 establishes (facts, `file:line` in the two documents)
| # | Fact | Consequence for the investigation |
|---|------|-----------------------------------|
| F1 | Before the window is visible: no network, no registry, no WinRT, no WMI, no font enumeration in app code. Only synchronous file I/O on the UI thread, all under `%APPDATA%` (Roaming) plus the exe's `Data\` folder, plus 5 `RegisterHotKey`, plus the first layout of a non-virtualized ~140-button Phrasebook grid. | If the 6–10 s are spent **before** any window, the suspects are the OS loader / AV / SmartScreen on an unsigned 178 MB single-file exe, the native-library self-extraction to `%TEMP%`, and roaming/synced/scanned `%APPDATA%`. Not the app's own code volume. |
| F2 | The update check is the **only** startup network call; it fires after first paint, from the UI thread, 8 s timeout, `default` cancellation token, failures **silent and unlogged**. | If the window appears then freezes, WPAD/PAC proxy resolution on the first `HttpClient` request is the prime suspect. |
| F3 | Single-file publish uses `IncludeNativeLibrariesForSelfExtract=true` (all three build paths); `DOTNET_BUNDLE_EXTRACT_BASE_DIR` never set. | First launch of every new build extracts native libs to `%TEMP%\.net\PWRUHelper\<hash>` — every extracted file is AV-scanned; a cleaned `%TEMP%` repeats it. Same failure shape as "PyInstaller onefile". |
| F4 | MSI installs try to write 3 data files into `Program Files\…\Data\` at **every** launch, fail, then fall back to `%APPDATA%`. | 3 denied writes per launch, each visible to an EDR. Portable vs MSI must be compared. |
| F5 | Google: `GET translate.googleapis.com/translate_a/single?client=gtx&sl&tl&dt=t&q`, hard-coded Chrome 120 UA, no `Accept`/`Accept-Language`, HTTP/1.1 default, system proxy, static `HttpClient`, 12 s timeout, retry ×3 on 429/5xx only (300/600 ms fixed, no `Retry-After`). 403 is folded with 400/404; an HTML captcha page surfaces as "unexpected response (may be temporarily blocked)". | The user-facing wording discriminates the cause: "**wait a minute**" = a real 429 on the 3rd attempt; "**may be temporarily blocked**" = HTML/captcha/proxy page; "**HTTP 403**" = bot block/geo/proxy; "**no Internet connection**" = DNS/TLS/proxy failure. |
| F6 | **No rate limiter, no circuit breaker, no cool-down timer, no persisted state** anywhere in the app. The "minutes" is advice text, not a mechanism. Cache is in-memory LRU 500, successes only, never persisted; two independent caches (read path vs write path). | Any symptom that "lasts minutes or never clears" is **external to the process** (IP/range throttle, proxy, DNS intercept) — or a stale pooled connection on a process-lifetime static client (Q2.6). |
| F7 | Translation is **never** triggered per keystroke (Enter/click only) and **never** at startup. LIVE worst case ≈120–240 req/min at slider 100 % (default 92 % ≈ 85/min), ×3 on retries, and a mismatched batch can explode to per-line requests. | "At launch" must mean the first user action after launch, a still-running LIVE loop, or a block inherited from a previous session. LIVE burst pattern is the most plausible trigger of a per-IP throttle. |
| F8 | `TranslationService` **logs nothing**; only `FallbackTranslator` logs, and only when a DeepL key is set. | The About-tab "Copy error report" is empty for P2 today. Phase 1 instrumentation must add status-code + response-snippet logging (on a branch). |

### Gating questions for the owner (they split the suspect lists in half)
1. **P1 — Q1.1:** on the slow machines, do the 6–10 s pass **before any window appears**, or does the window appear quickly and then **freeze**? (Stopwatch is enough.)
2. **P2 — Q2.1:** **which exact sentence** do users see? Candidates, verbatim from the code: `Google is limiting translations right now — wait a minute and try again.` · `The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.` · `Translation service error (HTTP 403). Please try again later.` · `Failed: no Internet connection` · `(rate-limited — try again shortly)`. A screenshot settles it.
3. Per affected machine: portable exe or MSI? Which antivirus/EDR? Corporate/Azure AD-joined or personal? VPN/proxy? Is `%APPDATA%` or `%TEMP%` redirected/synced? Was LIVE running when P2 appeared? Does restarting the app clear P2, or only waiting?

### Merged open-question count
Mary 20 + Amelia 16 → **28 distinct** after de-duplication (8 overlap). All carried into Phase 1 as the seed of `01-demarrage/hypotheses-matrice.md` and `02-traduction/analyse-implementation-actuelle.md`.
