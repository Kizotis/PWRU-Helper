using System.ComponentModel;
using PWRUHelper.Services;

namespace PWRUHelper;

/// <summary>One OCR line + its translation (updates the UI when translated).</summary>
public class OcrResultItem : INotifyPropertyChanged
{
    /// <summary>Speaker nickname (empty when none), set once when the item is created.</summary>
    public string Speaker { get; set; } = "";

    /// <summary>"Nick: " prefix shown greyed before the body on both lines; "" when no speaker.</summary>
    public string SpeakerPrefix => string.IsNullOrEmpty(Speaker) ? "" : Speaker + ": ";

    /// <summary>The Russian message body, with the nickname split off.</summary>
    public string OriginalBody { get; set; } = "";

    /// <summary>Full Russian line (speaker + body) — what the copy button puts on the clipboard.</summary>
    public string Original => TextMatching.WithSpeaker(Speaker, OriginalBody);

    private string _translationBody = "";
    /// <summary>The translated body (the nickname is never translated). Updating it refreshes the
    /// two-tone translator line and the single-string <see cref="Translation"/> the overlay binds.</summary>
    public string TranslationBody
    {
        get => _translationBody;
        set
        {
            _translationBody = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationBody)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Translation)));
        }
    }

    /// <summary>Full translated line (speaker + translated body), for the compact overlay.</summary>
    public string Translation => TextMatching.WithSpeaker(Speaker, _translationBody);

    // Slang decode ("🔑 В = LFM · ПП = Full Moon Pavilion"), "" when the line has no slang.
    private string _glossary = "";
    public string Glossary
    {
        get => _glossary;
        set { _glossary = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glossary))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
