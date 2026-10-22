using CleanSuite.App.Localization;
using CleanSuite.App.ViewModels;
using CleanSuite.Junk;

namespace CleanSuite.App;

/// <summary>One row of the junk results grid; checked unless an ignore list matched it (shown in expert mode only).</summary>
public sealed class JunkRow(JunkItem item) : SelectableRow(!item.Flags.HasFlag(JunkReason.IgnoredShown))
{
    public JunkItem Item { get; } = item;
    public string Name => Item.Name;
    public string Folder => Item.Folder;
    public long SizeKb => Item.SizeKb;
    public string SizeText => Loc.N0(Item.SizeKb);
    public string Category => Loc.Category(Item.Category);
    public string Reason => DescribeReason(Item.Flags);
    public string GroupTitle => Item.Group switch
    {
        >= 2000 => Loc.T("GroupUser"),
        >= 1000 => Loc.T("GroupTemp"),
        _ => Loc.F("GroupDisk", Item.Group / 100),
    };

    /// <summary>Re-reads the language-dependent columns after a language switch.</summary>
    public void RefreshTexts()
    {
        Raise(nameof(SizeText));
        Raise(nameof(Category));
        Raise(nameof(Reason));
        Raise(nameof(GroupTitle));
    }

    public static string DescribeReason(JunkReason f)
    {
        // Same precedence as the original.
        if (f.HasFlag(JunkReason.JunkType)) return Loc.T("ReasonJunkType");
        if (f.HasFlag(JunkReason.TempFolder)) return Loc.T("ReasonTemp");
        if (f.HasFlag(JunkReason.ZeroLength)) return Loc.T("ReasonZero");
        if (f.HasFlag(JunkReason.AlwaysInclude)) return Loc.T("ReasonAlwaysInclude");
        if (f.HasFlag(JunkReason.EmptyTempFolder)) return Loc.T("ReasonEmptyTemp");
        return "";
    }
}

/// <summary>Drive check box in the "Drives" panel.</summary>
/// <param name="name">"C:" or "C: (label)"; shown as its own left-to-right text so labels in either script keep their order.</param>
/// <param name="free">Free-space text in the UI language.</param>
public sealed class DriveChoice(string root, string name, string free, bool isChecked) : ViewModelBase
{
    private bool _isChecked = isChecked;
    public string Root { get; } = root;
    public string Name { get; } = name;
    public string Free { get; } = free;
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }
}
