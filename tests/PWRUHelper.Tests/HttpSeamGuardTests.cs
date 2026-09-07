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
/// It also PINS the retry behaviour. E1.S1 wrote those two pins as "3 requests" and said in its own
/// tasks that E2.S5 would re-point them to TP-RET-01 / TP-RET-03 in the same commit that changed the
/// loop; this is that commit, so they now read one request for a 429 and two for a 503.
/// </summary>
[Collection("Gates")]
public class HttpSeamGuardTests : GatesTestBase
{
    // The Google endpoint's shape: [[["translated","original",…], …], …]
    private const string GoogleOk = """[[["hello","привет",null,null,10]],null,"ru"]""";

    // ---------- IS-10: no provider can be built without a handler in a test ----------

    /// <summary>
    /// Every ITranslator in the app that can reach the network. The list grows on its own as
    /// providers are added — which is the point: a new provider without the seam fails here.
    ///
    /// <para>An <c>HttpProviderCore</c> field counts as well as an <c>HttpClient</c> one, and that
    /// is the whole derivation since E2.S5. The comment on <c>FieldsIncludingBase</c> anticipated
    /// the shared core arriving as a BASE CLASS; it arrived as a FIELD instead. E3's three new
    /// providers are meant to be "a URL, a payload and a parser" — a natural one holds only an
    /// <c>HttpProviderCore</c> and no <c>HttpClient</c> at all, and under the old derivation it
    /// would not have been enumerated: not a failing guard, an <b>absent</b> one, with CI free to
    /// reach the real Internet. Both shipped providers still keep a redundant <c>_http</c> field,
    /// which is the only reason this was not already broken.</para>
    /// </summary>
    private static List<Type> HttpProviders() =>
        AppTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ITranslator).IsAssignableFrom(t))
            .Where(t => FieldsIncludingBase(t).Any(f => f.FieldType == typeof(HttpClient)
                                                     || f.FieldType == typeof(HttpProviderCore)))
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

        // Without these the loop below could pass on an empty list. GoogleDictTranslator joined
        // them with E3.S4 — not to make the guard find it (the reflection above does that on its
        // own, which is the whole design) but so that a provider silently dropping off the list
        // fails here instead of quietly widening the network surface CI is allowed to reach.
        Assert.Contains(typeof(GoogleGtxTranslator), providers);
        Assert.Contains(typeof(GoogleDictTranslator), providers);
        Assert.Contains(typeof(DeepLTranslator), providers);
        // E6.S2's addition. The reflection above finds it on its own — the point of putting it on
        // the floor is that a provider dropping OFF the derived list fails here instead of quietly
        // widening the network surface CI is allowed to reach.
        Assert.Contains(typeof(AzureTranslator), providers);

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
        // The only three handler-less providers the suite builds (GoogleDict joined them with
        // E3.S4): IS-10 forbids them because they *could* reach the Internet, and none of the three
        // is ever asked to translate — constructing them is the only way to prove "null means the
        // shared client" (IS-8).
        // Both NotNull guards matter: without them a renamed field would make this Assert.Same
        // compare null to null and pass while checking nothing.
        Assert.NotNull(SharedClientOf(typeof(GoogleGtxTranslator)));
        Assert.NotNull(SharedClientOf(typeof(GoogleDictTranslator)));
        Assert.NotNull(SharedClientOf(typeof(DeepLTranslator)));
        Assert.NotNull(SharedClientOf(typeof(AzureTranslator)));

        Assert.Same(SharedClientOf(typeof(GoogleGtxTranslator)), ClientOf(new GoogleGtxTranslator()));
        Assert.Same(SharedClientOf(typeof(GoogleDictTranslator)), ClientOf(new GoogleDictTranslator()));
        Assert.Same(SharedClientOf(typeof(DeepLTranslator)), ClientOf(new DeepLTranslator("k:fx")));
        Assert.Same(SharedClientOf(typeof(AzureTranslator)), ClientOf(new AzureTranslator("k", "westeurope")));
    }

    [Fact]
    public void A_handler_gets_its_own_client()
    {
        var google = new GoogleGtxTranslator(new FakeHandler());
        var dict = new GoogleDictTranslator(new FakeHandler());
        var deepl = new DeepLTranslator("k:fx", new FakeHandler());
        var azure = new AzureTranslator("k", "westeurope", new FakeHandler());

        Assert.NotNull(ClientOf(google));
        Assert.NotNull(ClientOf(dict));
        Assert.NotNull(ClientOf(deepl));
        Assert.NotNull(ClientOf(azure));
        Assert.NotSame(SharedClientOf(typeof(GoogleGtxTranslator)), ClientOf(google));
        Assert.NotSame(SharedClientOf(typeof(GoogleDictTranslator)), ClientOf(dict));
        Assert.NotSame(SharedClientOf(typeof(DeepLTranslator)), ClientOf(deepl));
        Assert.NotSame(SharedClientOf(typeof(AzureTranslator)), ClientOf(azure));
    }

    [Fact]
    public void The_shared_clients_recycle_pooled_connections()
    {
        // These clients live for the whole process. Without a lifetime the pool can sit on a
        // connection that has gone stale (or on a DNS answer that has moved) and never replace it.
        // Asserted on the factory each shared client is built from: digging the handler back out of
        // an HttpClient means reading a private runtime field, which breaks on a .NET servicing
        // update for a reason that has nothing to do with this app. Since E2.S5 there is ONE
        // factory — the two were byte-identical — so this asserts the shape both providers get.
        using var handler = HttpProviderCore.CreatePooledHandler();

        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);

        // …and the production handler keeps the RUNTIME's connect path. E2.S5 first shipped a
        // ConnectCallback to learn the address family for §10.1's `ipv=`; Winston's review removed
        // it, because a connect callback replaces DNS resolution, dual-stack Happy Eyeballs, proxy
        // tunnelling and the connect-timeout semantics with this app's own code — on a tool that
        // runs on arbitrary home and corporate networks that nobody here can diagnose remotely. A
        // diagnostic field is not worth owning the path every request travels on. `ipv=` stays `?`
        // (pinned in HttpProviderCoreTests); this is the pin that stops the callback coming back.
        Assert.Null(handler.ConnectCallback);
    }

    // ---------- IS-11: the double really drives the providers ----------

    [Fact]
    public async Task Google_translates_through_the_fake_handler()
    {
        var fake = new FakeHandler().RespondJson(GoogleOk);

        Assert.Equal("hello", await new GoogleGtxTranslator(fake).TranslateAsync("привет", "ru", "en"));

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
        // The other half of the User-Agent pin above: E2.S5 moved the client factory into
        // HttpProviderCore, and the UA is a provider OPTION rather than a shared default precisely
        // so this stays true. DeepL has never sent one; a shared factory that added Google's Chrome
        // string here would be a behaviour change on a keyed vendor path, smuggled in by a refactor.
        Assert.False(call.Headers.ContainsKey("User-Agent"),
            "DeepL must not start sending a User-Agent it has never sent");
    }

    [Fact]
    public async Task A_scripted_sequence_lets_a_retry_succeed()
    {
        // 503 first, then the real answer — proving the script advances and the loop retries. It
        // used to be a 429; since E2.S5 that is the one thing a retry may NOT be tried on (the gate
        // owns the wait), and Unavailable is what a second attempt is for.
        var fake = new FakeHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .RespondJson(GoogleOk);

        Assert.Equal("hello", await new GoogleGtxTranslator(fake).TranslateAsync("привет", "ru", "en"));
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
        await new GoogleGtxTranslator(fake).TranslateAsync("привет", "ru", "en");

        Assert.True(sw.ElapsedMilliseconds >= 30, $"the delay was not honoured ({sw.ElapsedMilliseconds} ms)");
    }

    // ---------- The retry policy (§5.6), pinned where E1.S1 pinned its predecessor ----------

    /// <summary>TP-RET-01 — the sentence the whole epic is about: a refusal costs <b>one</b>
    /// request. It cost three, and the two extra ones bought nothing but a tripled abuse signal
    /// (benchmark-fournisseurs.md §11.4 item 3).</summary>
    [Fact]
    public async Task TP_RET_01_a_429_costs_exactly_one_request()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.TooManyRequests, "<html>blocked</html>", "text/html");

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleGtxTranslator(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(1, fake.Requests);
        Assert.Equal(TranslationErrorKind.RateLimited, ex.Kind);
        Assert.Contains("limiting translations", ex.Message);
    }

    /// <summary>TP-RET-03 — a 5xx is the failure a second attempt could plausibly survive, so it is
    /// the one that gets it: exactly two requests, then <c>Unavailable</c>.</summary>
    [Fact]
    public async Task TP_RET_03_a_503_costs_exactly_two_requests()
    {
        var fake = new FakeHandler().Respond(HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => new GoogleGtxTranslator(fake).TranslateAsync("привет", "ru", "en"));

        Assert.Equal(2, fake.Requests);
        Assert.Equal(TranslationErrorKind.Unavailable, ex.Kind);
        Assert.Contains("unavailable", ex.Message);
    }
}
