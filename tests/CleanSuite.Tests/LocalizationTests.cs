using System.Text.RegularExpressions;
using CleanSuite.App.Localization;

namespace CleanSuite.Tests;

public class LocalizationTests
{
    [Fact]
    public void Arabic_and_English_tables_have_the_same_keys()
    {
        Assert.Empty(Strings.En.Keys.Except(Strings.Ar.Keys));
        Assert.Empty(Strings.Ar.Keys.Except(Strings.En.Keys));
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        static string[] Holes(string s) => Regex.Matches(s, @"\{(\d+)(?::[^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();
        foreach (var (key, en) in Strings.En)
            Assert.True(Holes(en).SequenceEqual(Holes(Strings.Ar[key])), $"placeholders differ for '{key}'");
    }

    [Fact]
    public void Every_engine_reason_prefix_has_a_translation()
    {
        foreach (var (_, key) in Strings.ReasonPrefixes)
            Assert.True(Strings.En.ContainsKey(key) && Strings.Ar.ContainsKey(key), key);
    }

    [Theory]
    [InlineData("changed since scan", "R_Changed")]
    [InlineData("exists", "R_Exists")]
    [InlineData("original location unknown (relative name)", "R_UnknownRelative")]
    [InlineData(@"protected system folder: C:\Windows\System32", "R_ProtectedSystem")]
    public void Engine_reasons_are_translated(string reason, string key)
    {
        var old = Loc.Instance.Language;
        try
        {
            Loc.Instance.Language = UiLanguage.Arabic;
            Assert.StartsWith(Strings.Ar[key], Loc.Reason(reason));
            Assert.Equal("Access is denied.", Loc.Reason("Access is denied."));   // unknown OS text passes through
        }
        finally { Loc.Instance.Language = old; }
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    public void Sizes_and_dates_use_latin_digits_and_gregorian_calendar(string culture)
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            Assert.Equal("2026-10-21 09:05", Loc.FormatDate(new DateTime(2026, 10, 21, 9, 5, 0)));
            Assert.Equal("1,234", Loc.N0(1234));
            Assert.Matches(@"^1\.5\d? ", Loc.FormatSize(1536L << 10));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }
}
