namespace PWRUHelper.Services;

/// <summary>One row waiting to be re-translated: the row itself (by <b>reference</b>), the RAW body
/// that was sent for it, the target it was going to, and how many drains have already tried it.
///
/// <para><b>The body is raw and not expanded</b> (I6). <c>MainWindow.TranslateBodiesAsync</c> runs
/// <c>SlangGlossary.Expand</c> itself, so an entry holding the expanded text would be expanded twice
/// on retry — "нужен хил" → "нужен лекарь" → whatever a second pass makes of that. The queue stores
/// exactly what the failing call was given.</para>
///
/// <para><b>The row is a reference, and that is the whole of "no duplicate row"</b>: a drain writes
/// <c>Row.TranslationBody</c>, which raises <c>PropertyChanged</c> and repaints both feeds where the
/// row already is. Nothing here can append anything, because nothing here knows what a feed is.</para></summary>
/// <typeparam name="TRow">The feed-row type. Generic so this file stays inside <c>Services/</c>
/// without depending on <c>OcrResultItem</c> — the queue needs a reference and an attempt count and
/// has no opinion about what a row renders (I2).</typeparam>
internal sealed record PendingRetryEntry<TRow>(TRow Row, string Body, string Target, int Attempts)
    where TRow : class;

/// <summary>
/// <c>architecture-cible.md</c> §9.3 — <b>the rows that failed during a blip, kept so they can be
/// re-translated when the providers come back</b> (ruling E3-h: "capped/failed rows are re-translated
/// after recovery by E5"; DoD V1.3: zero permanently-parenthesised rows in a session that recovered).
///
/// <para><b>What this type deliberately is not.</b> It is not a retry policy, not a scheduler and not
/// a translator: it decides <i>which failures are worth keeping</i>, <i>how many</i>, and <i>in what
/// order</i>, and answers a caller that asks for them. <b>When</b> to drain is the LIVE loop's (the
/// first non-skipped tick, before the new lines) and <b>how</b> is
/// <c>MainWindow.TranslateBodiesAsync</c>' — the same method a fresh line goes through, so slang
/// expansion (I6) and the per-message <c>ru</c>/<c>auto</c> choice (I7) cannot come to have an
/// exception for retried rows.</para>
///
/// <para><b>§9.3 writes the queue as a bare <c>List&lt;(Row, Body, Target)&gt;</c> in the
/// code-behind.</b> It is here instead, generic over the row, for the reason the rest of
/// <c>Services/</c> exists: the three things that can silently make this story a no-op — the bound,
/// the FIFO order, and "an evicted row costs no request" — are then L1 unit tests instead of facts
/// about a window. The <i>field</i> still lives in <c>MainWindow.Live.cs</c> (AC 1), and
/// <c>LiveDedup</c> is still not touched at all, which is what AC 1 is actually protecting.</para>
///
/// <para><b>No lock, deliberately.</b> The LIVE loop is single-threaded on the dispatcher and
/// strictly sequential (I16); a lock here would be a claim that something else touches the queue,
/// which is exactly the claim §9.3 does not want made.</para>
/// </summary>
internal sealed class PendingRetryQueue<TRow> where TRow : class
{
    private readonly List<PendingRetryEntry<TRow>> _entries = new();
    private readonly int _capacity;

    /// <param name="capacity">Rows kept at most, oldest dropped. Defaults to
    /// <see cref="TranslationPolicy.PendingRetryCapacity"/>, which is the feed's own
    /// <c>MainWindow.MaxHistory</c>: the same bound, so the queue can never hold a row the feed has
    /// already forgotten.</param>
    internal PendingRetryQueue(int capacity = TranslationPolicy.PendingRetryCapacity)
        => _capacity = Math.Max(1, capacity);

    internal int Count => _entries.Count;

    internal bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// <b>Which failures are worth keeping.</b> A row is enqueued only when a later attempt could
    /// plausibly answer differently — an outage, a throttle, a block window, a timeout, or a refusal
    /// that never left the machine. Everything else gets the terminal <c>(…)</c> row it always got.
    ///
    /// <para>What is excluded and why, since an over-eager queue spends a player's rate budget on
    /// certainties:</para>
    /// <list type="bullet">
    /// <item><see cref="TranslationErrorKind.BadResponse"/> — the provider answered something we
    ///       could not read, and a retry re-sends the identical body for the identical answer. It is
    ///       not a gap that "recovery" closes. It is also not lost: three in a row open the gate
    ///       (<see cref="TranslationPolicy.BadResponseStrikesToOpen"/>), and the rows that fail after
    ///       that are <c>NotSent</c>/<c>AllProvidersPaused</c> and <i>are</i> queued — so the session
    ///       that recovers still fills them in.</item>
    /// <item><see cref="TranslationErrorKind.QuotaExhausted"/> and
    ///       <see cref="TranslationErrorKind.AuthFailed"/> — a billing period and a key box. Neither
    ///       recovers inside a session, and neither can reach the READ chain at all (it is keyless,
    ///       I8); excluded so that the day a keyed tier joins it, nothing starts re-sending into a
    ///       spent quota.</item>
    /// <item>Anything untyped, and <see cref="TranslationErrorKind.Unknown"/> — §4.4's honest last
    ///       resort. Nothing says a retry would help, so the conservative answer ships.</item>
    /// </list>
    ///
    /// <para><b>The genuine user cancel never reaches here</b>, and not by a filter: the call sites
    /// enqueue from their <i>generic</i> catch, while a cancel is taken by the filtered
    /// <c>OperationCanceledException</c> catch above it (I3). A cancelled read's rows are FINISHED,
    /// not failed (<see cref="UserMessages.ReadCancelledRow"/>) — re-sending them would spend the
    /// request the player just declined.</para>
    /// </summary>
    internal static bool IsRetryable(Exception ex)
        => ex is TranslationException te
           && (te.NotSent
               || te.Kind is TranslationErrorKind.RateLimited
                          or TranslationErrorKind.Blocked
                          or TranslationErrorKind.Timeout
                          or TranslationErrorKind.Network
                          or TranslationErrorKind.Unavailable
                          or TranslationErrorKind.AllProvidersPaused);

    /// <summary>Put a row on the queue, oldest dropped past the capacity.
    ///
    /// <para>A row already queued is <b>replaced</b> rather than added a second time. Nothing in the
    /// loop can queue one twice today — every row is enqueued once, in the catch of the call that
    /// created it — but a row present twice would be translated twice in one drain, and "this row is
    /// here at most once" is cheaper to guarantee than to argue about.</para></summary>
    internal void Enqueue(TRow row, string body, string target, int attempts = 0)
    {
        _entries.RemoveAll(e => ReferenceEquals(e.Row, row));
        _entries.Add(new PendingRetryEntry<TRow>(row, body, target, attempts));
        // Oldest first: the queue is bounded by the same 50 the feed is, so what falls off the front
        // here is a row that is about to fall off the feed anyway.
        while (_entries.Count > _capacity) _entries.RemoveAt(0);
    }

    /// <summary>
    /// Empty the queue and answer the entries that are still worth translating, <b>in FIFO order</b>.
    ///
    /// <para><paramref name="stillOnScreen"/> is checked HERE and not at enqueue time, because a row
    /// is evicted from the feed by the <c>MaxHistory</c> trim long after it was queued — by the LIVE
    /// loop's trim or by read-once's. An entry whose row has scrolled away is dropped <b>before</b>
    /// anything is sent, so a row nobody can see never costs a request (AC 3).</para>
    ///
    /// <para>It empties the queue on the way out, so a drain that throws cannot leave the same
    /// entries queued <i>and</i> in flight. Putting them back is the caller's job and it owes the
    /// entries a <c>finally</c>-shaped path: see <c>MainWindow.DrainPendingRetryAsync</c>.</para>
    /// </summary>
    internal List<PendingRetryEntry<TRow>> TakeAll(Func<TRow, bool> stillOnScreen)
    {
        var due = _entries.Where(e => stillOnScreen(e.Row)).ToList();
        _entries.Clear();
        return due;
    }

    /// <summary>Whether this entry has any attempt left, or has become the terminal <c>(…)</c> row
    /// AC 5 asks for. Counted in ATTEMPTS MADE: an entry enters at 0, so with
    /// <see cref="TranslationPolicy.PendingRetryMaxAttempts"/> = 2 a drain that fails twice gives
    /// up.</summary>
    internal static bool GivesUpAfter(PendingRetryEntry<TRow> entry)
        => entry.Attempts + 1 >= TranslationPolicy.PendingRetryMaxAttempts;

    /// <summary>■ Stop, and the start of a new session. A queue that outlived the loop that filled it
    /// would resurrect old rows onto a feed that has been cleared.</summary>
    internal void Clear() => _entries.Clear();
}
