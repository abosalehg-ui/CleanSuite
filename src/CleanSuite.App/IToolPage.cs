using System.Windows.Controls;

namespace CleanSuite.App;

/// <summary>
/// What the main window needs from every tool page, so it can treat them alike instead of naming each one: whether the page
/// is busy (the snapshot aid waits) or changing something (the window does not close half-way), and the buttons F5 and Esc
/// press. Pages without a scan or a cancel return null.
/// </summary>
public interface IToolPage
{
    /// <summary>A scan, analysis, refresh or clean is running.</summary>
    bool IsBusy { get; }

    /// <summary>Files, registry data or settings are being changed; closing now would stop the step half-way.</summary>
    bool IsChanging { get; }

    /// <summary>The button F5 presses (scan, analyse or refresh), or null.</summary>
    Button? ScanButton { get; }

    /// <summary>The button Esc presses, or null.</summary>
    Button? CancelButton { get; }

    /// <summary>Stops what can be stopped (called when the window closes).</summary>
    void CancelWork();
}
