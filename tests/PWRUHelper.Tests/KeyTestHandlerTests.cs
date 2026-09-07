using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using PWRUHelper.Services;
using Xunit;

namespace PWRUHelper.Tests;

/// <summary>
/// E6.S5 — the button itself: the in-flight state, the <c>finally</c> that gives it back, and the
/// two negatives AC 1 is actually about (a failed test clears nothing, and nothing modal ever
/// appears). The probe is handed IN, so not one of these cases touches a network — the sentences
/// and the provider requests are asserted in <c>KeyTestTests</c>.
///
/// <para><c>[Collection("WPF")]</c>: every case builds a real <see cref="MainWindow"/> on the one
/// STA thread, with <see cref="SettingsService"/> pointed at a throwaway file. The awaits are
/// pumped through a <see cref="DispatcherFrame"/> rather than blocked on, because the handler's
/// continuations come back to that dispatcher — blocking it would deadlock the very thing under
/// test.</para>
/// </summary>
[Collection("WPF")]
public class KeyTestHandlerTests
{
    private const string RealLookingKey = "0123456789abcdef0123456789abcdef";

    /// <summary>AC 3 at the surface: the labels the constructor puts on the buttons are the deck's,
    /// they differ, and the costly one carries its tooltip.</summary>
    [Fact]
    public void The_two_labels_come_from_the_deck_and_are_asymmetric()
    {
        using var temp = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            Assert.Equal(UserMessages.TestKeyLabel(), window.DeepLTestButton.Content);
            Assert.Equal(UserMessages.TestKeyLabelCosts(), window.AzureTestButton.Content);
            Assert.Equal(UserMessages.TestKeyCostsTooltip(), window.AzureTestButton.ToolTip);
            Assert.NotEqual(window.DeepLTestButton.Content, window.AzureTestButton.Content);
        });
    }

    /// <summary>AC 1's in-flight half, asserted while the probe is still pending — the button is
    /// disabled and reads "Testing…" — and again once it answers, where the ORIGINAL label is
    /// back. The two labels differ, so "restored" means restored, not "set to Test key".</summary>
    [Fact]
    public void A_test_in_flight_disables_the_button_and_gives_the_original_label_back()
    {
        using var temp = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var pending = new TaskCompletionSource<KeyTestResult>();

            var task = window.RunKeyTestAsync(window.AzureTestButton, window.AzureStatus,
                ProviderIds.Azure, "westeurope", _ => pending.Task);

            Assert.False(window.AzureTestButton.IsEnabled);
            Assert.Equal(UserMessages.TestingLabel(), window.AzureTestButton.Content);

            pending.SetResult(KeyTestResult.Works());
            Pump(task);

            Assert.True(window.AzureTestButton.IsEnabled);
            Assert.Equal(UserMessages.TestKeyLabelCosts(), window.AzureTestButton.Content);
            Assert.Equal("✓ Key works (westeurope) — Azure is used for what you write.",
                window.AzureStatus.Text);
        });
    }

    /// <summary>
    /// The story's quiet failure mode, written first: a probe that THROWS must still give the
    /// button back. Without the <c>finally</c> the button stays "Testing…" and disabled until the
    /// app is restarted — the same class of bug E5.S4's review had to patch with <c>_readingOnce</c>.
    /// </summary>
    [Fact]
    public void A_throwing_probe_still_restores_the_button_and_says_something()
    {
        using var temp = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            var task = window.RunKeyTestAsync(window.DeepLTestButton, window.DeepLStatus,
                ProviderIds.DeepL, "", _ => throw new InvalidOperationException("boom"));
            Pump(task);

            Assert.True(window.DeepLTestButton.IsEnabled);
            Assert.Equal(UserMessages.TestKeyLabel(), window.DeepLTestButton.Content);
            Assert.Contains("Could not check the key", window.DeepLStatus.Text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The budget, and I3 with it. A probe that never answers is cut by the token the handler owns,
    /// and the catch filters on the SOURCE — never on the exception type, which is the trap that
    /// cost this project three releases. The outcome is the timeout it is, not silence over a
    /// button that has just come back.
    ///
    /// <para>The budget is passed as zero rather than waited out: CI-3 forbids asserting a timing
    /// by sleeping for it.</para>
    /// </summary>
    [Fact]
    public void A_probe_that_never_answers_is_cut_by_the_budget_and_rendered()
    {
        using var temp = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            async Task<KeyTestResult> NeverAnswers(CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return KeyTestResult.Works();
            }

            var task = window.RunKeyTestAsync(window.DeepLTestButton, window.DeepLStatus,
                ProviderIds.DeepL, "", NeverAnswers, TimeSpan.Zero);
            Pump(task);

            Assert.True(window.DeepLTestButton.IsEnabled);
            Assert.Equal(UserMessages.TestKeyLabel(), window.DeepLTestButton.Content);
            Assert.Contains("took too long", window.DeepLStatus.Text, StringComparison.Ordinal);
        });
    }

    /// <summary>AC 1's other negative: a failed test writes nothing. The key box still holds what
    /// was typed, <c>settings.json</c> is byte-for-byte what it was, and the in-memory settings
    /// object never saw an Azure key at all.</summary>
    [Fact]
    public void AC1_A_failed_test_clears_nothing_and_persists_nothing()
    {
        using var temp = new TempSettings("""{ "SettingsVersion": 3 }""");
        string? before = null;

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            window.AzureKeyBox.Password = RealLookingKey;
            window.AzureRegionCombo.Text = "westeurope";
            before = File.ReadAllText(temp.Path);

            var task = window.RunKeyTestAsync(window.AzureTestButton, window.AzureStatus,
                ProviderIds.Azure, "westeurope",
                _ => Task.FromResult(KeyTestResult.Failed(TranslationErrorKind.AuthFailed)));
            Pump(task);

            Assert.Equal("✕ Azure refused this key. Check the key, and that the region matches your resource.",
                window.AzureStatus.Text);
            Assert.Equal(RealLookingKey, window.AzureKeyBox.Password);
            Assert.Equal("", Settings(window).AzureApiKey ?? "");
        });

        Assert.Equal(before, File.ReadAllText(temp.Path));
    }

    /// <summary>Re-entrancy (T3): the button IS the flag, so a second press while one is in flight
    /// starts nothing. A test that could be started twice would spend Azure's quota twice.</summary>
    [Fact]
    public void A_second_press_while_one_is_in_flight_starts_nothing()
    {
        using var temp = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            var pending = new TaskCompletionSource<KeyTestResult>();
            var started = 0;

            Task<KeyTestResult> Probe(CancellationToken ct) { started++; return pending.Task; }

            var first = window.RunKeyTestAsync(window.AzureTestButton, window.AzureStatus,
                ProviderIds.Azure, "westeurope", Probe);
            var second = window.RunKeyTestAsync(window.AzureTestButton, window.AzureStatus,
                ProviderIds.Azure, "westeurope", Probe);
            Pump(second);

            Assert.Equal(1, started);

            pending.SetResult(KeyTestResult.Works());
            Pump(first);
            Assert.True(window.AzureTestButton.IsEnabled);
        });
    }

    /// <summary>An empty box is answered by the resting line — §3.7's "cleared" row, which this app
    /// already words per engine — and costs no request at all. A probe that ran here would report
    /// "refused" over a key that was never sent.</summary>
    [Fact]
    public void An_empty_key_is_answered_by_the_resting_line_and_sends_nothing()
    {
        using var temp = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();

            Click(window, "DeepLTestKey_Click");
            Click(window, "AzureTestKey_Click");

            Assert.Equal("○ Using Google (free, no key needed)", window.DeepLStatus.Text);
            Assert.Equal(UserMessages.AzureNoKeyStatus(), window.AzureStatus.Text);
            // Nothing was in flight, so both buttons are exactly as they were.
            Assert.True(window.DeepLTestButton.IsEnabled);
            Assert.True(window.AzureTestButton.IsEnabled);
            Assert.Equal(UserMessages.TestKeyLabelCosts(), window.AzureTestButton.Content);
        });
    }

    /// <summary>A key with no region never reaches a request either: the Test button refuses
    /// exactly what the Save button refuses, through the same predicate (E6.S3 AC 5).</summary>
    [Fact]
    public void An_azure_key_with_no_region_is_refused_before_anything_is_sent()
    {
        using var temp = new TempSettings("{}");

        StaTestHost.Run(() =>
        {
            var window = new MainWindow();
            window.AzureKeyBox.Password = RealLookingKey;

            Click(window, "AzureTestKey_Click");

            Assert.Equal(UserMessages.AzureNeedsARegion(), window.AzureStatus.Text);
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static AppSettings Settings(MainWindow window) => (AppSettings)typeof(MainWindow)
        .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(window)!;

    private static void Click(MainWindow window, string handler) => typeof(MainWindow)
        .GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(window, new object?[] { window, null });

    /// <summary>
    /// Run the dispatcher until the handler's task completes. The handler awaits on the STA
    /// dispatcher and its continuations are posted back to it, so blocking on the task from this
    /// very thread would deadlock; a nested frame is how WPF itself waits.
    ///
    /// <para>The timer is a fuse and never an assertion: a hung case fails on the check below with
    /// something to read, instead of holding the whole suite (CI-3 forbids asserting BY waiting,
    /// not being bounded).</para>
    /// </summary>
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        var fuse = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Normal,
            (_, _) => frame.Continue = false, dispatcher);
        fuse.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { fuse.Stop(); }

        Assert.True(task.IsCompleted, "the key test never finished");
        task.GetAwaiter().GetResult();
    }
}
