# 00 — Annex: startup path and network path (code-level ground truth)

_Author: Amelia (BMAD Senior Software Engineer), forensic method borrowed from `gds-investigate`._
_Baseline: worktree `\.claude\worktrees\docs+investigations-diagnostic`, commit `4759712` = `main` = v0.14.0, clean._
_Method: **static code reading + grep only.** No build, no test run, no execution. Nothing was measured._

## Purpose and rules for readers

This document is the factual substrate for the Phase 1 investigators on **P1 (slow, variable startup)** and
**P2 (Google "translation limited" message)**. It contains **no fixes and no recommendations** — only what the code
does, with `file:line`, plus what remains unknown.

All paths are relative to the worktree root. Line numbers are those of commit `4759712`.

Evidence grades used throughout:

| Grade | Meaning |
|---|---|
| **[CONFIRMED]** | Read directly in the code at the cited `file:line`. |
| **[INFERRED]** | Follows from confirmed code plus documented platform behaviour; the reasoning is stated. Not verified. |
| **[UNKNOWN]** | Cannot be settled by reading this repository. What would settle it is stated. |

A grade is never upgraded for tidiness. Where the code merely *permits* a behaviour, the finding is INFERRED, not
CONFIRMED.

---

# CASE 1 — Startup execution path (P1)

## 1.0 Process entry

- **There is no hand-written `Main`.** `PWRUHelper.csproj:10` sets `<UseWPF>true</UseWPF>` and there is no `Program.cs`
  in the repository (`ls` of the root shows `App.xaml`, `App.xaml.cs`, `MainWindow.*`, no `Program.cs`). WPF's build
  targets generate `App.g.cs` containing `[STAThread] static void Main()`, which calls `App.InitializeComponent()`
  (loads `App.xaml`'s BAML, i.e. merges `Theme.xaml`) then `App.Run()`. **[CONFIRMED]** that no `Main` exists in
  source; **[INFERRED]** (standard, documented WPF SDK behaviour) for the generated body and its ordering.
- **`app.manifest`** (`app.manifest:1-30`): `requestedExecutionLevel level="asInvoker"` (no elevation at start,
  `app.manifest:10`); `dpiAwareness = PerMonitorV2` + legacy `dpiAware=true` (`app.manifest:18-19`); `supportedOS`
  declares Win8.1 and Win10 GUIDs (`app.manifest:26-27`). **[CONFIRMED]**
- **`App.xaml`** (`App.xaml:1-13`): `StartupUri="MainWindow.xaml"` (`App.xaml:5`) and a single merged dictionary
  `Theme.xaml` (`App.xaml:9`). **[CONFIRMED]**
- **`Theme.xaml`**: 312 lines / ~18 KB. Brushes, `TextBlock`/`ToolTip`/`Button`/`TextBox`/`RichTextBox`/`PasswordBox`/
  `TabControl`/`TabItem`/`ComboBox`/`ComboBoxItem` styles with `ControlTemplate`s. `FontFamily` is `Segoe UI`
  (`Theme.xaml:38`) — a system font, **no embedded/packaged font resource anywhere in the repo**
  (grep for `FontFamily` returns only `Theme.xaml:38`, `MainWindow.xaml:539` = `Consolas`,
  `SelectionOverlay.xaml:11` = `Segoe UI`). No images, no `BitmapImage`, no external URIs in `Theme.xaml`.
  **[CONFIRMED]** — this dictionary is small and self-contained; it is not a plausible multi-second cost.
- **Deployment shape** (relevant to P1 and unique to the shipped artefact, not to `dotnet run`):
  `Build Portable EXE.bat:20-23` and `.github/workflows/release.yml:44-45` publish with
  `-r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
  -p:DebugType=none`, and **deliberately without** `EnableCompressionInSingleFile`
  (`Build Portable EXE.bat:11-19`). **[CONFIRMED]**
  `IncludeNativeLibrariesForSelfExtract=true` means the bundle's **native** libraries are extracted to disk on
  launch (the documented .NET single-file behaviour: extraction under `%TEMP%\.net\<app>\<hash>\`, done once per
  build then reused). **[INFERRED]** — the flag is confirmed, the runtime extraction behaviour is platform
  documentation, and neither the extraction location nor its cost on the affected machines is verified here.

## 1.1 `App.OnStartup` — everything here runs BEFORE `MainWindow` exists

Ordering note: `base.OnStartup(e)` is called **last** (`App.xaml.cs:34`), and WPF creates the `StartupUri` window in
`Application.DoStartup`, after `OnStartup` returns. So every statement below precedes `MainWindow`'s construction.
**[CONFIRMED]** for the code order; **[INFERRED]** for the `StartupUri`-after-`OnStartup` framework ordering.

| # | Step | `file:line` | Thread | Blocking | I/O |
|---|---|---|---|---|---|
| 1 | `new Mutex(true, "PWRUHelper.SingleInstance", out isNew)` | `App.xaml.cs:17` | UI (main STA) | yes | Kernel object (session-local name, no `Global\` prefix) |
| 2 | If already running: `MessageBox.Show` + `Shutdown()` | `App.xaml.cs:20-23` | UI | yes | — |
| 3 | Subscribe `DispatcherUnhandledException` | `App.xaml.cs:28` | UI | no | — |
| 4 | `UpdateService.CurrentVersion` is read to build the log line | `App.xaml.cs:32` | UI | yes | Triggers the `UpdateService` **type initializer** |
| 5 | `Logging.Info("--- PWRU Helper v… starting ---")` | `App.xaml.cs:32` | UI | yes | **File write to `%AppData%\PWRUHelper\logs\log.txt`** |
| 6 | `base.OnStartup(e)` → WPF creates `MainWindow` | `App.xaml.cs:34` | UI | yes | BAML load |

Two consequences of step 4 and step 5 deserve to be called out for P1:

- **Step 4 constructs both of `UpdateService`'s static `HttpClient`s.** `CurrentVersion` (`UpdateService.cs:38-39`)
  is a static member; touching it runs the class's static field initializers, i.e.
  `Http = CreateClient()` (`UpdateService.cs:26`, an `HttpClient` with `Timeout = 8 s`, UA `PWRUHelper-UpdateCheck`,
  `Accept: application/vnd.github+json`, `UpdateService.cs:30-33`) and
  `Downloads = CreateDownloadClient()` (`UpdateService.cs:131-137`, `Timeout.InfiniteTimeSpan`).
  **No network traffic is produced by construction** — an `HttpClient` opens no socket and resolves no proxy until
  the first request. **[CONFIRMED]** for the field initializers and their timing trigger; **[INFERRED]** for
  "no traffic at construction" (documented `HttpClient`/`SocketsHttpHandler` behaviour).
  It also reads the assembly version via `Assembly.GetExecutingAssembly().GetName().Version`
  (`UpdateService.cs:39`). **[CONFIRMED]**
- **Step 5 is the first disk write of the process, it is synchronous, on the UI thread, and it targets ROAMING
  AppData.** `Logging.Default` is built from
  `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` (`Logging.cs:18-20`) —
  `SpecialFolder.ApplicationData` is `%APPDATA%` (**Roaming**), not `%LOCALAPPDATA%`. The write itself is
  `Directory.CreateDirectory(dir)` + `FileInfo` size probe + `File.AppendAllText`, under a lock, per line, with no
  buffering and no async path (`Logging.cs:81-87`, roll check `Logging.cs:95-105`, cap 1 MB → `log.txt` +
  `log.1.txt`, `Logging.cs:69`). Every failure is swallowed (`Logging.cs:89`). **[CONFIRMED]**
  **P1 relevance:** on a machine where `%APPDATA%` is redirected (corporate Folder Redirection to a UNC share,
  OneDrive Known Folder Move, roaming user profile), this is a **network or sync-mediated file write executed on the
  UI thread before the window is created**, and it happens again for settings (§1.2) and possibly three more times
  for the data files (§1.3). **[INFERRED]** — the code path is confirmed, the redirection hypothesis is not tested.

**Startup log lines written at launch (before the window):** exactly one, at `App.xaml.cs:32`. All other
`Logging.*` calls in the app are on failure paths (`grep` inventory: `App.xaml.cs:39`, `MainWindow.Live.cs:233`,
`MainWindow.Ocr.cs:409`, `MainWindow.Phrasebook.cs:276,283`, `MainWindow.Update.cs:110`,
`FallbackTranslator.cs:29,41`, `ScreenCapture.cs:62,66`). **[CONFIRMED]** Note `MainWindow.Phrasebook.cs:276,283`
*can* fire during startup — see §1.3.

## 1.2 `MainWindow` field initializers — run BEFORE the constructor body

Field initializers execute in declaration order, before `MainWindow()`'s body, therefore **before**
`InitializeComponent()`:

| Order | Field | `file:line` | What it does |
|---|---|---|---|
| 1 | `_allPhrases = new()` | `MainWindow.xaml.cs:21` | trivial |
| 2 | `_readTranslator = new CachingTranslator(new TranslationService())` | `MainWindow.xaml.cs:43` | **constructs `TranslationService` → triggers its type initializer → `Http = CreateClient()` (`TranslationService.cs:32,37-44`): an `HttpClient`, `Timeout = 12 s`, Chrome-120 UA.** No traffic. |
| 3 | `_updates = new UpdateService()` | `MainWindow.xaml.cs:49` | instance is trivial; statics already initialized in §1.1 |
| 4 | `_settings = SettingsService.Load()` | `MainWindow.xaml.cs:50` | **synchronous file read + possible immediate write** — see below |
| 5 | `_ocr = new OcrService("ru")` | `MainWindow.xaml.cs:51` | **cheap by design**: stores the tag and a `Lazy<OcrEngine?>`; the engine is NOT built here (`OcrService.cs:36-40`, rationale `OcrService.cs:17-26`) |
| 6 | `_slang = SlangGlossary.FromJson(null)` | `MainWindow.xaml.cs:52` | empty object (`SlangGlossary.cs:53-56`) |
| 7 | `_squad = SquadCatalog.FromJson(null)` | `MainWindow.xaml.cs:53` | empty object (`SquadCatalog.cs:46-49`) |
| 8 | `_ocrItems = new()` | `MainWindow.xaml.cs:54` | trivial |
| 9 | `_toastTimer = new DispatcherTimer { 1.6 s }` | `MainWindow.xaml.cs:56` | not started |
| 10 | `_dataRefreshNotes = new()` | `MainWindow.xaml.cs:121` | trivial |
| 11 | `_dedup = new LiveDedup()` | `MainWindow.xaml.cs:70` | trivial |

**[CONFIRMED]** for all of the above.

**`SettingsService.Load()` (`SettingsService.cs:108-129`) in detail — synchronous, UI thread, roaming path:**

- Path: `Environment.GetFolderPath(SpecialFolder.ApplicationData)` + `PWRUHelper\settings.json`
  (`SettingsService.cs:87-89`) — again **Roaming**. **[CONFIRMED]**
- `File.Exists` then `File.ReadAllText` then `JsonSerializer.Deserialize` (`SettingsService.cs:112-115`).
- `Sanitize` (`SettingsService.cs:118`, body `165-182`) — pure, in-memory.
- `if (Migrate(s)) Save(s)` (`SettingsService.cs:121`) — **a migration performs an immediate synchronous write
  back to disk, on the UI thread, before the window exists.** `Migrate` returns `true` for any file whose
  `SettingsVersion < 3` (`SettingsService.cs:133-159`), i.e. **once per user upgrading from ≤ v0.13.0**, then never
  again. `Save` is `Directory.CreateDirectory` + `File.WriteAllText(tmp)` + `File.Replace`/`File.Move`
  (`SettingsService.cs:186-194`). **[CONFIRMED]**
- Every failure is swallowed; defaults are returned (`SettingsService.cs:126-128`). **[CONFIRMED]**

**No WinRT type is touched in the field initializers.** `OcrService`'s constructor only captures a lambda; the
`Windows.*` types (`Language`, `OcrEngine`) are referenced inside `CreateEngine` (`OcrService.cs:65-88`), which the
`Lazy` does not run yet. **[CONFIRMED]** — this was a deliberate change, documented at `OcrService.cs:17-26` and
`MainWindow.xaml.cs:94-98`.

## 1.3 `MainWindow` constructor body (`MainWindow.xaml.cs:80-104`)

In order:

| # | Step | `file:line` | Notes |
|---|---|---|---|
| 1 | `_writeTranslator = BuildTranslator()` | `MainWindow.xaml.cs:82` → `MainWindow.Translate.cs:226-233` | reads `_settings.DeepLApiKey`; builds `CachingTranslator(TranslationService)` or `CachingTranslator(FallbackTranslator(DeepLTranslator, TranslationService))`. **Constructing `DeepLTranslator` triggers its type initializer → a third static `HttpClient` (`DeepLTranslator.cs:16`, `Timeout = 12 s`).** No traffic. |
| 2 | `InitializeComponent()` | `MainWindow.xaml.cs:83` | loads `MainWindow.xaml` BAML (685 lines / 52 KB). **Fires change handlers** — the reason `_restoringSettings` starts `true` (`MainWindow.xaml.cs:35`, rationale `29-34`). |
| 3 | `_toastTimer.Tick += …` | `MainWindow.xaml.cs:84` | |
| 4 | `OcrResults.ItemsSource = _ocrItems` | `MainWindow.xaml.cs:86` | empty collection |
| 5 | `OcrCommandBox.Text = …` | `MainWindow.xaml.cs:87` | |
| 6 | `ShowAppVersion()` | `MainWindow.xaml.cs:88` → `MainWindow.Update.cs:22-23` | reads assembly version again |
| 7 | `PopulateLanguageCombos()` | `MainWindow.xaml.cs:89` → `MainWindow.xaml.cs:341-356` | 15 `ComboBoxItem`s total |
| 8 | `LoadPhrases()` | `MainWindow.xaml.cs:90` → `MainWindow.Phrasebook.cs:24-69` | **embedded-resource read + editable-copy file I/O + JSON parse + `RebuildPhraseView()`** |
| 9 | `LoadSlang()` | `MainWindow.xaml.cs:91` → `MainWindow.Ocr.cs:132-145` | same shape |
| 10 | `LoadSquad()` | `MainWindow.xaml.cs:92` → `MainWindow.Squad.cs:130-143` | same shape |
| 11 | `BuildSquadTab()` | `MainWindow.xaml.cs:93` → `MainWindow.Squad.cs:31-37` | **builds every squad checkbox eagerly** (`PopulateSquadColumns`, `MainWindow.Squad.cs:42-86`) — CheckBoxes with inline-`Run` TextBlocks, created in code for both dungeon and class grids |
| 12 | `ApplySettings()` | `MainWindow.xaml.cs:99` → `MainWindow.xaml.cs:151-202` | restores every control; **sets `MainTabs.SelectedIndex = s.LastTab`** (`MainWindow.xaml.cs:173-174`); may set `Left/Top/Width/Height` (`177-183`); calls `ScreenCapture.SetMode` (`195`) and `ApplyFontScale` (`199`) |
| 13 | `FromCombo.SelectionChanged += …` | `MainWindow.xaml.cs:102` | wired only after restore |
| 14 | `Loaded += OnWindowLoaded` | `MainWindow.xaml.cs:103` | |

**[CONFIRMED]** for all rows.

**Explicitly NOT done in the constructor:** `CheckOcrAvailability()` is deliberately deferred, with the measurement
recorded in the comment: building the Windows OCR engine costs **26–38 ms measured in-app**
(`MainWindow.xaml.cs:94-98`, `OcrService.cs:17-23`). **[CONFIRMED]** that the code says so; the number itself is a
prior measurement, i.e. **[REPORTED]** in the index's vocabulary — not re-verified here.

### 1.3.1 The three data-file loads: exact file I/O before the window

All three go through the same helper (`FindOrCreateEditable`, `MainWindow.Phrasebook.cs:222-247`) and each is
preceded by an embedded-resource read (`ReadEmbeddedJson`, `MainWindow.Phrasebook.cs:204-214`), which calls
`Assembly.GetManifestResourceNames()` and `GetManifestResourceStream` — **three times** (once per file).
**[CONFIRMED]**

`FindOrCreateEditable` candidate order (`MainWindow.Phrasebook.cs:225-230`):

1. `AppContext.BaseDirectory\Data\<file>` — for a published single-file exe this is **the exe's own directory**.
2. `%AppData%\PWRUHelper\<file>` — **Roaming**.

Per file, worst case before the window is visible:

- 2 × `File.Exists` (`MainWindow.Phrasebook.cs:232`),
- if found: `UpgradeEditableIfStale` → `File.ReadAllText` of the user copy (`MainWindow.Phrasebook.cs:271`), and if
  stale: `FreeBackupPath` (a `File.Exists` loop, up to 1000 iterations, `MainWindow.Phrasebook.cs:290-295`) +
  `File.Copy` + `File.WriteAllText` + a `Logging.Warn` (`MainWindow.Phrasebook.cs:273-277`),
- if not found: `Directory.CreateDirectory` + `File.WriteAllText` at candidate 1, and **on failure a second attempt
  at candidate 2** (`MainWindow.Phrasebook.cs:237-245`),
- then `File.ReadAllText` of the chosen path (`MainWindow.Phrasebook.cs:39`, `MainWindow.Ocr.cs:141`,
  `MainWindow.Squad.cs:139`),
- then a JSON parse.

**[CONFIRMED].**

**Read-only exe directory (the MSI / Program Files case), explicitly:** `File.WriteAllText` on candidate 1 throws,
is caught (`MainWindow.Phrasebook.cs:245`), and the loop tries `%AppData%\PWRUHelper\` — which normally succeeds.
So the behaviour is: **an extra failed write attempt (an exception construction and an ACL check) per data file per
launch, for every MSI-installed user, forever** — the failure is never cached. If both locations fail, the method
returns `null` (`MainWindow.Phrasebook.cs:246`) and the embedded copy is used (`MainWindow.Phrasebook.cs:29,41-44`;
`MainWindow.Ocr.cs:139-142`; `MainWindow.Squad.cs:138-140`), so nothing breaks. **[CONFIRMED]** for the code path;
the cost of a denied write is **[UNKNOWN]** (needs measurement; on an EDR-monitored machine a denied write in
`Program Files` is a filter-driver event).

**A fact the investigators should not trip over:** `Data\slang.json` has **no top-level `"version"` key**
(`Data/slang.json:1-3` — the file starts with `_comment` then `entries`), whereas `Data/phrases.json:3` has
`"version": 1` and `Data/squad.json:3` has `"version": 2`. `UpgradeEditableIfStale` returns immediately when the
*shipped* file's version is `<= 0` (`MainWindow.Phrasebook.cs:270`). Therefore **`slang.json` editable copies are
never refreshed and never backed up**, and its refresh path never contributes startup I/O. **[CONFIRMED]** —
recorded as a fact, not as a finding to act on.

### 1.3.2 What `ApplySettings` costs, and the first layout pass

- `ApplySettings` restores the tab index from `s.LastTab` (`MainWindow.xaml.cs:173-174`) **before the first layout
  pass**. A WPF `TabControl` realizes only the selected `TabItem`'s content, so **which tab is restored determines
  what is built for the first frame.** **[CONFIRMED]** for the code; **[INFERRED]** for the WPF realization
  behaviour.
- If the restored tab is **Phrasebook (index 0)**: `PhraseList` is an `ItemsControl` whose `ItemsPanel` is a
  `UniformGrid` (`MainWindow.xaml:135-144`) — **an `ItemsControl` with a custom, non-virtualizing panel realizes
  every item**. `Data/phrases.json` contains 133 phrase entries (`grep -c '"ru"'`), plus up to 8 "Recent" clones and
  N "Favourites" clones (`MainWindow.Phrasebook.cs:113-118`). Each item is a templated `Button`
  (`MainWindow.xaml:162-166`, style `MainWindow.xaml:44-79`) containing a nested pin `Button` and text. So on the
  order of **140+ templated controls built synchronously during the first layout**. **[CONFIRMED]** for structure
  and count; the **[UNKNOWN]** is what that costs in ms on the affected machines.
- `ApplyFontScale` (`MainWindow.xaml.cs:264-272`) sets `LayoutTransform` on `PhraseList` and `OcrResults` — forces
  a layout invalidation.
- Window placement restore reads `SystemParameters.VirtualScreen*` (`MainWindow.xaml.cs:206-207`) — cheap.
- `ScreenCapture.SetMode(s.CaptureBackend)` (`MainWindow.xaml.cs:195` → `ScreenCapture.cs:35-41`) only sets an enum
  and clears the WGC latch. **It does not construct `WgcCapture`** — that is lazy at first capture
  (`ScreenCapture.cs:53`). **[CONFIRMED]** No WinRT/D3D cost at startup even when the user has selected WGC.

## 1.4 `OnSourceInitialized` (`MainWindow.xaml.cs:429-458`)

Runs when the HWND exists, i.e. during window creation, **before the window is painted**.

- `new WindowInteropHelper(this).Handle`, `HwndSource.FromHwnd`, `AddHook(HotkeyHook)`
  (`MainWindow.xaml.cs:432-434`). **[CONFIRMED]**
- **Five `RegisterHotKey` P/Invokes** (`MainWindow.xaml.cs:426`, calls at `444-448`): Ctrl+Alt+P/T/L/M/R with
  `MOD_NOREPEAT`. Each is a synchronous `user32` call. Failures are collected and surfaced as a persistent About-tab
  warning (`MainWindow.xaml.cs:450-457`). **[CONFIRMED]**
- No DPI code here; DPI is handled entirely by the manifest (§1.0).

## 1.5 `Loaded` → `OnWindowLoaded` (`MainWindow.xaml.cs:130-146`) — the first 2 seconds after the window exists

This handler is `async void`. It contains **two awaits**, so it yields to the dispatcher immediately at the first
one; nothing in it can block the window from being painted.

| # | Step | `file:line` | Thread | Blocking the UI? |
|---|---|---|---|---|
| 1 | `await Task.Run(() => _ocr.IsAvailable)` | `MainWindow.xaml.cs:138` | **thread-pool** | no — this is the deliberate off-UI construction of the Windows OCR engine (`OcrService.cs:39,45`, `CreateEngine` `OcrService.cs:65-88`: `new Language`, `OcrEngine.IsLanguageSupported`, `OcrEngine.TryCreateFromLanguage`, possibly `TryCreateFromUserProfileLanguages`). **This is the first WinRT projection load of the process.** |
| 2 | `CheckOcrAvailability()` | `MainWindow.xaml.cs:139` → `MainWindow.Ocr.cs:40-61` | UI | reads the already-built `Lazy` value and writes two labels |
| 3 | `ShowToast(...)` if any data file was refreshed | `MainWindow.xaml.cs:141` | UI | trivial |
| 4 | `await CheckForUpdatesAsync()` | `MainWindow.xaml.cs:145` → `MainWindow.Update.cs:36-66` | UI + net | **the first network request of the process** |

**[CONFIRMED]** for all four.

### 1.5.1 The update check, precisely (key P1 suspect)

- **When:** only from `OnWindowLoaded` (`MainWindow.xaml.cs:145`) and from the About-tab button
  (`MainWindow.Update.cs:31`, wired at `MainWindow.xaml:567`). **There is no timer, no periodic re-check, and no
  call from the constructor.** **[CONFIRMED]** (grep for `CheckForUpdates` returns exactly these sites).
- **Nothing awaits it before the window is shown.** It is the last statement of an `async void` `Loaded` handler
  that has already yielded twice. **[CONFIRMED]**
- **URL:** `https://api.github.com/repos/Kizotis/PWRU-Helper/releases/latest` (`UpdateService.cs:20-21`).
  Method `GET` (`UpdateService.cs:49`). **[CONFIRMED]**
- **Timeout:** 8 s (`UpdateService.cs:30`). Headers: `User-Agent: PWRUHelper-UpdateCheck`,
  `Accept: application/vnd.github+json` (`UpdateService.cs:32-33`). No proxy, no handler, no HTTP-version policy
  configured. **[CONFIRMED]**
- **On failure:** the whole method is wrapped in `try { … } catch { return null; }` (`UpdateService.cs:47,71-75`);
  a non-success status also returns `null` (`UpdateService.cs:50`). On the startup path `manual == false`, so a
  `null` result produces **no UI at all** (`MainWindow.Update.cs:48-52`). **Offline, DNS failure, proxy 407,
  GitHub 403 rate-limit and an 8 s timeout are therefore all silent and indistinguishable to the user, and none of
  them is logged.** **[CONFIRMED]**
- **Could a DNS/proxy stall delay the UI?** The window is already created and painted by the time this runs, so it
  cannot delay *first paint*. It **can** stall the **UI thread** for up to the 8 s timeout, because
  `Http.GetAsync(...)` is invoked on the UI thread and `HttpClient`/`SocketsHttpHandler` performs part of its
  set-up synchronously on the calling thread before the first genuine async suspension — including the one-time
  initialization of `HttpClient.DefaultProxy`, which on Windows reads the system (WinINET/WinHTTP) proxy
  configuration and, when WPAD is enabled, performs autodiscovery. **[INFERRED]** — the call site and the absence of
  any proxy configuration are CONFIRMED (`MainWindow.Update.cs:47`, `UpdateService.cs:26-35,49`); the *magnitude*
  and even the *existence* of a synchronous portion is platform behaviour that this repository cannot prove.
  **This is the single most important open question for P1** (see Q1.3): it predicts a symptom of
  "window appears, then freezes", not "no window for 6–10 s".
- **The same INFERRED proxy-initialization cost is paid once per process, by whichever `HttpClient` request comes
  first** — normally this update check, since it is the process's first request. A user who translates before the
  update check completes would pay it on the translation instead. **[INFERRED]**

### 1.5.2 What the update check does *after* a successful response

Only if `latest > CurrentVersion` (`UpdateService.cs:69`): a modal `MessageBox.Show(this, …)`
(`MainWindow.Update.cs:54-60`). If the user accepts, `DownloadAndApplyAsync` (`MainWindow.Update.cs:71-114`) runs —
and **that** path calls `IsInstalledBuild()` (`MainWindow.Update.cs:119-137`), which reads
`Environment.ProcessPath`, `Environment.GetFolderPath(ProgramFiles / ProgramFilesX86)`, and then
**`HasArpEntry("PWRU Helper")` — an enumeration of every subkey of both
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` and the `WOW6432Node` twin, opening each and reading
`DisplayName`** (`MainWindow.Update.cs:141-163`). **[CONFIRMED]**
**Important for P1: this registry sweep is NOT on the startup path.** It runs only after the user clicks "Yes" in
the update dialog. No registry access whatsoever occurs during a normal launch. **[CONFIRMED]** (grep for
`Registry` returns only `MainWindow.Update.cs:152`.)

## 1.6 (a) Ordered startup timeline

"Visible" = the first frame is presented. Steps 1–20 precede it; steps 21+ follow it.

| # | Step | `file:line` | Thread | Blocking | I/O type | Varies by environment? | Grade |
|---|---|---|---|---|---|---|---|
| 1 | OS loads the single-file exe; native libs self-extract to `%TEMP%\.net\…` | `Build Portable EXE.bat:20-23` | loader | yes | disk (write on first run per build) + AV scan | **Yes — heavily** (AV/EDR, SmartScreen, MOTW, unsigned 178 MB image, `%TEMP%` location) | [INFERRED] |
| 2 | WPF-generated `Main` → `App.InitializeComponent()` → `Theme.xaml` BAML | `App.xaml:9` | UI | yes | in-bundle resource | no | [INFERRED] |
| 3 | `new Mutex("PWRUHelper.SingleInstance")` | `App.xaml.cs:17` | UI | yes | kernel object | no | [CONFIRMED] |
| 4 | `UpdateService` type init → 2 static `HttpClient`s + assembly version read | `App.xaml.cs:32`, `UpdateService.cs:26,38-39,131` | UI | yes | none (no socket) | no | [CONFIRMED]/[INFERRED] |
| 5 | First log line written | `App.xaml.cs:32` → `Logging.cs:81-87` | UI | yes | **file write, `%APPDATA%` (Roaming)** | **Yes** (redirection / OneDrive KFM / roaming profile) | [CONFIRMED] |
| 6 | `MainWindow` field init: `TranslationService` type init → static `HttpClient` | `MainWindow.xaml.cs:43`, `TranslationService.cs:32,37-44` | UI | yes | none | no | [CONFIRMED] |
| 7 | `SettingsService.Load()`: `File.Exists` + `ReadAllText` + deserialize + sanitize | `MainWindow.xaml.cs:50`, `SettingsService.cs:112-118` | UI | yes | **file read, Roaming** | **Yes** | [CONFIRMED] |
| 8 | `Migrate` → `Save` (one-time, for pre-v0.14 users): temp write + `File.Replace` | `SettingsService.cs:121,186-194` | UI | yes | **file write, Roaming** | **Yes**, and only once per user | [CONFIRMED] |
| 9 | `OcrService` ctor — `Lazy` only, **no WinRT** | `MainWindow.xaml.cs:51`, `OcrService.cs:36-40` | UI | yes | none | no | [CONFIRMED] |
| 10 | `BuildTranslator()` (+ `DeepLTranslator` static `HttpClient` if a key is set) | `MainWindow.xaml.cs:82`, `MainWindow.Translate.cs:226-233`, `DeepLTranslator.cs:16` | UI | yes | none | no | [CONFIRMED] |
| 11 | `InitializeComponent()` — 52 KB BAML, fires change handlers | `MainWindow.xaml.cs:83` | UI | yes | in-bundle resource | no | [CONFIRMED] |
| 12 | `PopulateLanguageCombos()` | `MainWindow.xaml.cs:89` | UI | yes | none | no | [CONFIRMED] |
| 13 | `LoadPhrases()` — embedded read ×1, `File.Exists` ×≤2, possible create/refresh, `ReadAllText`, JSON parse of 133 entries | `MainWindow.xaml.cs:90`, `MainWindow.Phrasebook.cs:24-69,204-247` | UI | yes | **embedded + file (exe dir and/or Roaming)** | **Yes** | [CONFIRMED] |
| 14 | `LoadSlang()` — same shape (no version-refresh possible, §1.3.1) | `MainWindow.xaml.cs:91`, `MainWindow.Ocr.cs:132-145` | UI | yes | embedded + file | **Yes** | [CONFIRMED] |
| 15 | `LoadSquad()` — same shape | `MainWindow.xaml.cs:92`, `MainWindow.Squad.cs:130-143` | UI | yes | embedded + file | **Yes** | [CONFIRMED] |
| 16 | `BuildSquadTab()` — builds all dungeon/class checkboxes in code | `MainWindow.xaml.cs:93`, `MainWindow.Squad.cs:31-86` | UI | yes | none | no | [CONFIRMED] |
| 17 | `ApplySettings()` — restores controls, tab index, window bounds, font scale | `MainWindow.xaml.cs:99,151-202` | UI | yes | none | no | [CONFIRMED] |
| 18 | `OnSourceInitialized`: HWND, `AddHook`, **5 × `RegisterHotKey`** | `MainWindow.xaml.cs:429-448` | UI | yes | `user32` | **Yes** (another app owning a combo changes the result, not the duration) | [CONFIRMED] |
| 19 | First measure/arrange/render of the restored tab (≈140+ templated controls if Phrasebook) | `MainWindow.xaml:135-166` | UI (+ render thread) | yes | GPU/font | **Yes** (GPU driver, software rendering fallback, font cache service) | [CONFIRMED] structure / [UNKNOWN] cost |
| 20 | **— WINDOW VISIBLE —** | | | | | | |
| 21 | `Loaded` → `Task.Run(() => _ocr.IsAvailable)` — **first WinRT load**, off UI | `MainWindow.xaml.cs:138`, `OcrService.cs:65-88` | thread-pool | no | WinRT / language-pack probe | **Yes** (pack present or not; number of user-profile languages) | [CONFIRMED] |
| 22 | `CheckOcrAvailability()` — writes 2 labels | `MainWindow.Ocr.cs:40-61` | UI | yes (trivial) | none | no | [CONFIRMED] |
| 23 | Data-refresh toast, if any | `MainWindow.xaml.cs:141` | UI | trivial | none | rare | [CONFIRMED] |
| 24 | `CheckForUpdatesAsync()` → `GET api.github.com`, 8 s timeout | `MainWindow.xaml.cs:145`, `UpdateService.cs:49` | UI thread issues it | **partially — see §1.5.1** | **network + system proxy resolution (WPAD/PAC)** | **Yes — heavily** | [CONFIRMED] call / [INFERRED] blocking portion |

## 1.7 (b) Everything touched BEFORE the window is visible

**Network:** **none.** No socket is opened, no DNS lookup is issued, no proxy is resolved before the window exists.
Three `HttpClient` objects are *constructed* (steps 4, 6, 10) but constructing one performs no I/O.
**[CONFIRMED]** that no request call site exists on the pre-visible path (the only `GetAsync`/`SendAsync` sites are
`UpdateService.cs:49,111`, `TranslationService.cs:131`, `DeepLTranslator.cs:78`, none of which is reachable before
`Loaded`); **[INFERRED]** that `HttpClient` construction is I/O-free.

**Files (all synchronous, all on the UI thread):**

| Path | Operation | `file:line` |
|---|---|---|
| `%TEMP%\.net\PWRUHelper\<hash>\*` | native-lib self-extraction (first run per build) | `Build Portable EXE.bat:22` [INFERRED] |
| `%APPDATA%\PWRUHelper\logs\` | `Directory.CreateDirectory` | `Logging.cs:83` |
| `%APPDATA%\PWRUHelper\logs\log.txt` | `FileInfo` length probe, then `File.AppendAllText` (1 line) | `Logging.cs:99,86` |
| `%APPDATA%\PWRUHelper\logs\log.1.txt` | `File.Delete` + `File.Move`, only past the 1 MB cap | `Logging.cs:101-102` |
| `%APPDATA%\PWRUHelper\settings.json` | `File.Exists` + `File.ReadAllText` | `SettingsService.cs:112-114` |
| `%APPDATA%\PWRUHelper\settings.json(.tmp)` | `CreateDirectory` + `WriteAllText` + `File.Replace` — **migration only** | `SettingsService.cs:188-194` |
| `<exe dir>\Data\{phrases,slang,squad}.json` | `File.Exists` ×3; possibly `ReadAllText`, `Copy`, `WriteAllText` | `MainWindow.Phrasebook.cs:232,271,274-275`, and `:39` / `MainWindow.Ocr.cs:141` / `MainWindow.Squad.cs:139` |
| `%APPDATA%\PWRUHelper\{phrases,slang,squad}.json` | same, as the second candidate | `MainWindow.Phrasebook.cs:228-229` |
| in-bundle manifest resources | `GetManifestResourceNames()` + stream read, ×3 | `MainWindow.Phrasebook.cs:206-213` |

**[CONFIRMED]** for every row except the first.

**Registry:** **none.** The only registry access in the codebase is `MainWindow.Update.cs:152`, reachable only from
the update-download path. **[CONFIRMED]**

**WinRT:** **none** before the window is visible. The `Windows.*` namespaces are used in `OcrService.cs` (loaded via
`Lazy` at step 21) and `WgcCapture.cs` (loaded only at the first WGC capture, `ScreenCapture.cs:53`).
**[CONFIRMED]**

**WMI / fonts / `Process.Start` / environment probing:** no WMI anywhere (grep: no `ManagementObject`/`WMI`).
`Process.Start` exists only at `MainWindow.xaml.cs:302` (link click), `MainWindow.Ocr.cs:88` (OCR pack install),
`MainWindow.Update.cs:97,104,172` (update/releases) — none on the startup path. Environment probing before the
window is limited to `Environment.GetFolderPath(ApplicationData)` (`Logging.cs:19`, `SettingsService.cs:88`,
`MainWindow.Phrasebook.cs:228`) and `AppContext.BaseDirectory` (`MainWindow.Phrasebook.cs:227`).
`Environment.ProcessPath` and `GetFolderPath(ProgramFiles*)` are used only in `IsInstalledBuild`
(`MainWindow.Update.cs:123-126`), off the startup path. **Nothing on the startup path reads `Program Files`.**
**[CONFIRMED]**

**Blocking primitives:** no `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()` anywhere in app code (grep).
`Thread.Sleep` exists only at `ClipboardService.cs:44` (clipboard retry, on a worker thread) and
`WgcCapture.cs:73` (WGC frame wait) — neither on the startup path. `Task.Delay` appears at
`MainWindow.Live.cs:243`, `MainWindow.Ocr.cs:165`, `TranslationService.cs:153` — none on the startup path.
`Dispatcher.BeginInvoke` appears at `MainWindow.Phrasebook.cs:126` (`DispatcherPriority.Loaded`, scroll restore,
only fires when a previous scroll offset existed) and `MainWindow.xaml.cs:470` (`DispatcherPriority.Input`, hotkey
focus). **[CONFIRMED]**

## 1.8 (c) What happens in the first ~2 seconds AFTER the window is visible

1. A thread-pool work item builds the Windows OCR engine (`MainWindow.xaml.cs:138`); this is the **first WinRT
   projection load** and, on a machine without the Russian pack, also runs
   `OcrEngine.TryCreateFromUserProfileLanguages()` (`OcrService.cs:80`). **[CONFIRMED]**
2. Two `TextBlock`s on the Screen OCR tab are updated (`MainWindow.Ocr.cs:46-59`) — a tab the user is usually not
   looking at (`MainWindow.xaml.cs:96-98`).
3. Possibly one toast (`MainWindow.xaml.cs:141`).
4. **One HTTPS GET to `api.github.com`, issued from the UI thread, 8 s timeout, result silently discarded when the
   app is up to date** (`MainWindow.xaml.cs:145` → `UpdateService.cs:49`). This is where system proxy resolution
   happens for the whole process. **[CONFIRMED]** call, **[INFERRED]** proxy-resolution placement.
5. Nothing else. **No translation, no cache warm-up, no capture, no timer is started at launch.** The only
   `DispatcherTimer`s in the app are `_toastTimer` (started only by `ShowToast`, `MainWindow.xaml.cs:400`) and the
   overlay's `_beat` (started only when the overlay becomes visible, `CompactOverlay.xaml.cs:50`).
   **[CONFIRMED]**

## 1.9 (d) Open questions — Case 1

| ID | Question | Grade | What would settle it |
|---|---|---|---|
| Q1.1 | On the slow machines, is the 6–10 s spent **before any window appears**, or does the window appear quickly and then freeze? | [UNKNOWN] | A stopwatch observation by the affected users; or ETW/WPR trace. This single answer splits the suspect list in half: "no window" points at steps 1–19 (loader, AV, roaming I/O, first layout), "window then freeze" points at step 24 (proxy/network). |
| Q1.2 | Is `%APPDATA%` redirected (UNC Folder Redirection, OneDrive KFM, roaming profile) on the slow machines? | [UNKNOWN] | `echo %APPDATA%` + `fsutil reparsepoint query` on each machine; compare with a machine that starts fast. Steps 5, 7, 8, 13–15 all write/read there synchronously on the UI thread. |
| Q1.3 | Does `HttpClient.DefaultProxy` initialization (WPAD/PAC) block the UI thread inside `Http.GetAsync` at `UpdateService.cs:49`, and for how long? | [INFERRED → needs measurement] | Set `netsh winhttp show proxy` / check `HKCU\...\Internet Settings\AutoDetect` on the slow machines; or time the app with `DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER`-style probes / a stopwatch around the call in an instrumented build. |
| Q1.4 | What does the single-file self-extraction of native libraries actually cost per launch on the affected machines, and is `%TEMP%` itself redirected or scanned aggressively? | [UNKNOWN] | Compare a launch with a pre-warmed `%TEMP%\.net\…` against a cleared one; check AV exclusions. |
| Q1.5 | How long does the first layout of the Phrasebook tab (≈140+ non-virtualized templated buttons) take, and does startup time correlate with the restored `LastTab`? | [UNKNOWN] | Ask affected users which tab the app opens on; compare startup with `LastTab = 0` vs `LastTab = 4` (About). |
| Q1.6 | Does the one-time `SettingsVersion` migration write (`SettingsService.cs:121`) coincide with the reports? It fires exactly once, on the first launch after upgrading from ≤ v0.13.0. | [UNKNOWN] | Ask whether the slowness was present on the *second* launch too, or only the first after an update. |
| Q1.7 | Is the exe unsigned on the affected machines and does it carry Mark-of-the-Web? | [UNKNOWN] — code signing is confirmed pending (`packaging/signpath-signing.md`, per `project-context.md`) | `Get-Item PWRUHelper.exe -Stream Zone.Identifier`; test after `Unblock-File`. |
| Q1.8 | For MSI installs, what does the per-launch failed `File.WriteAllText` into `Program Files\…\Data\` cost under EDR? (3 denied writes per launch, never cached — `MainWindow.Phrasebook.cs:237-245`) | [UNKNOWN] | Procmon on an MSI-installed machine; compare portable vs installed startup times. |

---

# CASE 2 — Translation call path (P2)

## 2.1 (a) Endpoint, headers, timeouts

### Google (`Services/TranslationService.cs`) — the default and the only backend used by the OCR feed

| Property | Value | `file:line` |
|---|---|---|
| URL | `https://translate.googleapis.com/translate_a/single?client=gtx&sl={source}&tl={target}&dt=t&q={UrlEncode(text)}` | `TranslationService.cs:122-123` |
| Method | `GET` | `TranslationService.cs:131` |
| Query params present | `client=gtx`, `sl`, `tl`, `dt=t`, `q` | `TranslationService.cs:122-123` |
| Query params **absent** | `ie`, `oe`, `hl`, `dj`, `tk`, `dt` (only one), no `source`/`Referer` hints | `TranslationService.cs:122-123` |
| `q` encoding | `HttpUtility.UrlEncode` (`System.Web`) | `TranslationService.cs:123` |
| `MaxQueryBytes` | **1500** (UTF-8 bytes) | `TranslationService.cs:35` |
| `HttpClient` lifetime | **`private static readonly`** — one instance per process, shared by both `_writeTranslator` and `_readTranslator` | `TranslationService.cs:32` |
| `Timeout` | **12 s** | `TranslationService.cs:39` |
| `User-Agent` | **SET**, hard-coded: `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36` | `TranslationService.cs:41-42` |
| `Accept-Language` | **not set** | (absence — `TranslationService.cs:37-44` sets only UA) |
| `Accept`, `Referer`, cookies | **not set** | same |
| HTTP version | not configured → framework default `HTTP/1.1`, `RequestVersionOrLower`. No `DefaultRequestVersion`/`DefaultVersionPolicy` anywhere (grep). | [CONFIRMED] absence / [INFERRED] resulting default |
| Handler / proxy | **no `HttpClientHandler`/`SocketsHttpHandler` is constructed anywhere** → default handler, `UseProxy = true`, proxy = `HttpClient.DefaultProxy` = the Windows system proxy (WinINET/WinHTTP, incl. WPAD/PAC when enabled) | [CONFIRMED] absence / [INFERRED] resulting behaviour |
| Certificate/TLS callbacks | none | (grep: no `ServerCertificateCustomValidationCallback`) |

**[CONFIRMED]** for every row not marked otherwise. Note the UA is a **fixed, ageing Chrome 120 string with no
`Accept-Language` and no `Accept`** — a combination that a bot-detection layer can score differently from a real
browser. Whether Google actually does so is **[UNKNOWN]** (needs external research / traffic capture).

### DeepL (`Services/DeepLTranslator.cs`) — only when the user has saved a key

| Property | Value | `file:line` |
|---|---|---|
| URL | `https://api-free.deepl.com/v2/translate` when the key ends in `:fx`, else `https://api.deepl.com/v2/translate` | `DeepLTranslator.cs:24-26,30` |
| Method | `POST`, `FormUrlEncodedContent`, one `text` field per line + `target_lang` (+ `source_lang` unless `auto`) | `DeepLTranslator.cs:62-71` |
| `HttpClient` | `private static readonly`, `Timeout = 12 s`, **no custom headers at all** | `DeepLTranslator.cs:16` |
| Auth | `Authorization: DeepL-Auth-Key <key>` per request | `DeepLTranslator.cs:73` |

**[CONFIRMED]**

### GitHub update check

Covered in §1.5.1 — separate client, 8 s timeout, its own UA.

## 2.2 Chunking

- `TranslateAsync` sends **one request** when `UTF8.GetByteCount(text) <= 1500`; otherwise it iterates
  `ChunkText(text, 1500)` and issues **one request per chunk sequentially**, concatenating the results
  (`TranslationService.cs:57-64`). **[CONFIRMED]**
- `ChunkText` splits on sentence terminators `. ! ? … \n` via `Regex.Split(text, @"(?<=[\.\!\?…\n])")` and packs
  pieces up to the byte budget (`TranslationService.cs:181-202`). A single piece larger than the budget goes to
  `HardSplit`, which splits per character on the byte budget (`TranslationService.cs:204-218`). Both are `internal`
  and unit-tested. **[CONFIRMED]**
- `TranslateLinesAsync` (`TranslationService.cs:73-116`):
  - 0 lines → empty list; **1 line → exactly one `TranslateAsync`** (`:76-78`);
  - ≥2 lines and the newline-joined text ≤ 1500 bytes → **one batched request**; if the response splits back into
    the same number of `\n`-separated parts, done (`:80-88`);
  - otherwise (count mismatch, or joined text too long, or a non-`TranslationException` failure) → **one request
    per line** (`:95-108`).
  - **A `TranslationException` raised by the batched attempt is rethrown, not downgraded to per-line**
    (`:91`). **[CONFIRMED]**

## 2.3 Retry policy (`TranslationService.RequestAsync`, `TranslationService.cs:119-178`)

- **3 attempts maximum** (`for (int attempt = 0; attempt < 3; attempt++)`, `:126`).
- Retryable ("transient") = **HTTP 429 or any 5xx** (`:139`). Everything else is **not** retried.
- Delay between attempts: **`Task.Delay(300 * (attempt + 1), ct)`** → 300 ms then 600 ms (`:153`).
  **Fixed, linear, no jitter, no exponential growth, no `Retry-After` header is read.** **[CONFIRMED]**
- `HttpRequestException` is caught and retried **only while `attempt < 2`** (`:151`); on the third attempt it
  escapes uncaught.
- `ct.ThrowIfCancellationRequested()` at the top of each attempt (`:128`).
- **A timeout is not retried.** An `HttpClient` timeout surfaces as `TaskCanceledException`, which is neither
  `HttpRequestException` nor caught anywhere in this loop, so it propagates out of the very first attempt.
  **[CONFIRMED]** (no `catch (OperationCanceledException)` / `catch (TaskCanceledException)` exists in
  `RequestAsync`.)
- **Line 156-157 (`if (json == null) throw new TranslationException("Couldn't reach the translation service…")`)
  appears to be unreachable**: every path out of attempt 2 either breaks with `json` set, or throws at `:143`,
  `:145-146` or `:148`, or lets `HttpRequestException` escape. **[INFERRED]** — derived by exhausting the branches;
  worth stating because an investigator searching for that user-facing string will not find a live producer for it.

## 2.4 (b) Error mapping — condition → exception → exact user-visible string → logged?

**All user-visible strings below are quoted verbatim from the code.**

### Produced inside `TranslationService`

| # | Condition | Code | Exception thrown | Exact message | Logged? |
|---|---|---|---|---|---|
| E1 | Non-success, **not** 429 and **not** 5xx — i.e. **400, 401, 403, 404 are all folded together** | `TranslationService.cs:138-143` | `TranslationException` | `Translation service error (HTTP {code}). Please try again later.` | **No** |
| E2 | **429 on the 3rd attempt** | `TranslationService.cs:144-146` | `TranslationException` | **`Google is limiting translations right now — wait a minute and try again.`** | **No** |
| E3 | 5xx on the 3rd attempt | `TranslationService.cs:147-148` | `TranslationException` | `Translation service is unavailable (HTTP {code}). Try again shortly.` | **No** |
| E4 | Body is not the expected JSON (**HTML captcha / "unusual traffic" / consent page**) | `TranslationService.cs:172-177` | `TranslationException` | `The translation service returned an unexpected response (it may be temporarily blocked). Try again shortly.` | **No** |
| E5 | `HttpRequestException` on the 3rd attempt (**DNS failure, TLS failure, connection refused, proxy unreachable — all identical**) | escapes `TranslationService.cs:151` | `HttpRequestException` | mapped by `Friendly` → **`no Internet connection`** | **No** |
| E6 | 12 s timeout | `TranslationService.cs:131` | `TaskCanceledException` | mapped by `Friendly` → **`the request timed out`** | **No** |
| E7 | Per-line loop: first line to raise a `TranslationException` | `TranslationService.cs:105` | — (swallowed) | that line's cell becomes **`(rate-limited — try again shortly)`** | **No** |
| E8 | Per-line loop: every subsequent line after E7 | `TranslationService.cs:102` | — | **`(skipped — rate-limited, try again shortly)`** | **No** |
| E9 | Per-line loop: any other exception on one line | `TranslationService.cs:106` | — | **`(translation failed: {ex.Message})`** | **No** |
| E10 | Single-line path, non-`TranslationException` failure | `TranslationService.cs:114` | — | **`(translation failed: {ex.Message})`** | **No** |

**The code does NOT distinguish 429 from 403.** E1 lumps 403 with 400/404 into "Translation service error (HTTP
403)"; only a 429 reaches E2. An HTML block page reaches E4 via a `JsonException`, never E2. A DNS failure (E5) is
reported as "no Internet connection" even when the machine is online but the resolver or proxy is failing.
**[CONFIRMED]**

`Friendly` (`MainWindow.xaml.cs:404-410`):

```
TranslationException te            => te.Message,
System.Net.Http.HttpRequestException => "no Internet connection",
TaskCanceledException              => "the request timed out",
_                                  => ex.Message,
```

### Produced inside `DeepLTranslator` (only with a key set)

| # | Condition | `file:line` | Exact message |
|---|---|---|---|
| D1 | 401 / 403 | `DeepLTranslator.cs:101` | `DeepL rejected the API key — check it in Settings.` |
| D2 | 456 | `DeepLTranslator.cs:102` | `DeepL free quota is used up for this month.` |
| D3 | 429 | `DeepLTranslator.cs:103` | `DeepL is rate-limiting right now — try again shortly.` |
| D4 | any other non-success | `DeepLTranslator.cs:104` | `DeepL service error (HTTP {code}).` |
| D5 | timeout (OCE with `ct` NOT cancelled) | `DeepLTranslator.cs:81-88` | `DeepL timed out — check your connection or try again.` |
| D6 | `HttpRequestException` | `DeepLTranslator.cs:89-92` | `Couldn't reach DeepL. Check your Internet connection.` |
| D7 | unparseable body / count mismatch | `DeepLTranslator.cs:53,127` | `DeepL returned an unexpected response.` |

**These are almost never seen by the user**, because `FallbackTranslator` catches all of them and retries on Google
(`FallbackTranslator.cs:25-31,37-43`), logging **`Primary translator failed, using fallback: <message>`**
(`FallbackTranslator.cs:29,41`). **This is the only place in the entire translation pipeline that writes to the
log.** **[CONFIRMED]**

### Where each string is displayed

| Surface | Control / mechanism | `file:line` |
|---|---|---|
| Translator tab, status line under the buttons | `TranslateStatus.Text = $"Failed: {Friendly(ex)}"`, foreground reset to `TextMutedBrush` | `MainWindow.Translate.cs:106-109` |
| Screen OCR / LIVE status line (and the overlay's) | `SetScreenStatus($"Live hiccup ({Friendly(ex)}) — retrying…")`, or after 5 errors `Live stopped after repeated errors ({Friendly(ex)}).` | `MainWindow.Live.cs:238,235`; `SetScreenStatus` = `MainWindow.Live.cs:157-161` |
| Each feed row's translation cell | `it.TranslationBody = $"({Friendly(ex)})"` | `MainWindow.Live.cs:281`, `MainWindow.Ocr.cs:298` |
| Read-once status | `SetScreenStatus($"OCR failed: {Friendly(ex)}")` | `MainWindow.Ocr.cs:248` |
| Compact overlay reply line | `⚠ {r.Error} — your text is kept, press Enter to retry.` | `CompactOverlay.xaml.cs:142` |

**[CONFIRMED]**

### Logging in the translation pipeline

Complete inventory (grep over `Logging.Warn|Error|Info`):

- `FallbackTranslator.cs:29` and `:41` — `"Primary translator failed, using fallback: " + ex.Message`. **No status
  code beyond what the message already contains, no response body, no URL, no timestamp of the request.**
- `MainWindow.Live.cs:233` — `Logging.Error("Live translation auto-stopped after 5 consecutive errors", ex)`; this
  one does include the exception type and full `ToString()` (`Logging.cs:47`).
- **Nothing else.** `TranslationService` never logs. A user hitting E1–E10 on the Translator tab produces **zero log
  lines**, so the About tab's "Copy error report" button (`MainWindow.xaml.cs:312-322`) returns nothing useful for
  P2 unless a DeepL key is configured or LIVE auto-stopped. **[CONFIRMED]** — this is a significant evidence gap for
  the P2 investigation.

## 2.5 Pipeline shape, cache, and when it is rebuilt

Two independent translator chains coexist:

| Chain | Built where | Composition | Used by |
|---|---|---|---|
| `_readTranslator` | field initializer, `MainWindow.xaml.cs:43` | `CachingTranslator(TranslationService)` — **Google only, always** | OCR read-once and the LIVE loop (`MainWindow.Live.cs:315,320`) |
| `_writeTranslator` | `MainWindow.xaml.cs:82` → `BuildTranslator()`, `MainWindow.Translate.cs:226-233` | key empty → `CachingTranslator(TranslationService)`; key set → `CachingTranslator(FallbackTranslator(DeepLTranslator, TranslationService))` | Translator tab (`MainWindow.Translate.cs:94`) and overlay quick reply (`MainWindow.Translate.cs:37`) |

**[CONFIRMED]**, and this matches `project-context.md`'s stated pipeline shape.

- **Rebuilt only when the DeepL key is saved** (`MainWindow.Translate.cs:239`, from the Save button
  `MainWindow.xaml:623`). `_readTranslator` is `readonly` and is never rebuilt. **[CONFIRMED]**
- **Cache: in-memory bounded LRU, capacity 500 by default** (`CachingTranslator.cs:24-29`), backed by a
  `Dictionary` + `LinkedList` under a lock (`:20-22, 87-126`). **Not persisted to disk in any form** — there is no
  file, no serialization, and no cache path anywhere in `CachingTranslator.cs`. **It dies with the process, and a
  key re-save starts a fresh one** (`MainWindow.Translate.cs:239` comment). **[CONFIRMED]**
- **Cache key:** `source + "|" + target + "|" + text.Trim()` (`CachingTranslator.cs:73-78`).
- **Only successes are stored:** `IsCacheable` rejects empty input, empty result, and **any value starting with
  `(`** — which is exactly the shape of every failure placeholder E7–E10 (`CachingTranslator.cs:82-85`).
  **[CONFIRMED]** A rate-limit therefore cannot be "stuck" in the cache.
- `TranslateLinesAsync` on the cache serves hits locally and asks the inner translator only for the misses, then
  splices by index (`CachingTranslator.cs:42-69`). **This means the request the OCR feed actually issues contains
  only the not-yet-seen lines.**
- **Accepted, not a finding (per the owner):** `TranslationService.cs:104`,
  `catch (OperationCanceledException) { throw; }` inside the per-line loop, catches an `HttpClient` *timeout* as
  well as a real cancel (a timeout is a `TaskCanceledException` with `ct` not cancelled), so a timeout on line *k*
  discards the *k−1* successes that loop had already collected. Recorded here for completeness only.

## 2.6 (c) Every call site that triggers a translation

| # | Call site | Trigger | Cadence | Debounce | In-flight cancellation | Batch size | Translator |
|---|---|---|---|---|---|---|---|
| T1 | Translator tab "Translate" button | `Click` → `Translate_Click` → `RunTranslation()` | one per click | **none needed** — `RunTranslation` returns immediately while `TranslateButton.IsEnabled == false` (`MainWindow.Translate.cs:65,85,113`) | **no cancellation token is passed at all** (`MainWindow.Translate.cs:94` calls the 3-arg overload → `ct = default`) | 1 text (chunked if > 1500 B) | `_writeTranslator` |
| T2 | Translator tab, **Enter key** | `TranslateInput_KeyDown`, fires only for `Key.Enter` without Shift (`MainWindow.Translate.cs:52-61`), wired at `MainWindow.xaml:267` | one per Enter | same button-disabled guard | none | same | `_writeTranslator` |
| T3 | **Per keystroke?** | **NO.** `TranslateInput` has **only** `KeyDown="TranslateInput_KeyDown"` (`MainWindow.xaml:267`) — **no `TextChanged` handler on the translator input anywhere.** The only `TextChanged` handlers in the app are `PhraseSearch_TextChanged` (local list filter, `MainWindow.xaml:127`), `OcrColorHex_Changed` (settings save, `MainWindow.xaml:483`) and `ReplyBox_TextChanged` (placeholder visibility only, `CompactOverlay.xaml.cs:109-117`). **[CONFIRMED]** | — | — | — | — | — |
| T4 | Auto-flip on Cyrillic | inside `RunTranslation`, **before** the request: if `from != "ru"` and `TextMatching.CyrillicShare(text) >= 0.3`, flips `from`/`to` and the combos (`MainWindow.Translate.cs:77-83`). **Does not issue an extra request.** | — | — | — | — | — |
| T5 | Compact overlay quick reply | `ReplyBox_KeyDown`, `Key.Enter` only (`CompactOverlay.xaml.cs:124-137`); `_replying` flag blocks re-entry (`:122,128,133,137`) | one per Enter | flag guard | none (`MainWindow.Translate.cs:37` passes no token) | 1 text | `_writeTranslator` |
| T6 | Read-once OCR (button `MainWindow.xaml:297`, or Ctrl+Alt+R `MainWindow.xaml.cs:474`) | `ReadRegionOnceAsync` → `TranslateSentencesInto` → `TranslateBodiesAsync(..., default)` (`MainWindow.Ocr.cs:243,295`) | one per invocation; `_readingOnce` flag prevents overlap (`MainWindow.Ocr.cs:212,219,254`) | flag guard | **`ct = default` — explicitly no cancellation** (`MainWindow.Ocr.cs:295`) | **1 or 2 requests** (ru group + auto group), each possibly exploding to per-line | `_readTranslator` |
| T7 | LIVE loop | `LiveLoop` tick → `AppendLinesToHistory` → `TranslateBodiesAsync(..., ct)` (`MainWindow.Live.cs:207,275`) | **only when `confirmed.Count > 0`** (`MainWindow.Live.cs:203`) | dedup, see below | **yes** — `_liveCts.Token` threaded all the way to `HttpClient` (`MainWindow.Live.cs:109-110,315,320`) | 1–2 groups, each 1 batched request or N per-line | `_readTranslator` |
| T8 | Phrasebook | **Never translates.** `PhraseCard_Click` only copies `p.Ru` to the clipboard and records a "recent" (`MainWindow.Phrasebook.cs:190-200`). Squad builder likewise only copies (`MainWindow.Squad.cs:114-119`). **[CONFIRMED]** | — | — | — | — | — |
| T9 | **At startup** | **Nothing.** No warm-up call, no cache preload. The complete list of `Translate*` call sites (grep) is T1/T2, T5, T6, T7 — every one is user-initiated. **[CONFIRMED]** | — | — | — | — | — |

**Dedup before translating (T7):** `LiveDedup.Next(lines, SensitivityThreshold(), StabilityThreshold())`
(`MainWindow.Live.cs:201`) decides which lines are genuinely new; only those reach the translator. Lines are first
filtered by `TextMatching.LooksLikeText(l, minLetters)` (`MainWindow.Live.cs:194`). On top of that, the LRU cache
filters anything already translated this session (`CachingTranslator.cs:50-54`). Three independent filters stand
between an OCR frame and an HTTP request. **[CONFIRMED]**

**LIVE indicator on translation failure:** `AppendLinesToHistory` marks the pending rows `({Friendly(ex)})` and
**rethrows** (`MainWindow.Live.cs:281-282`); the loop's generic handler increments `consecutiveErrors`, shows
`Live hiccup (…) — retrying…` (`:238`), and **after 5 consecutive errors calls `StopLive()` then overwrites the
status with `Live stopped after repeated errors (…)`** (`:231-236`). `StopLive()` cancels the CTS and calls
`SetLiveUi(false)`, which hides the indicator (`MainWindow.Live.cs:142-151,163-173`). The `catch` that breaks the
loop is correctly filtered with `when (ct.IsCancellationRequested)` (`:227`) so a **timeout does not** leave a
zombie indicator. **[CONFIRMED]** — the OCE trap from `project-context.md` is correctly handled here.

## 2.7 LIVE worst-case request rate

**Stated assumptions** (all of them need runtime confirmation):

1. The speed slider is at 100 %, so `LiveIntervalMs(100) = 3000 − 2500 = 500 ms` (`MainWindow.Live.cs:354-355`).
   The shipped default is 92 % → **700 ms** (`SettingsService.cs:16`, `MainWindow.Live.cs:358`).
2. The loop is strictly sequential: capture → OCR → translate → `Task.Delay` — everything is `await`ed inside one
   `while` body (`MainWindow.Live.cs:179-245`), so **requests never overlap**. The per-tick wait is
   `Math.Max(150, interval − elapsed)` (`:242`), so a tick's true period is `max(500 ms, elapsed + 150 ms)`.
3. Every tick confirms new text (worst case; in practice a calm chat confirms nothing and issues **zero**
   requests).
4. Nothing is served from the LRU cache (worst case).

**Ceilings:**

| Scenario | Requests per tick | Ticks/min ceiling | Requests/min |
|---|---|---|---|
| Slider 100 %, 1 new line per tick, one language group | 1 | 120 | **≈ 120** |
| Slider 100 %, new lines in **both** the `ru` and the `auto` group (`MainWindow.Live.cs:313-322`) | 2 | 120 | **≈ 240** (in practice lower — the tick lengthens by the extra round-trip, so the 500 ms floor stops binding) |
| A tick whose batched request comes back with a mismatched line count, forcing per-line for K lines (`TranslationService.cs:86-108`) | 1 + K per group | — | a single tick can issue **2 + 2K** requests |
| Any of the above, with 429/5xx retries | ×3 per failing request (`TranslationService.cs:126,153`) | — | up to **×3**, plus 900 ms of added delay per retried request |
| Shipped default (92 %, 1 request/tick) | 1 | ≈ 85 | **≈ 85** |

**[INFERRED]** — arithmetic over confirmed constants. The real ceiling is bounded by wall-clock round-trip time
because the loop is sequential; 120/min is the *tick* ceiling, and the *request* ceiling equals it only when each
round-trip fits inside 350 ms.

For context, the read-once path (T6) issues **1–2 requests per user action**, and the Translator tab (T1/T2) issues
**1 request per Enter/click**, unless the text exceeds 1500 UTF-8 bytes (≈ 750 Cyrillic characters), in which case
it issues one sequential request per chunk (`TranslationService.cs:62-63`).

## 2.8 Rate limiting, circuit breaker, backoff, "retry in N minutes"

Answering the question directly:

- **There is no rate limiter.** No token bucket, no minimum interval between requests, no per-provider counter.
- **There is no circuit breaker.** Nothing anywhere records "Google is blocked" and skips subsequent calls. The next
  user action issues a request exactly as before.
- **There is no per-provider pause and no timer.** grep for `Timer` in the app finds only `_toastTimer`
  (`MainWindow.xaml.cs:56`) and the overlay's `_beat` (`CompactOverlay.xaml.cs:20`); neither has anything to do with
  translation.
- **The only backoff that exists** is inside a single `RequestAsync` call: `Task.Delay(300 * (attempt + 1))`, i.e.
  300 ms then 600 ms, for at most 2 retries (`TranslationService.cs:153`). It does not survive the call.
- **The only state that outlives a single call** is `bool rateLimited` inside one `TranslateLinesAsync` invocation
  (`TranslationService.cs:99,102,105`), which stops that one batch from hammering after the first
  `TranslationException`. It is a local variable; the next call starts clean.

**Where the "minutes" wording comes from:** `TranslationService.cs:145-146`, string
**`"Google is limiting translations right now — wait a minute and try again."`**, reachable **only** from
HTTP **429 on the third attempt**. Neighbouring strings say `"try again shortly"` (`:102, :105, :148, :176`) or
`"Please try again later."` (`:143`). **There is no timer behind any of them — the wording is advice to the user,
nothing in the app counts anything down or blocks anything.** **[CONFIRMED]**

**Consequence for P2's "lasts minutes, or never clears":** since the app holds no blocking state, **any persistence
of the symptom is external to this process** — a server-side / IP-level throttle, a proxy or filtering appliance, or
a DNS/HTTP intercept. The one in-app mechanism that *could* have made a failure sticky (the cache) is explicitly
prevented from caching failure placeholders (`CachingTranslator.cs:82-85`). **[INFERRED]** — strong, because the
absence of any rate-limit/circuit-breaker state is exhaustively confirmed, but the external cause itself is
unproven.

**Note on the message appearing "at launch":** nothing in the app translates at launch (T9). Therefore the reported
"appears at launch" **must** correspond to the user's first translation action after launch, or to a LIVE session
that was already running — **or the reported string is a different one** (e.g. E4's "may be temporarily blocked",
or E1's "Translation service error (HTTP 403)"). Establishing **which exact string** the users see is the highest-
value next step for P2 (see Q2.1).

## 2.9 (d) Open questions — Case 2

| ID | Question | Grade | What would settle it |
|---|---|---|---|
| Q2.1 | **Which of the ten exact strings E1–E10 do the affected users actually see?** They map to completely different causes: E2 = a real 429; E1 = a 403 (bot block, proxy, geo); E4 = an HTML interstitial/captcha; E5 = DNS/TLS/proxy; E6 = a 12 s timeout. | [UNKNOWN] — **blocking for P2** | A screenshot, or an instrumented build that logs the status code. Today **`TranslationService` logs nothing at all** (§2.4), so no existing log can answer this. |
| Q2.2 | Is `translate.googleapis.com` reachable at all from the affected machines, and what does it return? | [UNKNOWN] | `curl -A "<the UA at TranslationService.cs:42>" "https://translate.googleapis.com/translate_a/single?client=gtx&sl=ru&tl=en&dt=t&q=привет"` from the affected machine, and via the machine's proxy. |
| Q2.3 | Do the affected machines sit behind a proxy / TLS-inspecting appliance that `HttpClient.DefaultProxy` picks up silently? (No proxy is configured in code — §2.1.) | [UNKNOWN] | `netsh winhttp show proxy`, `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings"`, and whether the machine is Azure-AD/GPO-managed (the index already notes the owner's box is Azure-AD-joined). |
| Q2.4 | Does the fixed Chrome-120 UA with **no `Accept-Language` and no `Accept`** (`TranslationService.cs:41-42`) increase the odds of a Google bot-block relative to a plain .NET UA or a fuller browser header set? | [UNKNOWN] | External research on `translate_a/single` + `client=gtx` behaviour, dated; plus an A/B capture. |
| Q2.5 | Is the observed rate anywhere near the LIVE ceiling of §2.7 when the symptom appears — i.e. is the user running LIVE (85–240 req/min) or only typing (1 req per Enter)? | [UNKNOWN] | Ask the affected users what they were doing; a request counter in an instrumented build. |
| Q2.6 | Since all three `HttpClient`s are `static` and process-lifetime, is a stale/poisoned pooled connection or a stuck proxy resolution surviving the whole session, and does restarting the app clear the symptom? | [INFERRED possibility → needs test] | Ask whether restarting the app clears it, or only waiting does. `PooledConnectionLifetime` is left at its default (never set anywhere — grep). |
| Q2.7 | Is `TranslationService.cs:156-157` truly unreachable (§2.3)? | [INFERRED] | A unit test forcing each branch, or a careful re-read. Only matters so that investigators do not chase a string with no live producer. |
| Q2.8 | Does the 429 path ever actually fire, given that a Google block usually arrives as **403 + an HTML page** rather than 429? If so, E2 — the only string mentioning "a minute" — would be a red herring and the real user-visible strings are E1/E4. | [UNKNOWN] | Same capture as Q2.2. |

---

# Cross-checks against `project-context.md`

No contradiction was found between the code at commit `4759712` and `project-context.md`. Verified point by point:

| `project-context.md` claim | Verified at | Verdict |
|---|---|---|
| Pipeline is `CachingTranslator( key ? FallbackTranslator(DeepL, Google) : Google )` | `MainWindow.Translate.cs:226-233` | **matches** |
| Only successes cached; failure placeholders start with `(` | `CachingTranslator.cs:82-85` | **matches** |
| DeepL batch count mismatch throws, never pads | `DeepLTranslator.cs:47-53` | **matches** |
| Every OCE catch in the pipeline / LIVE loop filters with `when (ct.IsCancellationRequested)` | `FallbackTranslator.cs:26,38`; `DeepLTranslator.cs:80`; `MainWindow.Live.cs:227` | **matches**. The one unfiltered `catch (OperationCanceledException)` is `TranslationService.cs:104` — the per-line loop the owner has already accepted; it rethrows rather than swallowing, so it cannot mask a timeout as a success. |
| WPF `Clipboard` is banned; all writes go through `ClipboardService` | `MainWindow.xaml.cs:378-389`, `ClipboardService.cs:39` | **matches** (no `System.Windows.Clipboard` use anywhere) |
| Settings-restore re-entrancy guarded by `_restoringSettings` | `MainWindow.xaml.cs:35,157,201`; `MainWindow.Ocr.cs:435,368-380`; `MainWindow.Squad.cs:94` | **matches** |
| `SettingsVersion` + `Migrate` is the only way a changed default reaches existing users | `SettingsService.cs:102,121,133-159` | **matches** |
| OCR paths pick `IsProbablyRussian(body)` → `"ru"` else `"auto"` | `MainWindow.Live.cs:306-310` | **matches** |
| WGC latches after 3 consecutive failures; `SetMode` re-arms; `Mode` setter private | `ScreenCapture.cs:27-41,60-67` | **matches** |
| `UpdateService` trusts only `github.com` / `*.githubusercontent.com` | `UpdateService.cs:97-101` | **matches** |
| Single-file bundle deliberately uncompressed | `Build Portable EXE.bat:11-19`, `.github/workflows/release.yml:35` | **matches** |
| `MainTabs_SelectionChanged` filters `e.Source is TabControl` | `MainWindow.Phrasebook.cs:150` | **matches** |

One **addition** rather than a contradiction, for the record: `project-context.md` describes the three data files as
"EmbeddedResource **and** copied next to the exe as a first-run editable copy". That is accurate, but the versioned
**refresh** mechanism it enables is inert for `slang.json`, which ships without a `"version"` key (§1.3.1).

---

# Consolidated open questions

**16 open questions**: 8 for Case 1 (Q1.1–Q1.8) and 8 for Case 2 (Q2.1–Q2.8).

The two that gate everything else:

- **Q1.1** — is the missing time before the window appears, or after? Nothing else in Case 1 can be prioritised
  until this is answered, because the code path splits cleanly at that boundary (steps 1–19 vs step 24).
- **Q2.1** — which exact error string do users see? The ten producers map to five unrelated causes, and the app
  currently logs **none** of them.
