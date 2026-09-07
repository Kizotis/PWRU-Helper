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
    /// <para>The <c>error is null</c> in the first branch is not redundant: a partial result CAN
    /// arrive with an exception (one source group answered, the other threw), and a claim of "Done"
    /// over a batch that carried a failure is the exact class of lie this type exists to stop. Both
    /// conditions have to hold.</para>
    /// </summary>
    internal static string Status(int lines, int translated, Exception? error)
    {
        // Defensive, and cheap: a caller that counted over a longer list than it rendered must not
        // be able to buy itself a "Done" with a number bigger than the read.
        translated = Math.Clamp(translated, 0, Math.Max(lines, 0));

        if (lines > 0 && translated == lines && error is null)
            return UserMessages.ReadOnceAllTranslated(lines);
        if (translated > 0)
            return UserMessages.ReadOncePartlyTranslated(lines, translated, error);
        return UserMessages.ReadOnceNoneTranslated(lines, error);
    }
}
