using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace PWRUHelper.Services;

/// <summary>
/// Everything <c>architecture-cible.md</c> §7.0 calls "the rules shared by every HTTP provider",
/// in one place: the client, the gate consult, the send, the ≤ 2 attempts with full jitter, the
/// single classification point, <c>Retry-After</c>, the §10.1 log line and the outcome report.
///
/// <para>It exists because <c>TranslationService</c> and <c>DeepLTranslator</c> were two
/// implementations of the same shape that disagreed: one had three attempts and a linear back-off,
/// the other had none; one logged, the other did not; one read the error body, the other did not.
/// E3 adds three more providers, and a provider should be a URL, a payload and a parser — not a
/// fifth copy of a retry policy.</para>
///
/// <para><b>What stays outside.</b> The URL and the payload, the auth headers, the User-Agent, the
/// parser, the batch semantics and the provider's own account of a status. The core knows a
/// provider id, whether a key travelled, and how to turn a failure into a sentence — it is handed
/// the last one, it does not choose it (E7.S1 owns the wording).</para>
///
/// <para><b>I2</b>: no WPF type, no dispatcher, no settings read. <b>I3</b>: the app's only
/// filtered <c>OperationCanceledException</c> catch on the request path now lives here, once.
/// <b>I11</b>: the one place a keyed provider's body reaches the log, so the key is scrubbed here —
/// <see cref="RequestLog"/> is UI-free and cannot know a key's value.</para>
/// </summary>
internal sealed class HttpProviderCore
{
    // ---- the seams (IS-7, IS-8, IS-11) ---------------------------------------------------------

    /// <summary>IS-7 — the back-off and the rate-ceiling wait, as one injectable function so the
    /// suite never sleeps for real (CI-3 forbids asserting timing with a sleep, and four end-to-end
    /// cases used to pay ~900 ms each of production back-off). Null = <see cref="Task.Delay(TimeSpan,
    /// CancellationToken)"/>, which is what production always uses.</summary>
    internal static Func<TimeSpan, CancellationToken, Task>? DelayOverride;

    /// <summary>The full-jitter draw, injectable for the same reason: TP-RET-06 asserts the band and
    /// that twenty samples are not all equal, and neither claim may depend on a real random. Takes
    /// the exclusive upper bound, returns the draw. Null = <see cref="Random.Shared"/>.</summary>
    internal static Func<int, int>? JitterOverride;

    // ---- the client ----------------------------------------------------------------------------

    /// <summary>
    /// The production handler, one shape for every provider. Without a pooled-connection lifetime a
    /// process-lifetime client can sit on a connection (or a DNS answer) that has gone stale and
    /// never replace it.
    ///
    /// <para><b>It installs no <see cref="SocketsHttpHandler.ConnectCallback"/>, deliberately</b>
    /// (Winston's E2.S5 review ruling). §10.1's <c>ipv=</c> would have been readable from one, and
    /// this story first shipped one — but a connect callback <i>replaces the runtime's own connect
    /// path</i>: DNS resolution, dual-stack Happy Eyeballs, proxy tunnelling and the connect-timeout
    /// semantics all become this app's code. This app runs on arbitrary home and corporate networks
    /// and there is no way to diagnose one remotely, so a diagnostic field is not worth owning the
    /// path every request travels on. .NET 8 exposes the peer address nowhere else on a public
    /// per-request API (<see cref="HttpResponseMessage"/> carries no connection info), so
    /// <c>ipv=</c> stays <see cref="RequestLog.UnknownAddressFamily"/> — which is §10.1's own
    /// instruction for a value nobody can know: log <c>?</c> rather than guess. The gap is recorded
    /// in the story.</para>
    /// </summary>
    internal static SocketsHttpHandler CreatePooledHandler() =>
        new() { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// One factory for every provider and both paths, so a test client differs from the production
    /// one by its handler and nothing else (IS-8).
    ///
    /// <para><b>The User-Agent is a provider option, not a shared default</b>, and that is a real
    /// trap rather than a preference: Google's frozen Chrome string is Google's
    /// (<c>architecture-cible.md</c> §7.0 keeps it and does not rotate it), while
    /// <c>DeepLTranslator</c> has never sent one. A shared factory that added one unconditionally
    /// would start sending a Chrome UA on a keyed vendor path in the field — a behaviour change
    /// smuggled in by a refactor.</para>
    /// </summary>
    internal static HttpClient CreateClient(HttpMessageHandler? handler, string? userAgent)
    {
        var client = new HttpClient(handler ?? CreatePooledHandler())
        {
            Timeout = TimeSpan.FromSeconds(TranslationPolicy.RequestTimeoutSeconds),
        };
        if (userAgent != null) client.DefaultRequestHeaders.Add("User-Agent", userAgent);
        return client;
    }

    // ---- one provider's share of the contract ---------------------------------------------------

    private readonly ProviderOptions _options;
    private readonly HttpClient _http;
    private readonly ProviderGate? _gate;

    /// <summary>The gate for this provider. Resolved through the registry on every call rather than
    /// captured at construction, because <b>I10</b> forbids touching the registry before the first
    /// request — a provider is built in <c>MainWindow</c>'s field initializer, before first paint.
    /// A test may hand in its own gate, exactly as it hands in its own handler.</summary>
    private ProviderGate Gate => _gate ?? ProviderGates.For(_options.ProviderId);

    /// <summary>This provider's key scrubber, or null when it has no key. Built once per provider
    /// rather than per emit: a closure allocated on every failed request is a footprint cost for
    /// nothing, and a keyless provider must not allocate one at all.</summary>
    private readonly Func<string, string>? _scrub;

    internal HttpProviderCore(ProviderOptions options, HttpClient http, ProviderGate? gate = null)
    {
        _options = options;
        _http = http;
        _gate = gate;
        _scrub = string.IsNullOrEmpty(options.Secret) ? null : line => Redact(line, options.Secret)!;
    }

    // ---- the request -----------------------------------------------------------------------------

    /// <summary>
    /// One logical call: admission, up to <see cref="TranslationPolicy.MaxAttempts"/> attempts, one
    /// outcome. Returns whatever <paramref name="parse"/> makes of the success body.
    ///
    /// <para>The parser is passed IN rather than run by the caller on purpose: a body that reaches
    /// the parser and does not fit the provider's shape is a <c>BadResponse</c>, and §5.3 gives
    /// three of those in a row a gate reaction. A parse that ran above the core would be a failure
    /// the gate never heard about, and — with the probe token — an admission that never reported
    /// (R-01 by the back door).</para>
    /// </summary>
    /// <param name="endpoint">Only ever rendered as host + path (I11); the query never travels.</param>
    /// <param name="request">A fresh message per attempt: an <see cref="HttpRequestMessage"/> may
    /// not be sent twice.</param>
    /// <param name="text">Handed over to be MEASURED, never kept: <see cref="RequestLog.Call"/>
    /// keeps its byte and line counts only.</param>
    /// <param name="priority">§5.4's reserve. <c>Interactive</c> until E3.S7 / E5.S4 pass the real
    /// values — see the note on <see cref="ProviderOptions"/>.</param>
    internal async Task<T> SendAsync<T>(Uri endpoint, Func<HttpRequestMessage> request,
        Func<string, T> parse, string source, string target, string? text,
        RequestPriority priority = RequestPriority.Interactive, CancellationToken ct = default)
    {
        // §5.4 AC 1 — the gate is consulted BEFORE anything is sent, and this is the whole epic:
        // on Open nothing leaves the machine. One admission covers the logical call, retries
        // included, because the retry is bounded at one and both Kinds it fires on (Unavailable,
        // Timeout) arm the §5.6 soft cooldown — which Network and Unknown arm too, but those two
        // are not retried, so these are the only ones where it bites: re-entering per attempt
        // would let this call's own first failure refuse its own second attempt, and the retry
        // policy AC 1 asks for would be inert on arrival. The outcome is likewise reported once,
        // after — which is what TP-RET-07 pins.
        var gate = Gate;
        long probe = await AdmitAsync(gate, priority, ct).ConfigureAwait(false);

        // The line's identity is built AFTER the admission, and that ordering is the point: a
        // refused call writes nothing, and `ForRequest` MEASURES the payload (a UTF-8 byte count
        // and a line count over the whole text) plus a correlation id. An open gate is the common
        // case this epic exists to create — under a LIVE loop it is every tick — so it must cost
        // nothing at all.
        var call = RequestLog.ForRequest(_options.ProviderId, endpoint, source, target, text,
            TranslationPolicy.MaxAttempts);
        bool reported = false;

        // Every path out of a GRANTED admission reports exactly once (T4): a probe that is taken
        // and never resolved leaves the gate half-open with nothing outstanding and refuses
        // everything else for a whole window — R-01 by the back door. The loop below reports on
        // each of its own exits; this net catches the ones nobody enumerated (a request factory or
        // a parser that throws a type outside the filters, an OutOfMemory, a future edit) and
        // makes the guarantee structural rather than a property of the enumeration. `Report` is
        // idempotent on `reported`, so an exit that already reported cannot report twice.
        // A genuine cancel is the one deliberate exception: it is not an outcome §5.3 has a row
        // for, so the filter lets it past unreported and the gate's own ProbeTimeout re-arms.
        try
        {
            for (int attempt = 0; attempt < TranslationPolicy.MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // Every attempt is a request ISSUED, and burst60 counts them all — a failure log that
                // only counted failures would describe the incident and not the behaviour that caused
                // it (analyse… §2.3). This increment is the whole price of a successful request.
                int burst = RequestLog.Burst.Note(DateTimeOffset.UtcNow);
                long started = Stopwatch.GetTimestamp();

                TranslationErrorKind kind;
                DateTimeOffset? retryAt = null;
                try
                {
                    using var message = request();
                    using var resp = await _http.SendAsync(message, ct).ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                    {
                        // The body is read ONCE and shared by the §4.3 sniff, the classifier and the
                        // log. E1.S4 and E1.S5 each left this split — the sniff read the success body,
                        // the log read the error body, and neither was fed to Classify — and named
                        // this story as the place the two become one read.
                        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        // The read is inside the try on purpose: a body that never materialises is a
                        // transport failure, and mapping it as one is E1.S3's fix, not a regression.
                        ct.ThrowIfCancellationRequested();

                        // §4.3 step 4 — classify BEFORE parsing, never after. Google answers a
                        // throttled network with an HTML "Sorry…" page (benchmark… §3.1) served with a
                        // 200; parse-then-guess turns that into "an unexpected response" — the right
                        // sentence for the wrong reason, and a Kind no gate could act on.
                        if (!ProviderErrorMapper.LooksLikeHtml(resp, body))
                        {
                            // Structurally "a body or a throw": there is no local that can be null on
                            // the way out, so a future policy change cannot turn the success exit into
                            // an ArgumentNullException past the TranslationException contract.
                            T value;
                            try
                            {
                                value = parse(body);
                            }
                            catch (TranslationException ex)
                            {
                                // §5.2's last insertion point: an attempt whose body only became a
                                // failure after it was parsed. The response is still in hand, but the
                                // header set is not paid for — this WAS the success path (§10.1).
                                RequestLog.EmitStatus(call, attempt + 1, Stopwatch.GetElapsedTime(started),
                                    burst, (int)resp.StatusCode, body, _scrub);
                                Report(gate, ref reported, ex.Kind, ex.RetryAt, probe);
                                throw;
                            }

                            Report(gate, ref reported, null, null, probe);
                            return value;
                        }

                        kind = ProviderErrorMapper.Classify(resp, body, transport: null,
                            _options.KeyWasSent, ct);
                        retryAt = ProviderErrorMapper.RetryAfter(resp, gate.Now());
                        // Redaction happens at the RENDERING boundary, not upstream of the logic:
                        // §4.2 row 6 reads the envelope's own words, and a body rewritten before
                        // the classifier is a body the classifier can no longer read (a key whose
                        // letters happen to sit inside "quota" would turn a QuotaExhausted into an
                        // AuthFailed — a block only a key re-save lifts). It is also the rare
                        // branch, so the success path pays neither the copy nor the escape (a tiny
                        // footprint is a product requirement). Nothing between here and the log can
                        // render a body; the log is the one thing that can, and it gets the
                        // scrubbed one.
                        RequestLog.Emit(call, attempt + 1, Stopwatch.GetElapsedTime(started), burst,
                            resp, body, _scrub);
                        // The mapper's row 1 returns the cancel Kind whenever the token is
                        // cancelled, and the token can be cancelled by another thread in the window
                        // between the check above and Classify. That Kind may reach neither a gate
                        // (its §5.3 reaction is Ignore, which returns before releasing a probe) nor
                        // a TranslationException (TP-MAP-17 bans it outright). Re-checking here
                        // turns that window into what it actually is: a genuine cancel, which the
                        // filtered catch below rethrows untouched.
                        ct.ThrowIfCancellationRequested();
                        Report(gate, ref reported, kind, retryAt, probe);
                        throw new TranslationException(kind, _options.StatusMessage((int)resp.StatusCode),
                            retryAt, _options.ProviderId);
                    }

                    // The error body: read through SafeBodyAsync, which swallows its own errors — a
                    // diagnostic must never break the failure it is describing (Logging.cs:89's rule).
                    // It also swallows a genuine cancel, so the token is re-checked immediately after,
                    // exactly as every branch of the old loop did.
                    var errorBody = await RequestLog.SafeBodyAsync(resp, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    // Raw to the classifier, scrubbed to the log — see the note on the branch above.
                    kind = ProviderErrorMapper.Classify(resp, errorBody, transport: null,
                        _options.KeyWasSent, ct);
                    retryAt = ProviderErrorMapper.RetryAfter(resp, gate.Now());

                    // One line per non-success attempt, emitted BEFORE the throw decisions (§5.2), so a
                    // retried failure leaves its attempts under one cid with their real spacing.
                    RequestLog.Emit(call, attempt + 1, Stopwatch.GetElapsedTime(started), burst,
                        resp, errorBody, _scrub);

                    if (Retryable(kind) && attempt + 1 < TranslationPolicy.MaxAttempts)
                    {
                        await BackoffAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }

                    // The cancel-Kind window again (see above): re-checked before the Kind can
                    // reach the gate or a TranslationException.
                    ct.ThrowIfCancellationRequested();
                    Report(gate, ref reported, kind, retryAt, probe);
                    throw new TranslationException(kind, _options.StatusMessage((int)resp.StatusCode),
                        retryAt, _options.ProviderId);
                }
                // I3, and this is the one place it now lives. An HttpClient timeout is a
                // TaskCanceledException whose token is NOT cancelled; unfiltered, it masquerades as a
                // user cancel — the bug that silently disabled the DeepL→Google fallback and left a
                // zombie LIVE indicator, and that cost this project three releases. This filter takes
                // every genuine cancel first, so the catch below can only see a failure.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                                  or IOException)
                {
                    // The attempt really happened, so it gets its line first: a transport failure is
                    // evidence whatever the caller does next. The status field is the exception TYPE —
                    // what discriminates "no Internet" from "the request timed out" without asking the
                    // user (§5.2). The exception's MESSAGE is never logged: it can quote the URL.
                    RequestLog.Emit(call, attempt + 1, Stopwatch.GetElapsedTime(started), burst, ex, _scrub);
                    ct.ThrowIfCancellationRequested();

                    kind = KindOf(ex, ct);
                    if (Retryable(kind) && attempt + 1 < TranslationPolicy.MaxAttempts)
                    {
                        await BackoffAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }

                    // …and the same window here: KindOf goes through the mapper too, so a cancel
                    // that lands between the check above and it would come back as the one Kind
                    // that may reach neither a gate nor a TranslationException.
                    ct.ThrowIfCancellationRequested();
                    Report(gate, ref reported, kind, null, probe);
                    throw new TranslationException(kind, _options.TransportMessage(kind), null,
                        _options.ProviderId);
                }
            }
        }
        // The net described above. A genuine cancel is filtered out and travels on unreported.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Report(gate, ref reported, TranslationErrorKind.Unknown, null, probe);
            throw;
        }

        // Unreachable: every attempt returns, continues or throws, and the last one cannot
        // continue. Stated as a throw rather than as a `!` so a future policy change is a failed
        // request and not a null reference (E1.S3's `json!` item, closed structurally).
        Report(gate, ref reported, TranslationErrorKind.Unknown, null, probe);
        throw new TranslationException(TranslationErrorKind.Unknown,
            _options.TransportMessage(TranslationErrorKind.Unknown), null, _options.ProviderId);
    }

    // ---- admission ---------------------------------------------------------------------------

    /// <summary>
    /// §5's four answers. <c>Allow</c> and <c>Probe</c> send; <c>Open</c> raises without a request —
    /// which is the whole point of Epic 2 — and <c>Wait</c> is honoured through the injectable delay
    /// while it is worth waiting (<see cref="GateDecision.WorthWaiting"/>) and raises otherwise.
    ///
    /// <para>The wait is bounded rather than a loop on a condition: the ceiling is a token bucket
    /// and another caller may take the token this one just waited for, and a request path that can
    /// spin is worse than a refusal. <b>E3.S3</b> owns the chain's own version of this rule
    /// (§5.4's <c>MaxSpacingWaitMs</c>, "move on to the next tier"); until it exists, raising is the
    /// honest answer.</para>
    /// </summary>
    /// <returns>The probe token to quote when reporting — 0 for an ordinary admission (ruling
    /// E2-h).</returns>
    private async Task<long> AdmitAsync(ProviderGate gate, RequestPriority priority,
        CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        for (int wait = 0; ; wait++)
        {
            var decision = gate.TryEnter(priority);
            switch (decision.Outcome)
            {
                case GateOutcome.Allow:
                    return 0;
                case GateOutcome.Probe:
                    return decision.ProbeToken;
                case GateOutcome.Open:
                    // No request, and the Kind is the gate's own last one so the player reads the
                    // sentence that pause is about (E1.S6's table, rendered by MainWindow.Friendly).
                    throw Paused(gate, decision.RetryAt);
                default:
                    // Two bounds, and the second one is the policy's own. `WorthWaiting` asks
                    // whether ONE wait is short enough; `MaxSpacingWaitMs` is defined as how long a
                    // caller may sit on a wait ALTOGETHER before the tier counts as unavailable, so
                    // two waits of 2 s each would spend 4 s of a 2 s budget. The running total is
                    // what the policy actually says.
                    //
                    // The gate's own clock, never the wall clock: the RetryAt this hands the caller
                    // is compared against the instants the gate itself produces (IS-6), and two
                    // clocks that can disagree is what `ProviderGate.Now()` exists to prevent.
                    if (wait >= MaxWaits
                        || !GateDecision.WorthWaiting(decision.Delay)
                        || !GateDecision.WorthWaiting(waited + decision.Delay))
                        throw Paused(gate, gate.Now() + decision.Delay);
                    waited += decision.Delay;
                    await Delay(decision.Delay, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>How many times one call may be sent back to the bucket before it is refused. Two
    /// is a token's worth of patience plus one for a lost race; more would be a scheduler, which
    /// §5.4 explicitly refuses to build. The total time is bounded separately, by the policy's own
    /// <c>MaxSpacingWaitMs</c>.</summary>
    private const int MaxWaits = 2;

    private TranslationException Paused(ProviderGate gate, DateTimeOffset retryAt)
    {
        // Not a new sentence: the Kind is what the copy deck is keyed on, and a paused provider
        // says what it was paused FOR. RateLimited is the fallback because it is what an open gate
        // means in the absence of anything else — a provider that asked us to slow down. The
        // snapshot's Kind is safe to hand to a TranslationException by construction: the gate
        // stores a Kind only for the rows §5.3 reacts to, and the row this file may not name is
        // not one of them (I3).
        var kind = gate.Snapshot().LastKind ?? TranslationErrorKind.RateLimited;
        return new TranslationException(kind, _options.PausedMessage, retryAt, _options.ProviderId);
    }

    /// <summary>One report per admission, and exactly one: a granted probe that never reports
    /// leaves the gate half-open with nothing outstanding, and nothing else can enter for a whole
    /// window (R-01 by the back door). <c>AllProvidersPaused</c> is the chain's own outcome (§5.3)
    /// and is never reported to a gate — guarded here as well as in the gate itself.</summary>
    private static void Report(ProviderGate gate, ref bool reported, TranslationErrorKind? kind,
        DateTimeOffset? retryAt, long probe)
    {
        if (reported) return;
        reported = true;
        if (kind is null) gate.ReportSuccess(probe);
        else if (kind != TranslationErrorKind.AllProvidersPaused)
            // The raw parsed Retry-After: the clamp to [1 s, OpenCapMinutes] is the gate's and is
            // applied once (E2.S1 / ruling E2-f). Two clamps would double-apply the floor.
            gate.ReportFailure(kind.Value, retryAt, probe);
    }

    // ---- the retry policy (§4.3 / §5.6) --------------------------------------------------------

    /// <summary>Only a failure a second attempt could plausibly survive. A <c>RateLimited</c> or a
    /// <c>Blocked</c> raises on attempt 1 — <b>the gate owns the wait</b>, and a second request into
    /// a hard block triples the abuse signal for no benefit (benchmark… §11.4 item 3). A
    /// <c>Network</c> failure is no longer retried either: the chain (E3.S3) provides the redundancy
    /// the retry used to fake.</summary>
    private static bool Retryable(TranslationErrorKind kind) =>
        kind is TranslationErrorKind.Unavailable or TranslationErrorKind.Timeout;

    /// <summary>Full jitter, <c>Random(0, BackoffBaseMs &lt;&lt; attempt)</c> — so two instances
    /// behind one NAT stop retrying in lockstep, which fixed spacing guarantees they do.</summary>
    internal static TimeSpan Backoff(int attempt)
    {
        int ceiling = TranslationPolicy.BackoffBaseMs << attempt;
        var draw = JitterOverride ?? Random.Shared.Next;
        return TimeSpan.FromMilliseconds(Math.Clamp(draw(ceiling), 0, ceiling));
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct) => Delay(Backoff(attempt), ct);

    private static Task Delay(TimeSpan delay, CancellationToken ct)
    {
        var f = DelayOverride;
        return f != null ? f(delay, ct) : Task.Delay(delay, ct);
    }

    // ---- classification helpers -----------------------------------------------------------------

    /// <summary>The transport half of §4.2, plus the row E1.S3 deferred to this story:
    /// <see cref="IOException"/> (and <c>HttpIOException</c> with it) is in neither provider's
    /// filter today and would escape raw. It is a transport failure that is <b>not</b> a timeout,
    /// so it is a <c>Network</c> — and it gets the <c>Network</c> sentence, not the timeout one the
    /// old ternary would have given it.</summary>
    private TranslationErrorKind KindOf(Exception ex, CancellationToken ct) =>
        // IOException and HttpRequestException are unrelated types, so this arm cannot swallow one
        // the mapper already has a row for.
        ex is IOException
            ? TranslationErrorKind.Network
            : ProviderErrorMapper.Classify(resp: null, bodyHead: null, transport: ex,
                _options.KeyWasSent, ct);

    // ---- I11: the key, scrubbed before anything can render it -----------------------------------

    /// <summary>What a redacted secret leaves behind. Short, printable and unmistakable.</summary>
    internal const string Redacted = "[redacted]";

    /// <summary>
    /// The new I11 surface this story opens: the first keyed provider goes through the shared
    /// emitter, and <see cref="RequestLog"/> is UI-free — it cannot know a key's VALUE, only the
    /// parameter names it stops at (<c>RequestLog.cs:272</c>). The core does know it, so it scrubs
    /// the key and its URL-encoded form out of every body head <b>on the way into the log</b> — the
    /// one thing in this file that can render one.
    /// <para>At that boundary and deliberately not upstream of it: the §4.3 sniff, the parser and
    /// the classifier read the body VERBATIM, because §4.2 row 6 classifies on the envelope's own
    /// words and a body rewritten before it is read is a body the classifier can no longer read.
    /// It is also what keeps the success path free — scrubbing walks the whole body and allocates
    /// a copy of it, and a translation that worked must cost nothing but the burst counter.</para>
    /// <para>The adversarial case this is for is a page that quotes the key with no
    /// <c>auth_key=</c> in front of it, which the marker cut cannot see.</para>
    /// </summary>
    internal string? Redact(string? body) => Redact(body, _options.Secret);

    /// <summary>
    /// Three forms of the same secret, because the rendering pipeline can change its shape between
    /// the wire and the line:
    /// <list type="number">
    /// <item>the literal;</item>
    /// <item>its URL-encoded form — a page that echoes the query it refused carries it escaped;</item>
    /// <item>the literal with <b>whitespace anywhere inside it</b>. That last one is not paranoia:
    /// <c>ProviderErrorMapper.DeTaggedHead</c> replaces every tag with a space, so a page rendering
    /// <c>KEY-&lt;b&gt;PART&lt;/b&gt;-2</c> reaches the line as <c>KEY- PART -2</c> — a literal
    /// match misses it, and the story's own acceptance step ("grep the log for the key's first
    /// eight characters and get nothing") fails on it.</item>
    /// </list>
    /// <para>The whitespace-tolerant pass needs a secret long enough that tolerance cannot become
    /// noise, so it is gated on <see cref="MinTolerantSecretChars"/>. Below that the literal pass
    /// still runs — a short secret is never left unscrubbed, it is only matched exactly.</para>
    /// </summary>
    internal static string? Redact(string? body, string? secret)
    {
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(secret)) return body;

        var scrubbed = body!.Replace(secret!, Redacted, StringComparison.Ordinal);
        var encoded = Uri.EscapeDataString(secret!);
        if (!string.Equals(encoded, secret, StringComparison.Ordinal))
            scrubbed = scrubbed.Replace(encoded, Redacted, StringComparison.Ordinal);
        return ScrubSpaced(scrubbed, secret!);
    }

    /// <summary>Below this many non-whitespace characters a secret is matched literally and nothing
    /// else: tolerating gaps inside a three-character "key" would redact prose. Every real
    /// credential this app can hold is far longer (a DeepL key is 36+ characters).</summary>
    private const int MinTolerantSecretChars = 8;

    /// <summary>
    /// One left-to-right pass that matches the secret while skipping whitespace on both sides, and
    /// replaces the whole matched span. Hand-rolled rather than a regex because the pattern is a
    /// runtime value: a per-character <c>\s*</c> pattern built from a user-supplied key is a
    /// backtracking surface on the request path, and this is a bounded O(line × secret) scan over a
    /// line of a few hundred characters, on the failure path only.
    /// </summary>
    private static string ScrubSpaced(string text, string secret)
    {
        var needle = secret.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (needle.Length < MinTolerantSecretChars) return text;

        StringBuilder? sb = null;
        int copied = 0;
        for (int start = 0; start < text.Length; start++)
        {
            if (char.IsWhiteSpace(text[start])) continue;

            int i = start, n = 0;
            while (i < text.Length && n < needle.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (text[i] != needle[n]) break;
                i++; n++;
            }
            if (n < needle.Length) continue;

            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, start - copied).Append(Redacted);
            copied = i;
            start = i - 1;                  // the loop's own ++ resumes just past the match
        }

        if (sb == null) return text;
        return sb.Append(text, copied, text.Length - copied).ToString();
    }
}

/// <summary>
/// What one provider tells the core about itself. Everything here is provider-specific by
/// definition; everything the core does with it is not.
/// </summary>
/// <param name="ProviderId">A <see cref="ProviderIds"/> constant — never a second spelling, which
/// would be a silently duplicated gate rather than a typo that fails loudly.</param>
/// <param name="KeyWasSent">The flag that makes a 403 a <c>Blocked</c> on a keyless endpoint and an
/// <c>AuthFailed</c> on a keyed one (§4.2 rows 5–8).</param>
/// <param name="StatusMessage">The provider's account of an HTTP status, for the log and for the
/// <see cref="TranslationException"/>. What the PLAYER reads is the Kind's sentence from
/// <see cref="UserMessages"/> (E1.S6); the wording here is E7.S1's to fold in.</param>
/// <param name="TransportMessage">The same, for a failure that never got a status.</param>
/// <param name="PausedMessage">The account of a request the gate refused before it was made.</param>
/// <param name="UserAgent">Google's frozen Chrome string, or <c>null</c> — DeepL has never sent
/// one and must not start (see <see cref="HttpProviderCore.CreateClient"/>).</param>
/// <param name="Secret">The configured key, so the core can scrub it out of a body head (I11).</param>
internal sealed record ProviderOptions(
    string ProviderId,
    bool KeyWasSent,
    Func<int, string> StatusMessage,
    Func<TranslationErrorKind, string> TransportMessage,
    string PausedMessage,
    string? UserAgent = null,
    string? Secret = null)
{
    /// <summary>A record synthesises <c>ToString()</c> over every positional member, and one of
    /// them is the API key. One interpolation of this object into a log line, an exception message
    /// or a debugger dump pasted into an issue would put the key in the report the user sends to
    /// Discord — the exact thing I11 exists to prevent, in the one type that holds the secret.
    /// Overridden so that cannot happen by accident.</summary>
    public override string ToString() => $"ProviderOptions {{ ProviderId = {ProviderId} }}";
}
