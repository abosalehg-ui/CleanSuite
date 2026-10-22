using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CleanSuite.App.Localization;

namespace CleanSuite.App;

/// <summary>
/// Unexpected errors: written to %LocalAppData%\CleanSuite\Logs\errors.log and shown to the user, so a failure never closes
/// the program silently in the middle of a clean. Used by every page and by the global handlers in App.
/// </summary>
public static class UiErrors
{
    public static string LogPath =>
        Path.Combine(CleanSuite.Safety.CleanSuitePaths.Logs, "errors.log");

    public static void Log(Exception ex, string context)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + context
                                        + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Logs the error and tells the user what happened; safe to call from any page.</summary>
    public static void Show(Window? owner, Exception ex, string context)
    {
        Log(ex, context);
        var text = Loc.F("ErrUnexpected", ex.Message, Loc.Ltr(LogPath));
        owner ??= Application.Current?.MainWindow;
        if (owner != null && owner.IsLoaded) Loc.Show(owner, text, Loc.T("ErrTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(text, Loc.T("ErrTitle"), MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, Loc.MessageOptions);
    }
}
