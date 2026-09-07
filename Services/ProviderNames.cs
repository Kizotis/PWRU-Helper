namespace PWRUHelper.Services;

/// <summary>
/// <b>The <c>{P}</c> of the copy deck</b>: a provider id → the one name a player ever reads for it
/// (<c>ux-mode-degrade.md</c> §3.0, amendment <b>A2</b>, open question OQ-2). One table, one
/// direction, and every surface that names an engine reads it — the sentences of
/// <see cref="UserMessages"/>, the compact overlay's short forms, and E7.S3's chip. A second
/// spelling of a name is UX-DR19 applied to a noun instead of to a sentence, and it is how a chip
/// comes to say "Google (old)" over a status line that says "Google (backup)".
///
/// <para><b>Why "Google (backup)" and not "Google (old)"</b> (A2): a name should say a provider's
/// <i>role</i>, not its age. <c>google-gtx</c> is the tier under <c>google-dict</c>, which is what
/// the player needs to understand when they read it in a fallback notice.</para>
///
/// <para><b>Nothing here is ever invented</b> (§3.0 rule 1). An id this table does not know — and
/// <c>null</c>, which is the commonest case, because a <see cref="TranslationException"/> raised
/// outside <c>HttpProviderCore</c> carries no provider — answers <c>null</c>, and the caller renders
/// the sentence's <c>{P}</c>-less form ("The translation service …", "Your free translation quota
/// …"). It never throws: an unknown id at a player mid-fight must cost a name, not a crash.</para>
///
/// <para><b>Keyed off <see cref="ProviderIds"/>' constants and never off literals.</b> The ids
/// themselves are internals and may not appear in copy (§3's "no provider internal"): this file is
/// the boundary where one becomes the other.</para>
///
/// <para><b>I2</b>: no UI type, no formatting, no settings read — like every other file under
/// <c>Services/</c>, so the names are assertable headlessly.</para>
/// </summary>
internal static class ProviderNames
{
    /// <summary>§3.0's table, in full. Null for an id nobody has a name for.</summary>
    internal static string? Display(string? providerId) => providerId switch
    {
        ProviderIds.GoogleDict => "Google",
        ProviderIds.Edge => "Edge",
        ProviderIds.GoogleGtx => "Google (backup)",
        ProviderIds.DeepL => "DeepL",
        ProviderIds.Azure => "Azure",
        ProviderIds.Bergamot => "Offline engine",
        _ => null,
    };

    /// <summary>The short form §3.0 gives the compact overlay and the chip. It differs from
    /// <see cref="Display"/> for exactly one provider — <c>bergamot</c>, whose long name is two
    /// words the overlay's 40-character line cannot spare — so it is written as that one exception
    /// rather than as a second table nobody would keep in step.</summary>
    internal static string? Short(string? providerId)
        => string.Equals(providerId, ProviderIds.Bergamot, StringComparison.Ordinal)
            ? "Offline"
            : Display(providerId);
}
