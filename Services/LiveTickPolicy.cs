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

    /// <summary>Hard bound on the auto-stop's stamp queue. Five stamps decide the question
    /// (<see cref="TranslationPolicy.LiveAutoStopThreshold"/>) and the trim by time normally keeps
    /// it far below that; this is the guard for the pathological shape the trim cannot help with —
    /// a burst of failures inside one window, which would otherwise grow the queue for as long as
    /// the burst lasts. Structural and not tunable, so it lives here rather than in the graded
    /// table: moving it changes nothing a player can see until it drops below the threshold, which
    /// is what the bound is asserted against.</summary>
    internal const int MaxErrorStamps = 8;

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
    /// </summary>
    internal static LiveTickOutcome Classify(Exception ex) => ex switch
    {
        TranslationException { NotSent: true } => LiveTickOutcome.Refused,
        TranslationException { Kind: TranslationErrorKind.AllProvidersPaused } => LiveTickOutcome.Refused,
        _ => LiveTickOutcome.SentFailure,
    };
}

/// <summary>
/// <c>architecture-cible.md</c> §9.2 — <b>when LIVE stops itself</b>, as a pure object over an
/// injected clock. It replaces <c>MainWindow.Live.cs</c>' <c>int consecutiveErrors</c>, whose
/// <c>= 0</c> at the end of every non-throwing tick — an EMPTY one included — is why the auto-stop
/// never fired in a calm chat: the loop alternated "empty tick, failing tick" and the counter never
/// reached two while the app trickled failing requests all evening (<c>analyse…</c> A2, S4c).
///
/// <para><b>The rule:</b> <see cref="TranslationPolicy.LiveAutoStopThreshold"/> failures that cost a
/// request, inside <see cref="TranslationPolicy.LiveAutoStopWindowSeconds"/>. One queue answers both
/// of §9.2's triggers, because a translated tick clears it: what the queue holds is "the sent
/// failures since the last success", and the window is what stops a streak from outliving the
/// outage that produced it.</para>
///
/// <para><b>An instance, not a static</b> — two LIVE sessions in one test run must not share a
/// counter, and <c>StartLive</c> gets a clean five for free by building a new one (which is exactly
/// what a player pressing ▶ after an auto-stop must get).</para>
///
/// <para><b>I2 / IS-6 / CI-3</b>: the caller passes <c>now</c>, so the two-minute window is asserted
/// without a <c>Task.Delay</c> and without a wall clock. <see cref="DateTimeOffset"/> and not
/// <c>DateTime</c> — everything else on this path already speaks it.</para>
/// </summary>
internal sealed class LiveErrorTracker
{
    private readonly Queue<DateTimeOffset> _stamps = new();

    /// <summary>Sent failures still inside the window — which is also the length of the current
    /// streak, since a translated tick empties the queue. For the diagnostic log line and for the
    /// tests; nothing decides on it but <see cref="Record"/>.</summary>
    internal int RecentFailures => _stamps.Count;

    /// <summary>
    /// Record what a tick did and answer <b>whether LIVE must stop</b>. §9.2's table, in four arms:
    /// a tick that translated clears everything, an empty one changes nothing, a pause or a refusal
    /// changes nothing (E5-c), and a sent failure is stamped.
    ///
    /// <para>The trim runs on <b>every</b> record and not only on a failure, so the verdict never
    /// depends on when it was last asked: a session that fails four times, runs clean for an hour
    /// and fails once more sees one failure, not five.</para>
    /// </summary>
    internal bool Record(LiveTickOutcome outcome, DateTimeOffset now)
    {
        switch (outcome)
        {
            // The only evidence that the providers are actually working — and the only thing that
            // forgives a streak. Not an empty tick: a calm chat asked them nothing.
            case LiveTickOutcome.Translated:
                Reset();
                break;

            case LiveTickOutcome.SentFailure:
                _stamps.Enqueue(now);
                break;

            // Empty, Paused, Refused: neither a success nor a failure. Written as a comment rather
            // than as empty arms because "nothing happens here" is the ruling, not an omission.
        }

        Trim(now);
        return _stamps.Count >= TranslationPolicy.LiveAutoStopThreshold;
    }

    /// <summary>Forget everything — a fresh session, or a tick that translated.</summary>
    internal void Reset() => _stamps.Clear();

    private static readonly TimeSpan Window =
        TimeSpan.FromSeconds(TranslationPolicy.LiveAutoStopWindowSeconds);

    /// <summary>Drop what can no longer count: stamps older than the window (the edge itself is
    /// inside it), and anything past the queue's hard bound.</summary>
    private void Trim(DateTimeOffset now)
    {
        while (_stamps.Count > 0 && now - _stamps.Peek() > Window) _stamps.Dequeue();
        while (_stamps.Count > LiveTickPolicy.MaxErrorStamps) _stamps.Dequeue();
    }
}
