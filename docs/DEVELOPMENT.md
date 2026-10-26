# CleanSuite for developers

Command-line options, project layout and CLI reference. For users, see the [README](../README.md); how the projects fit
together and the safety model are in [ARCHITECTURE.md](ARCHITECTURE.md); how to contribute in [CONTRIBUTING.md](../CONTRIBUTING.md);
design notes and remaining work in [NEXT_STEPS.md](../NEXT_STEPS.md).

## Desktop app options

```
src\CleanSuite.App\bin\Release\net10.0-windows10.0.17763.0\CleanSuite.App.exe
CleanSuite.App.exe --scan <folder> [--scan <folder>] [--with-temp]   # scan only these folders on start
CleanSuite.App.exe --lang ar|en --theme system|light|dark           # override the saved UI choices for this run
CleanSuite.App.exe --tab home|junk|history|duplicates|shortcuts|registry|startup|uninstall|disk|restore|scheduler
CleanSuite.App.exe ... --snapshot <file.png> [--open-restore <zip> | --open-compactor | --open-about]  # developer aid: render to a PNG and exit
CleanSuite.App.exe ... --size 760x460 --high-contrast                  # developer aid: window size, contrast-theme palette
CleanSuite.App.exe --tab disk --auto-scan --scan <folder> --disk-view 1   # developer aid: add and analyze a folder, open a view
                                                                       # (0 overview, 1 contents, 2 file types, 3 largest files, 4 empty folders)
CleanSuite.App.exe --export-icon <file.ico> [<png folder>]           # developer aid: write the vector app icon (Assets/CleanSuite.ico)
CleanSuite.App.exe --tasks jrhs                                     # scheduled cleanup (started by the Scheduler task)
CleanSuite.App.exe --tasks jrhs --snapshot <file.png>                # developer aid: sample results, nothing is run
```

Language and theme are chosen at the bottom of the side bar and saved to `%LocalAppData%\CleanSuite\ui.json`.
A Windows contrast theme (High Contrast) replaces the palette with the system colours (`--high-contrast` shows it without
turning one on).

Keyboard: F5 runs the visible page's scan, analysis or refresh; Esc cancels it; Space ticks or clears the current row of a
result list; in a result list Ctrl+A checks every row and Delete starts the clean of the checked rows (which asks first). In
Disk analysis Alt+← / Alt+→ / Alt+↑ go back, forward and up (the arrows follow the reading direction). English button labels
have access keys (Alt+letter). Every control has a name for screen readers, status lines are announced as they change, and a
tool tip also shows on keyboard focus. Below 1000 pixels the side bar folds to icons (the menu button unfolds it). The window's
size, position and the junk page's options panel are kept in `ui.json`.

Safety: scanning is read-only; cleaning asks for confirmation, skips files that changed since the scan and
protected system locations, and journals every operation to `%LocalAppData%\CleanSuite\Journal`.
Zip backups go to `%LocalAppData%\CleanSuite\Backups`. "Restore from backup…" lists a CleanSuite or Ace Utilities
`Jnk *.zip` backup and restores the selected files to their original locations or to another folder. Entries without a
safe absolute path (relative names, `..`, device paths) are listed but never restored, protected system folders are never
written, and every restored file is journaled.

## Build & test

Requires the .NET 10 SDK on Windows 10 1809+. The target framework is set once, in `Directory.Build.props`.

```
dotnet test CleanSuite.sln
dotnet run --project src/CleanSuite.Cli -- junk scan --root D:\SomeFolder --import-ace
dotnet format whitespace CleanSuite.sln --verify-no-changes
dotnet test CleanSuite.sln -s coverage.runsettings --collect:"XPlat Code Coverage"
```

The code style is in `.editorconfig` (CI checks it). `coverage.runsettings` measures every CleanSuite assembly, the desktop
program included; CI requires 80 % line coverage of the engines (every assembly except `CleanSuite.App`).

UI texts are in `src/CleanSuite.App/Localization/Strings.resx` (English, the neutral resources) and `Strings.ar.resx`
(Arabic); every key must exist in both (a unit test checks it, and that the `{0}` placeholders match). Arabic counted nouns
use the four-form token `{0:ملف|ملفان|ملفات|ملفًا}`. Engine reason texts are translated through `Strings.ReasonPrefixes`.

## Installer

Requires Inno Setup 6.4 or later (`ISCC.exe` is found through its uninstall registration or the usual folders, or pass
`-Iscc`). One command runs the tests, publishes both programs self-contained as single files (no .NET needed on the
target computer) and compiles `installer/CleanSuite.iss`:

```
powershell -ExecutionPolicy Bypass -File .\build.ps1 [-SkipTests] [-Version 1.0.1]
```

Output: `artifacts/publish/CleanSuite.App.exe` and `CleanSuite.Cli.exe`, and `artifacts/installer/CleanSuite-Setup-<version>.exe`
(its SHA-256 is printed). The setup installs for the current user without administrator rights by default (all users on
request), adds a Start-menu entry, needs 64-bit Windows 10 1809 or later, and is in Arabic or English. Uninstall removes
the scheduled task "CleanSuite Maintenance" and asks before deleting `%LocalAppData%\CleanSuite` (settings, logs,
backups; kept by default). Continuous integration: `.github/workflows/build.yml` (build with warnings as errors, tests with
coverage, vulnerable-package check).

Release builds (a `v*` tag) are signed with Authenticode through SignPath when the `SIGNPATH_API_TOKEN` secret is set, and
`build.ps1` then runs in steps so the files can be signed in between: `-SkipInstaller` (publish only),
`-SignedUninstallerDir <dir> -PrepareUninstaller` (writes Inno Setup's uninstaller for signing and prints its path) and
`-SkipPublish -SignedUninstallerDir <dir>` (the installer from the signed files). Setup and the release flow:
[`CODE_SIGNING.md`](CODE_SIGNING.md); the artifact configurations are in `.signpath/artifact-configurations`.

`CleanSuite.Cli --help` lists every command. It refuses options a command does not know and exits with
0 done, 1 unknown command (usage printed), 2 invalid arguments, 3 plug-in entries not decoded,
4 a file or folder could not be read or written.

## Projects

| Project | Purpose |
|---|---|
| `CleanSuite.Matching` | `AceWildcard`, the original-compatible matcher (`*` crosses `\`, case-insensitive) |
| `CleanSuite.Junk` | Patterns, ignore lists (`/T`), temp-folder discovery, scanner (per-disk parallelism, reparse points skipped, 7-day window, access probe) |
| `CleanSuite.Duplicates` | Find Duplicate Files: name/size/date key and whole-file MD5 or CRC-32 as in the original, its default exclusions and file-type profiles; nothing pre-checked; removal to the Recycle Bin or a "Dup" zip backup, always keeping one copy unless confirmed |
| `CleanSuite.DiskUsage` | Disk Analysis: drive capacities, read-only size walk (junctions not followed), folder tree by size, share of parent, six file categories with the original's extension tables, largest 100 files, text report; deletion to the Recycle Bin only |
| `CleanSuite.Restore` | Restore tool: lists junk, duplicate and registry backups made by CleanSuite or the original (`Jnk`/`Dup`/`Reg *.zip`, `*.reg`); file restore never overwrites; registry restore imports in-process and only adds data (deletion lines are ignored); deleting a backup sends it to the Recycle Bin |
| `CleanSuite.RegistryCompact` | Registry Compactor, analysis only: hives from `hivelist` with the original's exclusions, a compacted copy (`RegSaveKeyEx`, latest format) measured in a private temp folder and deleted at once; needs administrator rights; never replaces hives or restarts |
| `CleanSuite.Scheduler` | Scheduler: one Windows Task Scheduler task ("CleanSuite Maintenance") that starts `CleanSuite.App.exe --tasks <jrhs>`; once/daily/weekly/monthly/startup/logon triggers, only while logged on and not on batteries; the scheduled run executes junk → registry (low-risk categories only) → history → shortcuts with each tab's defaults and normal backups, skips while the app is open, and appends to `Scheduled.log` |
| `CleanSuite.History` | Erase History engine (tab "Erase history"): path-rule engine, Windows/Start-menu items, Firefox-family cleaner, cookie whitelist; analysis is read-only, every change is journaled and registry data is backed up to .reg first |
| `CleanSuite.Startup` | Auto-Start Manager: Run keys and startup folders; disable/enable via StartupApproved or the original's `<Key>-` mechanism; delete with backup; detects entries that re-enabled themselves |
| `CleanSuite.Compat` | Read-only import of `HKCU\Software\Acelogix\Ace Utilities\Junk\*`; legacy plug-in decoder |
| `CleanSuite.RegistryClean` | Clean System Registry: generic path scanner, path validator and ignore words as in the original, 16 of its 20 categories (not IE, not ActiveX/COM); scan is read-only; removal exports each key/value to .reg first, skips entries changed since the scan, and is journaled; reads the original's settings and `Data\Ignore_Reg` read-only |
| `CleanSuite.Safety` | What every tool shares: operation journal, protected paths (also through links and short names), `CleanSuitePaths`, the Recycle Bin and `RecycleStep`, the `FileRemover` pipeline and zip backups (`JunkBackupArchive`, read back by CRC before anything is deleted), the backup ledger, `SafeWalk`, registry roots, the REGEDIT5 backup writer and importer with the auto-start filter for untrusted files |
| `CleanSuite.Shortcuts` | Fix Invalid Shortcuts: .lnk files whose target is missing on a fixed drive (read without IShellLink::Resolve), empty Start-menu folders; Recycle Bin removal; retarget with a backup copy |
| `CleanSuite.Uninstall` | Uninstall Manager: the three Uninstall keys with the original's filters; Uninstall/Change/Repair; Remove Entry after a verified .reg export; list report |
| `CleanSuite.App` | WPF desktop app: side bar (Cleaning: Junk files, Erase history, Duplicate files, Invalid shortcuts; System: Registry, Startup programs, Installed programs; Tools: Disk analysis, Restore, Scheduler) and a read-only home page. Every page implements `IToolPage`; the state of the scan → review → clean pages is in `ViewModels/ScanCleanViewModel` (junk, duplicates, history, shortcuts, registry) and of the list pages in `ListPageViewModel` (startup, installed programs, backups), both on `ToolViewModel`. Vector icons drawn in code (no image files, no emoji); status chips; empty-list hints; Windows 11 Mica backdrop; Arabic/English (resx), system/light/dark themes |
| `CleanSuite.Cli` | Read-only: `junk scan`, `junk settings`, `junk backup list`, `plugins decode`, `shortcuts scan`, `uninstall list`, `registry scan`, `duplicates scan` |
| `tests/CleanSuite.Tests` | 269 tests: reference tests T1–T15 plus every engine, the safety layer and its hardening, backups, journal, the page view models, localization and culture (synthetic data in temp sandboxes and a private registry hive only; no real task is registered) |

## CLI

```
junk scan --root <path> [--root ...] [--temp <path>...] [--system-temp] [--import-ace]
          [--ignore-dir <dir>] [--expert] [--temp-all-ignores] [--json <file>]
junk settings
junk backup list <zip>
plugins decode --ini <applications.ini> --out <file.json>
shortcuts scan [--root <path>...] [--drives C:,D:] [--empty-folders] [--include-removable] [--import-ace] [--ignore-dir <dir>]
uninstall list [--updates] [--invalid] [--report <file.txt>] [--tab]
duplicates scan --root <path> [--root ...] [--content md5|crc|none] [--no-name] [--date] [--min-size <bytes>] [--profile <name>] [--import-ace] [--ignore-dir <dir>] [--json <file>]
registry scan [--category <hex mask>] [--thorough] [--include-removable] [--include-cd]
              [--import-ace] [--ignore-dir <dir>] [--show-ignored] [--json <file>]
```

The junk, shortcuts, duplicates and registry scans apply the same ignore lists as their tabs: the original's
`Data\Ignore_*` folders when it is installed, and CleanSuite's own `%LocalAppData%\CleanSuite\Ignore_Junk` and
`Ignore_Reg`. `--no-default-ignore` turns that off; `--ignore-dir` adds folders. The built-in protection lists
(`src/CleanSuite.RegistryClean/IgnoreLists/CleanSuite.txt`: Windows servicing, Windows Installer data, packaged apps,
services, security software; `src/CleanSuite.Junk/IgnoreLists/CleanSuite.txt`: database checkpoint and program files)
are part of the program and always apply.

Result flags (`0x…` column) use the original bit values: 0x2 not ignored, 0x4 zero length, 0x8 junk type,
0x10 temp folder, 0x40 archivable, 0x80 can open, 0x100 can delete.
