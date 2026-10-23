using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Reclaimer.Demo;
using Reclaimer.Shell;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace Reclaimer.Features;

/// <summary>Dialogs drawn inside the main window (MainWindow.DialogHost), so screenshots and the GIF see them.</summary>
public static class Dialog
{
    static MainWindow? W => Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();

    /// <summary>The index of the clicked button; -1 on Esc.</summary>
    public static Task<int> Show(string title, UIElement? body, params (string Text, bool Primary)[] buttons)
    {
        var w = W;
        var done = new TaskCompletionSource<int>();
        if (w is null) { done.SetResult(-1); return done.Task; }
        var panel = new StackPanel { MinWidth = 440 };
        panel.Children.Add(new TextBlock { Text = title, FontTypography = FontTypography.Subtitle, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        if (body is not null) panel.Children.Add(body);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i].Text, Appearance = buttons[i].Primary ? ControlAppearance.Primary : ControlAppearance.Secondary, Margin = new(8, 0, 0, 0), MinWidth = 96 };
            b.Click += (_, _) => Close(index);
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        w.PreviewKeyDown += Esc;
        w.ShowDialog(panel);
        return done.Task;

        void Esc(object s, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(-1); } }
        void Close(int i)
        {
            w.PreviewKeyDown -= Esc;
            w.ShowDialog(null);
            done.TrySetResult(i);
        }
    }

    public static TextBlock Text(string text, bool secondary = false) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6), Appearance = secondary ? TextColor.Secondary : TextColor.Primary };

    public static InfoBar Warning(string text) =>
        new() { Message = text, Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false, Margin = new(0, 8, 0, 0) };

    /// <summary>The move confirmation (07 §7): every path with its size, the total, where each volume's quarantine is, the warnings.</summary>
    public static async Task Confirm()
    {
        var units = Model.I.Units.Where(u => u.Selected).ToList();
        if (units.Count == 0) return;
        var days = Math.Clamp(Settings.Current.QuarantineDays, 1, 30);
        var total = Format.Size(units.Sum(u => u.Bytes));
        var body = new StackPanel();

        var list = new StackPanel();
        foreach (var u in units)
        {
            var g = new Grid { Margin = new(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var path = Short(u.Path) + (u.Parts.Count > 1 ? "  " + string.Format(Loc.T("Confirm.Parts"), u.Parts.Count) : "");
            g.Children.Add(new TextBlock { Text = path, ToolTip = u.Path, TextTrimming = TextTrimming.CharacterEllipsis, FontTypography = FontTypography.Body });
            var size = new TextBlock { Text = u.SizeText, Margin = new(16, 0, 0, 0), FontTypography = FontTypography.BodyStrong };
            Grid.SetColumn(size, 1);
            g.Children.Add(size);
            list.Children.Add(g);
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var copy = new Button { Content = Loc.T("Confirm.Copy"), Icon = new SymbolIcon(SymbolRegular.Copy24), Margin = new(0, 8, 0, 8) };
        copy.Click += (_, _) => { try { Clipboard.SetText(Markdown(units)); } catch (Exception e) { Log.Warn($"clipboard: {Fs.Describe(e)}"); } };
        body.Children.Add(copy);

        body.Children.Add(Text(string.Format(Loc.T("Confirm.Total"), total, days, Format.Date(DateTime.UtcNow.AddDays(days)))));
        foreach (var (volume, root) in units.Select(u => RootOf(u)).Distinct())
            body.Children.Add(Text(string.Format(Loc.T("Confirm.Where"), volume, root), secondary: true));

        var models = units.Where(u => u.Category is Category.Models or Category.OwnModels && u.DedupeOf is null).ToList();
        if (models.Count > 0) body.Children.Add(Warning(string.Format(Loc.T("Confirm.WarnModels"), Format.Size(models.Sum(u => u.Bytes)))));
        if (units.Count(u => u.DedupeOf is not null) is var dupes and > 0) body.Children.Add(Warning(string.Format(Loc.T("Confirm.WarnDupes"), dupes)));
        if (units.Count(u => u.OriginUnknown) is var unknown and > 0) body.Children.Add(Warning(string.Format(Loc.T("Confirm.WarnUnknown"), unknown)));
        if (units.Any(u => u.Approximate)) body.Children.Add(Warning(Loc.T("Note.Hardlinks")));
        body.Children.Add(Warning(string.Format(Loc.T("Confirm.WarnSpace"), Format.Date(DateTime.UtcNow.AddDays(days)))));

        var r = await Show(string.Format(Loc.T("Confirm.Title"), units.Count), body,
            (string.Format(Loc.T("Confirm.Move"), units.Count, total), true), (Loc.T("Common.Cancel"), false));
        if (r != 0) return;
        var skipped = await Model.I.MoveSelectedAsync();
        if (skipped.Count > 0)
            await Show(Loc.T("Move.Skipped"), Text(string.Join("\n", skipped)), (Loc.T("Common.Ok"), true));
        if (skipped.Count < units.Count) W?.Navigate("quarantine");
    }

    /// <summary>"C:" and the quarantine root on its volume.</summary>
    static (string Volume, string Root) RootOf(Unit u)
    {
        if (Args.Current.Demo) return ("C:", DemoData.QuarantineRoot);
        try
        {
            var real = PathGuard.Real(u.Parts[0].Path);
            return (Native.VolumeOf(real).TrimEnd('\\'), Quarantine.RootFor(real));
        }
        catch { return ("?", "?"); }
    }

    static string Markdown(List<Unit> units)
    {
        var sb = new StringBuilder("| Item | Path | Size | Last used |\n|---|---|---:|---|\n");
        foreach (var u in units) sb.Append($"| {u.Title} | `{u.Path}` | {u.SizeText} | {u.UsedText} ({u.SourceText}) |\n");
        sb.Append($"| **Total** | | **{Format.Size(units.Sum(u => u.Bytes))}** | |\n");
        return sb.ToString();
    }

    /// <summary>Long paths are cut in the middle; the tooltip has the whole path.</summary>
    public static string Short(string p) => p.Length <= 90 ? p : p[..40] + "…" + p[^48..];

    /// <summary>
    /// --demo --page tray: the tray menu drawn in the window corner. A real tray menu lives outside the window and can't be captured,
    /// and a ContextMenu can't sit in the visual tree, so these are the same items as plain rows.
    /// </summary>
    public static void TrayPreview()
    {
        if (W?.Content is not Grid root) return;
        var rows = new StackPanel { Margin = new(4) };
        void Row(string text) => rows.Children.Add(new TextBlock { Text = text, Padding = new(12, 7, 24, 7) });
        void Line() => rows.Children.Add(new Separator { Margin = new(0, 3, 0, 3) });
        Row(Loc.T("Tray.Open"));
        Line();
        foreach (var i in TrayMenu.Items()) Row((string)((System.Windows.Controls.MenuItem)i).Header);
        Line();
        Row(Loc.T("Tray.Exit"));
        var menu = new Border
        {
            Child = rows, CornerRadius = new(8), BorderThickness = new(1), MinWidth = 260, Margin = new(0, 0, 24, 24),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.35 },
        };
        menu.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        menu.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        Grid.SetRowSpan(menu, 3);
        root.Children.Add(menu);
    }
}
