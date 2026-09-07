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
/// Thread-safe behind one <c>lock</c>, like <c>TranslationCacheStore.cs:29</c> (E4.S1 moved that lock
/// out of <c>CachingTranslator</c>, which this line used to cite): both chains and the LIVE
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
/// Since E2.S4 it also carries the persisted half: <see cref="TrySeedState"/> rebuilds the breaker
/// from <c>provider-state.json</c> — the breaker only; the bucket starts full in every process and
/// no probe latch is ever restored (I9, §5.7) — <see cref="ExportState"/> is what gets written, and
/// two <c>Action</c>s handed in at construction let the registry read the file on the first
/// <see cref="TryEnter"/> and queue a debounced write on a transition. Nothing here is wired to a
/// caller: providers reach a gate only through <c>HttpProviderCore</c> (E2.S5).
/// </summary>
internal sealed class ProviderGate
{
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly GatePolicy _policy;

    // The two seams E2.S4 hands in at construction. Neither is an event and neither is settable
    // afterwards (ruling R-2/OQ-c: no state-change event out of Services/) — an Action the registry
    // passes to the gates it builds is the registry talking to itself, and it keeps this class
    // passive. Both are null for a gate a test builds directly, which is what keeps ProviderGateTests
    // free of the file entirely.
    private readonly Action? _ensureLoaded;             // read provider-state.json, once per process
    private readonly Action<GateState, GateSnapshot>? _onTransition;   // queue a debounced save

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

    /// <summary>
    /// How long a half-open probe may be outstanding before the gate re-arms it. Derived rather
    /// than a new ungraded number, and derived from what a probe actually <b>is</b>: since E2.S5 an
    /// admission covers one LOGICAL CALL, so a probe is up to
    /// <see cref="TranslationPolicy.MaxAttempts"/> requests — each bounded by the client's own
    /// timeout — plus the back-off drawn between them.
    ///
    /// <para>It used to read "a probe is one request, and a request cannot outlive that timeout".
    /// That stopped being true the moment the probe covered a retry: two 12 s timeouts plus jitter
    /// outlive a 12 s bound, and the gate would then re-arm <i>while the probe was still in
    /// flight</i> — handing a second caller a real request into the same closed door, and leaving
    /// the original probe's report to be rejected by <see cref="IsOutstandingProbe"/>, so even a
    /// SUCCESSFUL probe could no longer close the gate. Derived from the policy, the bound moves
    /// with it and E2.S7 cannot tune one without the other.</para>
    ///
    /// <para>It also closes the only way this class could pause the app forever (R-01): a caller
    /// that takes the probe and never reports — a cancel mid-probe, or a crash — would otherwise
    /// leave the gate half-open, admitting nobody, for good.</para>
    /// </summary>
    internal static readonly TimeSpan ProbeTimeout =
        TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds * TranslationPolicy.MaxAttempts)
        // The gaps between attempts: full jitter draws below BackoffBaseMs << n, so the attempts-1
        // gaps sum to less than BackoffBaseMs * (2^(MaxAttempts-1) - 1).
        + TimeSpan.FromMilliseconds(
            TranslationPolicy.BackoffBaseMs * ((1 << (TranslationPolicy.MaxAttempts - 1)) - 1));

    /// <summary>How long a <c>Background</c> caller stands aside before taking a probe itself
    /// (§5.4, architect's concern #1). <c>Interactive</c> is never deferred.</summary>
    private static readonly TimeSpan ProbeDefer =
        TimeSpan.FromMilliseconds(TranslationPolicy.ProbeDeferMs);

    /// <summary>One token per <c>MinSpacingMs</c> — the refill period, as a period.</summary>
    private static readonly TimeSpan RefillPeriod =
        TimeSpan.FromMilliseconds(TranslationPolicy.MinSpacingMs);

    /// <summary>
    /// How far below its floor the bucket may sit and still count as at it. <c>_tokens</c> is a
    /// <c>double</c>, so a level that is arithmetically exactly at the floor lands a few ULPs under
    /// it on perfectly ordinary timings: two requests 0.4 × <c>MinSpacingMs</c> apart and a third
    /// 0.6 × later leave <c>0.4 + 0.6 = 0.9999999999999999</c>. Without this the gate answers
    /// <c>Wait(0 ticks)</c> — "come back immediately" — which a caller obeys and is refused again at
    /// the same instant: a spin, not a pause, and a refusal that names no delay is not a decision
    /// E3.S3 or E5.S1 can act on. One clock tick's worth of refill (100 ns) is finer than any
    /// instant <c>DateTimeOffset</c> can distinguish, so nothing the ceiling means to refuse gets
    /// through on it — and it makes the shortest <c>Wait</c> the bucket can return exactly one tick.
    /// Declared after <see cref="RefillPeriod"/> because static initialisers run in textual order.
    /// </summary>
    private static readonly double TokenEpsilon = 1.0 / RefillPeriod.Ticks;

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
    /// <param name="ensureLoaded">E2.S4: read <c>provider-state.json</c> if this process has not yet
    /// (AC 2 / I10). Called at the top of <see cref="TryEnter"/> and nowhere else — the trigger
    /// cannot be <c>ProviderGates.For</c>, which runs inside <c>MainWindow</c>'s constructor (E3.S7),
    /// before first paint.</param>
    /// <param name="onTransition">E2.S4: "this gate just changed state, from X to this snapshot" —
    /// the registry turns it into a debounced write and E2.S6 will turn it into a log line. Raised
    /// <b>outside</b> the lock (see <see cref="Notify"/>).</param>
    internal ProviderGate(Func<DateTimeOffset>? clock = null, GatePolicy? policy = null,
                          Action? ensureLoaded = null,
                          Action<GateState, GateSnapshot>? onTransition = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _policy = policy ?? GatePolicy.Default;
        _ensureLoaded = ensureLoaded;
        _onTransition = onTransition;
    }

    /// <summary>
    /// This gate's own "now" (IS-6). <c>HttpProviderCore</c> reads it to resolve a delta-form
    /// <c>Retry-After</c>: the header says "in 300 seconds", the gate stores an instant, and the two
    /// must be measured against the same clock or a value the server sent gets compared to a
    /// different now and clamped as if it were something else. Read-only, and reading a clock is
    /// not a side effect — R-2's rule is that a snapshot may not <i>advance</i> one.
    /// </summary>
    internal DateTimeOffset Now() => _clock();

    /// <summary>Seed this gate from <c>provider-state.json</c> if the process has not read it yet —
    /// E2.S4's lazy load, reached from the one caller that decides <b>not</b> to make a request.
    ///
    /// <para><see cref="TryEnter"/> stays the trigger for every path that sends something (AC 2);
    /// this exists because E5.S1's LIVE loop asks <see cref="Snapshot"/> whether it may skip the
    /// whole tick, and a snapshot of an unseeded gate answers "nothing is blocked" for a window that
    /// is standing on disk. Without it the first tick of every session captures, OCRs, advances the
    /// dedup clock and sends a request that the gate then refuses — the one case the persisted state
    /// exists for (resuming into a 30-minute window), and the story's own manual verification.</para>
    ///
    /// <para>Not a side effect in R-2's sense: no clock advances, no probe is taken, no token is
    /// spent. It is idempotent and, after the first call of the process, one predicted branch
    /// (<c>ProviderGates.EnsureLoaded</c>'s <c>Volatile.Read</c>). A gate built without the seam — a
    /// unit test's own — has nothing to load and this is a no-op.</para></summary>
    internal void EnsureStateLoaded() => _ensureLoaded?.Invoke();

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
        // E2.S4 / AC 2: the one place provider-state.json is read. It is here rather than in
        // ProviderGates.For because For runs while MainWindow's constructor builds the chains
        // (E3.S7), i.e. before first paint — loading there would break I10 and
        // TP-START-01. Before the lock, so the registry's load can seed this very gate without a
        // lock inversion; after the first request of the process it is one predicted branch
        // (ProviderGates.EnsureLoaded's Volatile.Read).
        _ensureLoaded?.Invoke();

        GateState from = default;
        GateSnapshot? landed = null;
        var decision = Enter(priority, ref from, ref landed);
        // Open → HalfOpen is the only edge TryEnter can take, and it is raised out here: firing a
        // callback that takes the registry's lock while holding this gate's would be a lock
        // inversion against the flush path, which walks every gate.
        if (landed is not null) Notify(from, landed);
        return decision;
    }

    /// <summary>The decision itself, under the lock. Split out of <see cref="TryEnter"/> only so the
    /// transition it can cause is announced after the lock is released.</summary>
    private GateDecision Enter(RequestPriority priority, ref GateState from, ref GateSnapshot? landed)
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

                // …but never re-arm THROUGH a block that landed while the probe was out. Ruling
                // E2-h is what makes that state reachable and this branch predates it: a stale
                // in-flight 429 or 401 is still classified against §5.3 and still sets a window,
                // yet it may no longer release the latch — so `_probeOutstanding` can now be true
                // with a brand-new block standing under it, and an abandonment (a cancel
                // mid-probe, which by I3 never reports at all, or a crash) leaves it there. Read
                // off the probe timeout alone, this branch then handed a caller a real request
                // every ProbeTimeout straight through the window the provider had just asked for
                // — and, on an `AuthFailed` MaxValue, through the one block only ClearAuthBlock
                // may lift (AC 4). The gate is eligible at the LATER of the two instants; until
                // then it is simply Open, which is also what `Snapshot()` already reports.
                if (BlockedUntil is { } standing)
                {
                    if (now < standing) return GateDecision.Open(standing);
                    if (standing > eligibleAt) eligibleAt = standing;
                }
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
            //
            // At BucketCapacity = 2 this IS §5.4's rule word for word — "Background may draw the
            // bucket down but may not take the last token; one token of the capacity-2 bucket is
            // reserved for Interactive" — because leaving one behind and requiring a full bucket
            // are the same statement when the capacity is two. They are NOT the same statement at
            // any other capacity: at 5, "may not take the last token" is `tokens >= 2` while this
            // line says `tokens >= 5`, which reserves four and lets a steady Interactive trickle
            // starve Background outright. E2.S7 (U9) is the story that may move the capacity, and
            // this is the line it must read before it does — the reserve is one token, not the
            // difference between the capacity and one.
            var floor = priority == RequestPriority.Background ? TranslationPolicy.BucketCapacity : 1;
            var tokens = Refill(now);

            if (tokens + TokenEpsilon < floor)
                // The time to the next token THIS caller is allowed to take. Computed against the
                // caller's own floor, or a Background caller would be told to come back 500 ms too
                // early and be refused again — the same request twice, which is the behaviour this
                // epic exists to remove. Never zero and never negative: `tokens` is below `floor`
                // by more than TokenEpsilon here, which is one tick's worth of refill.
                return GateDecision.Wait(RefillPeriod * (floor - tokens));

            // Spent: the caller is about to make a real request. Floored at zero because the
            // TokenEpsilon above admits a caller sitting a few ULPs under its floor, and a level
            // that went very slightly negative would be a debt nobody meant to record — `_tokens`
            // is a level, and levels do not go below empty.
            _tokens = tokens > 1 ? tokens - 1 : 0;

            if (eligibleAt is null) return GateDecision.Allow;

            from = StateAt(now);                // Open — the window this probe is being granted out of
            _probeOutstanding = true;           // half-open: this caller, and only this caller
            _probeStartedAt = now;
            var probe = GateDecision.Probe(_probeToken = ++_probeSeq);
            landed = SnapshotAt(now);           // §5.2's Open → HalfOpen edge, worth a write
            return probe;
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
    /// <param name="selfHealing"><b>Ruling E8-e, and it is written for callers that have no probe to
    /// hold.</b> A LOCAL provider never calls <see cref="TryEnter"/> — there is no endpoint to be
    /// polite to and no token bucket to spend — so nothing ever hands it
    /// <see cref="GateOutcome.Probe"/>, and the ordinary arm below cannot close a gate. The result
    /// was that one failure left <see cref="StateAt"/> reading <c>Open</c> for the rest of the
    /// process (the state outlives its window on purpose, ruling E3-a) and E7's chip said "paused"
    /// about a working engine, for ever.
    ///
    /// <para>What this flag buys is exactly one thing: <b>the first success after the window has
    /// ELAPSED closes the gate</b>. Not a success inside a live window — the chain skips a tier
    /// inside one, so such a success can only come from a caller that went round the chain, and it
    /// is not evidence the window was wrong. It is <c>false</c> by default and no HTTP provider
    /// passes it, so <c>ProviderGateTests</c>' probe semantics are untouched: for a remote endpoint
    /// only a probe may end a half-open window, which is ruling E2-h and stays.</para></param>
    internal void ReportSuccess(long probeToken = 0, bool selfHealing = false)
    {
        GateState from = default;
        GateSnapshot? landed = null;

        lock (_lock)
        {
            var now = _clock();
            from = StateAt(now);
            _lastAt = now;
            _badResponses = 0;                  // "a success in between resets the soft count" (§5.3)

            if (selfHealing && !_probeOutstanding && BlockedUntil is { } elapsed && elapsed <= now)
            {
                // The window is over and a local engine has just proved it is well. Both timelines,
                // the strikes and the clean run, exactly as a successful probe leaves them — because
                // that is what this IS for a provider that cannot be probed.
                _strikes = 0;
                _keyBlockedUntil = null;
                _ipBlockedUntil = null;
                _cleanSince = now;
                landed = SnapshotAt(now);       // Open → Closed: the pause is over, persist it
            }
            else if (IsOutstandingProbe(probeToken))
            {
                _probeOutstanding = false;
                _probeToken = 0;                // one report per probe; a replay resolves nothing
                _strikes = 0;
                _keyBlockedUntil = null;         // a successful probe closes the gate outright,
                _ipBlockedUntil = null;          // both timelines with it (§5.2)
                _cleanSince = now;
                landed = SnapshotAt(now);        // HalfOpen → Closed: the pause is over, persist it
            }
            else
            {
                // A clean run cannot start inside a block. The success is real, but it belongs to a
                // request that was already in flight when the gate closed behind it (the two chains
                // share one gate, I9), and it is not evidence that the provider is well now — only a
                // probe is. Without the guard, one such late 200 sets `cleanSince` at the start of a
                // 30-minute window; CleanResetMinutes later the probe's own failure runs DecayStrikes
                // first and re-opens at strike 1 for 60 s, erasing the whole escalation ladder.
                if (BlockedUntil is null) _cleanSince ??= now;

                // A strike reset is not a §5.2 edge — the state does not move — but it IS persisted
                // state, and T3 lists it as worth a write: a restart must not hand a provider back
                // an escalation ladder it has already worked off. E2.S6 filters it out by `from ==
                // to.State`, which is exactly why the edge is reported as it is and not invented.
                if (DecayStrikes(now)) landed = SnapshotAt(now);
            }
        }

        if (landed is not null) Notify(from, landed);
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

        GateState from = default;
        GateSnapshot? landed = null;

        lock (_lock)
        {
            var now = _clock();
            from = StateAt(now);
            var blockedBefore = BlockedUntil;
            // A probe failure is an ordinary failure that happens to end half-open: the machine
            // needs no branch on it, only the latch released so the window it is about to set is
            // the state everyone sees (§5.2, HalfOpen → Open).
            var wasProbe = IsOutstandingProbe(probeToken);
            if (wasProbe)
            {
                _probeOutstanding = false;
                _probeToken = 0;                // one report per probe; a replay resolves nothing
            }
            var strikesReset = DecayStrikes(now);   // a failure after a long clean run starts from strike 1

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

            // E2.S4 / T3: which failures are worth a write. The §5.2 edges are — a breaker that
            // opened, a probe that failed — and so is a strike reset. A SOFT COOLDOWN IS NOT, even
            // though it does set a 5-second window and does move Snapshot() to Open: it is worth
            // five seconds, and a DNS blip on every LIVE tick would otherwise rewrite the file every
            // 700 ms. E2.S6 must make the same call for its log lines (ruling E2-b already does:
            // "no line for strike resets, soft cooldowns"), and the two must agree.
            // The window moving counts too, and not only the state: a second 429 arriving while the
            // gate is already open escalates the ladder and lengthens the pause without changing
            // Open → Open, and a restart must not hand back the shorter window.
            var softOnly = reaction == GateReaction.SoftCooldown && !wasProbe;
            var to = StateAt(now);
            if (!softOnly && (to != from || strikesReset || BlockedUntil != blockedBefore))
                landed = SnapshotAt(now);
        }

        if (landed is not null) Notify(from, landed);
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
        GateState from = default;
        GateSnapshot? landed = null;

        lock (_lock)
        {
            if (_keyBlockedUntil is null) return;   // nothing a key could have caused

            var now = _clock();
            from = StateAt(now);
            _keyBlockedUntil = null;

            if (BlockedUntil is null)
            {
                _probeOutstanding = false;
                _probeToken = 0;                    // whatever was in flight can no longer resolve it
                _cleanSince = now;                  // the gate is closed: a clean run starts here
            }
            // Persisted either way: a QuotaExhausted window that the user has just bought their way
            // out of must not come back on the next start, whether or not an IP-scoped window is
            // still standing underneath it (E2-i). An AuthFailed sentinel was never on disk in the
            // first place (E2-a), so that case writes a file that says the same thing it already did.
            landed = SnapshotAt(now);
        }

        Notify(from, landed);
    }

    /// <summary>
    /// <b>Test-only seam.</b> Empties the rate-ceiling bucket as of now, so a case can reach the one
    /// state <c>TryEnter</c>/<c>Report*</c> cannot produce from outside with today's constants:
    /// probe-eligible <i>and</i> broke. (The shortest block window any <see cref="GatePolicy"/> can
    /// set is <c>SoftCooldownSecs</c>, which already refills <c>BucketCapacity × MinSpacingMs</c>,
    /// so by the time a window elapses the bucket is full. That is a relationship E2.S7 can change,
    /// not a law — which is why the guard it feeds is not dead code but one <c>if</c> ordering away
    /// from R-01.) No production code calls this, and nothing else may: it exists because reflecting
    /// on this class's own private fields from the suite makes a rename read as a passing test.
    /// Takes the lock like every other mutator.
    /// </summary>
    internal void DrainBucketForTests()
    {
        lock (_lock)
        {
            _tokens = 0;
            _tokensAt = _clock();
        }
    }

    /// <summary>The state, for a 1 Hz poll (R-2). Read-only: no clock is advanced, no probe taken,
    /// no token spent.</summary>
    internal GateSnapshot Snapshot()
    {
        lock (_lock) { return SnapshotAt(_clock()); }
    }

    /// <summary>Where the gate stands at <paramref name="now"/>. Called under <c>_lock</c>, by
    /// <see cref="Snapshot"/> and by every mutator that has to know which §5.2 edge it just took.
    ///
    /// <para>HalfOpen only while a probe could still be running — the same test <see cref="TryEnter"/>
    /// applies before it re-arms one. Reading <c>_probeOutstanding</c> alone would leave the 1 Hz
    /// status chip on "checking…" for the life of the process after a probe was abandoned (LIVE
    /// switched off mid-probe, or a crash), because the re-arm needs a <c>TryEnter</c> that a paused
    /// app never makes. Reading the clock is not a side effect: no probe is taken, no state is
    /// written.</para></summary>
    private GateState StateAt(DateTimeOffset now) =>
        _probeOutstanding && now - _probeStartedAt < ProbeTimeout ? GateState.HalfOpen
        : BlockedUntil is null ? GateState.Closed
        : GateState.Open;                       // still Open once the window elapses — until a caller
                                                // takes the probe, which is what makes it exactly one

    private GateSnapshot SnapshotAt(DateTimeOffset now) =>
        new(StateAt(now), BlockedUntil, _strikes, _lastKind);

    /// <summary>Announce a transition to the registry, <b>outside</b> the lock. Never inside: the
    /// registry's flush walks every gate and takes their locks, so a callback raised while holding
    /// one would put the two lock orders back to back and deadlock the first time a debounced save
    /// landed on the same instant as a report.</summary>
    private void Notify(GateState from, GateSnapshot to) => _onTransition?.Invoke(from, to);

    // ---- §5.7, the persisted half (E2.S4) ------------------------------------------------------

    /// <summary>
    /// This gate as the file sees it, or <c>null</c> when there is nothing worth a line — a gate that
    /// <c>For(id)</c> created and nothing ever failed on writes no entry at all, which is what keeps
    /// the file a few hundred bytes.
    ///
    /// <para><b>What is deliberately not exported.</b> The token bucket and the probe latch (§5.7,
    /// I9 — see the field comments). <c>_badResponses</c> too, and for the same reason as the
    /// bucket: three consecutive malformed bodies are evidence about one conversation, not about
    /// the provider tomorrow, and §5.7 has no field for it — so a gate sitting at two of three
    /// starts over next session. It is still counted by <see cref="ExportedStateExists"/>, because
    /// "may a file overwrite this gate?" is a different question from "is this worth writing?".
    /// And <c>AuthFailed</c>, both halves of it: ruling <b>E2-a</b> says
    /// it is never persisted in A.1 — in-memory only, a restart resets it. The key is re-read from
    /// settings at every start anyway, the user may well have fixed it while the app was closed, and
    /// a <i>file</i> that pauses a provider until the user notices is exactly the lockout R-01 is
    /// about (the story's own alternative — persist it and clamp it — was rejected here in favour of
    /// the ruling, which is later and binding). So the <c>MaxValue</c> sentinel is dropped from the
    /// account-scoped timeline and the kind is dropped from <c>lastKind</c>; a <c>QuotaExhausted</c>
    /// window that a later 401 happened to cover is lost with it, which is the accepted cost of the
    /// two blocks sharing one timeline.</para>
    /// </summary>
    internal ProviderStateRecord? ExportState()
    {
        lock (_lock)
        {
            var key = _keyBlockedUntil == DateTimeOffset.MaxValue ? null : _keyBlockedUntil;
            var kind = _lastKind == TranslationErrorKind.AuthFailed ? null : _lastKind;

            // A lone `cleanSince` is deliberately NOT worth a line. It exists only to decay
            // strikes, so with none to decay it restores nothing a fresh gate does not already
            // have — and it is precisely what a 401 and the key save that lifts it leave behind,
            // since E2-a drops both halves of AuthFailed on the way out. Counting it would make
            // that pair the one way a session with nothing to remember still creates
            // provider-state.json beside the user's settings.json.
            // This is NOT the question <see cref="ExportedStateExists"/> answers — that one asks
            // whether this gate has seen anything in THIS process and so may not be seeded over by
            // a file, and a clean run very much counts as evidence there.
            if (_ipBlockedUntil is null && key is null && _strikes == 0 && kind is null) return null;

            return new ProviderStateRecord(
                _ipBlockedUntil, key, _strikes, kind?.ToString(), _lastAt, _cleanSince);
        }
    }

    /// <summary>
    /// Restore what <see cref="ExportState"/> wrote, <b>without</b> going through
    /// <see cref="ReportFailure"/> — which would escalate a strike and announce a transition for a
    /// failure that happened in another process. Returns false, and changes nothing, if this gate has
    /// already recorded something in this process: evidence from a live request beats a file, and the
    /// load is what makes that ordering reachable at all.
    ///
    /// <para>Every window is clamped as it is read (AC 4 / §5.7): a machine whose clock jumped
    /// cannot pause the app for a week. The IP-scoped timeline is clamped to <c>OpenCapMinutes</c>,
    /// the account-scoped one to <c>QuotaOpenMinutes</c> — its own longest window (§15 R9), since
    /// clamping it to the cap would silently halve the one block §5.3 says lasts an hour. A value in
    /// the <i>past</i> needs no clamp: it is simply expired, the gate loads open and the first
    /// <c>TryEnter</c> half-opens it, which is the correct recovery and the reason the entry is not
    /// "helpfully" dropped.</para>
    /// </summary>
    internal bool TrySeedState(ProviderStateRecord record, out bool normalised)
    {
        normalised = false;
        lock (_lock)
        {
            if (ExportedStateExists()) return false;

            var now = _clock();
            _ipBlockedUntil = Clamp(record.BlockedUntil, now, TimeSpan.FromMinutes(_policy.OpenCapMinutes));
            _keyBlockedUntil = Clamp(record.KeyBlockedUntil, now, TimeSpan.FromMinutes(_policy.QuotaOpenMinutes));
            _strikes = Math.Max(0, record.Strikes);
            _lastAt = record.LastAt;

            // A clean run is time we WATCHED the provider behave, and we watched nothing while the
            // app was closed. Restored verbatim, `cleanSince` credits downtime as good behaviour:
            // CleanResetMinutes is 10, so a file carrying both strikes and a clean run would let
            // ten minutes of being CLOSED work the ladder off — quitting becoming the way to buy a
            // fresh 60-second window out of a provider that has already said stop four times,
            // which is exactly what persisting the strikes is meant to stop.
            // **This build cannot write that pair itself** — every path that sets `cleanSince`
            // first tests `BlockedUntil is null`, and a non-zero strike count always leaves an IP
            // timeline standing — so what this closes is the hand-edited or foreign-written file,
            // plus the case the app CAN produce: a `cleanSince` in the FUTURE (a PC whose clock was
            // ahead when the run started, then corrected) makes `now - since` negative, so nothing
            // ages out ever again. The run therefore restarts at the instant we load —
            // conservative in the only direction that matters, since it can lengthen a pause and
            // never shorten one, and it is re-applied on every load rather than written back.
            _cleanSince = record.CleanSince is null ? null : now;

            // E2-a from the reading side, so the invariant is total: this app never writes
            // AuthFailed, and a hand-edited or downgraded file that does cannot bring it back either.
            var kind = ProviderStateStore.ParseKind(record.LastKind);
            _lastKind = kind == TranslationErrorKind.AuthFailed ? null : kind;

            // Did reading it change it? If so the FILE still says the dangerous thing, and nothing
            // else will ever correct it: a gate that is merely refusing callers takes no transition,
            // so it queues no write, so the next launch reads the same value and clamps it again.
            // A clock that jumped forward once would then re-impose a 30-minute pause at EVERY
            // start, for ever — R-01 by the back door, and the exact lockout ruling E2-a says no
            // state may cause "without a way back". `cleanSince` is deliberately NOT counted: it is
            // re-normalised on every load, so writing it back would buy a write and nothing else.
            normalised = _ipBlockedUntil != record.BlockedUntil
                      || _keyBlockedUntil != record.KeyBlockedUntil
                      || _strikes != record.Strikes
                      || _lastKind?.ToString() != record.LastKind;

            return true;
        }
    }

    /// <summary>Whether anything in this gate would be written to the file — the same question
    /// <see cref="ExportState"/> answers, without building the record. Called under <c>_lock</c>.</summary>
    private bool ExportedStateExists() =>
        _ipBlockedUntil is not null || _keyBlockedUntil is not null
        || _strikes != 0 || _lastKind is not null || _cleanSince is not null
        || _badResponses != 0;      // not persisted, but still evidence a file may not overwrite

    private static DateTimeOffset? Clamp(DateTimeOffset? value, DateTimeOffset now, TimeSpan cap) =>
        value is not { } v ? null : v > now + cap ? now + cap : v;

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
    /// <returns>True if it actually reset something — E2.S4 persists a strike reset (T3), and
    /// "nothing to reset" must not queue a write on every single success.</returns>
    private bool DecayStrikes(DateTimeOffset now)
    {
        if (_cleanSince is { } since && now - since >= TimeSpan.FromMinutes(_policy.CleanResetMinutes))
        {
            var reset = _strikes != 0 || _badResponses != 0;
            _strikes = 0;
            _badResponses = 0;
            _cleanSince = now;                  // the run continues; it does not have to start over
            return reset;
        }
        return false;
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
