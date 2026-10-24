using System.ComponentModel;
using PWRUHelper.Services;

namespace PWRUHelper;

/// <summary>One OCR line + its translation (updates the UI when translated).</summary>
public class OcrResultItem : INotifyPropertyChanged
{
    /// <summary>Speaker nickname (empty when none), set once when the item is created.</summary>
    public string Speaker { get; set; } = "";

    /// <summary>True for a line produced by "read once" rather than by the live loop. Both feeds
    /// draw those with a coloured frame: a read-once result now lands IN the live history instead
    /// of wiping it, so it needs to be findable among the lines that were already there. Set once
    /// at creation, before the item reaches the collection — so no change notification is needed.</summary>
    public bool IsReadOnce { get; set; }

    /// <summary>"Nick: " prefix shown greyed before the body on both lines; "" when no speaker.</summary>
    public string SpeakerPrefix => string.IsNullOrEmpty(Speaker) ? "" : Speaker + ": ";

    /// <summary>The Russian message body, with the nickname split off.</summary>
    public string OriginalBody { get; set; } = "";

    /// <summary>The game colours of <see cref="OriginalBody"/>, as runs that concatenate back to it
    /// exactly (<see cref="ChatColors.ColorBody"/>); null when they couldn't be read, and the row
    /// then keeps the uncoloured look. Set once at creation, like the body.</summary>
    public IReadOnlyList<ChatRun>? BodyColors { get; set; }

    /// <summary>The Russian line as the feed paints it: the nickname in the game's pale yellow, the
    /// body in its own colours. The template dims it so the translation stays the line to read.</summary>
    public IReadOnlyList<ChatRun> OriginalRuns
        => WithSpeakerRun(BodyColors ?? new[] { new ChatRun(OriginalBody, null) });

    /// <summary>The translation as the feed paints it — <see cref="ChatColors.ColorTranslation"/>:
    /// the body's main colour, plus the [items] and names that came through unchanged.</summary>
    public IReadOnlyList<ChatRun> TranslationRuns
        => WithSpeakerRun(ChatColors.ColorTranslation(BodyColors, _translationBody));

    // No colours known = no nickname colour either: the row looks exactly as it always did.
    private IReadOnlyList<ChatRun> WithSpeakerRun(IReadOnlyList<ChatRun> body)
        => string.IsNullOrEmpty(Speaker)
            ? body
            : new[] { new ChatRun(SpeakerPrefix, BodyColors == null ? null : ChatPalette.Nick, IsSpeaker: true) }
                .Concat(body).ToList();

    /// <summary>Full Russian line (speaker + body) — what the copy button puts on the clipboard.</summary>
    public string Original => TextMatching.WithSpeaker(Speaker, OriginalBody);

    private string _translationBody = "";
    /// <summary>The translated body (the nickname is never translated). Updating it refreshes the
    /// coloured <see cref="TranslationRuns"/> both feeds paint and the single-string
    /// <see cref="Translation"/>.</summary>
    public string TranslationBody
    {
        get => _translationBody;
        set
        {
            _translationBody = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationBody)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Translation)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationRuns)));
        }
    }

    /// <summary>Full translated line (speaker + translated body), for the compact overlay.</summary>
    public string Translation => TextMatching.WithSpeaker(Speaker, _translationBody);

    /// <summary>True once a real translation is in: not while it is pending (empty) and not a
    /// failure placeholder, which always starts with "(". Gates the "copy translation" buttons.</summary>
    public bool HasTranslation => !string.IsNullOrWhiteSpace(_translationBody) && !_translationBody.StartsWith('(');

    // Slang decode ("🔑 В = LFM · ПП = Full Moon Pavilion"), "" when the line has no slang.
    private string _glossary = "";
    public string Glossary
    {
        get => _glossary;
        set { _glossary = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glossary))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
