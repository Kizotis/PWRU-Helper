using System.IO;
using System.Linq;
using System.Threading;
using PWRUHelper;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// <b>E8.S4 — amendment A-1(b): kept loaded while LIVE runs, unloaded after it stops.</b>
/// TP-BRG-05 and the behavioural half of TP-BRG-06 (the RSS half is E8.S7's, on the P1-affected
/// machines, and the spike's own working-set case is in <c>BergamotSpike</c> under
/// <c>PWRU_SPIKE=1</c>).
///
/// <para><b>Nothing here sleeps and nothing here downloads</b> (CI-3, CI-8). Every window is
/// arithmetic on an injected clock — the shape <c>ProviderGate</c>, <c>LiveTickPolicy</c> and
/// <c>CountdownTests</c> all use — and the engine is <see cref="FakeBergamotEngine"/>, so the suite
/// never loads a 22 MB native library to prove a policy about ten minutes.</para>
///
/// <para>It joins the non-parallel <c>Gates</c> collection twice over: the provider's default gate
/// is the registry's, and the two STA cases build a real <c>MainWindow</c> (E7.S8's finding).</para>
/// </summary>
[Collection("Gates")]
public class BergamotLifetimeTests : GatesTestBase
{
    private static readonly DateTimeOffset Start = new(2026, 9, 7, 20, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(TranslationPolicy.IdleUnloadMinutes);

    /// <summary>A clock a case advances by hand (IS-6). Not a stopwatch, not <c>UtcNow</c>: the
    /// whole point of extracting this policy was that "ten minutes have passed" becomes an
    /// assignment.</summary>
    private sealed class FakeClock
    {
        // Ticks and not a DateTimeOffset field: the interleaving case below advances this from six
        // threads while the provider reads it, and a 16-byte struct has no atomic assignment — a
        // torn read there would be a test failing for a reason that is not the code's.
        private long _ticks = Start.UtcTicks;
        internal DateTimeOffset Now => new(Volatile.Read(ref _ticks), TimeSpan.Zero);
        internal DateTimeOffset Read() => Now;
        internal void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>A model directory that exists and holds the config file the provider looks for. The
    /// fake engine never opens it — its existence is the only disk question this file asks.</summary>
    private sealed class TempModel : IDisposable
    {
        private readonly DirectoryInfo _dir;

        public string Path => _dir.FullName;

        public TempModel()
        {
            _dir = Directory.CreateTempSubdirectory("pwru-lifetime-");
            File.WriteAllText(System.IO.Path.Combine(_dir.FullName, BergamotTranslator.ConfigFileName),
                              "relative-paths: true\n");
        }

        public void Dispose()
        {
            try { _dir.Delete(recursive: true); } catch { /* the test already made its point */ }
        }
    }

    private static BergamotTranslator Provider(FakeBergamotEngineFactory factory, TempModel model,
                                               BergamotLifetime? lifetime)
        => new(modelDirectory: () => model.Path,
               nativeDirectory: () => null,
               engineFactory: factory.Create,
               gate: new ProviderGate(),
               lifetime: lifetime);

    // =============================================================================================
    //  Case 1 — LIVE running: the model stays, however long the gap (AC 1, AC 3, TP-BRG-05)
    // =============================================================================================

    /// <summary>
    /// <b>The row a naive implementation gets wrong.</b> A LIVE session whose every online tier is
    /// inside a 30-minute gate window has no offline translation for half an hour; a plain "unload
    /// after ten idle minutes" would free the model in the middle of it and pay the 82 ms init again
    /// on resume. A-1(b) exists to forbid exactly that, so the clock is advanced by ten TIMES the
    /// timeout and the answer is still no.
    /// </summary>
    [Fact]
    public void While_LIVE_runs_nothing_is_unloaded_however_long_the_gap()
    {
        var clock = new FakeClock();
        var live = true;
        var policy = new BergamotLifetime(() => live, clock.Read, Timeout);

        clock.Advance(Timeout * 10);

        Assert.False(policy.ShouldUnload(isLoaded: true));
        // …and it is the LIVE clause doing the work, not a long window: the same instant with the
        // loop stopped is an unload.
        live = false;
        Assert.True(policy.ShouldUnload(isLoaded: true));
    }

    /// <summary>The same property through the provider, which is where it has to hold: a whole
    /// LIVE session's worth of ticks with a gate-window gap between them, and
    /// <c>translator_free</c> is never called. One engine, from first tick to last.</summary>
    [Fact]
    public async Task A_LIVE_session_with_long_gaps_keeps_the_one_engine_it_loaded()
    {
        var clock = new FakeClock();
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model, new BergamotLifetime(() => true, clock.Read, Timeout));

        await provider.TranslateAsync("привет", "ru", "en");
        var engine = factory.Last!;

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(Timeout * 3);            // the loop was paused for three whole windows
            await provider.TranslateLinesAsync(new[] { "а" + i, "б" + i }, "ru", "en");
        }

        Assert.True(provider.IsLoaded);
        Assert.Equal(1, factory.Initialisations);
        Assert.Equal(0, engine.Disposals);
    }

    // =============================================================================================
    //  Cases 2 & 3 — LIVE stopped: the boundary, and where an off-by-one lives (AC 4, TP-BRG-06)
    // =============================================================================================

    [Theory]
    // Just under: still loaded. This is the boundary the story names, and a `>` written where a
    // `>=` belongs moves exactly this line.
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void With_LIVE_stopped_the_window_is_inclusive_at_its_edge(int secondsPastTheTimeout, bool unload)
    {
        var clock = new FakeClock();
        var policy = new BergamotLifetime(() => false, clock.Read, Timeout);

        clock.Advance(Timeout + TimeSpan.FromSeconds(secondsPastTheTimeout));

        Assert.Equal(unload, policy.ShouldUnload(isLoaded: true));
        // Nothing resident is never an unload, at any instant: the policy can only ever say "free
        // what is there" (I10 — it cannot cause a load, and it cannot free what was never loaded).
        Assert.False(policy.ShouldUnload(isLoaded: false));
    }

    /// <summary>Case 2 through the provider: the window elapses with LIVE stopped and
    /// <c>translator_free</c> runs <b>once</b>, on the engine that was really there.</summary>
    [Fact]
    public async Task With_LIVE_stopped_the_elapsed_window_frees_the_engine_once()
    {
        var clock = new FakeClock();
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model, new BergamotLifetime(() => false, clock.Read, Timeout));

        await provider.TranslateAsync("привет", "ru", "en");
        var engine = factory.Last!;
        Assert.True(provider.IsLoaded);

        // Not yet: the player may be between two Translator-tab lines.
        clock.Advance(Timeout - TimeSpan.FromSeconds(1));
        Assert.False(provider.UnloadIfIdle());
        Assert.True(provider.IsLoaded);
        Assert.Equal(0, engine.Disposals);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(provider.UnloadIfIdle());

        Assert.False(provider.IsLoaded);
        Assert.Equal(1, engine.Disposals);

        // …and the capability really is a capability: the next line brings a fresh engine up, which
        // is what makes "a second load costs the same as the first" a question worth asking of the
        // real one (BergamotSpike).
        await provider.TranslateAsync("пока", "ru", "en");
        Assert.Equal(2, factory.Initialisations);
        Assert.True(provider.IsLoaded);
    }

    // =============================================================================================
    //  Case 4 — a translation arriving first defers the unload (RecordUse)
    // =============================================================================================

    [Fact]
    public void A_use_defers_the_unload_even_after_the_window_has_elapsed()
    {
        var clock = new FakeClock();
        var policy = new BergamotLifetime(() => false, clock.Read, Timeout);

        clock.Advance(Timeout * 2);
        Assert.True(policy.ShouldUnload(isLoaded: true));

        policy.RecordUse();                         // a Translator-tab line landed on the offline tier

        Assert.False(policy.ShouldUnload(isLoaded: true));
        Assert.Equal(Timeout, policy.RemainingIdle());
        Assert.Equal(clock.Now, policy.LastUse);

        // …and the deferral is a deferral, not a cancellation: the window starts again from the use.
        clock.Advance(Timeout);
        Assert.True(policy.ShouldUnload(isLoaded: true));
    }

    /// <summary>
    /// The same through the provider, and this is the half that could have been wired backwards: a
    /// translate must RECORD a use, so a line that lands a minute before the one-shot fires pushes
    /// the free a whole window out instead of being freed out from under the next one. The
    /// intervals are deliberately just inside the window — a Translator-tab user typing every nine
    /// minutes — which is the shape in which "the timer fires and finds nothing to do" happens.
    /// </summary>
    [Fact]
    public async Task A_translation_records_a_use_and_moves_the_window()
    {
        var clock = new FakeClock();
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model, new BergamotLifetime(() => false, clock.Read, Timeout));

        await provider.TranslateAsync("привет", "ru", "en");
        var engine = factory.Last!;

        clock.Advance(Timeout - TimeSpan.FromMinutes(1));
        await provider.TranslateAsync("пока", "ru", "en");     // one minute short: nothing is swept

        // The one-shot armed at the first line would fire about now and find the window has moved.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(provider.UnloadIfIdle());
        Assert.True(provider.IsLoaded);
        Assert.Equal(0, engine.Disposals);
        Assert.Equal(1, factory.Initialisations);            // one engine across both lines

        // …and deferring is not cancelling: a full window after the LAST use, it goes.
        clock.Advance(Timeout - TimeSpan.FromMinutes(1));
        Assert.True(provider.UnloadIfIdle());
        Assert.Equal(1, engine.Disposals);
    }

    // =============================================================================================
    //  E8.S5 — the decline race E8.S4 recorded: deferring must not mean forgetting
    // =============================================================================================

    /// <summary>
    /// <b>The gap, closed at the provider end.</b> The one-shot asks "has the window elapsed?" on
    /// the dispatcher and the pool asks it again an instant later, under the provider's lock. A
    /// translation landing in between moves the window, <c>UnloadIfIdle</c> declines — and before
    /// E8.S5 it declined into silence, so the engine stayed resident until the next call into the
    /// provider or until <c>OnClosing</c>. It now answers with the idle it decided on, computed
    /// inside the same lock as the decision, and the tick re-arms from that.
    ///
    /// <para>The two negative answers matter as much as the positive one: a decline with nothing
    /// left to wait for means the policy said no for a reason a timer cannot fix, and re-arming on
    /// it would be a 50 ms loop for the rest of the session.</para>
    /// </summary>
    [Fact]
    public async Task A_decline_answers_with_what_is_left_so_the_tick_can_re_arm()
    {
        var clock = new FakeClock();
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model, new BergamotLifetime(() => false, clock.Read, Timeout));

        await provider.TranslateAsync("привет", "ru", "en");
        clock.Advance(Timeout);                                  // the one-shot is about to fire…

        // …and a line lands in the microseconds before the pool asks.
        await provider.TranslateAsync("пока", "ru", "en");

        Assert.False(provider.UnloadIfIdle(out var remaining));
        Assert.True(provider.IsLoaded);
        Assert.Equal(Timeout, remaining);

        // The window really has elapsed this time: it frees, and there is nothing to re-arm for.
        clock.Advance(Timeout);
        Assert.True(provider.UnloadIfIdle(out var afterTheFree));
        Assert.Equal(TimeSpan.Zero, afterTheFree);

        // Nothing is loaded any more, so a second ask declines with zero — the answer that stops
        // the caller re-arming for ever over an engine that is already gone.
        Assert.False(provider.UnloadIfIdle(out var nothingLoaded));
        Assert.Equal(TimeSpan.Zero, nothingLoaded);
    }

    /// <summary>A provider with no policy has no opinion and never unloads itself — and it answers
    /// zero rather than a window, so the caller does not arm a timer for an engine nothing will ever
    /// free on a schedule.</summary>
    [Fact]
    public async Task With_no_policy_a_decline_names_no_window()
    {
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model, lifetime: null);

        await provider.TranslateAsync("привет", "ru", "en");

        Assert.False(provider.UnloadIfIdle(out var remaining));
        Assert.Equal(TimeSpan.Zero, remaining);
        Assert.True(provider.IsLoaded);
    }

    /// <summary>
    /// <b>The (a) heartbeat, at the one moment it is the only thing left.</b> A session in which
    /// LIVE never ran never reaches <c>StopLive</c>, so the one-shot is never armed and nothing
    /// else would collect the engine — the next call into the provider is the sweep. It frees the
    /// stale engine and loads a fresh one for the line that asked, which is the policy being obeyed
    /// rather than work being wasted: ten idle minutes resident is exactly the tax "uses memory only
    /// while translating" promises not to charge.
    /// </summary>
    [Fact]
    public async Task A_call_after_the_window_sweeps_the_stale_engine_before_it_loads_a_new_one()
    {
        var clock = new FakeClock();
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model, new BergamotLifetime(() => false, clock.Read, Timeout));

        await provider.TranslateAsync("привет", "ru", "en");
        var stale = factory.Last!;

        clock.Advance(Timeout * 3);
        Assert.Equal("[пока]", await provider.TranslateAsync("пока", "ru", "en"));

        Assert.Equal(1, stale.Disposals);                 // the old handle really went
        Assert.False(stale.WasCalledAfterDispose);        // …and it went BEFORE the new call, not after
        Assert.Equal(2, factory.Initialisations);
        Assert.True(provider.IsLoaded);
    }

    // =============================================================================================
    //  Case 6 — idempotence: translator_free exactly once per load
    // =============================================================================================

    /// <summary>Two frees on one handle is a native crash, and a crash in a prototype branch is what
    /// closes the branch for the wrong reason. Asked four ways — twice through the policy, then
    /// through the unconditional door and through <c>Dispose</c>.</summary>
    [Fact]
    public async Task Unloading_twice_frees_once_and_a_provider_with_no_policy_never_unloads_itself()
    {
        var clock = new FakeClock();
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var provider = Provider(factory, model, new BergamotLifetime(() => false, clock.Read, Timeout));

        await provider.TranslateAsync("привет", "ru", "en");
        var engine = factory.Last!;
        clock.Advance(Timeout);

        Assert.True(provider.UnloadIfIdle());
        Assert.False(provider.UnloadIfIdle());
        provider.Unload();
        provider.Dispose();

        Assert.Equal(1, engine.Disposals);

        // …and no lifetime means no opinion: a provider built without one is never freed by a sweep,
        // however long the clock runs. E8.S5's chain must hand the policy in, or nothing unloads.
        var other = Provider(new FakeBergamotEngineFactory(), model, lifetime: null);
        await other.TranslateAsync("привет", "ru", "en");
        Assert.False(other.UnloadIfIdle());
        Assert.True(other.IsLoaded);
        other.Unload();
    }

    // =============================================================================================
    //  Case 7 — an idle unload racing a translate in flight
    // =============================================================================================

    /// <summary>
    /// The property the whole design rests on, driven through the NEW door: the one-shot's callback
    /// runs on the pool while a LIVE frame may be inside the engine. It must wait for that frame —
    /// never free a handle a native call is inside — and it must not be able to see a half-torn-down
    /// provider. Same shape as E8.S2's <c>An_Unload_racing_a_translate…</c>, because it is the same
    /// lock and the new caller has to be held to the same contract.
    /// </summary>
    [Fact]
    public async Task An_idle_unload_racing_a_translate_never_frees_the_handle_under_it()
    {
        var clock = new FakeClock();
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        // LIVE is "running" for the warm-up call and stopped by the time the race is set up, which
        // is exactly the ordering the one-shot lives in.
        var live = true;
        var provider = Provider(factory, model, new BergamotLifetime(() => live, clock.Read, Timeout));

        await provider.TranslateAsync("warm", "ru", "en");
        var engine = factory.Last!;

        using var insideTheCall = new ManualResetEventSlim(false);
        using var unloadWasAsked = new ManualResetEventSlim(false);

        var freedDuringTheCall = -1;
        engine.OnTranslate = (t, _) =>
        {
            insideTheCall.Set();
            unloadWasAsked.Wait(TimeSpan.FromSeconds(10));   // the sweep is now blocked on the lock
            freedDuringTheCall = engine.Disposals;
            return "[" + t + "]";
        };

        var translating = Task.Run(() => provider.TranslateAsync("привет", "ru", "en"));
        Assert.True(insideTheCall.Wait(TimeSpan.FromSeconds(10)), "the native call never started");

        // The player pressed ■ Stop and the window has already elapsed — the state OfflineIdleTick
        // hands to the pool.
        live = false;
        clock.Advance(Timeout * 2);

        var unloading = Task.Run(() =>
        {
            unloadWasAsked.Set();
            return provider.UnloadIfIdle();
        });

        Assert.Equal("[привет]", await translating);
        var freed = await unloading;

        Assert.Equal(0, freedDuringTheCall);
        Assert.False(engine.WasCalledAfterDispose);
        Assert.False(engine.WasDisposedWhileInFlight);
        Assert.Equal(1, engine.PeakConcurrentCalls);

        // The in-flight translate recorded a use on its way out, so the sweep that was waiting on
        // the lock finds the window has moved and declines — which is the policy answering with the
        // truth at the instant of the free rather than at the instant of the question. It is freed
        // by the next sweep, once the clock really has run out again.
        Assert.False(freed);
        Assert.Equal(0, engine.Disposals);

        clock.Advance(Timeout);
        Assert.True(provider.UnloadIfIdle());
        Assert.Equal(1, engine.Disposals);
    }

    // =============================================================================================
    //  I10 / I2 — the policy loads nothing, and never samples "is LIVE running?" at construction
    // =============================================================================================

    /// <summary>
    /// <b>The bug this case exists for is a captured <c>false</c>.</b> A chain is built in
    /// <c>MainWindow</c>'s constructor, before any LIVE session exists; a policy that sampled
    /// <c>liveIsRunning</c> there would make A-1(b) a no-op that nothing else in this file would
    /// catch, because every other case sets the flag before it asks.
    /// </summary>
    [Fact]
    public void The_LIVE_question_is_asked_at_every_question_and_never_at_construction()
    {
        var clock = new FakeClock();
        var asked = 0;
        var live = false;
        var policy = new BergamotLifetime(() => { asked++; return live; }, clock.Read, Timeout);

        Assert.Equal(0, asked);                     // I10: constructing decides nothing and asks nothing

        clock.Advance(Timeout);
        Assert.True(policy.ShouldUnload(isLoaded: true));
        live = true;
        Assert.False(policy.ShouldUnload(isLoaded: true));
        Assert.Equal(2, asked);

        // A clock that jumps backwards (a resync, a VM resume) is not "used in the future": the idle
        // span is floored, so the model is not held resident for as long as the jump lasted.
        live = false;
        clock.Advance(-Timeout * 5);
        Assert.False(policy.ShouldUnload(isLoaded: true));
        Assert.Equal(Timeout, policy.RemainingIdle());
    }

    /// <summary>The default window is <see cref="TranslationPolicy.IdleUnloadMinutes"/> and is not a
    /// literal in this policy: the constant is the one place the number is argued about (AC 2), and
    /// a second spelling here would be the drift the whole table exists to prevent.</summary>
    [Fact]
    public void The_default_window_comes_from_the_policy_table()
    {
        Assert.Equal(TimeSpan.FromMinutes(TranslationPolicy.IdleUnloadMinutes),
                     new BergamotLifetime(() => false).IdleTimeout);

        var source = Code(File.ReadAllText(RepoFile(Path.Combine("Services", "BergamotLifetime.cs"))));
        Assert.Contains("TranslationPolicy.IdleUnloadMinutes", source, StringComparison.Ordinal);
        // No timer, no window, no dispatcher — I2, and the reason this type is testable at all.
        foreach (var forbidden in new[] { "DispatcherTimer", "System.Windows", "MainWindow" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    // =============================================================================================
    //  Case 5 — the one-shot: armed by StopLive, disarmed by a LIVE start and by OnClosing
    // =============================================================================================

    /// <summary>
    /// The timer's lifetime, asserted on the flag rather than by waiting ten minutes (CI-3 — the
    /// shape <c>CountdownTests</c> established for the 1 Hz tick). Arming is idempotent because
    /// <c>StopLive</c> is reachable four ways.
    /// </summary>
    [Fact]
    public void The_one_shot_is_armed_and_disarmed_and_never_runs_twice_off_one_arming()
    {
        using var settings = new TempSettings("""{ "SettingsVersion": 3 }""");
        using var models = new TempModels();

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            // I10: nothing is armed by construction. The one-shot cannot run before a LIVE session
            // has ended, because StopLive is the only thing that arms it.
            Assert.False(window.OfflineIdleUnloadArmed);

            window.ArmOfflineIdleUnload();
            Assert.True(window.OfflineIdleUnloadArmed);

            window.ArmOfflineIdleUnload();                  // idempotent: four callers, one window
            Assert.True(window.OfflineIdleUnloadArmed);

            // Case 5 — LIVE restarts inside the window: the one-shot is disarmed and nothing is
            // freed. (The policy would refuse the free anyway; this is the state a reader can see
            // agreeing with it.)
            window.DisarmOfflineIdleUnload();
            Assert.False(window.OfflineIdleUnloadArmed);
        });
    }

    /// <summary>
    /// The three call sites, pinned in source because the alternative is driving a real LIVE loop on
    /// an STA thread — the same reason <c>ChainCompositionTests</c> pins the chain wiring this way.
    /// <c>StopLive</c> arms, <c>StartLive</c> disarms, <c>OnClosing</c> disarms and unloads with a
    /// bounded wait off the dispatcher.
    /// </summary>
    [Fact]
    public void StopLive_arms_it_StartLive_disarms_it_and_OnClosing_does_both()
    {
        var live = Code(File.ReadAllText(RepoFile("Views/MainWindow.Live.cs")));
        var main = Code(File.ReadAllText(RepoFile("Views/MainWindow.xaml.cs")));

        Assert.Contains("ArmOfflineIdleUnload();", Body(live, "private void StopLive()"),
                        StringComparison.Ordinal);
        Assert.Contains("DisarmOfflineIdleUnload();",
                        Body(live, "private void StartLive(System.Drawing.Rectangle rect, bool freshSession)"),
                        StringComparison.Ordinal);

        // THREE arming sites in the whole app and no more: StopLive (the ONE site a player's gesture
        // reaches — Stop comes from the ■ button, from Resume, from Ctrl+Alt+L and from E5.S2's
        // auto-stop, and arming at those four call sites would have missed three of them), the
        // tick's own re-arm when it finds the window has moved, and — E8.S5 — the re-arm after the
        // POOL declines, which is E8.S4's recorded gap: a translation landing between the
        // dispatcher's question and the provider's answer made UnloadIfIdle decline into silence.
        var arming = ProductionSources()
            .Select(f => (Name: Path.GetFileName(f),
                          Count: Occurrences(Code(File.ReadAllText(f)), "ArmOfflineIdleUnload();")))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Name}: {x.Count}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "MainWindow.Live.cs: 1", "MainWindow.xaml.cs: 2" }, arming);

        // OnClosing: the timer roots its handler on a window that is going away (E7.S2's reason for
        // StopCountdown), and the engine goes with it — off the dispatcher, bounded, because Unload
        // waits on the provider's lock for a frame in flight.
        var closing = Body(main, "protected override void OnClosing(CancelEventArgs e)");
        Assert.Contains("DisarmOfflineIdleUnload();", closing, StringComparison.Ordinal);
        Assert.Contains("Task.Run(_offlineEngine.Unload).Wait(", closing, StringComparison.Ordinal);

        // …and E7's countdown stop rule was NOT widened for this story: the tick still asks two
        // questions and neither of them is about an engine (the story's T3(b), declined on the
        // architect's ruling).
        var tick = Body(main, "internal void CountdownTick(ChainPause pause, bool chipNeedsIt)");
        Assert.DoesNotContain("Offline", tick, StringComparison.Ordinal);
        Assert.DoesNotContain("_offlineEngine", tick, StringComparison.Ordinal);

        // The unload never happens on the UI thread. Every site that can free the engine either
        // hands it to the pool or is a user gesture that deliberately waits (RemoveOfflineEngine).
        // Since E8.S5 the pool call also answers with the idle it decided on, and the hop back is a
        // BeginInvoke guarded by _closing — re-arming a rooted timer on a window that is going away
        // is precisely what E8.S4 refused to risk without this guard.
        var tickBody = Body(main, "internal void OfflineIdleTick()");
        Assert.Contains("engine.UnloadIfIdle(out var remaining)", tickBody, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", tickBody, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke", tickBody, StringComparison.Ordinal);
        Assert.Contains("_closing", tickBody, StringComparison.Ordinal);
        // …and the guard is really set, first thing, on the way out.
        Assert.Contains("_closing = true;", closing, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Answer 2 of the tick, driven rather than read.</b> A translation that lands between
    /// <c>StopLive</c> and the fire moves the window, and the tick must arm again for what is left —
    /// deferring is not cancelling. A freshly built window is exactly that state: its lifetime was
    /// stamped in the constructor a moment ago, so the whole window is still ahead of it.
    /// </summary>
    [Fact]
    public void The_tick_re_arms_when_it_finds_the_window_has_moved()
    {
        using var settings = new TempSettings("""{ "SettingsVersion": 3 }""");
        using var models = new TempModels();

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            window.DisarmOfflineIdleUnload();
            Assert.False(window.OfflineIdleUnloadArmed);

            window.OfflineIdleTick();

            Assert.True(window.OfflineIdleUnloadArmed,
                "the one-shot found the window had moved and then forgot about it — nothing would "
                + "have freed the engine until the next call into the provider");
        });
    }

    // =============================================================================================
    //  E8.S5 inherits this: ONE provider instance in the whole app
    // =============================================================================================

    /// <summary>
    /// <b>There is exactly one <c>BergamotTranslator</c> in the process, and it is the one this
    /// window owns.</b> E8.S5 wires the offline rung into the chains and must hand <b>this</b>
    /// instance to the builders: a second one constructed inside <c>TranslationChains</c> would be a
    /// second 121 MiB that no lifetime here could unload, and the two would disagree about what is
    /// resident — while <see cref="MainWindow.OnClosing"/> and the one-shot would both be freeing the
    /// wrong one. Pinned now so the next story cannot introduce it quietly.
    /// </summary>
    [Fact]
    public void The_app_constructs_exactly_one_offline_provider()
    {
        var sites = ProductionSources()
            .Select(f => (Name: Path.GetFileName(f),
                          Count: Occurrences(Code(File.ReadAllText(f)), "new BergamotTranslator(")))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Name}: {x.Count}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "MainWindow.xaml.cs: 1" }, sites);
    }

    // =============================================================================================
    //  translator_free exactly once per load — every door at once, under a randomised interleaving
    // =============================================================================================

    /// <summary>
    /// <b>Two frees on one handle is a native crash</b>, and the four doors that can free one
    /// (the sweep at the top of a translate, <c>UnloadIfIdle</c>, <c>Unload</c>, <c>Dispose</c>) are
    /// each proven in isolation above. This case opens all of them at once, on a seeded interleaving
    /// with the clock and the LIVE flag moving underneath, and asks the only question that matters of
    /// every engine the factory ever built: freed once, never called after its free, never freed with
    /// a call inside it, never two calls inside it at a time.
    ///
    /// <para>Seeded, so a failure is reproducible; no sleep and no timing assertion (CI-3) — the
    /// concurrency is real but nothing here waits for a duration.</para>
    /// </summary>
    [Fact]
    public async Task Every_door_at_once_still_frees_each_engine_exactly_once()
    {
        var clock = new FakeClock();
        var factory = new FakeBergamotEngineFactory();
        using var model = new TempModel();
        var live = 0;
        var provider = Provider(factory, model,
            new BergamotLifetime(() => Volatile.Read(ref live) != 0, clock.Read, Timeout));

        var seeds = new Random(20260907);
        var workers = Enumerable.Range(0, 6).Select(_ => seeds.Next()).Select(seed => Task.Run(async () =>
        {
            var r = new Random(seed);
            for (var i = 0; i < 30; i++)
                switch (r.Next(6))
                {
                    case 0: provider.UnloadIfIdle(); break;
                    case 1: provider.Unload(); break;
                    case 2: clock.Advance(TimeSpan.FromMinutes(r.Next(0, 20))); break;
                    case 3: Volatile.Write(ref live, r.Next(2)); break;
                    case 4:
                        await provider.TranslateLinesAsync(new[] { "а", "б" }, "ru", "en")
                                      .ConfigureAwait(false);
                        break;
                    default:
                        await provider.TranslateAsync("строка", "ru", "en").ConfigureAwait(false);
                        break;
                }
        })).ToArray();

        await Task.WhenAll(workers);
        provider.Dispose();

        Assert.False(provider.IsLoaded);
        // Not vacuous: the storm really did free engines and load fresh ones, so "exactly once" below
        // is a statement about several handles and not about the one that was never let go.
        Assert.True(factory.Initialisations > 1,
                    $"the interleaving never reloaded — {factory.Initialisations} engine(s) built");
        Assert.Equal(factory.Initialisations, factory.All.Count);
        foreach (var engine in factory.All)
        {
            Assert.Equal(1, engine.Disposals);            // translator_free, exactly once per load
            Assert.False(engine.WasCalledAfterDispose);
            Assert.False(engine.WasDisposedWhileInFlight);
            Assert.Equal(1, engine.PeakConcurrentCalls);
        }
    }

    // =============================================================================================
    //  E8.S3's review finding — Remove() while the engine is loaded
    // =============================================================================================

    /// <summary>
    /// <b>On Windows a loaded DLL cannot be deleted.</b> With the engine resident,
    /// <c>Directory.Delete</c> threw, the store logged a kind and answered 0, and the About row read
    /// "Offline engine removed — 0 MB freed from your disk" while every file was still there and the
    /// setting was already false. E8.S3 recorded it for this story; the fix is an ORDER, so the
    /// order is what is asserted — <c>translator_free</c> runs while the directory still exists, and
    /// the files go afterwards.
    /// </summary>
    [Fact]
    public async Task Remove_unloads_the_engine_before_it_deletes_the_files()
    {
        var native = TempModels.Blob(1, 64);
        var model = TempModels.Blob(2, 128);
        var manifest = TempModels.ManifestOver(native, model);
        using var models = new TempModels(manifest);
        models.Install(manifest, native, model);

        var store = new OfflineModelStore(manifest: manifest);
        var pair = Path.Combine(models.Root, OfflineModelManifest.RuEn);
        var factory = new FakeBergamotEngineFactory();
        var provider = new BergamotTranslator(
            modelDirectory: () => pair, nativeDirectory: () => models.Root,
            engineFactory: factory.Create, gate: new ProviderGate());

        await provider.TranslateAsync("привет", "ru", "en");
        var engine = factory.Last!;
        Assert.True(provider.IsLoaded);

        // The ordering, recorded from the one place that can see it: inside translator_free.
        bool? filesWereStillThere = null;
        engine.OnDispose = () => filesWereStillThere = Directory.Exists(pair);

        var onDisk = store.BytesOnDisk;
        var freed = MainWindow.UnloadThenRemove(provider, store);

        Assert.True(filesWereStillThere, "the files were deleted BEFORE the engine was freed — the bug");
        Assert.Equal(1, engine.Disposals);
        Assert.False(provider.IsLoaded);
        Assert.False(Directory.Exists(models.Root));
        // …and the sentence the row then shows is the true one: the delete really happened, so the
        // number it reports is the size that really came back and not E8.S3's "0 MB freed".
        Assert.True(onDisk > 0);
        Assert.Equal(onDisk, freed);
    }

    // ---- helpers (the shape the other source scans in this suite use) ---------------------------

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Code(string text) => string.Join("\n", text.Split('\n').Select(l =>
    {
        var cut = l.IndexOf("//", StringComparison.Ordinal);
        return cut >= 0 ? l[..cut] : l;
    }));

    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"could not find {signature}");
        var open = source.IndexOf('{', at);
        Assert.True(open >= 0, $"could not find the body of {signature}");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        Assert.Fail($"unbalanced braces after {signature}");
        return "";
    }

    private static IEnumerable<string> ProductionSources()
    {
        var root = RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = sep + Path.GetRelativePath(root, file);
            if (relative.Contains($"{sep}tests{sep}") || relative.Contains($"{sep}bin{sep}")
                || relative.Contains($"{sep}obj{sep}")) continue;
            yield return file;
        }
    }

    private static string RepoFile(string relative)
    {
        var path = Path.Combine(RepoRoot(), relative);
        Assert.True(File.Exists(path), $"expected {relative} at the repo root");
        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root (no PWRUHelper.csproj above the test output)");
        return dir!.FullName;
    }
}
