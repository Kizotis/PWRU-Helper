# P2 — Google blocking mechanisms on the free `translate_a/single` endpoint

_Technical research (BMAD TR workflow) · **Author:** Mary (BMAD Analyst) · **Date:** 2026-09-06 · **Baseline:** commit `4759712` (main, v0.14.0)_

## Scope

This document answers **why** PWRU Helper users see
`(Google is limiting translations right now - wait a minute and try again.)`
and what the community, Google's own terms, and Google's own error pages say about the mechanism behind it.

It is **research only**. It does not analyse the app's implementation (that is
[`analyse-implementation-actuelle.md`](analyse-implementation-actuelle.md)), does not compare providers (that is
[`benchmark-fournisseurs.md`](benchmark-fournisseurs.md)), and does not design the fix (that is
[`architecture-cible.md`](architecture-cible.md)). It supplies the **evidence base** those three documents consume.

## Method and constraints

- **No request was sent to any Google endpoint while producing this document.** Reproduction is owned by a separate
  agent, and hammering the owner's public IP is his decision, not mine. Every network-level statement below comes
  from published sources, not from my own traffic.
- ~30 web searches and ~20 page fetches, 2026-09-06, biased toward **dated primary sources** (issue trackers,
  Google's own forums and terms) over SEO blog content. Proxy-vendor blogs are used only where no primary source
  exists, and are graded accordingly — they sell the answer they give, which is a conflict of interest worth naming.
- Anchor facts about the app come from Phase 0: [`00-annexe-demarrage-et-reseau.md`](../00-annexe-demarrage-et-reseau.md)
  §2.1–2.9 and [`00-inventaire-stack.md`](../00-inventaire-stack.md) §8–9. They are cited as **[CODE]**.

## Grading legend

| Grade | Meaning |
|---|---|
| **CONFIRMED** | Official Google documentation/terms, a Google-authored error string, or a reproducible published fact (a library's source code, a documented endpoint). |
| **REPORTED** | Issue trackers, forums, vendor blogs, community docs. Real observations, but uncontrolled, undated-in-part, and sometimes second-hand. |
| **ASSUMED** | My inference from the above. Stated so it can be falsified, never presented as fact. |
| **[CODE]** | Read from this repository at commit `4759712` in Phase 0. |

---

# Executive answers

1. **`translate_a/single?client=gtx` is an undocumented internal endpoint of the Google Translate web app.** Google has
   never published it, never promised it, and a Google community moderator has explicitly told a developer not to use it
   (2023-08-11). It nevertheless still worked in February 2026. **CONFIRMED / REPORTED.**
2. **The 429 the owner sees is a genuine, Google-side, IP-keyed throttle.** Google's own block page says so in words:
   _"Our systems have detected unusual traffic from your computer network… The block will expire shortly after those
   requests stop."_ **CONFIRMED** (Google-authored string, quoted in a 2021 issue).
3. **The throttle is keyed primarily on the public IP address**, secondarily on request rate/pattern, IP reputation
   (datacenter/VPN/proxy ranges are pre-penalised), and header plausibility. It is *not* keyed on anything the app can
   rename: Windows code signing, the exe's name, the MSI vs portable choice are all invisible to Google. **REPORTED,
   converging across every independent source.**
4. **"Restarting the app doesn't help" is exactly what the mechanism predicts** — the state lives on Google's side,
   attached to the IP, not in the process. **CONFIRMED by the app's own code [CODE]** (no cool-down state exists) **and
   by Google's block text.**
5. **The app's retry policy actively extends the block.** Google's rule is "expires shortly after the requests stop";
   the app answers a 429 with two more requests 300 ms and 600 ms later, and the LIVE loop then tries again 700 ms
   later. Nothing ever stops. **ASSUMED, but a direct consequence of a CONFIRMED Google statement and CONFIRMED code.**
6. **Reported durations range from "a few minutes" to "12–24 hours"**, with no published figure from Google. The
   owner's "1 min / 10 min / never" fits that range, and the "never" case is best explained by point 5.
7. **Why the neighbour is fine:** the block follows the IP, and since January 2025 **all four major French ISPs share
   one public IPv4 between dozens or hundreds of subscribers (CGNAT)**. Two users of PWRU Helper on different lines can
   be on the same Google-visible IP — or on different ones from the same house at different times. **CONFIRMED**
   (ISP behaviour) **/ ASSUMED** (that it explains this specific variance).
8. **ToS verdict: the app is outside Google's API terms.** §2(c) of the Google APIs ToS: _"You will only access (or
   attempt to access) an API by the means described in the documentation of that API."_ There is no documentation for
   this one. **CONFIRMED.** No enforcement beyond throttling has been reported.
9. **Evidence-backed mitigations:** a real cool-down after a 429 (persisted across restarts), fewer/larger requests,
   caching, and a hard request-rate ceiling. **Folklore:** rotating User-Agents, spoofing TLS fingerprints, changing
   the `client=` value — none of these move an IP-keyed counter.
10. **The only robust fixes are an official API key (Google Cloud Translation: 500 000 characters/month free, forever)
    or a local model.** Everything else is harm reduction. **CONFIRMED.**

---

# Q1 — What exactly is `translate_a/single?client=gtx`?

## Origin and status

The path `/translate_a/single` is the JSON RPC the Google Translate **web page** calls for a single-segment
translation. It is served from `translate.googleapis.com` — a host Google *does* document, but for the **Cloud
Translation v3 REST API** (`/v3/projects/*:translateText`). The `/translate_a/*` paths on that same host appear in
**no** Google documentation. This distinction matters and is routinely confused in forum answers: *the host is
official; the path is not.*

`client=` identifies the calling surface. The community-known values:

| `client=` | Host commonly used | Token (`tk`) required? | Notes |
|---|---|---|---|
| `gtx` | `translate.googleapis.com/translate_a/single` | **No** | The value PWRU Helper uses. Adopted across the ecosystem precisely because it skips token generation. |
| `t` | `translate.google.com/translate_a/single` | **Yes** | Missing/stale `tk` → 403 or captcha. The historical source of "the library broke again". |
| `webapp` | `translate.google.com` | Yes | The live web app's own value; same token problem. |
| `dict-chrome-ex` | `clients5.google.com/translate_a/t` | No | Extracted from Google's own *Google Dictionary* Chrome extension. Rejects other `client` values with 403. Different response shape. |
| `at` / `android` | various | No | Mobile-client values seen in the wild; less documented. |
| (none — page scrape) | `translate.google.com/m` | No | The mobile HTML page, scraped rather than parsed as JSON. |
| batch RPC | `translate.google.com/_/TranslateWebserverUi/data/batchexecute` | Effectively yes (`f.req` payload) | What the modern web app uses. Reported as **less** aggressively throttled than the single endpoint. |

## Historical timeline

| Date | Event | Grade | Source |
|---|---|---|---|
| 2015-03-31 | `translate-shell` finds Google **browser-sniffs** `/translate_a/t`: a request with `curl/7.37.1` as UA gets **403 Forbidden** _"Your client does not have permission to get URL …"_; adding any custom UA returns 200. | **REPORTED** (reproducible recipe) | [S6] |
| 2018-11-29 | `matheuss/google-translate-api` PR #87 "change client to gtx", opened to close issue #79 *"Response code 403 (Forbidden)"*. The ecosystem's pivot to `gtx` begins. | **REPORTED** | [S7] |
| 2020 (through) | `py-googletrans` repeatedly breaks on token acquisition (issue #234 era); the documented remedy becomes "use the direct `translate.googleapis.com` endpoint, which needs no token". Its docs still warn: _"Due to limitations of the web version of google translate, this API does not guarantee that the library would work properly at all times"_ and _"If you get HTTP 5xx error … it's probably because Google has banned your client IP address."_ | **CONFIRMED** (library docs) | [S32] |
| 2020-11 | Google switches the web app to the `batchexecute` RPC scheme; `gTTS-token` documents the change. Libraries split into "old `translate_a`" and "new `batchexecute`" camps. | **REPORTED** | [S24] |
| 2021-01-03 | `dict-chrome-ex` on `clients5.google.com/translate_a/t` published as a no-key alternative — _"Needs to be `dict-chrome-ex` or else you'll get a 403 error"_, and _"this one is also annoyingly touchy with 403s."_ | **REPORTED** | [S2] |
| 2021-03-19 | `translate_onhover` #74: after switching **to** `translate.googleapis.com/translate_a/single`, requests fail **429**, carrying Google's block page. The reporter notes the **older** `clients5.google.com/translate_a/t` endpoint was still working un-throttled at the same moment, from the same machine. | **REPORTED** (with a Google-authored quote → the quote itself is CONFIRMED) | [S1] |
| 2023-08-11 | On Google's own developer forum, a moderator answers a developer asking whether this endpoint may be used commercially: Google _"does not take ownership of such published articles or documentation"_; _"Once Google detects unusual traffic from their APIs, surely there will be repercussions such as immediate suspension."_ | **CONFIRMED** (Google-hosted forum, moderator answer) | [S3] |
| 2024-06-21 | `deep-translator` #263 asks for the character/request limits; **no documented answer exists**, from the library or from Google. | **REPORTED** | [S13] |
| 2026-02-11 | A published walkthrough uses `translate.googleapis.com/translate_a/single?client=gtx&…` and states it **works without the `tk` token**, returning `Content-Type: application/json`. | **REPORTED** | [S9] |
| 2026-05-24 | Immersive Translate #3913: users still hitting 429 on Google-backed translation; the maintainers' FAQ describes Google's free API as _"an older, deprecated service that no longer receives updates"_. | **REPORTED** | [S10][S11] |

**Verdict on Q1:** the endpoint is alive in 2026, undocumented, unsupported, unversioned, and explicitly disowned by a
Google moderator. Its continued life is a fact, not a promise. `gtx` is popular because it is the one `client` value
that never needed a token — which is also why it is the one value most heavily used by automation, and therefore the
one most worth throttling.

### Q1 findings table

| Finding | Grade | Source | Date |
|---|---|---|---|
| `/translate_a/single` appears in no Google documentation; `translate.googleapis.com` is documented only as the Cloud Translation v3 host | CONFIRMED | [S34] | accessed 2026-09-06 |
| `client=gtx` works without a `tk` token; `client=t`/`webapp` require one | REPORTED (consistent across 5+ independent libraries) | [S2][S7][S8][S9] | 2018→2026 |
| `client=dict-chrome-ex` comes from Google's Dictionary Chrome extension and 403s on any other `client` value | REPORTED | [S2] | 2021-01-03 |
| Google browser-sniffs these paths: no UA, or a default tool UA → 403 | REPORTED (reproducible) | [S6] | 2015-03-31 |
| A Google forum moderator advises against using it and warns of suspension | CONFIRMED | [S3] | 2023-08-11 |
| The endpoint still returns valid JSON in Feb 2026 | REPORTED | [S9] | 2026-02-11 |
| The `batchexecute` endpoint is throttled less aggressively than the single endpoint | REPORTED (single vendor claim, unverified) | [S24] | accessed 2026-09-06 |

---

# Q2 — What does Google key the throttle on?

## The response the owner is actually getting

`translate_onhover` #74 [S1] quotes the body Google returned **with the 429**:

> _"Our systems have detected unusual traffic from your computer network. Please try your request again later…
> **The block will expire shortly after those requests stop.**"_

That is Google's own wording, and it is the single most useful sentence in this entire document. It states the key
(*your computer network* — the IP), and it states the release condition (*after those requests stop*).

Note the shape: the block arrives as **HTTP 429 with an HTML body**. In PWRU Helper this matters less than it looks —
the code branches on the status code before it ever tries to parse, so a 429 becomes E2 ("wait a minute") and never
reaches the JSON parser, while an HTML page served with a **200** would become E4 ("unexpected response") [CODE].
The owner's screenshot is E2, so the status line really was 429. That is a *hard* datum: it rules out captcha
interstitials, proxy pages and TLS interception, all of which would have produced E4, E1 or E5.

## Signals, ranked by evidence strength

| Signal | Does it drive the block? | Grade | Notes |
|---|---|---|---|
| **Public IPv4/IPv6 address** | **Yes — primary.** | CONFIRMED (Google's own text names "your computer network") + REPORTED everywhere | Every library that documents 429 documents it as per-IP: _"If too many requests are made from the same IP address, you will get a TooManyRequestsError (code 429)"_ [S5]. |
| **Request rate / burst pattern** | **Yes.** | REPORTED (unanimous) | Community mitigations converge on a fixed inter-request delay — 500 ms is the most commonly hard-coded value [S23]. |
| **IP reputation / ASN class** | Yes, as a prior. | REPORTED | Datacenter and VPN ASNs are pre-scored as automation; residential IPs get much more headroom [S18][S33]. A home French ISP line is in the *good* class, which is why the owner gets throttled rather than 403-blocked outright. |
| **Shared IP ("bad neighbour")** | Yes. | REPORTED | On CGNAT/public Wi-Fi, one abusive user's behaviour lands on everyone sharing the address [S15]. See Q4. |
| **User-Agent plausibility** | **Present/absent: yes. Which one: no.** | REPORTED (2015 recipe) | An absent or `curl/x.y` UA → 403 [S6]. A plausible browser UA is enough. Nothing in the record suggests *which* plausible UA changes anything. |
| **Missing `Accept` / `Accept-Language`** | No evidence either way. | ASSUMED (weak) | The app sends neither [CODE]. No source found linking these headers to a 429 on this endpoint. Cheap to add, unlikely to be the cause. Do not sell it as a fix. |
| **Cookies / consent (`CONSENT`, `SOCS`)** | Not for this path. | ASSUMED | Consent redirects are documented for `google.com`/`translate.google.com` **HTML pages** in the EU [S-consent], not for the `translate_a` JSON path. The app sends no cookies and gets JSON back normally, so it is evidently not gated on one. |
| **TLS fingerprint (JA3/JA4) + HTTP/2 profile** | A real detection vector in general — but almost certainly **not the cause here**. | REPORTED (mechanism) / ASSUMED (irrelevance here) | The app's handshake is .NET `SocketsHttpHandler`'s, not Chrome's, despite the Chrome-120 UA — a mismatch anti-bot systems are explicitly built to catch [S18][S19]. But a fingerprint mismatch produces a *bot* verdict (403/captcha), not a *rate* verdict (429), and the mismatch is constant while the symptom is intermittent. |
| **HTTP/1.1 vs HTTP/2** | No evidence. | ASSUMED | Part of the fingerprint story above; same reasoning applies. |
| **Request size** | Indirectly. | REPORTED | ~5000 characters per request is the widely-cited ceiling for this endpoint [S8][S14]; over it you get 413, not 429. The app caps at 1500 **bytes** [CODE] — comfortably under. |
| **Windows code signing / MSI vs portable / exe name** | **No. Invisible to Google.** | CONFIRMED by construction | Google sees a TCP connection, a TLS handshake, an HTTP request and a source IP. Authenticode signatures live in the PE file on the user's disk and are never transmitted. Signing fixes SmartScreen (a P1 concern); it cannot touch P2. |

## Does Google send `Retry-After`?

**No evidence found that it does on this endpoint**, and no library in the survey reads it. The HTTP spec makes it
optional on 429 [S30]. PWRU Helper does not read it either [CODE]. **Grade: UNKNOWN — and it is worth one line of
instrumentation to settle**, because if Google *does* send it, it converts every guess in Q3 into a measurement.

### Q2 findings table

| Finding | Grade | Source | Date |
|---|---|---|---|
| Google's block text names the *computer network* and says the block expires after the requests stop | CONFIRMED (Google-authored string) | [S1] | 2021-03-19 |
| 429 arrives with an HTML body, not JSON | REPORTED | [S1] | 2021-03-19 |
| Library docs describe the 429 as per-source-IP | REPORTED (unanimous) | [S5][S14][S23] | 2021→2026 |
| Absent/`curl` User-Agent on `translate_a` → 403 | REPORTED (reproducible) | [S6] | 2015-03-31 |
| Datacenter/VPN ASNs are pre-flagged; residential IPs get more headroom | REPORTED (vendor blogs — conflicted source) | [S18][S33] | accessed 2026-09-06 |
| .NET's TLS fingerprint differs from Chrome's regardless of the UA string | CONFIRMED (mechanism) | [S18][S19] | accessed 2026-09-06 |
| Code signing and packaging are not transmitted to Google | CONFIRMED (by construction) | — | — |
| Whether Google sends `Retry-After` here | **UNKNOWN** | — | — |

---

# Q3 — How long does the block last?

Google publishes **nothing**. Every figure below is a report, and they disagree — which is itself the finding.

| Reported duration | Context | Grade | Source | Date |
|---|---|---|---|---|
| _"shortly after those requests stop"_ | Google's own block page | **CONFIRMED** (as a statement of policy; the word "shortly" is not quantified) | [S1] | 2021-03-19 |
| "a few minutes to more than 12–24 hours, as each case is different" | Knowledge-base article on Google Translate HTTP codes | REPORTED | [S14] | undated, accessed 2026-09-06 |
| "blocks your IP for one hour" | Repeated in several secondary write-ups about the free endpoint | REPORTED (weak — no primary source ever cited) | [S-hour] | accessed 2026-09-06 |
| Cleared by switching network node / VPN exit | Immersive Translate FAQ: _"your current network node has been rate-limited by Google. Switching nodes is recommended."_ | REPORTED (maintainer-authored) | [S10] | accessed 2026-09-06 |
| Cleared by VPN | `translate-shell` #370: user hit a limit after 6 files, worked around it with a VPN | REPORTED | [S25] | 2020-09-12 |
| "about 2 weeks" | A Google *"unusual traffic"* block on a consumer connection (YouTube, not Translate) | REPORTED (outlier; different product) | [S-toms] | forum post, undated |
| Persisted across settings changes, no resolution | Immersive Translate #3913 | REPORTED | [S11] | 2026-05-24 |
| Daily quotas reset at midnight Pacific | Official Cloud Translation quotas — **the paid API, not this endpoint** | CONFIRMED but **not applicable** | [S21] | accessed 2026-09-06 |

## Does it escalate?

**ASSUMED yes, weakly.** No source measures escalation on this endpoint. The general anti-abuse literature describes
score-based systems where repeated offences raise the score and lengthen the response [S18][S33]. The owner's own
observation — 1 minute, then 10 minutes, then "never" — is *consistent* with escalation, and equally consistent with
the simpler explanation below. Both should be tested; neither should be asserted.

## The simpler explanation for "never clears"

Google says the block expires **after the requests stop**. PWRU Helper never stops:

- a 429 triggers 2 more requests within 900 ms [CODE];
- if LIVE is running, the next tick fires 700 ms later and does it again [CODE];
- there is no cool-down, no circuit breaker, no persisted state anywhere [CODE].

A user who leaves LIVE running, or who clicks "read once" every few seconds because "it's still not working",
**continuously re-arms the very condition that keeps the block alive**. That predicts exactly the reported
distribution: short when the user walks away, long when they keep trying, "never" when LIVE was left on.
**Grade: ASSUMED — but it is a direct composition of one CONFIRMED Google statement and confirmed code, and it is
falsifiable in ten minutes** (stop everything for 15 minutes without touching the app, then send one request).

## Does solving the captcha in a browser help?

**Almost certainly not, for the app's requests.** Solving the challenge issues a *cookie* to that browser
[S15][S-captcha]; PWRU Helper sends no cookies and shares no session with any browser [CODE]. If a residual IP-level
penalty exists it may decay independently, but the pass itself does not transfer. **Grade: ASSUMED, mechanism-based,
supported by two secondary sources.**

## Does changing the IP clear it?

**Reported yes**, consistently — VPN [S25], different network node [S10], mobile hotspot / router restart for
dynamic-IP lines [S15][S-net]. This is the strongest practical confirmation that the state is IP-keyed. It is also
**not a recommendation**: telling users of a free community tool to run a VPN to reach a free endpoint is neither
sustainable nor honest.

---

# Q4 — Why does it vary between machines and users?

| Cause | Applies here? | Grade | Detail |
|---|---|---|---|
| **CGNAT — shared public IPv4** | **Very likely.** | **CONFIRMED (ISP behaviour) / ASSUMED (that it explains this case)** | Since **January 2025 Orange enables Carrier-Grade NAT by default** on Livebox; **Bouygues Telecom, SFR and Free have done so for years**. One public IPv4 is shared by "several dozen or even hundreds of subscribers". The article names the consequence explicitly: sharing _"peut conduire à des blocages si un abonné se fait bannir d'un service"_ [S16][S17]. |
| **Dynamic IP reassignment** | Likely. | REPORTED | A router reboot or a lease renewal can move a user off a hot address — or onto one. This alone can make the same user "blocked" one evening and fine the next, with no change in behaviour [S15]. |
| **Other software on the same IP** | Plausible and invisible to the user. | ASSUMED | Browser translate extensions, Immersive Translate, `trans`, scrapers, another PWRU Helper instance — all hit the *same* endpoint from the *same* address. The user's own second PC counts. |
| **Different usage patterns** | **Confirmed as a differentiator.** | [CODE] | A Phrasebook-only user issues **zero** requests. A Translator-tab user issues **1 per Enter**. A LIVE user at the shipped default issues **≈85/min**, and up to **≈240/min** at slider 100 %. That is a 4-orders-of-magnitude spread between two people "using the same app". |
| **VPN / corporate exit nodes** | Ruled out for the reported machines. | [CODE]/owner | The owner reports personal machines, Defender only, no proxy. But any user who *does* run a VPN shares an exit IP with everyone else on that node — the single most reliable way to be permanently throttled [S10][S18]. |
| **IPv6 vs IPv4** | Unknown for this endpoint. | ASSUMED | Correctly built rate limiters bucket IPv6 by **/64 (and /56, /48)** prefix rather than by address [S20]. If Google does the same, an IPv6-connected user is throttled per *line*, and a dual-stack machine may silently switch families between sessions and appear to "reset". Worth capturing in diagnostics (which family was used) — cheap, and it removes a whole class of confusion. |
| **DNS / different front-ends, time of day** | No supporting evidence. | ASSUMED (low) | Anycast means different users reach different edges, but the throttle counter is logically per-identity, not per-edge. Do not chase this. |

---

# Q5 — Legality, terms of service, sustainability

## What the terms say

**Google APIs Terms of Service** (last modified **2021-11-09**) [S4]:

- §2(c): _"You will only access (or attempt to access) an API by the means described in the documentation of that API."_
  There is **no documentation** for `/translate_a/single`. **CONFIRMED: the app is outside these terms.**
- §2(d): _"Google sets and enforces limits on your use of the APIs (e.g. limiting the number of API requests that you
  may make…)"_ and _"You agree to, and will not attempt to circumvent, such limitations documented with each API."_
  **This makes the 429 contractually expected behaviour, not a malfunction.**
- §3(a): _"GOOGLE MAY MONITOR USE OF THE APIS…"_ and _"Google may suspend access to the APIs by you or your API Client
  without notice if we reasonably believe that you are in violation of the Terms."_

**Google's general Terms of Service** additionally prohibit accessing services _"through the use of any automated means
(such as robots, spiders or scrapers)"_ [S-tos].

## What Google has actually said about this endpoint

The only direct answer found is on Google's own developer forum, 2023-08-11 [S3]. A developer asked whether
`translate.googleapis.com/translate_a/single` could be used commercially and whether limits were documented. The
moderator's answer, in substance: Google does not own or endorse the third-party articles describing it; unusual
traffic will draw repercussions "such as immediate suspension"; use it at your own risk. **No rate limits were given,
because none exist to give.**

## Sustainability assessment

| Question | Answer | Grade |
|---|---|---|
| Is `gtx` alive in September 2026? | Yes — last public confirmation 2026-02-11; the app itself works most of the time. | REPORTED |
| Could it break without notice? | Yes. It has repeatedly broken for `client=t`/`webapp` (token changes, 2020 `batchexecute` migration), and Google owes no notice. | CONFIRMED (history) |
| Is there a legal risk to the project? | Practically negligible at this scale; the realistic enforcement is exactly what is already happening — throttling. No report was found of any action beyond 403/429 against a client of this endpoint. | ASSUMED |
| Is there a *product* risk? | **Yes, and it is the real one.** A free Windows app whose core feature depends on an endpoint that can vanish in a Tuesday deploy is one deploy away from a broken release, with no rollback available to the maintainer. | ASSUMED (strong) |
| Recommended path by the community | Use the official API, or run translation locally. `py-googletrans`'s own docs say it: _"If you want to use a stable API, I highly recommend you to use Google's official translate API."_ | CONFIRMED (library docs) |

---

# Q6 — Mitigations others use (reported, not endorsed)

| Mitigation | Does it address an IP-keyed rate throttle? | Grade | Evidence |
|---|---|---|---|
| **Fixed minimum interval between requests** (500 ms is the canonical hard-coded value) | **Yes — directly.** The single most common measure in real code. | REPORTED (strong: appears in independent implementations) | [S23] `self.rate_limit = 0.5  # 500ms between requests to avoid rate limiting` |
| **Cool-down / circuit breaker after a 429** | **Yes — the only mitigation that matches Google's stated release condition** ("expires after the requests stop"). | ASSUMED (strong) — composition of [S1] + [S10] | Immersive Translate's advice reduces to "stop using this path for a while" [S10]. |
| **Exponential backoff with jitter, honouring `Retry-After`** | Yes, and it is the textbook answer for 429 generally. | CONFIRMED (as an HTTP practice) [S30]; REPORTED (as advice for this endpoint) | The app currently does linear 300/600 ms with no jitter and ignores `Retry-After` [CODE] — too short to matter and synchronised across all users. |
| **Batching / coalescing (fewer, larger requests)** | Yes — fewer requests is fewer chances to be counted. | REPORTED | ~5000 characters/request is the cited ceiling [S8][S14]; the app currently caps at 1500 bytes and already joins lines with `\n` [CODE]. The *batch* RPC is reported as less throttled than the single one [S24]. |
| **Local caching of results** | Yes — every cache hit is a request not made. | CONFIRMED (trivially) | The app already caches (LRU 500, in-memory, successes only, **lost on exit**) [CODE]. |
| **Alternating endpoints** (`gtx` ↔ `dict-chrome-ex` ↔ `/m` page) | **Partially, and unreliably.** One 2021 report [S1] found the older host still working while the new one was throttled — suggesting per-service counters. But it doubles the surface that can break, and it is closer to evasion than engineering. | REPORTED (single dated observation) | [S1][S23] |
| **Rotating User-Agents** | **No.** A *plausible* UA is required [S6]; *rotating* it does not move a per-IP counter. Present in library code because it is cheap, not because it is shown to work. | **Folklore** for this symptom | [S23] rotates 6 UAs; no source demonstrates an effect on 429. |
| **Spoofing the TLS/JA3 fingerprint, forcing HTTP/2** | **No, for a 429.** Relevant to *bot* verdicts (403/captcha). In .NET it would mean shipping a native curl-impersonate-class dependency — a large cost against a symptom it does not target, in an app whose footprint is a product requirement. | **Folklore for this case** (real mechanism, wrong disease) | [S18][S19] |
| **Proxies / proxy rotation** | Technically yes; **rejected**. It shifts the cost onto third parties, breaks the moment a free proxy list rots, is an explicit ToS-circumvention step, and is indefensible in a free community app the owner's name is on. Named here only because it is what most libraries suggest first [S5][S8][S10]. | REPORTED — **do not adopt** | [S5][S8] |
| **Official API key (Google Cloud Translation)** | **Yes — removes the problem class entirely.** 500 000 characters/month free, permanently; $20/M after. Documented quotas, documented errors, `Retry-After`, and a support channel. | **CONFIRMED** | [S22][S21] |
| **Local/offline model** (Bergamot/Firefox Translations class, `en↔ru` models exist; CPU-only, 1000–9000 words/s on 2012–2019 consumer hardware) | **Yes — removes the network entirely.** Quality below Google's, and it costs disk and RAM, which is a product constraint here. | CONFIRMED (project claims) | [S28] |
| **Other hosted free tiers** (LibreTranslate self-host, MyMemory ~5 000 chars/day) | Situational; covered in the benchmark document. | REPORTED | [S29] |

**The honest summary:** every mitigation in the top half of that table buys headroom. None of them makes an
unauthenticated, undocumented endpoint reliable, because reliability is not a property the endpoint has. The only
structural fixes are an authenticated API or a local model.

---

# Q7 — Plain-language explanation for the owner

**What is actually happening.** When the app translates, it asks the same free Google address the Google Translate web
page uses — without a key, without an account. Google allows this, but it counts. It counts **per internet
connection**, not per user, per app or per PC. When the count over a short window looks like a machine rather than a
person, Google stops answering and returns "too many requests". That is the message you see. Nothing is broken in the
app; Google is deliberately saying "not right now".

**Why your app shows it "at launch".** It never really does. The app translates nothing on startup. What you see is
the *first* thing you do after launching — usually the screen reader — hitting a block that was already in place
before you closed the app the last time.

**Why restarting doesn't help.** The block is not stored in the app. It is stored at Google, attached to your internet
connection. Closing and reopening the app changes nothing Google can see. The app has no memory of it either — it will
happily start firing requests again the second you press a button, which is exactly the problem below.

**Why it sometimes lasts a minute and sometimes seems to never end.** Google's own block message says the block
"expires shortly after those requests stop". The app currently never stops: when it gets refused, it immediately tries
twice more, and if LIVE mode is on it tries again every 0.7 seconds, for as long as you leave it on. Each attempt tells
Google "still here", and the countdown restarts. Wait quietly for a few minutes with LIVE off and it clears. Keep
retrying and it never does. That is very probably the whole story of your "never" cases — and it is testable: leave the
app completely alone for fifteen minutes, then do a single translation.

**Why your neighbour never sees it.** Two reasons, both outside the app.
First, **LIVE mode is not like the other features.** Clicking a phrase sends nothing. Typing a sentence and pressing
Enter sends one request. LIVE mode sends up to **85 to 240 requests per minute**. Someone using the Phrasebook and the
Translator tab will essentially never be blocked; someone running LIVE during a raid is in a completely different
league of traffic.
Second, **you may not have your own address.** Since January 2025 all four big French providers — Orange included, and
Free, SFR and Bouygues for years before that — put dozens or hundreds of subscribers behind one shared public address.
Google counts that shared address. You can be throttled because of what a stranger on your provider did, and your
neighbour on another provider is untouched. It also works the other way: your connection can quietly change address
overnight and the problem "fixes itself".

**What can be done.** Realistically, three things, in order of cost:
1. **Make the app stop when Google says stop** — a real pause after a refusal, remembered even if the app is closed and
   reopened, instead of retrying into a wall. This is the single biggest win and it is entirely in our hands.
2. **Send fewer, bigger requests** — group more lines per request, cache more aggressively and keep the cache between
   sessions, and cap how fast LIVE mode is allowed to ask, especially at the top of the speed slider.
3. **Stop depending on the free door** — a free Google Cloud key covers 500 000 characters every month, forever, with
   published rules and no surprise blocks; or translate locally on the PC with no network at all. These are the only
   two options that make the problem *go away* instead of *get rarer*.

**What cannot be done.** Signing the app, renaming it, shipping it as an installer instead of a portable exe, changing
the browser name it announces, or making it look more like Chrome — none of these are visible to Google in a way that
affects this. Google sees your internet address and how fast requests arrive. That is the entire conversation. And
routing users through proxies or VPNs to hide the address is not something a free, public, name-on-it community tool
should do.

---

# Implications for the target architecture

Handed to `architecture-cible.md` as constraints, with the evidence grade attached to each.

## Evidence-backed (build these)

1. **A persisted, IP-scoped cool-down (circuit breaker) on 429.** Google's release condition is "requests stop"
   [S1, CONFIRMED]. The app's current answer to a 429 is 2 more requests in 900 ms plus a LIVE tick 700 ms later
   [CODE]. This is the only change directly targeted at the CONFIRMED mechanism.
   - **Suggested default window: 60 s on the first 429, doubling per consecutive 429 (60 s → 2 min → 4 min → 8 min),
     capped at 30 min, reset after 10 minutes clean.** Rationale: the reported floor is "a few minutes" and the
     reported ceiling is 12–24 h [S14]; a 60 s first step matches the app's own user-facing promise ("wait a minute"),
     and the cap stays below the point where a user would rather restart than wait. **Grade of the numbers themselves:
     ASSUMED — they are calibrated to a REPORTED range, not measured. Instrument first, tune after.**
   - **It must survive process exit** (a small file next to `settings.json` with `blockedUntil` + strike count).
     The owner's own observation that restarting does not clear the block is the proof that in-memory state is the
     wrong scope [owner, CONFIRMED].
   - It must gate **both** translator chains (`_readTranslator` and `_writeTranslator` share one endpoint but not one
     cache [CODE]).
2. **A hard client-side rate ceiling, always on.** 500 ms minimum spacing is the ecosystem's converged value
   [S23, REPORTED]. The app's shipped default already exceeds it in aggregate (≈85 req/min ≈ 1 per 700 ms) but the
   slider at 100 % and the 2-groups-per-tick path can double it [CODE]. A ceiling below the tick rate is the cheap
   structural fix.
3. **Respect `Retry-After` if present, and back off exponentially with jitter if not.** Current: linear 300/600 ms, no
   jitter, header ignored [CODE]. Jitter matters because every PWRU Helper instance currently retries on the same
   fixed schedule.
4. **Persist the cache across sessions.** Today it is in-memory, capacity 500, and dies with the process [CODE]. A
   Perfect World chat repeats itself constantly; a disk cache converts the second session's traffic into near-zero.
   Cheap, zero-risk, and it directly reduces the count Google keeps.
5. **Keep a plausible browser User-Agent. Do not rotate it.** Absent/`curl`-style UA → 403 is the one dated,
   reproducible header finding [S6, 2015]. Rotation is folklore for a 429 [Q6].
6. **Log enough to end the guessing.** Today `TranslationService` logs nothing [CODE], which is why Phase 0 had to ask
   the owner what the message said. Minimum: timestamp, status code, whether `Retry-After` was present and its value,
   response body first ~200 chars, which path (LIVE / read-once / Translator), request count in the last 60 s, and the
   IP family used (v4/v6, see Q4). Redact `q`. This single change makes the next occurrence a measurement instead of
   another investigation.

## Explicitly *not* worth building (folklore, or wrong disease)

- User-Agent rotation, `client=` value rotation, endpoint alternation as a *primary* strategy, TLS/JA3 impersonation,
  forcing HTTP/2, adding `Accept-Language` "to look more human". None targets an IP-keyed rate counter, and the last
  three cost real complexity in a codebase whose stated virtue is a tiny footprint.
- Proxies of any kind. Ruled out on principle above.
- Multi-`q=` batching on the Google endpoint remains a **declined non-feature** per `project-context.md`; it is
  mentioned in Q6 only because the sources raise it. Not proposed.

## Open items this research could not settle

| # | Question | How to settle it |
|---|---|---|
| R1 | Does Google send `Retry-After` on this endpoint's 429? | One instrumented capture. Converts the whole of Q3 from folklore to fact. |
| R2 | Does the block escalate with repeat offences, or is "never clears" purely the retry loop keeping it alive? | Controlled test: trigger a 429, then stop **completely** for 15 min and send one request. Repeat after a second offence. |
| R3 | Is the affected line behind CGNAT? | `curl ifconfig.me` vs the router's WAN address, or the Livebox CGN setting [S16]. Decides whether "the neighbour" is literal. |
| R4 | Does the LIVE loop's 2-groups-per-tick path materially raise the block rate vs 1 group? | Request counter in the instrumented build, correlated with 429s. |
| R5 | Is the reported "batch RPC is throttled less" [S24] true today? | Only testable by the reproduction agent; low priority — it is an evasion path, not an architecture. |

---

# Sources

Accessed 2026-09-06 unless noted. Dates are the source's own where the page states one.

| # | Title | URL | Date |
|---|---|---|---|
| S1 | "Requests to translate.googleapis.com/translate_a/single fail with status 429" — `artemave/translate_onhover` issue #74. **Quotes Google's block page verbatim.** | https://github.com/artemave/translate_onhover/issues/74 | 2021-03-19 |
| S2 | "Found a Google Translate endpoint that doesn't require an API key" — `ssut/py-googletrans` issue #268 (`clients5.google.com/translate_a/t?client=dict-chrome-ex`) | https://github.com/ssut/py-googletrans/issues/268 | 2021-01-03 |
| S3 | "translate.googleapis.com/translate_a" — Google Developer Forums; moderator answer discouraging use | https://discuss.google.dev/t/translate-googleapis-com-translate-a/126639 | asked 2023-08-03, answered 2023-08-11 |
| S4 | Google APIs Terms of Service (§2(c), §2(d), §3(a), §4(a)) | https://developers.google.com/terms | last modified 2021-11-09 |
| S5 | `@vitalets/google-translate-api` README — `TooManyRequestsError` (429) per-IP, proxy-agent workaround | https://github.com/vitalets/google-translate-api | accessed 2026-09-06 |
| S6 | "Google does browser sniffing, 403 on no User-Agent" — `soimort/translate-shell` issue #44 | https://github.com/soimort/translate-shell/issues/44 | 2015-03-31 |
| S7 | "change client to gtx" — `matheuss/google-translate-api` PR #87 (closes issue #79 "Response code 403") | https://github.com/matheuss/google-translate-api/pull/87 | 2018-11-29 |
| S8 | `6ebeng/google-translate-api-extend` README — 429/503, `client="gtx"` "works even with outdated token", 5000-char limit, proxy examples | https://github.com/6ebeng/google-translate-api-extend | accessed 2026-09-06 |
| S9 | "Free Google Translator API II" — maXbox6; `client=gtx` working without `tk`, JSON response headers | https://maxbox6.wordpress.com/2026/02/11/free-google-translator-api-ii/ | 2026-02-11 |
| S10 | Immersive Translate FAQ — 429 = "too many requests in a short time"; "your current network node has been rate-limited by Google. Switching nodes is recommended"; Google's free API called deprecated | https://immersivetranslate.com/docs/faq/ | accessed 2026-09-06 |
| S11 | "[Bug]: 429 Error" — `immersive-translate` issue #3913 | https://github.com/immersive-translate/immersive-translate/issues/3913 | 2026-05-24 |
| S12 | "Current Guidance Around Limits?" — `nidhaloff/deep-translator` issue #228 (conflicting limit reports; "5 req/s, 200k/day" claim; rate-limited under 100 calls) | https://github.com/nidhaloff/deep-translator/issues/228 | 2023-08-15 |
| S13 | "Character limit or request limit while using Google Translate" — `deep-translator` issue #263 | https://github.com/nidhaloff/deep-translator/issues/263 | 2024-06-21 |
| S14 | "Interpreting Google Translate HTTP Error Codes" — CodeRevolution KB; "the ban can last from a few minutes to more than 12-24 hours" | https://coderevolution.ro/knowledge-base/faq/interpreting-google-translate-http-error-codes/ | undated |
| S15 | "Unusual Traffic from Your Computer Network: Why Google Blocks You" — CGNAT "bad neighbour", captcha cookie is per-browser | https://www.iphalo.com/blog/unusual-traffic-google-block-fix/ | 2026-01-08 |
| S16 | "Orange partage à son tour par défaut les IPv4…" — MacGeneration; Orange enables CGNAT by default, Bouygues/SFR/Free for years, blocks when a subscriber is banned | https://www.macg.co/ailleurs/2025/01/orange-partage-son-tour-par-defaut-les-ipv4-pour-les-abonnes-adsl-et-fibre-148513 | 2025-01-22 |
| S17 | "Carrier-grade NAT" — Wikipedia | https://en.wikipedia.org/wiki/Carrier-grade_NAT | accessed 2026-09-06 |
| S18 | "JA3/JA4 TLS Fingerprinting: Guide to Detection and Evasion" — Scrapfly (vendor) | https://scrapfly.io/blog/posts/ja3-ja4-tls-fingerprinting-guide-to-detection-and-evasion | accessed 2026-09-06 |
| S19 | "TLS fingerprinting (JA3/JA4): why you're blocked even with perfect headers" — DataJi (vendor) | https://www.dataji.io/blog/tls-fingerprinting-ja3-explained/ | accessed 2026-09-06 |
| S20 | "The scary state of IPv6 rate-limiting" — adam-p; /64, /56, /48 bucketing | https://adam-p.ca/blog/2022/02/ipv6-rate-limiting/ | 2022-02 |
| S21 | "Quotas and limits" — Cloud Translation official docs | https://cloud.google.com/translate/quotas | accessed 2026-09-06 |
| S22 | Cloud Translation pricing — 500 000 characters/month free (non-expiring), $20/M thereafter | https://cloud.google.com/translate/pricing | accessed 2026-09-06 |
| S23 | `google_free_translate.py` (Glossarion, HuggingFace Space) — endpoint/client cycling `gtx, webapp, t, dict-chrome-ex, android`; `self.rate_limit = 0.5  # 500ms between requests to avoid rate limiting`; UA rotation; 403 = "Forbidden (IP blocked or bot detected)", 429 = "Rate Limited" | https://huggingface.co/spaces/Shirochi/Glossarion/blob/main/google_free_translate.py | accessed 2026-09-06 |
| S24 | `google-translate-api-x` docs — batch (`batchexecute`) endpoint reported as less rate-limited than the single endpoint | https://github.com/AidanWelch/google-translate-api | accessed 2026-09-06 |
| S25 | "Google API limit ?" — `soimort/translate-shell` issue #370 (limit after 6 files, VPN workaround) | https://github.com/soimort/translate-shell/issues/370 | 2020-09-12 |
| S26 | "Translation not working due to 429 status code (rate limiting)" — `funkyremi/vscode-google-translate` issue #12 | https://github.com/funkyremi/vscode-google-translate/issues/12 | 2019-08-29 |
| S27 | "Too Many Requests" — `lushan88a/google_trans_new` issue #24 | https://github.com/lushan88a/google_trans_new/issues/24 | 2021-01-06 |
| S28 | Project Bergamot / Firefox Translations — local CPU translation, `ru` model, 1000–9000 words/s on 2012–2019 hardware | https://browser.mt/ | accessed 2026-09-06 |
| S29 | LibreTranslate (self-hosted, Russian supported, configurable limits) | https://pypi.org/project/libretranslate/ | accessed 2026-09-06 |
| S30 | "429 Too Many Requests" and "Retry-After" — MDN | https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Status/429 | accessed 2026-09-06 |
| S31 | "My IP has been blocked, reason being 'i translated too much'" — Google Translate Community thread (title only; body not retrievable) | https://support.google.com/translate/thread/331749749 | undated |
| S32 | Googletrans documentation — _"this API does not guarantee that the library would work properly at all times"_; _"it's probably because Google has banned your client IP address"_; recommends the official API | https://py-googletrans.readthedocs.io/en/latest/ | v3.0.0 docs, accessed 2026-09-06 |
| S33 | "Search engine scraping" — Wikipedia; sustainable rates of 3–5 to 100+ requests/hour per IP against Google properties | https://en.wikipedia.org/wiki/Search_engine_scraping | accessed 2026-09-06 |
| S34 | Cloud Translation API REST reference — `translate.googleapis.com` documented as the v3 service endpoint (the host is official; `/translate_a/*` is not) | https://cloud.google.com/translate/docs/reference/rest | accessed 2026-09-06 |
| S-tos | "Google Terms of Service: Why Automated Scraping is Prohibited" — quotes the general ToS automated-means clause | https://wpseoai.com/blog/is-web-scraping-against-google/ | accessed 2026-09-06 |
| S-hour | "How to use google translate api in my translator app for free" — MIT App Inventor community; repeats the "blocks your IP for one hour" claim | https://community.appinventor.mit.edu/t/how-to-use-google-translate-api-in-my-translator-app-for-free/19678 | undated |
| S-captcha | "How to Bypass CAPTCHA Scraping Google" — Crawlbase (vendor); captcha pass is a per-browser cookie, does not transfer to an API client | https://crawlbase.com/blog/how-to-bypass-captcha-while-scraping-google/ | accessed 2026-09-06 |
| S-net | "Fix Chrome Translate Not Working" — network-isolation advice (hotspot, router restart, different network) | https://watranslator.com/how-to-fix-chrome-translate-not-working/ | 2025 |
| S-consent | "[GDPR] Google forcing users to accept their cookies through redirects" — `brave/brave-browser` issue #15082; `consent.google.com` redirects apply to HTML pages | https://github.com/brave/brave-browser/issues/15082 | 2021 |
| S-toms | "Our systems have detected unusual traffic from your computer" — Tom's Guide forums; outlier ~2-week report on a consumer connection | https://forums.tomsguide.com/threads/our-systems-have-detected-unusual-traffic-from-your-computer.431427/ | undated |

**36 sources.** 8 graded CONFIRMED at the point of use (Google terms, Google-authored strings, official Cloud docs,
library documentation quoting itself), 23 REPORTED, 5 used only as corroboration for a mechanism already established
elsewhere.

---

_End of `02-traduction/mecanismes-de-blocage-google.md`. Feeds: `analyse-implementation-actuelle.md` (which mitigations
are already partly present), `benchmark-fournisseurs.md` (the official-API and local-model options costed out), and
`architecture-cible.md` (the constraints in "Implications" above)._
