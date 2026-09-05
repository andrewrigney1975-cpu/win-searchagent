# Delve

A Windows 11, Fluent-styled search agent that sits in the notification area and gives instant,
Spotlight/Alfred-style access to [Docket](https://github.com/andrewrigney1975-cpu)'s
"Search Everywhere" file index — without Docket needing to know Delve exists.

## Scope

- Delve is a **companion** to Docket, not a replacement for it. It never builds, owns, or
  writes to a file index of its own — it only reads the SQLite index Docket already
  maintains, and only while that index has at least one entry.
- **v1 goals:** a fast native popup search bar over Docket's existing index, correct result
  actions (open via default handler, reveal in Explorer), and unobtrusive tray presence.
- **Explicitly out of scope for v1:** writing to the index, starting/stopping/reindexing
  Docket from Delve, content search (filename only, matching what Docket itself indexes),
  and support for a Docket index schema other than the one described below.
- **Zero changes to Docket.** Delve was built to require no modification whatsoever to
  Docket's source, settings, or security posture — see [Solution](#solution) for why.

## Features

- **Availability-aware.** Delve polls whether `docket.exe` is running and whether its search
  index actually has entries. Only then does it arm the global hotkey; otherwise the hotkey is
  unregistered and the tray icon switches to a muted/gray state with an explanatory tooltip.
  This is the "checks if the search index service is running" behavior — Docket's index isn't
  a separate Windows service, it's a feature of the running Docket process, so that's what
  Delve actually checks.
- **`Shift+Win+D` global hotkey** opens a borderless, centered, always-on-top, Mica-backed
  search bar above all other windows — a macOS Spotlight / PowerToys Run–style popup, not a
  normal application window. (`Ctrl+Win+D` was the original choice but is reserved by Windows
  itself for "create new virtual desktop" — confirmed by hands-on testing, where the popup
  never appeared because the shell consumed the hotkey first.)
- **Live ranked results** in a flyout below the search box as you type (debounced), using the
  same substring-then-fuzzy ranking algorithm Docket's own Search Everywhere uses, so the feel
  is consistent between the two apps.
- **Click** a result to open it with its registered default handler (same as double-clicking
  in Explorer). **Ctrl+click** reveals it in Explorer instead, selected.
- **Escape or click-away** closes the popup; the window is reused (hidden, not destroyed)
  between hotkey presses so re-opening is instant.
- **No taskbar button, no Alt+Tab entry.** The popup is a Spotlight/PowerToys Run-style
  overlay, not a regular app window — the tray icon is the only persistent, always-visible way
  to reach Delve.
- **Tray icon** with a context menu: **Open** (show the search popup), **Hide** (dismiss it if
  open), **Quit** (fully exit — unregisters the hotkey and disposes the tray icon).

## Solution

### Why Delve doesn't call into Docket

Docket already ships one external integration — a Chrome/Edge Native Messaging host — but it's
deliberately narrow (launched by the browser over stdio, scoped to one extension ID, answers
only "does this exact folder name exist"). It's not a general query API, and extending it (or
adding a new local IPC surface) would mean changing Docket's "no local server, minimal attack
surface" design specifically to serve Delve.

Instead, Delve reads Docket's index **directly and read-only**: Docket's SQLite database
(`%LocalAppData%\FileExplorerApp\search-index.db`) runs in WAL mode, which explicitly supports
concurrent external readers while Docket's own process holds the writer connection. This has
been verified against Docket's real, live, multi-million-row index while Docket was actively
indexing — Delve's read-only connection (`Mode=ReadOnly` in the SQLite connection string) never
creates, locks, or writes to the file.

Delve vendors a byte-identical copy of Docket's ~60-line `FuzzyMatcher` ranking algorithm
(`src/Delve/Helpers/FuzzyMatcher.cs`) rather than referencing Docket as a project/package, so
result ranking feels the same without dragging in Docket's much larger WinUI dependency graph.

### Project layout

```
src/Delve/
  App.xaml(.cs)                 Composition root: tray icon, hotkey, availability polling
  TrayIconResources.xaml        TaskbarIcon + context menu (Open/Hide/Quit), merged into App
  Services/
    DocketAvailabilityService   Polls docket.exe + index entry count; raises availability events
    DocketIndexReader           Read-only SQLite query against Docket's index + fuzzy ranking
    GlobalHotkeyService         RegisterHotKey on a hidden message-only window (own thread)
    ShellIconCacheService       Per-extension shell icon lookup (SHGetFileInfo), cached
    ShellOpenService            Default-handler open / reveal-in-Explorer
  Helpers/FuzzyMatcher.cs       Vendored copy of Docket's ranking algorithm
  Models/                       SearchResultItem (data) / SearchResultViewModel (+ icon, for binding)
  Views/SearchPopupWindow       The search bar + results flyout popup
  Assets/                       App icon + active/inactive tray icons (Segoe Fluent "Search" glyph)
tests/Delve.Tests/
  FuzzyMatcherTests             Ranking behavior (substring priority, subsequence fallback, edge cases)
  DocketIndexReaderTests        Against a hand-built DB matching Docket's schema, incl. concurrent-
                                 reader-while-writer-holds-a-transaction
```

### Notable implementation gotchas

A handful of non-obvious WinUI 3 / Win32 behaviors surfaced during hands-on testing and are
worth recording here rather than only in code comments:

- **`Ctrl+Win+D` is reserved by Windows** ("create new virtual desktop") and silently wins over
  `RegisterHotKey` — the popup never received the keypress. Delve uses `Shift+Win+D` instead.
- **A global hotkey can't reliably win real OS keyboard focus** via `Window.Activate()` or
  `AppWindow.Show(activateWindow: true)` when it summons a window from a background/tray
  process — Windows' foreground-lock rules block it. `SearchPopupWindow` uses the
  `AttachThreadInput`/`SetForegroundWindow` bypass standard to launcher-style apps (PowerToys
  Run, Wox), then requests focus via `FocusManager.TryFocusAsync` posted through the dispatcher
  queue (with retry) so it runs after the activation messages that bypass triggers have
  actually been processed, rather than racing ahead of them.
- **Windows 11's DWM draws its own border/rounded-corner frame** on top of a borderless
  (`SetBorderAndTitleBar(false, false)`) window by default, which showed up as an inconsistent
  single/double-width edge. Disabled explicitly via `DwmSetWindowAttribute`
  (`DWMWA_BORDER_COLOR` / `DWMWA_WINDOW_CORNER_PREFERENCE`).
- **Resizing the popup per keystroke caused compositor tearing/ghost pixels**, worse on the very
  first open. Fixed by sizing the window once *before* attaching the `MicaBackdrop` (not after),
  resizing at most once per search session (two fixed states — collapsed/expanded — rather than
  one size per result count), via a single atomic `AppWindow.MoveAndResize` instead of separate
  `Resize`+`Move` calls. The vertical position is anchored to the *collapsed* height specifically,
  so the search box's own top edge never moves as the results panel grows below it.
- **H.NotifyIcon's tray context menu doesn't route through the WinUI `Click` event.** Its
  default mode builds a native Win32 popup menu from the `MenuFlyout` and invokes each item's
  `Command`/`ExecuteRequested` — plain `MenuFlyoutItem.Click` handlers are silently never
  called. `TrayIconResources.xaml`'s Open/Hide/Quit items are `XamlUICommand`s wired via
  `ExecuteRequested` in `App.xaml.cs`, matching H.NotifyIcon's own documented pattern.

### Known limitation (inherited from Docket)

Docket's search — and so Delve's, since it deliberately mirrors it — pre-filters candidates in
SQL with `WHERE Name LIKE '%query%'` before handing them to the fuzzy ranker. That means a
truly non-contiguous typo (e.g. `rdme` against `readme.txt`, which has no contiguous `rdme`
substring) never reaches the fuzzy matcher at all; "typo tolerance" in practice only re-ranks
candidates that already contain the query as a literal substring. This is documented and
covered by a test (`SearchAsync_NonContiguousTypo_IsFilteredOutBeforeFuzzyMatcherEverSeesIt`)
rather than silently patched, since loosening it is a real design decision (likely a broader
SQL pre-filter or an in-memory scan), not a bug fix.

## Building

Delve targets WinUI 3 (Windows App SDK 1.6, .NET 8, unpackaged, x64) and — like Docket — needs
**Visual Studio 2022 or later with the ".NET Desktop Development" and "Windows App SDK"
workloads**. Plain `dotnet build`/`dotnet publish` cannot complete a WinUI 3 app's XAML/PRI
compilation without those components; open `Delve.slnx` in Visual Studio and build from there,
or invoke Visual Studio's own `MSBuild.exe` directly.

The pure-logic pieces (`Helpers/FuzzyMatcher.cs`, `Models/SearchResultItem.cs`,
`Services/DocketIndexReader.cs`) have no WinUI dependency and are linked directly into
`tests/Delve.Tests`, which builds and runs anywhere with `dotnet test` — no Visual Studio
required for that part.

## Running

1. Have Docket running with at least one folder added under Control Centre → Search Index.
2. Launch `delve.exe`. It has no window — check the notification area for its icon.
3. Press `Shift+Win+D` to open the search bar. Type to search; click a result to open it,
   Ctrl+click to reveal it in Explorer; Escape or click away to dismiss.
4. Right-click the tray icon for Open / Hide / Quit.
