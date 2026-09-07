namespace PWRUHelper.Services;

/// <summary>
/// <b>Amendment A-1(b), as a predicate.</b> §7.6 constraint 2 as originally written said "lazy load
/// on first fallback use, unload after <see cref="TranslationPolicy.IdleUnloadMinutes"/>"; A-1(b)
/// supersedes the unload half. Once loaded, the model <b>stays</b> loaded for the whole LIVE
/// session, and is released only after LIVE stops <b>and</b> an idle timeout elapses. The plain
/// idle-unload still governs when LIVE is not running.
///
/// <para>The row a naive implementation gets wrong is <b>LIVE running with a long gap</b>: a session
/// whose every online tier is inside a 30-minute gate window has no offline translation for half an
/// hour. "Unload after 10 idle minutes" would free the model in the middle of the session and pay
/// the 82 ms init again on resume — which is the behaviour A-1(b) exists to forbid, and TP-BRG-05 is
/// the case that catches it.</para>
///
/// <para><b>Why this is a type and not a paragraph inside <see cref="BergamotTranslator"/>.</b> It
/// is three inputs and one output. Written here, over literals and an injected clock, it is a dozen
/// unit tests; written inside the provider it is a timer nobody can test without a 22 MB DLL and a
/// stopwatch. <c>LiveTickPolicy</c>, <c>CompactOverlay.ResizeHitTest</c> and <c>EngineStatus.Of</c>
/// are the three precedents in this repo, and all three exist because their logic was untestable
/// while it lived inside a control.</para>
///
/// <para><b>I2</b>: two delegates and a <see cref="TimeSpan"/>. No <c>Window</c>, no
/// <c>DispatcherTimer</c>, no settings read — "is LIVE running?" arrives as a
/// <see cref="Func{T}"/> (the spelling <c>PendingRetryQueue</c> already uses at
/// <c>MainWindow.Ocr.cs</c>: <c>liveIsRunning: _liveCts != null</c>), never as a reference to the
/// window that knows. <b>I10</b>: this type can only ever say <i>unload</i>. Nothing it does causes
/// a load, and nothing it does runs before first paint. <b>IS-6 / CI-3</b>: the clock is injected,
/// exactly as <see cref="ProviderGate"/>'s is, so every window assertion in the suite is arithmetic
/// rather than a sleep.</para>
/// </summary>
internal sealed class BergamotLifetime
{
    /// <summary>Sampled on every question, <b>never at construction</b>. A chain is built in
    /// <c>MainWindow</c>'s constructor, before any LIVE session exists, so a captured <c>false</c>
    /// would make A-1(b) a no-op that nothing would notice until a player's model was freed
    /// mid-session.</summary>
    private readonly Func<bool> _liveIsRunning;

    private readonly Func<DateTimeOffset> _clock;

    /// <summary>The last use, as UTC ticks. A <c>long</c> rather than a
    /// <see cref="DateTimeOffset"/> because it is written from a pool thread inside the provider's
    /// lock and read from the dispatcher outside it: a 16-byte struct has no atomic assignment, and
    /// a torn instant would be a free at an arbitrary time rather than at the right one.</summary>
    private long _lastUseUtcTicks;

    /// <param name="liveIsRunning">"Is a LIVE session running right now?" — the whole of A-1(b)'s
    /// first clause. Required: a lifetime that cannot answer it is the plain idle-unload this type
    /// exists to replace.</param>
    /// <param name="clock">The one source of "now" (IS-6), the same shape and the same default as
    /// <see cref="ProviderGate"/>'s. Null means the wall clock.</param>
    /// <param name="idleTimeout">Null means <see cref="TranslationPolicy.IdleUnloadMinutes"/>. It is
    /// a parameter so the cases can be stated over literals — one minute, ten, thirty — and not so a
    /// user can tune it: §12's table is closed and ruling R-4 gives the offline engine exactly one
    /// user-facing decision (Download / Remove).</param>
    internal BergamotLifetime(Func<bool> liveIsRunning, Func<DateTimeOffset>? clock = null,
                              TimeSpan? idleTimeout = null)
    {
        _liveIsRunning = liveIsRunning ?? throw new ArgumentNullException(nameof(liveIsRunning));
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(TranslationPolicy.IdleUnloadMinutes);

        // "Now" and not DateTimeOffset.MinValue: an engine that has just been constructed has not
        // been idle since the year 1, and a zero here would make the very first ShouldUnload() after
        // a load answer true on a clock that has barely moved.
        Volatile.Write(ref _lastUseUtcTicks, _clock().UtcTicks);
    }

    /// <summary>The window this policy measures. Exposed so a caller can say what it is waiting for
    /// without spelling the constant a second time.</summary>
    internal TimeSpan IdleTimeout { get; }

    /// <summary>When the engine was last used, on the injected clock.</summary>
    internal DateTimeOffset LastUse => new(Volatile.Read(ref _lastUseUtcTicks), TimeSpan.Zero);

    /// <summary>A translation just went through the engine. Called by
    /// <see cref="BergamotTranslator"/> after each native call — the (a) half of E8.S4's heartbeat,
    /// which costs nothing and needs nothing new.</summary>
    internal void RecordUse() => Volatile.Write(ref _lastUseUtcTicks, _clock().UtcTicks);

    /// <summary>
    /// <b>The whole policy.</b> Unload when an engine is resident, no LIVE session is running, and
    /// nothing has used it for <see cref="IdleTimeout"/>.
    ///
    /// <para><paramref name="isLoaded"/> is a parameter and not a fourth delegate on purpose: the
    /// caller that matters asks this <i>inside</i> the provider's lock, holding the truth in a local
    /// field, and a <c>Func&lt;bool&gt;</c> sampled from outside that lock would be answering about
    /// a different instant than the one the free would happen at.</para>
    /// </summary>
    internal bool ShouldUnload(bool isLoaded) =>
        isLoaded && !_liveIsRunning() && Idle() >= IdleTimeout;

    /// <summary>How much of the window is left, so a one-shot timer can be armed for the REMAINDER
    /// rather than for the whole timeout. It matters at exactly one moment: LIVE stopping after a
    /// long gate-paused stretch has already served most of the idle window, and re-starting the
    /// clock there would keep 121 MiB resident for ten minutes the policy never asked for.
    /// <see cref="TimeSpan.Zero"/> when it has already elapsed.</summary>
    internal TimeSpan RemainingIdle()
    {
        var left = IdleTimeout - Idle();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Time since the last use, floored at zero. The floor is not defensive noise: a
    /// machine that resyncs its clock backwards (or a test that rewinds one) would otherwise produce
    /// a negative span that reads as "used in the future" and keeps the model resident for as long
    /// as the jump lasted.</summary>
    private TimeSpan Idle()
    {
        var elapsed = _clock() - LastUse;
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }
}
