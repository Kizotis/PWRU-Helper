# 02 — P2 · Current implementation analysis (translation path)

_Phase 1 · investigator: **Amelia** (BMAD Senior Software Engineer), method `gds-investigate` (evidence-graded) ·
baseline commit `f0efc26` = `main` v0.14.0 + docs · 2026-09-06._

**Scope.** What the shipped code actually does on the Google translation path, quantified: how a 429 becomes the
string the owner reported, how many requests a LIVE session sends, what makes a per-IP throttle **worse**, what
already makes it **better**, what cannot be observed today, and which seams Phase 2 can plug into.
**Not in scope:** the target architecture (Winston, `architecture-cible.md`), external research on Google's
throttling (Mary, `mecanismes-de-blocage-google.md`), any live request to Google (Amelia-QD's reproduction script).

**No production code was changed. No build was run. Every claim below is graded.**

Grades: **[CONFIRMED]** read in code with `file:line` · **[INFERRED]** derived, chain shown ·
**[UNKNOWN]** needs measurement or research. Paths are repo-root relative.

---

## 0. Executive summary

| # | Finding | Grade |
|---|---------|-------|
| **F-1** | The owner's string is **E2 wrapped by a feed row**. Only two sites in the whole app wrap `Friendly(ex)` in parentheses: `MainWindow.Live.cs:281` and `MainWindow.Ocr.cs:298`. Both are OCR-feed rows. | [CONFIRMED] |
| **F-2** | Of the two, **LIVE** fits "sometimes at launch, sometimes while typing"; read-once does not. Typing surfaces (`Failed: …`, `⚠ …`) are excluded by their own formatting. | [INFERRED], high |
| **F-3** | A LIVE session's request rate is **capped by the tick rate, not by chat volume**: ≈ 1/min calm, ≈ 36/min active, ≈ 103/min busy (ceiling 171), **≈ 128/min while already 429-throttled**. | [INFERRED] from confirmed constants |
| **F-4** | The **top amplifier is the ×3 retry on 429** (`Services/TranslationService.cs:126,139,153`): fixed 300/600 ms, no jitter, `Retry-After` never read. A throttled client triples its own rate. | [CONFIRMED] |
| **F-5** | The **auto-stop after 5 errors never fires in a calm chat**: `consecutiveErrors = 0` runs on every tick that confirms no new line (`MainWindow.Live.cs:219`). A quiet chat + a long throttle = 3 rejected requests per new message, forever. | [CONFIRMED] |
| **F-6** | **Phase 0's §13 item 18 is wrong for LIVE.** Failed lines are *not* re-requested next tick — `LiveDedup` remembers them as emitted (`Services/LiveDedup.cs:96`). The real cost is worse in a different way: the row is **burned** — permanently `(Google is limiting…)`, never retried even after Google recovers. | [CONFIRMED] — premise corrected |
| **F-7** | `TranslationService` **logs nothing**. A P2 incident on the default (no DeepL key) path writes **zero** lines, so "Copy error report" answers with *"No errors logged"* (`MainWindow.xaml.cs:317`). | [CONFIRMED] |
| **F-8** | Read-once shows **"Done — N line(s) translated."** after a *total* translation failure (`MainWindow.Ocr.cs:243-244` + `:296-299` returns instead of throwing) — a false success that invites manual retries at 3 requests each. | [CONFIRMED] |

**Accepted, not a finding** (owner's decision, re-verified at this commit): the unfiltered
`catch (OperationCanceledException) { throw; }` at `Services/TranslationService.cs:104` — it rethrows rather than
swallowing, so a timeout on line *k* discards the *k−1* successes of that loop but cannot mask a failure as a success.

---

## 1. Symptom → code mapping, proven

### 1.1 The string, character by character

Owner's verbatim report (2026-09-06):

```
(Google is limiting translations right now - wait a minute and try again.)
```

The code's literal, `Services/TranslationService.cs:145-146`:

```csharp
if (code == 429 && attempt == 2)
    throw new TranslationException(
        "Google is limiting translations right now — wait a minute and try again.");
```

| Element | Verdict | Grade |
|---|---|---|
| Body text | Byte-identical to `TranslationService.cs:146` apart from the dash | [CONFIRMED] |
| Dash | Code has **U+2014 em dash** (`—`); the owner typed **U+002D hyphen** (`-`). Only **one** string in the repo contains "wait a minute", and it is this one — so the difference is a transcription artefact of the owner's report, not a second producer. | [INFERRED], high |
| Enclosing parentheses | **Not** part of the exception message. Added by the caller. | [CONFIRMED] |
| Reachability | `E2` is reachable **only** from `HTTP 429 on attempt index 2` (`:144`). Every other status leaves via `:143` (non-transient), `:148` (5xx terminal) or `:175-176` (non-JSON). | [CONFIRMED] |

### 1.2 Who adds the parentheses — exhaustive

`grep "Friendly(ex)"` over all `*.cs` returns exactly seven sites:

| Site | Format | Surface | Parenthesised? |
|---|---|---|---|
| `MainWindow.Live.cs:281` | `it.TranslationBody = $"({Friendly(ex)})"` | **LIVE feed row** | **YES** |
| `MainWindow.Ocr.cs:298` | `it.TranslationBody = $"({Friendly(ex)})"` | **read-once feed row** | **YES** |
| `MainWindow.Live.cs:238` | `"Live hiccup ({…}) — retrying…"` | LIVE status line | parens, but with the `Live hiccup` prefix |
| `MainWindow.Live.cs:235` | `"Live stopped after repeated errors ({…})."` | LIVE status line | parens, but with the prefix |
| `MainWindow.Ocr.cs:248` | `"OCR failed: {…}"` | read-once status | no |
| `MainWindow.Translate.cs:106` | `"Failed: {…}"` | Translator tab status | no |
| `MainWindow.Translate.cs:41` | bare `Friendly(ex)` → `CompactOverlay.xaml.cs:142` renders `⚠ {r.Error} — your text is kept…` | overlay quick reply | no — **`⚠` prefix, no parens** |

**Conclusion.** A string that is *exactly* `(<message>)` with nothing before it can only come from
`MainWindow.Live.cs:281` or `MainWindow.Ocr.cs:298` — the **OCR feed (LIVE or read-once)**.
The Translator tab and the compact overlay reply are **excluded by their own formatting**. **[CONFIRMED]**
This confirms the owner's (b) mapping.

### 1.3 LIVE vs read-once

| Discriminator | LIVE | Read-once | Evidence |
|---|---|---|---|
| Can produce the parenthesised row | yes | yes | `Live.cs:281` / `Ocr.cs:298` |
| Can be triggered **seconds after launch** without any deliberate OCR action | **yes** — the saved region survives restarts (`Services/SettingsService.cs:66`), `ApplySettings` shows the Resume button (`MainWindow.xaml.cs:198` → `Live.cs:21-25`), and **Ctrl+Alt+L** is registered at `OnSourceInitialized` (`MainWindow.xaml.cs:472`) | only via an explicit button/Ctrl+Alt+R | [CONFIRMED] |
| Produces errors **continuously while the user does something else** (e.g. typing) | **yes** — it is a background `while` loop (`Live.cs:179-245`) | no — one shot per press | [CONFIRMED] |
| Fills **many** rows with the same parenthesised text over time | yes | one burst of N rows | [CONFIRMED] |
| Accompanying status text | `Live hiccup (…) — retrying…` then `Live stopped after repeated errors (…)` | **`Done — N line(s) translated.`** (false success, see F-8) | `Live.cs:238,235`; `Ocr.cs:244` |
| Nothing translates automatically at launch | true for both — no `StartLive` call in the ctor / `OnWindowLoaded` (grep) | true | [CONFIRMED-ABSENT] |

**Verdict: LIVE.** "Sometimes at launch" = the user resumed the saved region within seconds of the window appearing
(Resume button or Ctrl+Alt+L). "Sometimes while typing" = LIVE was already running in the background while the user
typed; the typing surfaces themselves cannot render parentheses. **[INFERRED], high confidence.**
Read-once remains a **secondary contributor**: each press is 3 more rejected requests, and F-8's false "Done" makes
repeated presses likely. **[INFERRED]**

### 1.4 The path, end to end

```
LiveLoop tick                          MainWindow.Live.cs:179-245
 └ capture + OCR + LooksLikeText       :187-194
 └ LiveDedup.Next → confirmed          :201     (2-frame confirm, Services/LiveDedup.cs:84-97)
 └ if confirmed.Count > 0              :203
    └ AppendLinesToHistory             :207
       └ rows created, TranslationBody = "…"          :259-269
       └ TranslateBodiesAsync                         :275 → :296-325
          └ slang Expand BEFORE translation           :302
          └ split ru / auto by IsProbablyRussian      :306-310
          └ _readTranslator.TranslateLinesAsync(ru)   :315   ← CachingTranslator(TranslationService)
          │   └ cache: serve hits, ask only misses    CachingTranslator.cs:50-58
          │      └ TranslationService.TranslateLinesAsync   TranslationService.cs:73-116
          │         └ joined ≤ 1500 B → ONE batched request  :80-88
          │            └ RequestAsync                        :119-178
          │               ├ attempt 0: HTTP 429 → Task.Delay(300)   :139,153
          │               ├ attempt 1: HTTP 429 → Task.Delay(600)   :139,153
          │               └ attempt 2: HTTP 429 → throw E2          :144-146
          │         └ catch (TranslationException) { throw; }        :91   ← NOT downgraded to per-line
          └ (auto group at :318-321 is NEVER reached — the ru await already threw)
       └ catch (Exception ex) → every pending row = $"({Friendly(ex)})"  :281
       └ throw;                                                          :282
 └ catch (Exception ex)                :228
    └ ++consecutiveErrors >= 5 ? StopLive() + "Live stopped after repeated errors (…)"  :231-236
    └ else SetScreenStatus("Live hiccup (…) — retrying…")                :238
 └ wait = Math.Max(150, interval − elapsed)   :242
```

All lines **[CONFIRMED]**.

### 1.5 Tick-by-tick, what the user sees when Google starts returning 429

Assumes LIVE running, default speed 92 % (700 ms), a chat producing new lines on most ticks.

| Tick | What the code does | What the user sees |
|---|---|---|
| **1** | ru batch: 429, 429, 429 over ~1.14 s → E2 → rows marked → rethrow → `consecutiveErrors = 1` | Rows that were `…` flip to **`(Google is limiting translations right now — wait a minute and try again.)`**; status: `Live hiccup (Google is limiting…) — retrying…`; the `●/○ LIVE` heartbeat keeps blinking |
| **2** | new lines confirmed → same 3 requests → `consecutiveErrors = 2` | more rows flip to the same parenthesised text; same status |
| **3–4** | idem → `consecutiveErrors = 3, 4` | the feed is now mostly parenthesised error rows |
| **5** | idem → `>= 5` → `Logging.Error("Live translation auto-stopped after 5 consecutive errors", ex)` (`Live.cs:233` — **the only log line P2 produces today**), `StopLive()` sets `"Live stopped."`, then `:235` overwrites it | **LIVE indicator disappears**, Stop button hides, status: **`Live stopped after repeated errors (Google is limiting translations right now — wait a minute and try again.)`** |
| **after** | nothing runs. The erroneous rows **stay** parenthesised forever — `LiveDedup` has already recorded those lines as emitted (`LiveDedup.cs:96`), so restarting LIVE will not re-translate them, and the cache never stored a failure (`CachingTranslator.cs:82-85`) so nothing is poisoned either | a feed of permanent error rows; pressing ▶ again starts a fresh loop that hits the same 429 |
| **but if the chat is calm** | any tick with `confirmed.Count == 0` reaches `consecutiveErrors = 0` (`Live.cs:219`) — **the counter resets** | **LIVE never auto-stops.** Each new message costs 3 rejected requests, indefinitely. This is F-5, and it is the shape that best matches "lasts 10 minutes, or never clears" |

**[CONFIRMED]** for every code reference; the tick narrative is **[INFERRED]** arithmetic over them.

### 1.6 The read-once variant

| Step | Code | Result |
|---|---|---|
| User presses "read once" / Ctrl+Alt+R | `MainWindow.Ocr.cs:207`, `:307` | `_readingOnce` guard prevents overlap (`:212`) |
| Capture + OCR + split | `:229-233` | N sentences |
| `TranslateSentencesInto` | `:243` → `:268-303` | rows created with `…` |
| `TranslateBodiesAsync(..., default)` | `:295` — **`ct = default`, nothing can cancel it** | ru batch: 3 × 429 over ~1.14 s → E2 |
| `catch (Exception ex)` | `:296-299` | every row = `$"({Friendly(ex)})"`, then **`return;` — no rethrow** |
| Back in `ReadRegionOnceAsync` | `:244` runs normally | status: **`Done — N line(s) translated.`** |

So read-once produces **the same parenthesised rows plus a status line claiming success** (F-8). Worst case the
call blocks for `3 × 12 s + 0.9 s ≈ 36.9 s` with no cancellation path (`TranslationService.cs:39` timeout,
`Ocr.cs:295` `default` token). **[CONFIRMED]**

---

## 2. Request-volume model of a LIVE session

### 2.1 Confirmed constants

| Constant | Value | `file:line` |
|---|---|---|
| Tick interval | `3000 − (speed/100)×2500` ms | `MainWindow.Live.cs:354-355` |
| Shipped default speed | 92 % → **700 ms** | `Services/SettingsService.cs:16`; pinned by `tests/PWRUHelper.Tests/DefaultsAndResizeTests.cs:28` |
| Interval range | 3000 ms (0 %) … **500 ms** (100 %) | `DefaultsAndResizeTests.cs:31-32` |
| Post-tick wait | `Math.Max(150, interval − elapsed)` → period `P = max(interval, elapsed + 150)` | `MainWindow.Live.cs:242` |
| Loop shape | strictly sequential `await` chain — **requests never overlap** | `MainWindow.Live.cs:179-245` |
| Requests per tick | 0 (no new line) · 1 (one language group) · 2 (both groups) | `MainWindow.Live.cs:313-322` |
| Batch budget | `MaxQueryBytes = 1500` UTF-8 bytes on the `\n`-joined lines | `Services/TranslationService.cs:35,81` |
| Retries | 3 attempts, `Task.Delay(300 × (attempt+1))` → 300 ms + 600 ms | `Services/TranslationService.cs:126,153` |
| Retryable statuses | 429 and 5xx only | `Services/TranslationService.cs:139` |
| Dedup layers | `LooksLikeText` → `LiveDedup` (2-frame confirm, re-emit only after 6 absent frames) → LRU cache 500 | `MainWindow.Live.cs:194,201`; `Services/LiveDedup.cs:32,84-87`; `Services/CachingTranslator.cs:24` |

### 2.2 Stated assumptions (all **[UNKNOWN]** until Amelia-QD measures them)

| ID | Assumption | Value used |
|---|---|---|
| A1 | capture + OCR filter + Windows OCR per tick | 120 ms |
| A2 | one **successful** Google round-trip | 150 ms |
| A3 | one **429** response (cheap rejection) | 80 ms |
| A4 | dedup delays each line by one tick and emits it exactly once | per `LiveDedup.cs:84-97` |
| A5 | 20 % of line-carrying ticks also carry a non-Cyrillic line → 2 groups | → factor 1.2 |
| A6 | worst case: no cache hits (a real PW-RU chat would hit often on recurring LFM/greeting lines) | 0 % hit rate |
| A7 | a Russian chat line ≈ 60 chars ≈ 110 UTF-8 bytes | → batch holds ≈ **13 lines** before the 1500 B budget is exceeded |

### 2.3 The table

Default speed 92 % (700 ms) unless stated. "req/min" = HTTP requests reaching `translate.googleapis.com`.

| # | Scenario | New lines/min | Tick period P | Ticks/min | Req/tick | **Req/min** | Notes |
|---|---|---|---|---|---|---|---|
| S1 | **Calm chat** — 1 new message/min | 1 | max(700, 270+150) = **700 ms** | 85.7 | 0 on 84.7 ticks, 1 on 1 | **≈ 1** | the three dedup layers make silence free |
| S2 | **Active chat** — 1 new line / 2 s | 30 | 700 ms | 85.7 | 1–2 on ~30 ticks | **≈ 36** | batching absorbs bursts |
| S3 | **Busy chat** — 3 new lines/s | 180 | max(700, 420+150) = **700 ms** | 85.7 | 1–2 on nearly every tick | **≈ 103** (ceiling **171**) | ≈ 2.1 lines/batch — well inside the 1500 B budget |
| S3b | Busy chat, **speed 100 %** (500 ms) | 180 | max(500, 570) = **570 ms** | 105 | 1–2 | **≈ 126** (ceiling **210**) | the 500 ms floor stops binding once round-trips exceed 350 ms |
| S4 | **429 storm, busy chat** | 180 | max(700, 120+1140+150) = **1410 ms** | 42.6 | **3** (3 attempts on the *first* group; the second group is never reached) | **≈ 128, all rejected** | auto-stops after 5 ticks ≈ **7.1 s / ≈ 15 requests** |
| S4b | **429 storm, active chat** (~65 % of ticks carry a line) | 30 | 1410 ms | 42.6 | 3 on ~28 ticks | **≈ 83, all rejected** | 5 *consecutive* failing ticks has probability ≈ 0.65⁵ ≈ 0.12 per window → auto-stop takes tens of seconds to minutes |
| S4c | **429 storm, calm chat** | 1 | 700 ms (mostly idle ticks) | 85.7 | 3 per new message | **≈ 3, all rejected — indefinitely** | **auto-stop NEVER fires** (`Live.cs:219` resets on every empty tick). This is the shape that keeps a per-IP throttle alive |
| S5 | **Chunking bypass** — a tick confirming ≥ 14 Russian lines | — | tick stretches to ≈ 14 × 150 ms ≈ 2.1 s | — | **K sequential requests** (`TranslationService.cs:81` fails → straight to the per-line loop at `:100`) | one tick = **up to 2K** if both groups overflow | healthy state only: under a 429 the `rateLimited` latch (`:99-105`) stops the loop after the first line's 3 attempts |
| S6 | **Batch-count mismatch** (successful response, `parts.Length != lines.Count`, `:87`) | — | — | — | **1 + K** per group | worst single tick = **2 + 2K**; with K = 14 that is **30 requests in one tick** |
| S7 | Read-once press (any state) | — | — | — | 1–2 healthy, **3** under 429 | per press | no cancellation (`Ocr.cs:295`), false "Done" invites repeats |
| S8 | Translator tab / overlay Enter | — | — | — | 1 healthy, **3** under 429, +1 per 1500 B chunk (`TranslationService.cs:62-63`) | per Enter | different cache instance from LIVE (see §3, A5) |

All rows **[INFERRED]** — arithmetic over the confirmed constants of §2.1 with the assumptions of §2.2.

### 2.4 What the model says

1. **The request rate is bounded by the tick rate, not by the chat.** Batching means a 3-lines-per-second chat and a
   1-line-per-second chat converge on the same ≈ 100–170 req/min. Making the LIVE loop slower is a bigger lever than
   making the batches bigger. **[INFERRED]**
2. **A 429 does not reduce the load.** S3 → S4 goes from ≈ 103 to ≈ 128 requests/min: the tick slows down by 2× but
   each tick sends 3× as many requests. **The app pushes harder precisely when Google has said stop.** **[INFERRED]**
3. **The dangerous regime is the quiet one** (S4c): a small, permanent trickle of rejected requests that never trips
   the auto-stop and never lets a per-IP counter decay. **[INFERRED]** — this is the best code-side explanation for
   "lasts 10 minutes, or never clears" (F-5).
4. **The 1500-byte budget is not a throttle, it is a cliff.** Below ≈ 13 lines/tick it batches (1 request); at 14 it
   silently switches to 14 requests (S5). **[CONFIRMED]** behaviour, **[UNKNOWN]** how often real chat crosses it.

---

## 3. What the current code does that worsens a per-IP throttle

Ordered by modelled impact.

| # | Amplifier | Evidence | Modelled cost | Grade |
|---|---|---|---|---|
| **A1** | **×3 retry on 429, fixed 300/600 ms, no jitter, `Retry-After` never read.** The header is not accessed anywhere in the repo (grep). | `Services/TranslationService.cs:126,139,144,153` | **3× the request count** on every throttled call; two instances behind the same NAT retry in lockstep (no jitter) | [CONFIRMED] |
| **A2** | **The 5-error auto-stop resets on empty ticks**, so it never fires in a calm chat. | `MainWindow.Live.cs:219` (reset) vs `:231` (trip) | S4c: an unbounded trickle of 429s for the whole session | [CONFIRMED] |
| **A3** | **No cool-down, no circuit breaker, no rate limiter of any kind.** No state survives a single `RequestAsync` call; the only cross-call state is the local `bool rateLimited` inside one `TranslateLinesAsync` (`:99`). | `Services/TranslationService.cs` (whole file), `Services/CachingTranslator.cs` (whole file) — **[CONFIRMED-ABSENT]**; independently confirmed by Phase 0 F6 | the next tick, the next press and the next Enter all fire immediately after a 429 | [CONFIRMED] |
| **A4** | **The app keeps sending through 4 failing ticks** before it even considers stopping — ≈ 12 rejected requests minimum in a busy chat. | `MainWindow.Live.cs:231` | 12–15 wasted requests per storm, best case | [CONFIRMED] |
| **A5** | **Two independent caches.** The read chain (`new CachingTranslator(new TranslationService())`) and the write chain (`BuildTranslator()`) are separate instances, so a line the feed already translated is re-fetched when the user types it — and saving a DeepL key throws the write cache away. | `MainWindow.xaml.cs:43` vs `MainWindow.Translate.cs:229-232,239` | duplicate requests for identical text; magnitude **[UNKNOWN]** | [CONFIRMED] |
| **A6** | **No persistent cache.** The LRU is in-memory, capacity 500, no TTL, no file, dies with the process. PW-RU chat is dominated by recurring greetings and LFM spam — and **restarting the app is exactly what a blocked user does**, which throws away the one thing that was reducing traffic. | `Services/CachingTranslator.cs:20-29,103-126` (no serialization anywhere) | every session re-translates the same recurring lines from zero | [CONFIRMED] |
| **A7** | **Read-once reports a false success** and cannot be cancelled, inviting manual retries at 3 requests each. | `MainWindow.Ocr.cs:243-244`, `:295`, `:296-299` | user-driven amplification; magnitude **[UNKNOWN]** | [CONFIRMED] |
| **A8** | **`ru` and `auto` are always two separate HTTP requests**, even when both groups are tiny. | `MainWindow.Live.cs:313-322` | up to 2× the requests per tick | [CONFIRMED] |
| **A9** | *Note only (Mary's call).* Fixed, ageing spoofed UA — `Chrome/120.0` — with **no `Accept`, no `Accept-Language`, no `Referer`, no cookies**. A header set no real Chrome would send. | `Services/TranslationService.cs:41-42` | effect on bot-scoring **[UNKNOWN]** | [CONFIRMED] fact / [UNKNOWN] effect |
| **A10** | *Note only (Mary's call).* Unofficial `translate_a/single?client=gtx` endpoint, `GET` with the user text in the query string, single process-lifetime `static HttpClient`, HTTP/1.1 default, `PooledConnectionLifetime` never set, system proxy inherited silently. | `Services/TranslationService.cs:32,37-44,122-123,131` | endpoint-specific throttling characteristics **[UNKNOWN]** | [CONFIRMED] fact / [UNKNOWN] effect |
| **A11** | **The 1500 B cliff** (S5) and the **mismatch explosion** (S6) can turn one logical translation into 14–30 requests inside a single tick. | `Services/TranslationService.cs:81,87,95-108` | burst spikes; frequency **[UNKNOWN]** | [CONFIRMED] |

### 3.1 Correction to Phase 0

`00-inventaire-stack.md` §13 item 18 says *"failure placeholders are never cached, so every failing line is
re-requested on the next tick — potentially deepening a rate-limit."* **That is not what happens on the LIVE path.**

`LiveDedup.Next` records every *emitted* line in `_seen` (`Services/LiveDedup.cs:91-97`) **before** the translation is
attempted; while the message stays on screen, `_tick − e.LastSeen <= 6` keeps it suppressed (`:76-77`). A line whose
translation failed is therefore **not re-sent** — the cache's refusal to store failures never comes into play.

The consequence is different, and arguably worse for the user: **the row is burned.** It keeps
`(Google is limiting translations right now — wait a minute and try again.)` for the rest of the session, and the
message is never re-translated even after Google recovers. **[CONFIRMED]** — this belongs in Phase 2's requirements
(retry-after-recovery), not in the amplifier list.

Where the "not cached → re-requested" effect **is** real: the Translator tab, the overlay reply and read-once, where
the user retries by hand (A7), and across restarts (A6).

---

## 4. What the current code does right (Phase 2 must keep this)

| # | Behaviour | Evidence | Why it matters |
|---|---|---|---|
| **R1** | **Three independent dedup layers** — `LooksLikeText` (min letters), `LiveDedup` (letter/digit signature, 2-frame confirmation, re-emit only after 6 absent frames), then the LRU cache. A calm chat costs **zero** requests. | `MainWindow.Live.cs:194,201`; `Services/LiveDedup.cs:32,84-97`; `Services/CachingTranslator.cs:50-54` | the single biggest reason the app is not already permanently blocked |
| **R2** | **Batch join** — N new lines become **one** request when they fit in 1500 B. | `Services/TranslationService.cs:80-88` | turns a 13-line burst into 1 request |
| **R3** | **A `TranslationException` from the batch is rethrown, never downgraded to per-line.** | `Services/TranslationService.cs:91` | a 429 does **not** explode into N per-line requests — a deliberate, load-bearing choice |
| **R4** | **The `rateLimited` latch** stops the per-line loop after the first failure, keeping earlier successes. | `Services/TranslationService.cs:99,102,105` and the comment at `:95-97` | bounds the S5/S6 explosion under a throttle |
| **R5** | **Successes only are cached**; anything starting with `(` is refused. | `Services/CachingTranslator.cs:82-85`; test `tests/PWRUHelper.Tests/CachingTranslatorTests.cs:97-109` | a transient failure can never get stuck on screen |
| **R6** | **`when (ct.IsCancellationRequested)` on every OCE catch** — the LIVE loop distinguishes a real Stop from a 12 s HTTP timeout, so a timeout can never leave a zombie `LIVE` indicator. | `MainWindow.Live.cs:227` (+ comment `:221-226`); `Services/FallbackTranslator.cs:26,38`; test `tests/PWRUHelper.Tests/TranslationBackendTests.cs:98-109` | the project's most expensive past bug, correctly handled |
| **R7** | **Auto-stop after 5 consecutive errors** — right idea, wrong trigger (A2). Keep the mechanism, fix the condition. | `MainWindow.Live.cs:231-236` | the only self-protection that exists today |
| **R8** | **Strictly sequential loop** — capture → OCR → translate → delay are all awaited in one body, so requests **never overlap** and never form a parallel burst. | `MainWindow.Live.cs:179-245` | keeps the ceiling at 2/tick instead of unbounded |
| **R9** | **The cancellation token reaches `HttpClient`** on the LIVE path, so pressing Stop aborts in-flight requests. | `MainWindow.Live.cs:109-110,207,315,320` → `TranslationService.cs:131` | Stop actually stops the traffic |
| **R10** | **The cache asks the inner translator only for misses** and splices by index. | `Services/CachingTranslator.cs:42-69`; test `CachingTranslatorTests.cs:69-94` | the request carries only never-seen lines |
| **R11** | **DeepL is never used by the OCR feed**, protecting a metered quota from an unmetered loop. | `MainWindow.xaml.cs:37-43`; `MainWindow.Translate.cs:222-225,293-295` | a deliberate constraint, see §6.2 |

---

## 5. Observability gap

### 5.1 What exists today

| Log site | Fires when | Content |
|---|---|---|
| `Services/FallbackTranslator.cs:29,41` | **only when a DeepL key is set** and DeepL failed | `"Primary translator failed, using fallback: " + ex.Message` |
| `MainWindow.Live.cs:233` | LIVE auto-stopped after 5 consecutive errors | `Logging.Error(...)` with exception type + full `ToString()` (`Services/Logging.cs:47`) |
| **nothing else** | — | `Services/TranslationService.cs` contains **no** `Logging.*` call (grep) — **[CONFIRMED-ABSENT]** |

**Net effect:** on the default no-key configuration, a P2 incident writes **zero** log lines unless LIVE auto-stops —
and per F-5 it often does not. `CopyErrorReport_Click` then shows the toast *"No errors logged — nothing to copy 🙂"*
(`MainWindow.xaml.cs:314-318`). **The About tab cannot evidence P2 today.** **[CONFIRMED]**

### 5.2 The minimal diagnostic line Phase 3 should story

One `Logging.Warn` per **non-success or exceptional** HTTP attempt (successes counted, not logged line-by-line — the
log is capped at 1 MB with one rollover, `Services/Logging.cs:69,95-105`). `LogWriter` already prefixes
`yyyy-MM-dd HH:mm:ss` (`Services/Logging.cs:85`).

| Field | Value | Why |
|---|---|---|
| timestamp | already prefixed; **add milliseconds** | to read the 300/600 ms retry spacing |
| provider | `google` \| `deepl` | two backends share the log |
| endpoint | host + `client=` param only (`translate.googleapis.com client=gtx`) | **never the full URL** — it carries the user's text in `q=` |
| direction | `sl→tl` (e.g. `ru→en`) | discriminates the ru/auto groups |
| attempt | `1/3`, `2/3`, `3/3` | proves the ×3 amplification in the field |
| correlation id | short id shared by the 3 attempts of one logical call | makes one call readable as one event |
| status | HTTP code, or exception type name for a transport failure | **discriminates E1/E2/E3/E4/E5/E6 without asking the user** |
| elapsed | ms for this attempt | distinguishes a fast 429 from a 12 s timeout |
| **`Retry-After`** | header value verbatim, or `-` | currently never read anywhere; the single highest-value new datum |
| other headers | presence + value of `X-RateLimit-*`, `Via`, `Server`; presence-only flag for `Set-Cookie` | proxy / interstitial detection |
| content-type | e.g. `application/json` vs `text/html` | separates a real 429 from a captcha page |
| response length | bytes | cheap anomaly signal |
| body head | **first 120 chars of a non-JSON body only**, whitespace-collapsed | identifies the block page or the proxy |
| payload shape | request **byte count** and **line count** only | to correlate with the 1500 B cliff |
| burst counter | requests issued in the trailing 60 s | turns the log into evidence for §2's model |
| **never** | the user's text, `q=`, the DeepL key, the full URL | privacy; the log is pasted to Discord by design (`MainWindow.xaml.cs:321`) |

**Where it plugs in** (all inside `RequestAsync`, `Services/TranslationService.cs:119-178`):

| Insertion point | `file:line` |
|---|---|
| non-success branch, **before** the throw decisions | `Services/TranslationService.cs:138` (after `int code = …`, before `:140`) |
| transport failure | `Services/TranslationService.cs:151` (the `catch (HttpRequestException) when (attempt < 2)` body, plus a new catch for the escaping 3rd attempt) |
| non-JSON body | `Services/TranslationService.cs:172` (the `catch (JsonException)` body) |
| success (**counter only**, or a verbose flag) | `Services/TranslationService.cs:132-135` |
| a matching line in DeepL for symmetry | `Services/DeepLTranslator.cs:99-105` |

All would go through the existing `Services/Logging.cs:44` (`Logging.Warn`) — no new infrastructure, and
`Logging.DirectoryOverride` (`Services/Logging.cs:31-39`) already keeps the test suite out of the real `%AppData%`.

### 5.3 What "Copy error report" would show afterwards

For a real P2 incident it would contain, for each failing call: the three attempt lines 300/600 ms apart, all
`status=429`; whether Google sent a `Retry-After` and its value; the burst counter for the preceding minute
(directly comparable to §2.3); the direction and the payload size; and, if the body was HTML, its first 120
characters. That is enough to settle Q2.1, Q2.2, Q2.8 and most of §2's `[UNKNOWN]` assumptions **from a single
user paste**, with no user text leaving the machine. **[INFERRED]** — follows from the field list above.

---

## 6. Constraints for the target architecture (input to Winston's CA)

### 6.1 What must stay

| Constraint | Evidence | Note |
|---|---|---|
| **Code-behind, no MVVM** | `project-context.md` | non-negotiable project decision |
| **`ITranslator` shape** — 2 methods, `ct` last, `Task<string>` / `Task<List<string>>` | `Services/TranslationService.cs:18-23` | every layer composes on it; new behaviour should be **decorators**, not changes to the interface |
| **`CachingTranslator` stays a decorator over ANY inner translator** | `Services/CachingTranslator.cs:15-29` | the existing composition pattern |
| **Successes-only caching** (`(` prefix = failure) | `Services/CachingTranslator.cs:82-85`; test `CachingTranslatorTests.cs:97-109` | a persistent cache must keep this rule |
| **OCR picks the source per message** (`IsProbablyRussian` → `ru`, else `auto`) | `MainWindow.Live.cs:306-310` | any request-merging must not lose per-message source selection |
| **Slang expansion happens BEFORE translation; the displayed original and the 🔑 line stay raw** | `MainWindow.Live.cs:298-302`; `MainWindow.Translate.cs:89-93` | the cache key is the **expanded** text — consistent today, must stay consistent |
| **No WPF `Clipboard`** | `Services/ClipboardService.cs`; `MainWindow.xaml.cs:373-389` | untouched by P2, but any new copy path must obey |
| **The OCE trap filter** — every `catch (OperationCanceledException)` in the pipeline and the LIVE loop filters `when (ct.IsCancellationRequested)` | `Services/FallbackTranslator.cs:26,38`; `MainWindow.Live.cs:227`; `Services/DeepLTranslator.cs:80` | any new decorator that catches broadly **must** carry the same filter, or a timeout becomes a phantom cancel |
| **`Services/` stays UI-free** | `project-context.md` | a circuit breaker belongs in `Services/`, not in `MainWindow.*` |
| **Changed defaults reach existing users only via `SettingsVersion` + `Migrate`** | `Services/SettingsService.cs:102,121,133-159` | a new "max requests/min" or "LIVE interval floor" default needs a migration, or existing users keep the old value |
| **Settings-restore re-entrancy** — new persisted controls must respect `_restoringSettings` | `MainWindow.xaml.cs:35`, and the comment at `:28-34` | if Phase 2 adds a UI knob |

### 6.2 What the OCR path forbids today — and the stated reason

**DeepL is write-path only, by design.** Two chains coexist:

| Chain | Built at | Composition | Used by |
|---|---|---|---|
| `_readTranslator` | `MainWindow.xaml.cs:43` (field init, `readonly`, never rebuilt) | `CachingTranslator(TranslationService)` — **Google only, always** | read-once + LIVE (`MainWindow.Live.cs:315,320`) |
| `_writeTranslator` | `MainWindow.xaml.cs:82` → `MainWindow.Translate.cs:226-233` | key set → `CachingTranslator(FallbackTranslator(DeepL, Google))`; else `CachingTranslator(Google)` | Translator tab (`Translate.cs:94`), overlay reply (`Translate.cs:37`) |

The code states the reason three times, in comments:

- `MainWindow.xaml.cs:40-41` — *"`_readTranslator` — the OCR feed (read-once + live). **ALWAYS the free Google engine**: a live loop translates every new chat line and would drain a DeepL quota fast."*
- `MainWindow.Translate.cs:223-225` — *"The screen-reading side never comes through here … because a live loop translating every new chat line would eat a DeepL quota in one session."*
- `MainWindow.Live.cs:293-295` — *"Always goes through `_readTranslator` (free Google) — never DeepL, whose quota a live loop would burn through in an evening."*

**[CONFIRMED].** Consequence for Phase 2: **the OCR feed has no second provider today**, so a per-provider circuit
breaker on that path has nowhere to fail over unless (a) a free/unmetered alternative is added, or (b) the owner
revisits the DeepL rule — the latter is an **owner decision**, not an architecture one. Note the arithmetic that
motivates it: §2.3 S3 at ≈ 103 req/min × ~2 lines × ~110 bytes ≈ **1.4 MB of characters per hour** of busy chat,
against a DeepL free tier measured in 500k characters **per month**. **[INFERRED]**

### 6.3 Seams — where Phase 2 changes plug in

| Change | Seam (`file:line`) | Note |
|---|---|---|
| Honour `Retry-After`; exponential backoff + jitter; cap attempts | `Services/TranslationService.cs:138-153` | fully private and local — the smallest possible blast radius |
| **Per-provider circuit breaker / cool-down** | a new `ITranslator` decorator inserted in **both** compositions: `MainWindow.xaml.cs:43` and `MainWindow.Translate.cs:229-232` | the two chains are **separate instances**, so the breaker's state must be shared (static, or one injected instance) — otherwise the Translator tab keeps hammering while LIVE is paused |
| Client-side rate limiter (min interval / token bucket) | same decorator seam, **or** immediately before `Http.GetAsync` at `Services/TranslationService.cs:131` | the decorator keeps `Services/` composable; the inner placement also covers the per-line loop |
| Debounce / coalesce batches across ticks | `MainWindow.Live.cs:203-207` (the `confirmed.Count > 0` branch) | accumulate confirmed lines and flush every N ms instead of every tick |
| Merge the `ru`/`auto` requests | `MainWindow.Live.cs:313-322` | halves req/tick, but must preserve per-message source selection (§6.1) |
| Adaptive tick interval under errors (back off the loop, not just the request) | `MainWindow.Live.cs:242` + `CurrentLiveIntervalMs()` `:357` | `LiveIntervalMs` is `internal static` and already unit-tested |
| Fix the auto-stop trigger (F-5) | `MainWindow.Live.cs:219` (the unconditional reset) and `:231` | e.g. reset only on a tick that **translated successfully**, and/or count errors in a time window |
| Retry burned rows after recovery (F-6) | `MainWindow.Live.cs:281` + `Services/LiveDedup.cs:91-97` | needs a "failed, retry later" state the dedup does not swallow |
| **Persistent cache** | `Services/CachingTranslator.cs:24-29` (load) and `:103-126` (`Store`) | keep the `(`-prefix rule; write under `%AppData%\PWRUHelper\` like `Services/SettingsService.cs` |
| **One shared cache for read + write** | `MainWindow.xaml.cs:43` and `MainWindow.Translate.cs:232` | pass one `CachingTranslator` instance instead of constructing two; note `Translate.cs:239` currently drops the write cache on key save |
| Per-request diagnostic logging | `Services/TranslationService.cs:132-153,172-177` (§5.2) | via the existing `Services/Logging.cs:44` |
| Honest read-once status | `MainWindow.Ocr.cs:296-299` (return → rethrow or return a status) + `:243-244` | fixes F-8 / A7 |
| HTTP client configuration (headers, HTTP version, `PooledConnectionLifetime`, proxy) | `Services/TranslationService.cs:37-44` | single factory method, one place |
| **Testability seam (prerequisite for TDD)** | `Services/TranslationService.cs:32,37-44` | the `static readonly HttpClient` is not injectable; a ctor overload taking an `HttpMessageHandler` is the minimal change that makes the retry loop and the batch→per-line path unit-testable |

### 6.4 The test surface that exists

Suite: `tests/PWRUHelper.Tests` — 26 `.cs` files, ≈ 256 executable cases, headless-safe, run on every PR
(`project-context.md`; counts re-verified). Translation-relevant files:

| File | Cases | Covers | Reusable for |
|---|---|---|---|
| `tests/PWRUHelper.Tests/CachingTranslatorTests.cs` | 7 | LRU eviction (`:112`), key normalisation (`:57`), per-target keys (`:45`), **misses-only batching** (`:69`), fully-cached batch (`:83`), **placeholders never cached** (`:97`) | persistent cache, shared cache — the `CountingTranslator` double at `:10-28` counts calls, exactly what a rate-limit test needs |
| `tests/PWRUHelper.Tests/TranslationBackendTests.cs` | 10 (2 classes) | DeepL code mapping + parse (`:9-46`); `FallbackTranslator` primary/fallback (`:64-82`), **real cancel propagates** (`:84`), **timeout-OCE falls back** (`:98`) | the `Fake : ITranslator` pattern at `:51-62` is the template for testing any new decorator (breaker, limiter) |
| `tests/PWRUHelper.Tests/ServicesTests.cs:75-95` | 2 | `TranslationService.ChunkText` byte budget + content preservation | the 1500 B cliff (S5) |
| `tests/PWRUHelper.Tests/LiveDedupTests.cs` | 8 | 2-frame confirmation (`:20`), single translation of a stable message (`:28`), emoji flicker (`:39`), brief flicker (`:51`), re-send after scroll-off (`:62`), orphaned fragment (`:84`), pure noise (`:99`) | feeding a frame sequence and asserting **how many requests it would issue** is a short step from here |
| `tests/PWRUHelper.Tests/LiveDefaultsTests.cs` | 7 | the shipped defaults' end-to-end dedup behaviour, incl. a documented `KNOWN_LIMITATION` (`:152`) | regression guard for any dedup change |
| `tests/PWRUHelper.Tests/DefaultsAndResizeTests.cs:16,28-32` | — | pins `LiveSpeedPercent = 92` and `LiveIntervalMs` → 700 / 3000 / 500 | any change to the tick cadence must update this deliberately |
| `tests/PWRUHelper.Tests/LoggingTests.cs` | 6 | `LogWriter` write/read/roll, the real-`%AppData%` guard (`:89`), `DirectoryOverride` (`:101`) | the new per-request log line is testable through `Logging.DirectoryOverride` |

**Gaps — [CONFIRMED-ABSENT]:**

1. **No test of `TranslationService.TranslateLinesAsync`** — the batch → per-line path, the `rateLimited` latch, and
   the `catch (TranslationException) { throw; }` at `:91` are all untested, because the class is only reachable
   through real HTTP today.
2. **No test of the retry policy** (`:126-153`) — attempt count, delay sequence, which statuses retry.
3. **No fake `HttpMessageHandler` anywhere in the suite** — nothing can simulate a 429.
4. **No request-counting / rate test**, and no performance or network integration test at all.

⇒ Gaps 1–3 are all unlocked by the single seam in §6.3 (an injectable handler on `TranslationService`). Phase 2's
behaviour — backoff, `Retry-After`, breaker, limiter — is otherwise untestable, which for this project means it
would be unshippable.

---

## 7. Open questions

### 7.1 For Mary (external research, `mecanismes-de-blocage-google.md`)

| ID | Question | Grade | What would settle it |
|---|---|---|---|
| **M1** | Does `translate_a/single?client=gtx` return a **`Retry-After`** header on 429, and does it carry a usable value? | [UNKNOWN] — **highest value**: it decides whether Phase 2 can honour a server-stated delay or must guess one | dated research + one capture from Amelia-QD's script |
| **M2** | What request volume triggers a per-IP throttle on this endpoint, and **how long does it last** (minutes / hours / days)? Compare against §2.3: is 100–170 req/min already over the line, or is the trigger the *retry* pattern rather than the rate? | [UNKNOWN] | dated research + P4 below |
| **M3** | Does the fixed `Chrome/120.0` UA **without `Accept` / `Accept-Language`** (`Services/TranslationService.cs:41-42`) raise the block probability versus a plain .NET UA or a complete browser header set? | [UNKNOWN] (= Phase 0 Q2.4) | dated research + an A/B capture |
| **M4** | Is the throttle keyed on the IP, the IP range, the UA, or a session cookie? Does CGNAT / a shared ISP pool make a single user inherit a neighbour's block? | [UNKNOWN] | research + probe P5 |
| **M5** | Which **free / unmetered** providers could serve the OCR feed as a fallback, given DeepL is forbidden there (§6.2)? Limits, terms, Russian quality. | [UNKNOWN] | `benchmark-fournisseurs.md` |
| **M6** | Do other `client=` values (or the official paid API's free tier) behave differently for throttling? | [UNKNOWN] | research |
| **M7** | **Answered by the owner's report, record it:** does the 429 path fire in the wild at all? Phase 0 Q2.8 wondered whether Google blocks with 403+HTML instead. The verbatim string is E2, which is reachable **only** from a 429 on attempt 3 (`Services/TranslationService.cs:144-146`). → **429 is real for this app.** | **[CONFIRMED]** — Q2.8 closed | — |

### 7.2 For Amelia-QD (reproduction / probe — she owns every request to Google)

| ID | Probe | Grade | Why |
|---|---|---|---|
| **P1** | Instrumented build (branch only) emitting the §5.2 log line; capture a real incident: the three 429s, their spacing, `Retry-After`, content-type, and the trailing-60 s burst counter. | [UNKNOWN] — **blocking**; nothing else can be confirmed without it | closes Q2.1/Q2.2/M1 from a single user paste |
| **P2** | Ask the affected users: **was LIVE running?** Did they see the blinking `● LIVE` indicator, a `Live hiccup (…) — retrying…` status, or `Live stopped after repeated errors (…)`? | [UNKNOWN] | turns F-2 from [INFERRED] into [CONFIRMED], and distinguishes S4 from S4c |
| **P3** | Measure the real per-tick timings (capture, OCR, HTTP round-trip, 429 round-trip) to replace assumptions **A1–A3** of §2.2. | [UNKNOWN] | every §2.3 number depends on them |
| **P4** | Measure the **actual block duration**: after a block, poll once per 60 s from the same IP and record when it clears. | [UNKNOWN] | the "1 min / 10 min / never" spread is the least understood part of P2 |
| **P5** | **Same machine, different IP** (mobile hotspot): does the block follow the IP or the machine? | [UNKNOWN] | directly tests the owner's (c) conclusion (per-IP, outside the process) |
| **P6** | Cache-hit rate over a real PW-RU evening: how many requests would a **persistent** cache (A6) actually save? | [UNKNOWN] | sizes the cheapest possible mitigation |
| **P7** | How often does the batch **count mismatch** (`Services/TranslationService.cs:87`) fire on real chat, and how often does a tick exceed the ~13-line batch budget (S5)? | [UNKNOWN] | decides whether A11 is a real amplifier or a theoretical one |
| **P8** | Does the process-lifetime `static HttpClient` (`Services/TranslationService.cs:32`, `PooledConnectionLifetime` never set) hold a poisoned connection? The owner reports **restart does not clear it**, which argues against this — but a hotspot test (P5) separates the two hypotheses cleanly. | [INFERRED — probably refuted by the owner's (c)] | closes Phase 0 Q2.6 |

---

_End of `02-traduction/analyse-implementation-actuelle.md`. Next in Phase 1: `mecanismes-de-blocage-google.md` (Mary)
and the reproduction script (Amelia-QD). Phase 2: `architecture-cible.md` (Winston) — §6 of this document is its
input._
