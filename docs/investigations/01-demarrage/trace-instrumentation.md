# 01 — P1 startup: in-process trace instrumentation (PLAN — not applied)

_Phase 1 · author: Amelia (BMAD Senior Software Engineer) · baseline commit `4759712` (main, v0.14.0) · written 2026-09-06._

> **Status: PLAN ONLY. Nothing in this document has been applied to the working tree.** Every code fragment below is a *proposal*, written as a diff sketch so it can be reviewed before anyone types it. Per the mission's ground rule 2, it lands on a dedicated branch (`fix/startup-trace` or similar) **only after the owner's explicit go**, and it ships to users only if the owner decides it should — the design below assumes it does not.

---

## 1. What this instrument is for, and what it cannot do

`hypotheses-matrice.md` splits the 6–10 s into two halves at its root:

- **`t_pre`** — from the user's click to `CreateProcess` completing (Explorer/ShellExecute, SmartScreen, Defender scan-on-execute, cloud/BAFS hold, file hydration).
- **`t_in`** — from process start to the first painted frame (host, native-lib extraction, CoreCLR, WPF, `App.OnStartup`, `MainWindow`, first layout).

**This trace measures `t_in` and nothing else.** It is deliberately narrow: it produces a millisecond-accurate breakdown of everything the process does to itself, so that the top-ranked hypotheses (D1, D2, S1, F1 — all of which live in `t_pre`) can be *confirmed by exclusion* rather than argued about. If the trace says the whole in-process path is 1.2 s on a machine where the user waits 8 s, that is a positive result: it proves 6.8 s were spent before any of our code ran, and it kills every code-level theory in one measurement.

**What it cannot do** (stated here so nobody expects it to):

- It cannot see anything before the first managed code in our assembly runs — so it cannot time the loader, the AV scan, the SmartScreen round-trip, the single-file host's own work, or the native-library extraction. Those are `t_pre`/host territory; §7 covers what does see them.
- It cannot, by itself, tell you *why* an in-process step is slow — only that it is.
- The very first mark is not "process start". The gap between process start and that first mark is itself the measurement of host + runtime init (§3.1), which is exactly what we want, but it is an *inferred* interval, not a timed one.

---

## 2. Where the process-start timestamp comes from

Two candidates, and the choice matters:

| Option | What it gives | Verdict |
|---|---|---|
| `Environment.TickCount64` sampled at the first managed mark | A monotonic millisecond counter since boot. Cheap and monotonic, but it has **no relationship to when the process was created** — it can only measure intervals *between* our own marks. | **Use it for nothing.** `Stopwatch` is better for the same job. |
| `Process.GetCurrentProcess().StartTime` | The **kernel's** process-creation time, as a `DateTime`, at 100 ns resolution. Subtracting it from `DateTime.UtcNow` at the first mark yields **host + runtime init elapsed**, i.e. the single most valuable number this instrument produces. | **Use this, once, at the very first mark.** |
| `Stopwatch.GetTimestamp()` | High-resolution monotonic ticks, immune to wall-clock adjustment. | **Use this for every subsequent mark.** All deltas are computed against the first timestamp. |

So the design is: **one absolute anchor (`StartTime` → wall-clock delta) and a monotonic ruler (`Stopwatch`) for everything after it.** Mixing them is safe because the anchor is only used once.

Caveats to record in the output header, not to hide:

- `Process.GetCurrentProcess()` is not free (it opens a handle and reads process times) — a few hundred microseconds. Because it is taken *once*, inside the trace-enabled path only, that cost is acceptable and is itself reported so it can be subtracted.
- `StartTime` is local-time-based; compare it against `DateTime.Now`, or convert both to UTC. Get this wrong and you get a DST-sized error, which is exactly the kind of bug that discredits a measurement.
- On a single-file app, process start is when the **apphost** starts, not when CoreCLR is ready. That is the point: the difference is the number we are hunting.

---

## 3. Insertion points

All `file:line` references are against commit `4759712`. Line numbers shift as soon as the first insertion is made — the table is ordered so a top-down application is unambiguous, and each row names an anchor *string* as well as a line, so the patch can be re-derived if the file moves.

### 3.1 Earliest possible managed code — a module initializer

There is **no hand-written `Main`** (`00-annexe-demarrage-et-reseau.md` §1.0 — WPF generates it). The earliest hook we can own in the app assembly is a `[ModuleInitializer]`, which the runtime executes before any other code in the module. That is where the anchor is taken.

| # | Where | Anchor | Mark |
|---|---|---|---|
| 1 | **new file** `Services/StartupTrace.cs` | — | `[ModuleInitializer] Init()` → decide opt-in, take `StartTime`, emit `host+runtime` and `mark 0` |

### 3.2 `App.xaml.cs`

| # | `file:line` (at `4759712`) | Anchor | Mark |
|---|---|---|---|
| 2 | `App.xaml.cs:15` (top of `OnStartup`, before the mutex) | `_instanceMutex = new Mutex(` | `app.onstartup.enter` |
| 3 | `App.xaml.cs:17→18` (after the mutex, before the `isNew` test) | `out bool isNew);` | `app.mutex` |
| 4 | `App.xaml.cs:32` (immediately **before** `Logging.Info`) | `Logging.Info($"--- PWRU Helper` | `app.beforeFirstLog` |
| 5 | `App.xaml.cs:32→33` (immediately **after** it) | same line, after | `app.afterFirstLog` — **isolates the process's first disk write to Roaming `%APPDATA%` (A1)** |
| 6 | `App.xaml.cs:34` (before `base.OnStartup(e)`) | `base.OnStartup(e);` | `app.beforeBaseOnStartup` — everything after this is WPF creating `MainWindow` |

### 3.3 `MainWindow.xaml.cs` — field initializers

Field initializers run in declaration order, before the constructor body, and cannot be wrapped. They are **bracketed** instead, by inserting two dummy fields that mark as a side effect of their own initialization. This is the only way to time them without restructuring the class.

| # | `file:line` | Anchor | Mark |
|---|---|---|---|
| 7 | before `MainWindow.xaml.cs:21` | `private readonly List<Phrase> _allPhrases = new();` | `private readonly int _t_fields0 = StartupTrace.Mark("fields.begin");` |
| 8 | before `MainWindow.xaml.cs:50` | `private readonly AppSettings _settings = SettingsService.Load();` | `private readonly int _t_set0 = StartupTrace.Mark("settings.load.begin");` |
| 9 | after `MainWindow.xaml.cs:50` | same | `private readonly int _t_set1 = StartupTrace.Mark("settings.load.end");` — **isolates `SettingsService.Load` incl. the one-shot migration write (A2)** |
| 10 | after `MainWindow.xaml.cs:70` (`_dedup`, the last initializer) | `private LiveDedup _dedup = new();` | `private readonly int _t_fields1 = StartupTrace.Mark("fields.end");` |

> Two properties of this trick to be aware of: (a) the dummy fields must be `readonly int` assigned from `Mark`, so the compiler cannot elide them and no nullable warning is produced; (b) `_dataRefreshNotes` is declared at line 121, *after* the constructor in source order but still a field initializer — C# runs initializers in **declaration order**, so `_t_fields1` at line ~70 does **not** actually close the bracket. **The `fields.end` mark must go after `MainWindow.xaml.cs:121`**, not after line 70. This is exactly the kind of detail that silently produces a wrong number, so it is called out rather than left to the implementer.

### 3.4 `MainWindow.xaml.cs` — constructor body (`:80-104`)

| # | `file:line` | Anchor | Mark |
|---|---|---|---|
| 11 | `:81` (first statement of the body) | `{` after `public MainWindow()` | `ctor.enter` |
| 12 | `:82→83` | `_writeTranslator = BuildTranslator();` | `ctor.buildTranslator` |
| 13 | `:83→84` | `InitializeComponent();` | `ctor.initializeComponent` — **the 206–233 ms [REPORTED] item; the single most valuable in-process number after the runtime floor** |
| 14 | `:89→90` | `PopulateLanguageCombos();` | `ctor.populateCombos` |
| 15 | `:90→91` | `LoadPhrases();` | `ctor.loadPhrases` |
| 16 | `:91→92` | `LoadSlang();` | `ctor.loadSlang` |
| 17 | `:92→93` | `LoadSquad();` | `ctor.loadSquad` |
| 18 | `:93→94` | `BuildSquadTab();` | `ctor.buildSquadTab` |
| 19 | `:99→100` | `ApplySettings();` | `ctor.applySettings` |
| 20 | `:103→104` | `Loaded += OnWindowLoaded;` | `ctor.exit` |

Marks 15–17 together answer whether the data-file probes cost anything (A3, F4 — the MSI denied-write path). Mark 19 answers whether `ApplySettings`, which sets `MainTabs.SelectedIndex` and therefore chooses what the first layout builds, is itself expensive (it is not — 4 ms **[REPORTED]** — but the layout it *causes* is not measured here; marks 22–24 catch that).

### 3.5 `MainWindow.xaml.cs` — window realization

| # | `file:line` | Anchor | Mark |
|---|---|---|---|
| 21 | `:431` (first line of `OnSourceInitialized`, after `base.OnSourceInitialized(e)`) | `base.OnSourceInitialized(e);` | `sourceInitialized.enter` + one-off environment line: DPI, `RenderCapability.Tier >> 16`, monitor count (**settles R4/R6 for free**) |
| 22 | `:449` (after the five `Reg(...)` calls, before the `failed.Count` test) | `Reg(HK_READ, 0x52, "Ctrl+Alt+R");` | `sourceInitialized.hotkeys` (A4) |
| 23 | `:458` (end of `OnSourceInitialized`) | closing `}` | `sourceInitialized.exit`; **also subscribe the one-shot `CompositionTarget.Rendering` handler here** — this is the last point that is guaranteed to run before the first frame |
| 24 | ctor, next to `:103` | `Loaded += OnWindowLoaded;` | also `ContentRendered += …` → mark `contentRendered`, and keep `Loaded` → mark `loaded` **first in the handler**, i.e. at `:132`, before the `await` |
| 25 | one-shot `CompositionTarget.Rendering` callback | — | `firstRender` — **unsubscribe immediately**, then **flush**. This is the closest managed proxy for "the user can see the window". |

`ContentRendered` and the first `Rendering` tick are both recorded because they are not the same event and the difference is informative: `ContentRendered` fires after the content has been rendered *at least once*, `Rendering` fires per frame on the compositor's cadence. Recording both costs nothing and removes an argument.

### 3.6 Post-paint marks (cheap, and they close out the *other* startup issue)

| # | `file:line` | Mark |
|---|---|---|
| 26 | `MainWindow.xaml.cs:138→139` (after `await Task.Run(() => _ocr.IsAvailable);`) | `loaded.ocrProbe` — the 26–38 ms **[REPORTED]** WinRT engine build, now off the UI thread |
| 27 | `MainWindow.xaml.cs:145` around `await CheckForUpdatesAsync();` | `loaded.updateCheckBegin` / `loaded.updateCheckEnd` — **not a P1 item under owner answer (a), but it is the entire evidence base for the "window appears then freezes" report, and it is two free marks** |

---

## 4. The trace class — diff sketch (NOT applied)

```diff
+++ b/Services/StartupTrace.cs
+using System.Diagnostics;
+using System.Runtime.CompilerServices;
+using System.Text;
+
+namespace PWRUHelper.Services;
+
+/// <summary>
+/// Opt-in, high-resolution startup timeline. OFF unless PWRUHELPER_TRACE_STARTUP=1 is set
+/// or the process was launched with --trace-startup: when off, every Mark() is a single
+/// static bool test and returns immediately, so this costs nothing on a normal launch.
+///
+/// Nothing touches the disk until Flush(): marks go into a preallocated array, so the
+/// instrument cannot become the thing it is measuring. Deliberately does NOT reuse Logging:
+/// Logging opens, appends and closes the file on EVERY line (Logging.cs:81-87), which on the
+/// startup path is precisely one of the costs under investigation.
+/// </summary>
+internal static class StartupTrace
+{
+    private const int Capacity = 64;
+    private static readonly long[] Ticks = new long[Capacity];
+    private static readonly string[] Labels = new string[Capacity];
+    private static int _count;
+    private static long _t0;
+    private static bool _enabled;
+    private static double _hostInitMs;      // process start -> first managed mark
+    private static DateTime _startTime;
+    private static readonly StringBuilder Notes = new();
+
+    [ModuleInitializer]
+    internal static void Init()
+    {
+        try
+        {
+            _enabled = Environment.GetEnvironmentVariable("PWRUHELPER_TRACE_STARTUP") == "1"
+                       || Array.IndexOf(Environment.GetCommandLineArgs(), "--trace-startup") > 0;
+            if (!_enabled) return;
+
+            // ONE absolute anchor: the kernel's process-creation time. Everything after this
+            // is measured with Stopwatch (monotonic, immune to a clock adjustment mid-launch).
+            using var p = Process.GetCurrentProcess();
+            _startTime = p.StartTime;
+            _hostInitMs = (DateTime.Now - _startTime).TotalMilliseconds;
+            _t0 = Stopwatch.GetTimestamp();
+            Mark("managed.firstCode");
+        }
+        catch { _enabled = false; }   // an instrument must never break the app it measures
+    }
+
+    /// <summary>Records a timestamp. Returns int so it can be used as a field initializer
+    /// (the only way to bracket MainWindow's field inits without restructuring the class).</summary>
+    internal static int Mark(string label)
+    {
+        if (!_enabled) return 0;
+        int i = _count;
+        if (i >= Capacity) return 0;
+        Ticks[i] = Stopwatch.GetTimestamp();
+        Labels[i] = label;
+        _count = i + 1;
+        return i;
+    }
+
+    internal static void Note(string line) { if (_enabled) Notes.AppendLine(line); }
+
+    /// <summary>Single write, at the end. Beside the normal log, never inside it.</summary>
+    internal static void Flush()
+    {
+        if (!_enabled) return;
+        _enabled = false;                       // flush exactly once
+        try
+        {
+            var dir = Path.Combine(
+                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
+                "PWRUHelper", "logs");
+            Directory.CreateDirectory(dir);
+
+            var sb = new StringBuilder(4096);
+            sb.AppendLine($"=== startup trace {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
+                          $"v{UpdateService.CurrentVersion.ToString(3)} pid {Environment.ProcessId} ===");
+            sb.AppendLine($"process start        {_startTime:HH:mm:ss.fff}");
+            sb.AppendLine($"host+runtime init    {_hostInitMs,8:F1} ms   " +
+                           "(process start -> first managed code; NOT measurable from inside, inferred)");
+            sb.AppendLine($"exe                  {Environment.ProcessPath}");
+            sb.AppendLine($"base dir             {AppContext.BaseDirectory}");
+            sb.AppendLine($"bundle extract dir   {Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") ?? "(default %TEMP%\\.net)"}");
+            sb.Append(Notes);
+            sb.AppendLine("  delta      total   step");
+            double prev = 0;
+            for (int i = 0; i < _count; i++)
+            {
+                double ms = (Ticks[i] - _t0) * 1000.0 / Stopwatch.Frequency;
+                sb.AppendLine($"{ms - prev,8:F1}  {ms + _hostInitMs,8:F1}   {Labels[i]}");
+                prev = ms;
+            }
+            sb.AppendLine($"TOTAL to first frame {_hostInitMs + prev,8:F1} ms");
+            File.AppendAllText(Path.Combine(dir, "startup-trace.log"), sb.ToString());
+        }
+        catch { /* an instrument must never break the app it measures */ }
+    }
+}
```

Call-site sketches:

```diff
--- a/App.xaml.cs
+++ b/App.xaml.cs
     protected override void OnStartup(StartupEventArgs e)
     {
+        StartupTrace.Mark("app.onstartup.enter");
         _instanceMutex = new Mutex(initiallyOwned: true, "PWRUHelper.SingleInstance", out bool isNew);
+        StartupTrace.Mark("app.mutex");
         if (!isNew)
@@
+        StartupTrace.Mark("app.beforeFirstLog");
         Logging.Info($"--- PWRU Helper v{UpdateService.CurrentVersion.ToString(3)} starting ---");
+        StartupTrace.Mark("app.afterFirstLog");
 
+        StartupTrace.Mark("app.beforeBaseOnStartup");
         base.OnStartup(e);
     }
```

```diff
--- a/MainWindow.xaml.cs
+++ b/MainWindow.xaml.cs
+    private readonly int _t_fields0 = StartupTrace.Mark("fields.begin");
     private readonly List<Phrase> _allPhrases = new();
@@
+    private readonly int _t_set0 = StartupTrace.Mark("settings.load.begin");
     private readonly AppSettings _settings = SettingsService.Load();
+    private readonly int _t_set1 = StartupTrace.Mark("settings.load.end");
@@  (after the LAST field initializer in declaration order — line 121, _dataRefreshNotes)
+    private readonly int _t_fields1 = StartupTrace.Mark("fields.end");
@@
     public MainWindow()
     {
+        StartupTrace.Mark("ctor.enter");
         _writeTranslator = BuildTranslator();
+        StartupTrace.Mark("ctor.buildTranslator");
         InitializeComponent();
+        StartupTrace.Mark("ctor.initializeComponent");
@@
         LoadPhrases();
+        StartupTrace.Mark("ctor.loadPhrases");
         LoadSlang();
+        StartupTrace.Mark("ctor.loadSlang");
         LoadSquad();
+        StartupTrace.Mark("ctor.loadSquad");
         BuildSquadTab();
+        StartupTrace.Mark("ctor.buildSquadTab");
         ApplySettings();
+        StartupTrace.Mark("ctor.applySettings");
         FromCombo.SelectionChanged += FromCombo_SelectionChanged;
         Loaded += OnWindowLoaded;
+        ContentRendered += (_, _) => StartupTrace.Mark("contentRendered");
+        StartupTrace.Mark("ctor.exit");
     }
@@
     protected override void OnSourceInitialized(EventArgs e)
     {
         base.OnSourceInitialized(e);
+        StartupTrace.Mark("sourceInitialized.enter");
+        StartupTrace.Note($"render tier         {RenderCapability.Tier >> 16}");
+        StartupTrace.Note($"dpi scale           {VisualTreeHelper.GetDpi(this).DpiScaleX:F2}");
+        StartupTrace.Note($"restored tab        {_settings.LastTab}");
@@
         Reg(HK_READ, 0x52, "Ctrl+Alt+R");       // R
+        StartupTrace.Mark("sourceInitialized.hotkeys");
@@   (end of the method)
+        // One-shot: the first composition frame is the closest managed proxy for
+        // "the user can now see the window". Unsubscribe immediately, then write the file.
+        EventHandler? onRender = null;
+        onRender = (_, _) =>
+        {
+            CompositionTarget.Rendering -= onRender;
+            StartupTrace.Mark("firstRender");
+            StartupTrace.Flush();
+        };
+        CompositionTarget.Rendering += onRender;
     }
```

---

## 5. Cost, and why it is negligible

- **Disabled (every normal launch):** `Mark` is `if (!_enabled) return 0;` over a static `bool`, and there are ~25 call sites on a path that already costs ~1200 ms. The only unconditional work is the `[ModuleInitializer]` reading one environment variable and the command line. **Unmeasurable.**
- **Enabled:** one `Process.GetCurrentProcess()` (~0.1–0.5 ms, taken once and reported so it can be subtracted), then two array stores per mark. **No I/O whatsoever until `Flush()`**, which happens on the first rendered frame — i.e. after the measurement window has closed. That is the whole reason this does not reuse `Logging`: `Logging.Write` does `Directory.CreateDirectory` + open + append + close **per line** (`Logging.cs:81-87`), which is one of the things under investigation (hypothesis A1). Measuring A1 with A1 would be circular.
- **Capacity is fixed at 64** and `Mark` silently drops overflow. A fixed array cannot allocate, cannot trigger a GC mid-measurement, and cannot throw.
- **Every entry point is inside `try/catch`.** An instrument that can crash the app is worse than no instrument, and this codebase already holds that line (`Logging.cs:89`, `SettingsService.cs:126`).

## 6. Opt-in, and why it must never ship as default noise

Two independent triggers, both checked in the module initializer:

- environment variable `PWRUHELPER_TRACE_STARTUP=1` — the one to give a remote user, because it survives a normal double-click (`setx`, or a one-line `.cmd` next to the exe);
- command-line argument `--trace-startup` — the one to use locally.

`StartupEventArgs.Args` is not available this early, so the argument is read via `Environment.GetCommandLineArgs()`. The app has no other command-line handling, so there is no parser to disturb.

**Off by default, and silent when off.** It writes `startup-trace.log` — a *separate* file beside `log.txt`, never into it, so the About tab's "Copy error report" (`Logging.ReadRecent`, `Logging.cs:107-127` → `MainWindow.xaml.cs:312-322`) is unaffected and users do not send timing noise to Discord. `startup-trace.log` is appended to, not rotated: it only grows on runs where tracing was explicitly requested.

**Test-suite obligation** (project rule): `TestLogRedirect.cs` redirects `Logging` to a temp directory so the suite never writes to the developer's real `%APPDATA%`. `StartupTrace` resolves `%APPDATA%` independently, so it would bypass that guard — **but only when the env var is set**, which the suite never sets. The safe belt-and-braces version adds an `internal static string? DirectoryOverride` mirroring `Logging.cs:31-39`. Whoever implements this must not forget it; it is exactly the trap `wpf-xaml-load-clobbers-settings` records for the settings file.

## 7. How this complements the pre-process measurement — and what only ETW/WPR can give

The trace and the external script measure **disjoint** intervals, and neither is a substitute for the other:

| Interval | Instrument | Output |
|---|---|---|
| click → `CreateProcess` | Amelia-QD's script (M1/M2 in `hypotheses-matrice.md` §4.1) | `t_pre` |
| process start → first managed code | **this trace**, `host+runtime init` line (inferred from `Process.StartTime`) | includes the single-file host and native-lib extraction, **as one opaque block** |
| first managed code → first frame | **this trace**, the mark table | the full code-side breakdown |
| click → window handle | the script | the total the user actually experiences |

The script's `t_in` and the trace's `TOTAL to first frame` should agree within tens of ms. **If they disagree, believe the script** — it measures from outside and cannot be fooled by a mark placed in the wrong scope — and treat the disagreement as a defect in the instrumentation.

**What only ETW/WPR (or Defender's own recorder) can give, and this trace never will:**

- **The `t_pre` breakdown itself.** `wpr -start GeneralProfile -start FileIO -start DiskIO` (or a custom profile enabling `Microsoft-Windows-Kernel-Process` and `Microsoft-Windows-Kernel-File`) around a cold launch shows the process-create event with a kernel timestamp, every file read of the 178 MB image, and — critically — **which other process was doing work in that window** (`MsMpEng.exe`, `smartscreen.exe`, `OneDrive.exe`). Nothing inside our process can see any of that.
- **Attribution of scan time to a named file.** `New-MpPerformanceRecording` → `Get-MpPerformanceReport` is purpose-built for this and reports scan durations per file, per process and per extension. It is the tool that turns D1/D2 from "the leading theory" into a finding; the in-process trace can only ever support them by exclusion.
- **The native-library extraction, itemised.** The trace lumps it into `host+runtime init`. WPR's file-I/O view shows each write to `%TEMP%\.net\PWRUHelper\<hash>\` with sizes and timings — settling the **[UNKNOWN]** file count and cost in hypothesis E1.
- **Loader and page-fault detail** (hard faults reading the image), which is how F1 (OneDrive hydration) and O1 (cold cache) become visible rather than inferred.

Recommended order of operations, cheapest first: **(1)** the script's `t_pre`/`t_in` split (M1, M2) — it may end the investigation on its own; **(2)** this trace, on one slow machine, if and only if `t_in` is the large half; **(3)** `Get-MpPerformanceReport`, on one slow machine, if `t_pre` is the large half; **(4)** a full WPR trace only if (1)–(3) leave the pre-process time unexplained. Steps (1)–(3) need no code change at all; only (2) requires the owner's go on this document.

## 8. Risks and how they are contained

| Risk | Containment |
|---|---|
| The instrument perturbs what it measures | No I/O before the flush; fixed-size array; ~25 predictable branches on a 1200 ms path. |
| It crashes the app on a user's machine | Every public entry point wrapped in `try/catch`; disabled state is the default and is a single `bool` test. |
| It leaks into a release and writes files for everyone | Off unless explicitly opted in; a `PublishFlagsTests`-style assertion could pin "no default-on trace" the same way the compression flag is pinned (`PublishFlagsTests.cs:44-60`). |
| Marks placed in the wrong scope give confident wrong numbers | The field-initializer bracket in §3.3 has a documented ordering trap (`_dataRefreshNotes` at line 121). Cross-check against the script's external `t_in`: they must agree. |
| It writes into the developer's real `%APPDATA%` during `dotnet test` | Mirror `Logging.DirectoryOverride` (`Logging.cs:31-39`) and set it from the test assembly's module initializer. |
| Line numbers drift | Anchor strings are given alongside every line number. |

---

_Applied only on the owner's explicit go, on a branch, never merged to `main` without a second decision. Companion documents: `hypotheses-matrice.md` (what the numbers mean), `mesures-protocole.md` (the external half), `recommandations.md` (what to do with the answer)._
