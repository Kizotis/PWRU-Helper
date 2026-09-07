using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;

namespace PWRUHelper.Services;

/// <summary>
/// Translates through <c>clients5.google.com/translate_a/t?client=dict-chrome-ex</c> — the endpoint
/// Chrome's own dictionary extension uses. No key, no cost, and — this is why it exists — 20/20
/// HTTP 200 at ~2.5 req/s on the exact network where <see cref="GoogleGtxTranslator"/> was answered
/// with a 429 HTML page (<c>benchmark-fournisseurs.md</c> §3.2, own probe 2026-09-06).
///
/// <para>It is "a URL, a payload and a parser" and nothing else: admission, the ≤ 2 attempts with
/// full jitter, the one body read, the §4.3 HTML sniff, the classification, <c>Retry-After</c>, the
/// §10.1 line and the outcome report all belong to <see cref="HttpProviderCore"/> (§7.0). A provider
/// that classified its own statuses would be the bug §4.2 exists to prevent.</para>
///
/// <para><b>Not wired into any chain here.</b> Composition is E3.S7's; this story adds the provider
/// and its tests, so that the switch and the wiring can be read — and reverted — separately.</para>
///
/// <para><b>The duplication is deliberate, and it is recorded rather than resolved.</b>
/// <c>TranslateLinesAsync</c>'s per-line loop and <c>SafeOne</c> are all but verbatim
/// <see cref="GoogleGtxTranslator"/>'s (~45 lines), and so is the byte-budget chunking above them.
/// That is TWO copies: the Rule of Three says the shared "batch semantics" helper is extracted when
/// a third provider needs it — which is E3.S5's Edge tier, if U2 ever lands — and E3.S8 is the story
/// that owns this loop anyway (<c>PerLineCap</c> bounds exactly this fan-out). Extracting it here
/// would mean rewriting the shipping provider's loop in the same commit that introduces a new
/// endpoint, which is two risks in one diff. <b>E3.S8 extraction candidate, named here so nobody has
/// to rediscover it.</b></para>
///
/// <para><b>ToS posture, stated once.</b> <c>clients5.google.com/robots.txt</c> carries no
/// <c>Disallow: /translate_a/</c>, unlike <c>translate.googleapis.com</c> line 162 — strictly better
/// than today, still not clean. R1 (an undocumented, rented endpoint that could be blocked next) is
/// this provider's own risk; the mitigations are structural and already built — three free tiers
/// from two vendors, a gate that fails legibly (E2), and <see cref="ClientId"/> as a const in one
/// place, one edit from a change.</para>
/// </summary>
public class GoogleDictTranslator : ITranslator
{
    /// <summary>How this endpoint names itself in the log and, above all, WHICH GATE it consults.
    /// Not spelled here: a second spelling of an id is not a typo that fails loudly — it is a
    /// silently duplicated gate (<see cref="ProviderIds"/>).</summary>
    private const string ProviderId = ProviderIds.GoogleDict;

    /// <summary>The client the endpoint is addressed as. In <b>one</b> place on purpose (R1's own
    /// mitigation): if this id is ever burned, the replacement is one edit here and not a search
    /// through interpolated URL strings.</summary>
    private const string ClientId = "dict-chrome-ex";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Byte-identical to <see cref="GoogleGtxTranslator"/>'s frozen string, and §7.0 says
    /// keep it and do not rotate it: <c>benchmark…</c> §3.1 measured the 429 <i>with and without</i>
    /// it (so it is not what saves us), while <c>mecanismes-de-blocage-google.md</c> Q2 records that
    /// an absent or <c>curl</c>-style UA does earn a 403 (so dropping it costs). The two copies are
    /// pinned equal by a test rather than shared through a constant: the UA is a provider OPTION —
    /// DeepL must never grow one — and the day one Google endpoint needs a different string, a
    /// shared const is a change to both.</summary>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private readonly HttpClient _http;

    private readonly HttpProviderCore _core;

    /// <summary>§5.4's reserve, per INSTANCE. <c>ITranslator</c> has no channel for a priority (I1)
    /// and E3.S7 needs the LIVE read chain to say <c>Background</c> while the Translator tab says
    /// <c>Interactive</c> — so the priority belongs to the provider object, and the two instances
    /// that answer to those two chains share <b>one</b> gate (I9), which is exactly right: the
    /// external condition a gate mirrors is per endpoint, not per chain. Defaults to
    /// <c>Interactive</c>, so nothing changes until E3.S7 asks.</summary>
    private readonly RequestPriority _priority;

    public GoogleDictTranslator() : this((HttpMessageHandler?)null) { }

    /// <summary>Test seam (IS-8/IS-10): a handler builds a private client — configured by the same
    /// factory as the shared one, so a test sees the same timeout and the same User-Agent — and the
    /// retry policy and the parser become reachable offline. The app passes nothing and keeps the
    /// shared static client. <paramref name="gate"/> is the same idea for E2's registry.</summary>
    internal GoogleDictTranslator(HttpMessageHandler? handler = null, ProviderGate? gate = null,
        RequestPriority priority = RequestPriority.Interactive)
    {
        _http = handler == null ? Http : CreateClient(handler);
        _core = new HttpProviderCore(Options, _http, gate);
        _priority = priority;
    }

    /// <summary>What this provider tells the core about itself (§7.0). <c>KeyWasSent: false</c> is
    /// what makes a 403 a <c>Blocked</c> and never an <c>AuthFailed</c> (§4.2 rows 5–8, ruling
    /// E2-g) — there is no key on this path at all. The sentences are the LOG's account of what the
    /// endpoint said; what the player reads is the Kind's sentence from <see cref="UserMessages"/>,
    /// and folding the two together is E7.S1's.</summary>
    private static readonly ProviderOptions Options = new(
        ProviderId,
        KeyWasSent: false,          // the keyless endpoint: its 403 is a block, never a rejected key
        StatusMessage: code => code switch
        {
            // A 2xx that reached here is the §4.3 abuse page served with a success status.
            >= 200 and < 300 => "The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.",
            429 => "Google is limiting translations right now — wait a minute and try again.",
            >= 500 => $"Translation service is unavailable (HTTP {code}). Try again shortly.",
            _ => $"Translation service error (HTTP {code}). Please try again later.",
        },
        TransportMessage: kind => kind == TranslationErrorKind.Timeout
            ? "the request timed out"
            : "Couldn't reach the translation service. Check your Internet connection.",
        PausedMessage: "The translation service is paused after a recent refusal.",
        UserAgent: UserAgent);

    private static HttpClient CreateClient(HttpMessageHandler? handler = null) =>
        HttpProviderCore.CreateClient(handler, UserAgent);

    /// <summary>
    /// Translate one piece of text. Language codes are ISO ("en", "ru"); "auto" detects the source,
    /// and the detected language that comes back with it is DISCARDED (I7 — the source is the
    /// caller's choice, per message). Text longer than the query budget is split by
    /// <see cref="TextChunker"/> and stitched back.
    /// </summary>
    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";

        if (Encoding.UTF8.GetByteCount(text) <= TranslationPolicy.MaxQueryBytes)
            return await RequestAsync(text, source, target, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        foreach (var chunk in TextChunker.ChunkText(text, TranslationPolicy.MaxQueryBytes))
            sb.Append(await RequestAsync(chunk, source, target, ct).ConfigureAwait(false));
        return sb.ToString();
    }

    /// <summary>
    /// Translate several lines — <b>one request per line</b>, which is OQ-A's settled answer and not
    /// a placeholder: this endpoint returns ONE string rather than gtx's segments, and whether a
    /// <c>\n</c>-joined <c>q</c> comes back with its newlines intact is [UNKNOWN] (U1). Until a
    /// capture says otherwise, joining would be guessing with the user's text.
    ///
    /// <para>The join path below is written, and dormant, behind
    /// <see cref="TranslationPolicy.GoogleDictBatchJoinEnabled"/>: E3.S1's fixture flips that one
    /// value and TP-PRV-04 turns from the negative pin ("3 lines cost 3 requests") into the positive
    /// one. Multi-<c>q=</c> is NOT the fallback — it is a declined non-feature
    /// (<c>project-context.md</c>), re-confirmed by the owner's OQ-A answer, and
    /// <c>benchmark…</c> §3.2 declines to propose it despite having measured it.</para>
    ///
    /// <para>Returns a list the same length as <paramref name="lines"/>; a line that could not be
    /// translated comes back as a "(…)" placeholder, which <see cref="CachingTranslator"/> never
    /// stores (I4). The fan-out is bounded three ways and each is somebody's: <c>PerLineCap</c>
    /// (E3.S8, not yet), the §5.4 rate ceiling (E2.S3, already pacing it) and the gate (E2.S1).</para>
    /// </summary>
    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines,
        string source, string target, CancellationToken ct = default)
    {
        if (lines.Count == 0) return new List<string>();
        if (lines.Count == 1)
            return new List<string> { await SafeOne(lines[0]).ConfigureAwait(false) };

        if (TranslationPolicy.GoogleDictBatchJoinEnabled)
        {
            var joined = string.Join("\n", lines);
            if (Encoding.UTF8.GetByteCount(joined) <= TranslationPolicy.MaxQueryBytes)
            {
                try
                {
                    var full = await RequestAsync(joined, source, target, ct).ConfigureAwait(false);
                    var parts = full.Split('\n');
                    // I5: a count mismatch falls through to per-line. It is NEVER padded — padding
                    // once bypassed a fallback and poisoned the cache.
                    if (parts.Length == lines.Count)
                        return parts.Select(p => p.Trim()).ToList();
                }
                catch (TranslationException) { throw; }  // rate-limit etc. — let the caller show it
                // A genuine Stop must not be spent on a per-line retry of a batch the user
                // abandoned. The bare catch below is an OCE catch too, and I3 asks every one of
                // them to say so — the filter is what keeps an HttpClient timeout (an OCE whose
                // token is NOT cancelled) from masquerading as a cancel.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* fall through to per-line */ }
            }
        }

        // Per line. Translate as many as possible and KEEP the successes even if a later line is
        // refused — otherwise translating line 30 of 40 and hitting a 429 would throw away the 29
        // good translations we already had.
        var result = new List<string>(lines.Count);
        bool rateLimited = false;
        foreach (var l in lines)
        {
            if (rateLimited) { result.Add("(skipped — rate-limited, try again shortly)"); continue; }
            try { result.Add(await TranslateAsync(l, source, target, ct).ConfigureAwait(false)); }
            // A real cancel must propagate — and ONLY a real one (I3). Unfiltered, this catch
            // rethrows an HttpClient timeout as if the user had pressed Stop and throws away every
            // line already translated above it. Timeouts never arrive here as an OCE anyway: the
            // core hands them over as a Timeout-kind TranslationException.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // I16, with E3.S6's narrowing: ONLY a refusal latches. Asking a provider that just said
            // "stop" for thirteen more lines is how a soft block becomes a hard one — a reason that
            // holds for RateLimited and Blocked and for nothing else. A BadResponse or a Timeout on
            // line 3 of 14 fails its own line and no other.
            catch (TranslationException tex) when (tex.Kind is TranslationErrorKind.RateLimited
                                                            or TranslationErrorKind.Blocked)
            { rateLimited = true; result.Add("(rate-limited — try again shortly)"); }
            // Every other typed failure is this line's problem and no other line's. Written out
            // rather than left to the generic catch — which renders it identically today — so the
            // intent survives an edit to that catch: this arm exists to NOT latch, and the string it
            // borrows is E7.S1's to reword (ruling E2-d).
            catch (TranslationException tex) { result.Add($"(translation failed: {tex.Message})"); }
            catch (Exception ex) { result.Add($"(translation failed: {ex.Message})"); }
        }
        return result;

        async Task<string> SafeOne(string line)
        {
            try { return await TranslateAsync(line, source, target, ct).ConfigureAwait(false); }
            catch (TranslationException) { throw; }
            // The one-line path is a third OCE catch and I3 asks every one of them to say so: the
            // generic catch below is where a genuine Stop would land, and it would turn the cancel
            // into a translation-failed line instead of propagating.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return $"(translation failed: {ex.Message})"; }
        }
    }

    /// <summary>One logical call: this method owns the URL, and hands everything else to
    /// <see cref="HttpProviderCore"/>. No <c>dt=</c>, no <c>ie=</c>/<c>oe=</c> — §7.1's four
    /// parameters and nothing more, because every extra parameter is a guess about an endpoint
    /// nobody documents.</summary>
    private Task<string> RequestAsync(string text, string source, string target, CancellationToken ct)
    {
        // sl/tl are encoded as well as q (E3.S4 review). They are combo-box Tag constants today, so
        // this is not reachable from the app — but TranslateAsync is public on a public class, and a
        // '&' in a language code would inject a parameter into a URL whose ONE parameter that must
        // never move is `client=`: R1's whole mitigation is that the client id lives in one place.
        // Encoding "ru"/"en"/"auto" is the identity, so it costs exactly nothing to close.
        var url = $"https://clients5.google.com/translate_a/t?client={ClientId}" +
                  $"&sl={HttpUtility.UrlEncode(source)}&tl={HttpUtility.UrlEncode(target)}" +
                  $"&q={HttpUtility.UrlEncode(text)}";

        // The address travels as a Uri because RequestLog renders host + path and CANNOT render a
        // query, so the q= never reaches the log; the text is handed over only to be MEASURED (I11).
        return _core.SendAsync(new Uri(url), () => new HttpRequestMessage(HttpMethod.Get, url),
            Parse, source, target, text, _priority, ct);
    }

    /// <summary>Pull the translation out of a <c>dict-chrome-ex</c> response. Two shapes, both
    /// recorded verbatim in <c>benchmark…</c> §3.2 and both fixtured:
    /// <list type="bullet">
    /// <item>a fixed <c>sl</c> answers <c>["Hello"]</c> — shape A, take <c>root[0]</c>;</item>
    /// <item><c>sl=auto</c> answers <c>[["Hello","ru"]]</c> — shape B, take <c>root[0][0]</c> and
    /// DISCARD the detected language: the source is chosen per message by the caller (I7), and a
    /// provider that second-guessed it would be answering a question nobody asked.</item>
    /// </list>
    /// Anything else is a <c>BadResponse</c> — strictly, and never padded or guessed at (I5).
    ///
    /// <para>Run by the core INSIDE the admission, so a body that is not this shape reaches the gate
    /// as the <c>BadResponse</c> it is (§5.3 counts three in a row). An HTML block page can no
    /// longer arrive here: the core's §4.3 sniff classifies it first, so what lands in the catch is
    /// a body that claimed to be JSON, did not start with '&lt;', and still is not this shape.</para></summary>
    internal static string Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // EXACTLY one element, not "at least one" (E3.S4 review). The root carries one element
            // per q=, and this provider sends exactly one q= — multi-q= is a declined non-feature
            // (project-context.md), so a root of two is a shape the app cannot have asked for.
            // Accepting it would read root[0] and DROP the rest while reporting a SUCCESS to the
            // gate, so §5.3's three-BadResponse strike never fires and the user gets translation 1
            // of N with no error anywhere: silent truncation, which is I5's "never pad" seen from
            // the other side. benchmark… §3.2 records `["Hello","How are you"]` as the two-q= reply,
            // which is precisely the body this rejects.
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1) throw new JsonException();

            var first = root[0];
            var text = first.ValueKind switch
            {
                JsonValueKind.String => first.GetString(),
                // Shape B. GetArrayLength() rather than a bare index: an empty inner array is
                // exactly the "anything else" this parser is strict about.
                JsonValueKind.Array when first.GetArrayLength() > 0 && first[0].ValueKind == JsonValueKind.String
                    => first[0].GetString(),
                _ => throw new JsonException(),
            };
            return text ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is JsonException or IndexOutOfRangeException
                                         or InvalidOperationException)
        {
            // §4.2 row 12: a success whose body is not the provider's shape.
            throw new TranslationException(TranslationErrorKind.BadResponse,
                "The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.");
        }
    }
}
