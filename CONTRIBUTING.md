# Contributing to CleanSuite

Thank you for helping. CleanSuite deletes files and registry data on other people's computers, so changes are held to a few
firm rules.

## Before you start

- Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): the dependency rules and the safety model.
- You need Windows 10 1809 or later and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Rules for changes

1. **Scans never change anything.** A change happens only in a remover, on items the user reviewed.
2. **Use the safety layer, not your own.** Remove files through `FileRemover` or `RecycleStep`, walk folders with `SafeWalk`,
   check targets with `ProtectedPaths.CheckResolved`, back up registry data with `RegBackupWriter`, journal with
   `OperationJournal`, and put CleanSuite's own files under `CleanSuitePaths`.
3. **Tests never touch the computer.** Use `Sandbox` (a temp folder), `AppHiveRegistryRoots` (a private registry hive file)
   and fake `IRecycleBin` / `ITaskStore` / `IProcessLauncher`. A new remover or planner comes with tests.
4. **Every UI text in both languages.** Add the key to `src/CleanSuite.App/Localization/Strings.resx` and `Strings.ar.resx`.
   Arabic counted nouns use `{0:ملف|ملفان|ملفات|ملفًا}`; paths and names inside a sentence go through `Loc.Ltr` and are shown with
   `Bidi.SetText`.
5. **No warnings.** The build treats warnings as errors.

## Checks before a pull request

```
dotnet build CleanSuite.sln -c Release
dotnet test CleanSuite.sln
dotnet format whitespace CleanSuite.sln --verify-no-changes
```

CI runs the same checks, plus coverage (the engines must stay at 80 % line coverage) and a vulnerable-package scan. For changes
to a page, check it in both languages and themes with the snapshot aid, for example:

```
CleanSuite.App.exe --tab disk --lang ar --theme dark --snapshot page.png
```

## Commit messages

One line saying what changed and why, in English; more detail in the body when it helps.
