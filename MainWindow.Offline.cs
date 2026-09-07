using System.Windows;
using PWRUHelper.Services;

namespace PWRUHelper;

/// <summary>
/// <b>E8.S3 — the offline engine's block on the About tab</b>: the consent dialog (§3.6), the one
/// button whose content flips between Download and Remove (§4.3), the Cancel that appears only while
/// a transfer is running, and the row that reports all three.
///
/// <para><b>Why it is a partial of its own</b> rather than more of <c>MainWindow.Translate.cs</c>,
/// where E7.S7 put the rest of this tab. That file carries E6.S5's pin
/// <c>KeyTestTests.AC1_The_handlers_name_no_MessageBox</c> — a key-test result goes to a status
/// line and never to a modal — and this story's two dialogs would have turned that assertion into a
/// narrower one about which handlers may show a box. A partial per concern is the repo's own layout
/// rule anyway (<c>project-context.md</c>: domain logic in <c>MainWindow.&lt;Feature&gt;.cs</c>), and
/// the file boundary is what keeps the older pin exactly as strict as it was.</para>
///
/// <para>The one piece that is deliberately NOT here is the <c>ApplySettings</c> call site: I12 says
/// a restore applies a control's side effects explicitly, and that call belongs where the restore
/// is.</para>
/// </summary>
public partial class MainWindow
{

    /// <summary>
    /// The store. Constructed as a FIELD INITIALISER, which is safe and is the point: its
    /// constructor touches no disk, reads no setting and asks the manifest nothing that costs I/O
    /// (I10). Every question it can answer is asked later — from a click, or from
    /// <c>OnWindowLoaded</c>'s pool thread.
    /// </summary>
    private readonly OfflineModelStore _offlineStore = new();

    /// <summary>The last known answer to "is the engine on disk?". Seeded by <c>ApplySettings</c>
    /// from <c>OfflineFallbackEnabled</c> — in memory, so first paint costs no I/O (AC 7) and ruling
    /// R-4 makes the setting a faithful proxy — and corrected by the post-paint probe.</summary>
    private bool _offlineInstalled;

    /// <summary>Non-null exactly while a download is running. It is the ONE piece of state the three
    /// members below share: it makes the main button a no-op mid-transfer, it is what Cancel
    /// cancels, and it stops a repaint from writing over the progress row.</summary>
    private CancellationTokenSource? _offlineDownload;

    /// <summary>
    /// <b>The row, the label and the Cancel button, in one place</b> — the shape
    /// <c>UpdateOcrFilterUi</c> and <c>UpdateEngineStatusUi</c> already have, and for the reason I12
    /// gives: <c>ApplySettings</c> suppresses change handlers for the whole restore, so a side
    /// effect that is not applied by hand does not happen at all. <c>_restoringSettings</c> is
    /// deliberately <b>not</b> consulted here (E6.S3's review found a real bug borrowing that flag
    /// for something it did not mean): nothing in this block is persisted by a change handler, so
    /// there is nothing for it to say.
    ///
    /// <para>It never touches the disk. Everything it renders comes from <see cref="_offlineInstalled"/>,
    /// which is why it is safe to call from inside the restore (AC 7 / I10).</para>
    ///
    /// <para>A transfer in flight owns the row, so a probe or a restore landing mid-download is a
    /// no-op rather than a repaint that erases a percentage.</para>
    /// </summary>
    private void UpdateOfflineEngineUi()
    {
        if (_offlineDownload is not null) return;

        OfflineEngineButton.Content = _offlineInstalled
            ? UserMessages.OfflineRemoveLabel()
            : UserMessages.OfflineDownloadLabel();
        OfflineEngineText.Text = _offlineInstalled
            ? UserMessages.OfflineEngineReady()
            : UserMessages.AboutOfflineNotInstalled();
        OfflineCancelButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// <b>The one click that may open a dialog</b> (AC 1, AC 2, AC 5). §3.6 is a deliberate
    /// divergence from the brief's own flow (d): the consent dialog is never raised by a background
    /// failure, only by this handler, because a modal over a fullscreen game opened by a LIVE loop
    /// the player forgot was running is the single worst thing this app could do (NFR12). A source
    /// scan pins that <c>MainWindow.Live.cs</c> and <c>MainWindow.Ocr.cs</c> can reach no dialog API
    /// at all.
    ///
    /// <para><c>MessageBox.Show(this, …)</c> — <b>with an owner</b>, per <c>project-context.md</c>
    /// and twice in §3.6: a parentless dialog behind an always-on-top window over a fullscreen game
    /// is unfindable. <b>Deviation:</b> §3.6's mockup labels the buttons
    /// <c>[ Download (about 50 MB) ]</c> and <c>[ Not now ]</c>; a WPF <c>MessageBox</c> cannot
    /// relabel its buttons, and a custom window for one confirmation is scope this story does not
    /// have. So it is Yes/No with <b>No as the default</b> — AC 1's "the safe default" — and the
    /// body's last line asks the question in Sally's words.</para>
    ///
    /// <para><c>async void</c> because it is an event handler, which is the one place the language
    /// allows it; every failure inside is caught, because an exception out of an <c>async void</c>
    /// takes the process with it.</para>
    /// </summary>
    private async void OfflineEngine_Click(object sender, RoutedEventArgs e)
    {
        // A transfer is running: the other button is the one that means anything.
        if (_offlineDownload is not null) return;

        if (_offlineInstalled) { RemoveOfflineEngine(); return; }

        if (MessageBox.Show(this, UserMessages.OfflineConsentBody(), UserMessages.OfflineConsentTitle(),
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            != MessageBoxResult.Yes)
            return;     // "Not now" closes with no trace: nothing written, nothing downloaded (AC 1)

        var cts = new CancellationTokenSource();
        _offlineDownload = cts;
        OfflineEngineButton.IsEnabled = false;
        OfflineCancelButton.Visibility = Visibility.Visible;
        OfflineEngineText.Text = UserMessages.OfflineDownloading(0);

        OfflineInstallResult result;
        try
        {
            // Progress<T> captures this thread's SynchronizationContext, so the callback lands back
            // on the dispatcher on its own — and the transfer itself is the store's, off this
            // thread, which is why the app stays fully usable throughout (AC 3). A null percentage
            // is AC 4's degradation and is carried by the type rather than by a convention.
            var progress = new Progress<double?>(p =>
                OfflineEngineText.Text = UserMessages.OfflineDownloading(p is null ? null : (int)p.Value));

            result = await _offlineStore.InstallAsync(progress, cts.Token);
        }
        catch (Exception ex)
        {
            // The store answers with a verdict rather than throwing, so this is the belt to that
            // braces — and it may not be an OperationCanceledException filter (I3): the store has
            // already decided what was a cancel and what was an HttpClient timeout, and re-deciding
            // it here is exactly the masquerade that once disabled the DeepL fallback. I11: the type
            // name, never the message, because a message can carry a path or a URL.
            Logging.Warn("the offline engine install did not finish: " + ex.GetType().Name);
            result = OfflineInstallResult.Failed(OfflineInstallFailure.Network);
        }
        finally
        {
            _offlineDownload = null;
            OfflineCancelButton.Visibility = Visibility.Collapsed;
            OfflineEngineButton.IsEnabled = true;
        }

        if (result.Installed)
        {
            // Ruling R-4 and OQ-11's answer — downloaded ⇒ enabled, and Remove is the only off
            // switch. This assignment and the one in RemoveOfflineEngine are the ONLY writers of
            // this field in the app; a source scan pins it. No SettingsVersion bump and no Migrate
            // step: the field is new, an old settings.json deserialises it to its default, and that
            // is the intended behaviour (I13, §12).
            _settings.OfflineFallbackEnabled = true;
            SettingsService.Save(_settings);
            _offlineInstalled = true;
            RebuildChainsForOfflineChange();
            UpdateOfflineEngineUi();
        }
        else if (result.Cancelled)
        {
            // AC 3's Cancel, held to AC 1's standard for "Not now": the row goes back to where it
            // was and says nothing about what happened. The store has already deleted the partial.
            _offlineInstalled = false;
            UpdateOfflineEngineUi();
        }
        else
        {
            // The row keeps the failure until the next gesture, so a player who looked away still
            // reads why. The label goes back to Download by hand rather than through
            // UpdateOfflineEngineUi, which would overwrite the sentence with the resting one.
            _offlineInstalled = false;
            OfflineEngineButton.Content = UserMessages.OfflineDownloadLabel();
            OfflineEngineText.Text = UserMessages.OfflineDownloadFailed(result.Failure);
        }
    }

    /// <summary>AC 3's Cancel — visible only while a transfer runs. It cancels the token the store
    /// is inside; the store deletes the partial download and answers <c>Cancelled</c>, and the
    /// handler above returns the row to <c>○ Not installed</c>.</summary>
    private void OfflineCancelDownload_Click(object sender, RoutedEventArgs e)
        => _offlineDownload?.Cancel();

    /// <summary>
    /// AC 5's other half. It asks — unlike <c>Clear cache</c>, which does not, and the difference is
    /// §4.3's: clearing the cache destroys nothing the app cannot rebuild, and this destroys 50 MB
    /// the user waited for. The dialog <b>states that it deletes the files</b> rather than asking
    /// whether they are sure; a confirmation that does not say what it will do is a click-through.
    /// </summary>
    private void RemoveOfflineEngine()
    {
        if (MessageBox.Show(this, UserMessages.OfflineRemoveBody(), UserMessages.OfflineRemoveTitle(),
                            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes)
            return;

        var freed = UnloadThenRemove(_offlineEngine, _offlineStore);
        _settings.OfflineFallbackEnabled = false;
        SettingsService.Save(_settings);
        _offlineInstalled = false;
        RebuildChainsForOfflineChange();
        UpdateOfflineEngineUi();
        // …and then the row says what came back, over the resting sentence UpdateOfflineEngineUi
        // just wrote. Same ownership rule the key boxes follow (E7.S7): the standing state is
        // rewritten by the refresh, a transient answer overwrites it until the next gesture.
        OfflineEngineText.Text = UserMessages.OfflineRemovedStatus(freed);
    }

    /// <summary>
    /// <b>E8.S3's review finding, fixed by E8.S4 because E8.S4 owns the lifecycle.</b> On Windows a
    /// loaded DLL cannot be deleted: with the engine resident, <c>Directory.Delete</c> threw, the
    /// store logged a kind and answered 0, and the row read "Offline engine removed — 0 MB freed"
    /// while every file was still on disk and <c>OfflineFallbackEnabled</c> was already false. So
    /// the handle goes first, and only then the files.
    ///
    /// <para><b>It waits, deliberately.</b> <c>Unload</c> takes the provider's one lock, so a frame
    /// in flight finishes (or fails typed through the chain) before the free — that is the whole
    /// point, and it is why this cannot be fired and forgotten. It is the one place the dispatcher
    /// waits on that lock, bounded by a batched frame (3.75 ms a line, measured), and the
    /// alternative is deleting a model out from under a native call.</para>
    ///
    /// <para><b>Static, and that is the seam.</b> Driving <c>RemoveOfflineEngine</c> would mean a
    /// modal in a headless run; as two arguments the ORDER — the only thing this method is — is
    /// provable against the fake engine with no window at all.</para>
    ///
    /// <para><b>What it does NOT do, and its owner.</b> Freeing is not closing: a translation
    /// arriving between the <c>Unload</c> and the <c>Delete</c> loads the engine again, the delete
    /// fails on the mapped DLL, and the row is back to E8.S3's untrue "removed — 0 MB freed". Nothing
    /// is wired to this provider yet, so the window is unreachable today; shutting it needs a
    /// terminal state on <see cref="BergamotTranslator"/> (a load that refuses after a retire), which
    /// is the same thing E8.S2's review already handed to <b>E8.S5</b> ("Dispose is not terminal").
    /// The residual case — a delete that fails for a reason no unload can remove, an AV hold — still
    /// owes a truthful sentence in <c>UserMessages</c> and in the copy deck, in one commit
    /// (ruling E8-b).</para>
    /// </summary>
    internal static long UnloadThenRemove(BergamotTranslator engine, OfflineModelStore store)
    {
        engine.Unload();
        return store.Remove();
    }

    /// <summary>
    /// <b>T7's rebuild, through the seam E6.S3 already landed</b> rather than a second one. Both
    /// chains, because the tier list changes on both: an install adds the offline rung and a remove
    /// takes it away, and a chain built before the change would go on answering from the old tier
    /// list until a restart — the divergence class E6.S3's region-change handler exists to prevent.
    ///
    /// <para><b>It is a no-op today and is written anyway.</b> The builders do not construct the
    /// Bergamot tier yet — that wiring, and the <c>Func&lt;string?&gt;</c> locators this store can
    /// already answer, are <b>E8.S5</b>'s (rulings E8-c/E8-e), which is why this story adds no
    /// plumbing to <c>TranslationChains</c> at all. Calling the seam here is what makes E8.S5 a
    /// change to the builders and not a hunt for the two places that had to learn about it.</para>
    /// </summary>
    private void RebuildChainsForOfflineChange()
    {
        _writeTranslator = BuildWriteChain();
        RebuildReadChains();
    }
}
