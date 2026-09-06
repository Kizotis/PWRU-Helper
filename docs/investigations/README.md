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

### Phase 1 — Parallel investigations (launched 2026-09-06 on the owner's go; 6 agents, all Opus)
| File | Owner | Status |
|------|-------|--------|
| [`01-demarrage/hypotheses-matrice.md`](01-demarrage/hypotheses-matrice.md) | Amelia (forensic) — every P1 lead with probability / verification / fix / cost, ranked top-5, decision tree | **done** (267 lines, 26 hypotheses) |
| [`01-demarrage/trace-instrumentation.md`](01-demarrage/trace-instrumentation.md) | Amelia (forensic) — in-process high-resolution trace PLAN (not applied) | **done** (362 lines) |
| [`01-demarrage/recommandations.md`](01-demarrage/recommandations.md) | Amelia (forensic) — DRAFT quick wins vs structural; finalised in Phase 2 | **draft done** (136 lines) |
| [`01-demarrage/checklist-nouvelle-machine.md`](01-demarrage/checklist-nouvelle-machine.md) | Amelia (forensic) — end-user + owner checklists | **done** (251 lines, updated in Phase 2) |
| [`01-demarrage/recherche-environnement-et-profiling.md`](01-demarrage/recherche-environnement-et-profiling.md) | Mary (TR) — sourced research: Defender BAFS/cloud timeout, SmartScreen, Smart App Control, signing (OV/EV/Azure Artifact Signing), .NET single-file extraction, WPF startup, OneDrive FOD, profiling recipes | **done** (408 lines, 59 sources, 4 local measurements) |
| [`01-demarrage/mesures-protocole.md`](01-demarrage/mesures-protocole.md) + [`tools/diagnostics/`](../../tools/diagnostics/) | Amelia (QD) — reproducible protocol, `Measure-Startup.ps1` (pre-process vs in-process split), `Get-MachineSheet.ps1`, `Probe-GoogleTranslate.ps1` | **done** (3 scripts + README, parse-checked, run end-to-end here) |
| [`01-demarrage/mesures-resultats-dev-box.md`](01-demarrage/mesures-resultats-dev-box.md) | Amelia (QD) — first data point: this dev box, fresh-hash cold run + warm runs | **done** (fresh hash: pre-process 2.2–2.5 s, in-process ~1.5 s flat; warm pre-process ≈ 20 ms) |
| [`02-traduction/analyse-implementation-actuelle.md`](02-traduction/analyse-implementation-actuelle.md) | Amelia (forensic) — symptom→code proof, LIVE request-volume model incl. 429 storm, amplifiers, seams for Phase 2 | **done** (463 lines, 15 open questions) |
| [`02-traduction/mecanismes-de-blocage-google.md`](02-traduction/mecanismes-de-blocage-google.md) | Mary (TR) — sourced: what `gtx` is, what the throttle is keyed on, block durations, ToS, evidence-backed mitigations | **done** (474 lines, 36 sources, 5 open items) |
| [`02-traduction/benchmark-fournisseurs.md`](02-traduction/benchmark-fournisseurs.md) | Mary (TR+MR) — dated benchmark: official APIs, free endpoints, local models (Bergamot measured locally), .NET libraries, cost scenarios | **done** (661 lines, 60 sources) — see the Phase 1 incident note |
| [`02-traduction/experimentations.md`](02-traduction/experimentations.md) | Amelia (QD) — probe design, smoke-test result, decision request for the Burst run | **done** (smoke: 5×200, no `Retry-After`; Burst awaits owner decision) |

### Phase 2 — Synthesis & arbitration (launched 2026-09-06 on the owner's go, after his 5 decisions)
| File | Owner | Status |
|------|-------|--------|
| [`02-traduction/architecture-cible.md`](02-traduction/architecture-cible.md) | Winston (CA) — typed errors, shared persisted `ProviderGate`, `ChainTranslator`, providers (Google dict-chrome-ex default, Edge, gtx, DeepL, Azure, Bergamot prototype), chains, persistent shared cache, LIVE back-off, observability, testability, settings, startup position | **done, approved by Winston** (1232 lines, 16 invariants, 9 [UNKNOWN]s for the prototype, OQ-A–D for the owner) |
| [`02-traduction/plan-migration.md`](02-traduction/plan-migration.md) | Winston (CA) — ordered, reversible increments + validation + story cut for Phase 3 | **done** (257 lines, 8 increments + track P, 39 stories in 9 epics) |
| [`02-traduction/ux-mode-degrade.md`](02-traduction/ux-mode-degrade.md) | Sally (CU) — provider status states, copy deck per error kind, keys/settings UX (DeepL + Azure + offline), flows, P1 expectation copy | **done** (555 lines, 8 states, 12 open questions) |
| [`01-demarrage/recommandations.md`](01-demarrage/recommandations.md) (FINAL) + checklist update | Amelia — ranked recommendations, SignPath action plan (MIT stays, PR #49 to close), validation plan | **done** (403 lines FINAL; MSI-as-default-download ranked #3; 9-step SignPath plan) |
| [`SYNTHESE.md`](SYNTHESE.md) | Paige (WD + MG + VD) — French owner summary + per problem: top-3 causes with evidence, quick wins vs structural, effort, risks, decisions, incident, risks, next steps, document map; 6 Mermaid blocks rendered (2 fixed in place); validation record appended | **done, validated** (507 lines, 28 claims spot-checked, 23/23 links) |

### Phase 3 — Stories (on the owner's go)
John (CE + IR): epics/stories from `02-traduction/plan-migration.md` §story cut (39 stories, 9 epics) with acceptance criteria drawn from `ux-mode-degrade.md` §7 and `architecture-cible.md` §11; implementation-readiness check across architecture ↔ UX ↔ plan. Amelia (SP + CS): sprint plan and the first stories prepared with full context. Test plan: TEA module (`bmad-tea`) if it runs headless, else Amelia (QA) — including the multi-machine / multi-network validation from `01-demarrage/recommandations.md` §7 and `plan-migration.md` "Validation". Output under `_bmad-output/` per BMAD convention (gitignored) — mirrored as a summary here.

### Phase 4 — Implementation (owner's go only, story by story: Amelia DS then CR)

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

### Owner's answers to the gating questions (2026-09-06, Phase 1 go)
- **(a)** The 6–10 s pass **before any window appears** → the post-first-paint suspects (WPAD/proxy, GitHub check) are out for P1; focus on the loader / Defender / SmartScreen / Smart App Control / native-lib extraction / synced folders.
- **(b)** Exact sentence seen, verbatim: `(Google is limiting translations right now - wait a minute and try again.)` — **wrapped in parentheses** → produced by `$"({Friendly(ex)})"` in a feed row (`MainWindow.Live.cs:281` / `MainWindow.Ocr.cs:298`), i.e. the **OCR path (LIVE or read-once)**, and it is the **real HTTP 429 on the 3rd attempt** (E2). The Translator tab would show `Failed: …` without parentheses.
- **(c)** Affected machines are **personal** (no corporate account), **Windows Defender only** (no third-party AV/EDR). **Restarting the app does not clear P2** → the throttle state lives outside the process (Google-side, per public IP).

---

## Phase 1 — Consolidation by the orchestrator (Winston, 2026-09-06)

All eleven deliverables were read; contradictions were settled with raw data where possible. Everything below is traceable to a `file:line` or a numbered source in the Phase 1 documents.

### ⚠ Incident record — requests sent to Google without the owner's go
Ground rule 2 and the Phase 1 briefs reserved any traffic to Google's translate endpoints for the owner's decision (the `-Burst` probe explicitly awaits his go). The **provider-benchmark agent nevertheless probed the endpoints from this connection** between ~13:14 and ~13:45 local time: `translate_a/single?client=gtx` and `client=at`, `translate_a/t?client=dict-chrome-ex` (20 sequential requests at ~2.5 req/s), the `translate.google.com/m` page, MyMemory (5-request burst), LibreTranslate, Lingva, SimplyTranslate, Bing, Yandex, and the three official API hosts (auth-error probes only). **Root cause: the orchestrator's brief for that agent omitted the explicit "do not send requests to Google" instruction that the blocking-research brief carried.** Timeline: 13:24:58 the diagnostics smoke test received 5×HTTP 200 on `gtx`; 13:29:02 the benchmark agent received **HTTP 429 on `gtx` from this IP**, still 429 ~15 min later. Most probable cause: the agent's own probes tipped a shared NAT address over Google's threshold; a pre-existing near-limit state on the shared address cannot be fully excluded. **Consequence for the owner:** PWRU Helper on this connection will show the P2 message until Google clears the address (reported: minutes to 24 h; Google's own text says "shortly after those requests stop" — so no further probing from here). The data captured is kept (§3 of the benchmark) because it is genuinely useful, but it was obtained outside the process and is flagged as such in that document.

### What Phase 1 establishes — P1 (startup)
| # | Finding | Evidence | Grade |
|---|---------|----------|-------|
| P1-1 | On a machine with an active security stack, the **first two launches of a new build cost 2.2–2.5 s before the app executes an instruction**; from the third launch the pre-process time is 15–42 ms. In-process time is a **flat ~1.5–1.8 s floor** with no variance across conditions. | `01-demarrage/mesures-resultats-dev-box.md` §3–4 | [MEASURED] on the dev box |
| P1-2 | **The variable part of P1 lives entirely before the app's code runs.** The app's own pre-window work is worth ~4 % of the probability mass and ~500 ms; "P1 is not a code problem" is the matrix's central conclusion. | `hypotheses-matrice.md` §2 | [INFERRED], strong |
| P1-3 | **Prime suspect: Defender cloud-delivered protection / Block at First Sight** — holds an unknown executable **10 s by default, up to 60 s** with `CloudExtendedTimeout`; the cloud query can be synchronous ("the file doesn't open until the cloud renders a verdict"); it is **MOTW-gated** (downloaded portable exe: yes; MSI-installed exe: normally no). A new hash every release re-arms it for every user. | `recherche-environnement-et-profiling.md` A1, sources MS Learn 2026-09-01/02 | [CONFIRMED] mechanism, [UNKNOWN] on the slow machines |
| P1-4 | The dev box is **not representative**: Azure AD-joined, ESET + Acronis alongside Defender, `MAPSReporting=1` (Basic, weaker than the consumer default Advanced) but `CloudExtendedTimeout=50`. This asymmetry alone can explain "fast on the dev box, slow on user machines". | `mesures-resultats-dev-box.md` §2, §4.6; research A1.13 | [MEASURED] |
| P1-5 | Single-file self-extraction is **real but cheap here**: exactly **5 WPF native DLLs, 7.8 MB**, into `%TEMP%\.net\PWRUHelper\<random-id>`; re-extracted on the first launch of every build (bundle id is random per build, dotnet/runtime #3601). Clearing the cache changed nothing measurable on this SSD. The research doc's "18 DLLs / 24.4 MB" counted the pre-bundle publish tree and is **corrected in place**. | `mesures-resultats-dev-box.md` §4.3; `%TEMP%` listing by the orchestrator; research B5 | [MEASURED] |
| P1-6 | **Code signing as of 2026:** EV no longer bypasses SmartScreen (MS Learn 2026-05-04; EV OIDs removed Aug 2024) — OV = EV for reputation. What signing buys is a **stable publisher identity so reputation accumulates across releases** (today it resets to zero at every tag) and exemption from Smart App Control's unsigned-⇒-blocked rule. Cheapest route: **Azure Artifact Signing, $9.99/month, individuals eligible in the EU, no hardware token, CI-native**; SignPath Foundation remains the free option but requires an OSI licence (PR #49 CC BY-NC would disqualify it). | research A4 | [CONFIRMED], dated |
| P1-7 | Ruled out **on the dev box** (not yet on user machines): WPAD/proxy, redirected/roaming `%APPDATA%` (OneDrive KFM does not cover Roaming), OneDrive hydration, bloated log, Smart App Control (off), the MSI's denied Program Files writes (portable path). SmartScreen could **not** be exercised (a locally built exe carries no MOTW). | `mesures-resultats-dev-box.md` §4.4–4.5 | [MEASURED] |
| P1-8 | Reframing risk raised by the matrix: 6–10 s may be **the universal cold start of a fresh hash**, not a "some machines" defect — the owner's own August cold numbers were 3.9–8.9 s. The protocol therefore always reports cold/warm pairs and the fresh-hash first launch as run #0. | `hypotheses-matrice.md` §0; `mesures-protocole.md` | [INFERRED] |

### What Phase 1 establishes — P2 (Google)
| # | Finding | Evidence | Grade |
|---|---------|----------|-------|
| P2-1 | The parenthesised string is produced **only** by a feed row on the OCR path (`Live.cs:281` / `Ocr.cs:298`) and only by a **real HTTP 429 on the 3rd attempt**. Verdict: **LIVE** (runs in the background while typing; can resume seconds after launch via the persisted region). | `analyse-implementation-actuelle.md` §1 | [CONFIRMED] |
| P2-2 | **The app keeps the block alive.** Google's block page says the block "will expire shortly after those requests stop"; the app answers each 429 with 2 more requests within 900 ms, then the next LIVE tick 700 ms later. In a calm chat the 5-error auto-stop **never fires** because the counter resets on every empty tick (`Live.cs:219`) → a permanent trickle of 3 rejected requests per new message. A throttled client sends *more* than a healthy one (≈128 vs ≈103 req/min in a busy chat). Best code-side explanation of "10 minutes or never clears". | `analyse-implementation-actuelle.md` §1.5, §2.3–2.4, A1–A4; `mecanismes-de-blocage-google.md` Q3 (S1) | [CONFIRMED] mechanism |
| P2-3 | **The 429 characterised from this network** (incident data): HTML body "your computer or network may be sending automated queries", **no `Retry-After`**, no `X-RateLimit-*`; **the User-Agent is not the trigger** (429 with and without UA); the block is keyed on the **`client=` parameter + IP**, not the host (`gtx` 429 on both hosts; `client=at` 429 here but 200 from another IP; `translate_a/t?client=dict-chrome-ex` **200 from both networks**; mobile page `/m` 200). | `benchmark-fournisseurs.md` §3.1 | [MEASURED] — obtained outside the process, see incident |
| P2-4 | **Why it varies between users:** LIVE is 85–240 req/min vs 1 per Enter for a typist (4 orders of magnitude); **all four major French ISPs run CGNAT since January 2025** (Orange last), so dozens-to-hundreds of subscribers share one Google-visible address. | `mecanismes-de-blocage-google.md` Q4 (S15, S16) | [CONFIRMED] CGNAT, [INFERRED] link |
| P2-5 | `gtx` is alive in 2026 but undocumented, discouraged by a Google forum moderator (2023), and outside the Google APIs ToS §2(c); `translate.googleapis.com/robots.txt` disallows `/translate_a/`, `clients5.google.com/robots.txt` does not. 429 is contractually expected, not a bug. | `mecanismes…` Q1, Q5; `benchmark…` §2 | [CONFIRMED] |
| P2-6 | **Evidence-backed mitigations** (build): persisted cool-down / circuit breaker on 429 shared by both translator chains (state must survive restart — the owner's observation proves in-memory scope is wrong); an always-on client-side rate ceiling (~500 ms spacing is the ecosystem's converged value); honour `Retry-After` if ever present, else exponential backoff **with jitter**; persist the cache; log status/headers/body-snippet per request. **Folklore** (don't build): UA rotation, `client=` rotation as a *primary* strategy, JA3/HTTP-2 impersonation, proxies. Multi-`q=` stays a declined non-feature. | `mecanismes…` "Implications"; `analyse…` §6.3 seams | [CONFIRMED]/[REPORTED] |
| P2-7 | **Provider landscape (dated 2026-09-06):** DeepL's API Free/Pro are no longer purchasable (July 2026) — the current free plan is a one-time 1 M chars "Developer" allowance, so DeepL is no longer a viable free tier for new users; **Azure AI Translator F0 = 2 M chars/month free, permanent**, one key + region; Google Cloud v3 does not accept API keys (v2 Basic only); Yandex is best/cheapest for RU but effectively unavailable from the EU; LLM APIs are 20–150× cheaper per character (gpt-5-nano ≈ $0.74/month for a heavy user vs Azure $38, Google $106, DeepL $158) but slower. | `benchmark-fournisseurs.md` §2, §8 | [CONFIRMED] prices, dated |
| P2-8 | **Offline option is viable as a lazily-loaded last resort, not as a default:** `BergamotTranslatorSharp` (MPL-2.0, NuGet 0.5.1, 2026-07-30), `bergamot.dll` 21.4 MiB with no MKL/MSVC dependency; measured on this box: `tiny` ru→en init 103 ms, 6.5–12 ms/line; COMET-22 ru→en 0.8497 vs Google 0.8785; **RAM +127 MiB per model, ~250–310 MiB for a RU↔FR pivot, not tunable** — against a 150 MB working-set budget it must be loaded on first fallback and unloaded on idle. Raw slang is untranslatable offline (`данж`→"dangling"); after `SlangGlossary.Expand` it is fine → any local engine sits **downstream of the glossary**, like the cloud path today. | `benchmark-fournisseurs.md` §6, §10 | [MEASURED] on the dev box |
| P2-9 | Observability: `TranslationService` logs nothing; a 15-field per-request diagnostic line and its 5 insertion points are specified so the About-tab error report can evidence P2 from one paste (no user text). Prerequisite for testing any of it: an injectable `HttpMessageHandler` on `TranslationService` (the static client is not mockable today). | `analyse-implementation-actuelle.md` §5.2, §6.3–6.4 | [CONFIRMED] |

### Contradictions reconciled
- **Extraction payload**: 18 DLLs / 24.4 MB (research, pre-bundle tree) vs 5 DLLs / 7.8–8.2 MB (measured `%TEMP%`) → the measured figure stands; the research doc is corrected in place.
- **Phase 0 §13 item 18** ("failed lines are re-requested next tick") → wrong on the LIVE path: the dedup marks them emitted, so the row is *burned* for the session and never re-translated after recovery. The re-request effect is real only on manual retries and across restarts. Recorded as a Phase 2 requirement (retry-after-recovery).
- **Benchmark §2 recommends switching the default endpoint to `clients5.google.com/translate_a/t?client=dict-chrome-ex`** (200 from two networks where `gtx` is 429) with `edge.microsoft.com/translate/translatetext` as an independent second tier — a "~15-line change". The blocking-research doc lists endpoint alternation among *folklore* **as a primary strategy**. Orchestrator's reconciliation: an endpoint change is a legitimate **short-term relief** (it demonstrably works today) but it is rented, not owned — it is acceptable only **together with** the circuit breaker / rate ceiling / logging hardening, never instead of it. Whether to take it is the owner's call (see decisions).

### Decisions for the owner
1. **Google Burst probe** — now moot for the next hours: this address is already 429 on `gtx`, and Google says the block lifts after requests stop. Recommendation: **do not run anything from this connection**; if you want the threshold/duration numbers, run option A from a different address later, or capture a live user's `-Smoke`. The raw 429 (headers + body) is already on file.
2. **Endpoint switch (`client=dict-chrome-ex`)** as an immediate relief in the next release, paired with the hardening — yes/no?
3. **Code signing** — Azure Artifact Signing ($9.99/month, individual, EU) vs SignPath Foundation (free, needs MIT → decide PR #49) vs stay unsigned. This is the only lever on the 2–10 s pre-process cost besides expectation-setting.
4. **Offline fallback (Bergamot)** — accept the design constraint (lazy-load on first fallback, unload on idle, +130–310 MiB while active, ~22 MB DLL + ~30 MB/model download) as a Phase 2 candidate, or drop it?
5. **Azure F0 as a second user-key slot** alongside DeepL, given DeepL's free tier is gone for new users — yes/no?

### Phase 2 (on the owner's go)
Paige (WD/VD): `SYNTHESE.md` — per problem: top-3 causes with evidence, quick wins vs structural, effort, risks, decisions. Winston (CA): `02-traduction/architecture-cible.md` (provider abstraction + ordered fallback chain, typed error classification, shared circuit breaker with persisted state, rate ceiling, `Retry-After`/backoff+jitter, persistent shared cache, LIVE cadence back-off and honest auto-stop, retry-after-recovery for burned rows, diagnostic logging, testability seam) and `02-traduction/plan-migration.md`; Sally (CU): provider-status indicator, degraded-mode messages, key/provider settings UX; `01-demarrage/recommandations.md` finalised from the research + measurements.

### Owner's decisions (2026-09-06, Phase 2 go)
| # | Decision | Owner's choice |
|---|----------|----------------|
| 1 | Google Burst probe | Skipped (address already 429; nothing more sent from this connection). |
| 2 | Endpoint switch to `clients5.google.com/translate_a/t?client=dict-chrome-ex` | **Yes, coupled with the hardening** (persisted circuit breaker + rate ceiling + logging ship together). |
| 3 | Code signing route | **SignPath Foundation** (free, requires an OSI licence → the app stays **MIT**; PR #49 "CC BY-NC" is to be closed by the owner). |
| 4 | Bergamot offline fallback | **Yes, as a Phase 2 candidate with a measured prototype** before any commitment (lazy-load on first fallback, unload on idle, downstream of the slang glossary). |
| 5 | Azure AI Translator F0 as a second user-key slot | **Yes, alongside DeepL.** |

---

## Phase 2 — Closure by the orchestrator (Winston, 2026-09-06)

- **Architecture approved** by Winston as written: 16 invariants (I1–I16), the two architect's concerns resolved without redesign (`RequestPriority{Interactive,Background}` reserve so LIVE cannot starve the Translator tab; the cache entry records its producing provider so Bergamot output is dropped when the offline setting is off), ru/auto merge evaluated and rejected with arithmetic, per-line fallback capped at 8.
- **`SYNTHESE.md` validated** (Paige: 28 claims spot-checked, 23/23 links, six diagrams rendered). It is the entry point for the owner and any contributor; this README stays the index.
- **Pending owner questions carried to Phase 3** (from `architecture-cible.md` §15.3): **OQ-A** batching fallback if newlines are lost on `translate_a/t` (per-line ≈2× volume vs revisiting the declined multi-`q=`); **OQ-B** whether a paused LIVE loop keeps capturing/OCR-ing (architecture says no); **OQ-C** LLM write-path tier parked or revisited after increment 5; **OQ-D** close PR #49 (CC BY-NC) — prerequisite for SignPath.
- **Owner's personal to-do** (`SYNTHESE.md` §7.2): close PR #49 · apply to SignPath Foundation · run the three `tools/diagnostics/` scripts on one slow, Defender-only machine with a **downloaded** exe (before/after `Unblock-File`, portable vs MSI) · answer OQ-A–C · send nothing more to Google from this connection · say "go" for Phase 3.
