using System.Text.RegularExpressions;

namespace PWRUHelper.Services;

/// <summary>
/// Pure text helpers for the live-translation diff: splitting OCR output into
/// sentences and deciding when two noisy OCR readings are "the same line".
/// No UI dependencies, so it's easy to reason about (and unit-test) on its own.
/// </summary>
public static class TextMatching
{
    /// <summary>How many wrapped OCR lines a single sentence may span before we flush it
    /// anyway. A long chat message usually wraps over 1–3 on-screen lines; capping at 3
    /// stops us from ever gluing a whole screen of separate messages into one blob.</summary>
    private const int MaxLinesPerSentence = 3;

    /// <summary>Rebuild OCR output into whole sentences instead of chopping it line-by-line.
    /// A message that wrapped over 2–3 screen lines is stitched back together (so it gets
    /// translated as one coherent sentence), and a line holding several sentences is split
    /// on . ! ? … Consecutive lines are joined until one ends on sentence punctuation, a
    /// blank line breaks the run, or <see cref="MaxLinesPerSentence"/> lines have piled up.</summary>
    public static List<string> ToSentences(IEnumerable<string> lines)
    {
        var result = new List<string>();
        var buffer = new List<string>();

        void Flush()
        {
            if (buffer.Count == 0) return;
            var joined = string.Join(" ", buffer).Trim();
            // The stitched block may itself contain several complete sentences — split those.
            foreach (var part in Regex.Split(joined, @"(?<=[\.\!\?…])\s+"))
            {
                var t = part.Trim();
                if (t.Length > 0) result.Add(t);
            }
            buffer.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }   // blank line = hard break
            buffer.Add(line);
            if (EndsSentence(line) || buffer.Count >= MaxLinesPerSentence) Flush();
        }
        Flush();
        return result;
    }

    /// <summary>True if the line ends on sentence-final punctuation (. ! ? …).</summary>
    private static bool EndsSentence(string line)
    {
        var t = line.TrimEnd();
        return t.Length > 0 && (t[^1] is '.' or '!' or '?' or '…');
    }

    // ---- game-chat message splitting (nickname/tag boundaries, not punctuation) ----

    /// <summary>A chat message rarely wraps beyond this many on-screen lines; caps a runaway
    /// stitch so a whole screen never glues into one message when no new header is seen.</summary>
    private const int MaxLinesPerMessage = 4;

    /// <summary>The "Tag Nick:" header must appear within this many characters — a colon deeper
    /// than this is almost certainly inside the message body, not a nickname separator.</summary>
    private const int MaxHeaderChars = 28;

    /// <summary>Extra colon-search slack allowed in front of the nick to leave room for a channel
    /// tag ("[Торговля] "). A tag can be a whole word plus its brackets and spacing, so the raw
    /// colon may sit past <see cref="MaxHeaderChars"/>; the tight limit is re-applied to the nick
    /// alone once the tag is peeled off. Untagged lines get no such slack.</summary>
    private const int MaxTagChars = 14;

    /// <summary>Perfect World RU channel-tag tokens (folded, lower-case). Seeing one at the very
    /// start of a line marks a new chat message even when the nickname after it is garbled.</summary>
    private static readonly HashSet<string> ChatTags = new()
    {
        "мир", "осн", "основной", "клан", "сист", "система", "фракц", "фракция", "фр",
        "группа", "гр", "отряд", "союз", "лс", "личка", "личное", "торг", "торговля", "помощь",
    };

    /// <summary>Split raw OCR lines into whole chat messages using the game's own structure —
    /// each message is <c>[Channel] Nick: text</c>, so a line that starts a new <c>Nick:</c>
    /// (optionally after a channel tag) begins a new message, and any following line without one
    /// is a wrapped continuation glued back on. This works even when players type no punctuation
    /// at all (the usual case in-game), where <see cref="ToSentences"/> can't tell messages apart.
    /// The channel tag is dropped; the nickname is kept as a "Nick: text" prefix.</summary>
    public static List<string> SplitChatMessages(IEnumerable<string> lines)
    {
        var messages = new List<string>();
        string speaker = "";
        var body = new List<string>();
        int lineCount = 0;

        void Flush()
        {
            if (body.Count == 0 && speaker.Length == 0) { lineCount = 0; return; }
            var text = string.Join(" ", body).Trim();
            var msg = speaker.Length > 0
                ? (text.Length > 0 ? $"{speaker}: {text}" : $"{speaker}:")
                : text;
            msg = msg.Trim();
            if (msg.Length > 0) messages.Add(msg);
            speaker = ""; body.Clear(); lineCount = 0;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }   // blank line = hard break
            if (TryParseHeader(line, out var sp, out var bd))
            {
                Flush();
                speaker = sp;
                if (bd.Length > 0) body.Add(bd);
                lineCount = 1;
            }
            else
            {
                body.Add(line);
                if (++lineCount >= MaxLinesPerMessage) Flush();   // safety cap on runaway wraps
            }
        }
        Flush();
        return messages;
    }

    /// <summary>Split one already-grouped message into its speaker prefix and body, so only the
    /// body is sent to the translator (the nickname is a proper noun and must stay untouched).
    /// Returns ("", message) when there's no <c>Nick:</c> header.</summary>
    public static (string Speaker, string Body) SplitSpeaker(string message)
        => TryParseHeader(message ?? "", out var sp, out var bd) && sp.Length > 0
            ? (sp, bd)
            : ("", (message ?? "").Trim());

    /// <summary>Like <see cref="SplitSpeaker"/>, but only peels a speaker off when the nick really
    /// looks like a player name — used on the live/read pipeline where a message body can itself
    /// start "word:" and be mistaken for a header. <see cref="TryParseHeader"/> happily treats a
    /// bare lowercase-Cyrillic token as a nick, so "тс: сбор у входа" would otherwise lose "тс" as
    /// a fake speaker and hide it from the translator. We keep the split only when the nick either
    /// sat behind a recognised channel tag, or carries a capital / Latin letter / digit — real
    /// nicknames do, a lowercase Cyrillic slang word ("тс") does not. Returns ("", message) otherwise.</summary>
    public static (string Speaker, string Body) SplitSpeakerStrict(string message)
    {
        var s = (message ?? "").Trim();
        if (!TryParseHeader(s, out var sp, out var bd) || sp.Length == 0) return ("", s);

        int colon = s.IndexOf(':');
        var head = colon > 0 ? s[..colon].Trim() : "";
        var firstToken = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        bool hadTag = firstToken != null && IsChatTag(firstToken);

        bool looksLikeName = hadTag || sp.Any(ch => char.IsUpper(ch) || IsLatin(ch) || char.IsDigit(ch));
        return looksLikeName ? (sp, bd) : ("", s);
    }

    private static bool IsLatin(char ch) => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    /// <summary>Re-attach a speaker prefix to a translated body: "Nick: translation".</summary>
    public static string WithSpeaker(string speaker, string translation)
        => string.IsNullOrEmpty(speaker) ? translation : $"{speaker}: {translation}";

    /// <summary>Detect a chat header "[Tag] Nick: body". <paramref name="speaker"/> is the nick
    /// with the channel tag removed (empty if only a tag was recognisable), and
    /// <paramref name="body"/> is the text after the colon. False when the line isn't a header
    /// (a wrapped continuation, or a colon that's really inside the message).</summary>
    public static bool TryParseHeader(string line, out string speaker, out string body)
    {
        speaker = ""; body = "";
        var s = (line ?? "").Trim();
        int colon = s.IndexOf(':');
        // Search for the colon with a wider window (room for a leading channel tag) so a tagged
        // line like "[Торговля] Продавец_Мечей_777: …" isn't rejected before the tag is peeled.
        // A colon at position 0 (":)" smiley start) is never a header.
        if (colon <= 0 || colon > MaxHeaderChars + MaxTagChars) return false;

        var head = s[..colon].Trim();
        body = s[(colon + 1)..].Trim();

        // Peel an optional leading channel tag: "[Мир] Nick" / "Мир Nick" / "(Клан) Nick".
        var tokens = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        bool hadTag = tokens.Count > 0 && IsChatTag(tokens[0]);
        if (hadTag) tokens.RemoveAt(0);

        // Only a recognised tag earns the wider colon window: on an untagged line a colon past
        // MaxHeaderChars is a colon inside the body ("… a colon: here"), not a nick separator.
        if (!hadTag && colon > MaxHeaderChars) return false;

        var nick = string.Join(" ", tokens)
            .Trim(' ', '[', ']', '(', ')', '<', '>', '+', '.', ',', '-', '*');

        // A real nickname has a letter (so "12" in "12:30" is rejected), isn't absurdly long, and
        // is at most a few words. A recognised tag with a garbled nick still marks a boundary.
        bool nickOk = nick.Length is > 0 and <= 22 && nick.Any(char.IsLetter) && tokens.Count <= 3;
        if (!nickOk && !hadTag) return false;

        speaker = nickOk ? nick : "";
        return true;
    }

    private static bool IsChatTag(string token)
        => ChatTags.Contains(FoldHomoglyphs(token.Trim().Trim('[', ']', '(', ')', '<', '>').ToLowerInvariant()));

    // Fold the Latin look-alikes players/OCR mix into Cyrillic tags ("Mиp" → "мир"), matching
    // the same homoglyph handling the slang decoder uses.
    private static string FoldHomoglyphs(string s)
    {
        if (s.Length == 0) return s;
        var map = "abcehkmoptxy";
        var cyr = "авсенкмортху";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            int i = map.IndexOf(ch);
            sb.Append(i >= 0 ? cyr[i] : ch);
        }
        return sb.ToString();
    }

    /// <summary>Split a message into chunks no longer than <paramref name="maxChars"/>,
    /// breaking only between words (never mid-word) so each chunk can be pasted into a
    /// game chat that caps a single message's length. A word longer than the limit on its
    /// own is hard-split as a last resort. Returns one chunk if it already fits.</summary>
    public static List<string> SplitForGameChat(string text, int maxChars)
    {
        var chunks = new List<string>();
        text = text?.Trim() ?? "";
        if (maxChars <= 0 || text.Length <= maxChars)
        {
            if (text.Length > 0) chunks.Add(text);
            return chunks;
        }

        var current = "";
        foreach (var word in Regex.Split(text, @"\s+"))
        {
            if (word.Length == 0) continue;

            // A single over-long word: emit what we have, then hard-split the word itself.
            if (word.Length > maxChars)
            {
                if (current.Length > 0) { chunks.Add(current); current = ""; }
                var rest = word;
                while (rest.Length > maxChars)
                {
                    chunks.Add(rest[..maxChars]);
                    rest = rest[maxChars..];
                }
                current = rest;
                continue;
            }

            if (current.Length == 0) current = word;
            else if (current.Length + 1 + word.Length <= maxChars) current += " " + word;
            else { chunks.Add(current); current = word; }
        }
        if (current.Length > 0) chunks.Add(current);
        return chunks;
    }

    /// <summary>True if a line is worth translating (has at least <paramref name="minLetters"/>
    /// letters), rather than background specks. The threshold is user-tunable in live mode.</summary>
    public static bool LooksLikeText(string s, int minLetters = 2)
        => s.Count(char.IsLetter) >= Math.Max(1, minLetters);

    /// <summary>Fraction (0..1) of the letters in <paramref name="s"/> that are Cyrillic.
    /// Used to decide whether pasted text is really Russian before auto-flipping the
    /// translator direction: a single stray Cyrillic character in otherwise Latin text
    /// (a smiley, a name) must NOT count as "this is Russian".</summary>
    public static double CyrillicShare(string s)
    {
        int letters = 0, cyrillic = 0;
        foreach (var ch in s)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            // Cyrillic + Cyrillic Supplement cover Russian and its neighbours.
            if (ch is >= 'Ѐ' and <= 'ԯ') cyrillic++;
        }
        return letters == 0 ? 0 : (double)cyrillic / letters;
    }

    /// <summary>True if a line is written mostly in Cyrillic, i.e. it really is Russian and
    /// should be translated FROM Russian. English chat/system messages (little or no Cyrillic)
    /// return false so the caller can auto-detect their language instead of forcing "ru" — which
    /// makes Google spit out invented Cyrillic for what was plain English. A line with no letters
    /// at all (pure numbers/slang) counts as Russian, since it's typically RU chat shorthand.</summary>
    public static bool IsProbablyRussian(string s, double threshold = 0.2)
        => !s.Any(char.IsLetter) || CyrillicShare(s) >= threshold;

    /// <summary>Lower-case, collapse whitespace, drop edge punctuation — so trivial OCR
    /// variations of the same line compare equal.</summary>
    public static string Normalize(string s)
    {
        s = Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");
        return s.Trim(' ', '.', ',', '!', '?', ':', ';', '"', '\'', '-', '…', '(', ')');
    }

    /// <summary>The stable "core" of a chat line for de-duplication: lower-cased letters and
    /// digits only, everything else (spaces, punctuation, symbols, and the garbage the OCR
    /// emits for animated emojis/icons) removed. Two frames of the SAME message — one read as
    /// "proBlemka: ТС ЛЕГА 2 ДД ❤❤" and the next as "proBlemka: ТС ЛЕГА 2 ДД W" because a
    /// heart animated — collapse to the same signature, so the live loop stops treating the
    /// flicker as a brand-new message to translate.</summary>
    public static string Signature(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>Drop the visual noise the OCR picks up from animated emojis, hearts and game
    /// icons — Unicode symbol/other-punctuation runs and emoji surrogate pairs — while keeping
    /// letters, digits and ordinary sentence punctuation. Used to clean a line before it is
    /// shown and sent to the translator, so a stray "❤"-artifact doesn't ride along.</summary>
    public static string StripNoise(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            // Emoji and pictographs live in the astral planes (surrogate pairs) — skip both halves.
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
                continue;
            }
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            // Symbols (hearts, arrows, dingbats) and "other" marks are OCR noise; keep the rest.
            if (cat is System.Globalization.UnicodeCategory.OtherSymbol
                    or System.Globalization.UnicodeCategory.OtherNotAssigned
                    or System.Globalization.UnicodeCategory.PrivateUse
                    or System.Globalization.UnicodeCategory.Surrogate)
                continue;
            sb.Append(ch);
        }
        // Collapse any whitespace the removals left behind.
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>True if <paramref name="line"/> fuzzy-matches any entry in the set.</summary>
    public static bool ContainsSimilar(IEnumerable<string> set, string line, double threshold)
        => set.Any(s => SimilarEnough(s, line, threshold));

    /// <summary>0..1 similarity from Levenshtein edit distance (1 = identical).</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    /// <summary>Like <see cref="Similarity"/> ≥ threshold, but skips the full edit-distance
    /// computation when the length difference alone already rules a match out — most
    /// candidate pairs in a chat differ in length, so this avoids the O(n·m) matrix.</summary>
    public static bool SimilarEnough(string a, string b, double threshold)
    {
        if (a == b) return true;
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return true;
        // |lenA − lenB| edits are unavoidable, so this bounds the best possible score.
        if (1.0 - (double)Math.Abs(a.Length - b.Length) / max < threshold) return false;
        return 1.0 - (double)Levenshtein(a, b) / max >= threshold;
    }

    // Two-row Levenshtein: O(min(n,m)) memory instead of the full n·m matrix.
    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
