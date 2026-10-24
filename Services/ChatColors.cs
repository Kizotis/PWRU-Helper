using System.Text.RegularExpressions;

namespace PWRUHelper.Services;

/// <summary>One word the OCR read, in reading order, with the game colour it was drawn in —
/// already snapped to <see cref="ChatPalette"/>'s display colour, or null when no palette colour
/// won the vote under it (a word lost in the 3D scene, an emoji).</summary>
public readonly record struct OcrWord(string Text, uint? Rgb);

/// <summary>One stretch of a feed line drawn in one colour (0xRRGGBB). A null colour means "the
/// TextBlock's own foreground" — the look every row had before chat colours, and still the look of
/// a row whose colours could not be read.</summary>
public readonly record struct ChatRun(string Text, uint? Rgb, bool IsSpeaker = false);

/// <summary>What one OCR pass produced: the lines (exactly what <see cref="OcrService.ReadLinesAsync"/>
/// returns) and every word with its colour, in the same reading order.</summary>
public sealed record OcrFrame(List<string> Lines, IReadOnlyList<OcrWord> Words);

/// <summary>
/// The colours Perfect World draws its chat in, so the feed can repeat them.
///
/// Measured, not guessed: the peaks of the bright pixels on seven screenshots of the owner's chat
/// (one per channel). The game draws text in a handful of flat colours with a dark outline, so a
/// word's colour is decided by a VOTE — every pixel under the word close enough to a palette colour
/// votes for it — rather than by averaging, which would blend the glyph with its outline and with
/// the 3D scene behind it into a colour the game never drew.
///
/// The nickname is ALWAYS the pale yellow <see cref="Nick"/>, whatever the channel: the channel
/// colour lives in the body, which is why <see cref="ChatColors"/> takes it from there.
/// </summary>
public static class ChatPalette
{
    /// <param name="Game">The colour as the game draws it — what a pixel is compared against.</param>
    /// <param name="Display">The colour the feed paints. The same as <paramref name="Game"/> except
    /// where the game's colour is unreadable on the feed's near-black card (the whisper blue, which
    /// the owner agreed to lighten).</param>
    public sealed record Entry(string Name, uint Game, uint Display);

    public const uint Nick = 0xFFDC8C;

    public static readonly IReadOnlyList<Entry> Entries = new Entry[]
    {
        new("nick",          0xFCDC8C, Nick),
        new("world",         0xFCE400, 0xFFE400),   // Мир
        new("announcer",     0xFCFC00, 0xFFFF00),   // the speaker of a system announcement
        new("faction",       0x00FCFC, 0x00FFFF),   // Клан
        new("group",         0x00FC00, 0x00FF00),   // Груп.
        new("normal",        0xFCFCFC, 0xFFFFFF),   // Оснв.
        new("whisper",       0x0064FC, 0x3D8BFF),   // Лично — the game's blue is too dark on near-black
        new("whisper-friend",0xFC4CB4, 0xFF4CB4),   // Лично, to a friend
        new("system",        0xFC3400, 0xFF3300),   // Сист.
        new("alert",         0xFC0000, 0xFF1A1A),   // "Leaving Safe Zone"
        new("item",          0xFC6400, 0xFF6600),   // "Got 1", item names in announcements
        new("info",          0xBCD494, 0xBCD494),   // "Card already in bag", skills gained / lost
        new("item-rare",     0x8484FC, 0x8484FF),
        new("item-epic",     0xAC34FC, 0xAC34FF),
    };

    /// <summary>How far (RGB Euclidean) a pixel may sit from a palette colour and still vote for
    /// it. Wide enough for the glyph's anti-aliased core, narrow enough that the two yellows 24
    /// apart still split their votes by nearest, and that the beige and blue game scenery seen on
    /// the screenshots (≥ 67 away from every entry) votes for nothing.</summary>
    internal const int Tolerance = 48;

    /// <summary>A pixel darker than this on every channel is outline or scenery, never text — the
    /// dimmest palette colour peaks at 0xD4. Skipping them is most of the work saved.</summary>
    private const int MinBrightness = 140;

    /// <summary>The share of a word's pixels a colour needs before its vote counts: a few stray
    /// scenery pixels must not paint a word the OCR read off a dark patch.</summary>
    private const double MinVoteShare = 0.03;

    /// <summary>Index of the palette entry nearest to a pixel, or -1 when none is within
    /// <see cref="Tolerance"/>.</summary>
    internal static int Nearest(byte r, byte g, byte b)
    {
        if (Math.Max(r, Math.Max(g, b)) < MinBrightness) return -1;
        int best = -1;
        long bestDist = (long)Tolerance * Tolerance;
        for (int i = 0; i < Entries.Count; i++)
        {
            uint c = Entries[i].Game;
            long dr = r - (int)(c >> 16 & 0xFF), dg = g - (int)(c >> 8 & 0xFF), db = b - (int)(c & 0xFF);
            long d = dr * dr + dg * dg + db * db;
            if (d <= bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>The display colour the pixels inside a box vote for, or null when no colour
    /// gathers enough votes. <paramref name="bgra"/> is a 32 bpp BGRA buffer (what
    /// <c>LockBits(Format32bppArgb)</c> hands back); the box is clamped to the image.</summary>
    public static uint? SampleBox(byte[] bgra, int width, int height, int stride,
                                  int left, int top, int right, int bottom)
    {
        left = Math.Clamp(left, 0, width); right = Math.Clamp(right, 0, width);
        top = Math.Clamp(top, 0, height); bottom = Math.Clamp(bottom, 0, height);
        if (right <= left || bottom <= top) return null;

        Span<int> votes = stackalloc int[Entries.Count];
        for (int y = top; y < bottom; y++)
        {
            int row = y * stride;
            for (int x = left; x < right; x++)
            {
                int o = row + x * 4;
                int i = Nearest(bgra[o + 2], bgra[o + 1], bgra[o]);
                if (i >= 0) votes[i]++;
            }
        }

        int winner = -1;
        for (int i = 0; i < votes.Length; i++)
            if (votes[i] > 0 && (winner < 0 || votes[i] > votes[winner])) winner = i;
        if (winner < 0) return null;

        int area = (right - left) * (bottom - top);
        return votes[winner] >= Math.Max(3, area * MinVoteShare) ? Entries[winner].Display : null;
    }
}

/// <summary>
/// Puts the game's colours on a feed row: the original body word by word, and the translation by
/// its body's colour plus the coloured pieces that survive translation unchanged.
///
/// Pure and UI-free: colours are 0xRRGGBB numbers, turned into brushes by <c>Views/ChatText</c>.
/// </summary>
public static class ChatColors
{
    /// <summary>How many OCR words a message token may skip to find its match — the channel badge,
    /// a speck of its border, an emoji artifact that <see cref="TextMatching.StripNoise"/> removed.</summary>
    private const int Lookahead = 4;

    private static readonly Regex Token = new(@"\S+", RegexOptions.Compiled);

    /// <summary>
    /// The colours of a message's BODY, as runs whose texts concatenate back to exactly
    /// <paramref name="body"/>. <paramref name="message"/> is the whole "Nick: body" line the body
    /// was split from — aligning the whole line, nickname included, is what tells two identical
    /// short bodies ("да") on screen apart.
    ///
    /// Null when the colours can't be trusted — no words, or fewer than half of the message's
    /// tokens found on screen — and the row then keeps the uncoloured look rather than guessing.
    /// </summary>
    public static IReadOnlyList<ChatRun>? ColorBody(string message, string body, IReadOnlyList<OcrWord>? words)
    {
        if (words == null || words.Count == 0 || string.IsNullOrWhiteSpace(body)) return null;

        var msgTokens = Token.Matches(message ?? "").Select(m => m.Value).ToList();
        var bodyTokens = Token.Matches(body).ToList();
        int offset = msgTokens.Count - bodyTokens.Count;    // the body is the message's tail
        if (offset < 0) return null;

        var aligned = Align(msgTokens, words);
        if (aligned == null) return null;

        var colors = new uint?[bodyTokens.Count];
        for (int i = 0; i < colors.Length; i++) colors[i] = aligned[offset + i];

        // A token the OCR words didn't match (or whose word had no colour) takes its neighbour's:
        // one message is one colour except where an announcement changes it on purpose.
        for (int i = 1; i < colors.Length; i++) colors[i] ??= colors[i - 1];
        for (int i = colors.Length - 2; i >= 0; i--) colors[i] ??= colors[i + 1];
        if (colors.Length == 0 || colors[0] == null) return null;

        // The gap in front of a token takes the colour of the token BEFORE it, so a run never
        // starts with a space and the texts concatenate back to the body exactly.
        var runs = new List<ChatRun>();
        int pos = 0;
        for (int i = 0; i < bodyTokens.Count; i++)
        {
            var t = bodyTokens[i];
            if (t.Index > pos) Append(runs, body[pos..t.Index], i > 0 ? colors[i - 1] : colors[i]);
            Append(runs, t.Value, colors[i]);
            pos = t.Index + t.Length;
        }
        if (pos < body.Length) Append(runs, body[pos..], colors[^1]);
        return runs;
    }

    /// <summary>The longest a nickname read off its colour may be — the same bound
    /// <see cref="TextMatching"/> puts on a nickname read off its text.</summary>
    private const int MaxNickChars = 22;

    /// <summary>
    /// One message's speaker, body and body colours — the text split (<see cref="TextMatching.SplitSpeakerStrict"/>)
    /// corrected by what the colours prove.
    ///
    /// The strict split refuses a lowercase Cyrillic nickname with no badge in front of it, because
    /// "тс: сбор у входа" must not lose its slang word as a fake speaker. That refusal also hit real
    /// players: on the owner's screenshots «заткнись: че где красивая пушка» kept «заткнись» in the
    /// body, and the translator turned the player's name into "shut up". The colour settles what the
    /// text can't: the game draws EVERY nickname in pale yellow (<see cref="ChatPalette.Nick"/>) and
    /// never a message body — so a body that opens with a pale-yellow run ending in ':' opens with
    /// its speaker, and that run leaves the text sent to the translator.
    /// </summary>
    public static (string Speaker, string Body, IReadOnlyList<ChatRun>? Colors) Colorize(
        string message, (string Speaker, string Body) split, IReadOnlyList<OcrWord>? words)
    {
        var runs = ColorBody(message, split.Body, words);
        if (split.Speaker.Length > 0 || runs == null || runs.Count < 2 || runs[0].Rgb != ChatPalette.Nick)
            return (split.Speaker, split.Body, runs);

        var head = runs[0].Text.TrimEnd();
        var nick = head.TrimEnd(':').Trim();
        if (!head.EndsWith(':') || nick.Length is 0 or > MaxNickChars || !nick.Any(char.IsLetter)
            || nick.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
            return (split.Speaker, split.Body, runs);

        var rest = runs.Skip(1).ToList();
        rest[0] = rest[0] with { Text = rest[0].Text.TrimStart() };
        return (nick, string.Concat(rest.Select(r => r.Text)), rest);
    }

    /// <summary>
    /// The translation coloured after its body (the owner's "A + B"):
    ///   A — all of it in the body's main colour, the channel colour of a player's message;
    ///   B — every piece of the original in ANOTHER colour (an [item], a player's name, a server)
    ///       that reappears unchanged in the translation gets its colour back there.
    /// B is the only honest mapping there is: a translation does not keep the word order, so a
    /// word-by-word recolouring would paint the wrong words. A placeholder row ("…", "(…)") and a
    /// row with no known colours come back as one uncoloured run.
    /// </summary>
    public static IReadOnlyList<ChatRun> ColorTranslation(IReadOnlyList<ChatRun>? body, string translation)
    {
        translation ??= "";
        var baseRgb = body == null ? null : MainColor(body);
        if (baseRgb == null || IsPlaceholder(translation)) return new[] { new ChatRun(translation, null) };

        var colors = new uint?[translation.Length];
        Array.Fill(colors, baseRgb);
        var claimed = new bool[translation.Length];

        foreach (var run in body!)
        {
            if (run.Rgb == null || run.Rgb == baseRgb) continue;
            // Trim the spacing and the sentence punctuation the run happens to end on, but never a
            // bracket: "[Cloud dust]" is how the item reads in English too.
            var needle = run.Text.Trim().Trim(',', '.', ':', ';', '!', '?');
            if (needle.Count(char.IsLetterOrDigit) < 2) continue;   // a lone "1" would paint every 1

            for (int at = translation.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = translation.IndexOf(needle, at + 1, StringComparison.Ordinal))
            {
                if (claimed.AsSpan(at, needle.Length).Contains(true)) continue;
                for (int k = at; k < at + needle.Length; k++) { colors[k] = run.Rgb; claimed[k] = true; }
                break;
            }
        }

        var runs = new List<ChatRun>();
        int start = 0;
        for (int k = 1; k <= translation.Length; k++)
            if (k == translation.Length || colors[k] != colors[start])
            {
                runs.Add(new ChatRun(translation[start..k], colors[start]));
                start = k;
            }
        return runs.Count > 0 ? runs : new[] { new ChatRun(translation, null) };
    }

    /// <summary>
    /// The colour the body is written in: the channel's colour for a player message, the
    /// announcement's own colour for a system one. Null when nothing is known.
    ///
    /// It is the colour the line OPENS in once past the names (a nickname, an announcer), because
    /// that is how the game composes a line — "Got 1 [Cloud dust]", "Elder of … announces loudly:
    /// Name upgrades [item]". It is NOT the colour with the most letters: a long [item] name
    /// out-weighs a short message ("держи [Cloud dust]"), and the whole translation went purple.
    /// A run with fewer than two letters is skipped — "1" opens "1 [Seal…] disappeared" too, but a
    /// single misread glyph must not decide the colour of a whole line.
    /// </summary>
    public static uint? MainColor(IReadOnlyList<ChatRun> runs)
    {
        uint? fallback = null;
        foreach (var r in runs)
        {
            if (r.Rgb is not uint rgb || IsNameColor(rgb)) continue;
            if (r.Text.Count(char.IsLetterOrDigit) >= 2) return rgb;
            fallback ??= rgb;
        }
        return fallback ?? runs.Select(r => r.Rgb).FirstOrDefault(c => c != null);
    }

    private static readonly uint AnnouncerDisplay = ChatPalette.Entries.Single(e => e.Name == "announcer").Display;

    private static bool IsNameColor(uint rgb) => rgb == ChatPalette.Nick || rgb == AnnouncerDisplay;

    // "…" is the pending row, "(…)" every terminal one (the HasTranslation rule): they are the
    // app talking, not the player, so they keep the app's own colour.
    private static bool IsPlaceholder(string t)
        => t.Length == 0 || t.StartsWith('(') || t == "…" || t == UserMessages.PendingRetryRow();

    private static void Append(List<ChatRun> runs, string text, uint? rgb)
    {
        if (runs.Count > 0 && runs[^1].Rgb == rgb) runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        else runs.Add(new ChatRun(text, rgb));
    }

    /// <summary>Find where the message sits among the frame's words: the start that matches the
    /// most tokens, each token searched a few words ahead of the last match. Null when fewer than
    /// half the tokens were found — a message that can't be placed is left uncoloured.</summary>
    private static uint?[]? Align(IReadOnlyList<string> tokens, IReadOnlyList<OcrWord> words)
    {
        if (tokens.Count == 0) return null;
        var tk = tokens.Select(Key).ToArray();
        var wk = words.Select(w => Key(w.Text)).ToArray();

        uint?[]? best = null;
        int bestScore = 0;
        var cur = new uint?[tk.Length];
        for (int start = 0; start < wk.Length; start++)
        {
            if (!Same(tk[0], wk[start]) && (tk.Length < 2 || !Same(tk[1], wk[start]))) continue;
            Array.Clear(cur);
            int score = 0, p = start;
            for (int i = 0; i < tk.Length && p < wk.Length; i++)
                for (int q = p; q < Math.Min(wk.Length, p + Lookahead); q++)
                    if (Same(tk[i], wk[q])) { cur[i] = words[q].Rgb; score++; p = q + 1; break; }
            if (score > bestScore) { bestScore = score; best = (uint?[])cur.Clone(); }
        }
        return best != null && bestScore * 2 >= tk.Length ? best : null;
    }

    // Letters and digits only, lower-cased: the message went through trims (the nickname's
    // decoration, the badge's dot) that the OCR word did not.
    private static string Key(string s)
        => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool Same(string a, string b)
        => a.Length > 0 && b.Length > 0
           && (a == b || (a.Length >= 3 && b.Length >= 3 && (a.Contains(b) || b.Contains(a))));
}
