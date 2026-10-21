using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace CleanSuite.App.Localization;

public enum UiLanguage { Arabic, English }

/// <summary>
/// UI strings. XAML binds through <see cref="TExtension"/> ({loc:T Key}) to the indexer, so switching the language updates the open
/// windows immediately. Arabic is the default; English is kept complete as the fallback.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private UiLanguage _language = UiLanguage.Arabic;

    public UiLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRtl)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public FlowDirection FlowDirection => _language == UiLanguage.Arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    public bool IsRtl => _language == UiLanguage.Arabic;

    /// <summary>
    /// Culture for numbers in every language: Latin digits with "," and "." separators. (Under ICU, ar-SA mixes Latin
    /// digits with Arabic separators "٬ ٫", and its default calendar is Hijri, so it is never used for formatting.)
    /// </summary>
    public CultureInfo NumberCulture => CultureInfo.GetCultureInfo("en-US");

    public string this[string key] => Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public static string T(string key) => Instance.Get(key);

    public static string F(string key, params object?[] args) =>
        string.Format(Instance.NumberCulture, Plurals(Instance.Get(key), args), args);

    // "{0:ملف|ملفان|ملفات|ملفًا}": an Arabic counted noun in its four forms (one | two | few | many).
    private static readonly System.Text.RegularExpressions.Regex PluralToken =
        new(@"\{(\d+):([^{}|]+)\|([^{}|]+)\|([^{}|]+)\|([^{}|]+)\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Resolves the plural tokens of a string for the counts it is formatted with, by the Arabic rules: 2 takes the dual
    /// alone ("ملفان"), 3–10 the plural ("5 ملفات"), 11–99 the singular accusative ("15 ملفًا"), 1, 0 and 100 or more the
    /// singular ("1 ملف", "100 ملف"); the last two digits decide, as in "103 ملفات". The count stays a placeholder, so it is
    /// formatted exactly as before. A count that is not a number keeps the singular.
    /// </summary>
    internal static string Plurals(string template, object?[] args)
    {
        if (!template.Contains('|')) return template;
        return PluralToken.Replace(template, m =>
        {
            int i = int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
            var hole = "{" + i + "} ";
            if (i >= args.Length || !TryCount(args[i], out var n)) return hole + m.Groups[2].Value;
            long last2 = Math.Abs(n) % 100;
            return n == 2 ? m.Groups[3].Value
                 : last2 is >= 3 and <= 10 ? hole + m.Groups[4].Value
                 : last2 is >= 11 and <= 99 ? hole + m.Groups[5].Value
                 : hole + m.Groups[2].Value;
        });
    }

    private static bool TryCount(object? arg, out long n)
    {
        switch (arg)
        {
            case int v: n = v; return true;
            case long v: n = v; return true;
            case string s:
                return long.TryParse(Digits(s), NumberStyles.AllowThousands | NumberStyles.AllowLeadingSign, Instance.NumberCulture, out n)
                       || long.TryParse(s.Replace(",", "").Replace("\u066C", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            default: n = 0; return false;
        }
    }

    public string Get(string key)
    {
        var table = _language == UiLanguage.Arabic ? Strings.Ar : Strings.En;
        if (table.TryGetValue(key, out var s)) return s;
        return Strings.En.TryGetValue(key, out var en) ? en : key;
    }

    /// <summary>Arabic-Indic (and Eastern Arabic-Indic) digits typed in the Arabic UI read as Western digits; trimmed.</summary>
    public static string Digits(string text) => new(text.Trim().Select(c => c switch
    {
        >= '\u0660' and <= '\u0669' => (char)('0' + (c - '\u0660')),
        >= '\u06F0' and <= '\u06F9' => (char)('0' + (c - '\u06F0')),
        _ => c,
    }).ToArray());

    public static string N0(long value) => value.ToString("N0", Instance.NumberCulture);

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => F("SizeGB", bytes / (double)(1L << 30)),
        >= 1L << 20 => F("SizeMB", bytes / (double)(1L << 20)),
        >= 1L << 10 => F("SizeKB", bytes / 1024.0),
        _ => F("SizeBytes", bytes),
    };

    /// <summary>Gregorian date/time in every language (the system culture here uses the Hijri calendar).</summary>
    public static string FormatDate(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Keeps a path or file name left-to-right inside message-box text (LRE…PDF, then a mark of the paragraph direction so
    /// neighbouring digits are not pulled into the name). Win32 message boxes honour these controls; WPF's text engine does
    /// not, so WPF layouts use separate left-to-right elements instead (see the drive list and the PathCell styles).
    /// </summary>
    public static string Ltr(string s) => "‪" + s + "‬" + (Instance.IsRtl ? "‏" : "‎");

    /// <summary>Translates the fixed reason strings produced by the engine; unknown texts (e.g. OS messages) pass through.</summary>
    public static string Reason(string reason)
    {
        foreach (var (prefix, key) in Strings.ReasonPrefixes)
            if (reason.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = reason[prefix.Length..].TrimStart(':', ' ');
                return rest.Length > 0 ? T(key) + ": " + Ltr(rest) : T(key);
            }
        return reason;
    }

    /// <summary>Built-in junk categories (JunkDefaults) are translated; user-defined categories are shown as written.</summary>
    public static string Category(string category) =>
        category.Length > 0 && Strings.En.ContainsKey("Cat_" + category) ? T("Cat_" + category) : category;

    public static MessageBoxOptions MessageOptions => Instance.IsRtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;

    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        MessageBox.Show(owner, text, caption, buttons, icon, defaultResult, MessageOptions);
}

/// <summary>{loc:T Key} → live binding to <see cref="Loc"/>'s indexer.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
