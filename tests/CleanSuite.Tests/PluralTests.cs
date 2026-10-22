using System.Runtime.InteropServices;
using CleanSuite.Duplicates;
using CleanSuite.Junk;
using CleanSuite.Safety;

namespace CleanSuite.Tests;

/// <summary>Arabic counted nouns: the four forms and the count that chooses between them. Temp sandboxes and a private registry hive only.</summary>
public class PluralTests
{
    [Theory]
    [InlineData(1, "حُذف 1 عنصر.")]
    [InlineData(2, "حُذف عنصران.")]
    [InlineData(5, "حُذف 5 عناصر.")]
    [InlineData(10, "حُذف 10 عناصر.")]
    [InlineData(11, "حُذف 11 عنصرًا.")]
    [InlineData(99, "حُذف 99 عنصرًا.")]
    [InlineData(100, "حُذف 100 عنصر.")]
    [InlineData(103, "حُذف 103 عناصر.")]
    [InlineData(1234, "حُذف 1,234 عنصرًا.")]
    [InlineData(0, "حُذف 0 عنصر.")]
    public void Arabic_plural_forms_follow_the_count(long n, string expected)
    {
        var old = CleanSuite.App.Localization.Loc.Instance.Language;
        try
        {
            CleanSuite.App.Localization.Loc.Instance.Language = CleanSuite.App.Localization.UiLanguage.Arabic;
            Assert.Equal(expected, CleanSuite.App.Localization.Loc.F("RgCleaned", CleanSuite.App.Localization.Loc.N0(n)));
            CleanSuite.App.Localization.Loc.Instance.Language = CleanSuite.App.Localization.UiLanguage.English;
            Assert.DoesNotContain("|", CleanSuite.App.Localization.Loc.F("RgCleaned", CleanSuite.App.Localization.Loc.N0(n)));
        }
        finally { CleanSuite.App.Localization.Loc.Instance.Language = old; }
    }

    [Fact]
    public void Every_plural_token_has_four_forms_and_no_string_is_left_with_a_bar()
    {
        var token = new System.Text.RegularExpressions.Regex(@"\{\d+:[^{}|]+\|[^{}|]+\|[^{}|]+\|[^{}|]+\}");
        var old = CleanSuite.App.Localization.Loc.Instance.Language;
        try
        {
            CleanSuite.App.Localization.Loc.Instance.Language = CleanSuite.App.Localization.UiLanguage.Arabic;
            foreach (var (key, ar) in CleanSuite.App.Localization.Strings.Ar)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(ar, @"\{\d+:[^}]*\|")) continue;     // file-dialog filters also use '|'
                Assert.True(token.IsMatch(ar), key);
                var holes = System.Text.RegularExpressions.Regex.Matches(ar, @"\{(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                var args = Enumerable.Range(0, holes.Max() + 1).Select(_ => (object?)"7").ToArray();
                Assert.DoesNotMatch(@"\{\d+:[^}]*\|", CleanSuite.App.Localization.Loc.F(key, args));
            }
        }
        finally { CleanSuite.App.Localization.Loc.Instance.Language = old; }
    }
}
