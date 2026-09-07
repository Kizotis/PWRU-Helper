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
    /// pin the set. It is not the chain itself: composition is E3's. Read-only by type and not just
    /// by convention: a <c>static readonly string[]</c> is mutable static state, which is the R4 /
    /// R-08 class this whole story is about containing, and one sorted array in one test would leak
    /// across the assembly.</summary>
    internal static readonly IReadOnlyList<string> All = new[]
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
/// honest model rather than a convenience. And the alternative would have to be threaded through
/// <c>MainWindow</c>'s constructor, which builds three chains before <c>InitializeComponent()</c>
/// (E3.S7), and through <c>BuildWriteChain()</c>, which runs again on every key save
/// (<c>MainWindow.Translate.cs</c>'s DeepL key handler) — an ordering hazard that has already
/// produced bugs here.</para>
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
/// <c>MainWindow</c> constructor, <c>ApplySettings</c> or <c>OnWindowLoaded</c> — a source scan
/// (TP-START-02) keeps it that way — and this type's static initialiser does no I/O.
/// <c>provider-state.json</c> is read lazily on the first <c>TryEnter</c> of the process
/// (<see cref="EnsureLoaded"/>) and written debounced on transitions (<see cref="QueueSave"/>,
/// <see cref="Flush"/>). <c>OnClosing</c>'s <see cref="Flush"/> is the one permitted reference to
/// this type outside <c>Services/</c> (ruling E2-e).</para>
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
        Registry.GetOrAdd(providerId, id => new ProviderGate(
            Now, null,
            // E2.S4's two seams. The load hangs off TryEnter and NOT off this factory: For runs
            // inside MainWindow's field initializer (MainWindow.xaml.cs:43), before first paint, so
            // reading the file here would break I10 and TP-START-01. The transition callback closes
            // over the id because a gate does not know its own — the registry is the only thing that
            // does.
            EnsureLoaded,
            (from, to) => NoteTransition(id, from, to)));

    // ---- §5.7: the file, read once and written debounced (E2.S4) -------------------------------

    /// <summary>One lock for the load and the save both. Two would be one lock order to get wrong:
    /// the flush walks every gate and takes their locks, and a gate announces a transition — which
    /// queues a save — only after releasing its own.</summary>
    private static readonly object Sync = new();

    private static bool _loaded;                 // has this process read provider-state.json yet?
    private static bool _savePending;            // a transition is waiting for the debounce to flush
    private static System.Threading.Timer? _saveTimer;
    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement>? _unknown;
    private static bool _keepFile;               // the file could not be read, or a newer build wrote it

    /// <summary>The §5.7 debounce, as a settable number rather than a constant so a test can prove
    /// coalescing without waiting a real second (CI-3: no <c>Task.Delay</c> anywhere). Production
    /// never touches it; <see cref="ResetForTests"/> puts it back.</summary>
    internal static int SaveDebounceMs = 1000;

    /// <summary>
    /// Read <c>provider-state.json</c>, once per process, and seed the gates with what it holds.
    /// Called from <see cref="ProviderGate.TryEnter"/> — <b>the first translation request of the
    /// session</b> — and from nowhere else (AC 2, I10, ruling E2-e).
    ///
    /// <para><b>"Off the UI thread", honestly.</b> <c>TryEnter</c> is synchronous, so this read
    /// happens on whichever thread issues that first request. E2.S5's <c>HttpProviderCore</c> is what
    /// makes that a pool thread (it <c>ConfigureAwait(false)</c>s), and until E2.S5 lands the read
    /// may fall on the dispatcher. That is accepted rather than hidden: it is a single sub-kilobyte
    /// read of a directory <c>SettingsService</c> has already created, on a user-initiated action,
    /// long after first paint — which is exactly what I10 protects. Warming it from
    /// <c>OnWindowLoaded</c> was explicitly rejected: it would put this type on a startup path and
    /// fail TP-START-02's scan.</para>
    ///
    /// <para>Synchronous under <see cref="Sync"/>, deliberately: the file is a few hundred bytes, and
    /// a second thread arriving mid-load must wait rather than translate against a gate that is about
    /// to be seeded under it. After the first request it is one <c>Volatile.Read</c> on the hot path.</para>
    /// </summary>
    internal static void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded)) return;

        lock (Sync)
        {
            if (_loaded) return;

            var state = ProviderStateStore.Load(StatePath);   // never throws; empty on any failure
            _unknown = state.Unknown;                         // re-emitted verbatim on the next write
            _keepFile = state.KeepFile;                       // unreadable, or a newer build's

            var normalisedAny = false;
            foreach (var pair in state.Providers)
            {
                // For(id), not a fresh gate: MainWindow's field initializer may already have handed
                // this id's gate to a chain, and seeding a different instance would restore the pause
                // into an object nobody consults. TrySeedState declines a gate that has already
                // recorded something in this process — live evidence beats a file.
                var gate = For(pair.Key);
                if (!gate.TrySeedState(pair.Value, out var normalised)) continue;
                if (normalised) normalisedAny = true;

                // Ruling E2-b's fifth logged edge (E2.S6). It is written here, and NOT announced
                // through NoteTransition, because a seed is not a §5.2 transition — nothing moved,
                // the gate was born in that state — and announcing it as one would queue a debounced
                // save on every single start, which is the churn §5.7 refuses. Whether the file needs
                // correcting is a separate question, answered by `normalisedAny` below.
                GateLog.Reload(pair.Key, gate.Snapshot());
            }

            Volatile.Write(ref _loaded, true);

            // Reading the file changed something in it — a window clamped back from a clock jump, a
            // nonsense strike count, an AuthFailed this build may not keep. Write the corrected
            // state back, because NOTHING ELSE WILL: a gate that is merely refusing callers takes
            // no transition and so queues no save, and the next launch would read the same
            // dangerous value and clamp it again. A clock set forward once would otherwise re-impose
            // a full 30-minute pause at every single start, for ever — R-01 arrived at through the
            // file, and the lockout "without a way back" that ruling E2-a forbids.
            if (normalisedAny) QueueSave();
        }
    }

    /// <summary>
    /// A gate changed state, so the file is stale. Debounced by 1 s and coalesced (§5.7): N
    /// transitions inside the window produce <b>one</b> write, of the state at flush time. A
    /// <c>System.Threading.Timer</c> and not a <c>DispatcherTimer</c> — <c>Services/</c> is UI-free
    /// (I2) — and the window is fixed from the first pending transition rather than restarted by
    /// each one, so a busy minute cannot postpone the write indefinitely.
    /// </summary>
    private static void QueueSave()
    {
        lock (Sync)
        {
            if (_savePending) return;                        // already inside a window: coalesced
            _savePending = true;
            _saveTimer ??= new System.Threading.Timer(static _ => Flush());
            _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Write the pending state now: the debounce timer's own callback, and the call
    /// <c>MainWindow.OnClosing</c> makes so a pause the user is waiting out survives the restart
    /// (<c>MainWindow.xaml.cs:229</c> — the one <c>ProviderGates</c> reference outside
    /// <c>Services/</c>, ruling E2-e). It is also the seam every persistence test uses instead of
    /// waiting a real second.
    ///
    /// <para>Nothing pending means nothing to do: the file already says what the gates say, and
    /// rewriting it on every close would be disk churn for the many sessions that never see a
    /// failure. Best-effort and non-blocking, like everything else on the close path — the store
    /// swallows its own I/O failures and this swallows anything else.</para>
    /// </summary>
    internal static void Flush()
    {
        try
        {
            lock (Sync)
            {
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                if (!_savePending) return;
                _savePending = false;

                // Read before write, so an id this build knows nothing about survives a rewrite
                // (AC 5) even when the first transition of the session beat the first TryEnter.
                // A no-op after the first request, which is when the load normally happens.
                EnsureLoaded();

                // The file is not ours to replace: we could not read it (an AV or a sync agent had
                // it open for the 50 ms of the session's first request), or a newer build wrote a
                // version this one does not understand. Writing from an empty registry would erase
                // a standing pause and every preserved unknown id — the AC 5 case — to save state
                // worth one request. Not persisting this session is the cheaper failure by far.
                if (_keepFile) return;

                var providers = new Dictionary<string, ProviderStateRecord>(StringComparer.Ordinal);
                foreach (var pair in Registry)
                    if (pair.Value.ExportState() is { } record) providers[pair.Key] = record;

                // Nothing to persist and no file yet ⇒ do not create one. Not every transition
                // changes a persisted byte: an AuthFailed is a real §5.2 edge that E2.S6 must log,
                // yet E2-a keeps it off the disk entirely, and the ClearAuthBlock that lifts it is
                // another. Without this line those two write `"providers": {}` into a fresh
                // %AppData%\provider-state.json for a session with nothing whatever to remember —
                // the same disk churn this method already refuses when nothing is pending.
                // An EXISTING file is still rewritten: it may hold state that has since expired,
                // and "the gates now say nothing" is exactly what has to reach it.
                if (providers.Count == 0 && (_unknown is null || _unknown.Count == 0)
                    && !System.IO.File.Exists(StatePath)) return;

                ProviderStateStore.Save(StatePath, providers, _unknown);
            }
        }
        catch { /* persistence must never be able to fail a translation or a window close */ }
    }

    /// <summary>
    /// The user re-saved this provider's key, so the two blocks a key can cause are lifted
    /// (§5.3; DeepL today at <c>MainWindow.Translate.cs:235-244</c>, Azure in E6.S3). Wiring the
    /// call sites is not this story — the entry point is. A provider that has never had a gate has
    /// nothing to clear, and a poll-shaped API must not create one, so this does <b>not</b> go
    /// through <see cref="For"/>.
    ///
    /// <para>Routing only: <b>which</b> blocks a key may lift is ruling E2-i and lives in
    /// <see cref="ProviderGate.ClearAuthBlock"/> — the account-scoped rows (<c>AuthFailed</c>,
    /// <c>QuotaExhausted</c>) and no other. Saving a DeepL key never touches Azure's gate, and it
    /// never lifts DeepL's own 429 window either.</para>
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
    /// <para>The arguments are what both consumers need and no more: the provider <b>id</b> (a gate
    /// does not know its own — the registry is the only thing that does), the state it came
    /// <b>from</b>, and the whole snapshot it landed on, which carries the new state, the reason
    /// (<c>LastKind</c>), the strikes and the <c>blockedUntil</c>. That is exactly E2.S6's
    /// <c>OnTransition(from, to, kind, strikes, blockedUntil)</c> and exactly what E2.S4 needs to
    /// decide whether a transition is one of the four §5.2 edges worth a write.</para>
    /// <para><b>E2.S6 fills it in, here</b> — as a field initialiser and not from any startup path.
    /// The registry is the only thing that knows an id, so the log line has to hang off the registry;
    /// installing it from <c>MainWindow</c> would put this type on a startup path and fail
    /// TP-START-02's scan (I10), and installing it lazily would lose the transitions that happen
    /// before whatever triggers the installation. A field initialiser costs one delegate and touches
    /// no disk: <see cref="GateLog"/>'s own initialiser allocates a suppressor and nothing else.
    /// <see cref="ResetForTests"/> puts this default back rather than nulling it, so a case that
    /// stubs the hook cannot leave the app silent for the rest of the run.</para>
    /// </summary>
    internal static Action<string, GateState, GateSnapshot>? TransitionHook = GateLog.Note;

    /// <summary>Announce a transition to whatever E2.S4/E2.S6 installed. Exception-free by contract:
    /// a save or a log line must never be able to fail a translation.</summary>
    internal static void NoteTransition(string providerId, GateState from, GateSnapshot to)
    {
        // The save is queued by the registry ITSELF and not by a subscriber (ruling R-2/OQ-c):
        // hanging it off TransitionHook would mean E2.S6's log line replaces persistence the day it
        // installs one, and a test that stubs the hook would silently stop the app saving.
        //
        // In a try, because this method's contract is that it is exception-free: QueueSave touches
        // a Timer, and Timer.Change throws on a negative period. A save may never fail a request.
        try { if (WorthAWrite(from, to)) QueueSave(); }
        catch { /* persistence must never be able to fail a translation */ }

        var hook = TransitionHook;                  // read once: it can be replaced concurrently
        if (hook == null) return;
        try { hook(providerId, from, to); } catch { /* observability must not break the request */ }
    }

    /// <summary>
    /// Whether a transition can have changed a byte of §5.7's record that is worth the disk.
    /// <b>Worth a write is not the same question as worth a log line</b>, and this is where the two
    /// part: the gate announces every §5.2 edge, E2.S6 filters that stream for ruling E2-b, and the
    /// write is filtered here. Two edges buy the file nothing.
    ///
    /// <para><b>Open → HalfOpen.</b> The probe latch is deliberately <i>not</i> persisted (§5.7,
    /// I9), and a probe grant moves nothing else — not the window, not the strikes, not the kind.
    /// The record is byte-for-byte identical either side of it, so the write was pure churn on a
    /// path that is otherwise pure arithmetic.</para>
    ///
    /// <para><b>Anything landing on a soft window</b> (§5.3's <c>Unavailable</c>/<c>Timeout</c>/
    /// <c>Network</c>/<c>Unknown</c>). The gate already suppresses the first soft cooldown; this is
    /// the one that came back through the cycle the cooldown itself creates — five seconds later a
    /// caller is granted a probe, the probe times out, five seconds later again — which rewrites
    /// the file every few seconds for as long as a provider is unreachable, while §5.7 budgets "a
    /// handful per session" and the footprint rule forbids the rest. A five-second window is worth
    /// nothing at all after a restart.</para>
    ///
    /// <para>Landing on <b>Closed</b> is always written, whatever the kind: that edge is the pause
    /// <i>ending</i>, and a file that keeps a block the provider has already answered out of is
    /// R-01. The one thing this rule can drop is a strike reset that happens to coincide with a
    /// failed soft probe; it costs the ladder one rung and the next clean run restores it.</para>
    /// </summary>
    private static bool WorthAWrite(GateState from, GateSnapshot to) =>
        !(from == GateState.Open && to.State == GateState.HalfOpen)
        && !(to.State == GateState.Open
             && to.LastKind is TranslationErrorKind.Unavailable or TranslationErrorKind.Timeout
                            or TranslationErrorKind.Network or TranslationErrorKind.Unknown);

    /// <summary>
    /// IS-4. Puts the process back where it started: no gates, the wall clock, the real state file
    /// and no hook. <b>Both halves matter</b> — clearing the registry alone would still let a save
    /// queued by one case land during the next, which is why <see cref="CancelPendingSave"/> is
    /// called here and not only in E2.S4.
    /// </summary>
    internal static void ResetForTests()
    {
        CancelPendingSave();                        // FIRST: nothing queued may outlive this line
        Registry.Clear();
        _clock = () => DateTimeOffset.UtcNow;
        PathOverride = null;                        // belt to TempGateState's braces (IS-1/IS-3)
        TransitionHook = GateLog.Note;              // the production default, not null (E2.S6)
        GateLog.ResetSuppression();                 // the storm valve's run is process-wide too
        SaveDebounceMs = 1000;
        // the next case reloads
        lock (Sync) { Volatile.Write(ref _loaded, false); _unknown = null; _keepFile = false; }
    }

    /// <summary>
    /// The second half of IS-4: cancel the debounce timer and drop whatever it was about to write.
    /// Without it a save queued by one case lands during the next one — in the next case's temp
    /// directory or, worse, after <see cref="PathOverride"/> has gone back to null and the developer's
    /// own <c>%AppData%</c> is live again. That is the failure mode the whole <c>Gates</c> collection
    /// exists to prevent, and it is why this runs before the registry is even cleared.
    /// </summary>
    private static void CancelPendingSave()
    {
        lock (Sync)
        {
            _savePending = false;                   // a callback already past its Change() sees this
            var timer = _saveTimer;
            _saveTimer = null;
            timer?.Dispose();
        }
    }
}
