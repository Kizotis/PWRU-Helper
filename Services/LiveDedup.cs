namespace PWRUHelper.Services;

/// <summary>
/// Decides which chat lines are genuinely new and worth translating, frame by frame, so the
/// live loop stops re-translating the same message when it merely flickers (animated emojis,
/// colour changes, the game panning behind the chat) or scrolls a little.
///
/// The old logic compared each frame only to the one before it, keeping almost every
/// character — so an animated heart or a colour-shifted letter made a message look "new".
/// This filter instead works on a <see cref="TextMatching.Signature"/> (letters+digits only)
/// and remembers the messages it has already emitted:
///
///  • a message that stays on screen (or briefly flickers off) is remembered as "active" and
///    never re-emitted;
///  • a message re-appears as new only after it has been GONE for a while
///    (<see cref="ReappearAfterFrames"/>) — i.e. it scrolled off and was really sent again;
///  • a two-frame confirmation still guards against one-off OCR garbage / fade-in reads.
///
/// Pure and UI-free, so it is unit-tested directly.
/// </summary>
public sealed class LiveDedup
{
    private sealed class Entry
    {
        public string Sig = "";
        public int LastSeen;   // frame index this signature was last on screen (or emitted)
    }

    /// <summary>A remembered message must have been OFF screen for at least this many frames
    /// before an identical read counts as a genuine re-send (rather than a brief flicker).</summary>
    private const int ReappearAfterFrames = 6;

    /// <summary>Forget a signature that hasn't been seen for this long, to bound memory.</summary>
    private const int MaxAgeFrames = 400;

    /// <summary>Hard cap on remembered signatures (oldest dropped first) as a second guard.</summary>
    private const int MaxEntries = 200;

    private readonly List<Entry> _seen = new();
    private List<string> _pendingSigs = new();   // signatures that looked fresh last frame
    private int _tick;

    /// <summary>Feed this frame's candidate lines (already sentence-split and filtered) and get
    /// back the subset to translate now. <paramref name="matchThreshold"/> sets how similar two
    /// signatures must be to count as "the same message" (from the Sensitivity slider);
    /// <paramref name="confirmThreshold"/> sets how closely a line must match itself across the
    /// confirmation frame (from the Stability slider).</summary>
    public List<string> Next(IReadOnlyList<string> lines, double matchThreshold, double confirmThreshold)
    {
        _tick++;

        // Signature every candidate, dropping any that reduce to nothing (pure punctuation/emoji).
        var cur = new List<(string line, string sig)>();
        foreach (var l in lines)
        {
            var sig = TextMatching.Signature(l);
            if (sig.Length > 0) cur.Add((l, sig));
        }

        // FRESH = on screen now but not a message we're already showing. A signature that
        // matches a remembered entry seen within ReappearAfterFrames is "still here" — refresh
        // it and skip. Anything unknown, or only matching an entry that's been gone a while, is
        // a candidate (new message, or a real re-send after it scrolled off).
        var fresh = new List<(string line, string sig)>();
        foreach (var x in cur)
        {
            var e = BestMatch(x.sig, matchThreshold);
            // A wrapped message can lose its "Nick:" line off the top of the capture, leaving only
            // the tail as a short headerless read. That fragment's signature is far shorter than
            // the remembered full one, so the length gap alone fails SimilarEnough and it looks
            // "new" — re-emitting a duplicate partial translation. Catch it: if no fuzzy match, a
            // remembered signature that CONTAINS this candidate (min length 10, so tiny strings
            // don't match spuriously) is the same message still on screen.
            if (e == null && x.sig.Length >= 10) e = ContainingMatch(x.sig);
            if (e != null && _tick - e.LastSeen <= ReappearAfterFrames)
                e.LastSeen = _tick;          // still on screen → keep alive, don't re-translate
            else
                fresh.Add(x);                // new, or reappeared after a real absence
        }

        // CONFIRM: only translate a fresh line that was ALSO fresh last frame. A single-frame
        // OCR artifact (camera pan, half-drawn fade-in) never survives this.
        var confirmed = fresh
            .Where(x => _pendingSigs.Any(p => TextMatching.SimilarEnough(p, x.sig, confirmThreshold)))
            .ToList();
        _pendingSigs = fresh.Select(x => x.sig).ToList();

        // Emit the confirmed lines and remember them as active.
        var outLines = new List<string>();
        foreach (var x in confirmed)
        {
            outLines.Add(x.line);
            var e = BestMatch(x.sig, matchThreshold);
            if (e != null) e.LastSeen = _tick;
            else _seen.Add(new Entry { Sig = x.sig, LastSeen = _tick });
        }

        Prune();
        return outLines;
    }

    /// <summary>Most-recently-seen remembered entry whose signature is close enough to <paramref name="sig"/>.</summary>
    private Entry? BestMatch(string sig, double threshold)
    {
        Entry? best = null;
        foreach (var e in _seen)
            if (TextMatching.SimilarEnough(e.Sig, sig, threshold) && (best == null || e.LastSeen > best.LastSeen))
                best = e;
        return best;
    }

    /// <summary>Most-recently-seen remembered entry whose signature CONTAINS <paramref name="sig"/>
    /// — catches an orphaned wrapped-line fragment whose "Nick:" header scrolled off the top.</summary>
    private Entry? ContainingMatch(string sig)
    {
        Entry? best = null;
        foreach (var e in _seen)
            if (e.Sig.Contains(sig) && (best == null || e.LastSeen > best.LastSeen))
                best = e;
        return best;
    }

    private void Prune()
    {
        _seen.RemoveAll(e => _tick - e.LastSeen > MaxAgeFrames);
        if (_seen.Count > MaxEntries)
        {
            _seen.Sort((a, b) => a.LastSeen.CompareTo(b.LastSeen));
            _seen.RemoveRange(0, _seen.Count - MaxEntries);
        }
    }
}
