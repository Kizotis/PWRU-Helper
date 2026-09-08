namespace PWRUHelper.Services;

/// <summary>
/// One piece of text the OCR engine returned, together with where it physically sat on the image.
///
/// Deliberately a plain value type and NOT a WinRT type: it keeps <see cref="OcrLayout"/> — and
/// its tests — free of <c>Windows.Media.Ocr</c>, which needs a real engine (and therefore a
/// language pack) that CI does not have. <see cref="OcrService"/> projects each recognized line
/// onto one of these by taking the union of its words' bounding rectangles.
///
/// Coordinates are in the engine's own pixel space, i.e. the SCALED image — the service enlarges
/// a small capture by up to 3×. That is why every threshold in <see cref="OcrLayout"/> is
/// expressed as a fraction of text height and never as an absolute number of pixels.
/// </summary>
internal readonly record struct OcrTextBox(string Text, double Left, double Top, double Width, double Height)
{
    internal double Right => Left + Width;
    internal double Bottom => Top + Height;
}

/// <summary>
/// Puts the OCR engine's output back into READING order.
///
/// Windows.Media.Ocr does not promise its <c>Lines</c> come out top-to-bottom, and on text whose
/// columns happen to line up it really doesn't: given a 10-line Russian dialogue where every
/// speaker name is the same width («Анна:» / «Иван:», so every body starts at the same x), the
/// engine reported TWENTY lines — all ten nicknames first (x≈42, y 46→586), then all ten message
/// bodies (x≈196, y 46→586). It had decided the text was two columns and read them column by
/// column. Measured, not guessed: that is the actual dump for the owner's Notepad screenshot.
///
/// Downstream, <see cref="TextMatching.SplitChatMessages"/> faithfully turned that into eight
/// "speaker with an empty body" messages followed by three messages holding several dialogue
/// lines glued together — and because the damage happens here, the grey ORIGINAL line shown to
/// the player was already wrong, before translation was ever involved.
///
/// The fix is to stop trusting the engine's ordering and rebuild it from the geometry: group the
/// fragments into visual rows by vertical overlap, order the rows top-to-bottom, and order what
/// is inside a row left-to-right. A row's fragments are then joined back into ONE line, which
/// restores the invariant every heuristic in <see cref="TextMatching"/> was written against —
/// one input line = one physical line on screen.
///
/// On ordinary single-column game chat (what users actually rely on) each row holds exactly one
/// fragment, so the output is the engine's own lines sorted by y — i.e. unchanged.
/// </summary>
internal static class OcrLayout
{
    /// <summary>How much of the SHORTER of two boxes must overlap vertically for them to count as
    /// the same visual row. Relative to text height on purpose (see <see cref="OcrTextBox"/>).
    ///
    /// Half is comfortably on both sides of the real numbers. Same-row fragments overlap by 0.7–1.0
    /// of the shorter box even when one is all x-height ("из") and its neighbour has ascenders and
    /// descenders ("Петербурга."); consecutive rows of readable text are drawn at a pitch greater
    /// than one glyph height, so their boxes don't overlap at all.</summary>
    internal const double SameRowOverlapRatio = 0.5;

    /// <summary>Rebuild the engine's fragments into lines in reading order. Blank fragments are
    /// dropped; every returned line is trimmed and non-empty. Order of the input is irrelevant —
    /// only the geometry decides — except that exact ties keep the order they came in.</summary>
    internal static List<string> OrderIntoLines(IEnumerable<OcrTextBox> boxes)
    {
        var frags = new List<OcrTextBox>();
        foreach (var b in boxes ?? Enumerable.Empty<OcrTextBox>())
        {
            var text = b.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) frags.Add(b with { Text = text! });
        }
        if (frags.Count == 0) return new List<string>();
        if (frags.Count == 1) return new List<string> { frags[0].Text };

        // OrderBy is a STABLE sort, so two fragments with identical geometry stay in the order the
        // engine gave them. List.Sort is not, and there is no reason to invent an ordering here.
        var byPosition = frags.OrderBy(f => f.Top).ThenBy(f => f.Left).ToList();

        var rows = new List<Row>();
        foreach (var f in byPosition)
        {
            // Every open row is considered, not just the last one: a ragged line whose tall
            // fragment comes late must still find its own row rather than start a new one.
            // Best match wins, so a fragment sandwiched between two rows joins the nearer.
            Row? best = null;
            double bestOverlap = 0;
            foreach (var row in rows)
            {
                double overlap = Math.Min(row.Bottom, f.Bottom) - Math.Max(row.Top, f.Top);
                if (overlap <= 0) continue;                       // also the guard for a zero-height box
                double shorter = Math.Max(1.0, Math.Min(row.Bottom - row.Top, f.Height));
                if (overlap < SameRowOverlapRatio * shorter) continue;
                if (overlap > bestOverlap) { bestOverlap = overlap; best = row; }
            }

            if (best == null) rows.Add(new Row(f));
            else best.Add(f);
        }

        return rows
            .OrderBy(r => r.Top).ThenBy(r => r.Left)
            .Select(r => string.Join(" ", r.Members.OrderBy(m => m.Left).ThenBy(m => m.Top).Select(m => m.Text)))
            .ToList();
    }

    /// <summary>A visual row under construction: its members plus the union of their boxes. The
    /// band is the union rather than the first member's box so a row assembled out of order still
    /// covers all of its text.</summary>
    private sealed class Row
    {
        internal readonly List<OcrTextBox> Members = new();
        internal double Top { get; private set; }
        internal double Bottom { get; private set; }
        internal double Left { get; private set; }

        internal Row(OcrTextBox first)
        {
            Members.Add(first);
            Top = first.Top; Bottom = first.Bottom; Left = first.Left;
        }

        internal void Add(OcrTextBox box)
        {
            Members.Add(box);
            Top = Math.Min(Top, box.Top);
            Bottom = Math.Max(Bottom, box.Bottom);
            Left = Math.Min(Left, box.Left);
        }
    }
}
