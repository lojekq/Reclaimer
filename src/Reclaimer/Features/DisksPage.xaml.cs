using System.IO;
using System.Windows;
using System.Windows.Controls;
using Reclaimer.Shell;

namespace Reclaimer.Features;

public partial class DisksPage
{
    public DisksPage()
    {
        InitializeComponent();
        DataContext = Model.I;
        Loaded += (_, _) => EmptyText.Visibility = Model.I.Disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PruneBuildButton.Click += (_, _) => _ = Prune(buildCache: true);
        PruneImagesButton.Click += (_, _) => _ = Prune(buildCache: false);
    }

    /// <summary>No quarantine for Docker: the dialog says so, and only the build cache or dangling images go.</summary>
    static async Task Prune(bool buildCache)
    {
        var cmd = buildCache ? "docker builder prune -f" : "docker image prune -f";
        if (await Dialog.Show(Loc.T("Docker.ConfirmTitle"), Dialog.Text(string.Format(Loc.T("Docker.ConfirmText"), cmd)),
                (Loc.T(buildCache ? "Docker.PruneBuild" : "Docker.PruneImages"), true), (Loc.T("Common.Cancel"), false)) != 0) return;
        await Model.I.PruneDockerAsync(buildCache);
    }

    /// <summary>The compaction wizard (v1.0): what will happen, Docker Desktop closed by the user, a second "yes" for wsl --shutdown.</summary>
    async void Compact_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Disk d) return;
        var wsl = d.Name != VhdxScanner.DockerName;
        var fstrim = new CheckBox { Content = Loc.T("Compact.Fstrim"), IsChecked = wsl, IsEnabled = wsl, Margin = new(0, 8, 0, 0) };
        var body = new StackPanel();
        body.Children.Add(Dialog.Text(string.Format(Loc.T("Compact.Intro"), d.Name, d.SizeText)));
        body.Children.Add(Dialog.Text(Loc.T("Compact.Steps"), secondary: true));
        body.Children.Add(fstrim);
        if (await Dialog.Show(Loc.T("Compact.Title"), body, (Loc.T("Compact.Continue"), true), (Loc.T("Common.Cancel"), false)) != 0) return;

        while (!Args.Current.Demo && OwnerCheck.Running([VhdxScanner.DockerName]) is not null)
            if (await Dialog.Show(Loc.T("Compact.CloseDockerTitle"), Dialog.Text(Loc.T("Compact.CloseDocker")),
                    (Loc.T("Findings.Recheck"), true), (Loc.T("Common.Cancel"), false)) != 0) return;

        if (await Dialog.Show(Loc.T("Compact.ShutdownTitle"), Dialog.Warning(Loc.T("Compact.ShutdownText")),
                (Loc.T("Compact.Go"), true), (Loc.T("Common.Cancel"), false)) != 0) return;

        if (Args.Current.Demo)
        {
            await Dialog.Show(Loc.T("Compact.Done"), Dialog.Text(string.Format(Loc.T("Compact.Result"), d.SizeText, Format.Size(d.Bytes * 2 / 3), Format.Size(d.Bytes / 3))), (Loc.T("Common.Ok"), true));
            return;
        }
        var before = d.Bytes;
        var why = await Task.Run(() => VhdxCompact.Steps(d, fstrim.IsChecked == true));
        var after = File.Exists(d.Path) ? new FileInfo(d.Path).Length : before;
        await Dialog.Show(Loc.T(why is null ? "Compact.Done" : "Compact.FailedTitle"),
            Dialog.Text(why is null ? string.Format(Loc.T("Compact.Result"), Format.Size(before), Format.Size(after), Format.Size(Math.Max(0, before - after))) : Loc.T(why)),
            (Loc.T("Common.Ok"), true));
        if (why is null) _ = Model.I.ScanAsync(); // sizes changed; a scan only reads
    }
}
