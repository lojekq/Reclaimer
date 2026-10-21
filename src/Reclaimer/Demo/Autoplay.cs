using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Reclaimer.Features;

namespace Reclaimer.Demo;

/// <summary>
/// --autoplay for gif.ps1 (07 §10): Scan → progress → "older than 90 days" → tick 3 rows (the sum grows) → Move… → confirm →
/// Quarantine with three "7 days left" entries → Restore one → it disappears.
/// </summary>
public static class Autoplay
{
    public static void Start(MainWindow w)
    {
        var old = new[] { @"old-telegram-bot\node_modules", @"rust-raytracer\target", @"shop-api\bin + obj" };
        // Seconds after the window is on screen; gif.ps1 starts recording when --rect-file appears, ffmpeg needs ~1 s
        (double At, Action Do)[] steps =
        [
            (0.0, () => w.Navigate("findings")),
            (1.5, () => _ = Model.I.ScanAsync()),
            (3.6, () => Page<FindingsPage>(w)?.SetAge(90)),
            (4.3, () => Pick(old[0])),
            (4.7, () => Pick(old[1])),
            (5.1, () => Pick(old[2])),
            (5.7, () => _ = Dialog.Confirm()),
            (7.0, () => ClickPrimary(w)),
            (9.3, () => { if (Model.I.Entries.Count > 1) _ = Model.I.RestoreAsync(Model.I.Entries[1]); }),
        ];
        var started = DateTime.UtcNow;
        var next = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            while (next < steps.Length && (DateTime.UtcNow - started).TotalSeconds >= steps[next].At) steps[next++].Do();
            if (next == steps.Length) timer.Stop();
        };
        timer.Start();
    }

    static void Pick(string title)
    {
        if (Model.I.Units.FirstOrDefault(u => u.Title == title) is { } u) u.Selected = true;
    }

    /// <summary>The primary button of the open dialog, clicked the way a mouse would.</summary>
    static void ClickPrimary(MainWindow w)
    {
        if (Find<Wpf.Ui.Controls.Button>(w.DialogContent).FirstOrDefault(b => b.Appearance == Wpf.Ui.Controls.ControlAppearance.Primary) is { } b)
            b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    static T? Page<T>(DependencyObject root) where T : DependencyObject => Find<T>(root).FirstOrDefault();

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }
}
