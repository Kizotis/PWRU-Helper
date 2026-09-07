namespace PWRUHelper.Services;

/// <summary>
/// What one press of <b>Test key</b> found out (E6.S5) — the typed answer the two providers hand
/// back, and the only thing <see cref="UserMessages.KeyTestSentence"/> is allowed to read.
///
/// <para>It carries a <see cref="TranslationErrorKind"/> and never a status code, which is the
/// whole point of routing a key test through the same pipeline a translation takes: E1.S3's
/// classifier is the one place in this app that decides what a 403 means, and the code-behind must
/// not re-decide it beside a button (T5). A result is therefore a Kind plus the two facts a Kind
/// cannot carry — what the provider said about the quota, and whether the call was refused by the
/// gate before anything was sent.</para>
///
/// <para><b>The countdown is a NUMBER here, never text</b> (I2). <c>MainWindow</c> renders it with
/// the same <c>CountdownText</c> the LIVE loop and read-once use, so the three cannot come to
/// disagree about what a countdown looks like; <c>Services/</c> only counts the seconds
/// (<see cref="LiveTickPolicy.CountdownSeconds"/>, which exists for exactly this split).</para>
/// </summary>
/// <param name="Ok">The key was accepted. <c>false</c> covers "refused" AND "the key works but the
/// quota is spent" — the deck gives that its own sentence (§3.7), so it is not a success.</param>
/// <param name="Kind">Why it is not ok, in the app's one error vocabulary. Null when it is ok.</param>
/// <param name="UsageText">What the provider volunteered about the quota, already worded by
/// <see cref="UserMessages"/> — DeepL's <c>/v2/usage</c> answers it for free, Azure answers nothing.</param>
/// <param name="Paused">The gate refused the call and <b>no request was made</b> (ruling E3-b's
/// <c>NotSent</c>, seen from the key test). A player diagnosing a paused provider is exactly who
/// presses this button, so it may not be reported as a refused key.</param>
/// <param name="SecondsUntilRetry">How long that pause still has to run, or null when there is
/// nothing honest to count down to.</param>
internal readonly record struct KeyTestResult(
    bool Ok,
    TranslationErrorKind? Kind = null,
    string? UsageText = null,
    bool Paused = false,
    int? SecondsUntilRetry = null)
{
    internal static KeyTestResult Works(string? usageText = null) => new(true, null, usageText);

    internal static KeyTestResult Failed(TranslationErrorKind kind, string? usageText = null)
        => new(false, kind, usageText);

    /// <summary>The gate said no. The Kind is the one the pause is FOR — the sentence names the
    /// pause, not the Kind, but the Kind is what a future reader (and E7.S1) needs.</summary>
    internal static KeyTestResult PausedFor(TranslationErrorKind kind, int? secondsUntilRetry)
        => new(false, kind, null, true, secondsUntilRetry);
}
