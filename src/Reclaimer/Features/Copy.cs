using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>"Copy name" / "Copy path" / "Copy source" on a row (context menu; Ctrl+C copies the name of the selected row).</summary>
public static class Copy
{
    public static void Text(string s)
    {
        try { Clipboard.SetText(s); }
        catch (Exception e) { Log.Warn($"clipboard: {Fs.Describe(e)}"); }
    }

    /// <summary><paramref name="of"/> maps a row's item to its name, path and source (null source = no such item); null for anything else.</summary>
    public static void Attach(Selector list, Func<object?, (string Name, string Path, string? Source)?> of)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item(Loc.T("Copy.Name"), v => v.Name));
        menu.Items.Add(Item(Loc.T("Copy.Path"), v => v.Path));
        var source = Item(Loc.T("Copy.Source"), v => v.Source ?? "");
        menu.Items.Add(source);
        list.ContextMenu = menu; // on the list, not in a row style: WPF-UI's row and item styles stay as they are
        list.PreviewMouseRightButtonDown += (_, e) => Select(e.OriginalSource as DependencyObject); // the row under the mouse is the one the menu is about
        list.ContextMenuOpening += (_, e) =>
        {
            if (of(list.SelectedItem) is not { } v) { e.Handled = true; return; } // no row: no menu
            source.Visibility = v.Source is null ? Visibility.Collapsed : Visibility.Visible;
        };
        list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control || of(list.SelectedItem) is not { } v) return;
            Text(v.Name);
            e.Handled = true;
        };

        MenuItem Item(string header, Func<(string Name, string Path, string? Source), string> pick)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => { if (of(list.SelectedItem) is { } v) Text(pick(v)); };
            return m;
        }
    }

    static void Select(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is DataGridRow r) { r.IsSelected = true; return; }
            if (d is ListBoxItem i) { i.IsSelected = true; return; }
        }
    }
}
