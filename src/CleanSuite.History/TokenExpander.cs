using System.Runtime.InteropServices;

namespace CleanSuite.History;

/// <summary>
/// Expands the path tokens of the original rule engine, then any remaining
/// %VAR% through the process environment. Token matching is case-insensitive.
/// </summary>
public sealed class TokenExpander
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string> _envExpand;

    public TokenExpander(IReadOnlyDictionary<string, string> tokens, Func<string, string>? environmentExpand = null)
    {
        foreach (var kv in tokens) _tokens[kv.Key.Trim('%')] = kv.Value.TrimEnd('\\');
        _envExpand = environmentExpand ?? Environment.ExpandEnvironmentVariables;
    }

    public static TokenExpander ForCurrentUser()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var docs = F(Environment.SpecialFolder.MyDocuments);
        var t = new Dictionary<string, string>
        {
            ["APPDATA"] = F(Environment.SpecialFolder.ApplicationData),
            ["LOCALAPPDATA"] = F(Environment.SpecialFolder.LocalApplicationData),
            ["LOCALLOWAPPDATA"] = KnownFolder(new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16")) ?? "",
            ["COMMONAPPDATA"] = F(Environment.SpecialFolder.CommonApplicationData),
            ["PROGRAMDATA"] = F(Environment.SpecialFolder.CommonApplicationData),
            ["INTERNET_CACHE"] = F(Environment.SpecialFolder.InternetCache),
            ["MYDOCS"] = docs, ["MYDOCUMENTS"] = docs, ["DOCUMENTS"] = docs, ["PERSONAL"] = docs,
            ["WINDOWS"] = F(Environment.SpecialFolder.Windows), ["WINDIR"] = F(Environment.SpecialFolder.Windows),
            ["SYSTEMDIRECTORY"] = F(Environment.SpecialFolder.System), ["SYSDIR"] = F(Environment.SpecialFolder.System),
            ["SYSTEMDIRECTORY(X86)"] = F(Environment.SpecialFolder.SystemX86),
            ["PROGRAMFILES"] = F(Environment.SpecialFolder.ProgramFiles),
            ["PROGRAMFILES(X86)"] = F(Environment.SpecialFolder.ProgramFilesX86),
            ["USERNAME"] = Environment.UserName,
            ["BOOTDRIVE"] = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:"),
        };
        return new TokenExpander(t.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>Returns a copy that also resolves %PATH% (a plug-in's install folder).</summary>
    public TokenExpander WithInstallPath(string? installPath)
    {
        var copy = new TokenExpander(_tokens, _envExpand);
        if (!string.IsNullOrEmpty(installPath)) copy._tokens["PATH"] = installPath.TrimEnd('\\');
        else copy._tokens.Remove("PATH");
        return copy;
    }

    /// <summary>Expands tokens; returns null if a token is unknown or %PATH% is unresolved (the rule must then be skipped).</summary>
    public string? Expand(string text)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int a = text.IndexOf('%', i);
            if (a < 0) { sb.Append(text, i, text.Length - i); break; }
            int b = text.IndexOf('%', a + 1);
            if (b < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, a - i);
            var name = text[(a + 1)..b];
            if (_tokens.TryGetValue(name, out var v)) sb.Append(v);
            else if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase)) return null;
            else
            {
                var env = _envExpand("%" + name + "%");
                if (env.StartsWith('%')) return null;
                sb.Append(env.TrimEnd('\\'));
            }
            i = b + 1;
        }
        return sb.ToString();
    }

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
