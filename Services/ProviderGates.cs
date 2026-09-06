using System.Collections.Concurrent;

namespace PWRUHelper.Services;

/// <summary>
/// The vocabulary of the whole rebuild: the id of every provider that can own a gate, spelled
/// <b>once</b>. A second spelling of an id is not a typo that fails loudly — it is a silently
/// duplicated gate, so the Translator tab keeps hammering the provider LIVE was told to leave
/// alone, which is the exact bug <c>ProviderGates</c> exists to fix.
///
/// <para><c>google-gtx</c> is today's endpoint's id <i>before</i> the rename in E3.S6 — E1.S5's log
/// line already writes it, so the field stays stable across the rename. <c>bergamot</c> is declared
/// even though E8 may never merge: an id costs nothing, a second spelling of one costs a gate.</para>
/// </summary>
internal static class ProviderIds
{
    internal const string GoogleDict = "google-dict";
    internal const string Edge = "edge";
    internal const string GoogleGtx = "google-gtx";
    internal const string DeepL = "deepl";
    internal const string Azure = "azure";
    internal const string Bergamot = "bergamot";

    /// <summary>Every id, in the chain order of §8.1 — for the E7 status list and for the tests that
    /// pin the set. It is not the chain itself: composition is E3's.</summary>
    internal static readonly string[] All =
    {
        GoogleDict, Edge, GoogleGtx, DeepL, Azure, Bergamot,
    };
}

/// <summary>
/// The process-global registry of provider gates: <see cref="For"/> hands the <b>same</b>
/// <see cref="ProviderGate"/> to everyone asking for the same id, for the life of the process (I9).
///
/// <para><b>Why static</b> (§5.1, restated here so no story re-opens it). The state a gate holds
/// mirrors an <i>external, process-independent</i> condition — an IP-scoped counter on Google's
/// side. It is not per-window, per-chain or per-request state, so a process-global singleton is the
/// honest model rather than a convenience. And the alternative would have to be threaded through a
/// field initializer that runs before <c>_settings</c> is even loaded (<c>MainWindow.xaml.cs:43</c>
/// vs <c>:50</c>) and through <c>BuildTranslator()</c>, which runs again on every key save
/// (<c>MainWindow.Translate.cs:239</c>) — an ordering hazard that has already produced bugs here.</para>
///
/// <para><b>The cost, paid deliberately.</b> Static state is shared across xUnit's parallel
/// collections, so every test that touches this type lives in the non-parallel <c>Gates</c>
/// collection whose fixture calls <see cref="ResetForTests"/> before and after each case (R4/R-08,
/// IS-4/IS-5). The same three seams this repo already uses twice —
/// <c>SettingsService.PathOverride</c> (<c>SettingsService.cs:95</c>) and
/// <c>Logging.DirectoryOverride</c> (<c>Logging.cs:31-39</c>), both added after the suite wrote to a
/// developer's real <c>%AppData%</c> — are here from the first line rather than after the third
/// incident.</para>
///
/// <para><b>Not on the startup path</b> (I10): nothing here is referenced from the
/// <c>MainWindow</c> constructor, <c>ApplySettings</c> or <c>OnWindowLoaded</c>, and this type's
/// static initialiser does no I/O. <c>provider-state.json</c> is read lazily on the first
/// <c>TryEnter</c> of the process and written debounced — both are <b>E2.S4</b>; this story lands
/// the seams they fill in (<see cref="PathOverride"/>, <see cref="CancelPendingSave"/>,
/// <see cref="TransitionHook"/>) so no test can ever be written that touches the real
/// <c>%AppData%</c>.</para>
///
/// <para><b>No events</b> (ruling R-2/OQ-c): the UI polls <see cref="Snapshot"/> / <see cref="All"/>
/// at 1 Hz from the countdown timer it already runs. There is no <c>StateChanged</c>, no
/// <c>INotifyPropertyChanged</c> and no callback list — <see cref="TransitionHook"/> is one internal
/// delegate for E2.S4/E2.S6, and nothing outside <c>Services/</c> may set it.</para>
/// </summary>
internal static class ProviderGates
{
    // Ordinal on purpose: ids are ASCII constants from ProviderIds, never user text. A culture-aware
    // comparison here would be a per-lookup cost on the request path for no possible benefit.
    private static readonly ConcurrentDictionary<string, ProviderGate> Registry = new(StringComparer.Ordinal);

    /// <summary>Where <c>provider-state.json</c> is read from / written to (IS-1). Tests point this
    /// at a temp directory through <c>TempGateState</c>; the store itself is <b>E2.S4</b>, and this
    /// property exists first so that story cannot be written without it. Null = the real file.</summary>
    internal static string? PathOverride;

    /// <summary>The default location, computed on demand rather than in a static field: this type's
    /// static initialiser must do no I/O and must cost nothing at startup (I10). Not cached — it is
    /// read a handful of times per session, by E2.S4's load and save.</summary>
    private static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PWRUHelper", "provider-state.json");

    /// <summary>The file E2.S4's store will actually use. Exposed so the suite's own guard case can
    /// assert it is never the developer's real <c>%AppData%</c> path — the same guard
    /// <c>LoggingTests.The_test_run_never_writes_to_the_real_AppData_log</c> is.</summary>
    internal static string StatePath => PathOverride ?? DefaultPath;

    private static Func<DateTimeOffset> _clock = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// The one source of "now" for the registry <b>and</b> every gate it hands out (IS-6). They must
    /// be the same clock or a <c>blockedUntil</c> restored from disk gets compared against a
    /// different now. Gates hold <see cref="Now"/> — this property read by indirection — rather than
    /// the delegate's current value, so setting <c>Clock</c> after gates already exist is honoured
    /// by those gates too (which is the order a test naturally writes).
    /// </summary>
    internal static Func<DateTimeOffset> Clock
    {
        get => _clock;
        set => _clock = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The stable delegate every gate is built with; it reads <see cref="Clock"/> at call
    /// time, never at construction time.</summary>
    private static DateTimeOffset Now() => _clock();

    /// <summary>
    /// The gate for <paramref name="providerId"/>, created on first use and the same instance for
    /// every later caller — that identity <i>is</i> I9. Thread-safe because both chains, the LIVE
    /// loop and the Translator tab call it concurrently; <c>GetOrAdd</c> may run the factory more
    /// than once under contention but publishes exactly one instance, which is the property that
    /// matters (a lock would be equally correct and slower on the request path).
    /// </summary>
    internal static ProviderGate For(string providerId) =>
        Registry.GetOrAdd(providerId, static _ => new ProviderGate(Now));

    /// <summary>
    /// The user re-saved this provider's key, so the two blocks a key can cause are lifted
    /// (§5.3; DeepL today at <c>MainWindow.Translate.cs:235-244</c>, Azure in E6.S3). Wiring the
    /// call sites is not this story — the entry point is. A provider that has never had a gate has
    /// nothing to clear, and a poll-shaped API must not create one, so this does <b>not</b> go
    /// through <see cref="For"/>.
    /// </summary>
    internal static void ClearAuthBlock(string providerId)
    {
        if (Registry.TryGetValue(providerId, out var gate)) gate.ClearAuthBlock();
    }

    /// <summary>
    /// What the UI polls at 1 Hz (ruling R-2). <c>null</c> means no gate exists for that id — nothing
    /// has ever been asked of the provider — which a caller reads exactly as "closed"; deliberately
    /// not "create one and report it closed", or the status poll would populate the registry with
    /// gates for providers the user does not even have configured.
    /// </summary>
    internal static GateSnapshot? Snapshot(string providerId) =>
        Registry.TryGetValue(providerId, out var gate) ? gate.Snapshot() : null;

    /// <summary>Every live gate's state in one pass, for the E7 status list. A point-in-time copy:
    /// the gates keep moving after it is taken.</summary>
    internal static IReadOnlyDictionary<string, GateSnapshot> All()
    {
        var map = new Dictionary<string, GateSnapshot>(Registry.Count, StringComparer.Ordinal);
        foreach (var pair in Registry) map[pair.Key] = pair.Value.Snapshot();
        return map;
    }

    /// <summary>
    /// The one seam E2.S4 (debounced atomic save) and E2.S6 (the transition log line) fill in.
    /// Deliberately a single internal delegate and not an event or a callback list: ruling R-2 keeps
    /// events out of <c>Services/</c>, and nothing outside <c>Services/</c> may set this — the UI
    /// polls. Unwired today, like <c>GateOutcome.Wait</c> and <c>RequestPriority</c> in E2.S1: this
    /// story adds no persistence and no logging (I11).
    /// </summary>
    internal static Action<string, GateSnapshot>? TransitionHook;

    /// <summary>Announce a transition to whatever E2.S4/E2.S6 installed. Exception-free by contract:
    /// a save or a log line must never be able to fail a translation.</summary>
    internal static void NoteTransition(string providerId, GateSnapshot snapshot)
    {
        var hook = TransitionHook;                  // read once: it can be replaced concurrently
        if (hook == null) return;
        try { hook(providerId, snapshot); } catch { /* observability must not break the request */ }
    }

    /// <summary>
    /// IS-4. Puts the process back where it started: no gates, the wall clock, the real state file
    /// and no hook. <b>Both halves matter</b> — clearing the registry alone would still let a save
    /// queued by one case land during the next, which is why <see cref="CancelPendingSave"/> is
    /// called here and not only in E2.S4.
    /// </summary>
    internal static void ResetForTests()
    {
        Registry.Clear();
        _clock = () => DateTimeOffset.UtcNow;
        PathOverride = null;                        // belt to TempGateState's braces (IS-1/IS-3)
        TransitionHook = null;
        CancelPendingSave();
    }

    /// <summary>
    /// The second half of IS-4, landed now so E2.S4 only fills it in. Nothing is queued in this
    /// story — the debounced save arrives with the store — and the method is called from
    /// <see cref="ResetForTests"/> already, so the day a timer exists there is exactly one place to
    /// cancel it and no test to remember to update.
    /// </summary>
    private static void CancelPendingSave()
    {
        // E2.S4: cancel the 1 s debounce timer and drop whatever it was about to write.
    }
}
