namespace PWRUHelper.Services;

/// <summary>What one engine is doing, in the four words a player can act on
/// (<c>ux-mode-degrade.md</c> §2.3). Deliberately NOT <see cref="GateState"/>: that enum is the
/// breaker's own vocabulary (<c>Closed</c> / <c>Open</c> / <c>HalfOpen</c>), it answers a question
/// about a circuit rather than about a service, and ruling <b>E3-a</b> says in so many words that a
/// UI reading <c>State == Open</c> would show a pause that has already elapsed — the state
/// deliberately outlives its window.</summary>
internal enum EngineState
{
    /// <summary>Configured, not blocked, and not the tier that answered the last call.</summary>
    Ready,

    /// <summary>The provider that served the most recent translation
    /// (<c>ChainTranslator.Outcome.ProviderId</c> — who <i>answered</i>, R-3).</summary>
    Answering,

    /// <summary>Inside a block window right now: <c>BlockedUntil &gt; now</c> (ruling E3-a).</summary>
    Paused,

    /// <summary>Not part of any chain the user has: no key, not shipped, not installed. It is not an
    /// error and never renders as one (§2.3's <c>— not set</c> / <c>— not available</c>).</summary>
    Off,
}

/// <summary>One line of the chip's tooltip — one provider, in <see cref="ProviderIds.All"/> order.
///
/// <para><b>Values only.</b> No brush, no glyph, no formatted duration: <see cref="PausedUntil"/> is
/// an instant and the countdown that renders it is <c>MainWindow</c>'s (<b>I2</b> —
/// <c>Services/</c> counts, the code-behind formats). <see cref="DisplayName"/> is the exception
/// worth stating: it is a <i>name</i>, from the one table <see cref="ProviderNames"/> owns (OQ-2),
/// and letting the chip re-derive it is exactly how a second spelling gets born.</para></summary>
/// <param name="ProviderId">The internal id. It may not be rendered (I11 / §3's "no provider
/// internal"); it is here so a caller can ask about a specific tier without matching on prose.</param>
/// <param name="DisplayName"><see cref="ProviderNames.Display"/>, or the id's own fallback when the
/// table has no name — never invented.</param>
/// <param name="PausedUntil">Non-null only for <see cref="EngineState.Paused"/>.
/// <see cref="DateTimeOffset.MaxValue"/> is the <c>AuthFailed</c> sentinel: a pause with no honest
/// countdown, whose exit is re-saving the key and not a timer.</param>
/// <param name="Kind">What the gate last recorded, for the tooltip's parenthetical. Never a status
/// code and never a provider's own message.</param>
/// <param name="OffReason">Why this tier is not there, for <see cref="EngineState.Off"/> only.</param>
internal sealed record EngineLine(
    string ProviderId,
    string DisplayName,
    EngineState State,
    DateTimeOffset? PausedUntil,
    TranslationErrorKind? Kind,
    string? OffReason)
{
    /// <summary>A pause the app can honestly count down to — i.e. not the <c>AuthFailed</c>
    /// sentinel. It is what decides whether the 1 Hz tick has anything left to repaint.</summary>
    internal bool HasLiveCountdown =>
        State == EngineState.Paused && PausedUntil is { } until && until != DateTimeOffset.MaxValue;
}

/// <summary>
/// <b>The whole chain, as values, at one instant</b> — what the provider chip and its tooltip are
/// rendered from (<c>ux-mode-degrade.md</c> §2.1–§2.3, rulings <b>R-2 / R-3 / OQ-c</b>).
///
/// <para><b>Pure, and that is the point.</b> <see cref="Of"/> takes snapshots and an outcome and
/// returns this record: no gate is entered, no clock is read, nothing is registered. The eight
/// states of §2.1 are therefore a unit test over eight literals rather than eight windows
/// (<c>ProviderChipTests</c>). The impure half — who the tiers are, what their gates say right now —
/// is <see cref="TranslationChains.EngineStatus"/>'s, in one place, so the code-behind never names
/// <see cref="ProviderGates"/> (TP-START-02).</para>
///
/// <para><b>No events</b> (ruling OQ-c/R-2 overruling OQ-4): the UI polls this at 1 Hz from the
/// countdown timer it already runs, and <c>Services/</c> stays passive.</para>
///
/// <para><b>I2</b>: no UI type, no format string, no culture. Every duration here is a
/// <see cref="DateTimeOffset"/>.</para>
/// </summary>
/// <param name="Lines">One per <see cref="ProviderIds.All"/>, in that order — the tooltip lists the
/// whole chain, including the tiers the user does not have (§2.3: a chain line that quietly
/// disagrees with the About tab's is worse than one that says why).</param>
/// <param name="ReadTiers">The ids the read chain actually built, in chain order. The chip speaks
/// for the read path, so "all paused" is a question about THESE and not about every id that has a
/// gate.</param>
/// <param name="LastAnswered">Who served the last call — <c>LastOutcome.ProviderId</c>, null on a
/// failing exit and before the first translation. <b>Never guessed</b> (§3.0 rule 1).</param>
/// <param name="FellBack">The last call was served by a tier below the first one, and something
/// above it was skipped — §2.1's <b>S2</b>, and ruling <b>E3-b</b>'s "skipped" vs "tried and
/// failed" is what makes it different from S3.</param>
/// <param name="StateKnown">Whether <c>provider-state.json</c> has been read yet (ruling
/// <b>E6-a</b>). False for the first moments of a session — the chip says "checking…" rather than
/// claiming a health it has not verified.</param>
/// <param name="Now">The GATES' clock, never the caller's (IS-6).</param>
internal sealed record EngineStatus(
    IReadOnlyList<EngineLine> Lines,
    IReadOnlyList<string> ReadTiers,
    string? LastAnswered,
    bool FellBack,
    bool StateKnown,
    DateTimeOffset Now)
{
    /// <summary>The tooltip's <c>— not available</c> / <c>— not installed</c> / <c>— not set</c>
    /// reasons. They are English words a player reads, so they live in
    /// <see cref="UserMessages"/>; these constants are the KEYS this record hands it.</summary>
    internal const string NotAvailable = "not-available";
    internal const string NotInstalled = "not-installed";
    internal const string NotSet = "not-set";

    /// <summary>The line for an id, or null if the id is not in the table.</summary>
    internal EngineLine? For(string providerId)
    {
        foreach (var line in Lines)
            if (string.Equals(line.ProviderId, providerId, StringComparison.Ordinal)) return line;
        return null;
    }

    /// <summary>Every rung of the READ chain is inside a block window — §2.1's <b>S5</b>. The same
    /// question <c>ChainTranslator.PauseNow()</c> answers, asked of a snapshot instead of of the
    /// gates, so the chip and the LIVE loop cannot come to disagree about what "all paused"
    /// means.</summary>
    internal bool AllReadTiersPaused
    {
        get
        {
            if (ReadTiers.Count == 0) return false;
            foreach (var id in ReadTiers)
                if (For(id) is not { State: EngineState.Paused }) return false;
            return true;
        }
    }

    /// <summary>The earliest instant a paused READ tier comes back, or null when none of them has an
    /// honest one. The chip's <c>{t}</c>; the caller turns it into seconds and then into text.</summary>
    internal DateTimeOffset? SoonestReadRetry
    {
        get
        {
            DateTimeOffset? earliest = null;
            foreach (var id in ReadTiers)
                if (For(id) is { HasLiveCountdown: true, PausedUntil: { } until }
                    && (earliest is null || until < earliest)) earliest = until;
            return earliest;
        }
    }

    /// <summary>
    /// <b>Whether the chip still needs the 1 Hz tick</b> (NFR7's "never runs idle", and the stop rule
    /// E7.S2's <c>CountdownTick</c> was told to widen). True only while some READ tier is inside a
    /// window with a real end to it: a chip whose text cannot change until something else happens
    /// must not keep a timer alive, and the <c>AuthFailed</c> sentinel
    /// (<see cref="DateTimeOffset.MaxValue"/>) is precisely that case — it counts down to nothing,
    /// for ever, and its way out is a key save rather than a second.
    /// </summary>
    internal bool NeedsTick
    {
        get
        {
            // Ruling E6-a's first second: with the file unread there is nothing to count down TO,
            // and "checking…" ends when a task completes rather than when a second passes. Without
            // this line a pause left over in the registry from earlier in the process could start
            // the countdown before first paint, which is the one thing I10 is about.
            if (!StateKnown) return false;

            foreach (var id in ReadTiers)
                if (For(id) is { HasLiveCountdown: true }) return true;
            return false;
        }
    }

    /// <summary>
    /// Build the whole picture from values. <b>Nothing here has a side effect</b> — no
    /// <c>TryEnter</c>, no gate creation, no file — which is what lets the chip poll it at 1 Hz
    /// (ruling R-2: <c>Snapshot()</c> is "side-effect free by contract").
    /// </summary>
    /// <param name="readTiers">The read chain's ids, in chain order.</param>
    /// <param name="gates">Every live gate's snapshot (<c>ProviderGates.All()</c>). A missing id
    /// means nothing has ever been asked of that provider, which reads exactly as "not blocked" —
    /// never as an error.</param>
    /// <param name="configured">The ids the user actually has: the read tiers, plus a keyed
    /// provider whose credential the write chain would really build a tier from. An id outside it
    /// is <see cref="EngineState.Off"/> with a reason, not a failure.</param>
    /// <param name="outcome">The last call's account (R-3), read ONCE by the caller: reading
    /// <c>LastOutcome</c> twice in one repaint can mix two calls' accounts.</param>
    internal static EngineStatus Of(
        IReadOnlyList<string> readTiers,
        IReadOnlyDictionary<string, GateSnapshot> gates,
        IReadOnlyCollection<string> configured,
        ChainTranslator.Outcome? outcome,
        bool stateKnown,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(readTiers);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(configured);

        var lines = new List<EngineLine>(ProviderIds.All.Count);
        foreach (var id in ProviderIds.All)
        {
            var snapshot = gates.TryGetValue(id, out var s) ? s : null;
            var name = ProviderNames.Display(id) ?? id;

            // Off FIRST, and before the gate is even consulted. A provider the user does not have
            // can still own a gate — Azure's survives the key being cleared, for the rest of the
            // session — and reporting that stale window as a PAUSE would tell a player to wait for
            // an engine that is not in any chain (§2.3: "tiers that are not configured render
            // — not set, not an error").
            if (!configured.Contains(id))
            {
                lines.Add(new EngineLine(id, name, EngineState.Off, null, null, OffReason(id)));
                continue;
            }

            // Ruling E3-a, written the same way ChainTranslator.BlockedUntil writes it: the INSTANT
            // against the gates' own clock, never GateState.Open, which outlives its window.
            if (snapshot?.BlockedUntil is { } until && until > now)
            {
                lines.Add(new EngineLine(id, name, EngineState.Paused, until, snapshot.LastKind, null));
                continue;
            }

            var answering = outcome?.ProviderId is { } p && string.Equals(p, id, StringComparison.Ordinal);
            lines.Add(new EngineLine(id, name,
                answering ? EngineState.Answering : EngineState.Ready, null, snapshot?.LastKind, null));
        }

        return new EngineStatus(lines, readTiers, outcome?.ProviderId,
            FellBackTo(readTiers, outcome), stateKnown, now);
    }

    /// <summary>§2.1's <b>S2</b>, and it is two conditions rather than one: a lower tier answered
    /// <i>and</i> something above it was passed over. <c>Skipped</c> alone is not enough (a chain
    /// whose first tier answered skips nothing), and "not the first tier" alone is not either — a
    /// one-tier chain's only tier is always its first.</summary>
    private static bool FellBackTo(IReadOnlyList<string> readTiers, ChainTranslator.Outcome? outcome)
        => outcome is { ProviderId: { } answered, Skipped.Count: > 0 }
           && readTiers.Count > 0
           && !string.Equals(readTiers[0], answered, StringComparison.Ordinal);

    /// <summary>Why a tier the user does not have is missing — and each of the three is a different
    /// sentence, because "not installed" and "no key" ask different things of the reader. Ruling
    /// <b>E3-d</b>: Edge is not shipped (U2 is owner-blocked), so its line says so rather than
    /// disappearing; §2.3's mockup lists it, and a tooltip that silently drops a row the About tab
    /// shows is worse than one that explains it.</summary>
    private static string OffReason(string providerId) => providerId switch
    {
        ProviderIds.Edge => NotAvailable,        // E3-d — no EdgeTranslator exists to build
        ProviderIds.Bergamot => NotInstalled,    // E8 has not shipped
        _ => NotSet,                             // a key nobody has entered
    };
}
