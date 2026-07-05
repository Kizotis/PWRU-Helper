namespace PWRUHelper.Models;

/// <summary>One quick-chat entry loaded from Data/phrases.json.</summary>
public class Phrase
{
    public string Category { get; set; } = "";
    public string En { get; set; } = "";
    public string Ru { get; set; } = "";
    public string Translit { get; set; } = "";

    /// <summary>Runtime only (not from JSON): whether the user has pinned this phrase.</summary>
    public bool IsFavourite { get; set; }
}
