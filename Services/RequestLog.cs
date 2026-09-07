using System.Net.Http;
using System.Text;

namespace PWRUHelper.Services;

/// <summary>
/// The per-request diagnostic line of <c>architecture-cible.md</c> §10.1, built here and written
/// through <see cref="Logging"/>.
///
/// It exists because <c>GoogleGtxTranslator</c> logs <b>nothing</b> today, so the About tab's "Copy
/// error report" (<c>MainWindow.xaml.cs:312-322</c>) is empty for exactly the failure players
/// report. One line per <b>non-success or exceptional</b> attempt turns the next incident into a
/// measurement instead of an argument; successes are counted into <c>burst60</c> and never logged,
/// because the log is capped at 1 MB with one rollover (<c>Logging.cs:69,95-105</c>) and a chatty
/// success path would evict the evidence.
///
/// <para><b>I11 is the whole point.</b> That report is pasted to Discord <i>by design</i>, so the
/// user's text, the <c>q=</c> that carries it, the full URL and any API key must be unable to reach
/// this file — not "must be remembered to be left out". Three structural choices do that, and they
/// are worth more than the assertions that pin them:</para>
/// <list type="number">
/// <item>the endpoint travels as a <see cref="Uri"/> and <see cref="Endpoint"/> renders host + path,
///       so no call site can leak a query by handing over the wrong string;</item>
/// <item><see cref="Call"/> is built from the request text but keeps only its SIZE, so the text is
///       not in the record the retry loop carries;</item>
/// <item><see cref="BodyHead"/> is the only door a body can walk through, and it takes markup only.</item>
/// </list>
///
/// <para>Written as functions rather than as methods on a provider on purpose: <b>E2.S5</b> moves
/// the emission into <c>HttpProviderCore</c>, where it becomes a call-site change and not a
/// rewrite.</para>
/// </summary>
internal static class RequestLog
{
    /// <summary>§10.1 — the de-tagged body head's hard cap.</summary>
    internal const int MaxBodyChars = 120;

    /// <summary>One header value's share of the line. §10.1's whole line is ~300 characters and a
    /// server is free to answer with a paragraph, so every value the SERVER controls is bounded.</summary>
    private const int MaxHeaderChars = 40;

    /// <summary>The trailing window <c>burst60</c> counts over.</summary>
    internal const int BurstWindowSeconds = 60;

    /// <summary>
    /// The address family actually used (<c>mecanismes…</c> Q4: rate limiters bucket IPv6 by
    /// prefix, so a dual-stack machine silently switching families looks like a block that "cleared
    /// itself"). <b>Not obtainable on this path today</b> — <see cref="HttpClient"/> does not expose
    /// the socket, and the only cheap way in is a <c>ConnectCallback</c> on the production handler,
    /// which <b>E2.S5's review deliberately refused to install</b> (Winston's ruling): a connect
    /// callback replaces the runtime's own connect path — DNS, dual-stack Happy Eyeballs, proxy
    /// tunnelling, connect-timeout semantics — on a tool that runs on arbitrary home and corporate
    /// networks with no way to diagnose one remotely, and no diagnostic field is worth that. .NET 8
    /// exposes the peer address on no other public per-request API, so this is what every line
    /// says, and §10.1's own instruction for that case is to log <c>?</c> rather than to guess.
    /// </summary>
    internal const string UnknownAddressFamily = "?";

    /// <summary>The value every absent field renders as, so a reader can tell "the server said
    /// nothing" from "the field was dropped".</summary>
    private const string Nothing = "-";

    /// <summary>The rolling request count shared by the whole process. Incremented by <b>every</b>
    /// request issued, successes included — that is what makes the number comparable with
    /// <c>analyse…</c> §2.3's volume model.</summary>
    internal static readonly BurstCounter Burst = new();

    private static int _sequence;

    /// <summary>A short id shared by every attempt of one logical call, so one call reads as one
    /// event. A counter rather than a Guid: it is cheaper, it is monotonic (which makes two
    /// interleaved calls readable in the file), and 16 bits is plenty inside one log.</summary>
    internal static string NewCorrelationId() =>
        (Interlocked.Increment(ref _sequence) & 0xFFFF).ToString("x4");

    internal static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int lines = 1;
        foreach (var c in text!) if (c == '\n') lines++;
        return lines;
    }

    // ---- the logical call ----------------------------------------------------------------------

    /// <summary>
    /// Everything about one logical call that does not change between its attempts — and, just as
    /// much, everything that must NOT survive into the retry loop. It is built from the request
    /// <see cref="Uri"/> and the request text, and it keeps neither: the address is already reduced
    /// to host + path, and the text to its <b>size</b> and <b>line count</b> (§5.2's "payload shape:
    /// request byte count and line count only"). So the query that carries the user's sentence and
    /// the sentence itself are not members of the record the line is rendered from, and no later
    /// change to that rendering can start printing them (I11).
    /// </summary>
    internal readonly record struct Call(string Provider, string Endpoint, string Source, string Target,
        string Cid, int MaxAttempts, int RequestBytes, int RequestLines);

    internal static Call ForRequest(string provider, Uri? endpoint, string source, string target,
        string? text, int maxAttempts) =>
        new(provider, Endpoint(endpoint), source, target, NewCorrelationId(), maxAttempts,
            text == null ? 0 : Encoding.UTF8.GetByteCount(text), CountLines(text));

    // ---- what one attempt produced --------------------------------------------------------------

    /// <summary>
    /// The response half of the line, captured on the <b>failure path only</b>: building it walks
    /// the header collection, and §10.1 says a success may cost nothing but the counter increment
    /// (a tiny footprint is a product requirement). Every factory swallows its own errors — a
    /// malformed header is a diagnostic problem, never a translation problem.
    /// </summary>
    internal readonly record struct ResponseFacts(string Status, string RetryAfter, string ContentType,
        string Len, string Hdrs, string? Body)
    {
        private static readonly string NoHeaders =
            $"via:{Nothing} srv:{Nothing} xrl:{Nothing} set-cookie:no";

        /// <summary>A real response, plus the body that was read from it (may be null: the caller
        /// is free not to read one).</summary>
        internal static ResponseFacts Of(HttpResponseMessage? resp, string? body)
        {
            if (resp == null) return OfStatus(0, body);
            try
            {
                return new ResponseFacts(
                    ((int)resp.StatusCode).ToString(),
                    HeaderValue(resp, "Retry-After"),
                    Ascii(resp.Content?.Headers?.ContentType?.MediaType, 40),
                    Length(resp, body),
                    Headers(resp),
                    BodyHead(body));
            }
            catch { return new ResponseFacts(((int)resp.StatusCode).ToString(), Nothing, Nothing, Nothing, NoHeaders, null); }
        }

        /// <summary>A transport failure: no response at all, so the status becomes the exception
        /// TYPE — §5.2's "discriminates E1/E2/E3/E5/E6 without asking the user". The exception's
        /// MESSAGE is never logged: on this path it can quote a URL.</summary>
        internal static ResponseFacts OfTransport(Exception? ex) =>
            new(ex == null ? Nothing : ex.GetType().Name, Nothing, Nothing, Nothing, NoHeaders, null);

        /// <summary>A failure discovered after the response object is gone — the parse failure below
        /// the retry loop. The status is the one that was kept; the header-derived fields read
        /// <c>-</c> because §10.1 forbids paying for them on the success path, which is where this
        /// response was still a success.</summary>
        internal static ResponseFacts OfStatus(int status, string? body) =>
            new(status <= 0 ? Nothing : status.ToString(), Nothing, Nothing,
                body == null ? Nothing : Encoding.UTF8.GetByteCount(body).ToString(),
                NoHeaders, BodyHead(body));

        private static string Length(HttpResponseMessage resp, string? body)
        {
            var declared = resp.Content?.Headers?.ContentLength;
            if (declared is { } n) return n.ToString();
            return body == null ? Nothing : Encoding.UTF8.GetByteCount(body).ToString();
        }

        // §5.2's "other headers": the proxy / interstitial detectors, plus a presence-only flag for
        // Set-Cookie (its value is a session token and has no place in a report).
        private static string Headers(HttpResponseMessage resp) =>
            $"via:{HeaderValue(resp, "Via")} srv:{HeaderValue(resp, "Server")} " +
            $"xrl:{RateLimitHeaders(resp)} set-cookie:{(Has(resp, "Set-Cookie") ? "yes" : "no")}";

        private static bool Has(HttpResponseMessage resp, string name)
        {
            try { return resp.Headers.NonValidated.Contains(name); } catch { return false; }
        }

        /// <summary>The header <b>as the server sent it</b>. Read through <c>NonValidated</c> on
        /// purpose: the typed accessors re-render a parsed header, so <c>Server: HTTP server
        /// (unknown)</c> comes back as three product tokens and is rebuilt as
        /// <c>HTTP,server,(unknown)</c> — a diagnostic that quietly rewrites its evidence. It also
        /// means a header this app has no parser for is still logged verbatim.</summary>
        private static string HeaderValue(HttpResponseMessage resp, string name)
        {
            try
            {
                return resp.Headers.NonValidated.TryGetValues(name, out var values)
                    ? Quoted(string.Join(",", values))
                    : Nothing;
            }
            catch { return Nothing; }
        }

        /// <summary>`X-RateLimit-*` with its NAME as well as its value: which of the family a
        /// provider sends is itself the signal, and there is no agreed spelling to assume.</summary>
        private static string RateLimitHeaders(HttpResponseMessage resp)
        {
            try
            {
                var joined = string.Join(",", resp.Headers.NonValidated
                    .Where(h => h.Key.StartsWith("x-ratelimit", StringComparison.OrdinalIgnoreCase))
                    .Take(3)
                    .Select(h => h.Key + "=" + string.Join("/", h.Value)));
                return Quoted(joined);
            }
            catch { return Nothing; }
        }
    }

    // ---- the line ------------------------------------------------------------------------------

    /// <summary>§10.1's fields, in the order AC 1 lists them. Pure: no I/O, no clock, no HTTP —
    /// everything it renders was decided by its caller.</summary>
    internal static string Line(Call call, int attempt, ResponseFacts facts, TimeSpan elapsed,
        int burst60, string addressFamily = UnknownAddressFamily)
    {
        var line = new StringBuilder(320)
            .Append("tr provider=").Append(Ascii(call.Provider, 24))
            .Append(" ep=").Append(Ascii(call.Endpoint, 80))
            .Append(" dir=").Append(Ascii(call.Source, 12)).Append("->").Append(Ascii(call.Target, 12))
            .Append(" attempt=").Append(attempt).Append('/').Append(call.MaxAttempts)
            .Append(" cid=").Append(Ascii(call.Cid, 8))
            .Append(" status=").Append(Ascii(facts.Status, 32))
            .Append(" elapsed=").Append((long)elapsed.TotalMilliseconds).Append("ms")
            .Append(" retry-after=").Append(Ascii(facts.RetryAfter, MaxHeaderChars))
            .Append(" ct=").Append(Ascii(facts.ContentType, MaxHeaderChars))
            .Append(" len=").Append(Ascii(facts.Len, 12))
            // Already sanitised (and already quoted where §10.1 quotes) by ResponseFacts: running
            // it through Ascii again would strip the quotes back off.
            .Append(" hdrs=[").Append(facts.Hdrs ?? $"via:{Nothing} srv:{Nothing} xrl:{Nothing} set-cookie:no").Append(']')
            .Append(" bytes=").Append(call.RequestBytes)
            .Append(" lines=").Append(call.RequestLines)
            .Append(" burst60=").Append(burst60)
            .Append(" ipv=").Append(Ascii(addressFamily, 2));

        // `body` is the last field and an OPTIONAL one: it appears for a markup body and for
        // nothing else, so its absence is itself information (the provider answered JSON).
        if (!string.IsNullOrEmpty(facts.Body))
            line.Append(" body=\"").Append(facts.Body).Append('"');

        return line.ToString();
    }

    /// <summary>Host + path, and there is no parameter that could add the rest. The query carries
    /// the user's text in <c>q=</c>, and this is the single function that turns a request's address
    /// into text (I11).</summary>
    internal static string Endpoint(Uri? uri)
    {
        if (uri == null) return Nothing;
        try { return Ascii(uri.Host + uri.AbsolutePath, 80); }
        catch { return Nothing; }
    }

    /// <summary>
    /// The one door a response body can walk through, and it is deliberately <b>narrower</b> than
    /// §4.3's <see cref="ProviderErrorMapper.LooksLikeHtml"/>: only step 2 (the body really starts
    /// with markup), never step 1 (the content-type claimed something non-JSON).
    ///
    /// <para>The reason is I11, not tidiness. The provider's own answer <b>contains the user's
    /// text</b> — the original and the translation both — so a proxy that relabels it
    /// <c>text/plain</c> would, under the wider rule, walk that text straight into a report the
    /// user pastes to Discord. Markup is the server's page and nobody's sentence. Passing
    /// <c>resp: null</c> is how the wider rule's step 1 is skipped while its tested implementation
    /// is reused.</para>
    /// </summary>
    internal static string? BodyHead(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            if (!ProviderErrorMapper.LooksLikeHtml(resp: null, body)) return null;
            var head = ProviderErrorMapper.DeTaggedHead(body, MaxBodyChars);
            head = head[..FirstEcho(head)].TrimEnd();
            if (head.Length == 0) return null;
            var safe = Ascii(head, MaxBodyChars, allowSpaces: true);
            return safe == Nothing ? null : safe;
        }
        catch { return null; }
    }

    /// <summary>The names that stand immediately in front of something a report may not carry: the
    /// parameter that holds the user's sentence, and the parameters that hold a credential. A page
    /// that quotes the request it refused quotes them by name, so the name is where the head stops.
    /// Matched case-insensitively and only at a word boundary, so prose cannot trip them.</summary>
    private static readonly string[] EchoMarkers =
    {
        "q=", "key=", "auth_key", "api_key", "apikey", "token=", "bearer ", "authorization",
        // E6.S2: Azure sends its credential in a header of its own name, and a page that quotes it
        // writes `Ocp-Apim-Subscription-Key: <key>` — a colon, not an `=`, so none of the markers
        // above sees it. Without this entry Azure would have ONE defence (the provider's own
        // `ProviderOptions.Secret` scrub) where DeepL has two, on the invariant this repo has paid
        // the most for. The name is enough on its own: nothing after it is prose worth keeping.
        "ocp-apim-subscription-key",
    };

    /// <summary>
    /// Where a body head stops being the server's prose and starts being an echo of the request.
    /// The gate above keeps the provider's JSON out; this covers the other way user text — or a key
    /// — can reach a page: an interstitial or proxy error that quotes the request it refused, which
    /// carries the sentence in <c>q=</c> (percent-escaped, or as HTML entities) and, for a keyed
    /// provider, the credential beside it. Google's own /sorry/ page does none of this, but "the
    /// page we have seen does not" is not a rule, and I11 has to hold for the page we have not seen.
    /// Cutting at the first escape (<c>%XX</c>), entity (<c>&amp;#</c>), scheme (<c>://</c>) or
    /// named parameter keeps the sentence that identifies the block and drops everything after it.
    /// </summary>
    private static int FirstEcho(string head)
    {
        for (int i = 0; i < head.Length; i++)
        {
            char c = head[i];
            if (c == '%' && i + 2 < head.Length && IsHex(head[i + 1]) && IsHex(head[i + 2])) return i;
            if (c == '&' && i + 1 < head.Length && head[i + 1] == '#') return i;
            if (c == ':' && i + 2 < head.Length && head[i + 1] == '/' && head[i + 2] == '/') return i;

            // Mid-word: `unique=` is not `q=`, and `monkey=` is not `key=`.
            if (i > 0 && char.IsLetterOrDigit(head[i - 1])) continue;
            foreach (var marker in EchoMarkers)
                if (i + marker.Length <= head.Length &&
                    string.Compare(head, i, marker, 0, marker.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
                    return i;
        }
        return head.Length;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    // ---- emission ------------------------------------------------------------------------------

    // The three doors. None of them carries an address family: E2.S5's review refused the
    // ConnectCallback that was the only way to learn one (see UnknownAddressFamily), so `ipv=`
    // renders Line's own `?` default and there is no parameter for a caller to get wrong. An
    // optional argument nobody can supply is not a seam — it is a promise the code cannot keep.
    //
    // What they DO carry is `scrub` (I11). This class is UI-free and cannot know a key's VALUE,
    // only the parameter names it stops at (EchoMarkers); a keyed provider hands its own scrubber
    // in. It is applied to the FINISHED line and to nothing earlier, which is the only placement
    // that holds: BodyHead de-tags the body AFTER any caller could have scrubbed it, so a page
    // rendering `KEY-<b>PART</b>-2` reassembles the credential inside this class, past a scrub
    // that ran on the raw body. The last thing before the write is the last chance.

    /// <summary>One line for an attempt that ended on a real response.</summary>
    internal static void Emit(Call call, int attempt, TimeSpan elapsed, int burst60,
        HttpResponseMessage resp, string? body, Func<string, string>? scrub = null) =>
        Write(call, StatusOf(resp),
            () => Scrubbed(Line(call, attempt, ResponseFacts.Of(resp, body), elapsed, burst60), scrub));

    /// <summary>One line for an attempt that ended in a transport exception.</summary>
    internal static void Emit(Call call, int attempt, TimeSpan elapsed, int burst60,
        Exception transport, Func<string, string>? scrub = null) =>
        Write(call, transport == null ? Nothing : transport.GetType().Name,
            () => Scrubbed(Line(call, attempt, ResponseFacts.OfTransport(transport), elapsed, burst60), scrub));

    /// <summary>One line for an attempt whose response is already gone — the parse failure.</summary>
    internal static void EmitStatus(Call call, int attempt, TimeSpan elapsed, int burst60,
        int status, string? body, Func<string, string>? scrub = null) =>
        Write(call, status <= 0 ? Nothing : status.ToString(),
            () => Scrubbed(Line(call, attempt, ResponseFacts.OfStatus(status, body), elapsed, burst60), scrub));

    /// <summary>A keyless provider pays nothing; a keyed one pays one ordinal scan of a line that
    /// is bounded at a few hundred characters, on the failure path only. A scrubber that throws is
    /// a diagnostic problem, never a translation problem — but it must not be allowed to write the
    /// UNSCRUBBED line either, so a failure drops the body rather than risking the secret.</summary>
    private static string Scrubbed(string line, Func<string, string>? scrub)
    {
        if (scrub == null) return line;
        try { return scrub(line); }
        catch { return Nothing; }
    }

    /// <summary>The status the suppressor keys on, read without building the rest of the line —
    /// a suppressed attempt must not pay for the line it is not going to write.</summary>
    private static string StatusOf(HttpResponseMessage? resp)
    {
        try { return resp == null ? Nothing : ((int)resp.StatusCode).ToString(); }
        catch { return Nothing; }
    }

    /// <summary>The per-provider storm valve (see <see cref="LogSuppressor"/>). Shared by the
    /// process, because the thing it protects — the 1 MB log — is shared by the process.</summary>
    internal static readonly LogSuppressor Suppression = new();

    /// <summary>A test that asserts an exact line count has to take this static the way it takes
    /// <c>Logging.DirectoryOverride</c>: the run the suppressor counts is process-wide.</summary>
    internal static void ResetSuppression() => Suppression.Reset();

    // Logging must never be the thing that breaks a feature — Logging.cs:89 keeps that property for
    // the WRITE, and this keeps it for the BUILD. A diagnostic that can cost a translation is not a
    // diagnostic.
    private static void Write(Call call, string status, Func<string> build)
    {
        try
        {
            // Provider and status are sanitised HERE and not inside the suppressor: they are the
            // two things a summary line renders, and everything a server controls has to be
            // bounded, printable, single-line ASCII before it can reach the file.
            var decision = Suppression.Note(Ascii(call.Provider, 24), Ascii(status, 32),
                DateTimeOffset.UtcNow);
            if (decision.Summary != null) Logging.Warn(decision.Summary);
            if (decision.Write) Logging.Warn(build());
        }
        catch { /* best-effort, exactly like the writer underneath it */ }
    }

    /// <summary>The body of a failed response, for the log and for nothing else. Never throws and
    /// never blocks the failure it is describing: an unreadable body simply has no <c>body=</c>.</summary>
    internal static async Task<string?> SafeBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        // ConfigureAwait(false) for the same reason the core's awaits carry it: this runs on the
        // request path, whose callers await from UI-thread methods, and Services/ must never
        // assume a dispatcher (I2).
        try { return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return null; }
    }

    // ---- rendering primitives --------------------------------------------------------------------

    /// <summary>Every value that reaches the line goes through here: bounded, printable ASCII, one
    /// line. A header carrying a newline would otherwise split one event into two, and a quote
    /// would end the <c>body="…"</c> field early.</summary>
    private static string Ascii(string? value, int maxChars, bool allowSpaces = false)
    {
        if (string.IsNullOrEmpty(value)) return Nothing;

        var text = new StringBuilder(Math.Min(value!.Length, maxChars));
        foreach (var c in value)
        {
            if (text.Length >= maxChars) break;
            if (c == '"' || c == '\\') continue;                 // would break the quoted field
            if (c == ' ' && !allowSpaces) { text.Append('_'); continue; }
            text.Append(c >= ' ' && c <= '~' ? c : '.');         // one line, ASCII-safe
        }

        var s = text.ToString().Trim();
        return s.Length == 0 ? Nothing : s;
    }

    /// <summary>A header value that may contain spaces, quoted the way §10.1 writes it
    /// (<c>srv:"HTTP server (unknown)"</c>) so the line stays trivially parseable.</summary>
    private static string Quoted(string? value)
    {
        var s = Ascii(value, MaxHeaderChars, allowSpaces: true);
        return s.Contains(' ') ? "\"" + s + "\"" : s;
    }
}

/// <summary>
/// <c>burst60</c>: how many requests were issued in the trailing window. It is what turns the log
/// into evidence for <c>analyse…</c> §2.3's volume model — "the app was making 37 requests a minute
/// when Google started refusing" is a measurement; "it felt like a lot" is not.
///
/// <para>Counted for <b>every</b> request, success or failure: counting only the failures would
/// describe the incident and not the behaviour that caused it. That makes this the one thing a
/// successful request pays for, so it is a lock and a queue of timestamps and nothing else.</para>
/// </summary>
internal sealed class BurstCounter
{
    // A hard ceiling on what a pathological minute can allocate. Reaching it means the number is
    // already saying "far too many"; losing precision above it costs nothing.
    private const int Cap = 4096;

    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _hits = new();
    private readonly TimeSpan _window;

    internal BurstCounter(int windowSeconds = RequestLog.BurstWindowSeconds) =>
        _window = TimeSpan.FromSeconds(windowSeconds);

    /// <summary>Records one issued request and returns the trailing-window count including it. The
    /// clock is the caller's (IS-6-shaped), so a test needs no wall clock.</summary>
    internal int Note(DateTimeOffset now)
    {
        lock (_gate)
        {
            // Trim FIRST. A queue still holding a pathological minute of hits that have since left
            // the window would otherwise refuse this one at the cap and then report 0 for it — the
            // first request after a storm reading as "no traffic at all" is the opposite of true.
            Trimmed(now);
            if (_hits.Count < Cap) _hits.Enqueue(now);
            return _hits.Count;
        }
    }

    /// <summary>The count without recording anything.</summary>
    internal int Count(DateTimeOffset now)
    {
        lock (_gate) return Trimmed(now);
    }

    private int Trimmed(DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (_hits.Count > 0 && _hits.Peek() < cutoff) _hits.Dequeue();
        return _hits.Count;
    }
}

/// <summary>
/// The storm valve. One failing attempt writes one line, which is the point — but a LIVE loop
/// meeting a hard 429 fails <b>every</b> request, and at LIVE rates three lines per logical call
/// is a few hundred lines a minute. The log is capped at 1 MB with one rollover
/// (<c>Logging.cs:69,95-105</c>), so a storm that ran for a few minutes would roll away the
/// beginning of the incident — the part that says what the app was doing when the block arrived,
/// which is the only part nobody can reconstruct afterwards.
///
/// <para>So: the first <see cref="Threshold"/> consecutive lines with the same
/// <c>provider</c> + <c>status</c> are written in full, and after that the run goes quiet and is
/// represented by <b>one</b> summary line per window carrying the count. Anything that changes —
/// a different status, a different provider, the storm ending — ends the run and flushes what it
/// held, so no suppressed line is ever silently lost.</para>
///
/// <para>The clock is the caller's, exactly like <see cref="BurstCounter"/>'s: a test needs no
/// wall clock, and E2's injected clock can drive this when the emission moves into
/// <c>HttpProviderCore</c>.</para>
/// </summary>
internal sealed class LogSuppressor
{
    /// <summary>How many identical lines are worth writing before the run stops being evidence and
    /// starts being noise. Three attempts of one logical call plus a couple of repeats still read
    /// in full; a loop hammering a closed door does not.</summary>
    internal const int Threshold = 10;

    /// <summary>What the caller should do with this line, and the summary line (if any) that has to
    /// be written before it.</summary>
    internal readonly record struct Decision(bool Write, string? Summary);

    private readonly object _gate = new();
    private readonly TimeSpan _window;
    private readonly int _threshold;
    private readonly string _prefix;
    private readonly string _separator;

    private string? _signature;     // provider + status of the run in progress
    private int _run;               // how many lines that run has seen, written or not
    private int _held;              // how many of them were not written
    private DateTimeOffset _since;  // when the current summary window opened

    /// <param name="prefix">The family the summary line belongs to — <c>"tr "</c> for E1.S5's
    /// per-request lines, <c>"gate "</c> for E2.S6's transition lines. A summary that announced
    /// itself as another family's line would be a report that reads as two streams.</param>
    /// <param name="separator">What sits between the provider and the second half of the key, so
    /// the summary is spelled in the grammar of the lines it replaces: <c>" status="</c> for a
    /// request line, a plain space for a gate line, whose second half is already an edge
    /// (<c>OPEN-&gt;HALF-OPEN</c>).</param>
    internal LogSuppressor(int threshold = Threshold,
        int windowSeconds = RequestLog.BurstWindowSeconds,
        string prefix = "tr ", string separator = " status=")
    {
        _threshold = threshold;
        _window = TimeSpan.FromSeconds(windowSeconds);
        _prefix = prefix;
        _separator = separator;
    }

    /// <summary>Records one line about to be emitted and answers whether to write it.</summary>
    internal Decision Note(string provider, string status, DateTimeOffset now)
    {
        var signature = provider + _separator + status;
        lock (_gate)
        {
            if (signature != _signature)
            {
                // A different failure is new information: end the old run, report what it held.
                var flushed = Flush(now);
                _signature = signature;
                _run = 1;
                _held = 0;
                _since = now;
                return new Decision(true, flushed);
            }

            if (++_run <= _threshold) return new Decision(true, null);

            _held++;
            return now - _since >= _window
                ? new Decision(false, Flush(now))
                : new Decision(false, null);
        }
    }

    /// <summary>Forgets the run in progress. For tests that assert an exact line count — the run is
    /// process-wide, so a class that counts lines has to start from a known state.</summary>
    internal void Reset()
    {
        lock (_gate) { _signature = null; _run = 0; _held = 0; }
    }

    /// <summary>The line that stands in for the ones that were not written. Same <c>tr </c> family
    /// and the same field names as the lines it replaces, so the report is still one stream — and
    /// it carries strictly less than they did.</summary>
    private string? Flush(DateTimeOffset now)
    {
        if (_held == 0) return null;
        var seconds = (long)Math.Max(0, (now - _since).TotalSeconds);
        var line = $"{_prefix}provider={_signature} suppressed={_held} in={seconds}s " +
                   "(identical lines not written)";
        _held = 0;
        _since = now;
        return line;
    }
}
