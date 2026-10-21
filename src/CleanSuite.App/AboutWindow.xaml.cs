using System.Reflection;
using System.Windows;
using CleanSuite.App.Theming;

namespace CleanSuite.App;

/// <summary>Program name, icon, version, what CleanSuite is, its licence and the developer's page.</summary>
public partial class AboutWindow : Window
{
    public const string DeveloperPage = "https://github.com/abosalehg-ui/CleanSuite";

    public AboutWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        ThemeManager.ApplyTitleBar(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Opens the project page in the default browser.</summary>
    private void Developer_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DeveloperPage) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { UiErrors.Show(this, ex, "developer page"); }
    }
}
