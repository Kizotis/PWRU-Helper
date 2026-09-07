using PWRUHelper.Services;

namespace PWRUHelper.Tests;

/// <summary>
/// <b>The fixture for the whole of epic E8.</b> Test-plan §3.13 is a field campaign — a real model,
/// a real 22 MB DLL, a real machine — so every AUTOMATED case in E8.S2, E8.S3, E8.S4 and E8.S5 runs
/// against this instead (CI-8: no model download and no native library in CI, ever). It implements
/// the same three C exports <see cref="IBergamotEngine"/> declares and nothing else.
///
/// <para>Hand-written, like every other double in this suite (CI-10 forbids a mocking library), and
/// the templates are <c>ChainTranslatorTests.Fake</c> and <c>CachingTranslatorTests.CountingTranslator</c>:
/// public counters, an optional behaviour delegate, no framework.</para>
/// </summary>
internal sealed class FakeBergamotEngine : IBergamotEngine
{
    /// <summary>What one line comes back as. Null is the default shape — a marked-up echo, so an
    /// assertion can tell a translated line from the line it went in as.</summary>
    internal Func<string, bool, string>? OnTranslate;

    /// <summary>What a frame comes back as. Null translates each line through
    /// <see cref="OnTranslate"/>, which is the honest default: the batch is the SAME native export.
    /// A case that wants I5's mismatch returns a list of the wrong length from here.</summary>
    internal Func<IReadOnlyList<string>, IReadOnlyList<string>>? OnBatch;

    internal readonly List<(string Text, bool Html)> Calls = new();

    internal readonly List<IReadOnlyList<string>> Batches = new();

    private int _disposals;

    private int _inFlight;

    private int _peakInFlight;

    /// <summary><c>translator_free</c> calls. A count and not a bool, because "exactly once, and
    /// safe to ask twice" is what T6 owes.</summary>
    internal int Disposals => Volatile.Read(ref _disposals);

    /// <summary>The most native calls this engine was ever inside at once. <b>It has to be 1.</b>
    /// The binding is not documented thread-safe and <c>BlockingService</c> is a synchronous native
    /// engine, not a server, so the provider owes it one caller at a time — while the read chain and
    /// the write chain share one process and the Translator tab is not sequential. A counter here
    /// rather than an assertion, so a case can state the contract as an equality.</summary>
    internal int PeakConcurrentCalls => Volatile.Read(ref _peakInFlight);

    /// <summary>Whether the handle had already been freed when a call arrived — the use-after-free
    /// T6 exists to make impossible, observed from the side that would suffer it.</summary>
    internal bool WasCalledAfterDispose;

    /// <summary>The mirror image: whether <c>translator_free</c> ran while a call was still inside
    /// this engine. Both directions, because "safe against a translate in flight" is a claim about
    /// an ordering and an ordering has two ends.</summary>
    internal bool WasDisposedWhileInFlight;

    /// <summary>Runs inside <c>translator_free</c>, before the count is raised — so a case can
    /// record what the WORLD looked like at the instant the handle went (E8.S4: the model directory
    /// must still be there when the engine is freed, because deleting it first is the bug).</summary>
    internal Action? OnDispose;

    public string Translate(string text, bool html)
    {
        Enter();
        try
        {
            return TranslateCore(text, html);
        }
        finally { Leave(); }
    }

    public IReadOnlyList<string> TranslateBatch(IReadOnlyList<string> lines)
    {
        Enter();
        try
        {
            lock (Batches) Batches.Add(lines.ToList());
            // TranslateCore and not Translate: the default batch is the SAME native export, so
            // counting each line as a nested call would make the peak 2 for one caller.
            return OnBatch?.Invoke(lines) ?? lines.Select(l => TranslateCore(l, true)).ToList();
        }
        finally { Leave(); }
    }

    private string TranslateCore(string text, bool html)
    {
        lock (Calls) Calls.Add((text, html));
        return OnTranslate?.Invoke(text, html) ?? "[" + text + "]";
    }

    private void Enter()
    {
        if (Disposals > 0) WasCalledAfterDispose = true;

        var now = Interlocked.Increment(ref _inFlight);
        // Raise the high-water mark without a lock: only ever upward, and only by the thread that
        // saw a higher count than the one already recorded.
        int seen;
        while ((seen = Volatile.Read(ref _peakInFlight)) < now)
            Interlocked.CompareExchange(ref _peakInFlight, now, seen);
    }

    private void Leave() => Interlocked.Decrement(ref _inFlight);

    public void Dispose()
    {
        if (Volatile.Read(ref _inFlight) > 0) WasDisposedWhileInFlight = true;
        OnDispose?.Invoke();
        Interlocked.Increment(ref _disposals);
    }
}

/// <summary>
/// <c>translator_initialize</c>, as the seam <see cref="BergamotTranslator"/> takes: a factory that
/// counts how many engines were built and can be told to fail. One instance per case, so
/// "constructing the provider loads nothing" and "two concurrent first calls build ONE engine" are
/// both assertions on <see cref="Initialisations"/>.
/// </summary>
internal sealed class FakeBergamotEngineFactory
{
    /// <summary>How many times the engine was initialised. Two would be 254 MiB.</summary>
    internal int Initialisations;

    /// <summary>The config paths it was handed, in order — the pin that this class asks the store
    /// for a file and does not invent a path of its own.</summary>
    internal readonly List<string> ConfigPaths = new();

    /// <summary>Set to make initialisation fail — §7.6 constraint 7's second failure mode.</summary>
    internal Exception? FailWith;

    /// <summary>Runs just before an engine is handed back, for a case that needs the two racing
    /// callers to actually meet inside the load rather than one after the other.</summary>
    internal Action? BeforeReturn;

    internal FakeBergamotEngine? Last { get; private set; }

    /// <summary>Every engine this factory built, so a case can assert the ones it dropped were
    /// freed.</summary>
    internal readonly List<FakeBergamotEngine> All = new();

    internal Func<string, IBergamotEngine> Create => configPath =>
    {
        lock (ConfigPaths) ConfigPaths.Add(configPath);
        Interlocked.Increment(ref Initialisations);
        if (FailWith is not null) throw FailWith;
        BeforeReturn?.Invoke();
        var engine = new FakeBergamotEngine();
        lock (All) { All.Add(engine); Last = engine; }
        return engine;
    };
}
