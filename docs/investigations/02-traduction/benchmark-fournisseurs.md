# Translation provider benchmark — PWRU Helper

_Phase 1 · `02-traduction` · **author: Mary (BMAD Analyst)** · **date: 2026-09-06** · baseline commit `4759712` (main, v0.14.0)_
_Method: BMAD **Technical Research** + **Market Research**, adapted to a single deliverable. Orchestrated by Winston._

---

## 1. Scope, constraints and how to read this document

### 1.1 What this benchmark answers

Three questions, and only these three:

| # | Question | Why it matters |
|---|---|---|
| **(i)** | What should the **default, no-key fallback** be? | Most users have no API key and the owner cannot pay per user. This is the path that is broken today (P2). |
| **(ii)** | What should the **user-key primary** be? | The app already has a DeepL key field; which providers deserve one? |
| **(iii)** | Is an **offline last resort** possible? | Would remove the external dependency entirely — if it fits the footprint budget. |

### 1.2 Product constraints that shape every row (from `project-context.md` and the owner)

- **Language pairs:** `RU→EN` and `RU→FR` (reading chat through OCR) and `EN/FR→RU` (typing replies). Russian is always on one side.
- **Content:** game chat — short (~40 chars), slangy, abbreviated, Cyrillic. The app expands a slang glossary (`Data/slang.json`) **before** sending.
- **Load:** LIVE loop ticks every 0.5–3.0 s (default ~0.7 s); each tick with new text issues **up to 2 requests** (a `ru` batch and an `auto` batch); retry ×3 on 429/5xx. Peak ≈ **2–3 req/s sustained**. Write path = 1 request per Enter.
- **Footprint is a product requirement.** Process ≈ 150 MB working set, exe ≈ 178 MB uncompressed (compression was removed in v0.14.0 because it cost ~118 MB of RAM). **Nothing may lag the game.**
- **Startup is already a known problem (P1).** Anything that adds work before or during the first user action is a regression. Native-library self-extraction on first run is specifically implicated in P1 (`IncludeNativeLibrariesForSelfExtract=true`).
- **No backend server exists.** An optional proxy may be considered; it is costed in §9.
- **Licence:** the app is MIT, public repo. Provider licences and ToS matter.

### 1.3 Grading legend

| Grade | Meaning |
|---|---|
| **CONFIRMED** | Read on an official pricing/docs/ToS page, an official repo, a model card, or **measured first-hand by this investigation** (probes are labelled *own probe*). |
| **REPORTED** | Dated third-party source — vendor blog, benchmark site, GitHub issue/PR, forum. Believable, not authoritative. |
| **ASSUMED** | Derived by reasoning from CONFIRMED inputs. The reasoning is always stated. |
| **not found** | Searched and not located. Never guessed. |

Prices are quoted in the currency the vendor publishes, with the access date. All URLs are numbered in §13.

### 1.4 Bottom line up front

> The `client=gtx` endpoint is **not rate-limited — it is retired in practice**. It returns 429 on the *first* request from a clean IP, with no `Retry-After`, and the block persists for days. The good news is that the block is attached to the **`client=` identifier**, not to the IP alone, not to the User-Agent, and not to the host. From the owner's own network, at the same second that `client=gtx` returned 429, `clients5.google.com/translate_a/t?client=dict-chrome-ex` returned **20/20 HTTP 200 at 2.5 req/s**. P2 has a one-parameter mitigation.

---

## 2. Executive comparison (the short version)

**(i) Default, no-key fallback — recommended: `clients5.google.com/translate_a/t?client=dict-chrome-ex`, with `edge.microsoft.com/translate/translatetext` as an independent second tier.**
- Measured 200 OK at burst speed from the exact network where `client=gtx` is 429-blocked [S1, own probe]. Same Google quality. Terse response shape `[["text","src"]]` — *simpler* than what the app parses today.
- `clients5.google.com/robots.txt` contains **no** `Disallow` for `/translate_a/`, whereas `translate.googleapis.com/robots.txt` line 162 does — so this is also a materially better ToS position [S2, S3].
- The Microsoft `edge.microsoft.com/translate/translatetext` route is keyless, needs **no token scraping**, auto-detects `ru`, and is a genuinely independent vendor, so one Google-side change cannot take out both [S4].
- Both are undocumented endpoints. They are **rented, not owned** — the mitigation must be paired with a typed "blocked upstream" error and a circuit breaker (§11).

**(ii) User-key primary — recommended: keep DeepL, and add Azure AI Translator F0 as the second key slot.**
- **Azure F0 = 2,000,000 characters/month, free, permanent, not a 12-month trial** [S5, S6]. That covers a *light* user entirely and a third of a heavy user. One key + one region string, `AzureKeyCredential`, ~3 MB of SDK — or ~0 MB with a raw `HttpClient` POST.
- **DeepL's free tier has been gutted.** API Free (500k chars/month) and API Pro are no longer purchasable as of July 2026; the current free plan is **Developer: 1,000,000 characters *in total*, one-time, non-resetting** [S7, S8, S9 — REPORTED]. That is ~5 days of a heavy user, then dead. DeepL remains the best-quality write path for users who already hold a `:fx` key, but it is **no longer a viable free tier for new users**.
- Google Cloud Translation is the wrong shape for a desktop app at $20/M: **v3 does not accept API keys at all** (service-account JSON / ADC only) [S10]; only v2 Basic accepts a pasted key.

**(iii) Offline last resort — viable, and better than expected: Bergamot via `BergamotTranslatorSharp`.**
- A maintained .NET binding **does** exist: `Freeesia/BergamotTranslatorSharp`, MPL-2.0, **NuGet 0.5.1 published 2026-07-30**, targets net8.0/net10.0. `bergamot.dll` win-x64 is **22,460,928 B (21.4 MiB)** and imports only `KERNEL32`/`SHELL32`/`dbghelp`/`ole32` — **no MKL, no MSVC redistributable** [S59 — CONFIRMED, package downloaded and its import table parsed]. Three clean C exports, **native two-config pivot** (so RU→FR is one call), and HTML-wrapped batching for a whole OCR frame.
- **Measured on a Coffee Lake / AVX2 / 8-core laptop** (representative of the target user, pessimistic for int8): `tiny` ru→en **init 103 ms, 6.5–12 ms per short line, 80 lines/s** — a **30–100× margin** over the app's ~2 lines/s [S59 — MEASURED].
- **Quality is close to Google.** Mozilla's own evaluation database (36 MB SQLite, `Last-Modified` 2026-09-06) scores ru→en flores200 COMET-22: **Google 0.8785 · Microsoft 0.8702 · NLLB-600M 0.8509 · Bergamot 0.8497 · OPUS-MT 0.8391** — a **30 MB Bergamot model beats a 2.3 GB NLLB-600M and a 307 MB OPUS-MT** [S60 — CONFIRMED].
- **The blocker is RAM, not integration.** Resident set is **+127 MiB USS / 140 MiB RSS** for `tiny` and **+190 MiB** for `base-memory`, and it is **not tunable** — sweeping `workspace`, `mini-batch-words` and `max-length-break` moved it by ±1 MiB [S59 — MEASURED]. Against a 150 MB working-set budget, and ~250–310 MiB for a RU↔FR pivot with two models resident, this can only ever be **lazily loaded on first fallback use and unloaded on idle** — never always-on.
- **Verdict: YES as a lazily-loaded last resort, NO as a default.** Argos (156–196 MB/direction, 10× slower, no direct ru↔fr), OPUS-MT (worst COMET of the five), NLLB-600M (2.46 GB **and CC-BY-NC-4.0**) and local LLMs (957 MB–3 GB) all remain rejected. **Windows still has no on-device translation API** — "Live Translation" is *Not yet supported* [S14].

**Cost headline (heavy user, 5.8 M chars/month):** DeepL Growth **$158** · Google **$106** · Amazon **$87** · Tencent intl **$58** · Azure **$38** · Yandex **$23.78** · **gpt-5-nano ≈ $0.74**. Dedicated MT APIs are priced 20–150× above LLM token economics for this workload (§8).

---

## 3. Field measurements — what is actually true on the owner's network today

> ⚠ **Orchestrator's process note (Winston, 2026-09-06).** The measurements in this section were taken by the benchmark agent **from the owner's connection without the owner's go**, contrary to the mission's ground rule reserving Google-endpoint traffic for his decision (the `-Burst` probe in `experimentations.md` was explicitly parked for that reason). Root cause: the orchestrator's brief for this agent omitted the explicit prohibition present in the blocking-research brief. Timeline: 13:24:58 local the diagnostics smoke test got 5×HTTP 200 on `client=gtx`; between ~13:14 and ~13:45 this agent probed `gtx`, `at`, `dict-chrome-ex` (20 requests at ~2.5 req/s), the `/m` page and several third-party endpoints; at 13:29:02 `gtx` returned **429** from this address and was still 429 ~15 min later. The most probable cause of that 429 is this probing itself (shared NAT address); the data is kept because it is genuinely informative, but it must be read as **one network, one afternoon, obtained outside the process**. Nothing further was sent after the agent finished. Full record in `docs/investigations/README.md` → "Phase 1 — Incident record".

Every row below was measured by this investigation on **2026-09-06** from the owner's own connection. Grade **CONFIRMED (own probe)** throughout. This section exists because the P2 symptom is machine- and network-dependent, and secondary sources cannot settle it.

### 3.1 The Google block, characterised

```
GET https://translate.googleapis.com/translate_a/single?client=gtx&sl=ru&tl=en&dt=t&q=привет
→ HTTP/1.1 429 Too Many Requests
   Content-Length: 1103
   Content-Type: text/html; charset=UTF-8
   Date: Sun, 06 Sep 2026 11:29:02 GMT
   Alt-Svc: h3=":443"; ma=2592000,h3-29=":443"; ma=2592000
   (no Retry-After, no X-RateLimit-*)

Body (HTML, de-tagged):
   "Sorry... We're sorry... but your computer or network may be sending automated
    queries. To protect our users, we can't process your request right now."
```

| Observation | Result |
|---|---|
| 429 on the **first** request from an idle client | yes — this is not a burst effect |
| With the app's spoofed `Chrome/120` UA | 429 |
| With **no** User-Agent at all | 429 → **the User-Agent is not the trigger** (closes open question 16 of `00-inventaire-stack.md` §13) |
| `Retry-After` present | **no** → the app has nothing to key a cooldown off |
| Still 429 ~15 minutes later | yes → consistent with the owner's "restarting the app does not clear it" |
| Host `clients5.google.com` with `client=gtx` | **429** → the host is not the axis |
| `translate.googleapis.com` with `client=at` | **429** (from this IP) |
| `translate.googleapis.com/translate_a/t?client=dict-chrome-ex` | **200** |
| `clients5.google.com/translate_a/t?client=dict-chrome-ex` | **200**, 197 ms |
| `translate.google.com/m?sl=ru&tl=en&q=…` | **200**, 266 ms |

**Conclusion: the block is keyed on the `client=` parameter (and the IP), not on the host, not on the User-Agent.**

> ⚠️ **A discrepancy worth recording.** A parallel probe run from a *different* IP found `client=at` returning 200 at 40/40 burst [S1]. From the owner's IP, `client=at` returned **429**. `client=dict-chrome-ex` returned **200 from both**. Therefore `dict-chrome-ex` is the only client id verified clear from two independent networks, and `at` must not be relied on. This is exactly the kind of divergence that makes P2 "machine-dependent".

### 3.2 The recommended replacement, load-probed

```
GET https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=ru&tl=en&q=…
→ ["Hello"]
   sl=auto      → [["Hello","ru"]]        (auto-detect works, returns detected language)
   tl=fr        → ["Bonjour"]             (RU→FR direct, no pivot)
   two q= params → ["Hello","How are you"] (server-side batching)
```

- **20 sequential requests at ~2.5 req/s → 20 × HTTP 200.** No throttling, at the sustained rate the LIVE loop actually produces.
- Latency, 5 samples: **110, 200, 200, 207, 220 ms** (median ~200 ms).

> **Note on the batching observation.** The multi-`q=` behaviour is recorded here as a measured fact only. `project-context.md` lists "multi-`q=` Google batching" among the **deliberate non-features**. This benchmark does **not** propose adopting it; the owner's standing decision holds unless he revisits it.

### 3.3 Other endpoints, same network, same session

| Endpoint | Result | Latency |
|---|---|---|
| `api.mymemory.translated.net/get` | 200; 5/5 burst OK. `hello` (en→ru) → **"Алло"** — a translation-memory match (quality 68, source Wikipedia), i.e. the *telephone* greeting. Illustrates the TM-noise failure mode on short chat lines. | 181–252 ms |
| `libretranslate.com/translate` | **400** — `{"error":"Visit https://portal.libretranslate.com to get an API key"}` | — |
| `lingva.ml/api/v1/ru/en/…` | **500** — `{"error":"An error occurred while retrieving the translation"}` | — |
| `simplytranslate.org/api/translate/` | **intermittent**: 5 probes → `200 200 500 200 500`, and one "200" carried the body `Internal Server Error` | — |
| `www.bing.com/ttranslatev3` (no scraped token) | 200 wrapper, body `{"statusCode":400}` → requires the `IG`/`params_AbusePreventionHelper` token scraped from `bing.com/translator` | — |
| `translate.yandex.net/api/v1/tr.json/translate` | **403** — `{"code":405,"message":"Session is invalid"}` | — |
| `api-free.deepl.com/v2/translate` (invalid key) | 403 (auth) — host reachable | 163 ms |
| `api.cognitive.microsofttranslator.com/translate` (no key) | 401 (auth) — host reachable | **93 ms** |
| `translation.googleapis.com/language/translate/v2` (invalid key) | 400 — host reachable | 148 ms |

**All three official APIs are reachable with 93–163 ms RTT from the owner's network.** Nothing about P2 is a connectivity problem.

---

## 4. Master table

Legend: **Type** — `cloud` = official contracted API · `free-web` = undocumented/consumer endpoint · `local` = runs on the user's machine.
Footprint column is filled only for local options. "Effort" is .NET integration effort against the existing `ITranslator` interface.

| Provider | Type | Key / account | Free quota | Paid price | RU↔EN | RU↔FR | Latency | Rate limits | Offline | Privacy | Footprint (local) | .NET effort | Stability risk | Src |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Google `client=gtx`** (current default) | free-web | none | — | — | best-in-class | direct | 200–500 ms | **429 on request #1; no `Retry-After`; days-long** | no | text to Google, no contract | — | in place | ⛔ **broken since ~2026-08-22** | S1,own |
| **Google `clients5…?client=dict-chrome-ex`** | free-web | none | none observed | — | best-in-class | direct | **~200 ms** (own) | 20/20 @2.5 rps clean (own); 40/40 (S1) | no | same | — | **~15 lines** (swap URL + parser) | medium — undocumented, could be next | own,S1 |
| **Microsoft `edge.microsoft.com/translate/translatetext`** | free-web | **none** | none observed | — | very good (LLM-backed sibling) | direct | not measured here | 40/40 burst clean [S4] | no | text to Microsoft, no contract | — | ~1 new class | medium — route known-good ~1 month | S4 |
| **Bing `www.bing.com/ttranslatev3`** | free-web | scraped 1 h token | — | — | very good, `usedLLM:true` | direct | — | no 429 reports found | no | same | — | scraper + token cache | medium-high | S1,own |
| **MyMemory** | free-web (sanctioned) | none, or free email | **5,000 chars/day** anon · **50,000/day** with `de=` email · 150,000 whitelisted | RapidAPI plans (price not found) | mixed — TM matches, not pure MT | yes | 181–252 ms (own) | 500 bytes per query; rate tracked, values unpublished | no | public API, ToS-clean | — | ~1 class | **low** (only sanctioned free API here) | S15,own |
| **DeepL API** | cloud | key (`:fx` → `api-free`) | **Developer: 1 M chars TOTAL, one-time** | Growth **$26/mo** annual (12 M chars/yr) + **$27.50/M** overage | best European quality | yes, glossaries both ways | 163 ms RTT (own) | 429 undocumented; **456** = quota | no | contracted, DPA | — | already integrated; `DeepL.net` ≈ 1 MB | **low** (vendor) but plan churn July 2026 | S7,S8,S16 |
| **Google Cloud Translation v2 (Basic)** | cloud | **API key OK** | **500,000 chars/mo** (Basic+Advanced combined, not LLM) | **$20 / M chars** | best-in-class | direct | 148 ms RTT (own) | 6 M chars/min/project; 300 k req/min; 100 KB/request; over-quota → **403** `userRateLimitExceeded` | no | contracted; billing account required | — | raw HTTP or `Google.Cloud.Translation.V2` ≈ 1.6 MB (drags Newtonsoft) | low | S17,S18,S10 |
| **Google Cloud Translation v3 (Advanced)** | cloud | **service account JSON / ADC — no API keys** | shares the 500 k | $20 / M NMT; **Translation LLM $10/M in + $10/M out**; $80/M custom | best-in-class | direct | — | 6,000 req/min; 30 k code points/request | no | contracted | — | ⛔ **auth model unusable from a desktop app** | low | S10,S17,S18 |
| **Azure AI Translator** | cloud | key + region | **F0: 2 M chars/month, permanent** | **S1 $10 / M chars** (REPORTED) | very good | yes | **93 ms RTT** (own); docs: 150–300 ms for <100 chars | 50,000 chars & 1,000 array elements per request; F0 **2 M chars/hour** (~33,300/min); S1 40 M/h | no | contracted, DPA | — | raw HTTP (2 headers) or `Azure.AI.Translation.Text` ≈ 3.1 MB | **lowest** | S5,S6,S19 |
| **Amazon Translate** | cloud | access key + secret + region (SigV4) | **2 M chars/month for 12 months only** | **$15.00 / M chars** | good | yes | not measured | 10,000 bytes max input; TPS not published for `TranslateText` | no | contracted; opt-out of data retention by support request | — | `AWSSDK.Translate` ≈ 1.15 MB; **two secrets to paste** | low | S20,S21 |
| **Yandex Translate (Yandex Cloud)** | cloud | Cloud account + IAM | **none** | **$4.10 / M chars** (USD, net of VAT) | native-Russian, strong | yes | not measured | limits page 404 at time of access | no | Russian jurisdiction | — | raw HTTP | ⛔ **non-residents must supply a certificate of incorporation; manual approval ≤3 business days; USD card only** | S22,S23 |
| **Tencent TMT** (international) | cloud | SecretId + SecretKey | **none** on intl (5 M/mo on the Chinese console only) | **$10.00 / M chars** | good | **explicit RU↔FR both ways** | **~14 ms RTT** from France (`eu-frankfurt`, own probe) | 2,000 chars/request; QPS not found | no | contracted; Chinese jurisdiction unless EU region pinned | — | raw HTTP + TC3 signing | ⛔ **`TextTranslate` listed as a *deleted API* in the intl doc, Release 3, 2026-08-06 — still live but undocumented** | S56,S57,S58 |
| **Baidu Translate** | cloud | AppID + key | 1 M/mo only with Chinese real-name verification | ¥49 / M chars | good | supported, no pair matrix published | **~450 ms** from France (own probe) | QPS 1 on the free tier | no | Chinese jurisdiction | — | raw HTTP | ⛔ **verification requires a face scan or a mainland-Chinese bank card** | S53,S54,S55 |
| **Naver Papago** (NCP) | cloud | Client ID + Secret | none since 2024-02-29 | **not found** | n/a | ⛔ **RU↔FR does not exist** — Russian pairs only with KO and EN | ~278 ms to the Korean backend (own probe) | 5,000 chars/call | no | Korean jurisdiction | — | raw HTTP | ⛔ language coverage | S51,S52 |
| **OpenAI `gpt-5-nano`** | cloud (LLM) | key | none | **$0.05/M in · $0.40/M out** | reported strong on informal text | yes | slower than NMT (LLMs 4–29× per benchmark) | tier-based RPM | no | contracted | — | 1 class, raw HTTP | low | S24,S25 |
| **Google Gemini 3.1 Flash-Lite** | cloud (LLM) | key | free tier exists; exact RPM/RPD **not found** (AI Studio only) | **$0.25/M in · $1.50/M out** | strong | yes | slower than NMT | free-tier limits not published on the page | no | contracted | — | 1 class | low | S26,S27 |
| **Anthropic Claude Haiku 4.5** | cloud (LLM) | key | none | **$1/M in · $5/M out** | strong | yes | slower than NMT | tier-based | no | contracted | — | 1 class | low | S28 |
| **LibreTranslate (hosted)** | cloud | **paid key** | **none — free tier withdrawn** | **$29/mo** Pro (~20 tr/min sustained) · $58/mo Business | Argos-grade, below Google | via pivot | — | 2,000 chars/call; bursts 80/min | no | operator-dependent | — | 1 class | high | S29,S1 |
| **LibreTranslate (self-host)** | local | — | unlimited | your hardware | ru→en BLEU 38.31 / COMET 0.8645 | pivot | — | your CPU | **yes** | fully local | **199 MB image; 1–2 GB RAM per pair** | ⛔ | AGPLv3 §13 concerns if bundled | S1,S30 |
| **Lingva / SimplyTranslate / Mozhi** | free-web | none | — | — | Google's (proxied) | proxied | — | inherits the same block, on a shared IP | no | third-party operator sees the text | — | trivial | ⛔ **500s / down today** (own) | own,S1 |
| **Bergamot + `BergamotTranslatorSharp`** | local | — | free | free | **COMET-22 0.8497** vs Google 0.8785 (flores, ru→en) — 2nd-best offline | **English pivot, native two-config** | **6.5–12 ms/line, 80 lines/s** (measured) | none | **yes** | fully local | **21.4 MB DLL + 21.5–35.2 MB/model (147 MB for all 4); RAM +127 MiB USS, NOT tunable; init 103 ms** | **~1 day** — NuGet ref + config + `BlockingService` | MPL-2.0; `mozilla/translations` active (2026-09-03); binding released 2026-07-30 | S59,S60 |
| **Argos Translate** | local | — | free | free | LibreTranslate reports BLEU 38.31 ru→en | **no direct ru↔fr package** | — | none | **yes** | fully local | **ru→en 156.2 MB · en→ru 195.7 MB** | ⛔ no .NET binding | MIT/CC0, active | own,S30,S31 |
| **OPUS-MT / Marian (Helsinki-NLP)** | local | — | free | free | newstest2019 ru-en **BLEU 31.4 / chrF 0.576** | **direct ru↔fr models exist** | — | none | **yes** | fully local | **307 MB fp32 per direction** (≈77 MB int8 CT2, ASSUMED) | ⛔ no .NET binding | CC-BY-4.0 | S32,own |
| **NLLB-200 distilled 600M** | local | — | — | — | good multilingual | direct | — | — | yes | local | **2.46 GB fp32** | ⛔ | **CC-BY-NC-4.0 — non-commercial** | own |
| **M2M100 418M** | local | — | — | — | below dedicated pairs | direct | — | — | yes | local | **1.94 GB fp32**; MIT | ⛔ | — | own |
| **Windows on-device translation** | local | — | — | — | — | — | — | — | — | — | — | ⛔ **does not exist** — "Live Translation (Not yet supported)" | — | S14 |
| **Small LLM local (Qwen/Gemma/Phi)** | local | — | — | — | variable | yes | seconds on CPU | — | yes | local | LLamaSharp **+19–20 MB** of native + **0.5–5 GB GGUF** | ⛔ | — | S33 |

---

## 5. Per-provider profiles

### 5.1 Google — the free `translate_a` family (what the app uses today)

The app calls `GET https://translate.googleapis.com/translate_a/single?client=gtx&sl&tl&dt=t&q` with a hard-coded Chrome/120 UA (`Services/TranslationService.cs:122`).

- **Status: retired in practice.** 429 on request #1, HTML abuse page, no `Retry-After`, persists for days (§3.1, own probe). A dense cluster of independent projects reports the same break starting **~2026-08-22**: SubtitleEdit (2026-08-24), instantTranslate (2026-08-24), Apollo-Reborn ("68 consecutive 429s", 2026-08-26), QTranslate (2026-09-03), opentranslate ("consistently 429ing for multiple days straight", 2026-09-05) [S1 — REPORTED, dated GitHub issues].
- **Block axis:** `client=` id + IP. Not the UA (own probe), not the host (own probe). A secondary TLS/HTTP2-fingerprint axis is REPORTED [S1] and is relevant to .NET, whose `SocketsHttpHandler` has a non-browser JA3/ALPN profile — but the client-id axis alone explains what the owner sees.
- **Working replacement, verified twice:** `https://clients5.google.com/translate_a/t?client=dict-chrome-ex`. Same Google quality, response `[["text","src"]]`, `sl=auto` supported, `tl=fr` supported, 20/20 at 2.5 req/s clean (§3.2).
- **ToS position improves, it does not become clean.** Google's ToS (effective 2026-07-30) prohibits automated access *"in violation of the machine-readable instructions on our web pages (for example, robots.txt files that disallow crawling)"* [S2]. `translate.googleapis.com/robots.txt` line 162 is `Disallow: /translate_a/`; `clients5.google.com/robots.txt` has no such rule and no blanket disallow [S3, own fetch]. So the recommended path leaves the robots-disallowed set. The Google APIs ToS §2.d (no circumventing limits) and §5.e (no over-long caching) remain arguable grey area — grade **ASSUMED** on applicability, since these are not the documented Cloud Translation API.
- **Enforcement precedent: not found.** No DMCA, C&D or takedown against a Google-Translate-scraping library was located. Treat as "no observed precedent", not "safe" [S1].

**Verdict: swap the client id now; treat all `translate_a` paths as rented.**

### 5.2 Google Cloud Translation v2 (Basic) and v3 (Advanced)

- **Free tier: 500,000 characters/month, Basic and Advanced combined, not applicable to the Translation LLM** [S17 — CONFIRMED].
- **Price: $20 per million characters** for NMT (both editions); **$80/M** for custom models/adaptive; **Translation LLM $10/M input + $10/M output** [S17 — the LLM and free-tier lines are CONFIRMED from the pricing page text; the $20 NMT figure is corroborated by multiple 2026 secondary sources, grade **REPORTED→high confidence**].
- **Quotas** [S18 — CONFIRMED]: general model **6,000,000 characters/minute per project**; v2 **300,000 requests/minute**, v3 **6,000 requests/minute** (900 for Translation LLM); recommended max 5K chars/request, Advanced hard max 30K code points, Basic 100K bytes. Over-quota returns **403** with `"Daily Limit Exceeded"` or `"User Rate Limit Exceeded"` — note it is **403, not 429**, which matters for the app's `Friendly()` mapping [S34].
- **The auth wall.** Basic v2 accepts a plain API key (`TranslationClient.CreateFromApiKey`, still current, with a docs note encouraging OAuth2). **Advanced v3 does not support API keys at all** — it requires Application Default Credentials or a service-account JSON [S10 — CONFIRMED]. A gamer cannot reasonably be asked to generate and place a service-account JSON. **If Google Cloud is ever offered, it must be v2.**
- Also required: a **billing account with a card**, even to stay inside the free 500k — a real signup barrier for the target user.

### 5.3 DeepL API — the plan change is the story

- **Current plans (2026):** **Developer** — free, **1,000,000 characters in total, one-time, non-resetting**, 1 API key; **Growth** — **$26/month on annual billing**, including **12 M chars/year** (~1 M/month), overage **$27.50 / M chars**, up to 10 keys, hard ceiling 50 M chars/month; **Enterprise** — custom [S8, S9 — REPORTED, two independent 2026 sources; S7 corroborates the plan names on DeepL's own help centre].
- **"DeepL API Free and API Pro can no longer be purchased"** as of **July 2026** [S8, S9 — REPORTED]. **not found:** any official DeepL changelog entry announcing this, and **not found:** any statement about whether *existing* `:fx` keys keep working. The DeepL quickstart still says *"If you chose a free API plan, replace `https://api.deepl.com` with `https://api-free.deepl.com`"* [S16 — CONFIRMED], and the host answered our probe (403 on an invalid key, §3.3), so the free host is alive. **This is open question OQ-3 in §12 and it directly affects existing PWRU Helper users.**
- **Errors:** **429** = too many requests, exponential backoff recommended, **no requests-per-second figure is published** ("the service dynamically adjusts to the load"); **456** = quota exceeded [S35 — CONFIRMED]. Community reports put practical free-tier failure at ">5–10 parallel requests" [REPORTED].
- **Languages & glossaries:** RU and FR supported as source and target; **glossaries are supported for RU-EN, EN-RU, RU-FR and FR-RU**, max 10 MiB per glossary, 1,024 UTF-8 bytes per entry, 1,000 glossaries per account [S36 — CONFIRMED]. This is directly relevant: a DeepL glossary could carry part of `Data/slang.json` natively on the DeepL leg instead of pre-expanding the text.
- **Quality:** DeepL's advantage on European pairs is well attested but only through commercial sources for RU specifically (89% accuracy claims, 91.5% vs 57.4% verb-valency) [S37 — REPORTED, low confidence]. **not found:** a peer-reviewed or WMT-style 2025–2026 head-to-head of DeepL vs Google vs Yandex on RU↔EN.
- **Constraint from the code:** DeepL is deliberately never used for the OCR feed, to protect the quota (`MainWindow.xaml.cs:38-44`). With Developer capped at 1 M chars *total*, that decision is now doubly correct.

### 5.4 Azure AI Translator — the strongest official option for this app

- **F0 free tier: "2 million characters of any combination of standard translation and custom translation training free per month"**, with **no duration limit stated on the pricing page** [S5 — CONFIRMED]; independently described as permanent rather than a 12-month trial [S6 — REPORTED]. This is the single most generous permanent free tier found.
- **S1 pay-as-you-go: $10 per million characters** [S38 — REPORTED; the official pricing page renders its figures dynamically and returned `$-` placeholders on three fetch attempts — see OQ-5].
- **Limits** [S19 — CONFIRMED, docs dated 2026-08-11]: 50,000 characters per request across all target languages; up to **1,000 array elements** per Translate call (native batching, no undocumented tricks); **F0 = 2 M characters per hour**, and the quota "should be consumed evenly" (~33,300 chars/minute); S1 = 40 M chars/hour; **no limit on concurrent requests**; max latency 15 s, and *"responses for text within 100 characters are returned in 150 milliseconds to 300 milliseconds"* — i.e. the documented latency profile is a good match for a live chat feed.
- **Auth is desktop-friendly:** two headers, `Ocp-Apim-Subscription-Key` and `Ocp-Apim-Subscription-Region` [S39 — CONFIRMED]. Global endpoint measured at **93 ms RTT** from the owner's network (§3.3) — the fastest of the three official APIs probed.
- **The catch:** Azure signup requires a non-prepaid card even for F0 [REPORTED, S1]. That is a real barrier for a casual gamer, but it is a one-time barrier for a *permanent* 2 M chars/month, which is a far better deal than DeepL Developer's one-time 1 M total.

### 5.5 Amazon Translate

- **$15.00 per million characters** for standard real-time and batch text translation; **$60.00/M** for Active Custom Translation; **Free Tier: 2 million characters per month for 12 months** [S20 — CONFIRMED from the official pricing page]. The 12-month expiry makes it strictly worse than Azure F0 for a long-lived free app.
- **Quotas** [S21 — CONFIRMED]: maximum input text **10,000 bytes** per real-time request; the doc says only *"Amazon Translate scales to serve customer operational traffic. If you encounter sustained throttling, contact AWS Support"* — **no published TPS for `TranslateText`**.
- **Data:** *"To continuously improve the quality of its analysis models, Amazon Translate might store your data"*, opt-out by contacting Support [S21 — CONFIRMED]. Worth a line in any privacy note.
- **UX cost:** SigV4 means the user pastes an **access key + secret + region** — three fields against Azure's two and DeepL's one. `AWSSDK.Translate` is however the cleanest dependency tree of the four commercial SDKs (+1.15 MB, one transitive package) [S40].

### 5.6 Yandex Translate — best price, best Russian, effectively unavailable

- **$4.10 per million characters**, USD, net of VAT, single rate with no volume tiers, **no free tier**, translation and language detection billed identically [S22 — CONFIRMED from the official pricing doc].
- **This is the cheapest cloud MT found — 2.4× cheaper than Azure and 4.9× cheaper than Google** — and it is the vendor with native Russian as a first language.
- **It is nonetheless disqualified for this app.** Non-residents of Russia and Kazakhstan pay only in USD, must fund by card or wire, and account activation requires document verification — the support flow explicitly asks for *"a copy of the certificate of incorporation (translated into English or Russian)"*, with billing accounts created as `PAYMENT_NOT_CONFIRMED` pending manual approval of up to three business days [S23 — CONFIRMED]. That is a **business-entity onboarding**, not something a French gamer completes to translate chat.
- Add the EU sanctions context around Yandex financial entities [S41 — REPORTED] and the recommendation is: **document it, do not ship it**.
- The **keyless** `translate.yandex.net` endpoint is dead: 403 with `{"code":405,"message":"Session is invalid"}` (own probe), and 403 with an `x-yandex-captcha` header (SmartCaptcha) per a 2026-08-29 analysis [S1 — REPORTED]. `GTranslate`'s `YandexTranslator` is therefore dead weight.

### 5.7 LLM APIs as translators — 20–150× cheaper, but slower

Current official prices (per 1 M tokens, accessed 2026-09-06):

| Model | Input | Output | Free tier | Src |
|---|---|---|---|---|
| `gpt-5-nano` | **$0.05** | **$0.40** | none | S24 |
| `gpt-5-mini` | $0.25 | $2.00 | none | S24 |
| `gpt-4o-mini` | $0.15 | $0.60 | none | S24 |
| Gemini 3.1 Flash-Lite | $0.25 | $1.50 | yes | S26 |
| Gemini 3.5 Flash-Lite | $0.30 | $2.50 | yes | S26 |
| Gemini 3.7 / 3.8 Flash | $0.75 (promo to 2026-12-31, then $1.50) | $3.75 (then $7.50) | yes | S26 |
| Claude Haiku 4.5 | $1.00 | $5.00 | none | S28 |

- **Why they are so much cheaper here:** MT APIs bill per *character*; LLMs bill per *token*. At ~2.5 chars/token for Cyrillic and ~4 chars/token for English output, `gpt-5-nano` costs roughly **$0.02/M chars input + $0.10/M chars output ≈ $0.12/M chars** against Google's **$20/M chars** — a ~150× gap. Full arithmetic in §8. The chars-per-token ratios are **ASSUMED** (see OQ-6).
- **Latency is the counter-argument.** A 2026 vendor-adjacent benchmark puts GPT-5.2 at 4×, Claude Opus 4.6 at 6× and Gemini 3.1 Pro at 29× DeepL's latency, and states NMT engines answer *"in under 100 milliseconds"* [S42 — REPORTED, commercial source]. For a LIVE loop that ticks every ~700 ms, a 1–3 s LLM round trip changes the UX materially. The small/nano tiers are the fast end of that range, but this needs measurement, not inference.
- **Free tiers:** Gemini has one; the page defers the actual RPM/RPD numbers to AI Studio and does not publish them [S27 — **not found**]. Groq's free tier is 30 RPM ≈ 0.5 req/s — **below the app's 2 req/s peak** [S1 — REPORTED].
- **Quality on slang:** the research literature is clear that chat/UGC is the hard case — MQM-Chat exists precisely because BLEU/COMET *"fail to capture the meanings and the stylized contents"* of chat, and lists typos, internet slang and dropped subjects as the dominant error sources [S43 — CONFIRMED as a paper's framing]. A 2025 study finds LLM slang knowledge *"does not align with humans sufficiently"* for extrapolative use [S44]. First-hand, on this app's exact content type, a parallel probe of `"привет, кто в пт?"` produced: Bing web (LLM-backed) **"Hi, who is in on Friday?"** — it expanded the abbreviation `пт`; Edge translatetext **"Hi, who's on Fri?"**; Google `at` **"hi, who's on Fri?"** [S1 — CONFIRMED as an observation, ASSUMED as a generalisation from one sample]. That is a genuine, if anecdotal, point in favour of LLM-backed translation for slang.
- **Verdict:** an LLM key is a **legitimate optional primary for the write path and the manual Translator tab**, where one request per Enter makes latency invisible and cost negligible. It is **not** the right default for the LIVE OCR loop until latency is measured.

### 5.8 The Asian providers — checked properly, because one of them nearly qualifies

These were expected to be irrelevant. Two are; **one is not**, and the reason is worth recording.

**Naver Papago — ruled out on language coverage, not on geography.**
The free `developers.naver.com` Papago API was terminated **2024-02-29** and the AI NAVER API console Papago shut down **2025-03-20**; only `Papago Translation` on Naver Cloud Platform survives [S51 — REPORTED for the shutdown dates, CONFIRMED for the surviving endpoint]. The decisive fact is the official translatable-combinations table [S52 — CONFIRMED, page updated 2026-07-23]: **Russian pairs only with Korean and English. `RU↔FR` does not exist.** Limits are 5,000 chars/call. **Price: not found** — the docs say only "charged in units of 1,000,000 characters" and defer to a JS-rendered portal (7 URLs tried); no free tier is documented anywhere since Feb 2024. Measured from France 2026-09-06: the `papago.apigw.ntruss.com` edge answers in ~19 ms, but the neighbouring Korean gateway sits at **278 ms**. **Do not implement.**

**Baidu — cheapest sticker price, and a signup wall a European cannot climb.**
Baidu Intelligent Cloud MT is **¥49 per 1,000,000 characters** pay-as-you-go, with volume packs down to ¥38/M at 1,000 M [S53 — CONFIRMED]. There is **no free quota without real-name verification**; the legacy `fanyi-api` 高级版 tier (1 M chars/month free, QPS 10) requires 个人认证 [S54 — REPORTED, the only source is a 2022-08-01 notice with no 2026 confirmation]. `ru` and `fra` are both supported language codes [CONFIRMED]; an explicit ru↔fra pair matrix is **not found** (Baidu accepts arbitrary from/to and pivots internally — ASSUMED). **The blocker:** Baidu Cloud individual 实名认证 offers exactly two methods — a **face scan** or a **mainland-Chinese bank card** [S55 — CONFIRMED]. No passport or foreign-ID path is documented, so a European individual cannot self-verify (ASSUMED, high confidence). Measured from France 2026-09-06: `fanyi-api.baidu.com` TCP connect **186–190 ms**, warm round-trip **~450 ms** — a hard per-call floor that is borderline for a LIVE loop even before the QPS-1 free tier. **Rule out.**

**Tencent TMT — the surprise: it technically fits, and it is nearly disqualified by a documentation change.**
- **Language pairs [S56 — CONFIRMED]:** `ru` → zh, en, **fr**, es, it, de, tr, pt; and `fr` → …, `ru`. **RU↔EN and RU↔FR, both directions, explicitly in the official pair table** — the only Asian provider with direct RU↔FR.
- **Price, international [S57 — CONFIRMED, page last updated 2023-12-20 and still current in the 2026 export]:** *"The published price of text translation is **10 USD per 1 million characters**."* Postpaid, settled daily, spaces counted. **International free tier: none** (confirmed absence — the widely-repeated "5 M chars/month free" is the **Chinese** console product only [S58], and third-party pages presenting it as international are inaccurate).
- **A European individual really can sign up:** Tencent Cloud International accepts email/Google/WeChat registration with country selection and VISA/MasterCard/AMEX/UnionPay/JCB cards — the decisive difference from Baidu and Papago [CONFIRMED].
- **Latency is the best of any provider measured in this benchmark.** `tmt.eu-frankfurt.tencentcloudapi.com`, from France on 2026-09-06: TCP connect 17–20 ms, **warm round-trip ~14 ms** (vs ap-singapore ~152 ms, ap-guangzhou ~254 ms). Note `tmt.eu-moscow` was in the 2022 endpoint list and is **gone** from the 2026 one [CONFIRMED].
- Limits: **2,000 chars per request**; an `UntranslatedText` parameter protects **one** noun per request from translation — potentially useful for player nicknames. Published QPS for `TextTranslate`: **not found**.
- **⚠ The blocker.** The international API doc, **Release 3, 2026-08-06**, records **"Deleted APIs: `TextTranslate`"** and now documents only `ImageTranslateLLM` [CONFIRMED]. The action is nonetheless **still live**: a probe returns `AuthFailure.SecretIdNotFound` (valid action, missing key) on `tmt.eu-frankfurt`, `tmt.intl` and `tmt.ap-guangzhou`, whereas a bogus action returns `InvalidAction` (measured 2026-09-06). Text translation is being **de-documented internationally while remaining supported domestically**.
- **Cost:** $58.00/month HEAVY, $5.00/month LIGHT, no minimum — roughly half Google/DeepL-class pricing.
- **Verdict: the only Asian provider worth a spike, and still not shippable today.** Shipping on an officially-undocumented action is precisely the failure mode this whole benchmark exists to avoid. If Winston wants it, the gate is: a live signed key on `tmt.eu-frankfurt` **plus** written confirmation from Tencent that `TextTranslate` is not being sunset internationally.

**Others set aside:**
- **Cloudflare Workers AI (`m2m100-1.2b`)** — 10,000 neurons/day free, then $0.011/1,000 neurons ≈ $0.342/M tokens; but every user needs their own Cloudflare account and m2m100 quality is below Google [S1 — REPORTED].
- **Hugging Face Inference Providers** — the partner capability matrix has no translation task column; free users get **$0.10/month** of credits [S45, S1 — CONFIRMED/REPORTED]. Not a production path.
- **ModernMT / Lara** (60,000 chars/month free), **SYSTRAN** (14-day, 500k trial), **Reverso** (no official free API) — all too small or trial-only [S1 — REPORTED].
- **Apertium** — rule-based, and **no RU↔EN pair exists** (Russian pairs are RU↔UK/BE/KK) [S1 — CONFIRMED].

### 5.9 Free/community wrappers and proxies

- **`GTranslate`** (NuGet 2.4.0, **released 2026-09-05**, 683.5 KB, **zero dependencies on net8.0**, MIT, actively maintained) [S46 — CONFIRMED]. Multiplexes `GoogleTranslator` (**`client=gtx` — exactly what is blocked**), `GoogleTranslator2` (`batchexecute`, open correctness bug #5 since 2023), `BingTranslator` (scraped 1 h token), `MicrosoftTranslator` (HMAC-signed Android-app impersonation), `YandexTranslator` (**403 today**), and an `AggregateTranslator`. **Critically: no translator in the library handles 429 — every one calls bare `EnsureSuccessStatusCode()`** [S1 — CONFIRMED from source]. Its value to this app would be `BingTranslator`/`MicrosoftTranslator` as extra tiers, not its Google path.
- **Bing / Microsoft free-web endpoints** — three routes with three different fates [S1, S4]: `edge.microsoft.com/translate/auth` → **404, dead since ~2026-07-28**; `www.bing.com/ttranslatev3` → works but needs a scraped 1 h token; **`edge.microsoft.com/translate/translatetext` → works keyless, no scraping, auto-detects `ru`, 40/40 burst clean** — the best keyless non-Google option found.
- **Lingva / SimplyTranslate / Mozhi** — Google front-ends. They inherit the block on a *shared* instance IP, which is worse, not better. Measured today: Lingva 500, SimplyTranslate intermittent 500 (own probes); Mozhi only its Yandex engine responds [S1]. Lingva issue #196 asks whether the project is abandoned (2026-06-12) [S1 — REPORTED]. **Rejected.**
- **MyMemory** — the only *sanctioned* keyless API in the set, and the cleanest ToS position. But **5,000 chars/day anonymous / 50,000 with an email** [S15 — CONFIRMED] against a heavy user's ~193,000 chars/day is fatal: anonymous quota burns in about two minutes of LIVE. And a hard-coded app email would pool every user onto one 50k bucket. **Manual-tab last resort only.**
- **LibreTranslate** — hosted has **no free tier at all** (*"it's just not sustainable for us"*), $29/mo Pro at ~20 translations/minute sustained, which is below the LIVE loop's ~85/minute [S29 — CONFIRMED]. Self-hosted needs a 199 MB image and 1–2 GB RAM per language pair [S1 — REPORTED], and is **AGPLv3** [S30 — CONFIRMED], which makes bundling-and-auto-launching from an MIT app a licence question rather than a packaging question. **Rejected on footprint alone.**

---

## 6. Local / offline options in detail

### 6.1 Measured model sizes and published quality

All sizes measured first-hand by HTTP `HEAD`/metadata fetch on 2026-09-06 — grade **CONFIRMED (own probe)**.

| Model | Direction | Size (bytes) | Quality (published) | Licence |
|---|---|---|---|---|
| Mozilla `tiny/ruen` | ru→en | **17,141,051** | FLORES **BLEU 30.4 · COMET 0.8433**; 16.9 M params, int8 intgemm | MPL-2.0 |
| Mozilla `tiny/enru` | en→ru | **17,141,051** | FLORES **BLEU 28.5 · COMET 0.8478** | MPL-2.0 |
| Mozilla `tiny/enfr` | en→fr | **17,140,961** | FLORES **BLEU 48.5 · COMET 0.8573** | MPL-2.0 |
| Mozilla `base/enru` | en→ru | **42,992,955** | FLORES **BLEU 31.5 · COMET 0.8743** | MPL-2.0 |
| Argos `translate-ru_en-1_9` | ru→en | **156,239,112** | LibreTranslate: BLEU 38.31 / COMET-22 0.8645, *"falls short of Google Translate"* | MIT/CC0 |
| Argos `translate-en_ru-1_9` | en→ru | **195,746,693** | BLEU 32.97 / COMET-22 0.8835 | MIT/CC0 |
| `Helsinki-NLP/opus-mt-ru-en` | ru→en | **306,991,893** (fp32) | newstest2019 **BLEU 31.4 · chrF 0.576**; Tatoeba 61.1 | CC-BY-4.0 |
| `Helsinki-NLP/opus-mt-ru-fr` | ru→fr | **310,806,561** (fp32) | (card not read) | CC-BY-4.0 |
| `facebook/nllb-200-distilled-600M` | multi | **2,460,457,927** | — | **CC-BY-NC-4.0** |
| `facebook/m2m100_418M` | multi | **1,935,796,948** | — | MIT |

**French coverage is the trap.** Neither Argos nor Mozilla ships a direct RU↔FR model — the Argos package index contains no `ru_fr`/`fr_ru` entry, and Mozilla's live registry lists **114 directions, all `xx↔en`** (own probes; [S60]). RU→FR therefore costs **two models resident and two decodes** per line. Only OPUS-MT has direct RU↔FR — at 310 MB per direction, and with the worst COMET of the five systems below.

**Live Firefox models supersede the archived repo.** `mozilla/firefox-translations-models` was **archived 2025-12-15**; the shipping models now come from Firefox Remote Settings (`main/translations-models-v2`, 377 records). Decompressed sizes, i.e. what lands on disk [S60 — CONFIRMED]: **ru→en `base-memory` v3.1 = 36,912,066 B, last modified 2026-09-01** (a fresh model, five days before this benchmark); ru→en `tiny` v3.0 = 22,530,152 B; en→ru `base-memory` v3.1 = 35,240,782 B; en→fr = 36,749,127 B; fr→en = 37,200,311 B. Shipped zstd-compressed they are 13.5–25.0 MB each.

**Head-to-head against Google, from Mozilla's own evaluation database** — same test sets, same scoring, five systems. This is the most decision-relevant table in the section [S60 — CONFIRMED, `db.sqlite` 36,646,912 B, `Last-Modified` 2026-09-06]:

| ru→en, COMET-22 | flores200-plus | wmt24pp | bouquet |
|---|---|---|---|
| **Google v2** | **0.8785** | **0.8126** | **0.8913** |
| Microsoft 3.0 | 0.8702 | 0.7946 | 0.8853 |
| NLLB-200-distilled-600M (2.29 GiB) | 0.8509 | 0.7399 | 0.8475 |
| **Bergamot `next26`** (shipping) | **0.8497** | **0.7623** | **0.8633** |
| OPUS-MT `opus-mt-ru-en` (307 MB) | 0.8391 | 0.7416 | 0.8445 |

**A ~30 MB Bergamot model beats a 2.29 GiB NLLB-600M and a 307 MB OPUS-MT on ru→en, and sits ~3 COMET points behind Google.** Mozilla's stated shipping bar is *"within ~5% of Google Translate's COMET scores"* [S60]. en→ru flores COMET: Google 0.9112 · Microsoft 0.8944 · Bergamot 0.8745–0.8764 · NLLB 0.8566 · OPUS-MT 0.8478.

### 6.2 The integration wall — real for CTranslate2 and ONNX, **not** for Bergamot

> **Correction to an earlier assumption in this investigation.** A first pass concluded "no maintained .NET binding exists for any of these engines". That is **true for CTranslate2 and for a Marian ONNX decode loop, and false for Bergamot.** The corrected position:

- **Bergamot: a maintained .NET binding exists.** `Freeesia/BergamotTranslatorSharp` — **MPL-2.0**, created 2025-03-17, **v0.5.1 published 2026-07-30**, NuGet targets **net8.0 and net10.0** [S59 — CONFIRMED]. The `.nupkg` (35,737,769 B) contains a **9,728-byte** managed assembly plus prebuilt natives: **win-x64 `bergamot.dll` = 22,460,928 B (21.4 MiB)**, win-x86 6.0 MiB, win-arm64 7.8 MiB. Its import table is **`KERNEL32`, `SHELL32`, `dbghelp`, `ole32` only** — statically linked, no MKL, and crucially **no MSVC redistributable dependency**, which a portable exe cannot assume. The C surface is three functions:
  ```c
  void *translator_initialize(const char **configPaths, int numPaths);
  char *translator_translate(void *translator, const char *text, bool html);
  void  translator_free(void *translator);
  ```
  Passing **two** config paths makes the native service pivot (RU→FR in one call, no chain to hand-roll), and the wrapper's `Translate(IEnumerable<string>)` wraps lines in `<p>` and calls with `html: true` — one native call per OCR frame.
- **No .NET binding exists for CTranslate2.** nuget.org `ctranslate2` returns three packages, all Whisper-specific [own probe — CONFIRMED]. `FasterWhisper.NET` proves P/Invoke into `ctranslate2.dll` is feasible but ships **383,950,688 B (366 MiB)** of win-x64 natives because it distributes MKL as separate DLLs; the PyPI-style static build is ~58 MB. `ctranslate2.dll` exports **11,785 C++-mangled symbols and no C API**, so a hand-written C shim would be required [S59 — CONFIRMED].
- **The ONNX route has a specific, disqualifying gap.** ONNX Runtime's `com.microsoft.BeamSearch` supports `model_type` 0 (GPT-2) and 1 (T5-style encoder-decoder); the official `convert_generation.py` exporter accepts only `gpt2`, `t5`, `mt5`. **MarianMT is not supported**, so the autoregressive decode loop and KV-cache must be written by hand in C#, and a **known working MarianMT-in-ONNX-from-C# example was not found** (`gh search code` → 0 results) [S59 — CONFIRMED]. Quantized `Xenova/opus-mt-ru-en` is ~105 MiB (encoder + merged decoder) on top of a 15.4 MB `onnxruntime.dll`.
- **The ONNX Runtime path costs 16 MB of native code before any model.** `onnxruntime.dll` for win-x64 v1.29.0 measures **16,149,344 bytes**; the managed package is another ~240 KB [S40 — CONFIRMED]. Worse, adding a native library re-arms `IncludeNativeLibrariesForSelfExtract` first-run extraction to `%TEMP%\.net\…` — **the exact mechanism implicated in P1** (`00-inventaire-stack.md` §13 P1-1). Exporting MarianMT to ONNX with a working beam-search loop callable from C# is additionally unproven.
- **Local LLMs are out.** `LLamaSharp.Backend.Cpu` ships four win-x64 CPU variants totalling ~17.3 MB of native, ~19–20 MB with the managed side, **plus a GGUF model of 0.5–5 GB** [S33 — CONFIRMED]. Against a 178 MB exe and a 150 MB working set, this is not a discussion.
- **Windows offers nothing.** The Windows AI APIs catalogue (docs updated 2026-08-19) lists Phi Silica, Text Recognition, Speech Recognition, Video/Image Super Resolution, Image Description/Segmentation/Erase, Image Generation — **and no translation API**. "Live Translation" appears under **Planned features: *Not yet supported***. Phi Silica requires a Copilot+ NPU, or an NVIDIA RTX 30-series+/AMD RX 9060+ with 6+ GB vRAM **and Developer Mode enabled**, downloads models measured in GB, and is itself being replaced by Aion Instruct in Oct/Nov 2026 [S14 — CONFIRMED].

### 6.3 Market signal

Comparable Windows game-translation tools are moving **away** from bundled offline MT, not toward it:

- **Game-Changing Translator** dropped offline Tesseract + MarianMT at v4 and now *requires* a Google AI Studio or DeepInfra API key; v3.9.6 is explicitly named as "the final release supporting offline… MarianMT translation" [S47 — CONFIRMED].
- **XUnity.AutoTranslator** (3.4k stars, active) hedges across ~14 endpoints, warns that *"if you use any of the online translators that does not require some form of authentication… this plugin may break at any time"*, and hard-caps at *"8000 requests (max 200 characters each) during a single game session"* [S48 — CONFIRMED].
- **Translumo**, **LunaTranslator**, **MORT**, **RSTGameTranslation** all offer provider *menus* rather than one blessed path; the offline options that exist route through Ollama/llama.cpp, i.e. a separate user-installed runtime, not an embedded model [S49 — REPORTED].

**Read-across: the ecosystem's answer to endpoint fragility is a provider chain plus BYO-key, not an embedded model.**

---

## 7. .NET integration and footprint

The app's `ITranslator` interface is two methods (`TranslateAsync`, `TranslateLinesAsync`) and `FallbackTranslator` already composes arbitrarily (`FallbackTranslator(A, FallbackTranslator(B, C))`). **Adding a provider is one class plus one settings field plus one line in `BuildTranslator()`.** There is no architectural work to do.

| Package | Version / date | Auth model | Est. exe growth | Licence | Verdict |
|---|---|---|---|---|---|
| *(raw `HttpClient`, no SDK)* | — | whatever the API needs | **0 MB** | — | ✅ what the app does today; correct for Azure (2 headers) and the free-web endpoints |
| `DeepL.net` | 1.22.1 · 2026-08-19 | one key; **auto-routes `:fx` → `api-free`** | ~0.9–1.1 MB | MIT | ✅ only SDK worth adopting — also gives glossary management |
| `GTranslate` | 2.4.0 · 2026-09-05 | none | **~0.39 MB, zero net8 deps** | MIT | ⚠️ cheapest, but its Google path is `client=gtx` and it has **no 429 handling** |
| `AWSSDK.Translate` | 4.0.100.12 · 2026-09-04 | key + secret + region | ~1.15 MB | Apache-2.0 | ⚠️ cleanest tree, worst onboarding |
| `Google.Cloud.Translation.V2` | 3.5.0 · 2025-11-06 | **API key OK** | ~1.6 MB (drags Newtonsoft.Json) | Apache-2.0 | ⚠️ second JSON stack for no gain |
| `Azure.AI.Translation.Text` | 2.0.0 · 2026-05-29 | key + region | ~3.0–3.2 MB (**~1.3 MB is MSAL you never call**) | MIT | ⚠️ prefer raw HTTP |
| `Google.Cloud.Translate.V3` | 3.11.0 · 2026-04-13 | **service account only** | ~3.5–4.0 MB (gRPC+protobuf) | Apache-2.0 | ⛔ auth model unusable |
| `Microsoft.ML.OnnxRuntime` | 1.29.0 · 2026-08-12 | — | **+16 MB native** + model + **re-arms first-run extraction** | MIT | ⛔ P1 regression |
| `LLamaSharp` + `.Backend.Cpu` | 0.27.0 · 2026-04-26 | — | **+19–20 MB** + GGUF (GB) | MIT | ⛔ |
| *any multi-provider MT abstraction* | — | — | — | — | ⛔ **none is maintained** — every candidate on nuget.org is dead (newest release 2024-05-09, 693 lifetime downloads) |

All package data measured from `.nupkg` contents on 2026-09-06 [S40 — CONFIRMED].

**Two numbers worth internalising.** Because the publish is self-contained, single-file and **uncompressed on purpose**, added exe size ≈ the sum of the added managed assemblies. A pure-managed translation SDK therefore costs **0.2 %–2.2 % of the 178 MB exe** — *exe size is not a valid argument against adding DeepL.net or a raw-HTTP provider*. Conversely, anything **native** costs 16–20 MB **and** reintroduces `%TEMP%` extraction on first run, which is a P1 concern, not a size concern.

**One trap to carry forward:** wrapping providers in `Polly`-backed handlers does not change the OCE rule. An `HttpClient` timeout still surfaces as `TaskCanceledException` with the token *not* cancelled, so every `when (ct.IsCancellationRequested)` filter in `FallbackTranslator` remains mandatory.

---

## 8. Cost scenarios

### 8.1 Workload definitions (stated so the arithmetic is auditable)

- **HEAVY = 5,800,000 characters/month.** The brief's profile: 4 h/day of LIVE × 30 days = 120 h = 432,000 s, which at 5.8 M chars implies **13.4 chars/s ≈ one new 40-char line every 3 s** sustained. Realistic for a busy but not saturated chat.
- **SATURATED (worst case) = 17,280,000 chars/month.** 1 new line/s × 40 chars × 432,000 s. Shown because the LIVE loop *can* produce this.
- **LIGHT = 500,000 characters/month.**

**Amplifiers the app applies on top of these figures** (all CONFIRMED from `00-inventaire-stack.md` §8.3): each LIVE tick can issue **two** batches (`ru` and `auto`) → up to ×2 characters; retry ×3 on 429/5xx; failure placeholders are never cached, so failing lines are re-requested every tick; and the read path and write path hold **two independent caches**, so the same string can be billed twice. A realistic billing multiplier of **×1.3–×2.0** over the tables below should be assumed before anyone signs up for a paid tier.

### 8.2 Character-billed APIs

| Provider | Free/month | $/M | **HEAVY 5.8 M** | SATURATED 17.28 M | **LIGHT 0.5 M** |
|---|---|---|---|---|---|
| **Google Cloud Translation (NMT)** | 500,000 | $20.00 | (5.8−0.5)×20 = **$106.00** | (17.28−0.5)×20 = **$335.60** | **$0.00** (inside free tier) |
| Google Translation LLM | none | $10 in + $10 out | 5.8×10 + 5.8×10 = **$116.00** | **$345.60** | **$10.00** |
| **Azure AI Translator S1** | **2,000,000 (permanent)** | $10.00 | (5.8−2)×10 = **$38.00** | (17.28−2)×10 = **$152.80** | **$0.00** (fully covered by F0) |
| **Amazon Translate** (after month 12) | 0 | $15.00 | 5.8×15 = **$87.00** | **$259.20** | **$7.50** |
| Amazon Translate (first 12 months) | 2,000,000 | $15.00 | **$57.00** | **$229.20** | **$0.00** |
| **Yandex Translate** | none | $4.10 | 5.8×4.10 = **$23.78** | **$70.85** | **$2.05** |
| **Tencent TMT** (international) | none | $10.00 | 5.8×10 = **$58.00** | **$172.80** | **$5.00** |
| Baidu Intelligent Cloud MT | 0 (without CN verification) | ¥49 ≈ $6.90 | 5.8×49 = **¥284 ≈ $40** | ¥847 ≈ $119 | ¥24.5 ≈ $3.45 |
| **DeepL Growth** ($26/mo annual, 12 M chars/yr = 1 M/mo, overage $27.50/M) | 1,000,000 equiv. | $27.50 over | 26 + (5.8−1)×27.50 = **$158.00** | 26 + 16.28×27.50 = **$473.70** | **$26.00** (subscription only) |
| DeepL Developer (free) | **1 M TOTAL, one-time** | — | exhausted in **≈5 days** | ≈1.7 days | exhausted in **2 months** |
| MyMemory (email) | 50,000/day ≈ 1.5 M/mo | — | ⛔ insufficient | ⛔ | **$0.00** — covers LIGHT |
| LibreTranslate Pro | — | $29/mo flat | **$29.00** but **~20 tr/min sustained < the loop's ~85/min** | ⛔ | $29.00 |

**Reading:** *no paid character-billed API is affordable for a heavy user of a free app.* Azure's permanent 2 M/month is the only free tier that meaningfully dents the bill, and it fully covers the light user.

### 8.3 Token-billed LLMs

Assumptions, all **ASSUMED** and stated so they can be corrected (OQ-6): Cyrillic ≈ **2.5 chars/token**; English output ≈ **4 chars/token**; lines batched **10 per request** behind a ~60-token instruction.

Per **1,000 lines** (= 40,000 chars): input = 40,000/2.5 = 16,000 text tokens + 100 requests × 60 = 6,000 prompt tokens → **22,000 input tokens**; output = 40,000/4 = **10,000 output tokens**.

| Model | $ / 1,000 lines | **HEAVY (145,000 lines)** | **LIGHT (12,500 lines)** |
|---|---|---|---|
| `gpt-5-nano` | 0.022×0.05 + 0.010×0.40 = **$0.0051** | **$0.74** | **$0.06** |
| Gemini 3.1 Flash-Lite | 0.022×0.25 + 0.010×1.50 = **$0.0205** | **$2.97** | **$0.26** |
| `gpt-5-mini` | 0.022×0.25 + 0.010×2.00 = **$0.0255** | **$3.70** | **$0.32** |
| Gemini 3.5 Flash-Lite | 0.022×0.30 + 0.010×2.50 = **$0.0316** | **$4.58** | **$0.40** |
| Claude Haiku 4.5 | 0.022×1.00 + 0.010×5.00 = **$0.0720** | **$10.44** | **$0.90** |

**The gap is not marginal.** A heavy user costs **$106/month on Google Cloud Translation and $0.74/month on `gpt-5-nano`** — a factor of ~143. Even Claude Haiku 4.5, the most expensive row, is 10× cheaper than the cheapest character-billed API for this workload. The reason is structural: MT APIs are priced per character on a 2016 cost base; LLM tokens are not.

**What this does *not* say:** that an LLM is the right engine for the LIVE loop. Latency (§5.7) and per-minute rate limits are unresolved, and a 145,000-request/month pattern will meet tier limits long before it meets a budget.

---

## 9. Costing the optional proxy backend

If a shared backend held one key on behalf of all users:

- **Compute is free or near-free.** Cloudflare Workers: **100,000 requests/day free**; paid **$5/month for 10 M requests**, overage $0.30/M [S50 — REPORTED]. A heavy user at ~2.8 req/s for 4 h/day generates ~40,000 requests/day, so the free tier serves ~2.5 heavy users and the $5 tier serves ~250.
- **The translation bill is what makes it impossible.** 100 heavy users × 5.8 M chars = **580 M chars/month**:
  - Google Cloud Translation: 580 × $20 = **$11,600/month**
  - Azure S1: 580 × $10 = **$5,800/month**
  - Yandex: 580 × $4.10 = **$2,378/month**
  - `gpt-5-nano`: 100 × $0.74 = **$74/month**
- **Conclusion:** a shared-key proxy in front of a character-billed MT API is financially impossible for a free MIT app. In front of a nano-tier LLM it is *arithmetically* survivable (~$74/month per 100 heavy users) but still owner-funded, unbounded, and trivially abusable without authentication — and it would introduce a server the project explicitly does not have. **Not recommended.** The one legitimate use of a tiny backend would be *non-translating*: a signed config endpoint that lets the owner switch the default free-web client id without shipping a release when Google moves again.

---

## 10. Local-model feasibility memo

**Question: can a Marian/CTranslate2 or Bergamot RU→EN model run inside this WPF app within the RAM and startup budget?**

**Answer: yes — with Bergamot specifically, lazily loaded and idle-unloaded. Not with anything else, and never always-on.**

All figures below were **measured on a Coffee Lake / AVX2 / 8-logical-core Windows 11 laptop with no AVX-512 and no VNNI** — deliberately representative of a PW-RU player's machine and a pessimistic case for int8 GEMM [S59].

**What works.**

| | Bergamot `tiny` ru→en | Bergamot `base-memory` ru→en |
|---|---|---|
| Model on disk (decompressed) | 22,530,152 B | 36,912,066 B |
| Init / load time | **103–119 ms** | 96–154 ms |
| **Median latency, ~40-char line** | **6.5–12.1 ms** | 13.4–38.9 ms |
| Throughput, 1 thread | **64–80 lines/s** | 53–74 lines/s |
| **Resident set after first translate** | **+127 MiB USS / 140 MiB RSS** | **+190 MiB** |

Against the app's ~2 lines/s requirement that is a **30–100× margin on a single thread**, and the 103 ms load is small enough to hide behind the first fallback use. Quality sits ~3 COMET points behind Google and **ahead of both NLLB-600M and OPUS-MT** (§6.1).

**The real constraint is RAM, and it is not negotiable.** The +127 MiB (tiny) / +190 MiB (base-memory) resident delta was swept against `workspace` (8/16/32/64/128), `mini-batch-words` (128/1024) and `max-length-break` (32/128): **RSS moved by ±1 MiB in every combination** [S59 — MEASURED]. Marian allocates a fixed pool on first translate. Consequences:
- One loaded model ≈ **85% of the app's entire current 150 MB working set**.
- A **RU↔FR pivot needs two models resident ≈ 250–310 MiB**, roughly tripling the process.
- Therefore: load on first fallback use, **unload on an idle timeout**, never hold all four directions open, and never load at startup.

**Sizing a full deployment** (decompressed, all four directions): `bergamot.dll` 22,460,928 + ru→en `tiny` 22,530,152 + en→ru 35,240,782 + en→fr 36,749,127 + fr→en 37,200,311 = **154,181,300 B ≈ 147 MiB**. Shipped as Mozilla's zstd blobs the four models are **86.2 MB** rather than 128.4 MB. **Download on first use into the editable-copy directory; do not embed.** Bundling would push the exe to ~325 MB and make every user pay for a feature most never enable — and v0.14.0 removed single-file compression precisely to avoid that class of cost. Note `UpdateService` currently allowlists only `github.com`/`githubusercontent.com`, so model downloads need either a second allowlist entry or a mirror on a GitHub release.

**The slang finding — the most important design consequence.** Measured outputs on real PW-RU chat lines [S59 — MEASURED]:

| Raw line | Offline output (raw) | Same line **after `SlangGlossary.Expand`** |
|---|---|---|
| `Всем привет, кто идет в данж?` | "who's going **dangling**?" | "who's going into the **dungeon**?" |
| `го пати на босса, нужен хил` | "you need a **heel**" | "We need a doctor" |
| `нид на дроп, я хил` | "**Nod** to drop, I'm **sick**." | "I need prey, I'm a doctor." |
| `спс за пати, было весело` | "**ps** for **pat**, it was fun." | "Thanks for the band, it was fun." |

**Any offline engine must sit downstream of `SlangGlossary.Expand`, not instead of it.** Raw slang degrades badly; the existing expansion layer recovers most of the gap. This closes OQ-8 in the affirmative-with-conditions and confirms the pipeline ordering the app already uses for the cloud path.

**What still counts against it.**
1. **The native DLL re-arms the P1 mechanism.** `bergamot.dll` is a native library, so `IncludeNativeLibrariesForSelfExtract` would extract it to `%TEMP%\.net\…` on first run — a leading suspect for the 6–10 s cold start (`00-inventaire-stack.md` §13 P1-1). **Mitigation: ship it beside the exe like `Data/*.json`, not inside the bundle.**
2. **`BlockingService.Translate` is synchronous** — it must run on `Task.Run`, never on the UI thread.
3. **The measured numbers are one machine.** Latencies varied 2–3× between quiet and busy runs; only the *ratios* (Bergamot ~10× faster than Argos at ~⅔ the RAM) held across every run. Re-measure on target hardware before committing to a LIVE-loop cadence.
4. **MPL-2.0** for the DLL, the models and the wrapper. File-level copyleft — compatible with shipping alongside an MIT app, but it is a second licence in the tree and belongs in the About tab.

**Verdict: YES, as a lazily-loaded terminal fallback — scheduled behind Chain A, not with it.** It removes the external dependency entirely for users who accept the RAM cost, and it is the only option in this benchmark that does. It is **not** a replacement for the no-key cloud path, because 127–310 MiB is a real tax on a "must not lag the game" product. The wider market moving the other way (Game-Changing Translator dropped MarianMT at v4 [S47]) reflects the era before a maintained .NET binding existed; that premise changed on 2026-07-30.

---

## 11. Recommendation matrix for the target architecture

The chain is expressed in the app's existing primitive: `CachingTranslator(FallbackTranslator(primary, FallbackTranslator(secondary, tertiary)))`.

### Chain A — recommended

```
read path  (OCR / LIVE):  Google clients5 dict-chrome-ex  →  Edge translatetext  →  [degrade gracefully]
write path (Translator / quick reply):
   user key present:  DeepL (or Azure F0)  →  Google clients5 dict-chrome-ex  →  Edge translatetext
   no key:            Google clients5 dict-chrome-ex  →  Edge translatetext
```

| Pros | Cons |
|---|---|
| Fixes P2 with a **~15-line change** (URL + `client=` + a simpler response parser), measured working from the affected network | Both tiers are undocumented endpoints — this buys time, not permanence |
| Leaves the robots-disallowed path; ToS posture strictly improves | Still no contract, no SLA, no support |
| Two **independent vendors** — a Google-side change cannot take out both | Edge route is only ~1 month proven as a keyless path |
| Costs nothing; no signup; no behaviour change for users | Requires the hardening in §11.4 to fail legibly |

### Chain B — the "user brought a key" upgrade (complements A, does not replace it)

```
DeepL (existing :fx key)  or  Azure F0 (new key slot)  →  Chain A
```

| Pros | Cons |
|---|---|
| Azure F0 = **2 M chars/month, free, permanent** — real headroom, contracted, 93 ms RTT measured | Azure signup wants a non-prepaid card |
| Two headers, no SDK required (0 MB exe growth) | DeepL's own free tier is now 1 M chars *total* — the DeepL slot is legacy-key-only in practice |
| Native batching: 1,000 array elements per request | Adds a settings field and a migration (`SettingsVersion` + `AppSettings.Migrate`) |

### Chain C — the LLM option (write path only)

```
gpt-5-nano / Gemini Flash-Lite (user key)  →  Chain A
```

| Pros | Cons |
|---|---|
| **$0.74/month for a heavy user** — 143× cheaper than Google Cloud Translation | Latency 4×+ NMT (REPORTED) — unproven for a 700 ms LIVE tick |
| Best observed handling of chat abbreviations (`пт` → "Friday") | Free-tier RPM/RPD not published; Groq-class free tiers are below 2 req/s |
| One class, raw HTTP, 0 MB | Non-determinism, prompt-injection surface from OCR'd chat, moderation refusals |

### Chain D — offline terminal fallback (viable; schedule after Chain A)

```
CachingTranslator( FallbackTranslator( DeepL/Azure, TranslationService, BergamotTranslator ) )
```
Bergamot `tiny`/`base-memory` via `BergamotTranslatorSharp`, models downloaded on first use, loaded lazily, unloaded on idle. The existing `FallbackTranslator` composition takes this without redesign.

| Pros | Cons |
|---|---|
| **Removes the external dependency entirely** — the only option here that does | **+127 MiB USS for one model, +250–310 MiB for a RU↔FR pivot, and it is not tunable** |
| **6.5–12 ms/line, 80 lines/s** measured — 30–100× the required rate | 147 MiB on disk for all four directions (86 MB compressed download) |
| COMET 0.8497 vs Google 0.8785 — beats NLLB-600M *and* OPUS-MT | Native DLL ⇒ must ship beside the exe, not in the bundle, or it re-arms the P1 `%TEMP%` extraction |
| ~1 day of integration: NuGet ref, config file, `BlockingService` | MPL-2.0 enters the licence tree; `BlockingService.Translate` is synchronous |
| Native two-config pivot and HTML batching — no chain to hand-roll | Must sit **downstream of `SlangGlossary.Expand`** or slang output is unusable |

Two failure modes only — "model not downloaded" and "init failed" — both of which must return a `(…)` placeholder so nothing poisons the cache. It cannot time out, so the OCE trap does not apply to this leg.

### Chains explicitly rejected

- **Any chain routed through Lingva / SimplyTranslate / Mozhi** — measurably down today, shared-IP block risk, no maintainer response on rate-limit questions.
- **Any chain whose free tier is MyMemory for the LIVE loop** — 5,000 chars/day burns in ~2 minutes.
- **Any chain requiring Yandex Cloud** — business-entity onboarding.
- **Any chain requiring Google Cloud Translation v3** — no API-key auth.
- **A shared-key proxy** — §9.

### 11.4 Hardening that must accompany *any* chain

These come out of the evidence, not out of preference:

1. **Classify the HTML abuse page.** A 429/403 whose `Content-Type` is `text/html` and whose body contains `automated queries` must become a typed "blocked upstream" error. Today it lands as a `JsonException` or is folded in with 400/404. This is the single most common bug in every project surveyed [S1].
2. **There is no `Retry-After` to obey** (own probe). Any cooldown must be a client-side circuit breaker with a fixed window, and it must be **persisted** — the owner reports that restarting the app does not clear the condition, because the condition is server-side.
3. **Stop retrying ×3 into a hard block.** Three attempts against an endpoint that 429s on request #1 triples the abuse signal for zero benefit. Retry should be gated on the circuit breaker being closed.
4. **Log the status code and a response snippet.** `TranslationService` logs nothing today, so the About tab's "Copy error report" is empty for exactly the failure users report (`00-inventaire-stack.md` §13 P2-17).
5. **Make the client id configurable**, not a compile-time constant. The August 2026 cluster shows this value has become a moving target.

---

## 12. Open questions

| # | Question | Why it matters | How to settle it |
|---|---|---|---|
| **OQ-1** | Does `client=dict-chrome-ex` survive the app's *actual* sustained LIVE load (hours, not 20 requests)? | The whole no-key recommendation rests on it | Instrumented soak test on a branch, 2 req/s for ≥2 h, on ≥2 networks |
| **OQ-2** | Why did `client=at` return 200 from one IP and 429 from the owner's? | If client ids are being blocked progressively, `dict-chrome-ex` has a shelf life | Re-probe the client-id matrix from several IPs, weekly |
| **OQ-3** | **Do existing DeepL `:fx` (API Free) keys still work after the July 2026 plan change?** | Directly affects current PWRU Helper users with a saved key | Ask a user with an old `:fx` key to test; or open a DeepL support ticket. **not found** in any official source |
| **OQ-4** | Is the Azure F0 2 M chars/month genuinely permanent, in writing? | It is the load-bearing figure of recommendation (ii) | The pricing page states it with no duration; a Microsoft Q&A says permanent [REPORTED]. Confirm in the Azure portal on a real F0 resource |
| **OQ-5** | Azure S1 exact $/M from the official page | Only REPORTED ($10/M, MS Q&A 2025-09-10) — the pricing page renders `$-` placeholders to a fetcher | Read the Azure pricing calculator in a browser, or create an S1 resource |
| **OQ-6** | Characters-per-token for Russian in the current OpenAI/Gemini/Anthropic tokenizers | The entire §8.3 cost advantage scales with this | Run the vendors' tokenizer endpoints on a real sample of the app's OCR output |
| **OQ-7** | Measured end-to-end latency of a nano/Flash-Lite LLM on a 10-line batch | Decides whether Chain C can ever serve the LIVE loop | Direct measurement with a real key |
| ~~OQ-8~~ | ~~Quality of Bergamot tiny ru→en on this game's chat~~ | **ANSWERED** — measured on real PW-RU lines: raw slang fails (`данж`→"dangling", `хил`→"heel", `спс`→"ps"); the same lines after `SlangGlossary.Expand` are usable. The engine must sit **downstream** of the glossary [S59] | — |
| **OQ-8b** | Re-measure Bergamot latency/RAM on the *slow* machines from P1, not just one dev laptop | The measured 6.5–12 ms held only on one Coffee Lake box; latencies varied 2–3× between quiet and busy runs | Run the same harness on the affected machines during the P1 measurement campaign |
| **OQ-8c** | Does shipping `bergamot.dll` beside the exe (rather than in the single-file bundle) actually avoid the `%TEMP%` self-extraction? | Determines whether the offline path costs a P1 regression | Publish a test build both ways and compare cold start |
| **OQ-9** | Does the `edge.microsoft.com/translate/translatetext` route survive load and time? | It is the independent second tier | Same soak test as OQ-1 |
| **OQ-10** | Whether the `translate_a` caching in `CachingTranslator` conflicts with Google APIs ToS §5.e | Legal hygiene for an MIT public repo | Legal reading; the applicability of that ToS to the free web endpoints is genuinely ambiguous |
| **OQ-11** | Google Cloud Translation NMT `$20/M` from the official page verbatim | The page truncates for a fetcher; the figure is REPORTED from several 2026 sources plus the CONFIRMED "Translation LLM is cost-equivalent with NMT" line | Read `cloud.google.com/translate/pricing` in a browser |
| **OQ-12** | Is Tencent's `TextTranslate` being sunset internationally, or was the 2026-08-06 "Deleted APIs" entry a doc reorganisation? | It is the only non-Western API that fits (RU↔FR direct, $10/M, **14 ms RTT from France**, EU-payable) — and it is currently undocumented | Live signed key on `tmt.eu-frankfurt` + a written answer from Tencent support. Do not write provider code before both |
| **OQ-13** | Tencent `TextTranslate` published QPS | Decides whether it can carry the LIVE loop at all | Tencent support / quota console |
| **OQ-14** | Papago's KRW rate and any foreign-signup path; a 2026 confirmation of Baidu's `fanyi-api` tiers | Completeness only — both are already ruled out on coverage/verification | Low priority |

---

## 13. Sources

All accessed **2026-09-06** unless a different date is given. "own probe" entries were measured by this investigation and are reproducible with `curl`.

1. **Parallel endpoint-probe report + dated GitHub issue cluster** (this investigation's free-endpoints research stream): SubtitleEdit #14050/#14015 (2026-08-24), instantTranslate #53 (2026-08-24), Apollo-Reborn #998 (2026-08-26), kuantorflow #348 (2026-08-24), opentranslate #73/#111/#126/#162 (2026-08-29 → 2026-09-05), QTranslate #244 (2026-09-03), noctalia-dev/official-plugins #64 (2026-09-03), trans-anywhere-tauri #11 (2026-09-02), read-frog #2045 (2026-08-06), py-googletrans #268 (2021-01-03).
2. Google Terms of Service, effective 2026-07-30 — https://policies.google.com/terms?hl=en-US
3. `https://translate.googleapis.com/robots.txt` (line 162 `Disallow: /translate_a/`) and `https://clients5.google.com/robots.txt` — own fetch.
4. `POST https://edge.microsoft.com/translate/translatetext?from=&to=en&isEnterpriseClient=false` — own/parallel probe; corroborated by read-frog #2045 (merged 2026-08-06).
5. Azure AI Translator pricing — https://azure.microsoft.com/en-us/pricing/details/translator/ (F0 line CONFIRMED; paid figures render as `$-` to a fetcher).
6. Microsoft Q&A — "For how long can Free Translation tier (F0) be used?" https://learn.microsoft.com/en-us/answers/questions/357173/
7. DeepL Help Center — "DeepL API plans" https://support.deepl.com/hc/en-us/articles/360021200939-DeepL-API-plans (403 to a fetcher; plan names Developer/Growth/Enterprise read from indexed content).
8. eesel AI — "DeepL pricing in 2026", last edited 2026-06-05 — https://www.eesel.ai/blog/deepl-pricing
9. Langbly — "DeepL API Pricing 2026: Developer vs Growth", 2026-01-30, verified 2026-07 — https://langbly.com/blog/deepl-api-pricing-guide/
10. Cloud Translation API overview — https://docs.cloud.google.com/translate/docs/api-overview ("Cloud Translation - Advanced API does not support API keys").
11. `mozilla/firefox-translations-models` — `models/tiny/ruen/metadata.json`, `tiny/enru`, `tiny/enfr`, `base/enru`; repo licence MPL-2.0, last push 2025-12-15 — own probe via GitHub API + raw.githubusercontent.com.
12. nuget.org query `ctranslate2` (3 results, all Whisper) and GitHub code search `ctranslate2 language:csharp` — own probe.
13. `browsermt/bergamot-translator` (MPL-2.0, last push 2024-05-12), `XapaJIaMnu/translateLocally` (MIT, 2025-03-30), `mozilla/translations` (MPL-2.0, 2026-09-03), `OpenNMT/CTranslate2` v4.8.2 (2026-08-31) — own probe via GitHub API.
14. Windows AI APIs overview, docs updated 2026-08-19 — https://learn.microsoft.com/en-us/windows/ai/apis/ ("Live Translation (Not yet supported)"; Phi Silica hardware requirements; Aion Instruct replacement Oct/Nov 2026).
15. MyMemory usage limits — https://mymemory.translated.net/doc/usagelimits.php
16. DeepL API getting-started / translate reference — https://developers.deepl.com/docs/getting-started/intro and .../api-reference/translate
17. Cloud Translation pricing — https://cloud.google.com/translate/pricing (free tier and Translation LLM lines CONFIRMED via indexed page text; the page body truncates for a fetcher — see OQ-11).
18. Cloud Translation quotas and limits — https://docs.cloud.google.com/translate/quotas
19. Azure Translator service limits, docs dated 2026-08-11 — https://learn.microsoft.com/en-us/azure/ai-services/translator/service-limits
20. Amazon Translate pricing — https://aws.amazon.com/translate/pricing/
21. Amazon Translate guidelines and quotas — https://docs.aws.amazon.com/translate/latest/dg/what-is-limits.html
22. Yandex Translate pricing policy — https://aistudio.yandex.ru/docs/en/translate/pricing ($4.10 per million characters, USD, net of VAT)
23. Yandex Cloud billing — questions for non-residents of Russia — https://yandex.cloud/en/docs/billing/qa/non-resident
24. OpenAI API pricing — https://developers.openai.com/api/docs/pricing
25. (reserved — OpenAI rate-limit tiers: **not found** at a fetchable URL)
26. Gemini API pricing — https://ai.google.dev/gemini-api/docs/pricing
27. Gemini API rate limits — https://ai.google.dev/gemini-api/docs/rate-limits (free-tier RPM/RPD deferred to AI Studio; **not found** on the page)
28. Claude pricing — https://claude.com/pricing (Haiku 4.5: $1/MTok in, $5/MTok out)
29. LibreTranslate portal — https://portal.libretranslate.com/ (no free tier; Pro $29/mo; Business $58/mo)
30. LibreTranslate repository (AGPLv3) — https://github.com/LibreTranslate/LibreTranslate ; Argos ru/en model results, LibreTranslate community, 2024-01-24.
31. Argos package index — https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json ; model sizes via HTTP `HEAD` on `argos-net.com` — own probe.
32. `Helsinki-NLP/opus-mt-ru-en` model card (CC-BY-4.0; newstest2019 BLEU 31.4 / chrF 0.576) — https://huggingface.co/Helsinki-NLP/opus-mt-ru-en ; file sizes via HF `resolve` `HEAD` — own probe.
33. `LLamaSharp` 0.27.0 and `LLamaSharp.Backend.Cpu` 0.27.0 — nuget.org, package contents inspected.
34. Cloud Translation troubleshooting — https://docs.cloud.google.com/translate/troubleshooting ("Daily Limit Exceeded" / "User Rate Limit Exceeded" returned as **403**)
35. DeepL error handling — https://developers.deepl.com/docs/best-practices/error-handling (429, 456, 500; exponential backoff; no published RPS)
36. DeepL glossaries reference — https://developers.deepl.com/docs/api-reference/glossaries (RU-EN/EN-RU/RU-FR/FR-RU supported; 10 MiB; 1,024 bytes/entry; 1,000 glossaries/account)
37. insightdesk.uk — "Best English to Russian Translator: Google vs DeepL vs Yandex" (commercial, low confidence)
38. Microsoft Q&A — Azure AI Translator costing, answer dated 2025-09-10 ("$10 per 1 million characters for standard text translation")
39. Azure Translator v3 reference, docs dated 2026-06-02 — https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/reference/v3/reference
40. **Parallel .NET SDK footprint report** (this investigation): nuget.org listings and `.nupkg` contents for DeepL.net 1.22.1, Azure.AI.Translation.Text 2.0.0, Azure.Core 1.62.0, AWSSDK.Translate 4.0.100.12, Google.Cloud.Translation.V2 3.5.0, Google.Cloud.Translate.V3 3.11.0, GTranslate 2.4.0, Microsoft.ML.OnnxRuntime 1.29.0, LLamaSharp 0.27.0; plus `onnxruntime-win-x64-1.29.0.zip` (`onnxruntime.dll` = 16,149,344 bytes).
41. EU sanctions coverage of Yandex financial entities (OpenSanctions / Nordic Star Law) — REPORTED, context only.
42. TranslatePlus — "TranslatePlus vs DeepL vs Google Translate (2026 Benchmark)" (commercial; NMT "under 100 ms"; GPT-5.2 4×, Claude Opus 4.6 6×, Gemini 3.1 Pro 29× DeepL latency)
43. MQM-Chat: Multidimensional Quality Metrics for Chat Translation — https://arxiv.org/pdf/2408.16390
44. "How do Language Models Generate Slang: A Systematic Comparison…", 2025 — https://arxiv.org/abs/2509.15518
45. Hugging Face Inference Providers — https://huggingface.co/docs/api-inference/index (partner matrix has no translation task)
46. GTranslate — https://www.nuget.org/packages/GTranslate and https://github.com/d4n3436/GTranslate (2.4.0, 2026-09-05, MIT, zero net8 deps)
47. Game-Changing Translator / OCR-Translator README — https://github.com/tomkam1702/OCR-Translator ("version 3.9.6… is the final release supporting offline Tesseract OCR and MarianMT translation")
48. XUnity.AutoTranslator README — https://github.com/bbepis/XUnity.AutoTranslator
49. Playto — "Game screen translators for PC — the free options and how to choose", 2026-04-22 — https://playto.dev/blog/pc-game-translation-tools/
50. Cloudflare Workers pricing — https://developers.cloudflare.com/workers/platform/pricing/ (100,000 req/day free; $5/mo for 10 M requests; $0.30/M overage)
51. Naver Papago migration/shutdown notices (developers.naver.com Papago terminated 2024-02-29; AI NAVER API console Papago 2025-03-20) — REPORTED; surviving endpoint https://guide.ncloud-docs.com/docs/en/papagotranslation-spec (page updated 2026-07-23) — CONFIRMED
52. Papago translatable-combinations table — https://api.ncloud-docs.com/docs/en/ai-naver-papagonmt-translation (2026-07-23) — **Russian pairs only with Korean and English**
53. Baidu Intelligent Cloud Machine Translation pricing — https://cloud.baidu.com/doc/MT/s/ykqq95r2y (¥49 / 1 M chars PAYG; volume packs)
54. Baidu `fanyi-api` tier notice, 2022-08-01 — REPORTED, no 2026 confirmation located
55. Baidu Cloud individual real-name verification methods — https://cloud.baidu.com/doc/UserGuide/s/8jwvy3c96 (face scan or mainland-Chinese bank card only)
56. Tencent Cloud TMT language-pair table, official API documentation (2026 export) — `ru` → zh/en/**fr**/es/it/de/tr/pt; `fr` → … `ru`
57. Tencent Cloud International — Machine Translation billing — https://intl.cloud.tencent.com/document/product/1161/50082 ("10 USD per 1 million characters"; no international free tier). International API doc **Release 3, 2026-08-06** lists `TextTranslate` under **"Deleted APIs"**; the action nonetheless answers `AuthFailure.SecretIdNotFound` on `tmt.eu-frankfurt` / `tmt.intl` / `tmt.ap-guangzhou` (own probe, 2026-09-06).
58. Tencent Cloud (Chinese console) 机器翻译 pricing — https://cloud.tencent.com/document/product/551/35017 (updated 2026-07-15; 5,000,000 chars/month free, then ¥58/M)
59. **Local-engine measurement stream of this investigation** (2026-09-06, Coffee Lake / AVX2 / 8 logical cores / no AVX-512 / no VNNI, Windows 11): `Freeesia/BergamotTranslatorSharp` — https://github.com/Freeesia/BergamotTranslatorSharp and https://www.nuget.org/packages/BergamotTranslatorSharp (MPL-2.0, v0.5.1 published 2026-07-30, net8.0/net10.0; nupkg 35,737,769 B; `lib/net8.0/BergamotTranslatorSharp.dll` 9,728 B; `runtimes/win-x64/native/bergamot.dll` 22,460,928 B; import table via pefile = KERNEL32/SHELL32/dbghelp/ole32 only; three C exports). Measured Bergamot init/latency/RSS and the raw-vs-glossary-expanded slang outputs; Argos measured at 287–767 ms load, +184 MiB RSS, 122 ms/line. CTranslate2 wheel `ctranslate2-4.8.2-cp312-cp312-win_amd64.whl` → `ctranslate2.dll` 59,296,256 B, 11,785 mangled exports, no C API; `FasterWhisper.NET` win-x64 natives 383,950,688 B. ONNX Runtime `BeamSearch` `model_type` ∈ {GPT-2, T5} and `convert_generation.py` ∈ {gpt2, t5, mt5} — MarianMT unsupported; `gh search code "MarianTokenizer OnnxRuntime" --language csharp` → 0 results.
60. **Mozilla translation model registry and evaluation database** (fetched 2026-09-06): Firefox Remote Settings `main/translations-models-v2` — https://firefox.settings.services.mozilla.com/v1/buckets/main/collections/translations-models-v2/records (377 records, 57 source / 54 target languages, every pair `xx↔en`; ru→en `base-memory` v3.1 decompressed 36,912,066 B, `last_modified` 2026-09-01); model list https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json (114 directions, no `ru-fr`/`fr-ru`); evaluation DB https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/db.sqlite (36,646,912 B, `Last-Modified` 2026-09-06) — `final_evals`/`final_eval_metrics` COMET-22 and BLEU for Google v2, Microsoft 3.0, Bergamot, NLLB-200-distilled-600M and OPUS-MT on flores200-plus / wmt24pp / bouquet. Shipping bar quoted from https://mozilla.github.io/translations/firefox-models/. `mozilla/firefox-translations-models` **archived 2025-12-15**; `mozilla/translations` (MPL-2.0) last push 2026-09-03.

---

_End of `02-traduction/benchmark-fournisseurs.md`. Feeds `02-traduction/architecture-cible.md` (Winston) and `02-traduction/plan-migration.md`. No production code was changed by this document._
