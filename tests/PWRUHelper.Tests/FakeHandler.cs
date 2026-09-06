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
        public HttpStatusCode? Status;      // what we answered; null when we threw instead
        public string? ResponseBody;
        public Exception? Thrown;
    }

    private sealed record Step(HttpStatusCode Status, string Body, string ContentType, Exception? Throw);

    private readonly List<Step> _steps = new();

    public readonly List<Call> Calls = new();
    public int Requests => Calls.Count;

    /// <summary>Artificial latency, for the slow-provider / timeout paths.</summary>
    public TimeSpan Delay = TimeSpan.Zero;

    public FakeHandler Respond(HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        _steps.Add(new Step(status, body, contentType, null));
        return this;
    }

    public FakeHandler RespondJson(string json) => Respond(HttpStatusCode.OK, json);

    /// <summary>Answer with a transport failure (an HttpRequestException, or anything else).</summary>
    public FakeHandler Throws(Exception ex)
    {
        _steps.Add(new Step(default, "", "", ex));
        return this;
    }

    /// <summary>The HttpClient-timeout shape: a TaskCanceledException carrying a token that is NOT
    /// cancelled. Without it the "a timeout is not a user cancel" rule cannot be tested at all.</summary>
    public FakeHandler TimesOut() => Throws(new TaskCanceledException(
        "The request was canceled due to the configured HttpClient.Timeout of 12 seconds elapsing.",
        new TimeoutException(), CancellationToken.None));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var call = new Call
        {
            Method = request.Method,
            Uri = request.RequestUri ?? new Uri("about:blank"),
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct),
        };
        Calls.Add(call);

        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);

        // Past the end of the script the last step repeats, so "429 on every attempt" is one step.
        var step = _steps.Count == 0
            ? new Step(HttpStatusCode.OK, "", "application/json", null)
            : _steps[Math.Min(Calls.Count - 1, _steps.Count - 1)];

        if (step.Throw != null)
        {
            call.Thrown = step.Throw;
            throw step.Throw;
        }

        call.Status = step.Status;
        call.ResponseBody = step.Body;
        return new HttpResponseMessage(step.Status)
        {
            Content = new StringContent(step.Body, Encoding.UTF8, step.ContentType),
        };
    }
}
