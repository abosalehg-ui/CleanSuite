# Changelog

All notable changes to CleanSuite. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Code signing

- Release builds sign `CleanSuite.App.exe`, `CleanSuite.Cli.exe`, the uninstaller and the setup with Authenticode through
  SignPath (free signing for open-source projects by SignPath Foundation), so Smart App Control no longer blocks them
  ("An Application Control policy has blocked this file", error 4551). Every signature is checked before the release is
  drafted; without the SignPath secret the release is built unsigned as before. Setup: `docs/CODE_SIGNING.md`.
- Every program file carries the product name CleanSuite and the plain version number (no commit hash).
- `build.ps1` can run in steps (`-SkipInstaller`, `-PrepareUninstaller`, `-SkipPublish`, `-SignedUninstallerDir`) so files
  can be signed in between.

## [1.1.0] - 2026-10-23

### Disk analysis, redesigned after the original program

- The folder tree starts at **This PC** with every drive; This PC's overview lists each drive's capacity, used space and share,
  with a pie of used and free space. Double-click (or Enter on) a drive to open it.
- **Choosing a drive analyzes it at once**: no Analyze button to press. The rest of the page stays usable while it runs; an
  analyzed drive is kept, so returning to it is instant, and Analyze runs it again.
- A drive or folder's **Overview** shows its path, size and occupied space, the largest folders and files and the size by file
  type, each as a list next to a pie chart. Then Folder contents, File types, Top 100 files and Empty folders.
- Back, Forward and Up buttons (and Alt+←, Alt+→, Alt+↑); a status bar with the drive's free and total space.
- The views of a large folder are computed off the UI thread, so the window never freezes.

### Safety

- Nothing is removed through a junction, symbolic link, subst drive or short name that leads into a protected location; temp
  folders and scan roots that are links are not cleaned.
- Outlook's secure temp folder and Firefox profile and cache locations are checked before anything in them is planned.
- Scheduled runs no longer empty the clipboard.
- A `.reg` file CleanSuite did not make never restores the keys that start programs (also from the Registry page's Restore).
- Zip backups are read back and checked by CRC-32 before the originals are deleted.
- A backup that cannot be recorded in the ledger is journaled; every refusal is journaled.
- Windows DLLs are loaded from System32 only.

### Interface

- Paths and program names inside Arabic sentences keep their order everywhere; untranslated engine reasons and key names are
  translated; attributes are spelled out.
- Tab reaches the pages before language and theme; the side bar folds to icons in narrow windows.
- Ctrl+A and Delete in result lists, access keys on English buttons, tool tips on keyboard focus.
- The window's size and position and the junk options panel are remembered.
- The shortcut scan can be cancelled; the junk scan shows how many files it has looked at; more empty-list hints.
- Grid columns size to their content instead of fixed pixel widths.

### Under the hood

- .NET 10 (LTS). UI texts moved to `.resx` files. One `FileRemover` pipeline, `RecycleStep`, `SafeWalk` and `CleanSuitePaths`
  in the safety library; the junk library is no longer a dependency of duplicates, shortcuts, disk analysis and restore.
- Every page on shared view models (`ToolViewModel`, `ScanCleanViewModel`, `ListPageViewModel`); the junk page has its own view.
- The Erase History planner is a table, one entry per item.
- 269 tests; coverage now includes the desktop program; CI checks the code style and engine coverage, and builds the installer
  for version tags.

## [1.0.0] - 2026-10-21

First release: junk files, erase history (Windows and the Firefox family), duplicate files, invalid shortcuts, registry
cleaning, startup programs, installed programs, disk analysis, restore, scheduler and the registry compactor (analysis only),
in Arabic and English, with an installer.
