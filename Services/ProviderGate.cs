using System.Text.Json;

namespace PWRUHelper.Services;

/// <summary>Why a request is being made, so the rate ceiling can keep one token for the user.
/// <c>Interactive</c> is a person waiting on a keystroke — the Translator tab, the overlay quick
/// reply and (ruling OQ-a) read-once, which is a user click. <c>Background</c> is the LIVE loop.
/// The enum is defined here because §16 puts it in this file; the two <c>if</c>s that read it are
/// the token bucket's, and the bucket is <b>E2.S3</b>.</summary>
internal enum RequestPriority
{
    Interactive,
    Background,
}

/// <summary>Where the breaker stands, as the UI polls it (ruling R-2/OQ-c: poll at 1 Hz, no event).
/// <c>Open</c> covers the whole open window including the moment it has elapsed — the gate only
/// becomes <c>HalfOpen</c> when a caller actually takes the probe, which is what makes "exactly one
/// probe" true.</summary>
internal enum GateState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>The four answers <see cref="ProviderGate.TryEnter"/> can give. <c>Wait</c> is the rate
/// ceiling's (§5.4) and is never returned in this story — the bucket lands in E2.S3; it is declared
/// here because the glossary §16 puts the whole decision vocabulary in this file, and because the
/// callers E2.S5/E3 will be written against all four at once.</summary>
internal enum GateOutcome
{
    Allow,
    Wait,
    Open,
    Probe,
}

/// <summary>
/// What the gate answered, as one value type — <c>TryEnter</c> is consulted before every request,
/// so the decision must not allocate (footprint is a product requirement).
/// <list type="bullet">
/// <item><c>Allow</c> — go.</item>
/// <item><c>Wait</c> — the rate ceiling is empty; come back in <see cref="Delay"/> (E2.S3).</item>
/// <item><c>Open</c> — the breaker is open; nothing is sent before <see cref="RetryAt"/>.</item>
/// <item><c>Probe</c> — you are the one half-open probe. Report success or failure.</item>
/// </list>
/// </summary>
internal readonly record struct GateDecision(GateOutcome Outcome, TimeSpan Delay, DateTimeOffset RetryAt)
{
    public static readonly GateDecision Allow = new(GateOutcome.Allow, TimeSpan.Zero, default);
    public static readonly GateDecision Probe = new(GateOutcome.Probe, TimeSpan.Zero, default);

    public static GateDecision Wait(TimeSpan delay) => new(GateOutcome.Wait, delay, default);
    public static GateDecision Open(DateTimeOffset retryAt) => new(GateOutcome.Open, TimeSpan.Zero, retryAt);
}

/// <summary>
/// The gate's state as a value, for the UI to poll and for E2.S6 to log. Immutable, allocation-cheap
/// and side-effect free by contract: taking a snapshot must never advance a clock, take the probe or
/// spend a rate-ceiling token (ruling R-2 — <c>Services/</c> stays passive; the status chip polls
/// this at 1 Hz from the countdown timer the UX already requires, in E7.S2/E7.S3).
/// </summary>
internal sealed record GateSnapshot(
    GateState State,
    DateTimeOffset? BlockedUntil,
    int Strikes,
    TranslationErrorKind? LastKind);

/// <summary>
/// The six breaker numbers a gate actually runs on, so a field experiment (IS-12) can drive short
/// windows without waiting 30 real minutes. <see cref="Default"/> is
/// <see cref="TranslationPolicy"/>'s graded table; <see cref="Parse"/> reads the diagnostic override
/// hatch — <c>AppSettings.ProviderGateOverrides</c>, which arrives in <b>E6.S3</b> and is used as a
/// field hatch by E2.S7. This story lands the parser only: it takes a string and depends on no
/// setting.
/// </summary>
internal sealed record GatePolicy(
    int OpenBaseSeconds,
    int OpenCapMinutes,
    int CleanResetMinutes,
    int QuotaOpenMinutes,
    int SoftCooldownSecs,
    int BadResponseStrikesToOpen)
{
    public static readonly GatePolicy Default = new(
        TranslationPolicy.OpenBaseSeconds,
        TranslationPolicy.OpenCapMinutes,
        TranslationPolicy.CleanResetMinutes,
        TranslationPolicy.QuotaOpenMinutes,
        TranslationPolicy.SoftCooldownSecs,
        TranslationPolicy.BadResponseStrikesToOpen);

    /// <summary>
    /// Turns the overrides JSON into effective numbers. Pure, and it <b>never throws</b> (the L1 half
    /// of TP-SET-11): null, empty, truncated, the wrong shape, a negative or a non-numeric value all
    /// mean "nothing said" and leave <see cref="Default"/>'s value in place, field by field. A
    /// diagnostic hatch that could crash the app on a typo would be worse than no hatch.
    /// </summary>
    internal static GatePolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Default;

            return new GatePolicy(
                Read(doc, nameof(OpenBaseSeconds), Default.OpenBaseSeconds),
                Read(doc, nameof(OpenCapMinutes), Default.OpenCapMinutes),
                Read(doc, nameof(CleanResetMinutes), Default.CleanResetMinutes),
                Read(doc, nameof(QuotaOpenMinutes), Default.QuotaOpenMinutes),
                Read(doc, nameof(SoftCooldownSecs), Default.SoftCooldownSecs),
                Read(doc, nameof(BadResponseStrikesToOpen), Default.BadResponseStrikesToOpen));
        }
        catch (Exception)
        {
            // Deliberately every exception, not just JsonException: JsonDocument.Parse(string)
            // transcodes to UTF-8 first and throws ArgumentException — not JsonException — on
            // invalid UTF-16 (a lone surrogate that survived a hand-edited settings file). The
            // contract here is "never throws" (IS-12, the L1 half of TP-SET-11), and a diagnostic
            // hatch that crashes the app on a typo would be worse than no hatch; there is no
            // failure of a pure string parse that should be louder than "nothing said".
            return Default;   // unparseable ⇒ the graded defaults, silently
        }
    }

    /// <summary>One field, matched case-insensitively (the hatch is typed by a human in a settings
    /// file, not generated). A value that is not a positive int is ignored rather than corrected:
    /// zero or negative windows are exactly the "paused forever / never paused" bugs R-01 is about.</summary>
    private static int Read(JsonDocument doc, string name, int fallback)
    {
        foreach (var p in doc.RootElement.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.Number
                       && p.Value.TryGetInt32(out var v) && v > 0 ? v : fallback;
        return fallback;
    }
}

/// <summary>
/// One provider's circuit breaker: it stops the app asking after the provider has said stop, so the
/// block Google put on a shared ISP address actually expires (§5.2, §5.3). Pure arithmetic over an
/// injected clock — no I/O, no network, no UI, no logging on the hot path (I2, I10; E2.S6 logs
/// <i>transitions</i>, never skipped requests, or a 30-minute open window fills the 1 MB log).
///
/// Thread-safe behind one <c>lock</c>, like <c>CachingTranslator.cs:20</c>: both chains and the LIVE
/// loop consult the same instance concurrently (I9). No static state lives here — instances are
/// handed out by <c>ProviderGates</c> (E2.S2) and persisted by <c>ProviderStateStore</c> (E2.S4);
/// static state in this class would leak across xUnit's parallel collections (R4/R-08).
///
/// Seams left deliberately open: E2.S3 inserts the token bucket where <see cref="TryEnter"/> returns
/// <c>Allow</c> today (and charges the half-open probe a token — ruling OQ-b: it is a real request,
/// one rule, no special case); E2.S4 adds the restore path that rebuilds a gate from
/// <c>provider-state.json</c>. Nothing in this story is wired to a caller: providers reach a gate
/// only through <c>HttpProviderCore</c> (E2.S5).
/// </summary>
internal sealed class ProviderGate
{
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly GatePolicy _policy;

    private int _strikes;                     // consecutive opening failures; drives the doubling
    private int _badResponses;                // consecutive BadResponse; 3 of them open the gate
    private DateTimeOffset? _blockedUntil;    // null = closed. MaxValue = until the key changes
    private TranslationErrorKind? _lastKind;
    private DateTimeOffset? _lastAt;
    private DateTimeOffset? _cleanSince;      // first success since the last failure; null = not clean
    private bool _probeOutstanding;
    private DateTimeOffset _probeStartedAt;

    /// <summary>How long a half-open probe may be outstanding before the gate re-arms it. It is the
    /// request timeout on purpose rather than a new ungraded number: a probe is one request, and a
    /// request cannot outlive that timeout. It also closes the only way this class could pause the
    /// app forever (R-01): a caller that takes the probe and never reports — a cancel mid-probe, or
    /// a crash — would otherwise leave the gate half-open, admitting nobody, for good.</summary>
    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds);

    /// <param name="clock">The one source of "now" (IS-6). <c>ProviderGates</c> (E2.S2) passes its
    /// own clock into every gate it builds — they must share one, or a persisted
    /// <c>blockedUntil</c> ends up compared against a different now. Defaults to the wall clock.</param>
    /// <param name="policy">The six §5.6 numbers; defaults to <see cref="TranslationPolicy"/>'s.
    /// A test (and later a field experiment) passes <see cref="GatePolicy.Parse"/>'s result here.</param>
    internal ProviderGate(Func<DateTimeOffset>? clock = null, GatePolicy? policy = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _policy = policy ?? GatePolicy.Default;
    }

    /// <summary>
    /// Asked once before every request. §5.2: open until <c>blockedUntil</c> passes, then <b>exactly
    /// one</b> caller is let through as the probe and everyone else keeps getting <c>Open</c>.
    /// </summary>
    /// <param name="priority">Read by the rate ceiling only (§5.4 concern #1: one of the two tokens
    /// is reserved for <c>Interactive</c>, and an interactive caller is preferred for the probe).
    /// The bucket is E2.S3, so today the breaker answers the same way for both — the parameter is
    /// here so E2.S5 and E3 state the priority at the call site from the start.</param>
    internal GateDecision TryEnter(RequestPriority priority)
    {
        lock (_lock)
        {
            var now = _clock();

            if (_probeOutstanding)
            {
                // Somebody else holds the probe. Hand back when it will have resolved — measured
                // from when it was taken, not from now, so a caller arriving late is not told to
                // wait a full timeout again. (AC 2 says now + probeTimeout; for the concurrent
                // callers it describes — same instant — the two are the same value.)
                if (now - _probeStartedAt < ProbeTimeout) return GateDecision.Open(_probeStartedAt + ProbeTimeout);

                _probeStartedAt = now;          // abandoned probe: re-arm rather than pause forever
                return GateDecision.Probe;
            }

            if (_blockedUntil is { } until)
            {
                if (now < until) return GateDecision.Open(until);

                _probeOutstanding = true;       // half-open: this caller, and only this caller
                _probeStartedAt = now;
                return GateDecision.Probe;
            }

            return GateDecision.Allow;          // E2.S3: the token bucket answers here, or Wait(t)
        }
    }

    /// <summary>The request worked. A probe that succeeds closes the gate outright (§5.2); an
    /// ordinary success starts — or ages — the clean run that resets the strikes.</summary>
    internal void ReportSuccess()
    {
        lock (_lock)
        {
            var now = _clock();
            _lastAt = now;
            _badResponses = 0;                  // "a success in between resets the soft count" (§5.3)

            if (_probeOutstanding)
            {
                _probeOutstanding = false;
                _strikes = 0;
                _blockedUntil = null;
                _cleanSince = now;
                return;
            }

            // A clean run cannot start inside a block. The success is real, but it belongs to a
            // request that was already in flight when the gate closed behind it (the two chains
            // share one gate, I9), and it is not evidence that the provider is well now — only a
            // probe is. Without the guard, one such late 200 sets `cleanSince` at the start of a
            // 30-minute window; CleanResetMinutes later the probe's own failure runs DecayStrikes
            // first and re-opens at strike 1 for 60 s, erasing the whole escalation ladder.
            if (_blockedUntil is null) _cleanSince ??= now;
            DecayStrikes(now);
        }
    }

    /// <summary>
    /// The request failed, classified by <c>ProviderErrorMapper</c> (E1.S3). Which rows touch the
    /// gate, and how, is §5.3's table and nothing else.
    /// </summary>
    /// <param name="retryAt">The server's own "come back at", when it sent a <c>Retry-After</c>
    /// (§5.5). It is <b>a floor, never a shortcut</b> (ruling E2-f): it may lengthen the computed
    /// window, clamped to <c>[1 s, max(OpenCapMinutes, the row's own window)]</c>, and can never
    /// shorten it — see <see cref="Honour"/>. The clamp is the one E1.S3 deferred to "whoever first
    /// reads <c>RetryAt</c>", which is this method. Absolute rather than a delta on purpose: that
    /// is what <c>TranslationException.RetryAt</c> carries, and resolving it against this gate's own
    /// clock is the only way the two cannot disagree. Parsing it off the response is E2.S5's.</param>
    internal void ReportFailure(TranslationErrorKind kind, DateTimeOffset? retryAt = null)
    {
        var reaction = Reaction(kind);
        if (reaction == GateReaction.Ignore) return;   // not one byte of state changes — see Reaction

        lock (_lock)
        {
            var now = _clock();
            // A probe failure is an ordinary failure that happens to end half-open: the machine
            // needs no branch on it, only the latch released so the window it is about to set is
            // the state everyone sees (§5.2, HalfOpen → Open).
            var wasProbe = _probeOutstanding;
            _probeOutstanding = false;
            DecayStrikes(now);                  // a failure after a long clean run starts from strike 1

            switch (reaction)
            {
                case GateReaction.Escalate:     // RateLimited, Blocked — and a failed probe re-opens here
                    _strikes++;
                    var window = now + Honour(retryAt, EscalatedWindow(_strikes), now);
                    // "Extend, never shorten", with no exception for an explicit hint any more:
                    // ruling E2-f made Retry-After a floor rather than the server's last word, so
                    // there is nothing left that may lift a longer standing block. Doubling always
                    // extends this row's own window, so the guard only bites where a LONGER block
                    // stands and a stale in-flight 429 would otherwise end it — an AuthFailed
                    // MaxValue (AC 4: only ClearAuthBlock lifts it) or a 60-minute QuotaExhausted
                    // window (§15 R9). The strike is still counted either way.
                    BlockUntil(window);
                    break;

                case GateReaction.Quota:        // open for an hour, no strike escalation (§15 R9)
                    BlockUntil(now + Honour(retryAt, TimeSpan.FromMinutes(_policy.QuotaOpenMinutes), now));
                    break;

                case GateReaction.Auth:         // dead credentials: no window can fix them
                    _blockedUntil = DateTimeOffset.MaxValue;
                    break;

                case GateReaction.SoftCooldown: // Unavailable, Timeout, Network, Unknown — no strike
                    // Small, but not optional: without it a DNS blip means the next LIVE tick
                    // re-hits the same dead provider 700 ms later.
                    BlockUntil(now + Honour(retryAt, TimeSpan.FromSeconds(_policy.SoftCooldownSecs), now));
                    break;

                case GateReaction.SoftStrike:   // BadResponse: 3 consecutive open the gate
                    _badResponses++;
                    if (_badResponses >= _policy.BadResponseStrikesToOpen)
                    {
                        _badResponses = 0;
                        BlockUntil(now + Honour(retryAt, TimeSpan.FromSeconds(_policy.OpenBaseSeconds), now));
                    }
                    break;
            }

            // §5.2 has exactly one arrow out of HalfOpen on a failure, and it lands on Open. The
            // SoftStrike row can leave the window untouched (one BadResponse is not three), which
            // would leave `blockedUntil` where it was — already elapsed, because that is how the
            // probe was granted. The gate would then read Open to the UI while handing a fresh
            // probe to every caller in turn, with no cooldown between them: the breaker off in the
            // one state it was entered to manage. A failed probe therefore always leaves a future
            // block; the shortest one the policy has is the right floor.
            if (wasProbe && _blockedUntil is { } b && b <= now)
                BlockUntil(now + TimeSpan.FromSeconds(_policy.SoftCooldownSecs));

            _lastKind = kind;
            _lastAt = now;
            _cleanSince = null;                 // the clean run is over; the next success restarts it
        }
    }

    /// <summary>
    /// The user re-saved this provider's key, so the two blocks a key can cause — <c>AuthFailed</c>
    /// (§5.3: open until the key changes) and <c>QuotaExhausted</c> (cleared when the key is
    /// re-saved) — are lifted. <c>ProviderGates.ClearAuthBlock(id)</c> (E2.S2) forwards here from the
    /// key-save handler. It clears the gate whatever opened it: it is a per-provider object and the
    /// user asking for that provider again is an explicit "try now", which is also the only escape
    /// hatch from a wrong <c>MaxValue</c>.
    /// </summary>
    internal void ClearAuthBlock()
    {
        lock (_lock)
        {
            _blockedUntil = null;
            _strikes = 0;
            _badResponses = 0;
            _probeOutstanding = false;
            _cleanSince = _clock();
        }
    }

    /// <summary>The state, for a 1 Hz poll (R-2). Read-only: no clock is advanced, no probe taken,
    /// no token spent.</summary>
    internal GateSnapshot Snapshot()
    {
        lock (_lock)
        {
            // HalfOpen only while a probe could still be running — the same test TryEnter applies
            // before it re-arms one. Reading `_probeOutstanding` alone would leave the 1 Hz status
            // chip on "checking…" for the life of the process after a probe was abandoned (LIVE
            // switched off mid-probe, or a crash), because the re-arm needs a TryEnter that a
            // paused app never makes. Reading the clock is not a side effect: no probe is taken,
            // no state is written.
            var state = _probeOutstanding && _clock() - _probeStartedAt < ProbeTimeout ? GateState.HalfOpen
                      : _blockedUntil is null ? GateState.Closed
                      : GateState.Open;         // still Open once the window elapses — until a caller
                                                // takes the probe, which is what makes it exactly one
            return new GateSnapshot(state, _blockedUntil, _strikes, _lastKind);
        }
    }

    // ---- the arithmetic ------------------------------------------------------------------------

    /// <summary><c>OpenBaseSeconds × 2^(strikes−1)</c>, clamped at <c>OpenCapMinutes</c>:
    /// 60 s → 2 → 4 → 8 → 16 → 30 min and 30 min thereafter. The shift is bounded before it is
    /// taken, because a long-lived session can reach a strike count that would overflow it.</summary>
    private TimeSpan EscalatedWindow(int strikes)
    {
        var shift = Math.Min(Math.Max(strikes - 1, 0), 30);
        var seconds = (long)_policy.OpenBaseSeconds << shift;
        var cap = (long)_policy.OpenCapMinutes * 60;
        return TimeSpan.FromSeconds(Math.Min(seconds, cap));
    }

    /// <summary>
    /// Opens until <paramref name="candidate"/>, but <b>never shortens</b> a block that already
    /// stands. Used by the three rows that do not escalate. The case is not hypothetical: two
    /// requests are in flight (the LIVE batch and the Translator tab share the gate, I9), the first
    /// comes back 429 and opens the gate for 30 minutes, the second times out a second later — and
    /// a plain assignment would replace those 30 minutes with a 5-second cooldown and hand a
    /// blocked provider a request every 5 seconds. A soft failure may extend a block, never lift
    /// one. Every row goes through here since ruling E2-f: the last exemption — an escalating row
    /// carrying an explicit <c>Retry-After</c> — went away when the hint became a floor rather than
    /// the server's last word.
    /// </summary>
    private void BlockUntil(DateTimeOffset candidate) =>
        _blockedUntil = _blockedUntil is { } standing && standing > candidate ? standing : candidate;

    /// <summary>
    /// A <c>Retry-After</c> is <b>a floor, never a shortcut</b> (ruling E2-f, which supersedes
    /// §5.5's "it overrides the computed window"):
    /// <c>max(computed, clamp(hint, 1 s, max(OpenCapMinutes, the kind's own window)))</c>.
    ///
    /// <para>The three ways the old reading switched the breaker off, all found by E2.S1's review.
    /// <b>A tiny hint</b>: <c>Retry-After: 1</c> bought a one-second pause while the strike ladder
    /// climbed with no effect at all — the server deciding how long the app pauses. <b>A skewed
    /// one</b>: the HTTP-date form is resolved against the <i>client</i> clock
    /// (<c>ProviderErrorMapper.cs:307</c> hands over <c>header.Date</c>), so a PC running five
    /// minutes fast collapsed every date hint to the same floor. <b>A ceiling below the row's own
    /// window</b>: clamping to <c>OpenCapMinutes</c> (30) made a hint <i>shorten</i> a
    /// <c>QuotaExhausted</c> block (60 min) — §15 R9 says one request an hour, not one every half
    /// hour. A hint may now only lengthen, and only within its own row's ceiling.</para>
    /// </summary>
    private TimeSpan Honour(DateTimeOffset? retryAt, TimeSpan computed, DateTimeOffset now)
    {
        if (retryAt is not { } at) return computed;

        var cap = TimeSpan.FromMinutes(_policy.OpenCapMinutes);
        var ceiling = computed > cap ? computed : cap;   // a row whose own window exceeds the cap keeps it
        var delta = at - now;

        // The floor of the clamp is stated because the ruling states it; with "a floor, never a
        // shortcut" it can no longer be observed, since the shortest window any row computes is
        // SoftCooldownSecs (5 s). It is kept so the arithmetic reads as the ruling wrote it.
        var hint = delta < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
                 : delta > ceiling ? ceiling
                 : delta;

        return hint > computed ? hint : computed;
    }

    /// <summary>
    /// <c>CleanResetMinutes</c> of clean operation resets the strikes, so the next failure opens for
    /// 60 s and not for the escalated window (AC 8). Evaluated lazily, in the two places that can act
    /// on it, so no timer is needed.
    ///
    /// Worth saying out loud, because it looks like dead code and is not: §5.2 also has a successful
    /// probe clear the strikes, and every route back to <c>Closed</c> runs through one — a strike
    /// always opens the gate, and only a probe (or <see cref="ClearAuthBlock"/>) closes it. So this
    /// is the safety net rather than the usual path today. It is kept because §5.2 states both
    /// transitions, because it is what ages <c>_badResponses</c> when no success has landed, and
    /// because E2.S4 restores a gate from disk with a <c>cleanSince</c> the process never observed.
    /// </summary>
    private void DecayStrikes(DateTimeOffset now)
    {
        if (_cleanSince is { } since && now - since >= TimeSpan.FromMinutes(_policy.CleanResetMinutes))
        {
            _strikes = 0;
            _badResponses = 0;
            _cleanSince = now;                  // the run continues; it does not have to start over
        }
    }

    // ---- §5.3, as one total function -----------------------------------------------------------

    private enum GateReaction { Ignore, Escalate, Quota, Auth, SoftCooldown, SoftStrike }

    /// <summary>
    /// Every row of §5.3's table, and nothing but. Written as a mapping rather than as
    /// <c>if</c>s at the call site so "which kind opens the circuit" is one readable list.
    ///
    /// The two rows that reach <c>Ignore</c>: <c>AllProvidersPaused</c> is the chain's own outcome
    /// (E3.S3) and is never reported to a gate — guarded here rather than trusted; and the row this
    /// file may not name, a genuine user cancel (I3), which never touches the gate. That row is the
    /// <c>default</c> arm on purpose: no production source may write the token
    /// (<c>TranslationErrorsTests.No_production_source_names_Kind_Cancelled</c>), so the table states
    /// it by construction — everything the gate reacts to is listed above, and what is not listed
    /// changes nothing. A kind added later lands here too, inert, until someone gives it a row.
    /// </summary>
    private static GateReaction Reaction(TranslationErrorKind kind) => kind switch
    {
        TranslationErrorKind.RateLimited => GateReaction.Escalate,
        TranslationErrorKind.Blocked => GateReaction.Escalate,
        TranslationErrorKind.QuotaExhausted => GateReaction.Quota,
        TranslationErrorKind.AuthFailed => GateReaction.Auth,
        TranslationErrorKind.Unavailable => GateReaction.SoftCooldown,
        TranslationErrorKind.Timeout => GateReaction.SoftCooldown,
        TranslationErrorKind.Network => GateReaction.SoftCooldown,
        TranslationErrorKind.Unknown => GateReaction.SoftCooldown,
        TranslationErrorKind.BadResponse => GateReaction.SoftStrike,
        TranslationErrorKind.AllProvidersPaused => GateReaction.Ignore,
        _ => GateReaction.Ignore,
    };
}
