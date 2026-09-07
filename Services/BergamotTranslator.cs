using System.IO;

namespace PWRUHelper.Services;

/// <summary>
/// <c>architecture-cible.md</c> §7.6 — the offline tier: a local, keyless, network-free engine that
/// answers when every online rung is gone. It is <b>a class that turns text into text</b> and
/// nothing else. Three neighbouring concerns are deliberately not here, because each touches files
/// this one does not: the model store and the one-click install are <b>E8.S3</b>, the
/// keep-loaded-while-LIVE lifetime is <b>E8.S4</b> (this story ships the CAPABILITY —
/// <see cref="Load"/> / <see cref="Unload"/> / <see cref="IsLoaded"/> — and no policy, and above all
/// no timer), and the chain placement, the chip and the cache-drop rule are <b>E8.S5</b>.
///
/// <para><b>0 MB at rest, and that is the whole argument for shipping it.</b> One model is ~85 % of
/// the app's current working set (E8.S1: +121 MiB resident, +367 MiB committed private, while the
/// engine is live), and the app's stated virtue is not lagging the game. Nothing here touches the
/// disk before something asks for a translation — not the constructor (I10), not a health check,
/// not the About tab painting. A convenience engine that never unloads would turn an opt-in into a
/// permanent tax.</para>
///
/// <para><b>Why there is no <c>TryEnter</c>.</b> The gate's admission is a token bucket and a rate
/// ceiling built for a remote endpoint; a local engine has no one to be polite to, and taking a
/// token would let the offline tier eat the reserve §5.4 keeps for interactive traffic. This class
/// uses <see cref="ProviderGate"/> for exactly one of its two jobs — the breaker: one
/// <see cref="ProviderGate.ReportFailure"/> opens a soft window, <see cref="ChainTranslator"/> skips
/// the tier for its duration with no exception and no flag, and the half-open probe re-arms it for
/// free. That window is also the "the engine is broken" cache, which is why no private
/// <c>_initFailed</c> bool exists here: a second, disagreeing one is worse than none.</para>
///
/// <para><b>I2</b>: no UI type, no dispatcher, no settings read — the store's answer arrives as a
/// <c>Func&lt;string?&gt;</c> and "is LIVE running?" never arrives at all (that is E8.S4's, as a
/// <c>Func&lt;bool&gt;</c> it will own). <b>I1</b>: <see cref="ITranslator"/> is implemented
/// unchanged; <see cref="Load"/> and the rest are members of this class, never of the interface.
/// <b>I6</b>: this file never expands slang — see the class's own remark below, and the source scan
/// that enforces it.</para>
/// </summary>
internal sealed class BergamotTranslator : ITranslator, IDisposable
{
    /// <summary>Not spelled as a literal anywhere: a second spelling of an id is not a typo that
    /// fails loudly, it is a silently duplicated gate (<see cref="ProviderIds"/>). The user-facing
    /// name already exists too — <see cref="ProviderNames.Display"/> answers "Offline engine" and
    /// <see cref="ProviderNames.Short"/> "Offline" — so this story invents no name.</summary>
    private const string ProviderId = ProviderIds.Bergamot;

    /// <summary>The Marian config file the engine reads, inside the model directory. It is the ONE
    /// contract between this class and E8.S3's store, so it is a const in one place rather than a
    /// string agreed twice.</summary>
    internal const string ConfigFileName = "config.txt";

    /// <summary>The pairs release C installs — <b>ru→en and nothing else</b>. It is a constructor
    /// default rather than a hardcoded test because E8.S3's store is what will know which models are
    /// really on disk, and this class must not become the second place that decides.
    ///
    /// <para><c>auto</c> is deliberately NOT in it and is not a gap: a ru→en model cannot detect a
    /// language, and the OCR path already picks its source per message (I7) — the Russian lines
    /// arrive as <c>"ru"</c> and are exactly the ones this tier is for, while the rest ask for a
    /// pair nobody installed and are told so without loading 121 MiB to find out.</para></summary>
    internal static readonly IReadOnlyList<(string Source, string Target)> DefaultPairs =
        new[] { ("ru", "en") };

    private readonly Func<string?> _modelDirectory;
    private readonly Func<string?> _nativeDirectory;
    private readonly Func<string, IBergamotEngine> _engineFactory;
    private readonly IReadOnlyList<(string Source, string Target)> _pairs;
    private readonly ProviderGate? _gate;

    /// <summary>One engine, one lock, and the lock is taken by the translate path too (T6): an
    /// <see cref="Unload"/> racing a translate in flight would free the handle under a native call.
    /// It serialises this class's native work, which is the honest contract — <c>BlockingService</c>
    /// is a synchronous native engine, not a server — and AC 5's requirement is that two threads may
    /// CALL it at once, not that two translations run at once.</summary>
    private readonly object _sync = new();

    private IBergamotEngine? _engine;

    /// <summary>
    /// Nothing here touches the disk (I10) and nothing here resolves a gate's state: the locators
    /// are called on the first translation, and <see cref="ProviderGates.For"/> — which constructs
    /// only, and reads no file — is consulted per call through <see cref="Gate"/>.
    /// </summary>
    /// <param name="modelDirectory">E8.S3's store, as a question: the directory holding the
    /// installed model and its <see cref="ConfigFileName"/>, or <c>null</c> when nothing is
    /// installed. A <c>Func</c> and not a <c>string</c> because the answer changes while the app
    /// runs — the user can install or remove the engine from the About tab mid-session — and because
    /// E8.S1's harness has to be able to point it at <c>%TEMP%</c>. Defaults to "not installed", so
    /// a caller that has no store yet gets the honest answer rather than a path this class made
    /// up.</param>
    /// <param name="nativeDirectory">Where <c>bergamot.dll</c> was downloaded to. Same shape, same
    /// owner, and it is consumed by the resolver in <see cref="BergamotEngine"/> rather than
    /// here.</param>
    /// <param name="engineFactory">The seam. Every automated case in this epic runs against a fake
    /// implementing the same three calls, so CI never downloads a model and never loads a 22 MB
    /// native DLL (CI-8). Null means the real engine.</param>
    /// <param name="pairs">What is installed. Null means <see cref="DefaultPairs"/>.</param>
    /// <param name="gate">A test's own gate, exactly as the HTTP providers take one. Null means the
    /// registry's — resolved per call, never captured, because the registry may not be touched
    /// before the first request.</param>
    internal BergamotTranslator(
        Func<string?>? modelDirectory = null,
        Func<string?>? nativeDirectory = null,
        Func<string, IBergamotEngine>? engineFactory = null,
        IReadOnlyList<(string Source, string Target)>? pairs = null,
        ProviderGate? gate = null)
    {
        _modelDirectory = modelDirectory ?? (static () => null);
        _nativeDirectory = nativeDirectory ?? (static () => null);
        _engineFactory = engineFactory ?? (config => BergamotEngine.Create(config, _nativeDirectory));
        _pairs = pairs ?? DefaultPairs;
        _gate = gate;
    }

    /// <summary>This provider's gate, resolved through the registry on every call rather than
    /// captured at construction — the same rule, and the same reason, as
    /// <c>HttpProviderCore.Gate</c>: a provider is built while <c>MainWindow</c>'s constructor
    /// builds its chains, before first paint, and I10 forbids reaching the registry there.</summary>
    private ProviderGate Gate => _gate ?? ProviderGates.For(ProviderId);

    /// <summary>Is an engine resident right now? E8.S4's policy reads this; nothing in this class
    /// decides anything from it.</summary>
    internal bool IsLoaded => Volatile.Read(ref _engine) is not null;

    // =============================================================================================
    //  ITranslator — I1, two methods, ct last, and no member added to the interface
    // =============================================================================================

    /// <summary>
    /// One line. The synchronous native call runs on <see cref="Task.Run"/> and never on the UI
    /// thread (AC 1, AC 5, §7.6 constraint 3): E8.S1 measured 82 ms of init and another 109 ms for
    /// the first translate — the pool is allocated there — which is a visible freeze if it lands on
    /// the dispatcher.
    ///
    /// <para><b>I6 — and this is a pin, not an implementation.</b> The text arriving here has
    /// ALREADY been through the slang layer, upstream, in <c>MainWindow.Live.cs</c> and
    /// <c>MainWindow.Translate.cs</c>; the displayed original and the 🔑 line stay raw. This file
    /// must never reach for that layer itself: the cloud tiers were handed the same expanded text,
    /// so doing it again here would expand it twice — the exact hazard
    /// <c>Services/PendingRetryQueue.cs</c> opens by warning about. It is measured rather than
    /// stylistic: raw, this engine renders <c>данж</c> as "dangling" and <c>хил</c> as "heel". A
    /// source scan enforces the absence, because a comment can be deleted and a scan cannot.</para>
    /// </summary>
    public async Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return "";

        // Before anything is loaded (AC 3): a pair nobody installed cannot be answered by 121 MiB of
        // the wrong model, so the honest reply costs no memory and no disk.
        RequireInstalledPair(source, target);

        return await Task.Run(() => TranslateOne(text, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A whole OCR frame in <b>one</b> native call — the shape §6.2 specifies and E8.S1 measured at
    /// 3.75 ms/line against 8.34 ms/line one at a time.
    ///
    /// <para><b>I5 — a count mismatch is never padded.</b> §6.3's join/split per-line fallback is for
    /// the CLOUD join providers, where a newline surviving a round trip is genuinely unknown; a
    /// local engine handing back a different number of lines than it was given is a bug, not a shape
    /// to route around. Padding once bypassed the fallback and poisoned the cache
    /// (<c>DeepLTranslator.cs</c>), so this throws <see cref="TranslationErrorKind.BadResponse"/>
    /// and lets the chain move on.</para>
    /// </summary>
    public async Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string source,
        string target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        // Nothing to translate is not a reason to load an engine (I10, AC 3).
        if (lines.Count == 0) return new List<string>();

        RequireInstalledPair(source, target);

        // One line is the per-line call, not a one-element batch: it is the cheaper of the two
        // native shapes and it needs no markup round trip at all. It also gets TranslateAsync's own
        // guard, because the two entry points must not disagree about what a blank line costs: an
        // OCR row the nickname split emptied is not a question about a translation, and answering
        // it by loading 121 MiB would break the one rule (I10, AC 3) this class is built around.
        if (lines.Count == 1)
        {
            var only = lines[0]?.Trim() ?? "";
            if (only.Length == 0) return new List<string> { "" };

            return await Task.Run(() => new List<string> { TranslateOne(only, ct) }, ct)
                .ConfigureAwait(false);
        }

        return await Task.Run(() => TranslateBatch(lines, ct), ct).ConfigureAwait(false);
    }

    // =============================================================================================
    //  The lifetime — the capability only; E8.S4 owns WHEN (A-1(b))
    // =============================================================================================

    /// <summary>
    /// Bring the engine up now. E8.S4's policy calls this on the first LIVE tick after the user
    /// enabled the tier; nothing else may, and <b>nothing calls it at startup</b> (I10). It is
    /// idempotent, and two concurrent first calls produce ONE engine — two would be 254 MiB.
    ///
    /// <para><b>Two things E8.S4 has to know, because the signature hides both.</b> (1) It
    /// <b>blocks</b>: it runs <c>translator_initialize</c> — 82 ms measured, §7.6 constraint 3
    /// allows up to 500 — on the CALLING thread, and it waits on the same lock a frame in flight
    /// holds. Call it from a dispatcher tick and it is the freeze every other member of this class
    /// is shaped to avoid; the policy owns the <c>Task.Run</c>. (2) It <b>throws</b>
    /// <see cref="TranslationException"/> and reports to the gate when the model is gone or the
    /// engine will not come up — a lifetime hint with a typed failure and a soft window as side
    /// effects, which a timer callback must catch rather than let escape.</para>
    /// </summary>
    internal void Load()
    {
        lock (_sync) { LoadLocked(CancellationToken.None); }
    }

    /// <summary>
    /// <c>translator_free</c>, and the memory comes back: E8.S1 measured the working set returning
    /// to +6.0 MiB of a 121 MiB load and private bytes to +0.4 MiB of 367 MiB. Idempotent, and safe
    /// against a translate in flight because it takes the same lock that one holds.
    ///
    /// <para><b>There is no timer here and there must not be.</b> "Kept loaded while LIVE runs,
    /// unloaded after LIVE stops plus an idle window" is E8.S4's policy, which is unit-testable
    /// precisely because it owns an injected clock and this class owns none.</para>
    ///
    /// <para><b>It blocks, and E8.S4 pays for it.</b> "Safe against a translate in flight" is
    /// bought by waiting for that translate: this call sits on the lock for the length of the frame
    /// the engine is inside. Never from the dispatcher — the policy owns the <c>Task.Run</c>, the
    /// same way it owns the clock. It does not throw: see <see cref="SafeFree"/>.</para>
    /// </summary>
    internal void Unload()
    {
        lock (_sync)
        {
            var engine = _engine;
            Volatile.Write(ref _engine, null);
            // INSIDE the lock, and that is the whole point. `translator_free` is a native call like
            // the other two, and the binding is not documented thread-safe: freeing outside the lock
            // would let it run beside a `translator_initialize` or a `translator_translate` that a
            // waiting caller started the instant the field went null — which is exactly the
            // serialisation this lock exists to provide, given away for a few milliseconds of
            // latency on a path that is already tearing the engine down. It is also what makes
            // "a translate racing an Unload never touches a freed handle" true rather than nearly
            // true: the in-flight call holds this lock, so the free waits for it.
            SafeFree(engine);
        }
    }

    /// <summary><b>Not terminal, deliberately</b>, and E8.S4 is the one that has to know: this is
    /// <see cref="Unload"/> under another name, so a translate arriving afterwards brings a fresh
    /// engine up rather than throwing. That is what makes the load/unload cycle a capability instead
    /// of a one-shot — but it means a discarded instance can still be made to allocate 121 MiB that
    /// nothing holds a reference to unload again. Whoever retires this provider (E8.S5's chain
    /// rebuild) must drop every reference to it in the same breath.</summary>
    public void Dispose() => Unload();

    // =============================================================================================
    //  The native path — everything below runs on a pool thread, inside Task.Run
    // =============================================================================================

    private string TranslateOne(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string answer;
        lock (_sync)
        {
            // Again, and this one is not redundant: a caller that queued behind a whole frame can
            // have been cancelled while it waited on this lock, and "we do not start one you
            // cancelled" has to mean the 82 ms init as well as the translate.
            ct.ThrowIfCancellationRequested();
            var engine = LoadLocked(ct);
            ct.ThrowIfCancellationRequested();
            answer = Call(() => engine.Translate(text, false), ct);
        }

        // §5.3's other half: without it the soft strike count is cumulative-for-ever instead of
        // consecutive, and three BadResponses an hour apart would open a window on a healthy engine.
        Gate.ReportSuccess();

        // The honest half of the cancellation contract (T4): the native call itself is not
        // cancellable, so what this class promises is "we do not start one you cancelled, and we
        // discard the result of one you cancelled".
        ct.ThrowIfCancellationRequested();
        return answer;
    }

    private List<string> TranslateBatch(IReadOnlyList<string> lines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<string> answer;
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            var engine = LoadLocked(ct);
            ct.ThrowIfCancellationRequested();
            answer = Call(() => engine.TranslateBatch(lines), ct);
        }

        // I5. Not a pad, not a truncation, not a per-line retry: a BadResponse, which the gate
        // counts (three in a row open it) and the chain hands to the next tier.
        //
        // A null ELEMENT is the same verdict and it has to be, even though the count is right: the
        // read path substitutes the SOURCE line for a null answer, so letting one through would
        // render the untranslated Russian as if it were the English — a wrong answer shown
        // confidently, which is precisely what I5 exists to refuse.
        if (answer is null || answer.Count != lines.Count || answer.Any(static l => l is null))
            throw Reported(TranslationErrorKind.BadResponse);

        // Reported before the discard check for the same reason the failure above is: the engine
        // answered, and a cancel arriving afterwards is not evidence against it.
        Gate.ReportSuccess();
        ct.ThrowIfCancellationRequested();

        return answer.ToList();
    }

    /// <summary>
    /// The one place a native call is made, so the one place its failures are mapped. A raw native
    /// exception may never leave this class: the chain would send it through
    /// <c>ProviderErrorMapper</c> and read it as <see cref="TranslationErrorKind.Unknown"/>, and
    /// §4.1 says Unknown showing up in a field log is a bug report about the mapper.
    ///
    /// <para><b>I3 does not apply to this leg, and the code says so rather than leaving the next
    /// reader to wonder.</b> There is no <c>HttpClient</c> here and the engine cannot time out, so
    /// there is no <c>OperationCanceledException</c> whose token is NOT cancelled — the masquerade
    /// I3 exists to prevent cannot happen. The filter is written anyway, because a bare
    /// <c>catch (OperationCanceledException)</c> is wrong even where it would be harmless, and
    /// because the next person to read this file will be looking for it.</para>
    /// </summary>
    private T Call<T>(Func<T> nativeCall, CancellationToken ct)
    {
        try
        {
            return nativeCall();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TranslationException already)
        {
            // Already classified — a fake engine in a test, or a future wrapper that knows better
            // than this method does. Re-mapping it would be a second, disagreeing classifier; the
            // engine still has to go if the classification says it is gone.
            RetireLocked(already.Kind);
            throw;
        }
        catch (Exception ex)
        {
            // The engine is not THERE (the DLL never resolved, the entry point moved, the wrong
            // architecture) versus the engine is there and misbehaved. The first is Unavailable and
            // is what the resolver failing looks like; the second is a BadResponse, which the gate
            // counts three of before it opens.
            var kind = ex is DllNotFoundException or EntryPointNotFoundException
                            or BadImageFormatException or TypeInitializationException
                ? TranslationErrorKind.Unavailable
                : TranslationErrorKind.BadResponse;
            RetireLocked(kind);
            throw Reported(kind);
        }
    }

    /// <summary>
    /// A failure that says <b>the engine is gone</b> takes the handle with it. Without this the
    /// dead <see cref="IBergamotEngine"/> stays in <see cref="_engine"/>, <see cref="LoadLocked"/>
    /// keeps handing it to every later call, <see cref="IsLoaded"/> tells E8.S4 that 121 MiB is
    /// resident and well, and the tier can never come back inside the session even though a fresh
    /// <c>translator_initialize</c> is exactly the retry the gate's half-open window is about to ask
    /// for. A <see cref="TranslationErrorKind.BadResponse"/> is the other case and keeps its engine:
    /// the engine is there, it answered, it answered wrongly — three of those open the gate.
    ///
    /// <para>Called under <see cref="_sync"/>, which is why it may free here: nothing else can be
    /// inside a native call while this runs.</para>
    /// </summary>
    private void RetireLocked(TranslationErrorKind kind)
    {
        if (kind != TranslationErrorKind.Unavailable) return;

        var engine = _engine;
        Volatile.Write(ref _engine, null);
        SafeFree(engine);
    }

    /// <summary>
    /// <b>The load, once.</b> Called under <see cref="_sync"/> from every path that needs an engine,
    /// which is what makes "two concurrent first calls produce one engine" structural rather than
    /// hopeful.
    ///
    /// <para>Both of §7.6 constraint 7's failure modes live here, in the shape ruling <b>E8-c</b>
    /// gives them. §7.6 was written before E1 landed the typed errors: read literally it would have
    /// this class RETURN a <c>(</c>-prefixed placeholder, which no provider in this app does any
    /// more. What it protects is <b>I4 — nothing failed is ever cached</b> — and a throw satisfies
    /// that more strongly than a return, because <c>CachingTranslator</c> is never handed a value at
    /// all. The sentence the player reads is rendered upstream by <c>UserMessages</c>, with
    /// "Offline engine" substituted from the provider id.</para>
    /// </summary>
    private IBergamotEngine LoadLocked(CancellationToken ct)
    {
        var existing = _engine;
        if (existing is not null) return existing;

        // Failure mode 1 — the model is not there. E8.S5 builds this tier only if the setting is on
        // AND the model is present, so a missing model reaching this class is a race: the user
        // pressed Remove mid-session. It is reported to the gate for the same reason mode 2 is —
        // one 40-row frame must not ask forty times.
        //
        // The locator is E8.S3's code, called here: it is inside the try for the same reason the
        // factory is. A store that throws an IOException while it stats its directory must not send
        // a raw exception up the chain, where ProviderErrorMapper reads it as Unknown — and §4.1
        // says Unknown in a field log is a bug report about the mapper.
        string config;
        try
        {
            var directory = _modelDirectory();
            if (string.IsNullOrWhiteSpace(directory)) throw Reported(TranslationErrorKind.Unavailable);
            config = Path.Combine(directory, ConfigFileName);
            if (!File.Exists(config)) throw Reported(TranslationErrorKind.Unavailable);
        }
        catch (TranslationException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            throw Reported(TranslationErrorKind.Unavailable);
        }

        // Failure mode 2 — initialisation failed. Same Kind, same report: an engine that failed to
        // come up will fail again in 200 ms, and the gate's window is what stops the next line
        // paying for it.
        //
        // I3 AGAIN, and this catch is why the filter is worth writing where it "cannot fire": the
        // factory is an injected seam and E8.S3's store will do file work inside it, so an
        // OperationCanceledException CAN arrive here. Laundering a user cancel into a provider
        // failure would open a soft window and make the chain skip a healthy tier — the same
        // masquerade, running the other way.
        IBergamotEngine? engine;
        try
        {
            engine = _engineFactory(config);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Reported(TranslationErrorKind.Unavailable);
        }

        // A factory that answers null did not initialise anything. Storing it would leave a class
        // that believes it is loaded and NREs on the next line, mapped to BadResponse — a wrong
        // Kind for an engine that never ran.
        if (engine is null) throw Reported(TranslationErrorKind.Unavailable);

        Volatile.Write(ref _engine, engine);
        return engine;
    }

    /// <summary><c>translator_free</c> may not throw out of a teardown. If it ever does, the handle
    /// is already unreachable and the memory is the native side's problem — what must not happen is
    /// an exception escaping <see cref="Unload"/> or <see cref="IDisposable.Dispose"/>, which are
    /// the two members E8.S4's policy and E8.S5's chain rebuild call from paths that have no
    /// business handling one. It is the one native interaction outside <see cref="Call{T}"/>,
    /// because there is no caller left to hand a typed failure to.</summary>
    private static void SafeFree(IBergamotEngine? engine)
    {
        try { engine?.Dispose(); }
        catch (Exception) { /* the handle is gone from this class either way */ }
    }

    /// <summary>
    /// The pair check, and it happens <b>before anything is loaded</b>. Not reported to the gate:
    /// nothing is broken and nothing will heal in five seconds — "you did not install French" is a
    /// permanent fact about this installation, and opening a soft window for it would make the chain
    /// skip the tier for pairs that ARE installed.
    /// </summary>
    private void RequireInstalledPair(string source, string target)
    {
        foreach (var pair in _pairs)
            if (string.Equals(pair.Source, source, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.Target, target, StringComparison.OrdinalIgnoreCase))
                return;

        throw Failure(TranslationErrorKind.Unavailable);
    }

    /// <summary>
    /// A typed failure carrying this provider's id — and <b>never <c>NotSent</c></b> (ruling E8-c).
    /// <c>ChainTranslatorTests.NotSent_is_written_in_exactly_one_place</c> asserts exactly one
    /// writer, <c>HttpProviderCore</c>, and it is right to: ruling E3-b gives the flag the meaning
    /// "the gate refused before anything left the machine", which is a statement about a REQUEST. A
    /// local engine never makes one.
    /// </summary>
    private static TranslationException Failure(TranslationErrorKind kind) =>
        new(kind, UserMessages.Sentence(kind, ProviderId) ?? "The offline engine is not available.",
            null, ProviderId);

    /// <summary>The same failure, with the breaker told about it first: one report opens a soft
    /// window, <see cref="ChainTranslator"/> skips the tier for its duration without an exception at
    /// all, and its half-open probe re-arms the tier for free. The report comes first so a caller
    /// that catches the throw cannot observe a gate that has not heard yet.</summary>
    private TranslationException Reported(TranslationErrorKind kind)
    {
        Gate.ReportFailure(kind);
        return Failure(kind);
    }
}
