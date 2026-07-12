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
        // Every one of these was read off a real screenshot of the game chat.
        "мир",                                  // world
        "клан", "фракц", "фракция", "фр",       // faction
        "сист", "система",                      // system
        "лично", "личн", "личное", "личка", "лс",   // private / whisper
        "оснв", "осн", "основной",              // main
        "групп", "группа", "гр",                // group
        "грул",                                 // …which the OCR reliably reads as "грул." — keep it
        "отряд", "отр", "союз", "альянс",
        "торг", "торговля", "торговый", "помощь",
        "обыч", "обычн", "обычный",             // the white "normal" / local channel
    };

    /// <summary>Punctuation the OCR sticks to a badge: the chip's border and the badge's own dot
    /// («Сист.», «Оснв.»). Trimmed before a token is matched against <see cref="ChatTags"/>.
    /// A COMMA is deliberately absent: a badge never ends in one, but a word in the middle of a
    /// sentence does — and "фракция," starting a wrapped line must stay part of the message
    /// (a player listing the channels had their message eaten alive by exactly that).</summary>
    private static readonly char[] TagEdgeTrim = { '[', ']', '(', ')', '<', '>', '.', ':', ';', '|', '-', '—', '*', '_', '\'' };

    /// <summary>Decoration to strip from both ends of a nickname — the player's own ("~V0oDo0~",
    /// "|Ghost|") and the crumbs the OCR leaves behind from the badge's border.</summary>
    private static readonly char[] NickEdgeTrim =
        { ' ', '[', ']', '(', ')', '<', '>', '+', '.', ',', '-', '—', '–', '*', '~', '|', '_', '=' };

    /// <summary>
    /// Peel the channel badge («Мир», «Клан», «Сист.») off the front of an OCR line.
    ///
    /// The game draws it as a coloured chip in front of the nickname, and the OCR reads it in one
    /// of two ways depending on the background filter:
    ///   • filter off  → inline, lower-cased: <c>"мир Hokasse: ОР вар прист +3"</c>
    ///   • filter on   → the chip's own text becomes legible and comes out as a LINE OF ITS OWN,
    ///                   with all the badges grouped ahead of the messages: <c>"Мир"</c>, <c>"Клан"</c>…
    /// The second form is the nastier one: those lines used to be glued together into a fake
    /// message ("Мир Мир Клан Клан") and sent to the translator.
    ///
    /// Returns true when a badge was found. <paramref name="rest"/> is what remains — EMPTY for a
    /// badge-only line, which the caller drops. Tolerates a speck of chip border the OCR mistook
    /// for a letter in front of the tag (<c>"т мир Hokasse: …"</c>).
    /// </summary>
    public static bool TryPeelChannelTag(string line, out string rest)
    {
        var tokens = (line ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int i = 0, afterFirstTag = -1;
        bool found = false;

        while (i < tokens.Length)
        {
            if (IsChatTag(tokens[i]))
            {
                found = true;
                i++;
                if (afterFirstTag < 0) afterFirstTag = i;
                continue;
            }
            // A 1–2 character speck sitting between the chip border and the tag itself.
            if (i + 1 < tokens.Length && tokens[i].Length <= 2 && IsChatTag(tokens[i + 1])) { i++; continue; }
            break;
        }

        if (!found) { rest = string.Join(" ", tokens); return false; }

        // Consume EVERY tag when the line is nothing but badges — that is the filter-on case, where
        // several chips are read together ("Мир Мир Клан Клан") and the whole line must vanish.
        //
        // But a line that still carries text wears exactly ONE badge, so only the first is peeled.
        // Peeling them all would eat the nickname of a player CALLED after a channel ("Мир Мир:
        // привет" → the header would be lost and the message glued onto the one above it).
        rest = string.Join(" ", tokens.Skip(i));
        if (rest.Length > 0) rest = string.Join(" ", tokens.Skip(afterFirstTag));
        return true;
    }

    /// <summary>
    /// Fixed phrases the game uses for its OWN announcements — a rarity drop, a squad join, the
    /// header the game prints above a whisper. Written in the folded alphabet of
    /// <see cref="OcrFold"/>, because the OCR mangles them: "announces loudly" comes back as
    /// "аппоипсеs Ioudly", and "You are speaking to" as "Уои аге speaking to".
    ///
    /// They matter because a system line carries no "Nick:" header and its colon (when it has one)
    /// sits far too deep to read as one — so it was taken for the WRAPPED TAIL of the player message
    /// above it and glued onto it, corrupting both the text and its translation. Its own red «Сист.»
    /// badge can't save it: on the dark game background the contrast filter erases it, so the OCR
    /// never reads it at all.
    ///
    /// Every entry must be a phrase that OPENS an announcement, never one that ends it: an
    /// announcement wraps over several OCR lines, and a phrase from its tail ("…has become the
    /// supreme deity!") would cut the announcement in half instead of separating it from the
    /// message above. The tail carries no marker, so it glues back on by itself.
    ///
    /// This list only holds phrases seen on real screenshots. An unknown announcement still glues —
    /// add it here when one shows up.
    /// </summary>
    private static readonly string[] SystemMarkers =
    {
        "annou",            // "<Elder of the City of Swords> announces loudly: …"
        "joinedthesquad",   // "<Player> joined the squad"
        "speakingto",       // "You are speaking to <Player>: …"  (the whisper header)
        "becomestheowner",  // "<Player> becomes the owner of a real rarity…"
        "heavensgateway",   // "The heavens gateway have opened!…"
    };

    /// <summary>True if the line is one of the game's own announcements rather than a player's
    /// message. See <see cref="SystemMarkers"/> for why this can't lean on the «Сист.» badge.</summary>
    public static bool IsSystemLine(string line)
    {
        var folded = OcrFold(line);
        return SystemMarkers.Any(m => folded.Contains(m, StringComparison.Ordinal));
    }

    // The OCR reads these English announcements through a Russian engine, so it happily spells them
    // with Cyrillic look-alikes ("Уои аге" for "You are", "аппои" for "annou"), and confuses I/l/1
    // and O/0. Fold all of that onto one alphabet, keeping only letters and digits, so a marker can
    // be matched against what the OCR ACTUALLY produced.
    private const string CyrillicLookAlikes = "авсекмнорхтуигпь";
    private const string LatinLookAlikes    = "abcekmhopxtyurnb";

    private static string OcrFold(string? s)
    {
        var sb = new System.Text.StringBuilder((s ?? "").Length);
        foreach (var raw in (s ?? "").ToLowerInvariant())
        {
            int i = CyrillicLookAlikes.IndexOf(raw);
            char ch = i >= 0 ? LatinLookAlikes[i] : raw;
            ch = ch switch { '0' => 'o', '1' or 'l' or '!' or '|' => 'i', _ => ch };
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Split raw OCR lines into whole chat messages using the game's own structure —
    /// each message is <c>[Channel] Nick: text</c>, so a line that starts a new <c>Nick:</c>
    /// (optionally after a channel tag) begins a new message, and any following line without one
    /// is a wrapped continuation glued back on. This works even when players type no punctuation
    /// at all (the usual case in-game), where <see cref="ToSentences"/> can't tell messages apart.
    /// The channel badge is dropped entirely (never shown, never translated — see
    /// <see cref="TryPeelChannelTag"/>); the nickname is kept as a "Nick: text" prefix.</summary>
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

            bool tagged = TryPeelChannelTag(line, out var rest);

            // A badge on its own line (what the background filter produces) carries no message:
            // drop it. It still marks a boundary — the badge belongs to the message that follows.
            if (tagged && rest.Length == 0) { Flush(); continue; }

            // The ORIGINAL line, not `rest`: TryParseHeader peels the badge itself, and it knows the
            // one thing this call site doesn't — that the token before the colon is the nickname and
            // must survive even when it happens to be spelled like a channel ("мир Мир: привет").
            if (TryParseHeader(line, out var sp, out var bd))
            {
                Flush();
                speaker = sp;
                if (bd.Length > 0) body.Add(bd);
                lineCount = 1;
            }
            else if (tagged && rest.Contains(':'))
            {
                // Badged, but the nickname didn't survive the OCR (or there is none — a system
                // announcement like "Сист. Elder of the City of Swords announces loudly: …", whose
                // colon sits far too deep to read as a header). The badge still proves a new message
                // starts here, so don't let it glue onto the previous player's text.
                //
                // The colon is what makes this safe: every chat line has one ("Nick:", "announces
                // loudly:"). Without it, a wrapped line that merely BEGINS with a channel word —
                // "фракция, гильдия, объявление…" — would be mistaken for a badge, and the word
                // would be deleted from the player's message.
                Flush();
                body.Add(rest);
                lineCount = 1;
            }
            else if (IsSystemLine(line))
            {
                // One of the game's own announcements. It has no "Nick:" header and its badge was
                // never read, so it looked like the wrapped tail of the player above — and was glued
                // onto their message. It starts its own message instead. (Its OWN wrapped tail, which
                // carries no marker, still glues onto it correctly: that's the branch below.)
                //
                // Use `rest` when a badge WAS read here ("сист. X joined the squad"): a system marker
                // behind a channel word settles it — that word is the chip, not part of the sentence.
                Flush();
                body.Add(tagged ? rest : line);
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

        // Peel the leading channel badge: "[Мир] Nick" / "Мир Nick" / "т мир Nick" (with the speck of
        // chip border the OCR sometimes leaves in front of it).
        //
        // NEVER the last token, whatever it looks like: in a header that one is the NICKNAME. A
        // player is perfectly entitled to be called "Мир", and peeling their name as if it were a
        // badge left the message headerless — glued, nameless, onto the one above it.
        var tokens = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        int peel = 0;
        bool hadTag = false;
        while (peel < tokens.Count - 1)
        {
            if (IsChatTag(tokens[peel])) { hadTag = true; peel++; continue; }
            if (tokens[peel].Length <= 2 && IsChatTag(tokens[peel + 1])) { peel++; continue; }   // border speck
            break;
        }
        tokens.RemoveRange(0, peel);

        // Whatever the OCR made of the badge's border ("[32)", "|") carries no letters and is not
        // part of the nickname — drop it, but never the last token (that IS the nick).
        while (tokens.Count > 1 && !tokens[0].Any(char.IsLetter)) tokens.RemoveAt(0);

        // Only a recognised tag earns the wider colon window: on an untagged line a colon past
        // MaxHeaderChars is a colon inside the body ("… a colon: here"), not a nick separator.
        if (!hadTag && colon > MaxHeaderChars) return false;

        // Players decorate their names ("~V0oDo0~") and the OCR adds its own crumbs, so trim the
        // decoration off both ends — including the em/en dashes it likes to turn a tilde into.
        var nick = string.Join(" ", tokens).Trim(NickEdgeTrim);

        // A real nickname has a letter (so "12" in "12:30" is rejected), isn't absurdly long, and is
        // at most a few words. One word more is allowed behind a badge: the badge PROVES this is a
        // header, so whatever stands between it and the colon is the name — even a spaced-out one
        // like "С α к э", a real player. Without that proof, a fourth word means we are reading a
        // sentence, not a nickname. A recognised tag with a garbled nick still marks a boundary.
        bool nickOk = nick.Length is > 0 and <= 22 && nick.Any(char.IsLetter)
                      && tokens.Count <= (hadTag ? 4 : 3);
        if (!nickOk && !hadTag) return false;

        speaker = nickOk ? nick : "";
        return true;
    }

    private static bool IsChatTag(string token)
        => ChatTags.Contains(FoldHomoglyphs(token.Trim().Trim(TagEdgeTrim).ToLowerInvariant()));

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

    /// <summary>How many characters the game accepts in one chat message. Anything longer has to be
    /// sent in several — the compact overlay splits a reply on it, the Translator tab shows where
    /// the cut falls.</summary>
    public const int GameChatLimit = 78;

    /// <summary>The blocks a long message has to be sent in, as (start, length) spans into the
    /// ORIGINAL text — so a UI can highlight where each chat message ends without rebuilding the
    /// string. Rebuilding is what a UI must never do: re-joining the pieces would insert a space
    /// between the halves of a hard-split long word, and the text the user copies would no longer be
    /// the text that was translated. Empty when the whole thing fits in one message (nothing to
    /// show), so the caller renders it plain.</summary>
    public static List<(int Start, int Length)> GameChatBlockSpans(string text, int maxChars)
    {
        var spans = BlockSpans(text ?? "", maxChars);
        return spans.Count > 1 ? spans : new List<(int, int)>();
    }

    /// <summary>Split a message into chunks no longer than <paramref name="maxChars"/>, breaking only
    /// between words (never mid-word) so each chunk can be pasted into a game chat that caps a single
    /// message's length. A word longer than the limit on its own is hard-split as a last resort.
    /// Returns one chunk if it already fits.</summary>
    public static List<string> SplitForGameChat(string text, int maxChars)
    {
        text = text?.Trim() ?? "";
        if (maxChars <= 0 || text.Length <= maxChars)
            return text.Length > 0 ? new List<string> { text } : new List<string>();

        return BlockSpans(text, maxChars).Select(s => text.Substring(s.Start, s.Length)).ToList();
    }

    /// <summary>
    /// The one place a message is cut into chat-sized blocks. Both the compact overlay (which sends
    /// the blocks) and the Translator tab (which highlights where they fall) are built on it, so the
    /// two can no longer disagree about where the cut is — they used to: the highlighter located the
    /// blocks by searching for them in the original text, which silently failed on ANY separator that
    /// wasn't a single space (a newline from a Shift+Enter input, a double space), and then reported
    /// the message as fitting when it plainly did not.
    ///
    /// Spans measure the text as it really is, separators included — which is exactly what the game
    /// counts when the user pastes it.
    /// </summary>
    private static List<(int Start, int Length)> BlockSpans(string text, int maxChars)
    {
        var spans = new List<(int Start, int Length)>();
        if (maxChars <= 0 || text.Length == 0) return spans;

        int blockStart = -1, blockEnd = -1;
        void Flush()
        {
            if (blockStart >= 0) spans.Add((blockStart, blockEnd - blockStart));
            blockStart = -1;
        }

        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }

            int wordStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            int wordEnd = i;

            // A single word longer than a whole chat message: nothing can be done but cut it.
            if (wordEnd - wordStart > maxChars)
            {
                Flush();
                for (int p = wordStart; p < wordEnd; p += maxChars)
                    spans.Add((p, Math.Min(maxChars, wordEnd - p)));
                continue;
            }

            if (blockStart < 0) { blockStart = wordStart; blockEnd = wordEnd; }
            else if (wordEnd - blockStart <= maxChars) blockEnd = wordEnd;   // still fits, separators and all
            else { Flush(); blockStart = wordStart; blockEnd = wordEnd; }
        }
        Flush();
        return spans;
    }

    /// <summary>
    /// The digits of a chat line that CARRY MEANING, in order — the ones a reader would act on.
    ///
    /// In an LFM chat the number IS the message: "+5дд" becoming "+2дд" means three slots just
    /// filled. But a plain digit-scan can't be trusted, because the OCR invents digits inside words:
    /// "Olympus" comes back as "01ympus", "f1oomy" as "f100my", "real" as "геа1". Those digits say
    /// nothing about the message and flicker from frame to frame — counting them would make the same
    /// message look like a new one and translate it twice.
    ///
    /// A digit counts when it OPENS its run — a bare number ("4-1", "100+", "2") or a count fused to
    /// the Russian word it counts ("+5дд" → "5", "танк+5прист" → "5"). It does not count when it sits
    /// inside or at the end of a word, or ahead of a LATIN one: that is the shape of every invention
    /// the OCR makes ("f100my", "геа1", "01ympus" — a misread "Olympus", digits and all).
    ///
    /// The runs are cut on punctuation, not just on spaces, because the OCR glues tokens together:
    /// as one whitespace token, "танк+5прист" is a nine-letter word and its count would go in the bin
    /// with it — the very message this rule exists to save.
    ///
    /// The OCR's own O↔0 and l/I↔1 confusion is folded out, but ONLY when folding turns the run into
    /// a pure number ("1OO+" → "100"): otherwise "ОР" — Knights Island — would become "0Р" and the
    /// app would invent a count out of a dungeon name.
    /// </summary>
    public static string MeaningfulDigits(string? s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in Regex.Split(s ?? "", @"[^\p{L}\p{Nd}]+"))
        {
            if (run.Length == 0 || !run.Any(char.IsDigit)) continue;

            var folded = new string(run.Select(ch => ch switch
            {
                'O' or 'o' or 'О' or 'о' => '0',
                'l' or 'I' or 'i' or 'І' or 'і' => '1',
                _ => ch,
            }).ToArray());

            if (folded.All(char.IsDigit)) { sb.Append(folded); continue; }   // "100", "1OO" → "100", "4"

            int digits = 0;
            while (digits < run.Length && char.IsDigit(run[digits])) digits++;
            if (digits == 0) continue;                          // "f100my", "геа1" — buried, invented

            // A count is followed by the Russian word it counts. Latin letters after the digits mean
            // we are looking at a mangled English word, not a number.
            if (run[digits..].All(IsCyrillicLetter)) sb.Append(run[..digits]);
        }
        return sb.ToString();
    }

    private static bool IsCyrillicLetter(char ch) => ch is >= 'Ѐ' and <= 'ԯ';

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
