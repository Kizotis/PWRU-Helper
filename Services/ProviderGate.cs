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
/// ceiling's (§5.4) and is returned since E2.S3 landed the token bucket; the whole decision
/// vocabulary lives in this file because the glossary §16 puts it here.</summary>
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
/// <item><c>Wait</c> — the rate ceiling is empty; come back in <see cref="Delay"/> (§5.4).</item>
/// <item><c>Open</c> — the breaker is open; nothing is sent before <see cref="RetryAt"/>.</item>
/// <item><c>Probe</c> — you are the one half-open probe. Report success or failure, quoting
///       <see cref="ProbeToken"/>.</item>
/// </list>
/// </summary>
/// <param name="ProbeToken">Ruling <b>E2-h</b>: the identity of the probe this decision granted, so
/// a report can be matched to it. Non-zero on <c>Probe</c> and zero on every other outcome. A
/// report that quotes no token, or a token that is not the outstanding probe's, is a stale
/// in-flight result: it may still be classified against §5.3, but it can neither close the gate nor
/// release the half-open latch. Without it a 200 from a request dispatched before the block closed
/// the gate and reset the whole strike ladder, and a stale failure stole the probe and left a
/// healthy provider blocked for a full window.</param>
internal readonly record struct GateDecision(
    GateOutcome Outcome, TimeSpan Delay, DateTimeOffset RetryAt, long ProbeToken = 0)
{
    public static readonly GateDecision Allow = new(GateOutcome.Allow, TimeSpan.Zero, default);

    public static GateDecision Probe(long token) => new(GateOutcome.Probe, TimeSpan.Zero, default, token);
    public static GateDecision Wait(TimeSpan delay) => new(GateOutcome.Wait, delay, default);
    public static GateDecision Open(DateTimeOffset retryAt) => new(GateOutcome.Open, TimeSpan.Zero, retryAt);

    /// <summary>AC 4 of §5.4, as the one comparison its two callers share: <c>ChainTranslator</c>
    /// (E3.S3) awaits a <c>Wait</c> only while it is this short and otherwise treats the tier as
    /// unavailable, and the LIVE loop (E5.S1) reads it the same way. It lives here rather than in
    /// <c>TranslationPolicy</c> — which is a table with no behaviour and a test that keeps it
    /// that way — and it is the caller's rule: <see cref="ProviderGate.TryEnter"/> never waits.</summary>
    internal static bool WorthWaiting(TimeSpan delay) =>
        delay <= TimeSpan.FromMilliseconds(TranslationPolicy.MaxSpacingWaitMs);
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
/// It also holds the §5.4 rate ceiling (E2.S3): a lazily-refilled token bucket under the same lock,
/// with one token of its capacity reserved for <c>Interactive</c> callers so the LIVE loop cannot
/// spend the whole budget — and the probe — on the feature the user is not looking at. The probe
/// pays a token like anybody else (ruling OQ-b), and carries a token of its own so a stale
/// in-flight report cannot resolve it (ruling E2-h).
///
/// Seam left deliberately open: E2.S4 adds the restore path that rebuilds a gate from
/// <c>provider-state.json</c> — the breaker only; the bucket starts full in every process (I9,
/// §5.7). Nothing here is wired to a caller: providers reach a gate only through
/// <c>HttpProviderCore</c> (E2.S5).
/// </summary>
internal sealed class ProviderGate
{
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly GatePolicy _policy;

    private int _strikes;                     // consecutive opening failures; drives the doubling
    private int _badResponses;                // consecutive BadResponse; 3 of them open the gate
    // Two independent block timelines, because ruling E2-i gives them two different exits: the
    // ACCOUNT-scoped one is the user's key (AuthFailed, QuotaExhausted — `ClearAuthBlock` lifts it),
    // the IP-scoped one is the provider's own counter on the address this PC dials from
    // (RateLimited, Blocked, and the soft rows — only time lifts it). One field could not express
    // that: a 60-minute quota landing on a standing 30-minute 429 would swallow it, and a key save
    // would then "lift a quota block" that is really the 429 window underneath — resuming exactly
    // the hammering this epic exists to stop. What the gate enforces is <see cref="BlockedUntil"/>,
    // whichever of the two runs longer.
    private DateTimeOffset? _keyBlockedUntil;  // null = no account-scoped block. MaxValue = AuthFailed
    private DateTimeOffset? _ipBlockedUntil;   // null = no IP-scoped block
    private TranslationErrorKind? _lastKind;
    private DateTimeOffset? _lastAt;
    private DateTimeOffset? _cleanSince;      // first success since the last failure; null = not clean
    private bool _probeOutstanding;
    private DateTimeOffset _probeStartedAt;
    private long _probeSeq;                   // never reset: a token is never handed out twice
    private long _probeToken;                 // the outstanding probe's identity; 0 = none (E2-h)

    // The §5.4 rate ceiling, refilled lazily on read rather than by a timer: a Timer per gate would
    // allocate a thread-pool item, be untestable on a fake clock (CI-3) and cost something while
    // nobody is asking. `_tokens` is a double because the refill is continuous — `(now - _tokensAt)
    // / MinSpacingMs` — and is clamped at BucketCapacity before every comparison so a gate idle for
    // an hour cannot bank 7 200 tokens. Compared with `>=`, never with `==`.
    // The bucket lives under the SAME lock as the breaker (§5.2: the ceiling is independent of the
    // breaker in RULE, not in lock) — two locks would let a caller be admitted by the breaker and
    // refused by the bucket with the probe latch already taken. It is deliberately NOT in
    // `GateSnapshot` (R-2/OQ-c) and NOT persisted (I9, §5.7): a bucket restored from a file written
    // 40 minutes ago is either full, so restoring it was pointless, or a fabricated debt.
    private double _tokens = TranslationPolicy.BucketCapacity;   // starts full in every process
    private DateTimeOffset? _tokensAt;        // null = never refilled; the first TryEnter seeds it

    /// <summary>How long a half-open probe may be outstanding before the gate re-arms it. It is the
    /// request timeout on purpose rather than a new ungraded number: a probe is one request, and a
    /// request cannot outlive that timeout. It also closes the only way this class could pause the
    /// app forever (R-01): a caller that takes the probe and never reports — a cancel mid-probe, or
    /// a crash — would otherwise leave the gate half-open, admitting nobody, for good.</summary>
    private static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds);

    /// <summary>How long a <c>Background</c> caller stands aside before taking a probe itself
    /// (§5.4, architect's concern #1). <c>Interactive</c> is never deferred.</summary>
    private static readonly TimeSpan ProbeDefer =
        TimeSpan.FromMilliseconds(TranslationPolicy.ProbeDeferMs);

    /// <summary>One token per <c>MinSpacingMs</c> — the refill period, as a period.</summary>
    private static readonly TimeSpan RefillPeriod =
        TimeSpan.FromMilliseconds(TranslationPolicy.MinSpacingMs);

    /// <summary>The block the gate actually enforces: the later of the two timelines, or
    /// <c>null</c> (closed) when neither stands. Read under <c>_lock</c> like the fields it reads.</summary>
    private DateTimeOffset? BlockedUntil =>
        _keyBlockedUntil is { } key
            ? (_ipBlockedUntil is { } ip && ip > key ? ip : key)
            : _ipBlockedUntil;

    /// <summary>§5.3's two account-scoped rows — the only ones a new key can lift (ruling E2-i).
    /// <c>AuthFailed</c> is "open until the key changes"; a <c>QuotaExhausted</c> is an allowance on
    /// the account the key names. Everything else is the provider's counter on this connection's
    /// address, or a dead endpoint, and no key the user types moves either.</summary>
    private static bool IsKeyScoped(TranslationErrorKind kind) =>
        kind is TranslationErrorKind.AuthFailed or TranslationErrorKind.QuotaExhausted;

    /// <summary>
    /// Ruling <b>E2-h</b>: is this report the outstanding probe's own? Only then may it close the
    /// gate or release the latch. Zero — an ordinary request, which is what every caller that was
    /// answered <c>Allow</c> passes — never matches, and neither does a token from a probe that has
    /// already been resolved or re-armed. Called under <c>_lock</c>.
    /// </summary>
    private bool IsOutstandingProbe(long probeToken) =>
        _probeOutstanding && probeToken != 0 && probeToken == _probeToken;

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
    /// Asked once before every request — <b>before</b>, never after (§5.4 AC 1), and it never
    /// blocks: no <c>Task</c>, no <c>CancellationToken</c>, no sleep. A gate that waits for you is
    /// the scheduler §5.4 explicitly refuses to build; the caller decides what to do with a
    /// <c>Wait</c> (see <see cref="GateDecision.WorthWaiting"/>).
    ///
    /// <para>Three verdicts in this order, and the order is the point:</para>
    /// <list type="number">
    /// <item><b>the breaker</b> (§5.2) — open until <c>blockedUntil</c> passes, then <b>exactly
    ///       one</b> caller is let through as the probe and everyone else keeps getting
    ///       <c>Open</c>;</item>
    /// <item><b>the probe preference</b> (§5.4) — a <c>Background</c> caller stands aside for
    ///       <c>ProbeDeferMs</c> so a user pressing Enter inside that second gets the probe;</item>
    /// <item><b>the rate ceiling</b> (§5.4) — the token bucket, which the probe pays too
    ///       (ruling OQ-b: it is a real request, one rule, no special case).</item>
    /// </list>
    ///
    /// <para>The token and the latch are then taken <b>together, or neither</b>. A probe-eligible
    /// caller that cannot afford a token gets <c>Wait</c> and the gate stays half-open-eligible —
    /// otherwise the one probe of a 30-minute window is spent by a caller that never sent a
    /// request, and the gate stays open for another full window with nothing outstanding (R-01,
    /// arrived at by the back door). <c>Wait</c> and <c>Open</c> spend nothing at all: no request
    /// is made on those paths, so no budget may be charged for one.</para>
    /// </summary>
    /// <param name="priority">§5.4's reserve, as one enum and two <c>if</c>s rather than a
    /// scheduler: <c>Background</c> (the LIVE loop) may draw the bucket down but may not take the
    /// last token, and it stands aside for the probe. <c>Interactive</c> is the person waiting on a
    /// keystroke — and, by ruling OQ-a, read-once. Who passes what is settled at the call sites
    /// (E2.S5 passes it through; E3.S7 and E5.S4 decide it).</param>
    internal GateDecision TryEnter(RequestPriority priority)
    {
        lock (_lock)
        {
            var now = _clock();

            // ---- 1. the breaker. `eligibleAt` set = this caller may take the probe, if it can pay
            DateTimeOffset? eligibleAt = null;

            if (_probeOutstanding)
            {
                // Somebody else holds the probe. Hand back when it will have resolved — measured
                // from when it was taken, not from now, so a caller arriving late is not told to
                // wait a full timeout again. (AC 2 says now + probeTimeout; for the concurrent
                // callers it describes — same instant — the two are the same value.)
                if (now - _probeStartedAt < ProbeTimeout) return GateDecision.Open(_probeStartedAt + ProbeTimeout);

                // Abandoned probe: re-arm rather than pause forever. The opportunity opened when
                // the old probe could no longer be running, not now — so the defer window below is
                // measured from that instant and a Background caller does not restart it by asking.
                eligibleAt = _probeStartedAt + ProbeTimeout;
            }
            else if (BlockedUntil is { } until)
            {
                if (now < until) return GateDecision.Open(until);
                eligibleAt = until;             // the window that has just elapsed
            }

            // ---- 2. the probe preference (§5.4). Latch the INSTANT, not the caller: a per-caller
            // "has deferred once" flag is untestable on a fake clock and would let two Background
            // callers each defer once and then both race for the probe. Before
            // `eligibleAt + ProbeDeferMs` no Background caller gets the probe; at or after it, the
            // first caller of any priority does.
            if (eligibleAt is { } at && priority == RequestPriority.Background && now < at + ProbeDefer)
                return GateDecision.Open(at + ProbeDefer);

            // ---- 3. the rate ceiling (§5.4). Written as a floor derived from BucketCapacity, not
            // as a hard-coded 2, so E2.S7 can move the capacity without silently deleting the
            // reserve: Background needs the whole bucket, Interactive needs one token.
            var floor = priority == RequestPriority.Background ? TranslationPolicy.BucketCapacity : 1;
            var tokens = Refill(now);

            if (tokens < floor)
                // The time to the next token THIS caller is allowed to take. Computed against the
                // caller's own floor, or a Background caller would be told to come back 500 ms too
                // early and be refused again — the same request twice, which is the behaviour this
                // epic exists to remove.
                return GateDecision.Wait(RefillPeriod * (floor - tokens));

            _tokens = tokens - 1;               // spent: the caller is about to make a real request

            if (eligibleAt is null) return GateDecision.Allow;

            _probeOutstanding = true;           // half-open: this caller, and only this caller
            _probeStartedAt = now;
            return GateDecision.Probe(_probeToken = ++_probeSeq);
        }
    }

    /// <summary>
    /// The bucket's level at <paramref name="now"/>: <c>+1</c> per <c>MinSpacingMs</c> elapsed,
    /// clamped at <c>BucketCapacity</c>. Commits the refill — which spends nothing and is
    /// idempotent — so the arithmetic never accumulates across calls. Called from
    /// <see cref="TryEnter"/> only: <see cref="Snapshot"/> must not refill the bucket, or the
    /// ceiling would depend on whether the About tab is open (T6).
    /// </summary>
    private double Refill(DateTimeOffset now)
    {
        var cap = (double)TranslationPolicy.BucketCapacity;

        if (_tokensAt is { } since)
        {
            var elapsed = now - since;
            if (elapsed > TimeSpan.Zero) _tokens += elapsed / RefillPeriod;
            if (_tokens > cap) _tokens = cap;   // clamp BEFORE the comparison, always
        }

        _tokensAt = now;
        return _tokens;
    }

    /// <summary>The request worked. A probe that succeeds closes the gate outright (§5.2); an
    /// ordinary success starts — or ages — the clean run that resets the strikes.</summary>
    /// <param name="probeToken">Ruling <b>E2-h</b>: the token <see cref="TryEnter"/> handed this
    /// caller with <c>GateOutcome.Probe</c>. Omitted (0) by every ordinary request, which is the
    /// point: a 200 from a request dispatched <i>before</i> the gate closed behind it used to
    /// arrive while a probe was outstanding, close the gate and reset the whole strike ladder. It
    /// is still a real success — the soft count and the clean run see it — but only the probe's own
    /// report may end the half-open window.</param>
    internal void ReportSuccess(long probeToken = 0)
    {
        lock (_lock)
        {
            var now = _clock();
            _lastAt = now;
            _badResponses = 0;                  // "a success in between resets the soft count" (§5.3)

            if (IsOutstandingProbe(probeToken))
            {
                _probeOutstanding = false;
                _probeToken = 0;                // one report per probe; a replay resolves nothing
                _strikes = 0;
                _keyBlockedUntil = null;         // a successful probe closes the gate outright,
                _ipBlockedUntil = null;          // both timelines with it (§5.2)
                _cleanSince = now;
                return;
            }

            // A clean run cannot start inside a block. The success is real, but it belongs to a
            // request that was already in flight when the gate closed behind it (the two chains
            // share one gate, I9), and it is not evidence that the provider is well now — only a
            // probe is. Without the guard, one such late 200 sets `cleanSince` at the start of a
            // 30-minute window; CleanResetMinutes later the probe's own failure runs DecayStrikes
            // first and re-opens at strike 1 for 60 s, erasing the whole escalation ladder.
            if (BlockedUntil is null) _cleanSince ??= now;
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
    /// <param name="probeToken">Ruling <b>E2-h</b>, the failure half: only the outstanding probe's
    /// own token releases the half-open latch. A stale in-flight failure still counts against §5.3
    /// — the evidence is real — but it may not <i>steal</i> the probe, which would leave the
    /// healthy provider that is still being probed blocked for another full window.</param>
    internal void ReportFailure(TranslationErrorKind kind, DateTimeOffset? retryAt = null, long probeToken = 0)
    {
        var reaction = Reaction(kind);
        if (reaction == GateReaction.Ignore) return;   // not one byte of state changes — see Reaction

        lock (_lock)
        {
            var now = _clock();
            // A probe failure is an ordinary failure that happens to end half-open: the machine
            // needs no branch on it, only the latch released so the window it is about to set is
            // the state everyone sees (§5.2, HalfOpen → Open).
            var wasProbe = IsOutstandingProbe(probeToken);
            if (wasProbe)
            {
                _probeOutstanding = false;
                _probeToken = 0;                // one report per probe; a replay resolves nothing
            }
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
                    BlockUntil(window, kind);
                    break;

                case GateReaction.Quota:        // open for an hour, no strike escalation (§15 R9)
                    BlockUntil(now + Honour(retryAt, TimeSpan.FromMinutes(_policy.QuotaOpenMinutes), now), kind);
                    break;

                case GateReaction.Auth:         // dead credentials: no window can fix them
                    _keyBlockedUntil = DateTimeOffset.MaxValue;   // account-scoped: the key is the exit
                    break;

                case GateReaction.SoftCooldown: // Unavailable, Timeout, Network, Unknown — no strike
                    // Small, but not optional: without it a DNS blip means the next LIVE tick
                    // re-hits the same dead provider 700 ms later.
                    BlockUntil(now + Honour(retryAt, TimeSpan.FromSeconds(_policy.SoftCooldownSecs), now), kind);
                    break;

                case GateReaction.SoftStrike:   // BadResponse: 3 consecutive open the gate
                    _badResponses++;
                    if (_badResponses >= _policy.BadResponseStrikesToOpen)
                    {
                        _badResponses = 0;
                        BlockUntil(now + Honour(retryAt, TimeSpan.FromSeconds(_policy.OpenBaseSeconds), now), kind);
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
            //
            // The hint goes through Honour here too: §5.5 parses a Retry-After on EVERY non-success
            // response, and this arm is the one place a row could otherwise drop one — a failed
            // probe on the first or second BadResponse sets no window of its own, so a flat 5 s
            // would silently discard a "come back in ten minutes" the server just sent.
            if (wasProbe && BlockedUntil is { } b && b <= now)
                BlockUntil(now + Honour(retryAt, TimeSpan.FromSeconds(_policy.SoftCooldownSecs), now), kind);

            _lastKind = kind;
            _lastAt = now;
            _cleanSince = null;                 // the clean run is over; the next success restarts it
        }
    }

    /// <summary>
    /// The user re-saved this provider's key. <b>Ruling E2-i</b> (which supersedes E2.S1's D7 "it
    /// clears the gate whatever opened it"): a new key clears only what depends on the key — the two
    /// <i>account-scoped</i> rows of §5.3, <c>AuthFailed</c> ("open until the key changes") and
    /// <c>QuotaExhausted</c> ("cleared when the key is re-saved"). A <c>RateLimited</c> or
    /// <c>Blocked</c> window is <i>IP-scoped</i> — it is a counter on the provider's side, keyed to
    /// the address this PC dials from, and no key the user types can move it; a soft cooldown is a
    /// dead endpoint or a DNS blip, which a key cannot fix either. Clearing those would turn a
    /// key-save into "resume hammering the provider that just said stop", which is the one thing
    /// this whole epic exists to prevent.
    ///
    /// <para>Which is why the two timelines exist: this clears the <b>account-scoped</b> one and
    /// leaves the other exactly where it was. A 429 window that a later 401 hid underneath a
    /// <c>MaxValue</c> is still standing when the key save lifts that <c>MaxValue</c>, and the gate
    /// stays open for the rest of it — a single field could not tell those two apart, and the user
    /// would have bought the provider a fresh round of requests it had already refused. The strike
    /// ladder is left alone for the same reason: it is the IP-scoped evidence, and it ages out
    /// through <c>CleanResetMinutes</c> of clean operation.</para>
    ///
    /// <para><c>ProviderGates.ClearAuthBlock(id)</c> (E2.S2) forwards here from the key-save
    /// handler.</para>
    /// </summary>
    internal void ClearAuthBlock()
    {
        lock (_lock)
        {
            if (_keyBlockedUntil is null) return;   // nothing a key could have caused

            _keyBlockedUntil = null;
            if (BlockedUntil is not null) return;   // an IP-scoped window still stands (E2-i)

            _probeOutstanding = false;
            _probeToken = 0;                        // whatever was in flight can no longer resolve it
            _cleanSince = _clock();                 // the gate is closed: a clean run starts here
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
                      : BlockedUntil is null ? GateState.Closed
                      : GateState.Open;         // still Open once the window elapses — until a caller
                                                // takes the probe, which is what makes it exactly one
            return new GateSnapshot(state, BlockedUntil, _strikes, _lastKind);
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
    /// Opens until <paramref name="candidate"/> on <paramref name="kind"/>'s own timeline, but
    /// <b>never shortens</b> a block that already stands there. The case is not hypothetical: two
    /// requests are in flight (the LIVE batch and the Translator tab share the gate, I9), the first
    /// comes back 429 and opens the gate for 30 minutes, the second times out a second later — and
    /// a plain assignment would replace those 30 minutes with a 5-second cooldown and hand a
    /// blocked provider a request every 5 seconds. A soft failure may extend a block, never lift
    /// one. Every row goes through here since ruling E2-f: the last exemption — an escalating row
    /// carrying an explicit <c>Retry-After</c> — went away when the hint became a floor rather than
    /// the server's last word.
    ///
    /// <para>Which timeline it lands on is ruling E2-i (see <see cref="IsKeyScoped"/>), and the two
    /// never shorten each other: the gate enforces the later of them, so a 60-minute quota does not
    /// erase the 30-minute 429 window it covers, and lifting the quota hands the rest of that
    /// window back rather than the whole provider.</para>
    /// </summary>
    private void BlockUntil(DateTimeOffset candidate, TranslationErrorKind kind)
    {
        if (IsKeyScoped(kind))
            _keyBlockedUntil = _keyBlockedUntil is { } key && key > candidate ? key : candidate;
        else
            _ipBlockedUntil = _ipBlockedUntil is { } ip && ip > candidate ? ip : candidate;
    }

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
