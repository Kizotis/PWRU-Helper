namespace PWRUHelper.Services;

/// <summary>What a LIVE tick did, as one value both counters read.
///
/// <para>It exists because <c>MainWindow.Live.cs</c> held <b>two</b> independent notions of "a good
/// tick": <c>consecutiveErrors = 0</c> ran on every non-throwing tick, empty ones included, so a
/// silent chat quietly forgave a real failure streak. E5.S1 needed the same distinction for
/// <c>backoffSteps</c> (AC 4 — reset only on a tick that <b>translated</b>, never on an empty one),
/// so the outcome is named once here rather than derived twice at the call site. <b>E5.S2 deleted
/// that line and pointed the error counter at this enum</b>, which is why the two error arms below
/// are part of the same vocabulary and not a second one.</para></summary>
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

    /// <summary>The tick ran and was refused from <b>inside</b> itself: a gate said no before
    /// anything left the machine (<see cref="TranslationException.NotSent"/> — a probe deferral, a
    /// rate-ceiling refusal), or the chain answered <c>AllProvidersPaused</c> because a window
    /// closed between E5.S1's pre-tick check and the request.
    ///
    /// <para><b>Ruling E5-c</b>: that is a pause wearing an exception's clothes, so it counts
    /// exactly as much as one does — not at all. It is not <see cref="Paused"/> either: the tick
    /// really did capture and OCR, so it does not advance the back-off.</para></summary>
    Refused,

    /// <summary>A request left the machine and failed. <b>The only outcome the auto-stop counts</b>
    /// (E5-c), and the back-off is untouched — that one is E5.S1's.</summary>
    SentFailure,
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

    /// <summary>Floor on the wait, mirroring the one the WORKING path keeps
    /// (<c>Math.Max(150, …)</c> at the loop's own <c>Task.Delay</c>). The doubling can only ever
    /// grow a wait, so nothing reachable today lands here — the speed slider is clamped to 0–100
    /// and maps to 500–3000 ms. It is a floor rather than a comment because the value leaves this
    /// method and goes straight into <c>Task.Delay</c>, which throws
    /// <see cref="ArgumentOutOfRangeException"/> on a negative — from OUTSIDE the loop's
    /// <c>try</c>, faulting the fire-and-forget loop task with no <c>SetLiveUi(false)</c> behind it.
    /// That is the R-02 zombie indicator reached through arithmetic, and one <c>Math.Max</c> closes
    /// it (review, E5.S1).</summary>
    internal const int MinWaitMs = 150;

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
        return (int)Math.Clamp(doubled, MinWaitMs, TranslationPolicy.LiveBackoffCapMs);
    }

    /// <summary>AC 4, and the reason <see cref="LiveTickOutcome"/> exists: only a tick that really
    /// translated clears the back-off. An empty tick asked the providers nothing, and a tick that
    /// really SENT something and failed belongs to the error counter, not to this curve.
    ///
    /// <para><b>Ruling E5-f (E5.S3): a <see cref="LiveTickOutcome.Refused"/> tick advances the same
    /// step a <see cref="LiveTickOutcome.Paused"/> one does.</b> The two are the same event seen from
    /// either side of the pre-tick check — a gate saying no, at no cost in requests — and E5.S1 only
    /// bounded the half it could see in advance. Nothing bounded the other: a tier refusing from
    /// INSIDE the tick (a probe deferral, a rate-ceiling refusal, or a window that closed between the
    /// check and the request) left the loop capturing and OCR-ing a full frame every ~700 ms for as
    /// long as the refusal lasted — the exact cost OQ-B's full pause exists to remove, reached
    /// through the branch that throws. It still counts as no error at all
    /// (<see cref="LiveErrorTracker"/>, ruling E5-c): backing off is not the same as blaming.</para></summary>
    internal static int NextBackoffSteps(int backoffSteps, LiveTickOutcome outcome) => outcome switch
    {
        LiveTickOutcome.Translated => 0,
        // Clamped rather than incremented for ever: the wait reaches the cap long before
        // MaxBackoffShift, so the counter has nothing left to say, and an unbounded int would
        // eventually wrap negative on a session left paused overnight.
        LiveTickOutcome.Paused or LiveTickOutcome.Refused => Math.Min(backoffSteps + 1, MaxBackoffShift),
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

    /// <summary>
    /// <b>Ruling E5-c, written down exactly once.</b> What a failing tick was: a request that was
    /// sent and failed (<see cref="LiveTickOutcome.SentFailure"/>), or a refusal that cost nothing
    /// (<see cref="LiveTickOutcome.Refused"/>). Only the first may feed the auto-stop — "a pause or
    /// a gate refusal is never an error", and an app that stops itself because it was correctly
    /// waiting is failure mode R-02/R6.
    ///
    /// <para>Two shapes reach here even though E5.S1's pre-tick <c>PauseNow()</c> skip normally
    /// prevents them, because a gate can open between that check and the request:
    /// <see cref="TranslationErrorKind.AllProvidersPaused"/> (the chain found every tier blocked)
    /// and <see cref="TranslationException.NotSent"/> (one tier refused from inside the core, with
    /// the gate's LAST kind on it — a 429 that was never re-sent still reads as
    /// <c>RateLimited</c>, which is why the flag and not the kind is what decides).</para>
    ///
    /// <para><b>I3 lives in the default arm.</b> A 12 s HttpClient timeout arrives as a
    /// <c>TaskCanceledException</c> with the loop's token NOT cancelled; it is a request that was
    /// sent and did not come back, and it must count. Nothing here filters on cancellation — the
    /// loop's own <c>catch … when (ct.IsCancellationRequested)</c> has already taken the genuine
    /// Stop, and re-testing it here is how a timeout becomes a phantom user-cancel.</para>
    ///
    /// <para><b>What else the default arm sweeps up, said plainly:</b> a throw from the READ half of
    /// the tick — a failed screen capture, a dead OCR engine — never touched a provider and is
    /// counted all the same. That is deliberate and it is what shipped before this story: five
    /// consecutive ticks that could not even read the screen is a broken LIVE whoever's fault it is,
    /// and stopping is the honest answer. Only the <i>diagnosis</i> in the status sentence is then
    /// aimed at the translator, which is E7.S1's copy pass to fix, not this function's.</para>
    /// </summary>
    internal static LiveTickOutcome Classify(Exception ex) => ex switch
    {
        TranslationException { NotSent: true } => LiveTickOutcome.Refused,
        TranslationException { Kind: TranslationErrorKind.AllProvidersPaused } => LiveTickOutcome.Refused,
        _ => LiveTickOutcome.SentFailure,
    };
}

/// <summary>
/// <c>architecture-cible.md</c> §9.2 — <b>when LIVE stops itself</b>, as one pure counter. It
/// replaces <c>MainWindow.Live.cs</c>' <c>int consecutiveErrors</c>, whose <c>= 0</c> at the end of
/// every non-throwing tick — an EMPTY one included — is why the auto-stop never fired in a calm
/// chat: the loop alternated "empty tick, failing tick" and the counter never reached two while the
/// app trickled failing requests all evening (<c>analyse…</c> A2, S4c).
///
/// <para><b>The rule, and it is the whole of it:</b> LIVE stops after
/// <see cref="TranslationPolicy.LiveAutoStopThreshold"/> <b>consecutive</b> failures that cost a
/// request, counted <b>since the last tick that translated</b> — <i>whatever the elapsed time</i>.
/// Only <see cref="LiveTickOutcome.Translated"/> clears the count;
/// <see cref="LiveTickOutcome.Empty"/> (the bug), <see cref="LiveTickOutcome.Paused"/> and
/// <see cref="LiveTickOutcome.Refused"/> (ruling E5-c) leave it exactly where it was.</para>
///
/// <para><b>There is no time window, and that is a ruling and not an omission</b> (architect,
/// E5.S2 review). §9.2 sketched a second trigger over a two-minute queue of stamps; a translated
/// tick clears the queue in its own table, which makes that trigger a strict subset of this one,
/// and a window would in exchange let a genuinely dead evening run for ever — five ticks whose
/// every request times out cost up to ~48 s each, so five of them never fit inside two minutes.
/// The count is honest because of what it refuses to count, not because of a clock.</para>
///
/// <para><b>An instance, not a static</b> — two LIVE sessions in one test run must not share a
/// counter, and <c>StartLive</c> gets a clean five for free by building a new one (which is exactly
/// what a player pressing ▶ after an auto-stop must get).</para>
///
/// <para><b>I2 / IS-6 / CI-3</b>: no clock, no <c>DateTime.UtcNow</c>, no state but one int — so
/// every case is an L1 unit test with no <c>Task.Delay</c> in it.</para>
/// </summary>
internal sealed class LiveErrorTracker
{
    private int _failures;

    /// <summary>Sent failures since the last tick that translated — the streak the rule is about.
    /// For the diagnostic log line and for the tests; nothing decides on it but
    /// <see cref="Record"/>. "Consecutive" among the ticks that cost a request: an empty, paused or
    /// refused tick in the middle does not break the streak, because none of them is evidence that
    /// anything works.</summary>
    internal int ConsecutiveFailures => _failures;

    /// <summary>
    /// Record what a tick did and answer <b>whether LIVE must stop</b>. §9.2's table, in three arms:
    /// a tick that translated clears the streak, a sent failure lengthens it, and an empty tick, a
    /// pause or a refusal change nothing at all (E5-c).
    /// </summary>
    internal bool Record(LiveTickOutcome outcome)
    {
        switch (outcome)
        {
            // The only evidence that the providers are actually working — and the only thing that
            // forgives a streak. Not an empty tick: a calm chat asked them nothing.
            case LiveTickOutcome.Translated:
                Reset();
                break;

            // Clamped at the threshold rather than incremented for ever, the same guard
            // NextBackoffSteps keeps: past the threshold the counter has nothing left to say (the
            // loop breaks on the verdict below), and an unbounded int on a session that somehow
            // kept running would eventually wrap NEGATIVE and silently disable the auto-stop.
            case LiveTickOutcome.SentFailure:
                _failures = Math.Min(_failures + 1, TranslationPolicy.LiveAutoStopThreshold);
                break;

            // Empty, Paused, Refused: neither a success nor a failure. Written as a comment rather
            // than as empty arms because "nothing happens here" is the ruling, not an omission.
        }

        return _failures >= TranslationPolicy.LiveAutoStopThreshold;
    }

    /// <summary>Forget everything — a fresh session, or a tick that translated.</summary>
    internal void Reset() => _failures = 0;
}
