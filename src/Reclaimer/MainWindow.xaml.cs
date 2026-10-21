using System.Windows;
using Reclaimer.Features;
using Reclaimer.Shell;

namespace Reclaimer;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        ElevatedBar.IsOpen = AppInfo.IsElevated;
        Loaded += (_, _) => { if (Nav.SelectedItem is null) Navigate("overview"); };
    }

    /// <summary>By TargetPageTag: "overview", "findings", "quarantine", "disks", "settings", "about"; "confirm" and "tray" exist for --demo screenshots.</summary>
    public void Navigate(string page)
    {
        switch (page)
        {
            case "main": Nav.Navigate("overview"); break;
            case "confirm": Nav.Navigate("findings"); _ = Dialog.Confirm(); break;
            case "tray": Nav.Navigate("overview"); Dialog.TrayPreview(); break;
            default: Nav.Navigate(page); break;
        }
    }

    public void ShowUpdate(string version, string url)
    {
        UpdateBar.Message = string.Format(Loc.T("Update.Message"), version);
        UpdateLink.NavigateUri = url;
        UpdateBar.IsOpen = true;
    }

    internal void ShowDialog(UIElement? content)
    {
        DialogContent.Content = content;
        DialogHost.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
