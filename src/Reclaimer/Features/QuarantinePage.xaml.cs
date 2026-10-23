using System.IO;
using System.Windows;
using Reclaimer.Shell;

namespace Reclaimer.Features;

public partial class QuarantinePage
{
    public QuarantinePage()
    {
        InitializeComponent();
        DataContext = Model.I;
        CleanButton.Click += (_, _) => _ = CleanAll();
        Copy.Attach(List, o => o is Entry e ? (e.Title, e.Original, null) : null);
        Loaded += (_, _) => { Model.I.QuarantineChanged += Sync; Sync(); };
        Unloaded += (_, _) => Model.I.QuarantineChanged -= Sync;
    }

    void Sync()
    {
        var n = Model.I.Entries.Count;
        EmptyText.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanButton.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        Summary.Text = string.Format(Loc.T("Quarantine.Summary"), n, Model.I.QuarantineText, Settings.Current.QuarantineDays);
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Entry entry) return;
        var why = await Model.I.RestoreAsync(entry);
        if (why == "Restore.Conflict")
        {
            var name = Path.GetFileName(entry.Original) + ".restored";
            if (await Dialog.Show(Loc.T("Restore.ConflictTitle"), Dialog.Text(string.Format(Loc.T("Restore.ConflictText"), entry.Original)),
                    (string.Format(Loc.T("Restore.Beside"), name), true), (Loc.T("Common.Cancel"), false)) != 0) return;
            why = await Model.I.RestoreAsync(entry, beside: true);
        }
        if (why is not null) await Dialog.Show(Loc.T("Restore.Failed"), Dialog.Text(Model.Reason(why)), (Loc.T("Common.Ok"), true));
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Entry entry) return;
        if (await Dialog.Show(Loc.T("Delete.Title"), Dialog.Text(string.Format(Loc.T("Delete.Text"), entry.Title, entry.SizeText)),
                (Loc.T("Quarantine.Delete"), true), (Loc.T("Common.Cancel"), false)) != 0) return;
        if (await Model.I.PurgeAsync([entry]) is { } why) await Dialog.Show(Loc.T("Purge.Failed"), Dialog.Text(Model.Reason(why)), (Loc.T("Common.Ok"), true));
    }

    /// <summary>Also from the tray menu ("Clean quarantine now…"): always asks, there is no undo.</summary>
    public static async Task CleanAll()
    {
        var all = Model.I.Entries.Where(e => !e.IsBroken).ToList();
        if (all.Count == 0) return;
        if (await Dialog.Show(Loc.T("CleanAll.Title"), Dialog.Text(string.Format(Loc.T("CleanAll.Text"), all.Count, Format.Size(all.Sum(e => e.TotalBytes)))),
                (Loc.T("Quarantine.CleanAll"), true), (Loc.T("Common.Cancel"), false)) != 0) return;
        if (await Model.I.PurgeAsync(all) is { } why) await Dialog.Show(Loc.T("Purge.Failed"), Dialog.Text(Model.Reason(why)), (Loc.T("Common.Ok"), true));
    }
}
