namespace PWRUHelper.Services;

/// <summary>
/// One rung of the chain: which provider it is, the gate that says whether it is paused, and the
/// translator itself.
///
/// <para><b>Why the id is here</b> (ruling E3-c / deviation D-3). <c>architecture-cible.md</c> §6.1
/// describes the tier as a gate plus a translator, "since the gate already has an <c>Id</c> for
/// logging". It does not, and deliberately so: <see cref="ProviderGates.For"/> closes over the id
/// precisely <i>because</i> a gate does not know its own (<c>ProviderGates.cs:118-128</c>) — the
/// registry is the only thing that does. Ruling R-3 needs a name for
/// <see cref="ChainTranslator.Outcome.ProviderId"/> and for the per-tier warning, so the tier
/// carries one rather than the gate growing one.</para>
/// </summary>
internal readonly record struct ChainTier(string ProviderId, ProviderGate Gate, ITranslator Translator);

/// <summary>
/// <c>architecture-cible.md</c> §6 — the ordered provider chain, and the second half of Epic 2's
/// answer: E2 put a breaker in front of an endpoint that can refuse on request #1, and this is what
/// makes a paused tier <b>skipped</b> rather than fatal. Tiers are tried in order; a tier whose gate
/// is inside a block window costs no request and no measurable delay; a tier that tries and fails
/// hands over to the next one; and when every tier was skipped the caller gets exactly one
/// <see cref="TranslationErrorKind.AllProvidersPaused"/> carrying the <b>earliest</b> of the
/// remembered windows.
///
/// <para>It replaces <c>FallbackTranslator</c>, which knew two engines, had no idea what a gate was,
/// and spent a request on a provider that had already refused.</para>
///
/// <para><b>The chain never calls <c>TryEnter</c></b> (ruling E3-a / deviation D-1). §6.2's
/// pseudocode did, but since E2.S5 every request already consults the gate inside
/// <see cref="HttpProviderCore"/> — which remains the one admission point. A second
/// <c>TryEnter</c> per logical call would halve the §5.4 rate ceiling and, worse, would take the
/// half-open probe the chain has no way to report (<see cref="ITranslator"/> has no channel for a
/// probe token — I1), leaving the gate half-open for a whole window: R-01 introduced from above.
/// So the chain reads <see cref="ProviderGate.Snapshot"/>, which is side-effect free by contract,
/// and admission, waiting, the probe and the report stay inside the core, once.</para>
///
/// <para><b>And it skips on <c>BlockedUntil</c>, never on <c>State == Open</c></b> — the single most
/// dangerous line in this file. <see cref="GateState.Open"/> deliberately covers the whole window
/// <i>including the moment it has elapsed</i>: the gate becomes half-open only when a caller
/// actually takes the probe. A chain that skipped on the state would skip the tier forever — nobody
/// would call <c>TryEnter</c>, so nobody would take the probe, so the gate would never close.</para>
///
/// <para><b>I2</b>: no WPF type, no dispatcher, no settings read, no formatting — the countdown
/// <c>{t}</c> §3.1 wants is E7.S1's, and <see cref="Outcome.RetryAt"/> is the instant it renders.
/// <b>I3</b>: one filtered <c>OperationCanceledException</c> catch, first, in the one loop both
/// public methods share. <b>I5</b>: the chain never pads a short batch — that policy is E3.S8's and
/// it lives inside a provider.</para>
/// </summary>
public sealed class ChainTranslator : ITranslator
{
    /// <summary>
    /// Ruling <b>R-3</b>: what the last call actually did, so the code-behind can say "DeepL is
    /// paused, this came from Google" without <see cref="ITranslator"/> growing a return type (I1).
    /// Immutable and replaced wholesale after every call — success and failure both.
    ///
    /// <para><see cref="RetryAt"/> is the <b>earliest</b> instant among the tiers that were skipped,
    /// i.e. when the chain first gets a rung back, and it is null when nothing was skipped. On the
    /// all-paused exit it is exactly what the <c>AllProvidersPaused</c> carries. <b>On the failure
    /// exit the two differ on purpose</b> and E7 must not treat them as one: the thrown exception
    /// carries the failing provider's own <c>RetryAt</c> (a <c>Retry-After</c>, say), while this
    /// field still answers "when does a skipped rung come back". <see cref="Kind"/> is null on
    /// success and otherwise the kind the caller was thrown.</para>
    ///
    /// <para>Two instants it can hold that a countdown must not render raw, both inherited from the
    /// gate and neither this class's to clamp (formatting is E7's, I2): an instant already in the
    /// <b>past</b> (a ceiling refusal is <c>now + a few ms</c>, and a skip with no window at all
    /// records <c>now</c>), and <see cref="DateTimeOffset.MaxValue"/> — the <c>AuthFailed</c>
    /// sentinel, whose real exit is re-saving the key and not a timer.</para>
    ///
    /// <para>Nothing reads this yet — E7.S1 / E7.S3 do. It is written now so E7 does not have to
    /// reopen this file.</para>
    /// </summary>
    internal sealed record Outcome(
        string? ProviderId,
        IReadOnlyList<(string ProviderId, string Reason)> Skipped,
        DateTimeOffset? RetryAt,
        TranslationErrorKind? Kind);

    /// <summary>The reason recorded for a tier skipped inside a window the gate has no kind for —
    /// a window restored from <c>provider-state.json</c> before anything failed in this process.</summary>
    private const string PausedReason = "Paused";

    private readonly IReadOnlyList<ChainTier> _tiers;
    private Outcome? _lastOutcome;

    /// <summary>Read through <see cref="Volatile"/> because the code-behind may read it from the
    /// dispatcher while a second call is already in flight on a pool thread. The record itself is
    /// immutable, so a reader either sees the previous call's account or this one's — never a
    /// half-written one.</summary>
    internal Outcome? LastOutcome => Volatile.Read(ref _lastOutcome);

    internal ChainTranslator(IReadOnlyList<ChainTier> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        // A chain with no tiers is a composition bug, not a runtime state. Inventing an
        // AllProvidersPaused with no retryAt at call time would hide it behind a sentence that
        // tells the player to wait for something that is never coming.
        if (tiers.Count == 0)
            throw new ArgumentException("A chain needs at least one tier.", nameof(tiers));
        // Copied, not aliased: the count above is checked ONCE, and a caller that keeps its list and
        // later clears it would walk straight past that guard into the AllProvidersPaused exit with
        // an empty skipped set and a null retryAt — the sentence with nothing behind it this
        // constructor exists to prevent. (E3.S7 rebuilds chains; `Of` hands in a local it still
        // holds.) A chain is also read from a pool thread while the code-behind holds the reference,
        // so a live list would be a mid-iteration mutation away from a raw InvalidOperationException
        // thrown OUTSIDE the try, past every catch in RunAsync.
        _tiers = tiers.ToArray();
    }

    /// <summary>
    /// Build a chain from provider ids, resolving each gate through the registry here so the
    /// <b>composition site never names <see cref="ProviderGates"/></b> — which is not a style
    /// preference: <c>ProviderStateStoreTests.No_startup_path_mentions_ProviderGates</c> (TP-START-02)
    /// asserts that <c>ProviderGates.Flush()</c> is the only reference to the registry outside
    /// <c>Services/</c> in the whole app, and the next one must be a decision rather than a
    /// convenience.
    ///
    /// <para><see cref="ProviderGates.For"/> <b>constructs only</b> — it reads no file — so calling
    /// it from a constructor keeps I10: the first <c>TryEnter</c>, inside the core and after first
    /// paint, is still what loads <c>provider-state.json</c>.</para>
    /// </summary>
    internal static ChainTranslator Of(params (string Id, ITranslator Translator)[] tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        var built = new List<ChainTier>(tiers.Length);
        foreach (var (id, translator) in tiers)
        {
            // Composition-time, like the empty-chain guard below it and for the same reason: these
            // are bugs in a caller's argument list, and every one of them fails as DEGRADED RUNTIME
            // BEHAVIOUR rather than loudly if it is let through. A blank id registers a gate under
            // "" that no E7 status list will ever show; a repeated id gives two tiers ONE gate, so
            // the second is skipped whenever the first is, refused by the same token bucket, and
            // counted twice in Outcome.Skipped — a fallback that structurally cannot be one.
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("A tier needs a provider id.", nameof(tiers));
            if (translator is null)
                throw new ArgumentException($"Tier '{id}' has no translator.", nameof(tiers));
            if (built.Any(t => t.ProviderId == id))
                throw new ArgumentException($"'{id}' appears twice: two tiers would share one gate.",
                    nameof(tiers));

            built.Add(new ChainTier(id, ProviderGates.For(id), translator));
        }
        return new ChainTranslator(built);
    }

    public Task<string> TranslateAsync(string text, string source, string target,
        CancellationToken ct = default)
        => RunAsync(t => t.TranslateAsync(text, source, target, ct), ct);

    public Task<List<string>> TranslateLinesAsync(IReadOnlyList<string> lines, string source,
        string target, CancellationToken ct = default)
        => RunAsync(t => t.TranslateLinesAsync(lines, source, target, ct), ct);

    /// <summary>
    /// §6.2's algorithm, written <b>once</b>. The section says it is identical for
    /// <c>TranslateAsync</c> and <c>TranslateLinesAsync</c>, and two copies of this loop would be
    /// two places for the OCE filter to rot — which is how this project lost three releases the
    /// first time. A batch is one call to the chain: the per-line fallback a provider may run
    /// inside itself is that provider's business (§6.3).
    /// </summary>
    private async Task<T> RunAsync<T>(Func<ITranslator, Task<T>> call, CancellationToken ct)
    {
        // (id, reason, retryAt) rather than the record's (id, reason): the retryAt is what AC 3's
        // "earliest" is computed from, and it is not part of what E7 renders per tier.
        var skipped = new List<(string ProviderId, string Reason, DateTimeOffset RetryAt)>();
        TranslationException? lastFailure = null;

        for (int i = 0; i < _tiers.Count; i++)
        {
            var tier = _tiers[i];
            ct.ThrowIfCancellationRequested();

            // Ruling E3-a: BlockedUntil against the GATE's own clock, never State and never the
            // wall clock. See the class comment for why both halves of that sentence are load-bearing.
            var snapshot = tier.Gate.Snapshot();
            if (snapshot.BlockedUntil is { } until && until > tier.Gate.Now())
            {
                skipped.Add((tier.ProviderId, snapshot.LastKind?.ToString() ?? PausedReason, until));
                continue;
            }

            try
            {
                var value = await call(tier.Translator).ConfigureAwait(false);
                Publish(tier.ProviderId, skipped, null);
                return value;
            }
            // I3, and it is FIRST for a reason: a genuine user cancel stops the chain here and now.
            // A timeout is an OCE too, with the caller's token NOT cancelled — it falls to the
            // catches below and becomes the next tier's turn, which is the whole point of the filter.
            //
            // LastOutcome is deliberately NOT published here (nor by the ThrowIfCancellationRequested
            // above): a cancelled call produced no account of the chain, and overwriting the previous
            // call's with a half-one would make E7 render a status for an attempt the player stopped.
            // The rule is "the last call that ran to an answer", and it is pinned by a test.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            // Ruling E3-b / D-2: the core refused this tier WITHOUT sending anything — an open gate
            // or a rate-ceiling wait past MaxSpacingWaitMs. That is a skip, not a failure, so it may
            // not become the exception the player reads while a healthy tier is still untried.
            catch (TranslationException paused) when (paused.NotSent)
            {
                skipped.Add((tier.ProviderId, paused.Kind.ToString(), paused.RetryAt ?? tier.Gate.Now()));
            }
            catch (TranslationException failed)
            {
                LogTierFailure(tier.ProviderId, failed.Kind, i);
                lastFailure = failed;
            }
            catch (Exception ex)
            {
                // Anything a tier throws that is not already classified goes through the mapper —
                // E1.S3 owns that vocabulary and re-deriving it here is how a second, disagreeing
                // classifier gets written. The two guards around it are the core's own
                // (HttpProviderCore.cs:295, :305): the mapper's row 1 answers "a genuine cancel" on
                // a cancelled token, and that is the one Kind nothing may construct (I3), so a
                // cancel landing inside this window must leave as an OperationCanceledException.
                ct.ThrowIfCancellationRequested();
                var kind = ProviderErrorMapper.Classify(null, null, ex, false, ct);
                ct.ThrowIfCancellationRequested();
                LogTierFailure(tier.ProviderId, kind, i);
                lastFailure = new TranslationException(kind,
                    UserMessages.Sentence(kind) ?? ex.Message, null, tier.ProviderId);
            }
        }

        // AC 4 — a tier that really tried and failed outranks "everything is paused": the player
        // must read what the provider actually said, not a sentence about the ones that were skipped.
        //
        // And when SEVERAL tiers tried, it is the LAST one's failure, because `lastFailure` is
        // overwritten: §6.2 spells that out — "the real reason the last provider that actually tried
        // gave". Not a ranking of Kinds, which would need an order nothing in this app defines, and
        // not the first failure, which is the tier the chain already decided not to trust. Pinned.
        if (lastFailure is not null)
        {
            Publish(null, skipped, lastFailure.Kind);
            throw lastFailure;
        }

        // AC 3 — the EARLIEST remembered window, and it is written as Min because Max is the
        // plausible typo: the chain gets a rung back when the FIRST of them reopens, and telling
        // the player to wait for the last one would leave a working provider unused for the
        // difference. Not First() either — tier order and window order are unrelated.
        var retryAt = Earliest(skipped);
        Publish(null, skipped, TranslationErrorKind.AllProvidersPaused);
        throw new TranslationException(TranslationErrorKind.AllProvidersPaused,
            UserMessages.AllProvidersPaused, retryAt);
    }

    /// <summary>One line per tier that really <b>tried</b> and failed — a skipped tier logs nothing,
    /// because the point of the chain is that it cost nothing. The <see cref="TranslationErrorKind"/>
    /// and never the message: a provider's message can quote its URL (I11). <c>Logging.Warn</c>
    /// swallows its own errors by design, so a failed log line can never fail a translation.
    ///
    /// <para>The tail states what actually follows. "Trying the next tier" on the <i>last</i> tier is
    /// the kind of line a reader trusts and is wrong about — and reading this log is step 3 of the
    /// story's own manual verification.</para></summary>
    private void LogTierFailure(string providerId, TranslationErrorKind kind, int index)
        => Logging.Warn($"chain: {providerId} failed ({kind}) — "
            + (index + 1 < _tiers.Count ? "trying the next tier" : "no tier left after it"));

    /// <summary>The one assignment to <see cref="LastOutcome"/>, built from locals and published
    /// whole. Never mutated field by field mid-loop: a reader on the dispatcher would otherwise see
    /// a half-finished account of a call that is still running.</summary>
    private void Publish(string? providerId,
        List<(string ProviderId, string Reason, DateTimeOffset RetryAt)> skipped,
        TranslationErrorKind? kind)
        => Volatile.Write(ref _lastOutcome, new Outcome(
            providerId,
            skipped.Select(s => (s.ProviderId, s.Reason)).ToList(),
            Earliest(skipped),
            kind));

    private static DateTimeOffset? Earliest(
        List<(string ProviderId, string Reason, DateTimeOffset RetryAt)> skipped)
        => skipped.Count == 0 ? null : skipped.Min(s => s.RetryAt);
}
