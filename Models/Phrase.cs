namespace PWRUHelper.Models;

/// <summary>One quick-chat entry loaded from Data/phrases.json.</summary>
public class Phrase
{
    public string Category { get; set; } = "";
    public string En { get; set; } = "";
    public string Ru { get; set; } = "";
    public string Translit { get; set; } = "";
}
