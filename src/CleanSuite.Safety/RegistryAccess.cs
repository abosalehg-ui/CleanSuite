using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CleanSuite.Safety;

public enum RegHive { ClassesRoot, CurrentUser, LocalMachine, Users }

/// <summary>
/// Root keys the History items read and change. Abstracted so tests use a private hive file (RegLoadAppKey) instead of
/// the real registry; production uses the real roots.
/// </summary>
public interface IRegistryRoots
{
    /// <summary>The root key for a hive (callers must not dispose it).</summary>
    RegistryKey Root(RegHive hive);
}

public sealed class WindowsRegistryRoots : IRegistryRoots
{
    public static WindowsRegistryRoots Instance { get; } = new();
    private static readonly RegistryKey LocalMachine64 =
        RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);

    public RegistryKey Root(RegHive hive) => hive switch
    {
        RegHive.ClassesRoot => Registry.ClassesRoot,
        RegHive.CurrentUser => Registry.CurrentUser,
        RegHive.LocalMachine => LocalMachine64,
        RegHive.Users => Registry.Users,
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };
}

/// <summary>
/// Test roots: one subkey per hive ("HKCU", "HKLM"…) inside an application hive file loaded with RegLoadAppKey.
/// Nothing outside that file is touched.
/// </summary>
public sealed class AppHiveRegistryRoots : IRegistryRoots, IDisposable
{
    private readonly RegistryKey _root;
    private readonly Dictionary<RegHive, RegistryKey> _hives = new();

    public AppHiveRegistryRoots(string hiveFile)
    {
        int err = RegLoadAppKeyW(hiveFile, out var handle, 0xF003F /* KEY_ALL_ACCESS */, 0, 0);
        if (err != 0) throw new System.ComponentModel.Win32Exception(err, "RegLoadAppKey failed");
        _root = RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true));
        foreach (RegHive h in Enum.GetValues<RegHive>()) _hives[h] = _root.CreateSubKey(HiveName(h), writable: true);
    }

    public RegistryKey Root(RegHive hive) => _hives[hive];

    public static string HiveName(RegHive h) => h switch
    {
        RegHive.ClassesRoot => "HKCR",
        RegHive.CurrentUser => "HKCU",
        RegHive.LocalMachine => "HKLM",
        _ => "HKU",
    };

    public void Dispose()
    {
        foreach (var k in _hives.Values) k.Dispose();
        _root.Dispose();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKeyW(string file, out IntPtr key, uint sam, uint options, uint reserved);
}

/// <summary>
/// Writes a standard REGEDIT5 backup (UTF-16LE with BOM, importable with regedit/reg.exe) of every key or value before it is
/// changed. One file per clean session, created on first use: "%LocalAppData%\CleanSuite\Backups\Reg yyyy_MM_dd_hh_mm_ss_tt.reg".
/// </summary>
public sealed class RegBackupWriter : IDisposable
{
    private readonly string _folder;
    private StreamWriter? _writer;

    /// <param name="journal">Receives a line when the finished backup cannot be recorded in the ledger (see <see cref="BackupLedger"/>).</param>
    public RegBackupWriter(string? folder = null, OperationJournal? journal = null)
    {
        _folder = folder ?? CleanSuitePaths.Backups;
        _journal = journal;
    }

    private readonly OperationJournal? _journal;

    public string? FilePath { get; private set; }

    public static string HivePath(RegHive h) => h switch
    {
        RegHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegHive.CurrentUser => "HKEY_CURRENT_USER",
        RegHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        _ => "HKEY_USERS",
    };

    private StreamWriter Writer()
    {
        if (_writer != null) return _writer;
        Directory.CreateDirectory(_folder);
        var name = "Reg " + BackupNames.Timestamp();
        var path = Path.Combine(_folder, name + ".reg");
        for (int n = 1; File.Exists(path); n++) path = Path.Combine(_folder, $"{name}({n}).reg");
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UnicodeEncoding(false, true))
        { AutoFlush = true, NewLine = "\r\n" };
        _writer.WriteLine("Windows Registry Editor Version 5.00");
        FilePath = path;
        return _writer;
    }

    /// <summary>Exports a key with all its values and subkeys (nothing is written when the key does not exist).</summary>
    public bool ExportKey(RegistryKey root, RegHive hive, string subKey)
    {
        using var key = root.OpenSubKey(subKey, writable: false);
        if (key == null) return false;
        var w = Writer();
        void Walk(RegistryKey k, string path)
        {
            w.WriteLine();
            w.WriteLine(KeyLine(hive, path));
            foreach (var name in k.GetValueNames()) w.WriteLine(FormatValue(k, name));
            foreach (var sub in k.GetSubKeyNames())
            {
                using var s = k.OpenSubKey(sub, writable: false);
                if (s != null) Walk(s, path + "\\" + sub);
            }
        }
        Walk(key, subKey);
        return true;
    }

    /// <summary>Exports one value (nothing is written when it does not exist).</summary>
    public bool ExportValue(RegistryKey root, RegHive hive, string subKey, string valueName)
    {
        using var key = root.OpenSubKey(subKey, writable: false);
        if (key == null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase)) return false;
        var w = Writer();
        w.WriteLine();
        w.WriteLine(KeyLine(hive, subKey));
        w.WriteLine(FormatValue(key, valueName));
        return true;
    }

    private static string KeyLine(RegHive hive, string path) =>
        Representable(path) ? $"[{HivePath(hive)}\\{path}]"
                            : throw new IOException($"the key name {path} cannot be written to a .reg backup, so it is not changed");

    /// <summary>
    /// One value line with its exact registry type and bytes, read with RegQueryValueEx: strings that a plain "…" line
    /// cannot carry (line breaks, control characters, embedded or missing terminators) become hex(1), a DWORD of another
    /// length becomes hex(4), and every other type is hex(type), so a restore writes back exactly what was there.
    /// </summary>
    internal static string FormatValue(RegistryKey k, string name)
    {
        if (!Representable(name)) throw new IOException($"the value name {name} cannot be written to a .reg backup, so it is not changed");
        var label = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
        var (type, data) = RawValue(k, name);
        if (type == 1 && PlainString(data) is { } s) return $"{label}=\"{Escape(s)}\"";
        if (type == 4 && data.Length == 4) return $"{label}=dword:{BitConverter.ToUInt32(data):x8}";
        return label + "=" + Hex(type == 3 ? "hex" : $"hex({type:x})", data);
    }

    /// <summary>The text of a REG_SZ that fits a quoted line: one terminating NUL at the end and no control characters.</summary>
    private static string? PlainString(byte[] data)
    {
        if (data.Length < 2 || data.Length % 2 != 0) return null;
        var s = Encoding.Unicode.GetString(data, 0, data.Length - 2);
        if (data[^1] != 0 || data[^2] != 0 || s.Any(char.IsControl)) return null;
        return s;
    }

    private static bool Representable(string s) => !s.Any(c => c is '\r' or '\n' or '\0');

    private static (uint Type, byte[] Data) RawValue(RegistryKey k, string name)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int size = 0;
            int err = RegQueryValueExW(k.Handle, name, IntPtr.Zero, out _, null, ref size);
            if (err != 0) throw new IOException($"cannot read value {name}", new System.ComponentModel.Win32Exception(err));
            var data = new byte[size];
            err = RegQueryValueExW(k.Handle, name, IntPtr.Zero, out var type, data, ref size);
            if (err == 234 /* ERROR_MORE_DATA: it grew in between */) continue;
            if (err != 0) throw new IOException($"cannot read value {name}", new System.ComponentModel.Win32Exception(err));
            return (type, size == data.Length ? data : data[..size]);
        }
        throw new IOException($"value {name} keeps changing while it is backed up");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Hex(string prefix, byte[] data) => prefix + ":" + string.Join(",", data.Select(b => b.ToString("x2")));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string? name, IntPtr reserved, out uint type, byte[]? data, ref int size);

    public void Dispose()
    {
        if (_writer == null) return;
        _writer.Dispose();
        _writer = null;
        if (FilePath != null && !BackupLedger.Record(FilePath)) _journal?.Record("ledger", FilePath, false, error: BackupLedger.NotRecorded);
    }
}

/// <summary>Imports a REGEDIT5 file written by <see cref="RegBackupWriter"/> (restore of a History or registry clean).</summary>
public static class RegFileImporter
{
    /// <param name="refuse">Keys (as written in the file, "HKEY_…\path") that must not be written; their values are skipped.</param>
    /// <param name="refused">Called with every key skipped because of <paramref name="refuse"/>.</param>
    public static int Import(string regFile, IRegistryRoots roots, Func<string, bool>? refuse = null, Action<string>? refused = null)
    {
        var lines = File.ReadAllText(regFile).Replace("\r\n", "\n").Split('\n');
        RegistryKey? current = null;
        int values = 0;
        try
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd();
                while (line.EndsWith('\\') && i + 1 < lines.Length) line = line[..^1] + lines[++i].Trim();
                if (line.Length == 0 || line.StartsWith("Windows Registry Editor", StringComparison.Ordinal)
                    || line.Equals("REGEDIT4", StringComparison.OrdinalIgnoreCase) || line.StartsWith(';')) continue;
                if (line.StartsWith("[-", StringComparison.Ordinal))
                {
                    // A deletion instruction: a restore only puts data back, so the key and its lines are ignored.
                    current?.Dispose();
                    current = null;
                    continue;
                }
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    current?.Dispose();
                    current = null;
                    var path = line[1..^1];
                    if (refuse?.Invoke(path) == true) { refused?.Invoke(path); continue; }
                    int slash = path.IndexOf('\\');
                    var hive = ParseHive(slash < 0 ? path : path[..slash]);
                    current = roots.Root(hive).CreateSubKey(slash < 0 ? "" : path[(slash + 1)..], writable: true);
                    continue;
                }
                if (current == null) continue;
                var (name, rest) = SplitValue(line);
                if (rest == "-") continue;                                         // "value"=- (deletion): ignored, see above
                SetValue(current, name, rest);
                values++;
            }
        }
        finally { current?.Dispose(); }
        return values;
    }

    private static RegHive ParseHive(string s) => s.ToUpperInvariant() switch
    {
        "HKEY_CLASSES_ROOT" => RegHive.ClassesRoot,
        "HKEY_CURRENT_USER" => RegHive.CurrentUser,
        "HKEY_LOCAL_MACHINE" => RegHive.LocalMachine,
        "HKEY_USERS" => RegHive.Users,
        _ => throw new InvalidDataException("unknown hive " + s),
    };

    private static (string Name, string Data) SplitValue(string line)
    {
        // "@=…" and the original's "@ = …" (spaces around '=') both name the default value.
        if (line.StartsWith('@')) return ("", line[(line.IndexOf('=') + 1)..].Trim());
        var sb = new StringBuilder();
        int i = 1;
        for (; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { sb.Append(line[++i]); continue; }
            if (line[i] == '"') break;
            sb.Append(line[i]);
        }
        int eq = line.IndexOf('=', i);
        return (sb.ToString(), line[(eq + 1)..].Trim());
    }

    private static void SetValue(RegistryKey key, string name, string data)
    {
        const System.Globalization.NumberStyles HexStyle = System.Globalization.NumberStyles.AllowHexSpecifier;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (data.StartsWith('"'))
        {
            if (data.Length < 2 || !data.EndsWith('"')) throw new InvalidDataException($"unterminated string for value {name}");
            key.SetValue(name, Unescape(data[1..^1]), RegistryValueKind.String);
        }
        else if (data.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(data[6..].Trim(), HexStyle, inv, out var dword)) throw new InvalidDataException($"invalid dword for value {name}: {data}");
            key.SetValue(name, unchecked((int)dword), RegistryValueKind.DWord);
        }
        else
        {
            // hex:… is REG_BINARY and hex(n):… any other type (hex(1) strings, hex(2) expandable, hex(7) multi-string,
            // hex(b) QWORD, REG_NONE, resource lists…). The bytes are written back exactly, with their type.
            int colon = data.IndexOf(':');
            var kind = colon > 0 ? data[..colon].Trim().ToLowerInvariant() : "";
            uint type;
            if (kind == "hex") type = 3;
            else if (!(kind.StartsWith("hex(", StringComparison.Ordinal) && kind.EndsWith(')') && uint.TryParse(kind[4..^1], HexStyle, inv, out type)))
                throw new InvalidDataException($"unknown data for value {name}: {(data.Length > 40 ? data[..40] + "…" : data)}");
            var parts = data[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!byte.TryParse(parts[i].Trim(), HexStyle, inv, out bytes[i])) throw new InvalidDataException($"invalid byte '{parts[i].Trim()}' in value {name}");
            int err = RegSetValueExW(key.Handle, name, 0, type, bytes, bytes.Length);
            if (err != 0) throw new IOException($"cannot write value {name}", new System.ComponentModel.Win32Exception(err));
        }
    }

    /// <summary>Reverses the .reg escapes \\ and \" from left to right.</summary>
    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '\\' or '"') i++;
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string? name, int reserved, uint type, byte[] data, int size);
}
