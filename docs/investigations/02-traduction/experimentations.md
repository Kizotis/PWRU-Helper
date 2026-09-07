# 02 — Google Translate probe: design, smoke result, and a decision request

_Phase 1 · Amelia (Dev, BMAD QD) · 2026-09-06_

Tool: [`tools/diagnostics/Probe-GoogleTranslate.ps1`](../../../tools/diagnostics/Probe-GoogleTranslate.ps1)
· end-user instructions in [`tools/diagnostics/README.md`](../../../tools/diagnostics/README.md).

## 0. What we already know (Phase 0, do not re-litigate)

- The exact user-visible string is `(Google is limiting translations right now - wait a minute and
  try again.)`, **in parentheses** → produced by `$"({Friendly(ex)})"` on the **OCR path** (LIVE or
  read-once), i.e. `MainWindow.Live.cs:281` / `MainWindow.Ocr.cs:298`. That message is only reachable
  from `TranslationService.cs:145-146`, which fires **only on a real HTTP 429 on the third attempt**.
  **[CONFIRMED]**
- The app has **no rate limiter, no circuit breaker, no cool-down, no persisted state** (F6), and
  restarting the app does not clear the symptom (owner, answer (c)). Therefore the state that
  "lasts minutes" is **entirely on Google's side, keyed on the public IP**. **[CONFIRMED + owner]**
- The app never reads `Retry-After`; its backoff is a fixed 300 ms then 600 ms, no jitter
  (`TranslationService.cs:153`). **[CONFIRMED]**
- The LIVE loop can issue **up to 2 requests per tick**, one tick every 0.5–3.0 s (default ≈0.7 s)
  → a sustained ~85–170 req/min, ×3 on retries, and a mismatched batch degrades to one request per
  line (F7). This is the most plausible trigger. **[CONFIRMED]**

What is **[UNKNOWN]** and what this probe exists to answer: **at what request rate does that IP get
429'd, does the 429 carry a `Retry-After`, and how long does it last?**

---

## 1. Probe design

### 1.1 Fidelity to the app's request

The probe uses `System.Net.HttpWebRequest` directly (not `Invoke-WebRequest`, which injects headers
of its own) so that every field can be pinned to what `Services/TranslationService.cs` sends:

| Field | App (`TranslationService.cs`) | Probe |
|---|---|---|
| URL | `https://translate.googleapis.com/translate_a/single?client=gtx&sl={src}&tl={tgt}&dt=t&q={enc}` (`:122-123`) | identical, same parameter order |
| `q` encoding | `HttpUtility.UrlEncode` (`:123`) | `[System.Web.HttpUtility]::UrlEncode` — the same method |
| Method | GET (`:131`) | GET |
| User-Agent | hard-coded Chrome 120 string (`:41-42`) | byte-identical copy |
| `Accept`, `Accept-Language`, `Referer`, cookies | **not set** | **not set** |
| HTTP version | framework default → HTTP/1.1 | `ProtocolVersion = 1.1` explicitly |
| Proxy | `HttpClient.DefaultProxy` = system proxy | `HttpWebRequest.Proxy` default = system proxy |
| Timeout | 12 s (`:39`) | `Timeout` and `ReadWriteTimeout` = 12 000 ms |

Captured per request: HTTP status, **every** response header (`Retry-After`, `Content-Type`,
`Server`, `Alt-Svc`, and the full list), body length, body *shape* (`json-array` / `html` / `empty` /
`other`), the first 300 characters of the body, and elapsed ms. One CSV row per request plus a text
summary.

The ten query strings are short generic Russian chat lines, rotated so consecutive requests are not
identical (an identical query could be served from a Google-side cache and hide the throttle). They
are stored base64-encoded so the `.ps1` file stays pure ASCII — PowerShell 5.1 reads a BOM-less
UTF-8 script as ANSI and would silently mangle literal Cyrillic, changing the request under test.

### 1.2 Modes

| Mode | Behaviour | Risk | Run? |
|---|---|---|---|
| `-Smoke` | 5 requests, 2 s apart | none | **done, below** |
| `-Burst` | up to `-Count` requests every `-IntervalMs`; stops at the **first 429 or 403**; then one request every `-RecoveryPollSeconds` (default 30 s) for up to `-MaxWaitMinutes` until a 200 comes back | **throttles the public IP** | **not run — owner's decision, §4** |
| `-Variant` | same as Burst but cycling four request fingerprints: `app` (Chrome 120 UA), `no-ua` (no User-Agent at all), `chrome-recent` (Chrome 131 UA), `app+lang` (Chrome 120 UA + `Accept-Language`) | same | not run |

Burst and Variant **refuse to start** without the explicit `-IUnderstandTheRisk` switch, and the
help text carries the warning in full.

### 1.3 Known limitation — TLS fingerprint

Windows PowerShell 5.1 runs on .NET Framework; the app runs on .NET 8. Both use Windows SChannel, so
the TLS handshakes are close, but they are not guaranteed byte-identical (JA3). If Google
discriminates on TLS fingerprint, a threshold measured by the probe is **indicative, not exact**: a
clean probe result does not prove the app is safe, and a probe 429 does not prove the app would have
been throttled at the same request count. Documented in the script header and in the summary file it
writes.

---

## 2. Smoke result — 2026-09-06 13:24–13:25 (dev box, Ethernet, no proxy)

Raw: `%USERPROFILE%\PWRU-Diagnostics\google-probe-smoke-GSIN-WS-DT004-20260906-132458.{csv,txt}`

| # | status | elapsed ms | body shape | body bytes |
|---|---|---|---|---|
| 1 | **200** | 438.8 | `json-array` | 74 |
| 2 | **200** | 362.1 | `json-array` | 168 |
| 3 | **200** | 406.0 | `json-array` | 152 |
| 4 | **200** | 62.3 | `json-array` | 87 |
| 5 | **200** | 337.8 | `json-array` | 164 |

Average on 200: **321.4 ms**. Request #4 at 62 ms is the connection being reused (keep-alive).
**`Retry-After` header: absent on all five.** **[MEASURED]**

**Response shape** (request #1, `sl=ru&tl=en`, "hello everyone"):

```
[[["hi all","<russian source echoed back>",null,null,10]],null,"ru",null,null,null,null,[]]
```

Exactly the `[[["translated","original",...], ...], ...]` shape the app's parser expects
(`TranslationService.cs:160+`). **[MEASURED]**

**Notable response headers** (identical across all five):

| Header | Value |
|---|---|
| `Server` | `ESF` (Google's Edge Serving Frontend) |
| `Content-Type` | `application/json; charset=utf-8` |
| `Content-Disposition` | `attachment; filename="json.txt"` |
| `Cache-Control` | `no-cache, no-store, max-age=0, must-revalidate` |
| `Alt-Svc` | `h3=":443"; ma=2592000, h3-29=":443"; ma=2592000` |
| `Transfer-Encoding` | `chunked` (no `Content-Length`) |
| `Vary` | `Accept-Encoding` |
| `Accept-CH` / `Permissions-Policy` | requests client hints: `Sec-CH-UA-Arch`, `-Bitness`, `-Full-Version`, `-Full-Version-List`, `-Model`, `-WoW64`, `-Platform`, `-Platform-Version`, `-Form-Factors` |
| `reporting-endpoints`, `Content-Security-Policy`, `X-Frame-Options`, `X-XSS-Protection: 0`, `Cross-Origin-*` | standard Google front-end set |

### What the smoke test establishes

1. **The probe is faithful and works.** It reproduces the app's request and captures everything
   needed to characterise a 429 when one occurs. **[MEASURED]**
2. **The endpoint is healthy from this IP right now.** No throttle in place; five requests two
   seconds apart are nowhere near any threshold. **[MEASURED]**
3. **Google advertises HTTP/3 (`Alt-Svc: h3`).** The app pins HTTP/1.1 by omission. A client that
   never upgrades is a (weak) fingerprint signal. **[MEASURED] fact, [UNKNOWN] whether it matters.**
4. **Google asks for User-Agent Client Hints** (`Accept-CH`). The app sends none, and its UA is a
   frozen Chrome **120** string — a browser that claims to be Chrome yet never returns a single
   `Sec-CH-UA-*` header is a plausible bot signal. **[MEASURED] fact, [UNKNOWN] whether it feeds the
   throttle.** This is what `-Variant` exists to test.
5. **No `Retry-After` on 200 responses** — which says nothing yet about whether a 429 carries one.
   That is the single most actionable unknown: if 429s *do* carry `Retry-After`, the fix in
   `TranslationService.RequestAsync` is small, obvious and correct (honour it instead of the fixed
   300/600 ms), and the user-facing "wait a minute" can become a real number.

---

## 3. What Burst / Variant would measure

| Question | How Burst answers it | Why it matters |
|---|---|---|
| **At what rate does this IP get 429?** | requests at a fixed interval until the first 429; the sequence number and elapsed time give a rate | Tells us whether the LIVE loop's ~85–170 req/min is above or below the line, and therefore whether the fix is "slow LIVE down" or "change providers". |
| **Does the 429 carry `Retry-After`?** | the header is captured verbatim on the blocking response | Decides a ~10-line fix in `RequestAsync`, and whether the app can tell the user a real duration instead of "wait a minute". |
| **What does a 429 body look like?** | body shape + first 300 chars | Distinguishes a genuine quota 429 from an HTML block/captcha page (which the app currently reports as "may be temporarily blocked"). |
| **How long does the block last?** | one request every 30 s after the block, up to `-MaxWaitMinutes`, stopping at the first 200 | The owner's users report "1 min, 10 min, or never". A measured duration turns that into a spec: how long a cool-down the app should enforce. |
| **Is it the fingerprint or the rate?** (`-Variant`) | four UA / header variants cycled through the same burst | If `chrome-recent` or `app+lang` survives longer, the cheap mitigation is a header change, not an architecture change. |

Without those numbers, `mecanismes-de-blocage-google.md` and `architecture-cible.md` will have to
rest on web research and inference alone.

---

## 4. ⚠ Decision requested from the owner

**Question: may we run `-Burst` (and possibly `-Variant`) from your connection?**

**What it does.** Sends requests to `translate.googleapis.com` as fast as the parameters say until
Google answers 429, then polls once every 30 s to see when it clears.

**The risk — stated plainly.**
- The rate limit is applied to your **public IP address**, not to the app.
- Everything on that connection is affected: your PC, your phone, anyone else in the house, **and
  PWRU Helper itself**.
- Duration is exactly what we do not know: the reports say minutes to "never clears in one session".
  Assume **it could last hours**.
- **You stream from this connection.** If a stream is planned, do not do it then.

**The benefit.**
- The 429 threshold (requests/minute) → tells us whether LIVE at default speed is inherently over
  the line.
- Presence and value of `Retry-After` → a small, correct fix to `TranslationService` instead of the
  current fixed 300/600 ms guess.
- Measured block duration → the app can enforce a real cool-down and display a real number instead
  of "wait a minute".
- The body of an actual 429 → confirms whether users are hitting a quota response or a block page.

**Three ways to say yes, in increasing order of exposure:**

| Option | Command | Exposure |
|---|---|---|
| **A — gentle** (recommended first) | `-Burst -Count 120 -IntervalMs 700 -MaxWaitMinutes 20 -IUnderstandTheRisk` | ~85 req/min for at most ~90 s — deliberately mimics the LIVE loop at its default speed. If this does **not** 429, LIVE alone is not the trigger, and that is already a finding. |
| **B — aggressive** | `-Burst -Count 300 -IntervalMs 200 -MaxWaitMinutes 60 -IUnderstandTheRisk` | ~300 req/min. Will almost certainly find the threshold. Longer block likely. |
| **C — fingerprint** | `-Variant -Count 200 -IntervalMs 500 -MaxWaitMinutes 30 -IUnderstandTheRisk` | as B, plus it answers the UA question. Run only after A or B has shown where the threshold is. |

**Safer alternatives if the answer is no** — all of them acceptable, none of them as good:
1. Run option A **from a different connection** (mobile hotspot, a friend's PC, a cheap VPS) — the
   threshold is per-IP, so any IP will do, and no one loses anything if it gets throttled.
2. Wait until a user reports the symptom **live**, and have them run `-Smoke` at that moment: five
   safe requests are enough to capture a 429 that is *already* in place, with its headers and body.
   This is the zero-risk path to the `Retry-After` answer, and it is worth asking for regardless.
3. Skip measurement entirely and design defensively: honour `Retry-After` when present, add
   exponential backoff with jitter, add a process-wide cool-down after a 429, and cap the LIVE
   request rate. All of that is correct engineering independent of the exact threshold — but the
   cap value would be a guess.

> **Nothing further will be run against Google without an explicit "go" naming the option.**
