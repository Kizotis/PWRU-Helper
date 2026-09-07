namespace PWRUHelper.Services;

/// <summary>What a LIVE tick did, as one value both counters read.
///
/// <para>It exists because <c>MainWindow.Live.cs</c> has historically held <b>two</b> independent
/// notions of "a good tick": <c>consecutiveErrors = 0</c> runs on every non-throwing tick, empty
/// ones included, so a silent chat quietly forgives a real failure streak. E5.S1 needs the same
/// distinction for <c>backoffSteps</c> (AC 4 — reset only on a tick that <b>translated</b>, never on
/// an empty one), so the outcome is named once here rather than derived twice at the call site.
/// <b>E5.S2 points the error counter at the same enum.</b></para></summary>
internal enum LiveTickOutcome
{
    /// <summary>Lines were confirmed AND translated — the only outcome that clears the back-off.</summary>
    Translated,

    /// <summary>The tick read the screen and found nothing new. A calm chat is not evidence that the
    /// providers are back, so it leaves the back-off exactly where it was.</summary>
    Empty,

    /// <summary>Every read tier was inside a block window, so the tick body never ran (OQ-B's full
    /// pause). It advances the back-off and — this is the half that matters — it is <b>not an
    /// error</b>: pausing may never feed the auto-stop counter.</summary>
    Paused,

    /// <summary>The tick threw. E5.S2's counter, not this one's: the back-off is untouched.</summary>
    Threw,
}

/// <summary>
/// <c>architecture-cible.md</c> §9.1 — the arithmetic of a paused LIVE loop, as pure functions so
/// TP-LIVE-02/03 are L1 unit tests instead of a loop the suite would have to drive on an STA thread
/// (test-plan §3.7: "the tick outcome must be extracted as a pure <c>internal</c> decision").
///
/// <para><b>I2</b>: ints in, ints out. No WPF type, no dispatcher, no settings read and no
/// formatting — <see cref="CountdownSeconds"/> deliberately answers a <i>number</i> of seconds and
/// leaves the sentence to the code-behind, which is the same split <c>UserMessages</c> documents for
/// <c>{t}</c>.</para>
/// </summary>
internal static class LiveTickPolicy
{
    /// <summary>Ceiling on the doubling, in shifts. Not cosmetic — see <see cref="BackoffWaitMs"/>:
    /// C# masks a shift count to its low 5 bits, so <c>interval &lt;&lt; 32</c> is
    /// <c>interval &lt;&lt; 0</c> and the 32nd paused tick would silently return the BASE interval —
    /// a 700 ms busy-wait after nine minutes of pause. Twenty is far past the cap for every interval
    /// the speed slider can produce (500 ms &lt;&lt; 4 already exceeds
    /// <see cref="TranslationPolicy.LiveBackoffCapMs"/>) and small enough that the shift below
    /// cannot overflow the <c>long</c> it is computed in.</summary>
    internal const int MaxBackoffShift = 20;

    /// <summary>Beyond an hour a countdown stops being one: the sentence drops the number rather
    /// than telling a player to wait fifty-one minutes. It is also the guard for the
    /// <see cref="DateTimeOffset.MaxValue"/> sentinel a gate stores for <c>AuthFailed</c>, whose
    /// real exit is re-saving the key and not a timer.</summary>
    internal const int MaxCountdownSeconds = 3600;

    /// <summary>§9.1's <c>min(interval &lt;&lt; steps, cap)</c>: the wait between two SKIPPED ticks,
    /// doubling per skipped tick and capped at <see cref="TranslationPolicy.LiveBackoffCapMs"/>.
    /// At the shipped speed (700 ms) that is 0.7 s → 1.4 → 2.8 → 5 → 5…
    ///
    /// <para>It changes the WAIT and never <c>MainWindow.LiveIntervalMs(double)</c> (AC 5) — the
    /// pure slider mapping keeps its own pins.</para>
    ///
    /// <para>Computed in a <c>long</c>: <c>3000 &lt;&lt; 20</c> overflows <see cref="int"/> into a
    /// NEGATIVE number, which <c>Math.Min</c> would then happily prefer to the cap — the second way
    /// this one line can turn a five-second back-off into a busy-wait.</para></summary>
    internal static int BackoffWaitMs(int intervalMs, int backoffSteps)
    {
        long doubled = (long)intervalMs << Math.Clamp(backoffSteps, 0, MaxBackoffShift);
        return (int)Math.Min(doubled, TranslationPolicy.LiveBackoffCapMs);
    }

    /// <summary>AC 4, and the reason <see cref="LiveTickOutcome"/> exists: only a tick that really
    /// translated clears the back-off. An empty tick asked the providers nothing, and a tick that
    /// threw belongs to the error counter.</summary>
    internal static int NextBackoffSteps(int backoffSteps, LiveTickOutcome outcome) => outcome switch
    {
        LiveTickOutcome.Translated => 0,
        // Clamped rather than incremented for ever: the wait reaches the cap long before
        // MaxBackoffShift, so the counter has nothing left to say, and an unbounded int would
        // eventually wrap negative on a session left paused overnight.
        LiveTickOutcome.Paused => Math.Min(backoffSteps + 1, MaxBackoffShift),
        _ => backoffSteps,
    };

    /// <summary>Whole seconds left until <paramref name="retryAt"/>, or <c>null</c> when there is no
    /// countdown to show — no remembered window, one that has already elapsed, or one so far out
    /// (see <see cref="MaxCountdownSeconds"/>) that a number would be noise.
    ///
    /// <para>Rounded UP, so the last second reads "1 s" rather than "0 s" for a whole tick.</para>
    ///
    /// <para><b>This is A.2's coarse countdown</b>: it is recomputed by the skipped tick itself,
    /// every ≤ 5 s, and there is deliberately no timer behind it. <b>E7.S2</b> replaces it with the
    /// 1 Hz poll, §2.4's granularity bands and the repaint guard.</para></summary>
    internal static int? CountdownSeconds(DateTimeOffset? retryAt, DateTimeOffset now)
    {
        if (retryAt is not { } at) return null;
        var left = at - now;
        if (left <= TimeSpan.Zero) return null;
        if (left.TotalSeconds > MaxCountdownSeconds) return null;
        return (int)Math.Ceiling(left.TotalSeconds);
    }
}
