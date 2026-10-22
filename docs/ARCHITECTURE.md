# CleanSuite architecture

How the projects depend on each other, and the safety model every tool that changes something follows. For build commands
and the command line see [DEVELOPMENT.md](DEVELOPMENT.md).

## Projects and dependencies

```
                         CleanSuite.App (WPF)            CleanSuite.Cli (read-only)
                                │                                 │
          ┌──────────────┬──────┴──────┬─────────────┬───────────┐ │
          │              │             │             │           │ │
     Scheduler ──► Compat ──► Junk   History   RegistryClean  Shortcuts  Startup  Uninstall
          │                    │       │             │           │         │        │
          │        Duplicates  │   DiskUsage   Restore   RegistryCompact   │        │
          │            │       │       │          │           │            │        │
          └────────────┴───────┴───────┴──────────┴───────────┴────────────┴────────┘
                                          │
                               Safety      Matching
```

- **Safety** and **Matching** are the base: no project below them, every tool above them. Safety holds what all tools share
  (see the safety model). Matching is the original program's wildcard matcher (`*` crosses `\`).
- Each tool is one library: a read-only **scanner** and a separate **remover/cleaner** that only acts on what the user reviewed.
  Outside effects sit behind interfaces (`IRecycleBin`, `IRegistryRoots`, `ISystemEnvironment`, `ITaskStore`,
  `IProcessLauncher`, `IProcessProbe`), so the tests use temp folders and a private registry hive file, never the computer's.
- **Compat** reads the original program's settings (read only); **Scheduler** runs several tools unattended, so it depends on them.
- **App** is the desktop program. Each page implements `IToolPage` (busy, changing, the buttons F5 and Esc press), so the main
  window treats pages alike. Page state lives in view models: `ScanCleanViewModel<TRow>` for scan → review → clean pages (junk,
  duplicates, erase history, shortcuts, registry) and `ListPageViewModel<T>` for list pages (startup, installed programs,
  backups), both on `ToolViewModel` (one busy flag, cancellation, errors raised as an event instead of thrown).

## Safety model

Every tool that changes the computer follows the same rules, implemented once in **Safety**:

1. **Scans only read.** Nothing is selected for removal without the user seeing it (or, in a scheduled run, only the low-risk
   categories `ScheduledRunner` lists).
2. **Checked again right before acting.** `FileRemover` (junk files, duplicates) and the other removers re-check every item:
   - `ProtectedPaths.CheckResolved`: never Windows, program or profile roots, the program's own folder, a drive root, or a folder
     that contains one of them, **also when reached through a junction, a symbolic link, a subst drive or a short 8.3 name**
     (the path Windows would act on is resolved with `GetFinalPathNameByHandle`);
   - the item still exists and is not itself a link;
   - `FileIdentity.ChangedSince`: same size and last write time as at the scan (registry: same value bytes, or the same
     `KeyStamp` of subkeys, values and last write time).
3. **A way back first.** Files go to the Recycle Bin (`RecycleStep`: drives without one are refused, and only what is really
   gone counts) or into a zip backup that is **read back in full and checked by CRC-32 before any original is deleted**.
   Registry keys and values are exported to a `.reg` file before they change; without a backup nothing is changed.
4. **Every step journaled.** `OperationJournal` writes one JSON line per change and per refusal, before results are reported.
   Backups are recorded with their SHA-256 in `BackupLedger`; a backup that could not be recorded is journaled too.
5. **Restores are careful.** File restores never overwrite and never write into Startup folders (or Program Files, for a backup
   CleanSuite did not make). Registry restores only add data; a `.reg` file CleanSuite did not write (or that changed) never
   writes the keys through which programs start (`AutoStartKeys`).
6. **Walks never follow links.** `SafeWalk` lists folders without entering junctions or symbolic links, stops at 260 characters,
   reports unreadable folders and uses an explicit stack. Temp folders and scan roots that are links are not cleaned.
7. **Settings are not trusted blindly.** Wipe roots read from writable settings are checked for plausibility: Outlook's secure
   temp folder must be a `Content.Outlook` (or `OLK…`) folder in the user's cache or temp folder; a Firefox profile must hold
   profile files; a custom Firefox cache location only has its `cache2` folder emptied, and only when it holds Firefox's index.
8. **Windows DLLs from System32 only.** Every assembly is marked `DefaultDllImportSearchPaths(System32)`.

## Where data lives

All under `%LocalAppData%\CleanSuite` (`CleanSuitePaths`): `Backups` (zip and .reg backups, `ledger.tsv`), `Journal` (one folder
per session), `Logs\errors.log`, `Ignore_Junk` and `Ignore_Reg` (the user's ignore lists), `Scheduled.log`, `ui.json`
(language, theme, window placement) and `startup-monitor.json`.
