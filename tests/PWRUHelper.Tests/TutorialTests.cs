using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>the first-run tour: its script (copy, targets, poses), the bubble placement
/// geometry, and the window wiring (never auto-starts headless, closing marks it seen).</summary>
[Collection("Gates")]
public class TutorialTests
{
    public static IEnumerable<object[]> BothScripts() => new[] { new object[] { false }, new object[] { true } };

    [Theory]
    [MemberData(nameof(BothScripts))]
    public void The_script_is_short_and_every_step_fits_its_limits(bool packMissing)
    {
        var steps = TutorialScript.Steps(packMissing);
        Assert.Equal(packMissing ? 9 : 8, steps.Count);
        Assert.Null(steps[0].Target);                              // the welcome has no spotlight
        Assert.Equal("TutorialButton", steps[^1].Target);          // the finish shows where to replay
        Assert.True(steps[^1].Soft);
        Assert.Equal(packMissing, steps.Any(s => s.Target == "InstallOcrButton"));

        foreach (var s in steps)
        {
            Assert.True(s.Title.Length <= TutorialScript.TitleMax, $"title too long: {s.Title}");
            Assert.True(s.Body.Length <= TutorialScript.BodyMax, $"body is {s.Body.Length} chars: {s.Body}");
            Assert.True(s.Tab is null or >= 0 and <= 4);
        }
        // Under a minute: ~6 s per step at most.
        Assert.True(steps.Count * 6 <= 60);
        Assert.True(steps.Sum(s => s.Title.Count(c => c == '!') + s.Body.Count(c => c == '!')) <= 2);
    }

    [Fact]
    public void Every_spotlight_target_exists_in_the_main_window()
    {
        var xaml = File.ReadAllText(RepoFile("Views/MainWindow.xaml"));
        foreach (var target in TutorialScript.Steps(ocrPackMissing: true).Select(s => s.Target).OfType<string>())
            Assert.Matches(new Regex($"x:Name=\"{Regex.Escape(target)}\""), xaml);
    }

    [Fact]
    public void Every_pose_has_its_image_and_pointing_faces_the_spotlight()
    {
        foreach (var file in TutorialScript.PoseFiles)
            Assert.True(File.Exists(RepoFile($"assets/snufkin/{file}.png")), $"missing pose {file}");
        foreach (var step in TutorialScript.Steps(ocrPackMissing: true))
        {
            Assert.Contains(TutorialScript.PoseFile(step.Pose, true), TutorialScript.PoseFiles);
            Assert.Contains(TutorialScript.PoseFile(step.Pose, false), TutorialScript.PoseFiles);
        }
        Assert.Equal("point-left", TutorialScript.PoseFile(TutorialScript.PosePoint, targetIsLeftOfGuide: true));
        Assert.Equal("point-right", TutorialScript.PoseFile(TutorialScript.PosePoint, targetIsLeftOfGuide: false));
        Assert.Equal("wave", TutorialScript.PoseFile(TutorialScript.PoseWave, targetIsLeftOfGuide: true));
    }

    public static IEnumerable<object[]> Layouts()
    {
        foreach (var (w, h) in new[] { (640.0, 720.0), (460.0, 480.0), (1280.0, 900.0) })
        foreach (var (fx, fy) in new[] { (0.05, 0.05), (0.5, 0.1), (0.9, 0.5), (0.2, 0.85), (0.5, 0.5) })
            yield return new object[] { w, h, fx, fy };
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void The_guide_and_the_bubble_stay_inside_the_window_and_off_the_spotlight(double w, double h, double fx, double fy)
    {
        bool small = w < 560 || h < 560;
        var area = new Size(w, h);
        var bubble = new Size(small ? 230 : 280, 130);
        var guide = small ? new Size(74, 140) : new Size(100, 190);
        var target = new Rect(w * fx - 60, h * fy - 16, 120, 32);
        target.Intersect(new Rect(area));

        var p = TutorialLayout.Place(area, target, bubble, guide);
        var b = new Rect(p.Bubble, bubble);
        var g = new Rect(p.Guide, guide);
        var inside = new Rect(TutorialLayout.Edge - 0.01, TutorialLayout.Edge - 0.01,
                              w - 2 * TutorialLayout.Edge + 0.02, h - 2 * TutorialLayout.Edge + 0.02);
        Assert.True(inside.Contains(b), $"bubble {b} outside {area}");
        Assert.True(inside.Contains(g), $"guide {g} outside {area}");
        Assert.False(b.IntersectsWith(g), $"bubble {b} covers the guide {g}");
        Assert.False(b.IntersectsWith(target), $"bubble {b} covers the spotlight {target}");
    }

    [Fact]
    public void The_welcome_centres_the_guide_with_the_bubble_under_it()
    {
        var p = TutorialLayout.Place(new Size(640, 720), null, new Size(280, 120), new Size(120, 230));
        Assert.Equal(260, p.Guide.X, 1);
        Assert.True(p.Bubble.Y >= p.Guide.Y + 230);
    }

    [Fact]
    public void The_tour_never_starts_headless_and_closing_it_marks_it_seen()
    {
        using var settings = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            Assert.False(window.TutorialLayer.IsActive);
            Assert.Equal(Visibility.Collapsed, window.TutorialLayer.Visibility);

            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(640, 720));
            root.Arrange(new Rect(0, 0, 640, 720));
            root.UpdateLayout();

            window.StartTutorial();
            Assert.True(window.TutorialLayer.IsActive);
            Assert.InRange(window.TutorialLayer.StepCount, 8, 9);

            // Esc is Skip. A never-shown native source stands in for the keyboard's (headless-safe).
            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("tutorial-test"));
            var esc = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.TutorialLayer.HandleKey(esc);
            Assert.True(esc.Handled);
            Assert.False(window.TutorialLayer.IsActive);
        });

        Assert.Contains("\"TutorialSeen\": true", settings.Read());
    }

    [Fact]
    public void Every_pose_resolves_as_a_packed_resource()
    {
        // A broken pack URI builds green and fails only at run time — this is where it fails instead.
        StaTestHost.Run(() =>
        {
            foreach (var file in TutorialScript.PoseFiles)
                Assert.NotNull(Application.GetResourceStream(
                    new Uri($"pack://application:,,,/PWRUHelper;component/assets/snufkin/{file}.png")));
        });
    }

    [Fact]
    public void Held_keys_and_rapid_presses_cannot_run_through_the_tour_and_Alt_keys_pass()
    {
        using var _ = new TempSettings("""{ "SettingsVersion": 3 }""");

        StaTestHost.Run(() =>
        {
            var window = StartedWindow();
            for (int i = 0; i < 12; i++) window.TutorialLayer.Next();   // all during the first transition
            Assert.True(window.TutorialLayer.IsActive);
            Assert.Equal(0, window.TutorialLayer.StepIndex);

            using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("tutorial-test"));
            var alt = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.System) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.TutorialLayer.HandleKey(alt);
            Assert.False(alt.Handled);                                   // Alt+F4 still closes the app
            window.TutorialLayer.Close(finished: false);
        });
    }

    private static MainWindow StartedWindow()
    {
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(640, 720));
        root.Arrange(new Rect(0, 0, 640, 720));
        root.UpdateLayout();
        window.StartTutorial();
        return window;
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PWRUHelper.csproj")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }
}
