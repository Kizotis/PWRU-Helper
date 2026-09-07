# 02 — P2 · Degraded-mode UX — provider status, messages, keys

_Phase 2 · author: **Sally** (BMAD UX Designer), menu item **CU** (`bmad-ux`, adapted to a single file) ·
peer document to Winston's `architecture-cible.md` · baseline `4759712` (main, v0.14.0) · 2026-09-06 ·
**amended 2026-09-07** (A1–A12, see "Amendments" below — E7 implements from this file and no other)._

**What this document is.** The user-facing half of the translation rebuild: what a player sees when a provider
throttles, blocks, times out or runs out of quota; where the status lives on each surface; the exact words; the
About-tab settings for the two key slots and the offline engine; and the **P1 expectation copy** (the only UX lever
left on startup).

**Shape.** An EXPERIENCE spec only — behaviour, states, microcopy, flows, accessibility. No companion DESIGN.md:
the visual identity is frozen (`Theme.xaml`). **No i18n, no MVVM, no new theme/font/image/window.** Every control
named below exists today or is one `TextBlock` next to one that does. Not in scope: production code (nothing here
was implemented), the chain itself (Winston), the log format (`analyse-implementation-actuelle.md` §5.2).

**Grades.** **[CONFIRMED]** = read in code with `file:line` · **[DECIDED]** = owner's Phase 2 decision, `README.md`
of this folder · **[ASSUMED]** = my design judgement or arithmetic that has not been validated with a user.

**The chain this designs for** [DECIDED]:

```
read path  (Screen OCR: read-once + LIVE)
    [Azure, only if the user opts in]  →  Google dict-chrome-ex  →  Edge  →  Google gtx  →  [Bergamot, if installed]
write path (Translator tab + compact overlay quick reply)
    [DeepL key]  →  [Azure key]  →  Google dict-chrome-ex  →  Edge  →  Google gtx  →  [Bergamot, if installed]
```

Each provider has a **gate** (circuit breaker): a 429/403 pauses it 60 s, doubling, capped at 30 min, and the gate
exposes `retryAt`. When every provider in a path is paused, the path returns one `AllProvidersPaused(retryAt)`
outcome. Errors are typed: `RateLimited`, `Blocked`, `Unavailable`, `Timeout`, `Network`, `BadResponse`,
`QuotaExhausted`, `AuthFailed`, `Cancelled`.

---

## Amendments (2026-09-07) — the questions the implementation raised, settled

_Sally, after E1.S6, E2, E3, E4 and E5.S1–S4 shipped. Two things changed the ground under §3:
`ChainTranslator.LastOutcome` exists (R-3), so the app now **knows** which engine answered and which were
skipped; and `ProviderGate.Snapshot()` exists (R-2/OQ-c), so `{t}` is a real number. The deviations that were
parked waiting for exactly those two — **D1** (no `{P}`) and **D2** (no "trying another engine") — are therefore
settled here rather than deferred again, and **D5** is ruled._

**These amendments are binding for E7. Every row they touch is edited in place below** — implement from §2–§5,
not from this list; the list exists so a reviewer can see what moved and why in one place.

| # | What was open | Ruling | Edited in |
|---|---|---|---|
| **A1** | **D5** — `QuotaExhausted`, `RateLimited`, `Blocked` as shipped deviate beyond D1–D3, and `Blocked` answers none of principle 2's three questions | **All three are replaced.** New wording in §3.1; each names its engine, says how long, and writes the third answer instead of implying it | §3.1 |
| **A2** | No stable user-facing name per provider id (OQ-2) | `google-dict` → **Google** · `google-gtx` → **Google (backup)** · `deepl` → **DeepL** · `azure` → **Azure** · `edge` → **Edge** · `bergamot` → **Offline engine** (short form **Offline**). "Google (old)" is retired — a name should say a provider's *role*, not its age | §3.0, §2.3, §4.2 |
| **A3** | **D1** — restore `{P}`, and where | `{P}` comes from `LastOutcome.ProviderId` **only**; it is never guessed. Per-surface templates in §3.0 | §3.0, §3.1, §3.2, §3.4 |
| **A4** | **D2** — restore "— trying another engine" | **Not restored as written.** A §3.1 sentence renders only after the whole attempt has failed, so a promise of another engine would be false at the one moment it is read. The chain's progress is told by the chip and by §3.5's one-time notice; the tail of each sentence answers "must you act?" instead | §3.1 |
| **A5** | Feed rows still stamp the whole failure sentence (`({Friendly(ex)})`, `Live.cs:513`, `Ocr.cs:466`) | **A row never carries a §3.1 sentence, a provider name or a countdown.** Three row texts exist and no more: `…`, `(not translated — the engines did not come back)`, `(not translated — read cancelled)` | §2.2, §3.3a |
| **A6** | E5-c/E5-d — auto-stop is 5 consecutive *sent* failures, no window; §3.2's row still says "reads" and splits into two sentences | One sentence, with the count, the reason and the way back: see §3.2 | §3.2 |
| **A7** | E5-g — the paused read-once sentence now knows `{n}`, and the promise "they will fill in when one is back" is only true while LIVE is running | Two paused forms, forked on whether a retry queue is draining. Neither promises what the other delivers | §3.3 |
| **A8** | E5-g — only Ctrl+Alt+R can cancel a running read; both buttons are greyed | **The read-once button stays enabled and becomes the cancel** (label → `Cancel read`; overlay glyph → `■`, tooltip `Cancel read`). No new control, no new window. A hotkey named in a status line is the affordance nobody uses mid-raid, and a greyed button during a 30 s wait is what makes a player press again (amplifier A7) | §3.3, §4.3 |
| **A9** | OQ-c — the countdown polls at 1 Hz; `CountdownText` ships `58 s` / `2 min`, §2.4 asks for `0:58` / `about 4 min` | **§2.4's bands win** and `CountdownText` is amended in E7.S2. Exact paused templates for the main window and for the overlay (≤ 40 chars) in §3.2 | §2.4, §3.2 |
| **A10** | E4-c — the cache holds chat text and nothing tells the user | **One sentence in About and in the README, plus one `Clear cache` button.** Stating that chat text is stored while giving no way to remove it is the shape principle 2 forbids | §4.2, §4.3 |
| **A11** | §3.3's "lower-cased at the join" rule vs restored `{P}` | **The lower-casing rule now applies to the colon join only** (`Could not read the screen: …`). Every other join keeps the sentence's own capital, because with `{P}` restored the first word is usually an engine's name and "google asked us to slow down" is a typo, not a sentence | §3.1 |
| **A12** | Three of §3.1's sentences carry `{t}`, and `RetryAt` can legitimately be absent | **One substitution rule, not three sentences:** when `{t}` is unknown, `for {t}` renders `briefly` and `in {t}` renders `shortly`. No sentence is duplicated for the no-countdown case | §3.1 |

---

## 1. Principles

Five, in priority order. When two conflict, the lower number wins.

1. **One state, one voice.** A degraded state is announced **once per window**, in the status line — never repeated
   on every feed row, never phrased two different ways on two surfaces. Today the same sentence is stamped into
   every failed row (`MainWindow.Live.cs:281`, `MainWindow.Ocr.cs:298`) and again into the status line
   (`Live.cs:238`); that is the noise this rebuild removes.
2. **Say what it is, when it ends, and whether the user must act.** Every message answers three questions in one
   sentence. "Nothing you need to do" is a valid third answer and must be written, not implied.
3. **Never block the game.** No modal, no focus steal, no sound, no window activation for anything transient. A
   modal is allowed only for a deliberate, consequential, **user-initiated** choice (the offline-model download).
   Background loops never open dialogs — ever.
4. **Honest status only.** No "Done" over a total failure (today `MainWindow.Ocr.cs:243-244` does exactly that), no
   blinking LIVE dot while nothing is being sent, and no promise we cannot keep ("wait a minute" when we know the
   pause is four).
5. **Cheap on screen and on the machine.** At most **one countdown per window**, ticking at **1 Hz**, running only
   while something is actually paused. No animation beyond the existing 600 ms heartbeat
   (`CompactOverlay.xaml.cs:20`). Tiny footprint is a product requirement.

---

## 2. Provider status model → UI states

### 2.1 The eight states

| # | State | Entered when | User must act? |
|---|-------|--------------|----------------|
| **S1** | **Healthy** | The first provider of the path answered | no |
| **S2** | **Degraded — fallback active** | A lower provider answered because a higher one is paused or failed | no |
| **S3** | **Paused until** | The preferred provider is gated; something below it still serves | no |
| **S4** | **Offline model active** | Bergamot answered (installed + enabled) | no |
| **S5** | **All paused** | `AllProvidersPaused(retryAt)` | no — but offer the offline engine |
| **S6** | **No network** | `Network` from every provider (nothing resolves) | yes — check the connection |
| **S7** | **Key invalid** | `AuthFailed` on DeepL or Azure | yes — fix or clear the key |
| **S8** | **Quota exhausted** | `QuotaExhausted` on DeepL or Azure | no (falls back), but tell them |

`Cancelled` is **not a state**: it is the user pressing Stop or closing. It must render **nothing at all** — no row
text, no status change beyond the normal "Live stopped." [ASSUMED, but load-bearing: this is the OCE trap wearing a
UX hat, and `Services/FallbackTranslator.cs:26,38` already proves the code half.]

### 2.2 What each surface shows

The four surfaces, with the code that owns them today:

| Surface | Control | Setter |
|---|---|---|
| Translator tab, write path | `TranslateStatus` (`MainWindow.xaml:278`) | `MainWindow.Translate.cs:106-109` |
| Translator tab, read feed header | `ScreenReadStatus` (`MainWindow.xaml:308`) | `SetScreenStatus`, `MainWindow.Live.cs:157-161` |
| Screen OCR tab | `LiveStatus` (`MainWindow.xaml:394`) | `SetLiveUi`, `MainWindow.Live.cs:163-175` |
| Feed rows (both windows) | `TranslationBody` | `Live.cs:281`, `Ocr.cs:298` |
| Compact overlay | `OverlayStatus` (`CompactOverlay.xaml:44`) | `SetStatus`, `CompactOverlay.xaml.cs:67-72` |
| About tab | `DeepLStatus` (`MainWindow.xaml:625`) | `UpdateDeepLStatus`, `Translate.cs:246-252` |

| State | Chip | Translator status line | LIVE / Screen OCR status line | Feed rows | Compact overlay | About tab |
|---|---|---|---|---|---|---|
| **S1** Healthy | `● Google` teal | unchanged (`Translated · 1 block`) | `🔴 Live — watching…` (unchanged) | translation | chip hidden, status unchanged | `● Google (free) · DeepL for what you write` |
| **S2** Fallback | `● Edge · backup` gold | one-time line: `Translated by Edge — Google is paused.` | same, plus the same one-time line | translation (no marker) | chip prefix on the status line | per-provider list, see §4 |
| **S3** Paused-until | `○ Google paused 0:58` muted | only if it changes what the user gets | `🔴 Live — Google paused (0:58), using Edge.` | translation | `○ Google 0:58` prefix | `○ paused — retries in 0:58` |
| **S4** Offline active | `● Offline` teal | `Translated on your PC (offline engine).` | `🔴 Live — using the offline engine.` | translation | `● Offline` prefix | `● Offline engine — active` |
| **S5** All paused | `○ All paused 4 min` gold | `All engines are paused — next try in about 4 min. Nothing you need to do.` | `○ Live — paused, next try in about 4 min. It resumes on its own; nothing is lost.` | pending rows stay `…`; given-up rows → `(not translated — the engines did not come back)` **[A5]** | `○ Live paused — back in about 4 min` | `○ All engines paused — retries in about 4 min` |
| **S6** No network | `⚠ No internet` red | `No internet connection — nothing can be translated until it is back.` | `○ Live — paused, no internet connection. It resumes on its own; nothing is lost.` **[GAP-3: full pause, like S5]** | `…` | `○ Live paused — no internet` | `⚠ No internet connection` |
| **S7** Key invalid | `⚠ DeepL key refused` red | `Your DeepL key was refused — using the free engines.` | (read path only if the Azure opt-in is on) | translation | `⚠ Key refused` | `✕ DeepL refused this key.` + the fix |
| **S8** Quota out | `● Google · Azure quota out` gold | `Your Azure free quota is used up — using the free engines.` | same when the opt-in is on | translation | short form | `⚠ Quota used up — resets on the 1st` |

Reading rules that make the table work:

- **The chip is the only always-on indicator.** The status lines carry a sentence **only when the state changes**,
  and the sentence is then left in place until the next change — no per-message repaint.
- **A row never carries a countdown — and, from A5, never carries a §3.1 sentence or a provider name
  either.** Principle 5 and principle 1. Pending rows keep the existing `…` placeholder (`Live.cs:263`); the
  explanation lives in the status line and the chip. Today `Live.cs:513` and `Ocr.cs:466` stamp
  `({Friendly(ex)})` into every row of the batch, which is the same sentence repeated fifty times under a status
  line that already said it once. **E7.S1 replaces both call sites with §3.3a's three row texts** — `…`,
  `(not translated — the engines did not come back)`, `(not translated — read cancelled)` — and nothing else may
  be written onto a row.
- **The LIVE heartbeat means "requests are flowing".** While the path is paused (S3 where nothing below serves,
  S5, S6), `UpdateLiveIndicator` (`CompactOverlay.xaml.cs:77-83`) must **freeze on `○`** instead of blinking, and
  the main window's `LiveIndicator` (`MainWindow.xaml:299`) must switch from `●  LIVE` to `○  LIVE (paused)`. A
  blinking dot over a stopped pipe is a lie [CONFIRMED as today's behaviour: the dot keeps blinking through the
  whole 429 storm, `analyse-implementation-actuelle.md` §1.5].

### 2.3 The provider chip

**What it is.** One `TextBlock`, glyph + name + optional countdown. No border, no background, no animation.

**Where it goes** (three instances, all existing containers):

| Path | Placement | Why there |
|---|---|---|
| write | `MainWindow.xaml:270-279` — the button row, right of `TranslateStatus` | next to the thing it explains |
| read | `MainWindow.xaml:296-302` — the "From screen" header, left of `LiveIndicator` | visible whether or not LIVE is running |
| both, compact | prefix of `OverlayStatus` (`CompactOverlay.xaml:44`), shown **only when not healthy** | the overlay is 360 px wide; healthy needs no words |

**Vocabulary** — glyphs already shipped in this UI, so no font risk:

| Glyph | Meaning | Brush (`Theme.xaml`) |
|---|---|---|
| `●` | this engine is serving you | `TealBrush` :22 — or `GoldBrush` :23 when it is a **backup** engine |
| `○` | paused / not sending | `TextMutedBrush` :25 |
| `⚠` | you may need to do something | `AccentBrush` :20 |

Colour is never the only signal: the glyph and the word carry the same information (§6).

**Tooltip** (the dark `ToolTip` style in `Theme.xaml` is load-bearing — reuse it, do not restyle):

```
Google           ○ paused — retries in 0:58   (asked us to slow down)
Edge             ● in use
Google (backup)  ● ready
Offline engine   — not installed
Your keys        DeepL ● ready · Azure — not set
```

Names are §3.0's, verbatim — **"Google (old)" is retired (A2)**: a user-facing name says what a provider is
*for*, and the second Google endpoint is the backup, not the obsolete one.

One line per provider, aligned, no jargon, no HTTP codes. The tooltip is the **only** place the whole chain is
listed outside the About tab. [ASSUMED — a tooltip is the cheapest disclosure that does not cost screen space.]

### 2.4 The countdown pattern (and how it stops flickering)

| Rule | Value |
|---|---|
| Tick rate | **1 Hz**, one `DispatcherTimer` for the whole app |
| Timer lifetime | started when a pause begins, **stopped** when nothing is paused; never runs idle |
| Granularity | `≤ 90 s` → `m:ss`, updated every second · `> 90 s` → `about N min`, updated when N changes · at the 30 min cap → `about 30 min` |
| Repaint guard | assign `.Text` **only when the rendered string differs** from the last one — above 90 s that is once a minute |
| Placement | at most **one** countdown per window (chip **or** status line, never both showing the same clock) |
| Under 5 s | do not show `0:03` counting to zero; show `about to retry` and let the state change do the talking |

Rationale: one text re-measure per second is invisible next to the existing 600 ms heartbeat; two of them, or a
per-row countdown over 50 feed rows, is not. [ASSUMED — WPF measure cost, not benchmarked.]

**Amendment A9 — the bands above are the ruling, and today's renderer differs.** `MainWindow.Live.CountdownText`
(`Live.cs:451-454`) ships `{s} s` under a minute and `{n} min` above it, so a 75-second window reads `2 min`.
E7.S2 replaces it with the three bands in the table: `0:58` · `about 4 min` · `about to retry`. One shape, and
the one that does not round a wait *up* past what the gate will actually do. The renderer stays in the
code-behind — formatting a time is not `Services/`' job (I2) — and it is shared by the LIVE status, the read-once
status and the chip, so the three cannot come to disagree about what a countdown looks like.

**When there is no countdown to render** (`RetryAt` absent — the gate is open on a strike count, not a window),
`{t}` is not faked. §3.1's substitution rule A12 applies instead: `for {t}` → `briefly`, `in {t}` → `shortly`.

---

## 3. Copy deck

Rules for every string below: **English**, sentence case, no HTTP codes, no provider internals (`gtx`,
`dict-chrome-ex`, `429`), no exclamation marks, ≤ 90 characters rendered, and **no terminal full stop on any
sentence that is joined into a wrapper** (deviation D4, and it is now a rule of this deck rather than a deviation
from it — see §3.0). `{n}` is a count, `{t}` a countdown rendered per §2.4, `{P}` a provider's user-facing name
per §3.0.

### 3.0 `{P}`: the names, where they come from, and what each surface does with them

**Amendment A2/A3.** `ChainTranslator.LastOutcome` (`{ProviderId, Skipped, RetryAt?, Kind?}`) now says which
engine answered and which were skipped, so §3.1's `{P}` is a **lookup, never a guess**. One table, one direction:

| Provider id | `{P}` — user-facing name | Short form (overlay, chip) |
|---|---|---|
| `google-dict` | **Google** | `Google` |
| `google-gtx` | **Google (backup)** | `Google (backup)` |
| `deepl` | **DeepL** | `DeepL` |
| `azure` | **Azure** | `Azure` |
| `edge` | **Edge** | `Edge` |
| `bergamot` | **Offline engine** | `Offline` |

Rules that make the names safe to substitute:

1. **No name is ever invented.** If `LastOutcome` is null or its `ProviderId` is unknown, the sentence renders
   its **`{P}`-less form**: `{P}` → `The translation service`, and `Your {P} quota` → `Your free translation
   quota`. Those are exactly the A.0 sentences shipped in `Services/UserMessages.cs`, so the fallback is already
   written and already tested — it stops being the default, it does not stop existing.
2. **The `· backup` chip suffix is dropped when the name already carries it.** `● Google (backup)`, never
   `● Google (backup) · backup`.
3. **`Offline engine` is the name; `Offline` is the chip.** Both are the same provider and the About tab uses the
   long form.
4. **A row never renders `{P}`** (A5). Neither does the About tab's *Chain* line, which lists ids in chain order
   and therefore renders every name whether or not it answered.

**Templates per surface**, with the wrapper each one applies to the §3.1 sentence:

| Surface | Template | Notes |
|---|---|---|
| Translator tab (`TranslateStatus`, `Translate.cs:106`) | `{Sentence}.` — the sentence **alone**, capitalised, terminated | **`Failed: ` is retired.** With `{P}` restored the sentence names the engine and says what happened; "Failed:" in front of it is the app saying "bad news" twice and demoting the sentence to a sub-clause. The chip beside it carries the who at a glance |
| Translator feed rows / Screen OCR feed rows | `…` or one of §3.3a's two `(not translated — …)` forms | never a §3.1 sentence, never `{P}` (A5) |
| Compact overlay quick reply (`CompactOverlay.xaml.cs:142`) | `⚠ {Short} — your text is kept, press Enter to retry.` | `{Short}` is §3.4's short form, **not** the §3.1 sentence: the wrapper alone costs 46 characters of a 360 px line |
| LIVE / Screen OCR status (`SetScreenStatus`) | §3.2's own sentences, which embed `{P}` and `{t}` directly | a status line describes a *state*, not one failed call |
| Auto-stop wrapper (`Live.cs:394`) | `Live stopped after {n} failed reads in a row ({reason}) — press ▶ to try again.` | `{reason}` is the §3.1 sentence, **capital kept** (A11) |
| Read-once (`ReadFailed`) | `Could not read the screen: {sentence}` | the **one** join that lower-cases (A11), because the sentence continues a clause after a colon |

### 3.1 One sentence per typed error kind

These replace the ten ad-hoc strings E1–E10 (`00-annexe-demarrage-et-reseau.md` §2.4) and the five DeepL ones.

**Every sentence below is written WITHOUT a terminal full stop** (D4). The surface that renders it alone — the
Translator tab, per §3.0 — adds the stop; the five that join it must not have to strip one.

| Kind | Sentence | Notes |
|---|---|---|
| `RateLimited` | `{P} asked us to slow down — paused for {t}, and it retries on its own` | **[A1/D5 — replaces the shipped "try again in a moment".]** §3.1 always banned "wait a minute"; the shipped hedge sat next to it because there was no gate. There is one now, so the number is real and the tail answers "must you act?" — no |
| `Blocked` | `{P} is refusing requests from your connection — paused for {t}, nothing you need to do` | **[A1/D5 — replaces the shipped sentence, which answered none of principle 2's three questions.]** What happened (refused), what the app does (paused for {t}), what the user does (nothing). The block is about the connection, not about anything they typed, and saying so is what stops them re-pressing |
| `Unavailable` | `{P} is down right now — another engine is being tried` **/** `{P} is down right now — try again shortly` | 5xx. **First form when `LastOutcome.Skipped` shows a tier below this one is still available; second when it does not** (A4). The promise is made only when it is true |
| `Timeout` | `{P} took too long to answer — another engine is being tried` **/** `{P} took too long to answer — try again shortly` | same fork as above |
| `Network` | `No internet connection — nothing can be translated until it is back` | the only kind with no `{P}`: when nothing resolves, naming an engine is noise |
| `BadResponse` | `{P} sent something we could not read — another engine is being tried` **/** `{P} sent something we could not read — try again shortly` | same fork |
| `QuotaExhausted` | `Your {P} quota is used up for this month — the free engines are used instead` | **[A1/D5 — replaces the shipped "Your free translation quota is used up for this month".]** Keys only, so `{P}` is `DeepL` or `Azure` and is never ambiguous once E6 lands two key slots. "for this month" is what both free tiers actually do. The tail is a statement about the chain, true whether or not this particular call then succeeded — which is why it survives A4 where "trying another engine" does not |
| `AuthFailed` | `Your {P} key was refused — check it in About, or clear it` | keys only. The one sentence in the deck whose third answer is "yes, act" — and the only exit is the key box, not a timer |
| `Cancelled` | *(nothing)* | must never reach a surface |
| `AllProvidersPaused` | `All engines are paused — next try in {t}, nothing you need to do` | the single most important line in this document |

**A12 — the `{t}` substitution, once, for all three sentences that carry one.** `RetryAt` can legitimately be
absent (a gate opened on a strike count rather than a window). There is **no second sentence** for that case:
`for {t}` renders `briefly`, `in {t}` renders `shortly`. So `Blocked` with no `RetryAt` reads
`Google is refusing requests from your connection — paused briefly, nothing you need to do`.

**A11 — the join rule, corrected.** §3.3 wrote "lower-cased at the join" when every sentence began with "The
translation service". With `{P}` restored the first word is usually a proper noun, and "google asked us to slow
down" is a typo, not a sentence. **Only the colon join lower-cases** (`Could not read the screen: no internet
connection…`, which is the branch `UserMessages.LowerAtJoin` already guards); the parenthetical joins and the
`⚠ … — your text is kept` join keep the sentence's own capital.

When a **fallback succeeded**, the error kind is *not* shown at all — the user got their translation. Only the chip
changes, plus the one-time notice of §3.5.

### 3.2 LIVE statuses

Replacing `MainWindow.Live.cs:235` and `:238`.

| Situation | Main window (`ScreenReadStatus` / `LiveStatus`) | Compact overlay (≤ 40 chars) |
|---|---|---|
| running (unchanged) | `🔴 Live — watching (check #{n})…` | *(unchanged, no chip)* |
| one failed read, still trying | `🔴 Live — one read did not translate, retrying…` | `⚠ one read is retrying` |
| the preferred engine is paused, a backup serves | `🔴 Live — {P} paused ({t}), using {P2}.` | `○ {P} {t} · using {P2}` |
| everything paused **[A9 — the 1 Hz template]** | `○ Live — paused, next try in {t}. It resumes on its own; nothing is lost.` | `○ Live paused — back in {t}` |
| everything paused, no `{t}` to give | `○ Live — paused. It resumes on its own; nothing is lost.` | `○ Live paused — it resumes on its own` |
| paused, under 5 s left (§2.4's floor) | `○ Live — paused, about to retry.` | `○ Live paused — about to retry` |
| no internet (S6 — a full pause, GAP-3) | `○ Live — paused, no internet connection. It resumes on its own; nothing is lost.` | `○ Live paused — no internet` |
| resumed | `🔴 Live — watching (check #{n})…` — the normal running line, plus §3.5's one-time `Back on {P}.` | *(chip cleared, normal status)* |
| recovered with a backlog | `🔴 Live — back on. Catching up on {n} message(s).` | `● back on — {n} to catch up` |
| **auto-stopped, honestly [A6]** | `Live stopped after {n} failed reads in a row ({reason}) — press ▶ to try again.` | `■ Live stopped — press ▶ to retry` |
| auto-stopped because everything is paused past the cap | `Live stopped — every engine is paused. Try again in {t}, or add the offline engine in About.` | `■ Live stopped — engines paused` |

**A6 — the auto-stop sentence, and why it is one sentence.** Ruling E5-d made the trigger **5 consecutive *sent*
failures since the last translated tick, with no time window**; E5-c means a pause or a gate refusal never counts,
so `{n}` is always five real requests that really failed. `LiveErrorTracker.ConsecutiveFailures` is the `{n}` and
it already exists. The shipped wrapper `Live stopped after repeated errors ({Friendly(ex)}).` is replaced rather
than kept: "repeated" is the app declining to say how many, and the way back — ▶ — is not in it at all. The new
form keeps the shipped wrapper's one genuinely good idea (the reason travels with the stop) and adds the two
things a stopped loop owes the player, the count and the gesture:

> `Live stopped after 5 failed reads in a row (Google took too long to answer) — press ▶ to try again.`

`{reason}` is a §3.1 sentence with **its capital kept** (A11) and its full stop absent (D4), so the wrapper's own
stop is the only one. Rendered length with the longest sentence in the deck is ~120 characters; the main window's
status line wraps and the overlay takes the short form instead — the reason is what the overlay cannot afford.

**Countdown behaviour on these lines (A9, E7.S2).** One `DispatcherTimer` at 1 Hz feeds every `{t}` above,
started when a pause begins and stopped when nothing is paused (§2.4). The main window's paused line and the
overlay's are re-rendered from the same `CountdownText`, and each is assigned **only when its own rendered string
changed** — above 90 s that is once a minute on both. The overlay budget is **40 characters**: `○ Live paused —
back in about 4 min` is 35, `○ Live paused — back in 0:58` is 28.

The UX requirement behind all of them is unchanged: **when LIVE stops, the reason is in the sentence and the way
back is in the sentence.**

### 3.3 Read-once statuses (killing the false "Done")

Replacing `MainWindow.Ocr.cs:243-244`.

**Amendment A7 — the four shipped statuses are CONFIRMED as written** (`Services/UserMessages.cs`,
`Services/ReadOnceSummary.cs`, E5.S4). What changes is the paused row, which E5-g gave a `{n}` it did not have,
and which was promising something it could not always deliver.

| Outcome | String |
|---|---|
| all lines translated | `Done — {n} line(s) translated.` **[confirmed as shipped]** |
| some translated | `Read {n} line(s) — {k} translated, {m} could not be. {reason}` **[confirmed as shipped]** |
| none translated | `Read {n} line(s) — none could be translated. {reason}` **[confirmed as shipped]** |
| none translated, everything paused, **LIVE is running** | `Read {n} line(s) — every engine is paused, they fill in when one is back.` |
| none translated, everything paused, **LIVE is stopped** | `Read {n} line(s) — every engine is paused, try again in {t}.` |
| …and no `{t}` to give | `Read {n} line(s) — every engine is paused, try again shortly.` **[shipped shape, kept]** |
| the person cancelled the read | `Read cancelled.` **[confirmed as shipped]** |
| OCR itself failed | `Could not read the screen: {reason}` (today's `OCR failed:` prefix is developer-speak) |
| in flight | `Reading…` then `Read {n} line(s). Translating…` **[confirmed as shipped]** |

**Why the paused row forks [ASSUMED, but the alternative is a lie either way].** §3.3 used to promise "They will
fill in when one is back". E5.S3's retry queue is drained **by the LIVE loop**, so that promise is true for a read
taken while LIVE is running and false for one taken with LIVE stopped — where nothing will ever come back for
those rows. One sentence cannot be honest in both states, and principle 4 does not allow picking the friendlier
one. The loop's own state is the fork, and the code-behind can see it.

`{reason}` is the sentence from §3.1, **lower-cased only after the colon** (A11). **"Done" may only ever appear
when every line has a translation** — now enforced in `ReadOnceSummary.Status`, not hoped for. [CONFIRMED bug in
v0.14.0: `Ocr.cs:296-299` returned instead of throwing, so `:244` printed Done over a total failure. Fixed in
E5.S4.]

**Amendment A8 — the cancel affordance. The read-once button becomes the cancel; no new control.**

E5.S4 wired three cancel routes and shipped none of them reachable: `SetReadOnceEnabled(false)` greys both
read-once buttons (a disabled WPF button raises no `Click`), ■ Stop is `Collapsed` unless a live loop is running,
and `ToggleLive` returns early while `_readingOnce`. Only **Ctrl+Alt+R** can stop a read today. The ruling:

| Surface | While idle | While `_readingOnce` |
|---|---|---|
| `SelectAreaButton` (main window) | `Read the area once` — **enabled** | `Cancel read` — **still enabled**; the press cancels |
| `ReadOnceButton` (overlay, icon-only) | its normal glyph — **enabled** | `■`, `ToolTip` = `Cancel read`; the press cancels |

**Why this and not "name the hotkey in the status line".** A gesture that exists only in a sentence is a gesture
nobody makes mid-raid — and the status line is where the *state* lives, not where controls are kept (principle 1).
More directly: a greyed button during a 30-second wait, with nothing else to press, is precisely what makes a
player press it again and again (amplifier A7), which is the behaviour this whole epic exists to stop. Giving that
press a meaning costs one label swap and zero controls.

**Two implementation conditions, both from the E5.S4 review and both mandatory.** (1) The `if (_readingOnce) {
CancelReadOnce(); return; }` guard must be the **first line of `SelectAreaAndReadOnceAsync`**, ahead of
`StopLive()` and `SelectRegionAsync()` — otherwise a cancel press starts a region drag over the game.
(2) `SetReadOnceEnabled` becomes `SetReadOnceCancelMode(bool reading)`: it swaps label, tooltip and
`AutomationProperties.Name`, and never disables. `ShowOcrPackNeeded` and the other genuine disable paths are
untouched.

### 3.3a Feed-row texts — the complete list

**Amendment A5.** Three strings, and a row may carry nothing else — no §3.1 sentence, no provider name, no
countdown (principle 1, principle 5).

| Row state | Text | Retry candidate? |
|---|---|---|
| waiting for the next drain | `…` (no parenthesis — it has not failed, it is pending) | yes |
| the retry queue gave up on it | `(not translated — the engines did not come back)` | no |
| the read that owned it was cancelled | `(not translated — read cancelled)` | **no** — the player refused this request; re-sending it spends what they just declined |

Both parenthesised forms are **confirmed as shipped** (`UserMessages.RetryGaveUpRow`, `.ReadCancelledRow`); the
`(` is I4's "this is not a translation" marker, added by the call site.

**One gap this deck must not leave open [ASSUMED].** A read-once taken with LIVE **stopped** has no queue behind
it, so its failed rows would sit on `…` for ever — the pending state with nothing pending, which is A7 again. At
the end of such a read the rows are stamped `(not translated — the engines did not come back)` immediately,
using the same finished form; only rows with a live drain behind them are allowed to stay on `…`.

### 3.4 Compact overlay quick reply

Replacing `CompactOverlay.xaml.cs:142`. The overlay defaults to 360 px wide — these are the tightest strings in the
app; keep them under ~60 characters.

**Amendment A3.** The wrapper `⚠ … — your text is kept, press Enter to retry.` costs 46 characters on its own, so
this surface takes a **short form**, never a §3.1 sentence. `{Short}` is chosen by `Kind`, and `{P}` is §3.0's
short name.

| Situation (`Kind`) | String | Rendered length with `{P}` = `Google` |
|---|---|---|
| generic failure (shape kept) | `⚠ {Short} — your text is kept, press Enter to retry.` | — |
| `RateLimited` / `Blocked` — one engine | `⚠ {P} paused ({t}) — your text is kept, press Enter to retry.` | 61 |
| `AllProvidersPaused` | `⚠ Engines paused ({t}) — your text is kept.` | 42 |
| `Network` | `⚠ No internet — your text is kept, press Enter to retry.` | 55 |
| `Timeout` / `Unavailable` / `BadResponse` | `⚠ {P} did not answer — your text is kept, press Enter to retry.` | 62 |
| `AuthFailed` | `⚠ Your {P} key was refused — see About.` | 39 |
| `QuotaExhausted` | `⚠ Your {P} quota is used up — see About.` | 40 |

The existing promise — *your text is kept* — is the best sentence in the current app. Keep it verbatim wherever it
still applies; it is dropped only where nothing was typed to keep (`AuthFailed`, `QuotaExhausted`, which send the
player to About instead) and where the line would otherwise pass ~65 characters.

### 3.5 "Fallback active" notice

Shown **once per switch**, in the status line, then left alone:

- `Translated by Edge — Google is paused.`
- `Translated by the offline engine — no internet needed.`
- `Back on Google.` (on recovery, so the user knows the chip changed for a reason)

Never per row, never per message, never as a toast (a toast for something that happens 40 times an evening is a
punishment).

### 3.6 First-use consent for the offline engine

**Design decision [ASSUMED, and a deliberate divergence from the brief's flow (d):** the consent dialog is **never**
raised by a background failure. It is raised only when the user clicks **Download the offline engine** in About.
Reason: principle 3 — a `MessageBox` over a fullscreen game, opened by a LIVE loop the user forgot was running, is
the single worst thing this app could do. What the failure path may do is **nudge**: `All engines are paused — you
can add an offline engine in About.`

`MessageBox.Show(this, …)` with an owner, per `project-context.md`. Title: `Add the offline engine?`

> **`%LocalAppData%`, not `%AppData%` — ruling E8-b (Winston, 2026-09-07), landed with E8.S3's code in one
> commit.** The line below said `%AppData%\PWRUHelper\models` when this deck was written, by analogy with
> `settings.json`, `provider-state.json`, `translation-cache.json` and the log — all four of which are kilobytes.
> This directory is 22 MB of native library plus 22–37 MB per model, and a roaming or OneDrive-synced profile
> copies its contents **at logon**: the exact class of machine-dependent startup cost P1 spent a phase hunting.
> Machine-local, re-downloadable binary data belongs in Local, and the consent dialog's whole job is to say where
> the 50 MB went — so the copy moved with the code rather than after it. The **cache** sentence in §4.2 still says
> `%AppData%\PWRUHelper\` and is still right: that file is kilobytes of the user's own data and roams correctly.

```
The offline engine translates on your PC, with no internet at all.
It is a bit rougher than Google, and it is used only when every online
engine is unavailable.

Download          about 22 MB for the engine + about 30 MB per language pair
While translating it uses 130-310 MB of memory, freed when it goes idle
Stored in         %LocalAppData%\PWRUHelper\models
To remove it      About tab → Offline engine → Remove (deletes the files)

                             [ Download (about 50 MB) ]   [ Not now ]
```

During the download the About row becomes `Downloading the offline engine… {p}%` with a `Cancel` button; on
completion, `● Offline engine ready — used only when everything else is unavailable.` If the download fails:
`Download failed — {reason}. Nothing was installed.`

On **Remove**, the row reports what really came back: `Offline engine removed — {n} MB freed from your disk.`

> **The residual case ruling E8-b left owed, landed with E8.S5's code in one commit.** A `Remove` deletes the
> whole root, and the two causes the app can remove are removed before it tries: the engine is freed **and**
> closed for good first (E8.S4's ordering + E8.S5's terminal `Close`, so a translation arriving mid-delete cannot
> map the DLL again). What can still be left is a handle this process does not hold — an antivirus or a
> sync agent with a file open — and the old row reported that as "removed — 0 MB freed from your disk" over
> 50 MB that had not moved, which is the one thing principle 1 forbids outright. So:
>
> `Could not remove every file — {n} MB left; close the app and try again.`
>
> It says what happened, how much is left, and **what the user can do about it** (principle 2 — never a statement
> with no exit). Closing the app is the honest instruction: it releases the handle, and it costs nothing.

### 3.7 Key validation feedback (DeepL and Azure)

`Test key` is a real request that validates key **and** region without translating a paying character where the
provider allows it (Winston: see §7 OQ-6).

| Provider | Result | String on `DeepLStatus` / `AzureStatus` |
|---|---|---|
| DeepL | ok | `✓ Key works — DeepL is used for what you write.` |
| DeepL | invalid | `✕ DeepL refused this key. Check you pasted all of it (free keys end in :fx).` |
| DeepL | quota out | `⚠ The key works, but the DeepL quota is used up — the free engines are used until it resets.` |
| Azure | ok | `✓ Key works ({region}) — Azure is used for what you write.` |
| Azure | invalid key | `✕ Azure refused this key. Check the key, and that the region matches your resource.` |
| Azure | wrong region | `✕ Azure did not accept the region "{region}". Pick the one shown on your Azure resource.` |
| Azure | quota out | `⚠ Your 2 million free characters for this month are used up — resets on the 1st.` |
| either | no network | `Could not check the key — no internet connection.` |
| cleared | — | `○ No key — using the free engines (Google, Edge).` |

Saving stays **explicit** (a Save button, as today at `MainWindow.xaml:622`): a `PasswordChanged` handler would
fight `_restoringSettings` for no benefit (§4.3).

### 3.8 P1 expectation copy (startup)

Nothing in-app can shorten the wait: it happens **before the process exists** [CONFIRMED,
`hypotheses-matrice.md` §2 and Group P; the owner's own answer (a)]. A splash screen cannot help, and must not be
proposed as a fix. The only lever is expectation-setting, in three places.

**README** — new bullet under `⬇️ Download & use`, and the same words in `checklist-nouvelle-machine.md` §A4:

> **The first launch after downloading — and after every update — can take up to about 10 seconds, with nothing on
> screen.** Windows checks a file it has never seen before. Later launches are fast (about a second). Every update
> is a brand-new file as far as Windows is concerned, so the check happens again after each one.

**Release notes** — one line at the top of every release, verbatim, every time:

> First launch after this update can take a few seconds while Windows checks the new file. Launches after that are
> back to normal.

**In-app, one-time after an update** — my recommendation: **yes, ship it, narrowly scoped.**

> `Updated to v{X.Y.Z}. The first launch after an update is slower — Windows checks the new file. The next ones are
> fast again.`

**Why yes:** the failure mode here is not the delay, it is the interpretation — "the app is broken / it hung /
I'll double-click again". A retrospective sentence at the moment of the memory converts a bug report into a known
behaviour, and it is the only channel that reaches users who never read the README. **Why narrowly:** it must fire
**once**, on the first run after the version string changes (a new `LastRunVersion` in `AppSettings`, seeded by
`Migrate` with the *current* version so no existing user gets a spurious toast on the release that ships it —
`Services/SettingsService.cs:102,133-159`), it must use the existing `ShowToast` (`MainWindow.xaml.cs:400`, which
already gives >40-character messages 3.5 s), and it must be **suppressed in compact mode** — `ShowToast` routes to
`_overlay.SetStatus` (`MainWindow.xaml.cs:400-403`), which would overwrite the LIVE status line with a startup
notice mid-raid. [ASSUMED — the "once per version" rule is judgement; the routing hazard is CONFIRMED in code.]

---

## 4. Settings and keys UX (About tab)

### 4.1 Layout decision

**Everything stays in the About tab. No settings window, no sixth tab.** The About tab already owns the DeepL key
(`MainWindow.xaml:611-626`), the update check and the error report; the tab order is fixed by
`project-context.md` and referenced by `MainTabs_SelectionChanged`. The existing "Better translations (optional)"
heading becomes **"Translation engines"** with three blocks: *what is being used* (read-only), *your keys*, *offline
engine*. Many users will never open it — which is exactly why nothing in the degraded-mode flows may **require**
it (principle 2).

### 4.2 ASCII mockup

```
 Translation engines                                                     (About tab)
 ─────────────────────────────────────────────────────────────────────────────────
 By default everything runs on free engines — no key, no signup, nothing to set up.

   In use now   ● Edge · backup            Google is paused, retries in 0:58
   Chain        Google → Edge → Google (backup) → Offline engine (not installed)

 Your keys (optional)
 ─────────────────────────────────────────────────────────────────────────────────
 A key gives you better translations and your own quota, instead of sharing a free
 door with everyone else. Keys are stored only on your PC.

   DeepL key    [ ••••••••••••••••••••••••••••  ]  [ Save ]  [ Test key ]
                ✓ Key works — DeepL is used for what you write.

   Azure key    [ ••••••••••••••••••••••••••••  ]  [ Save ]  [ Test key ]
   Region       [ westeurope            ▾ ]   (the region shown on your Azure resource)
                ✓ Key works (westeurope) — Azure is used for what you write.

   [ ] Use my Azure key for screen reading too (uses your free quota faster)
       Azure gives you 2 million characters a month for free — roughly 20 to 40
       hours of busy chat. Screen reading is off by default because live mode
       reads every new line and can use it up in a few evenings.

 Offline engine (optional)
 ─────────────────────────────────────────────────────────────────────────────────
   ○ Not installed — about 50 MB to download, works with no internet at all.
     Used only when every online engine is unavailable.
                                       [ Download the offline engine ]

   (once installed)
   ● Ready — 52 MB on disk, ru→en.  Uses memory only while translating.
                                       [ Remove ]   [ Add a language pair ]

 Saved translations
 ─────────────────────────────────────────────────────────────────────────────────
   Translations you have already seen are saved in %AppData%\PWRUHelper\ so the
   same chat line is never translated twice — they hold chat text, they never
   leave your PC, and they are never included in the error report.
                                       [ Clear cache ]
 ─────────────────────────────────────────────────────────────────────────────────
```

**Amendment A10 — the cache privacy line, and why it comes with a button.** Ruling E4-c already guarantees the
technical half (`translation-cache.json` is never logged, never in `CopyErrorReport_Click`, deleted with the app
data). What was missing is that **nobody told the user the file exists**, and it holds other players' chat.

The one sentence, verbatim in the About tab and as a README bullet under `⬇️ Download & use`:

> **Translations you have already seen are saved in `%AppData%\PWRUHelper\` so the same chat line is never
> translated twice — they hold chat text, they never leave your PC, and they are never included in the error
> report.**

**One control: `[ Clear cache ]`.** A statement that the app stores your chat, with no way to remove it, is the
exact shape principle 2 forbids ("what can you do about it" must be written, not implied). It is a `GhostButton`
next to the sentence, and it takes **no confirmation dialog** — unlike `Remove` for the offline engine, clearing
the cache destroys nothing the app cannot rebuild; the cost is a few extra requests. The result goes to the same
status line as the key tests, never a `MessageBox`:

> `Cache cleared — {n} saved translation(s) removed.`

It clears the in-memory store **and** deletes the file, in that order, so a debounced save cannot resurrect it.

### 4.3 Control-by-control specification

| Control | Type | Notes |
|---|---|---|
| `DeepLKeyBox` | `PasswordBox` (exists, `MainWindow.xaml:621`) | unchanged; masked; restored in `ApplySettings` by `DeepLKeyBox.Password = s.DeepLApiKey` (`MainWindow.xaml.cs:185`) |
| `AzureKeyBox` | `PasswordBox` | **same pattern, verbatim** — a `PasswordBox` cannot be data-bound, and there is no MVVM here anyway |
| `AzureRegionCombo` | `ComboBox IsEditable="True"` | seeded with `global`, `westeurope`, `francecentral`, `northeurope`, `eastus`, `eastus2`, `westus2`, `uksouth`, `swedencentral`; free text accepted because Azure adds regions faster than we ship. Use the existing `SelectTag`/`SelectedTag` helpers (`MainWindow.xaml.cs:359-370`) — do not re-roll the loop [CONFIRMED rule, `project-context.md`] |
| `AzureForReadingCheck` | `CheckBox` | default **off**; changing it rebuilds the read chain immediately, like `DeepLSaveKey_Click` rebuilds the write chain (`Translate.cs:239`) |
| `TestKeyButton` ×2 | `Button` (`GhostButton`) | disabled while in flight, label → `Testing…`; result into the status line, never a `MessageBox`; a failed test **does not** clear the key |
| `OfflineEngineToggle` | `Button` (Download / Remove) | not a checkbox: the two actions have very different weight. `Remove` asks for confirmation with `MessageBox.Show(this, …)` and states that it deletes the files |
| `EngineChainText` | `TextBlock`, read-only | the "Chain" line, in §3.0's names; refreshed by `UpdateEngineStatusUi()` |
| `ClearCacheButton` | `Button` (`GhostButton`) | **A10.** No confirmation (nothing is lost that cannot be re-fetched); clears the store then deletes the file; result into the status line, never a `MessageBox`. Not persisted, so no `_restoringSettings` handler and no `Migrate` step |
| `SelectAreaButton` / overlay `ReadOnceButton` | existing `Button`s | **A8.** `SetReadOnceEnabled` becomes `SetReadOnceCancelMode(bool reading)`: swaps label (`Read the area once` ⇄ `Cancel read`), overlay glyph (⇄ `■`), `ToolTip` and `AutomationProperties.Name`. **It never disables** — a disabled button raises no `Click`, which is exactly why E5.S4's three cancel routes were unreachable |

**The `_restoringSettings` contract — mandatory for every new control** [CONFIRMED,
`MainWindow.xaml.cs:28-35`]. XAML loading itself raises `Checked`/`SelectionChanged` during
`InitializeComponent()`, long before `ApplySettings` runs; a handler that writes settings there clobbers the saved
value (this is the v0.12.3 / v0.13.0 bug class). So:

1. `AzureForReadingCheck_Changed` and `AzureRegionCombo_Changed` **must** start with
   `if (_restoringSettings) return;`.
2. `ApplySettings` sets them **and then applies their UI side-effects explicitly** — a new `UpdateEngineStatusUi()`
   called at the same place `UpdateOcrFilterUi` is called today (`MainWindow.xaml.cs:193`).
3. New persisted defaults (`AzureRegion`, `AzureForReading`, `OfflineEngineEnabled`, `LastRunVersion`) reach
   **existing** users only through `SettingsVersion` + `AppSettings.Migrate`
   (`Services/SettingsService.cs:102,133-159`). A property initializer alone does nothing.
4. Keys are never written to the log or the error report (`CopyErrorReport_Click`,
   `MainWindow.xaml.cs:312-322`, is pasted into Discord by design).

**The quota hint arithmetic** [ASSUMED]: `benchmark-fournisseurs.md` §8.1 puts a heavy LIVE user at 5.8 M
characters over 120 h → ≈48 k characters per hour of busy chat; Azure F0 is 2 M/month → ≈41 h; the app's own
billing multiplier of ×1.3–2.0 (two groups per tick, retries, two caches) brings that to **≈20–40 h**. That is the
number in the copy. If Winston merges the `ru`/`auto` groups and shares the cache, the multiplier drops and this
sentence must be re-derived, not left to rot.

---

## 5. Interaction flows

### (a) Google throttles during LIVE

1. **t+0 s** — a read fails on Google. The chain silently tries Edge; Edge answers. The row shows a real
   translation. Chip flips to `● Edge · backup` (gold). Status line, once: `Translated by Edge — Google is
   paused.` The heartbeat keeps blinking, because requests really are flowing.
2. **t+1 s → t+60 s** — Google's gate holds. The chip shows `○ Google paused 0:58`, counting at 1 Hz, granularity
   per §2.4. Nothing else changes; the feed keeps filling normally. **The player, mid-raid, notices nothing.**
3. **t+60 s** — the gate reopens. First read goes back to Google. Chip → `● Google`. Status line, once:
   `Back on Google.`
4. **If Edge also fails** — the chain drops to Google (old); if that fails too, state **S5**: heartbeat freezes on
   `○`, status becomes `○ Live — paused, next try in about 2 min. It resumes on its own; nothing is lost.`,
   pending rows stay `…`. **No requests are sent during the pause** — that is the entire point (`mecanismes…` Q7:
   the block expires *after the requests stop*).
5. **On recovery** — the rows that were waiting are re-translated **in place** (same position, `…` → text). Status:
   `🔴 Live — back on. Catching up on 7 message(s).` Rows must not be appended a second time; a duplicated feed
   would be worse than the failure.
6. **If the cap is reached (30 min) and the chat is still producing** — LIVE auto-stops honestly:
   `Live stopped — every engine is paused. Try again in about 30 min, or add the offline engine in About.`

### (b) All providers paused

1. Chip: `○ All paused 4 min` (gold, glyph + word, never colour alone).
2. Translator tab: pressing Translate does **not** send. `TranslateStatus` → `All engines are paused — next try in
   about 4 min, nothing you need to do.` — the §3.1 sentence rendered alone and terminated, with no `Failed: `
   prefix (A3). The input keeps the user's text; the button stays enabled (pressing again just re-shows the
   sentence — no request, no punishment).
3. Overlay quick reply: `⚠ Engines paused (4 min) — your text is kept.`
4. Once, when the state is entered and the offline engine is **not** installed, the About-tab nudge appears in the
   status line: `You can add an offline engine in About — it works with no internet.`
5. At `retryAt`, the first successful call clears everything with no fanfare: chip back to `●`, status line to its
   normal text.

### (c) The user pastes an Azure key

1. About tab → **Azure key** → paste → **Save**. Toast: `Azure key saved — used when you write.` (mirrors
   `DeepLSaveKey_Click`, `Translate.cs:241-243`).
2. The chain is rebuilt immediately, fresh cache — same behaviour as the DeepL key today (`Translate.cs:239`).
3. **Test key** → `Testing…` → `✓ Key works (westeurope) — Azure is used for what you write.`
4. Wrong region is the likely mistake, so it gets its own sentence (§3.7) naming the region they typed.
5. The read-path opt-in stays **off** until they tick it, and the tick carries its own cost sentence — a free
   quota that disappears in three evenings, silently, would be a betrayal.
6. Both keys set → the order is DeepL, then Azure, then the free engines. Shown plainly in the *Chain* line; not
   configurable [ASSUMED — ordering UI is complexity nobody asked for].

### (d) First use of the offline engine

1. State S5 puts the nudge in the status line. Nothing pops up. Nothing downloads.
2. The user opens About and clicks **Download the offline engine**.
3. Consent dialog (§3.6) — size, memory, storage location, how to remove. `Not now` is the safe default and closes
   with no trace.
4. Download with a percentage and a `Cancel`; the app stays fully usable throughout.
5. Ready: `● Offline engine ready — used only when everything else is unavailable.`
6. Next time everything is paused, the chip shows `● Offline engine`, the status line says `Translated on your PC
   (offline engine).`, and the feed keeps moving. When the online engines recover, the chain goes back up and the
   model is unloaded on idle — silently; the chip changing is enough.

### (e) No network at all

1. Every provider returns `Network`. State **S6** immediately, without a countdown (there is nothing to wait for).
2. Chip `⚠ No internet` (red + glyph + word). Status: `No internet connection — nothing can be translated until it
   is back.`
3. LIVE does **not** auto-stop for this — ruling E5-c: a pause is never an error, so the counter is untouched and
   the loop waits instead of giving up. **Amended by ruling GAP-3 (Winston, from the owner's OQ-B): the loop does
   not keep capturing either.** My original text here had LIVE go on reading the screen because OCR is local and
   free; the owner's answer is a **full pause** — no capture, no OCR, zero CPU — and it is universal, so S6
   behaves exactly like S3 and S5. The feed freezes, the status says why, and the first tick after the connection
   returns catches up. Status: `○ Live — paused, no internet connection. It resumes on its own; nothing is lost.`
   [Was ASSUMED; now DECIDED against, and the reason is the product requirement that outranks it: nothing may
   cost the game a frame while it is waiting for something that cannot arrive.]
4. If the offline engine is installed, S6 is bypassed entirely: it just works, and the chip says so.
5. The Phrasebook and Squad tabs keep working with no message at all — they never needed the network.

### (f) First launch after an update

1. Double-click. Nothing on screen for up to ~10 s — Windows is checking a file it has never seen.
2. The window appears on the tab the user left (`ApplySettings`, `MainWindow.xaml.cs:173-174`).
3. **Once**, the update toast of §3.8 appears for 3.5 s — suppressed if the app opened straight into compact mode.
4. If a persisted pause is still active from the previous session, the chip shows it immediately
   (`○ All paused 3 min`) — **before** the user presses anything. This is the fix for the owner's original
   complaint that the error "appears at launch": now it is a state the app owns and explains, not a surprise the
   first click produces.
5. Nothing else is different. No splash, no "what's new" window, no changelog dialog.

### (g) A read the player gives up on **[new — amendment A8]**

1. The player presses **Read the area once**, drags a region, and the status shows `Reading…`. The button's label
   changes to **`Cancel read`** and stays enabled; in compact mode the overlay's read-once glyph becomes `■` with
   the tooltip `Cancel read`. Ctrl+Alt+R keeps working — it is now the second way, not the only one.
2. OCR lands: `Read 14 line(s). Translating…`. Every engine is paused, so the chain answers
   `AllProvidersPaused` **without sending anything** (ruling E5-g: the capture and the OCR are local and always
   run; the cache serves whatever it can).
3. The player presses **Cancel read** at second 9 rather than waiting out the 30 s budget. The status becomes
   `Read cancelled.` — not a failure sentence, not a countdown, not a chip: nothing here is degraded, and §2.1
   says a cancel is not a state.
4. The rows the read created are stamped `(not translated — read cancelled)` and are **finished, not failed**:
   E5.S3's retry queue must not pick them up. Re-sending a request the player just refused is the one thing worse
   than not sending it.
5. Had they waited instead, the read would have ended on `Read 14 line(s) — every engine is paused, try again in
   about 4 min.` (LIVE stopped) or `…, they fill in when one is back.` (LIVE running) — §3.3's fork.

---

## 6. Accessibility and footprint

- **Never colour alone.** Every state carries glyph + word + colour; `○ Google paused 0:58` says everything with
  the palette stripped out, which is the test a colour-blind player applies.
- **Contrast.** Status text keeps `TextMutedBrush` (#b0a184) on `#071c2f` / `#0e2c47` as today — do not dim
  degraded copy further to make it "quieter". If it matters enough to render, it matters enough to read.
- **`AutomationProperties.Name`** on the chip (`"Translation engine status"`), matching the icon-only buttons
  (`MainWindow.xaml:259-261`, `CompactOverlay.xaml:31-38`).
- **No new fonts, images or colours.** Every glyph above already ships (`● ○ ⚠ ✓ ✕ ▶ ■ 🔴`); every brush is a
  `Theme.xaml` key.
- **Repaint budget.** One 1 Hz timer, stopped when idle; one text assignment per second at most, only when the
  string changed; no per-row countdown; no new animation. The 600 ms heartbeat stays the fastest thing on screen —
  and it **stops** while paused, so the degraded state costs *less* than the healthy one.
- **No modal from a background path, ever.** The one dialog here is user-initiated (§3.6).
- **Every new `Run.Text` binding in a feed template is `Mode=OneWay`** and goes into the STA `TemplateRenderTests`
  [CONFIRMED rule, `project-context.md`] — relevant if a given-up row gets its own template.

---

## 7. Open questions for Winston and John

What the architecture must expose for this UX to exist. Each is a hard dependency, not a nice-to-have.

**Answered in Phase 3/4, and the amendments above depend on the answers:** **OQ-1** → `ProviderGate.Snapshot()`
returns `{State, BlockedUntil, Strikes, LastKind}`, and `AllProvidersPaused` carries the earliest reopening
(ruling R-2). **OQ-2** → `ProviderIds` + §3.0's name table. **OQ-3** → `ChainTranslator.LastOutcome`
`{ProviderId, Skipped:[(ProviderId, Reason)], RetryAt?, Kind?}` (ruling R-3) — this is what makes `{P}` a lookup
instead of a guess. **OQ-4** → **overruled**: no event; the UI **polls `Snapshot()` at 1 Hz** from the countdown
timer this deck already required (ruling OQ-c), so `Services/` stays passive and the repaint budget is unchanged.
**OQ-5** → the gates are shared between both chains. **OQ-7** → yes, rows are re-translated in place (E5.S3).
**OQ-8** → yes, the pause survives restart (`provider-state.json`, E2.S4) and is readable before the first user
action. The rest are still open.

| # | Question / requirement | Why the UI needs it |
|---|---|---|
| **OQ-1** | A **`retryAt`** per gate **and** one for the whole path (the earliest reopening). | Every countdown in §2.4 and every `{t}` in §3. |
| **OQ-2** | A **stable, user-facing provider name** (`Google`, `Edge`, `Google (old)`, `DeepL`, `Azure`, `Offline engine`) — not a class name, not a URL, not `client=`. | The chip, the tooltip, the Chain line, §3.5. |
| **OQ-3** | The **provider that actually answered**, per call, plus the **reason a higher one was skipped** (paused / failed-now / not configured). | S2 vs S3 are different sentences. |
| **OQ-4** | A **state-changed notification** (plain C# event or callback, no WPF types — `Services/` stays UI-free) rather than the UI polling. | Principle 5; polling a breaker every 250 ms is exactly the cost we are removing. |
| **OQ-5** | Is the gate state **shared between the read and write chains**? (`analyse…` §6.3 says it must be.) | If shared, "Google paused" has to appear on the Translator tab even when the user only ran LIVE. If not, the chip means different things on two surfaces and the copy has to say which. |
| **OQ-6** | Can a key be validated **without spending quota** (DeepL `/usage`, Azure `/languages` or an equivalent)? | The `Test key` button's honesty, §3.7. If not, the button must say `Test key (uses a few characters)`. |
| **OQ-7** | Do recovered rows re-translate **in place**, keeping their position and their `Glossary` (🔑) line? | Flow (a) step 5. Appending duplicates would be worse than the failure. |
| **OQ-8** | Does the pause **survive restart** (it must, per the owner's evidence), and can the UI read it **before** the first user action? | Flow (f) step 4 — the whole "it appears at launch" complaint. |
| **OQ-9** | Is the Bergamot download **progress-observable** (bytes / total)? | §3.6 shows a percentage; otherwise the copy becomes `Downloading…` with no number, which for 50 MB is poor. |
| **OQ-10** | Can the app **count characters sent per key**, locally, per calendar month? | Would let About show `about 340k of 2M used this month` instead of my [ASSUMED] "20 to 40 hours". Optional, but it turns a guess into a fact. |
| **OQ-11** | (John) Does the offline engine ship **enabled-if-downloaded** or as a separate toggle? | §4.2 assumes downloaded ⇒ enabled, with `Remove` as the only off switch. Two controls for one decision would be noise. |
| **OQ-12** | (John) Do we keep the read-path Azure opt-in **off** for users migrating with an Azure key already saved? | I assume yes: an opt-in that arrives pre-ticked is not an opt-in. |

### Acceptance-criteria hints for the stories

1. `Cancelled` never reaches any surface: pressing Stop mid-translation leaves the feed rows and the status line
   untouched apart from `Live stopped.`
2. No row ever contains a countdown; exactly one countdown is visible per window at any time.
3. The LIVE heartbeat does not blink while the path is paused — assert on `LiveIndicator.Text` / `LiveDot.Text`.
4. `Done — {n} line(s) translated.` is unreachable when any line lacks a translation (unit-testable at the
   read-once status seam, `MainWindow.Ocr.cs:243-244`).
5. A successful fallback produces **no** error text anywhere — only the chip and one notice line.
6. Zero HTTP requests are issued while the path is paused (the existing `CountingTranslator` double,
   `tests/PWRUHelper.Tests/CachingTranslatorTests.cs:10-28`, is the tool).
7. The countdown `TextBlock` is assigned at most once per second, and not at all above 90 s except on a minute
   boundary.
8. Every new persisted control leaves settings unchanged across a load → apply → save cycle with no user
   interaction (the `_restoringSettings` regression test).
9. The update toast fires exactly once per version change and never in compact mode.
10. No string in the new copy deck contains an HTTP status code, a provider internal (`gtx`, `dict-chrome-ex`), or
    the word "error" without a following action.
11. Every user-facing string in §3 exists exactly once in the codebase (no ad-hoc re-phrasing at a call site) —
    this is what the ten strings of E1–E10 cost us.
12. **(A5)** No feed row ever contains a §3.1 sentence or a provider name: a source scan proves `Live.cs` and
    `Ocr.cs` write only §3.3a's three texts onto `TranslationBody`.
13. **(A3)** `{P}` is substituted **only** from `LastOutcome.ProviderId`; with `LastOutcome` null every sentence
    renders its `{P}`-less form and none of them contains an empty pair of spaces or a stray article.
14. **(A8)** The read-once button is enabled for the whole of a read, its label reads `Cancel read`, and a press
    ends the read — the STA test that E5.S4's three wiring tests could not be.
15. **(A6)** The auto-stop sentence carries the real `ConsecutiveFailures` count, and a run in which the five
    failures were pauses or refusals never renders it (rulings E5-c/E5-d).
16. **(A10)** `Clear cache` empties the store and deletes the file, and a debounced save queued before the click
    cannot recreate it.
