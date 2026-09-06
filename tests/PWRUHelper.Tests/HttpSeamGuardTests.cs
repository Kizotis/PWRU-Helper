using System.Net;
using System.Net.Http;
using System.Reflection;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// The HTTP seam itself (IS-8/IS-10/IS-11): every provider that owns an HttpClient must accept a
/// test handler, or its retry policy and its response parsing stay unreachable — and a test that
/// forgets one turns CI into a client of a rented endpoint.
///
/// It also PINS today's retry behaviour before anything changes it. Those two pins are deliberately
/// "3 requests"; the story that re-points the policy (429 ⇒ one request, 503 ⇒ two) rewrites them in
/// the same commit that changes the loop.
/// </summary>
public class HttpSeamGuardTests
{
    // The Google endpoint's shape: [[["translated","original",…], …], …]
    private const string GoogleOk = """[[["hello","привет",null,null,10]],null,"ru"]""";

    // ---------- IS-10: no provider can be built without a handler in a test ----------

    /// <summary>Every ITranslator in the app that holds an HttpClient. The list grows on its own as
    /// providers are added — which is the point: a new provider without the seam fails here.</summary>
    private static List<Type> HttpProviders() =>
        AppTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ITranslator).IsAssignableFrom(t))
            .Where(t => FieldsIncludingBase(t).Any(f => f.FieldType == typeof(HttpClient)))
            .OrderBy(t => t.Name)
            .ToList();

    // A type that fails to load must not quietly shrink the list into "no providers found".
    private static IEnumerable<Type> AppTypes()
    {
        try { return typeof(ITranslator).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    // GetFields does not return a base class private fields, so walk the chain: the planned shared
    // HttpProviderCore would otherwise hide its client from this guard and fail open.
    private static IEnumerable<FieldInfo> FieldsIncludingBase(Type? t)
    {
        for (; t != null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Static |
                                          BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.DeclaredOnly))
                yield return f;
    }

    [Fact]
    public void Every_http_provider_accepts_a_test_handler()
    {
        var providers = HttpProviders();

        // Without these two the loop below could pass on an empty list.
        Assert.Contains(typeof(TranslationService), providers);
        Assert.Contains(typeof(DeepLTranslator), providers);

        foreach (var t in providers)
        {
            var seams = t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(c => c.GetParameters()
                                      .Any(p => p.ParameterType == typeof(HttpMessageHandler)))
                         .ToList();
            Assert.True(seams.Count > 0,
                $"{t.Name} owns an HttpClient but takes no HttpMessageHandler — a test could construct it " +
                "and hit the real network (IS-10).");
            // Every match, not just the first: GetConstructors order is not specified, so checking
            // one of two overloads would pass or fail depending on the run.
            Assert.All(seams, c => Assert.True(c.IsAssembly,
                $"{t.Name}'s handler ctor must stay internal — it is a test seam, not public API."));

            // …and the seam must not shrink the public surface on its way in: a public type whose
            // only ctor is internal is not constructible outside the assembly at all.
            if (t.IsPublic)
                Assert.True(t.GetConstructors().Length > 0,
                    $"{t.Name} is public but has no public constructor left.");
        }
    }

    // ---------- IS-8: null ⇒ the shared static client, a handler ⇒ a private one ----------

    private static object? ClientOf(object provider) =>
        provider.GetType().GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(provider);

    private static object? SharedClientOf(Type t) =>
        t.GetField("Http", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);

    [Fact]
    public void No_handler_keeps_the_shared_static_client()
    {
        // The only two handler-less providers the suite builds: IS-10 forbids them because they
        // *could* reach the Internet, and neither is ever asked to translate — constructing them is
        // the only way to prove "null means the shared client" (IS-8).
        // Both NotNull guards matter: without them a renamed field would make this Assert.Same
        // compare null to null and pass while checking nothing.
        Assert.NotNull(SharedClientOf(typeof(TranslationService)));
        Assert.NotNull(SharedClientOf(typeof(DeepLTranslator)));

        Assert.Same(SharedClientOf(typeof(TranslationService)), ClientOf(new TranslationService()));
        Assert.Same(SharedClientOf(typeof(DeepLTranslator)), ClientOf(new DeepLTranslator("k:fx")));
    }

    [Fact]
    public void A_handler_gets_its_own_client()
    {
        var google = new TranslationService(new FakeHandler());
        var deepl = new DeepLTranslator("k:fx", new FakeHandler());

        Assert.NotNull(ClientOf(google));
        Assert.NotNull(ClientOf(deepl));
        Assert.NotSame(SharedClientOf(typeof(TranslationService)), ClientOf(google));
        Assert.NotSame(SharedClientOf(typeof(DeepLTranslator)), ClientOf(deepl));
    }

    [Fact]
    public void The_shared_clients_recycle_pooled_connections()
    {
        // These clients live for the whole process. Without a lifetime the pool can sit on a
        // connection that has gone stale (or on a DNS answer that has moved) and never replace it.
        // Asserted on the factory each shared client is built from: digging the handler back out of
        // an HttpClient means reading a private runtime field, which breaks on a .NET servicing
        // update for a reason that has nothing to do with this app.
        using var google = TranslationService.CreatePooledHandler();
        using var deepl = DeepLTranslator.CreatePooledHandler();

        Assert.Equal(TimeSpan.FromMinutes(2), google.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), deepl.PooledConnectionLifetime);
    }

    // ---------- IS-11: the double really drives the providers ----------

    [Fact]
    public async Task Google_translates_through_the_fake_handler()
    {
        var fake = new FakeHandler().RespondJson(GoogleOk);

        Assert.Equal("hello", await new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        var call = Assert.Single(fake.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("translate.googleapis.com", call.Uri.Host);
        Assert.Equal(HttpStatusCode.OK, call.Status);
        // The private test client comes off the same factory as the shared one, so this also pins
        // the browser-like User-Agent that is frozen on purpose (architecture-cible §7.0).
        Assert.Contains("Chrome/120.0", call.Headers["User-Agent"]);
    }

    [Fact]
    public async Task DeepL_translates_through_the_fake_handler()
    {
        var fake = new FakeHandler().RespondJson("""{"translations":[{"text":"hello"}]}""");

        Assert.Equal("hello", await new DeepLTranslator("k:fx", fake).TranslateAsync("привет", "ru", "en"));

        var call = Assert.Single(fake.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("api-free.deepl.com", call.Uri.Host);
        Assert.Equal("DeepL-Auth-Key k:fx", call.Headers["Authorization"]);
        Assert.Contains("target_lang=EN-US", call.Body);
    }

    [Fact]
    public async Task A_scripted_sequence_lets_a_retry_succeed()
    {
        // 429 first, then the real answer — proving the script advances and the loop retries.
        var fake = new FakeHandler()
            .Respond(HttpStatusCode.TooManyRequests, "<html>blocked</html>", "text/html")
            .RespondJson(GoogleOk);

        Assert.Equal("hello", await new TranslationService(fake).TranslateAsync("привет", "ru", "en"));
        Assert.Equal(2, fake.Requests);
    }

    [Fact]
    public async Task A_transport_failure_reaches_the_provider()
    {
        var fake = new FakeHandler().Throws(new HttpRequestException("no route"));

        await Assert.ThrowsAsync<TranslationException>(
            () => new DeepLTranslator("k:fx", fake).TranslateAsync("привет", "ru", "en"));
        Assert.Equal(1, fake.Requests);
    }

    [Fact]
    public async Task A_timeout_is_not_read_as_a_user_cancel()
    {
        // The synthetic TaskCanceledException carries an UNCANCELLED token, which is exactly what an
        // HttpClient timeout looks like. DeepL's `when (ct.IsCancellationRequested)` filter must let
        // it through to the timeout branch instead of treating it as a cancellation.
        var fake = new FakeHandler().TimesOut();

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new DeepLTranslator("k:fx", fake).TranslateAsync("привет", "ru", "en"));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task The_double_can_slow_a_response_down()
    {
        // IS-11 artificial latency, the raw material of the slow-provider cases. Only a lower bound
        // is asserted — an upper one would be a flake on a loaded CI box.
        var fake = new FakeHandler { Delay = TimeSpan.FromMilliseconds(40) }.RespondJson(GoogleOk);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await new TranslationService(fake).TranslateAsync("привет", "ru", "en");

        Assert.True(sw.ElapsedMilliseconds >= 30, $"the delay was not honoured ({sw.ElapsedMilliseconds} ms)");
    }

    // ---------- Pre-change pins of today's retry policy ----------

    [Fact]
    public async Task Today_a_429_costs_three_requests()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, "<html>blocked</html>", "text/html");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(3, fake.Requests);
        Assert.Contains("limiting translations", ex.Message);
    }

    [Fact]
    public async Task Today_a_503_costs_three_requests()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new TranslationService(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(3, fake.Requests);
        Assert.Contains("unavailable", ex.Message);
    }
}
