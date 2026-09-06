using System.Globalization;
using System.Text;

namespace PWRUHelper.Services;

/// <summary>
/// The gate transition line of <c>architecture-cible.md</c> §10.2 — one line per <b>transition</b>,
/// never per skipped request, or a 30-minute open window would fill the 1 MB log with the same
/// sentence and roll away the incident that started it.
///
/// <para>It exists so that "the gate opens and never clears" stops being a feeling and becomes a
/// timeline in a paste: "Copy error report" (<c>MainWindow.xaml.cs:323-333</c>) copies
/// <c>Logging.ReadRecent()</c>, so a gate line is in the report the moment it is in the log, beside
/// E1.S5's per-request <c>tr </c> lines. That pair is §10.3.</para>
///
/// <para><b>Why here and not on <see cref="ProviderGate"/>.</b> A gate does not know its own id —
/// the registry is the only thing that does — and the gate is deliberately free of I/O and of
/// logging on the hot path (I2, I10). So this hangs off <see cref="ProviderGates.TransitionHook"/>,
/// the seam E2.S4 built and left for this story: the registry announces every §5.2 edge, E2.S4's
/// <c>WorthAWrite</c> filters that stream for the disk and <see cref="ReasonFor"/> filters it for
/// the log. Two questions, two filters, one definition of "a transition".</para>
///
/// <para><b>I11 is the point of AC 2.</b> The report is pasted to Discord <i>by design</i>. These
/// lines carry a provider id, a <see cref="TranslationErrorKind"/> name, an int and two instants —
/// nothing else may enter, ever. Only one of those is a string at all, and <see cref="Id"/> is the
/// single door it walks through: everything outside <c>[A-Za-z0-9._-]</c> becomes a dot and the
/// whole thing is cut at 24 characters, so a <c>q=</c>, a key, a URL or a second line cannot be
/// spelled here even by a caller that invents its own id. A full <c>RequestLog.Ascii</c>-style
/// sanitiser would be over-engineering — no value on this line is server-controlled — but a
/// narrower one is four lines and makes AC 2 structural instead of remembered.</para>
/// </summary>
internal static class GateLog
{
    /// <summary>What an absent field renders as, exactly like <c>RequestLog</c>'s: a reader can tell
    /// "there is no window" from "the field was dropped".</summary>
    private const string Nothing = "-";

    /// <summary>An id's share of the line. Every real one is a <see cref="ProviderIds"/> constant
    /// and none of them is close to this.</summary>
    internal const int MaxProviderChars = 24;

    /// <summary>§5.3's <c>AuthFailed</c> row opens with <see cref="DateTimeOffset.MaxValue"/>, so
    /// <c>for=</c> would render an eight-digit number of seconds and <c>until=</c> a year nobody
    /// will see. One spelling, and it does not change: E2.S7 and the owner will grep for it.</summary>
    internal const string UntilKeyChange = "until-key-change";

    // The five reasons, spelled once. E2.S7's spike reads these lines; a second spelling of one is
    // a row its grep silently misses.
    internal const string ReasonFailure = "failure";
    internal const string ReasonProbe = "probe";
    internal const string ReasonSuccess = "success";
    internal const string ReasonClear = "clear";
    internal const string ReasonReload = "reload";

    /// <summary>
    /// The storm valve, keyed by provider + edge and shared by the process because the 1 MB log it
    /// protects is.
    ///
    /// <para><b>"One per transition" already bounds the normal case</b> — the escalation ladder
    /// doubles its own window, so a real 429 episode writes a handful of lines an hour and needs no
    /// valve at all. <b>One pattern repeats a single edge without bound</b>, and it is reachable:
    /// every caller granted the probe is cancelled before it reports (LIVE switched off mid-probe —
    /// by I3 such a caller never reports at all), so the gate re-arms one <c>RequestTimeoutSeconds</c>
    /// later and grants another, for as long as it lasts. That is one <c>OPEN-&gt;HALF-OPEN</c> line
    /// every twelve seconds, which is exactly the eviction this family exists to avoid.</para>
    ///
    /// <para>It cannot hide an escalation (which is what E2.S7 needs from these lines): the first
    /// ten of a run are written in full, every <i>different</i> edge ends the run and flushes what
    /// it held, and the summary carries the count. A gate escalating 60 s → 2 → 4 min alternates
    /// edges by construction and so never reaches the threshold.</para>
    /// </summary>
    internal static readonly LogSuppressor Suppression = new(prefix: "gate ", separator: " ");

    /// <summary>A case that asserts an exact line count has to take this static the way it takes
    /// <c>Logging.DirectoryOverride</c>: the run it counts is process-wide.</summary>
    internal static void ResetSuppression() => Suppression.Reset();

    // ---- the filter (ruling E2-b) ---------------------------------------------------------------

    /// <summary>
    /// Which of the edges the registry announces gets a line, and what it is called — or
    /// <c>null</c> for the ones ruling <b>E2-b</b> keeps out.
    ///
    /// <para><b>Logged:</b> the four §5.2 edges — <c>Closed→Open</c> and <c>HalfOpen→Open</c>
    /// (a failure), <c>Open→HalfOpen</c> (the probe grant) and <c>HalfOpen→Closed</c> (the probe
    /// succeeded) — plus <c>Open→Closed</c>, which is the key save lifting an account-scoped block
    /// (<c>ClearAuthBlock</c>, ruling E2-i). A pause <i>ending</i> is the other half of the evidence:
    /// without it "it never clears" is unanswerable.</para>
    ///
    /// <para><b>Not logged:</b> everything where the state did not move — a strike reset (§5.7 makes
    /// it a <i>save</i> trigger, not an edge; nothing is wrong and nothing moved) and an
    /// <c>Open→Open</c> escalation that only lengthens a standing window — and, per <c>TryEnter</c>'s
    /// own contract, every refusal and every rate-ceiling <c>Wait</c>, which never reach here at all
    /// because they are not transitions.</para>
    ///
    /// <para><b>And the soft cooldowns</b> (§5.3's <c>Unavailable</c>/<c>Timeout</c>/<c>Network</c>/
    /// <c>Unknown</c>), which E2-b names explicitly. The suppression has to cover the whole cycle a
    /// cooldown creates and not only its first edge: five seconds later a caller is granted a probe,
    /// the probe fails, five seconds later again — so a dead DNS would write two lines every five
    /// seconds for as long as the network is down while §10.2 budgets a handful per session. Hence
    /// the rule: an edge <i>landing</i> on Open or HalfOpen whose last word from the provider was a
    /// soft failure is silent. This is deliberately the same call E2.S4's <c>WorthAWrite</c> makes
    /// for the file, which is what "the save rule and the log rule agree" means.
    /// <br/>Landing on <b>Closed</b> is never filtered, whatever the kind: it is the pause ending.
    /// The one thing this rule can drop is the probe-grant line of a real 429 episode that happened
    /// to take a timeout while it was already open (the timeout moves <c>LastKind</c> without moving
    /// the window); the OPEN line before it and the CLOSED or escalated OPEN after it are both still
    /// there, so the timeline still reads.</para>
    /// </summary>
    internal static string? ReasonFor(GateState from, GateSnapshot to)
    {
        var reason = (from, to.State) switch
        {
            (GateState.Closed, GateState.Open) => ReasonFailure,
            (GateState.HalfOpen, GateState.Open) => ReasonFailure,
            (GateState.Open, GateState.HalfOpen) => ReasonProbe,
            (GateState.HalfOpen, GateState.Closed) => ReasonSuccess,
            (GateState.Open, GateState.Closed) => ReasonClear,
            _ => null,
        };

        if (reason == null) return null;
        return to.State != GateState.Closed && IsSoft(to.LastKind) ? null : reason;
    }

    private static bool IsSoft(TranslationErrorKind? kind) =>
        kind is TranslationErrorKind.Unavailable or TranslationErrorKind.Timeout
             or TranslationErrorKind.Network or TranslationErrorKind.Unknown;

    // ---- emission -------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="ProviderGates.TransitionHook"/>'s implementation: the registry announces a
    /// transition, this decides whether it is one E2-b logs and writes the line.
    ///
    /// <para>Never throws — <c>NoteTransition</c> guards it as well, and this guards itself, because
    /// a diagnostic that can cost a translation is not a diagnostic (the same contract
    /// <c>Logging.cs:92</c> keeps for the write and <c>RequestLog.Write</c> keeps for the build).
    /// Nothing here blocks either: it is one synchronous append, on a path that is taken a handful
    /// of times per session, and the gate raises it <b>outside</b> its own lock.</para>
    /// </summary>
    internal static void Note(string providerId, GateState from, GateSnapshot to)
    {
        try
        {
            if (ReasonFor(from, to) is { } reason) Emit(providerId, from, to, reason);
        }
        catch { /* observability must never break the request that caused it */ }
    }

    /// <summary>
    /// The fifth edge ruling E2-b names: a gate rebuilt from <c>provider-state.json</c> on the first
    /// request of the session (E2.S4's <c>EnsureLoaded</c>). Called from there directly rather than
    /// through <see cref="ProviderGates.NoteTransition"/>, and that is not a shortcut: a seed is not
    /// a §5.2 transition — nothing moved, the gate was born in that state — and announcing it as one
    /// would queue a debounced <b>save</b> on every single start, which is precisely the churn §5.7
    /// refuses. <paramref name="from"/> is <c>Closed</c> by construction: <c>TrySeedState</c> only
    /// accepts a gate that has recorded nothing in this process, and such a gate is closed.
    /// </summary>
    internal static void Reload(string providerId, GateSnapshot to)
    {
        try { Emit(providerId, GateState.Closed, to, ReasonReload); }
        catch { /* a start must not fail because a line could not be written */ }
    }

    private static void Emit(string providerId, GateState from, GateSnapshot to, string reason)
    {
        // The registry's clock, not the wall clock (IS-6): `for=` is a duration measured against
        // the same "now" the window was computed from, so a test drives it without waiting.
        var now = ProviderGates.Clock();
        var id = Id(providerId);
        var edge = Edge(from, to.State);

        var decision = Suppression.Note(id, edge, now);
        if (decision.Summary != null) Logging.Warn(decision.Summary);
        if (!decision.Write) return;

        var line = Render(id, edge, to, reason, now);

        // §10.2's own example levels: opening is a warning — it is the app pausing a provider —
        // while a probe and a recovery are information.
        if (to.State == GateState.Open) Logging.Warn(line); else Logging.Info(line);
    }

    // ---- the line -------------------------------------------------------------------------------

    /// <summary>
    /// §10.2's fields, as one pure function: no clock, no I/O, everything it renders decided by its
    /// caller — the same discipline as <c>RequestLog.Line</c> (<c>RequestLog.cs:201-203</c>), and for
    /// the same reason. A builder that is a function is testable without a file and movable without
    /// a rewrite.
    /// </summary>
    internal static string Line(string providerId, GateState from, GateSnapshot to, string reason,
                                DateTimeOffset now) =>
        Render(Id(providerId), Edge(from, to.State), to, reason, now);

    /// <summary>Every value on this line is rendered with the invariant culture, not only the one
    /// where a French or Russian Windows visibly disagrees: <c>:</c> in a custom date format is the
    /// <i>culture's</i> time separator, and <c>StringBuilder.Append(int)</c> formats with the
    /// current culture too. A line E2.S7 greps and the owner reads must be the same line on every
    /// machine, and one rule for the whole builder is cheaper to keep than one exception to
    /// remember.</summary>
    private static string Render(string id, string edge, GateSnapshot to, string reason,
                                 DateTimeOffset now)
    {
        var sb = new StringBuilder(96)
            .Append("gate provider=").Append(id)
            .Append(' ').Append(edge)
            .Append(" kind=").Append(to.LastKind?.ToString() ?? Nothing)
            .Append(" strikes=").Append(to.Strikes.ToString(CultureInfo.InvariantCulture))
            .Append(" for=").Append(For(to.BlockedUntil, now))
            .Append(" until=").Append(Until(to.BlockedUntil))
            .Append(" reason=").Append(reason);
        return sb.ToString();
    }

    private static string Edge(GateState from, GateState to) => Name(from) + "->" + Name(to);

    /// <summary>§10.2 writes the states in capitals, and <c>HALF-OPEN</c> with the hyphen. They are
    /// what the owner greps for, so they are spelled here and not taken from the enum.</summary>
    private static string Name(GateState state) => state switch
    {
        GateState.Closed => "CLOSED",
        GateState.Open => "OPEN",
        _ => "HALF-OPEN",
    };

    /// <summary>How much of the window is left, in whole seconds. <c>-</c> once it has elapsed —
    /// which is exactly the state a probe is granted out of, so the probe line says
    /// <c>for=- until=&lt;the window it came out of&gt;</c>.</summary>
    private static string For(DateTimeOffset? until, DateTimeOffset now)
    {
        if (until is not { } at) return Nothing;
        if (at == DateTimeOffset.MaxValue) return UntilKeyChange;
        var left = at - now;
        return left > TimeSpan.Zero
            ? ((long)Math.Round(left.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "s"
            : Nothing;
    }

    /// <summary><b>Local</b> <c>HH:mm:ss</c>, because <c>LogWriter</c> stamps every line with
    /// <c>DateTime.Now</c> (<c>Logging.cs:88</c>): a UTC <c>until=</c> beside a local timestamp is a
    /// report that reads as a bug. Invariant culture so a Russian or French locale renders the same
    /// digits.</summary>
    private static string Until(DateTimeOffset? until) =>
        until is not { } at || at == DateTimeOffset.MaxValue
            ? Nothing
            : at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The one door a string walks through (I11). Provider ids are ASCII constants from
    /// <see cref="ProviderIds"/>, so anything outside an id's own alphabet is not a value to be
    /// preserved — it is something that has no business in a report the user pastes to Discord.</summary>
    private static string Id(string? providerId)
    {
        if (string.IsNullOrEmpty(providerId)) return Nothing;

        var sb = new StringBuilder(Math.Min(providerId!.Length, MaxProviderChars));
        foreach (var c in providerId)
        {
            if (sb.Length >= MaxProviderChars) break;
            sb.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                        or '-' or '_' or '.' ? c : '.');
        }
        return sb.Length == 0 ? Nothing : sb.ToString();
    }
}
