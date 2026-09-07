using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace PWRUHelper.Tests;

/// <summary>
/// The HTTP test double every provider test hangs off (IS-11). The suite had none at all, which is
/// why the retry loop, the status mapping and every response parser were unreachable from a test.
///
/// It records what was SENT (count, method, absolute URI, headers, body — the URI is what the
/// "no q= in a log line" assertions read) and what it ANSWERED, and it is scripted: each
/// Respond/Throws call appends one step and the LAST step repeats forever, so "429 on every
/// attempt" is one step and "429 then 200" is two. Nothing here touches the network — a provider
/// built on this handler cannot reach the Internet (IS-10).
/// </summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    /// <summary>One recorded exchange.</summary>
    internal sealed class Call
    {
        public HttpMethod Method = HttpMethod.Get;
        public Uri Uri = new("about:blank");
        public Dictionary<string, string> Headers = new();
        public string? Body;
        public HttpStatusCode? Status;      // what we answered; null if we threw or never finished
        public string? ResponseBody;
        public Exception? Thrown;
    }

    private sealed record Step(HttpStatusCode Status, string Body, string ContentType, Exception? Throw,
        IReadOnlyList<KeyValuePair<string, string>>? Headers = null, TimeSpan Delay = default);

    // Guards both lists: a provider that ever sends two requests at once must still get a
    // deterministic script step and an intact recording.
    private readonly object _gate = new();
    private readonly List<Step> _steps = new();
    private readonly List<Call> _calls = new();

    /// <summary>The exchanges so far, oldest first (a snapshot; the Call objects are live).</summary>
    public IReadOnlyList<Call> Calls { get { lock (_gate) return _calls.ToList(); } }

    public int Requests { get { lock (_gate) return _calls.Count; } }

    /// <summary>Artificial latency, for the slow-provider / timeout paths.</summary>
    public TimeSpan Delay = TimeSpan.Zero;

    public FakeHandler Respond(HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        lock (_gate) _steps.Add(new Step(status, body, contentType, null));
        return this;
    }

    /// <summary>
    /// One response header on the step just scripted — <c>Retry-After</c> above all: §5.5 cannot be
    /// tested at all without it, and neither can the allow-list <c>ResponseFacts</c> renders
    /// (<c>Via</c>, <c>Server</c>, <c>X-RateLimit-*</c>, <c>Set-Cookie</c>). Added without
    /// validation, because a header a real server sends malformed is exactly the shape the log's
    /// sanitiser exists for.
    /// </summary>
    public FakeHandler WithHeader(string name, string value)
    {
        lock (_gate)
        {
            if (_steps.Count == 0)
                throw new InvalidOperationException("WithHeader needs a Respond/Throws step in front of it.");
            var last = _steps[^1];
            var headers = new List<KeyValuePair<string, string>>(last.Headers ?? Array.Empty<KeyValuePair<string, string>>())
            {
                new(name, value),
            };
            _steps[^1] = last with { Headers = headers };
        }
        return this;
    }

    /// <summary>Artificial latency on the step just scripted, so attempt 1 can be slow and attempt 2
    /// fast — which the single global <see cref="Delay"/> cannot express, because it applies to
    /// both.</summary>
    public FakeHandler After(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_steps.Count == 0)
                throw new InvalidOperationException("After needs a Respond/Throws step in front of it.");
            _steps[^1] = _steps[^1] with { Delay = delay };
        }
        return this;
    }

    public FakeHandler RespondJson(string json) => Respond(HttpStatusCode.OK, json);

    /// <summary>Answer with a transport failure (an HttpRequestException, or anything else). A
    /// repeated step rethrows the same instance, so assert on its type and message, not its
    /// stack trace.</summary>
    public FakeHandler Throws(Exception ex)
    {
        lock (_gate) _steps.Add(new Step(default, "", "", ex));
        return this;
    }

    /// <summary>The HttpClient-timeout shape: a TaskCanceledException carrying a token that is NOT
    /// cancelled. Without it the "a timeout is not a user cancel" rule cannot be tested at all.</summary>
    public FakeHandler TimesOut() => Throws(new TaskCanceledException(
        "The request was canceled due to the configured HttpClient.Timeout of 12 seconds elapsing.",
        new TimeoutException(), CancellationToken.None));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // A real handler observes the token, so a "cancelled token costs zero requests" assertion
        // has to be able to fail here rather than record a phantom exchange.
        ct.ThrowIfCancellationRequested();

        // Request headers and content headers land in one bag (they never share a name), so a
        // Content-Type assertion finds what it expects. Multi-value headers are joined with ", ":
        // the User-Agent therefore reads as its parsed product tokens, not the raw string.
        var headers = request.Headers.Concat(
            request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());
        var call = new Call
        {
            Method = request.Method,
            Uri = request.RequestUri ?? new Uri("about:blank"),
            Headers = headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct),
        };

        Step step;
        lock (_gate)
        {
            _calls.Add(call);

            // An unscripted 200 with an empty body would surface as "the service returned an
            // unexpected response" — a parser bug that is not one. Say what actually happened.
            if (_steps.Count == 0)
                throw new InvalidOperationException(
                    "FakeHandler received a request with nothing scripted — call Respond/Throws first.");

            // Past the end of the script the last step repeats, so "429 on every attempt" is one
            // step. The index is this call's own, taken under the lock — not a later count.
            step = _steps[Math.Min(_calls.Count - 1, _steps.Count - 1)];
        }

        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (step.Delay > TimeSpan.Zero) await Task.Delay(step.Delay, ct);

        if (step.Throw != null)
        {
            call.Thrown = step.Throw;
            throw step.Throw;
        }

        call.Status = step.Status;
        call.ResponseBody = step.Body;
        var response = new HttpResponseMessage(step.Status)
        {
            Content = new StringContent(step.Body, Encoding.UTF8, step.ContentType),
        };
        foreach (var header in step.Headers ?? Enumerable.Empty<KeyValuePair<string, string>>())
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }
}
