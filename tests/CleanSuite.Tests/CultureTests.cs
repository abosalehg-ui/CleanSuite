using System.Globalization;
using CleanSuite.Compat;
using CleanSuite.Junk;
using CleanSuite.Safety;
using Xunit;

namespace CleanSuite.Tests;

/// <summary>Regression tests: results must not depend on the user's culture (ar-SA uses the Hijri calendar).</summary>
public class CultureTests
{
    private static T WithCulture<T>(string name, Func<T> f)
    {
        var (c, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
        try { return f(); }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (c, ui); }
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    [InlineData("en-US")]
    public void Journal_folder_uses_gregorian_date(string culture)
    {
        using var sb = new Sandbox();
        var folder = WithCulture(culture, () =>
        {
            using var j = new OperationJournal("t", sb.Root);
            return Path.GetFileName(j.SessionFolder);
        });
        Assert.StartsWith(DateTime.Now.ToString("yyyy", CultureInfo.InvariantCulture), folder);
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    public void Plugin_decoding_is_culture_independent(string culture) =>
        Assert.Equal(@"%AppData%\4Sync\*.log",
            WithCulture(culture, () => LegacyPluginDecoder.Decode("5AC9639CC10A22349C4BA9995DD6C250D1743D941626E89B", "4Sync", 0)));

    [Fact]
    public void Wildcard_matching_is_culture_independent()
    {
        // Turkish dotted/dotless i must not change results.
        Assert.True(WithCulture("tr-TR", () => CleanSuite.Matching.AceWildcard.IsMatch("FILE.INI", "*.ini")));
        Assert.True(WithCulture("tr-TR", () => PatternSet.BuildDefault().Classify("x.tmp", @"C:\x.tmp") != null));
    }
}
