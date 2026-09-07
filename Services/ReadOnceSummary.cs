namespace PWRUHelper.Services;

/// <summary>
/// What a "read the screen once" is allowed to claim, as arithmetic — the four-way branch of
/// <c>ux-mode-degrade.md</c> §3.3 written as a pure function of what the read produced, so the
/// rule that matters can be tested without a window: <b>"Done" may only ever appear when every
/// line read has a translation</b> (UX hint 4, TP-ONCE-02, DoD V1.5).
///
/// <para>It exists because the claim used to be made from the wrong number. <c>ReadRegionOnceAsync</c>
/// wrote <c>Done — {n} line(s) translated.</c> from <c>sentences.Count</c> — the number of lines
/// READ — while the method that filled them swallowed its failure and returned, so a total outage
/// printed "Done" over a list of "(no internet connection)" rows. A player told Done over an empty
/// result presses the button again, and again: the false success feeds the block that caused it
/// (amplifier A7).</para>
///
/// <para>The counting half is deliberately not re-invented here. "This is a translation" already has
/// exactly one definition in this app — the test <c>CachingTranslator.IsCacheable</c> applies before
/// it stores anything, i.e. non-empty and not starting with "(" (I4's failure marker) — and both
/// sides now read it from <see cref="IsTranslation"/>. Two definitions would mean a row the cache
/// refuses to keep still counting towards a "Done".</para>
///
/// <para>The sentences themselves are <b>not</b> here: they are in <see cref="UserMessages"/> with
/// the rest of the copy deck (ruling GAP-4), so E7.S1's copy pass has one file to open. This type
/// owns which one is chosen, which is the part a test can prove wrong.</para>
/// </summary>
internal static class ReadOnceSummary
{
    /// <summary>The app's one definition of "this string is a translation", in the one shape a
    /// caller outside <c>CachingTranslator</c> can read. A provider's per-line fallback (E3.S8)
    /// returns "(skipped — …)" placeholders for the lines it could not do, and
    /// <c>TranslateBodiesAsync</c> fills a gap with the source text rather than a null, so neither
    /// the length of the result list nor its non-nullness says anything about how much was
    /// translated.</summary>
    internal static bool IsTranslation(string? value)
        => !string.IsNullOrEmpty(value) && !value.StartsWith('(');

    /// <summary>How many of a batch's results are translations — the <c>{k}</c> of §3.3, and the
    /// number "Done" has to equal.</summary>
    internal static int CountTranslated(IReadOnlyList<string> results)
    {
        int n = 0;
        foreach (var r in results)
            if (IsTranslation(r)) n++;
        return n;
    }

    /// <summary>
    /// The status a finished read shows, from what it actually produced.
    /// <paramref name="error"/> is the failure to name, or null when there was none.
    ///
    /// <para>The <c>error is null</c> in the first branch is a guard against a shape today's caller
    /// cannot quite produce, and it is kept deliberately. <c>TranslateBodiesAsync</c> awaits its two
    /// source groups in sequence, so the first throw aborts the batch and the caller reports
    /// <c>(0, error)</c> — a full count alongside a live exception is unreachable from THAT call
    /// site. It is one edit away from being reachable, though (a batch that tolerates a partial
    /// failure is E5.S3's whole subject), and a claim of "Done" over a batch that carried a failure
    /// is the exact class of lie this type exists to stop. Both conditions have to hold.</para>
    ///
    /// <para><b>The fourth branch is ruling E5-g's</b> (E5.S3). A read whose every line failed
    /// because every engine is inside a block window is not "none could be translated" plus a
    /// reason — it is the paused state, it cost no request, and §3.3 gives it its own sentence.
    /// It is reachable only now that the pause is REPORTED by the chain instead of checked before
    /// the capture: that check could not know <paramref name="lines"/>, and it refused reads whose
    /// answers were already in the cache. <paramref name="pausedTryAgainIn"/> is the "{t}" the caller
    /// has already rendered, exactly as <see cref="UserMessages.ReadOncePaused"/> documents —
    /// formatting a countdown is not <c>Services/</c>' job (I2).</para>
    ///
    /// <para>Order matters between branches two and four: a read that translated SOMETHING (the
    /// cache served part of it) reports the counts and names the pause as its reason, because
    /// "all engines are paused" alone would hide the lines the player did get.</para>
    /// </summary>
    /// <param name="liveIsRunning"><b>Amendment A7's fork</b>, and only the paused branch reads it.
    /// §3.3's paused row promises the rows "fill in when one is back"; the E5.S3 retry queue is
    /// drained by the LIVE LOOP, so that promise is true for a read taken while the loop runs and
    /// false for one taken with it stopped, where nothing is ever coming. One sentence cannot be
    /// honest in both states, and §1 principle 4 does not allow picking the friendlier one — so the
    /// loop's state is passed in by the code-behind, which is the only thing that can see it.</param>
    internal static string Status(int lines, int translated, Exception? error,
                                 string? pausedTryAgainIn = null, bool liveIsRunning = false)
    {
        // Defensive, and cheap: a caller that counted over a longer list than it rendered must not
        // be able to buy itself a "Done" with a number bigger than the read.
        translated = Math.Clamp(translated, 0, Math.Max(lines, 0));

        if (lines > 0 && translated == lines && error is null)
            return UserMessages.ReadOnceAllTranslated(lines);
        if (translated > 0)
            return UserMessages.ReadOncePartlyTranslated(lines, translated, error);
        if (IsAllPaused(error))
            return UserMessages.ReadOncePaused(lines, pausedTryAgainIn, liveIsRunning);
        return UserMessages.ReadOnceNoneTranslated(lines, error);
    }

    /// <summary>Whether the failure a read came back with is "every engine is inside a block window"
    /// — the one kind that is a STATE and not an error (<c>ux-mode-degrade.md</c> §2.1), so it gets
    /// §3.3's paused sentence rather than a failure one. Typed, never a string match, and exposed so
    /// the code-behind can decide whether it owes the sentence a countdown without re-deriving the
    /// same test.</summary>
    internal static bool IsAllPaused(Exception? error)
        => error is TranslationException { Kind: TranslationErrorKind.AllProvidersPaused };
}
