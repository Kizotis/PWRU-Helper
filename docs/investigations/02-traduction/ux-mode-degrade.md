# 02 — P2 · Degraded-mode UX — provider status, messages, keys

_Phase 2 · author: **Sally** (BMAD UX Designer), menu item **CU** (`bmad-ux`, adapted to a single file) ·
peer document to Winston's `architecture-cible.md` · baseline `4759712` (main, v0.14.0) · 2026-09-06._

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
| **S4** Offline active | `● Offline engine` teal | `Translated on your PC (offline engine).` | `🔴 Live — using the offline engine.` | translation | `● Offline` prefix | `● Offline engine — active` |
| **S5** All paused | `○ All paused 4 min` gold | `All engines are paused — next try in about 4 min.` | `○ Live — paused, next try in about 4 min. It resumes on its own.` | pending rows stay `…`; given-up rows → `(not translated — all engines were paused)` | full sentence (short form) | `○ All engines paused — retries in about 4 min` |
| **S6** No network | `⚠ No internet` red | `No internet connection — check your network.` | `⚠ Live — no internet connection. It retries by itself.` | `…` | `⚠ No internet` | `⚠ No internet connection` |
| **S7** Key invalid | `⚠ DeepL key refused` red | `Your DeepL key was refused — using the free engines.` | (read path only if the Azure opt-in is on) | translation | `⚠ Key refused` | `✕ DeepL refused this key.` + the fix |
| **S8** Quota out | `● Google · Azure quota out` gold | `Your Azure free quota is used up — using the free engines.` | same when the opt-in is on | translation | short form | `⚠ Quota used up — resets on the 1st` |

Reading rules that make the table work:

- **The chip is the only always-on indicator.** The status lines carry a sentence **only when the state changes**,
  and the sentence is then left in place until the next change — no per-message repaint.
- **A row never carries a countdown.** Principle 5. Pending rows keep the existing `…` placeholder
  (`Live.cs:263`); the explanation lives in the status line and the chip.
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
Google        ○ paused — retries in 0:58   (asked us to slow down)
Edge          ● in use
Google (old)  ● ready
Offline       — not installed
Your keys     DeepL ● ready · Azure — not set
```

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

---

## 3. Copy deck

Rules for every string below: **English**, sentence case, no HTTP codes, no provider internals (`gtx`,
`dict-chrome-ex`, `429`), no exclamation marks, ≤ 80 characters wherever the overlay can show it. `{n}` is a count,
`{t}` a countdown rendered per §2.4, `{P}` a provider's user-facing name (`Google`, `Edge`, `DeepL`, `Azure`,
`Offline engine`).

### 3.1 One sentence per typed error kind

These replace the ten ad-hoc strings E1–E10 (`00-annexe-demarrage-et-reseau.md` §2.4) and the five DeepL ones.

| Kind | Sentence | Notes |
|---|---|---|
| `RateLimited` | `{P} asked us to slow down — paused for {t}.` | never "wait a minute": we now know the real number |
| `Blocked` | `{P} is refusing requests from your connection — paused for {t}.` | the honest wording for a 403 / captcha page |
| `Unavailable` | `{P} is down right now — trying another engine.` | 5xx |
| `Timeout` | `{P} took too long to answer — trying another engine.` | |
| `Network` | `No internet connection — nothing can be translated until it is back.` | the only kind with no `{P}` |
| `BadResponse` | `{P} sent something we could not read — trying another engine.` | |
| `QuotaExhausted` | `Your {P} free quota is used up — using the free engines instead.` | keys only |
| `AuthFailed` | `Your {P} key was refused — check it in About, or clear it.` | keys only |
| `Cancelled` | *(nothing)* | must never reach a surface |
| `AllProvidersPaused` | `All engines are paused — next try in {t}. Nothing you need to do.` | the single most important line in this document |

When a **fallback succeeded**, the error kind is *not* shown at all — the user got their translation. Only the chip
changes, plus the one-time notice of §3.5.

### 3.2 LIVE statuses

Replacing `MainWindow.Live.cs:235` and `:238`.

| Situation | String |
|---|---|
| running (unchanged) | `🔴 Live — watching (check #{n})…` |
| one failed read, still trying | `🔴 Live — one read did not translate, retrying…` |
| the preferred engine is paused, a backup serves | `🔴 Live — {P} paused ({t}), using {P2}.` |
| everything paused | `○ Live — paused, next try in {t}. It resumes on its own; nothing is lost.` |
| recovered | `🔴 Live — back on. Catching up on {n} message(s).` |
| auto-stopped, honestly | `Live stopped — {n} reads in a row failed. Press ▶ to try again.` |
| auto-stopped because everything is paused past the cap | `Live stopped — every engine is paused. Try again in {t}, or add the offline engine in About.` |

The auto-stop trigger itself is Winston's (`Live.cs:219` resets the counter on empty ticks, so today it never
fires in a calm chat — F-5). The UX requirement is only this: **when LIVE stops, the reason is in the sentence and
the way back is in the sentence.**

### 3.3 Read-once statuses (killing the false "Done")

Replacing `MainWindow.Ocr.cs:243-244`.

| Outcome | String |
|---|---|
| all lines translated | `Done — {n} line(s) translated.` (unchanged) |
| some translated | `Read {n} line(s) — {k} translated, {m} could not be. {reason}` |
| none translated | `Read {n} line(s) — none could be translated. {reason}` |
| none translated, everything paused | `Read {n} line(s) — all engines are paused. They will fill in when one is back.` |
| OCR itself failed | `Could not read the screen: {reason}` (today's `OCR failed:` prefix is developer-speak) |

`{reason}` is the sentence from §3.1, lower-cased at the join. **"Done" may only ever appear when every line has a
translation.** [CONFIRMED bug today: `Ocr.cs:296-299` returns instead of throwing, so `:244` prints Done over a
total failure.]

### 3.4 Compact overlay quick reply

Replacing `CompactOverlay.xaml.cs:142`. The overlay defaults to 360 px wide — these are the tightest strings in the
app; keep them under ~60 characters.

| Situation | String |
|---|---|
| generic failure (shape kept) | `⚠ {reason} — your text is kept, press Enter to retry.` |
| paused | `⚠ Engines paused ({t}) — your text is kept.` |
| no network | `⚠ No internet — your text is kept, press Enter to retry.` |
| key refused | `⚠ Your {P} key was refused — see About.` |

The existing promise — *your text is kept* — is the best sentence in the current app. Keep it verbatim wherever it
still applies.

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

```
The offline engine translates on your PC, with no internet at all.
It is a bit rougher than Google, and it is used only when every online
engine is unavailable.

Download          about 22 MB for the engine + about 30 MB per language pair
While translating it uses 130-310 MB of memory, freed when it goes idle
Stored in         %AppData%\PWRUHelper\models
To remove it      About tab → Offline engine → Remove (deletes the files)

                             [ Download (about 50 MB) ]   [ Not now ]
```

During the download the About row becomes `Downloading the offline engine… {p}%` with a `Cancel` button; on
completion, `● Offline engine ready — used only when everything else is unavailable.` If the download fails:
`Download failed — {reason}. Nothing was installed.`

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
   Chain        Google → Edge → Google (old) → Offline (not installed)

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
 ─────────────────────────────────────────────────────────────────────────────────
```

### 4.3 Control-by-control specification

| Control | Type | Notes |
|---|---|---|
| `DeepLKeyBox` | `PasswordBox` (exists, `MainWindow.xaml:621`) | unchanged; masked; restored in `ApplySettings` by `DeepLKeyBox.Password = s.DeepLApiKey` (`MainWindow.xaml.cs:185`) |
| `AzureKeyBox` | `PasswordBox` | **same pattern, verbatim** — a `PasswordBox` cannot be data-bound, and there is no MVVM here anyway |
| `AzureRegionCombo` | `ComboBox IsEditable="True"` | seeded with `global`, `westeurope`, `francecentral`, `northeurope`, `eastus`, `eastus2`, `westus2`, `uksouth`, `swedencentral`; free text accepted because Azure adds regions faster than we ship. Use the existing `SelectTag`/`SelectedTag` helpers (`MainWindow.xaml.cs:359-370`) — do not re-roll the loop [CONFIRMED rule, `project-context.md`] |
| `AzureForReadingCheck` | `CheckBox` | default **off**; changing it rebuilds the read chain immediately, like `DeepLSaveKey_Click` rebuilds the write chain (`Translate.cs:239`) |
| `TestKeyButton` ×2 | `Button` (`GhostButton`) | disabled while in flight, label → `Testing…`; result into the status line, never a `MessageBox`; a failed test **does not** clear the key |
| `OfflineEngineToggle` | `Button` (Download / Remove) | not a checkbox: the two actions have very different weight. `Remove` asks for confirmation with `MessageBox.Show(this, …)` and states that it deletes the files |
| `EngineChainText` | `TextBlock`, read-only | the "Chain" line; refreshed by `UpdateEngineStatusUi()` |

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
   about 4 min. Nothing you need to do.` The input keeps the user's text; the button stays enabled (pressing again
   just re-shows the sentence — no request, no punishment).
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
3. LIVE does **not** auto-stop for this: it keeps reading the screen (OCR is local and free) and retries the
   network on a slow cadence, so the moment the connection returns the feed catches up. Status:
   `⚠ Live — no internet connection. It retries by itself.` [ASSUMED — this differs from a provider pause on
   purpose: a network outage costs Google nothing and the user everything.]
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
