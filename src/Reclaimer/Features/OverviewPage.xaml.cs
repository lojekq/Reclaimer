using System.Windows;
using Microsoft.Win32;
using Reclaimer.Shell;
using Wpf.Ui.Controls;

namespace Reclaimer.Features;

public partial class OverviewPage
{
    public OverviewPage()
    {
        InitializeComponent();
        DataContext = Model.I;
        ScanButton.Click += (_, _) => _ = Model.I.ScanAsync();
        CancelButton.Click += (_, _) => Model.I.CancelScan();
        AddRootsButton.Click += (_, _) => AddRoots();
        Model.I.PropertyChanged += (_, _) => Sync();
        Sync();
        if (Model.I.Tiles.Count == 0) Model.I.RefreshTiles();
    }

    void Sync()
    {
        var scanning = Model.I.Scanning;
        Ring.Visibility = CancelButton.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        RootsBar.IsOpen = !Args.Current.Demo && Settings.Current.ProjectRoots.Count == 0;
        LastScanText.Text = Settings.Current.LastScanUtc is { } t && !Args.Current.Demo
            ? string.Format(Loc.T("Overview.LastScan"), t.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentUICulture)) : "";
    }

    /// <summary>First run asks for project folders here, not in a pop-up: the user sees why.</summary>
    void AddRoots()
    {
        if (Pick() is not { } dirs) return;
        Settings.Current.ProjectRoots.AddRange(dirs.Except(Settings.Current.ProjectRoots, StringComparer.OrdinalIgnoreCase));
        Settings.Save();
        Sync();
    }

    public static string[]? Pick() =>
        new OpenFolderDialog { Multiselect = true, Title = Loc.T("Overview.AddRoots") } is var d && d.ShowDialog() == true ? d.FolderNames : null;

    void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CardAction { Tag: Category c }) return;
        if (c == Category.Disks) { (Window.GetWindow(this) as MainWindow)?.Navigate("disks"); return; }
        FindingsPage.CategoryFilter = c;
        (Window.GetWindow(this) as MainWindow)?.Navigate("findings");
    }
}
