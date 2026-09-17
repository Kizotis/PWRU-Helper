namespace PWRUHelper.Services;

/// <summary>One stop of the first-run tour: which tab to show, which real control to spotlight
/// (by x:Name; null = no spotlight), and what the guide says.</summary>
/// <param name="Tab">Tab index to switch to, or null to stay on the current tab (header controls).</param>
/// <param name="Padding">Space between the control and the edge of the spotlight, in DIP.</param>
/// <param name="Soft">The finish step: a lighter dim and a single halo pulse.</param>
/// <param name="Pose">Which pose of the guide to show (assets/snufkin/&lt;pose&gt;.png). <see cref="TutorialScript.PosePoint"/>
/// is resolved at layout time to point-left or point-right, towards the spotlight.</param>
internal sealed record TutorialStep(
    int? Tab, string? Target, string Title, string Body,
    double Padding = 6, bool Soft = false, string Pose = TutorialScript.PosePoint);

/// <summary>
/// The whole first-run tour as data — UI-free so its copy and targets are unit-tested, and the
/// one place its words live. Storyboard: Sally (UX); copy: Sophia. The guide is the owner, Kizotis, drawn as the app's mascot. The tour is under a
/// minute at normal reading speed, which is why every body is capped at <see cref="BodyMax"/>.
/// </summary>
internal static class TutorialScript
{
    // The owner's mascot poses. Point is not a file: it becomes point-left/point-right.
    public const string PoseWave = "wave", PosePoint = "point", PoseThumbsUp = "thumbs-up",
                        PosePresent = "present", PoseFishing = "fishing";
    public static readonly string[] PoseFiles =
        { PoseWave, "point-left", "point-right", PoseThumbsUp, PosePresent, PoseFishing };

    /// <summary>The image a step shows: a pointing pose faces the spotlight.</summary>
    public static string PoseFile(string pose, bool targetIsLeftOfGuide)
        => pose == PosePoint ? (targetIsLeftOfGuide ? "point-left" : "point-right") : pose;
    public const int TitleMax = 24, BodyMax = 110;

    public const string StartLabel = "Show me", SkipLabel = "Skip", BackLabel = "Back",
                        NextLabel = "Next", DoneLabel = "Let's play";

    // Tab indices, in step with MainWindow's TabControl (Phrasebook 0 · Squad 1 · Translator 2 ·
    // Screen OCR 3 · About 4).
    private const int Phrasebook = 0, Squad = 1, Translator = 2, ScreenOcr = 3, About = 4;

    /// <param name="ocrPackMissing">Adds the "install the Russian reading pack" stop — only for a
    /// player who still needs it.</param>
    public static IReadOnlyList<TutorialStep> Steps(bool ocrPackMissing)
    {
        var steps = new List<TutorialStep>
        {
            new(Phrasebook, null, "Hi, I'm Kizotis",
                "Let me show you the way. It takes under a minute, and you can skip anytime.",
                Pose: PoseWave),
            new(Phrasebook, "PhraseSearch", "Your phrasebook",
                "Search for a phrase, then click its card: the Russian is copied. Paste it in game with Ctrl+V."),
            new(Squad, "SquadMessagePanel", "Find a squad",
                "Tick dungeons and classes above. Your Russian group message builds here, ready to copy.",
                Padding: 4),
            new(Translator, "TranslateInput", "Write in Russian",
                "Type in English or French, press Enter. The Russian is copied, cut into blocks that fit game chat.",
                Pose: PoseThumbsUp),
            new(Translator, "FromScreenBar", "Read the chat",
                "Read once translates one box of Russian text. Live keeps translating new chat lines as they arrive."),
            new(ScreenOcr, "LiveButton", "Pick the chat area",
                "Start here: draw a box over the last few lines of the game chat. Live translation begins at once.",
                Padding: 8),
        };
        if (ocrPackMissing)
            steps.Add(new(ScreenOcr, "InstallOcrButton", "Russian reading pack",
                "Windows needs its Russian reading pack once. One click installs it; Windows asks for permission."));
        steps.Add(new(null, "CompactButton", "While you play",
            "Travel light: shrink the app into a small overlay on top of the game. Ctrl+Alt+M switches back and forth.",
            Pose: PosePresent));
        steps.Add(new(About, "TutorialButton", "Good fishing!",
            "Ctrl+Alt+L starts or stops Live on your last area. Replay this tour anytime from About → Tutorial.",
            Soft: true, Pose: PoseFishing));
        return steps;
    }
}
