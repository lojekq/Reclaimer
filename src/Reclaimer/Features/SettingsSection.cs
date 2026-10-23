using System.Windows;
using System.Windows.Controls;
using Reclaimer.Shell;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace Reclaimer.Features;

/// <summary>Reclaimer's part of the Settings page (SettingsPage.AppSection): folders, thresholds, exclusions (07 §7).</summary>
public static class SettingsSection
{
    public static FrameworkElement Create()
    {
        var s = Settings.Current;
        var panel = new StackPanel();
        var title = new TextBlock { Text = AppInfo.DisplayName, FontTypography = FontTypography.BodyStrong, Margin = new(0, 0, 0, 8) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        panel.Children.Add(title);
        panel.Children.Add(Folders("Settings.ProjectRoots", SymbolRegular.Code24, s.ProjectRoots, "Settings.ProjectRoots.Note"));
        panel.Children.Add(Number("Settings.AgeDays", SymbolRegular.Clock24, s.AgeDays, 1, 3650, v => s.AgeDays = v));
        panel.Children.Add(Number("Settings.QuarantineDays", SymbolRegular.Archive24, s.QuarantineDays, 1, 30, v => s.QuarantineDays = v));
        panel.Children.Add(Number("Settings.MinSize", SymbolRegular.Filter24, s.MinSizeMb, 0, 1_000_000, v => s.MinSizeMb = v));
        var remind = new ToggleSwitch { IsChecked = s.RemindWeekly };
        remind.Click += (_, _) => { s.RemindWeekly = remind.IsChecked == true; Settings.Save(); };
        panel.Children.Add(new CardControl { Header = Loc.T("Settings.Remind"), Icon = new SymbolIcon(SymbolRegular.Alert24), Content = remind, Margin = new(0, 0, 0, 4) });
        panel.Children.Add(Folders("Settings.ModelDirs", SymbolRegular.BrainCircuit24, s.ModelDirs, "Settings.ModelDirs.Note"));
        panel.Children.Add(Folders("Settings.ExtraCaches", SymbolRegular.BoxMultiple24, s.ExtraCacheDirs, "Settings.ExtraCaches.Note"));
        panel.Children.Add(Folders("Settings.Launchers", SymbolRegular.DocumentText24, s.LauncherDirs, "Settings.Launchers.Note"));
        panel.Children.Add(Folders("Settings.Excluded", SymbolRegular.ShieldProhibited24, s.Excluded, "Settings.Excluded.Note"));
        return panel;
    }

    static CardControl Number(string key, SymbolRegular icon, int value, int min, int max, Action<int> set)
    {
        var box = new NumberBox { Value = value, Minimum = min, Maximum = max, MaxDecimalPlaces = 0, Width = 140, ClearButtonEnabled = false };
        box.ValueChanged += (_, _) =>
        {
            if (box.Value is not { } v) return;
            set((int)Math.Clamp(v, min, max));
            Settings.Save();
        };
        return new CardControl { Header = Loc.T(key), Icon = new SymbolIcon(icon), Content = box, Margin = new(0, 0, 0, 4) };
    }

    static CardExpander Folders(string key, SymbolRegular icon, List<string> list, string note)
    {
        var box = new ListBox { MinHeight = 36, MaxHeight = 180 };
        foreach (var p in list) box.Items.Add(p);
        var count = new TextBlock { FontTypography = FontTypography.Caption, Appearance = TextColor.Secondary, TextWrapping = TextWrapping.Wrap };
        void Sync() => count.Text = list.Count == 0 ? Loc.T(note) : string.Format(Loc.T("Settings.Folders"), list.Count) + " · " + Loc.T(note);
        Sync();

        var add = new Button { Content = Loc.T("Settings.Add"), Icon = new SymbolIcon(SymbolRegular.FolderAdd24), Margin = new(0, 0, 8, 0) };
        add.Click += (_, _) =>
        {
            foreach (var d in OverviewPage.Pick() ?? [])
                if (!list.Contains(d, StringComparer.OrdinalIgnoreCase)) { list.Add(d); box.Items.Add(d); }
            Settings.Save();
            Sync();
        };
        var remove = new Button { Content = Loc.T("Settings.Remove"), Icon = new SymbolIcon(SymbolRegular.Dismiss24) };
        remove.Click += (_, _) =>
        {
            if (box.SelectedItem is not string d) return;
            list.Remove(d);
            box.Items.Remove(d);
            Settings.Save();
            Sync();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 8, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(remove);
        var content = new StackPanel();
        content.Children.Add(box);
        content.Children.Add(buttons);

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = Loc.T(key) });
        header.Children.Add(count);
        return new CardExpander { Header = header, Icon = new SymbolIcon(icon), Content = content, Margin = new(0, 0, 0, 4) };
    }
}
