using CleanSuite.Safety;
using Microsoft.Win32;

namespace CleanSuite.History;

/// <summary>Base for registry changes: the affected data is exported to the session's .reg backup first; no backup, no change.</summary>
public abstract class RegistryAction : HistoryAction
{
    public required RegHive Hive { get; init; }
    /// <summary>Key path below the hive root.</summary>
    public required string SubKey { get; init; }

    protected string Display => RegBackupWriter.HivePath(Hive) + "\\" + SubKey;

    protected ActionResult Run(ExecutionContext ctx, string kind, Func<RegistryKey, bool> backup, Action<RegistryKey> change)
    {
        var root = ctx.Registry.Root(Hive);
        try
        {
            if (ctx.RegistryBackup == null) return ActionResult.Fail("no registry backup available");
            if (!backup(root)) return ActionResult.Ok;                                   // nothing there any more
            change(root);
            ctx.Journal.Record(kind, Display + (Detail is { } d ? " :: " + d : ""), true, backupRef: ctx.RegistryBackup.FilePath);
            return ActionResult.Ok;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            var msg = e is UnauthorizedAccessException or System.Security.SecurityException ? "requires administrator rights" : e.Message;
            ctx.Journal.Record(kind, Display, false, backupRef: ctx.RegistryBackup?.FilePath, error: msg);
            return ActionResult.Fail(msg);
        }
    }
}

/// <summary>DELKEY: deletes a key and everything below it.</summary>
public sealed class DeleteRegistryKeyAction : RegistryAction
{
    public override string Kind => "reg-delete-key";

    public override ActionResult Execute(ExecutionContext ctx) => Run(ctx, Kind,
        root => ctx.RegistryBackup!.ExportKey(root, Hive, SubKey),
        root => root.DeleteSubKeyTree(SubKey, throwOnMissingSubKey: false));
}

/// <summary>EMPTYKEY: deletes all values and subkeys but keeps the key.</summary>
public sealed class EmptyRegistryKeyAction : RegistryAction
{
    public override string Kind => "reg-empty-key";

    public override ActionResult Execute(ExecutionContext ctx) => Run(ctx, Kind,
        root => ctx.RegistryBackup!.ExportKey(root, Hive, SubKey),
        root =>
        {
            using var key = root.OpenSubKey(SubKey, writable: true);
            if (key == null) return;
            foreach (var sub in key.GetSubKeyNames()) key.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            foreach (var v in key.GetValueNames()) key.DeleteValue(v, throwOnMissingValue: false);
        });
}

/// <summary>DELVAL: deletes one value.</summary>
public sealed class DeleteRegistryValueAction : RegistryAction
{
    public required string ValueName { get; init; }
    public override string Kind => "reg-delete-value";
    public override string? Detail => ValueName.Length == 0 ? "(default)" : ValueName;

    public override ActionResult Execute(ExecutionContext ctx) => Run(ctx, Kind,
        root => ctx.RegistryBackup!.ExportValue(root, Hive, SubKey, ValueName),
        root =>
        {
            using var key = root.OpenSubKey(SubKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        });
}

/// <summary>Plans registry actions only for data that exists (analysis reads with KEY_READ only).</summary>
public sealed class RegistryPlanner
{
    private readonly IRegistryRoots _roots;
    public RegistryPlanner(IRegistryRoots roots) => _roots = roots;

    public IRegistryRoots Roots => _roots;

    private static bool HasContent(RegistryKey k) => k.ValueCount > 0 || k.SubKeyCount > 0;

    public void DeleteKey(List<HistoryAction> actions, string itemKey, RegHive hive, string subKey)
    {
        try
        {
            using var k = _roots.Root(hive).OpenSubKey(subKey, writable: false);
            if (k != null) actions.Add(new DeleteRegistryKeyAction { ItemKey = itemKey, Target = RegBackupWriter.HivePath(hive) + "\\" + subKey, Hive = hive, SubKey = subKey });
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    public void EmptyKey(List<HistoryAction> actions, string itemKey, RegHive hive, string subKey)
    {
        try
        {
            using var k = _roots.Root(hive).OpenSubKey(subKey, writable: false);
            if (k != null && HasContent(k))
                actions.Add(new EmptyRegistryKeyAction { ItemKey = itemKey, Target = RegBackupWriter.HivePath(hive) + "\\" + subKey, Hive = hive, SubKey = subKey });
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    public void DeleteValue(List<HistoryAction> actions, string itemKey, RegHive hive, string subKey, string valueName)
    {
        try
        {
            using var k = _roots.Root(hive).OpenSubKey(subKey, writable: false);
            if (k != null && k.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                actions.Add(new DeleteRegistryValueAction
                {
                    ItemKey = itemKey, Target = RegBackupWriter.HivePath(hive) + "\\" + subKey, Hive = hive, SubKey = subKey, ValueName = valueName,
                });
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    /// <summary>Value names of a key (empty when missing or unreadable).</summary>
    public IReadOnlyList<string> ValueNames(RegHive hive, string subKey)
    {
        try
        {
            using var k = _roots.Root(hive).OpenSubKey(subKey, writable: false);
            return k?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public bool KeyExists(RegHive hive, string subKey)
    {
        try { using var k = _roots.Root(hive).OpenSubKey(subKey, writable: false); return k != null; }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    public string? ReadString(RegHive hive, string subKey, string value)
    {
        try
        {
            using var k = _roots.Root(hive).OpenSubKey(subKey, writable: false);
            return k?.GetValue(value) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }
}
