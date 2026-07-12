---
project_name: 'PWRU Helper (repo folder: PWRU-Traduction)'
user_name: 'Kizotis'
date: '2026-07-12'
sections_completed:
  ['technology_stack', 'language_rules', 'framework_rules', 'testing_rules', 'quality_rules', 'workflow_rules', 'anti_patterns']
status: 'complete'
optimized_for_llm: true
---

# Project Context for AI Agents

_This file contains critical rules and patterns that AI agents must follow when implementing code in this project. Focus on unobvious details that agents might otherwise miss._

---

## What This Project Is

**PWRU Helper** — a free Windows app (MIT, public repo `Kizotis/PWRU-Helper`) helping English/French speakers play **Perfect World Russia** (pwonline.ru). Five modules: Phrasebook (click → clipboard), Screen OCR (one-shot + LIVE loop over the game chat), Translator (EN↔RU with game-slang expansion), Compact in-game overlay, Squad builder (RU LFM messages). Distributed as portable exe + MSI. **Tiny footprint is a product requirement — nothing may lag the game.** The UI is intentionally English-only (a decision, not an oversight).

## Technology Stack & Versions

- **C# / WPF on .NET 8**, TFM `net8.0-windows10.0.19041.0` — the 19041 Windows target is REQUIRED (it provides the free on-device `Windows.Media.Ocr` engine). `Nullable` + `ImplicitUsings` enabled.
- **Code-behind, NO MVVM — deliberate.** Do not introduce MVVM, view-models, or binding frameworks.
- `System.Drawing.Common` 8.0.10 (GDI screen capture).
- Tests: **xUnit 2.9.2**, Test SDK 17.11.1, runner 2.8.2 — `dotnet test tests/PWRUHelper.Tests` (142 tests, headless-safe, incl. STA render tests).
- Installer: **WiX v5.0.2 exactly** (`installer/Product.wxs`); WiX v6+ has a paid OSMF EULA that blocks CI builds.
- **No `.sln` at repo root** — adding one breaks `dotnet run` and both `Build *.bat` scripts.
- `Data/phrases.json`, `Data/slang.json`, `Data/squad.json`: EmbeddedResource **and** copied next to the exe as a first-run editable copy; the files are tracked source.

### Code Layout

- `MainWindow.xaml.cs` = core (fields, ctor, lifecycle, hotkeys, shared helpers). Domain logic in partials: `MainWindow.Phrasebook.cs` / `.Squad.cs` / `.Translate.cs` / `.Ocr.cs` / `.Live.cs` / `.Compact.cs` / `.Update.cs`.
- `Services/` = pure-ish, unit-testable classes (translation pipeline, capture backends, OCR, dedup, slang, settings, logging, update).
- `OcrResultItem.cs` lives at repo ROOT with namespace `PWRUHelper` (NOT `.Models`) — the render test depends on this; do not move it.
- Windows: `CompactOverlay`, `SelectionOverlay`. Tabs order: Phrasebook(0) · Squad(1) · Translator(2) · Screen OCR(3) · About(4).
- Theme: `Theme.xaml`, pwonline.ru dark-navy palette (bg `#071c2f`, panel `#0e2c47`, red `#a01116`, teal `#278eb4`, gold `#ffdc50`, text `#f4eddd`). The dark ToolTip style in Theme.xaml is load-bearing.

## Critical Implementation Rules

### Language-Specific Rules (each of these cost a real shipped bug)

- **NEVER use the WPF `Clipboard` class.** All clipboard writes go through `Services/ClipboardService` (raw Win32 on `Task.Run`). Reintroducing WPF Clipboard cost 3 releases of bugfixing.
- **The OCE trap:** an `HttpClient` timeout throws `TaskCanceledException` (an `OperationCanceledException` subclass) with the token NOT cancelled. Every OCE catch in the translation pipeline and LIVE loop must filter with `when (ct.IsCancellationRequested)` — otherwise timeouts masquerade as user cancels (this silently disabled the DeepL→Google fallback and left a zombie LIVE indicator).
- **WinRT interop:** `UnmanagedType.HString` marshalling was REMOVED in .NET 5+ (throws `MarshalDirectiveException`). `WgcCapture` creates HSTRINGs manually via `WindowsCreateString`/`WindowsDeleteString`; `MarshalInterface<T>.FromAbi` is fine.
- Changed DEFAULT setting values reach EXISTING users ONLY via `SettingsVersion` + `AppSettings.Migrate(s)` (run in `Load`, persisted immediately). A new property initializer alone does nothing for users with a saved settings.json.

### Framework-Specific (WPF) Rules

- **`Run.Text` binds TwoWay BY DEFAULT** — on a get-only property it throws once per rendered item. Use `Mode=OneWay` explicitly. Any new `Run.Text`-bound feed template must be added to the STA `TemplateRenderTests`.
- **Settings-restore re-entrancy:** `ApplySettings` must never let a control's change-handler write settings mid-restore. The `_restoringSettings` flag makes handlers bail early; UI side-effects are applied explicitly (e.g. `UpdateOcrFilterUi`). Every NEW persisted control must follow this pattern.
- `MainTabs_SelectionChanged` must filter `e.Source is TabControl`. Use `MessageBox.Show(this, …)` so dialogs get an owner. Tag-driven combos: use the existing `SelectTag`/`SelectedTag` helpers — don't re-roll the loop.
- `CompactOverlay` resizing = `WM_NCHITTEST` hook returning HT* codes (native resize); geometry lives in unit-tested `internal static ResizeHitTest`.

### Translation Pipeline Rules

- Pipeline shape (built in `BuildTranslator()`, rebuilt live on key save): `CachingTranslator( key ? FallbackTranslator(DeepLTranslator, TranslationService) : TranslationService )`.
- Only SUCCESSES are cached (failure placeholders start with `(`). On DeepL batch count mismatch, throw `TranslationException` — **never pad the result** (padding once bypassed the fallback and poisoned the cache).
- Slang: bodies are `SlangGlossary.Expand`-ed BEFORE translation; one shared position-aware matcher feeds both `Decode` (🔑 line) and `Expand`. In `Data/slang.json`, only add a `full` field when SURE of the Russian expansion.
- OCR paths pick source per message: `IsProbablyRussian(body)` → `"ru"` else `"auto"`; displayed original + 🔑 line stay raw (never show expanded text as the original).

### Testing Rules

- `dotnet test tests/PWRUHelper.Tests` — must stay headless-safe (CI runs it on every PR).
- The app csproj excludes `tests\**` via `DefaultItemExcludes` and grants `InternalsVisibleTo PWRUHelper.Tests` — keep both intact.
- `TemplateRenderTests` (STA) renders the REAL ItemTemplates via `ContentControl` — an `ItemsControl` defers container generation headless and renders nothing. Always verify list rendering with a real injected item.

### Code Quality & Style Rules

- No linter/`.editorconfig` — match the surrounding code: short "why" comments explaining constraints, PascalCase files, one class per concern.
- **Rule of Three before abstraction.** Prefer boring, proven approaches; developer productivity over architectural purity.
- Keep `Services/` free of UI dependencies so they stay unit-testable.

### Development Workflow Rules

- `main` = single source of truth. All changes land via PR (branch names: `feature/…`, `fix/…`, `docs/…`, `release/vX.Y.Z`). No direct pushes to main, no force-push, no self-merging.
- **Release checklist:** (1) bump `<Version>` in `PWRUHelper.csproj` (the in-app update check misfires otherwise) · (2) sync the hardcoded `VERSION=` in `Build MSI Installer.bat` · (3) land on main via PR · (4) annotated tag `vX.Y.Z` → `.github/workflows/release.yml` builds exe + MSI and attaches BOTH · (5) verify `gh release view vX.Y.Z --json name,assets` shows both artifacts.
- `installer/Product.wxs` UpgradeCode is FIXED forever (MajorUpgrade replaces old installs); version needs the 4th part (`X.Y.Z.0`).
- Keep branches `backup-clearer-v02base`, `release-v0.4.0`, `release-v0.7.1` (the owner's markers).

### Critical Don't-Miss Rules

- **Deliberate non-features — do NOT propose or implement:** i18n/.resx (English UI is a choice), MVVM, multi-`q=` Google batching, WGC session caching (until WGC proves useful on a real fullscreen game).
- `UpdateService` only trusts download URLs on `github.com`/`githubusercontent.com` — keep that allowlist.
- WGC capture failures latch after 3 consecutive misses (log once, GDI for the session); `SetMode` re-arms. `ScreenCapture.Mode` setter is private — use `SetMode(string)`.
- Accepted tradeoffs — don't re-flag: a Google result cached during a DeepL blip persists until restart/key-resave; MSI update quits before the UAC outcome; WGC pays a one-shot D3D device cost per capture.
- Code signing is pending (SignPath guide + drafted CI steps in `packaging/signpath-signing.md`, gated on `secrets.SIGNPATH_API_TOKEN`); winget submission is drafted under `packaging/winget/`, gated on `WINGET_TOKEN`.

---

## Usage Guidelines

**For AI Agents:**

- Read this file before implementing any code
- Follow ALL rules exactly as documented
- When in doubt, prefer the more restrictive option
- Update this file if new patterns emerge

**For Humans:**

- Keep this file lean and focused on agent needs
- Update when the stack, pipeline shape, or release process changes
- Remove rules that become obvious over time

Last Updated: 2026-07-12
