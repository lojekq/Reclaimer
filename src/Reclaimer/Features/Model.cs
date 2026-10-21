using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Reclaimer.Demo;
using Reclaimer.Shell;
using Wpf.Ui.Controls;

namespace Reclaimer.Features;

public sealed record VolumeView(string Name, double UsedPercent, string Text, string QuarantineText);

public sealed record TileView(Category Category, string Title, string Sum, string Count)
{
    public IconElement Icon => new SymbolIcon(Category switch
    {
        Category.Models => SymbolRegular.BrainCircuit24, Category.Caches => SymbolRegular.BoxMultiple24,
        Category.Projects => SymbolRegular.Code24, _ => SymbolRegular.Storage24,
    });
}

/// <summary>What the pages show and the actions behind their buttons. One instance; pages bind to Model.I.</summary>
public sealed class Model : INotifyPropertyChanged
{
    public static Model I { get; } = new();

    public ObservableCollection<Unit> Units { get; } = [];
    public ObservableCollection<Entry> Entries { get; } = [];
    public ObservableCollection<Disk> Disks { get; } = [];
    public ObservableCollection<VolumeView> Volumes { get; } = [];
    public ObservableCollection<TileView> Tiles { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];
    public ObservableCollection<DockerRow> DockerRows { get; } = [];

    string dockerText = "";
    public string DockerText { get => dockerText; set { dockerText = value; Changed(); } }

    bool scanning;
    public bool Scanning { get => scanning; private set { scanning = value; Changed(); Changed(nameof(Idle)); } }
    public bool Idle => !scanning && !busy;

    bool busy;
    public bool Busy { get => busy; private set { busy = value; Changed(); Changed(nameof(Idle)); } }

    string scanText = "";
    public string ScanText { get => scanText; private set { scanText = value; Changed(); } }

    public string SelectedText => string.Format(Loc.T("Findings.Selected"), Units.Count(u => u.Selected), Format.Size(Units.Where(u => u.Selected).Sum(u => u.Bytes)));
    public bool AnySelected => Units.Any(u => u.Selected);
    public long QuarantineBytes => Entries.Sum(e => e.TotalBytes);
    public string QuarantineText => Format.Size(QuarantineBytes);

    /// <summary>Raised after the quarantine list changed (tray tooltip and menu follow it).</summary>
    public event Action? QuarantineChanged;

    CancellationTokenSource? scanCts;
    readonly CancellationTokenSource exit = new();
    Task? work = Task.CompletedTask;

    Model()
    {
        Units.CollectionChanged += (_, e) =>
        {
            foreach (Unit u in e.NewItems ?? Array.Empty<Unit>()) u.PropertyChanged += UnitChanged;
            SelectionChanged();
        };
    }

    void UnitChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Unit.Selected)) SelectionChanged();
    }

    void SelectionChanged()
    {
        Changed(nameof(SelectedText));
        Changed(nameof(AnySelected));
    }

    // ------------------------------------------------------------ start / exit

    /// <summary>After the window exists: reconcile the quarantine, list it, start the autoclean timer.</summary>
    public void Start()
    {
        if (Args.Current.Demo) return; // DemoData fills everything; nothing real runs
        work = Task.Run(() =>
        {
            Quarantine.Reconcile();
            OnUi(() => { RefreshQuarantine(); RefreshVolumes(); });
        });
        _ = AutoCleanLoop();
    }

    /// <summary>App.Exiting: stop the scan and the purge between files; a move in progress finishes its renames.</summary>
    public void Stop()
    {
        scanCts?.Cancel();
        exit.Cancel();
        try { work?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    // ------------------------------------------------------------ scan

    public async Task ScanAsync()
    {
        if (Scanning || Busy) return;
        Scanning = true;
        scanCts = CancellationTokenSource.CreateLinkedTokenSource(exit.Token);
        Units.Clear();
        Disks.Clear();
        ScanText = Loc.T("Scan.Running");
        try
        {
            if (Args.Current.Demo) await DemoData.FakeScan(scanCts.Token);
            else
            {
                var disks = await Task.Run(() => Scan.Run(
                    u => OnUi(() => Units.Add(u)),
                    step => OnUi(() => ScanText = string.Format(Loc.T("Scan.Step"), step)),
                    scanCts.Token));
                foreach (var d in disks) Disks.Add(d);
                await RefreshDocker();
                Settings.Current.LastScanUtc = DateTime.UtcNow;
                Settings.Save();
            }
            ScanText = string.Format(Loc.T("Scan.Done"), Units.Count, Format.Size(Units.Sum(u => u.Bytes)));
        }
        catch (OperationCanceledException) { ScanText = Loc.T("Scan.Cancelled"); }
        catch (Exception e)
        {
            Log.Error($"scan: {Fs.Describe(e)}");
            ScanText = Loc.T("Scan.Failed");
        }
        finally
        {
            Scanning = false;
            RefreshTiles();
        }
    }

    public void CancelScan() => scanCts?.Cancel();

    async Task RefreshDocker()
    {
        var df = await Task.Run(Docker.Df);
        DockerRows.Clear();
        foreach (var r in df ?? []) DockerRows.Add(r);
        DockerText = df is null ? Loc.T("Docker.None") : "";
    }

    /// <summary>Build cache or dangling images: deleted for good, no quarantine (the button says so and asks first).</summary>
    public async Task PruneDockerAsync(bool buildCache)
    {
        if (Busy) return;
        if (Args.Current.Demo) { DockerText = string.Format(Loc.T("Docker.Reclaimed"), buildCache ? "7.8GB" : "9.1GB"); return; }
        Busy = true;
        try
        {
            var total = await Task.Run(() => Docker.Prune(buildCache));
            await RefreshDocker();
            DockerText = total is null ? Loc.T("Docker.Failed") : string.Format(Loc.T("Docker.Reclaimed"), total.Length > 0 ? total : "0B");
        }
        finally { Busy = false; }
    }

    /// <summary>"Check again" after the user closed Ollama: owners only, no rescan.</summary>
    public void RecheckOwners()
    {
        if (Args.Current.Demo) return;
        var names = OwnerCheck.ProcessNames();
        foreach (var u in Units.Where(u => u.Status is UnitStatus.OwnerRunning or UnitStatus.Ready))
        {
            var o = OwnerCheck.Running(u.Owners, names);
            (u.Status, u.Owner) = o is null ? (UnitStatus.Ready, null) : (UnitStatus.OwnerRunning, o);
        }
    }

    /// <summary>Everything older than the threshold except models: models are only ever selected by hand (07 §8.9).</summary>
    public void MarkRecommended(IEnumerable<Unit> visible)
    {
        var age = Settings.Current.AgeDays;
        foreach (var u in visible)
            u.Selected = u.Category != Category.Models && u.CanSelect && u.AgeDays >= age;
    }

    // ------------------------------------------------------------ actions

    /// <summary>The selected units, one after another on a background task. Returns the lines for the units that were skipped.</summary>
    public async Task<List<string>> MoveSelectedAsync()
    {
        var chosen = Units.Where(u => u.Selected).ToList();
        var skipped = new List<string>();
        if (chosen.Count == 0 || Busy) return skipped;
        Busy = true;
        try
        {
            var days = Math.Clamp(Settings.Current.QuarantineDays, 1, 30);
            foreach (var u in chosen)
            {
                if (exit.IsCancellationRequested) break; // exit between units; the current one finishes its renames
                string? why;
                if (Args.Current.Demo) why = DemoData.Move(u, days);
                else
                {
                    var t = Task.Run(() => Quarantine.Move(u, days));
                    work = t;
                    why = await t;
                }
                u.Selected = false;
                if (why is null) Units.Remove(u);
                else skipped.Add($"{u.Title}: {Reason(why, u)}");
            }
        }
        finally
        {
            Busy = false;
            RefreshQuarantine();
            RefreshTiles();
        }
        return skipped;
    }

    public async Task<string?> RestoreAsync(Entry e, bool beside = false)
    {
        if (Args.Current.Demo) { Entries.Remove(e); QuarantineChanged?.Invoke(); return null; }
        var why = await Task.Run(() => Quarantine.Restore(e, beside));
        RefreshQuarantine();
        return why;
    }

    public async Task<string?> PurgeAsync(IEnumerable<Entry> entries)
    {
        var list = entries.ToList();
        if (Args.Current.Demo) { foreach (var e in list) Entries.Remove(e); QuarantineChanged?.Invoke(); return null; }
        Busy = true;
        try
        {
            var t = Task.Run(() => PurgeNow(list, exit.Token));
            work = t;
            var (freed, errors) = await t;
            if (freed > 0) Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Notify.Freed"), Format.Size(freed)));
            return errors.FirstOrDefault();
        }
        finally
        {
            Busy = false;
            RefreshQuarantine();
            RefreshVolumes();
        }
    }

    static (long Freed, List<string> Errors) PurgeNow(List<Entry> list, CancellationToken ct)
    {
        long freed = 0;
        var errors = new List<string>();
        foreach (var e in list)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var (f, why) = Quarantine.Purge(e, ct);
                freed += f;
                if (why is not null) errors.Add(why);
            }
            catch (OperationCanceledException) { break; } // stays Purging, finished on the next start
            catch (Exception x) when (x is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"purge: {Fs.Describe(x)}");
                errors.Add("Purge.Failed");
            }
        }
        return (freed, errors);
    }

    /// <summary>30 s after start, then every 6 h; postponed by 15 min while a fullscreen game or presentation runs. Below-normal priority.</summary>
    async Task AutoCleanLoop()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), exit.Token);
            while (true)
            {
                var busyNow = UserBusy.Now;
                if (busyNow) Log.Info("autoclean postponed: fullscreen");
                else
                {
                    var t = Task.Run(() =>
                    {
                        var thread = Thread.CurrentThread;
                        var old = thread.Priority;
                        thread.Priority = ThreadPriority.BelowNormal;
                        try { return PurgeNow(Quarantine.List().Where(e => e.IsExpired(DateTime.UtcNow)).ToList(), exit.Token); }
                        finally { thread.Priority = old; }
                    });
                    work = t;
                    var (freed, _) = await t;
                    if (freed > 0)
                        OnUi(() =>
                        {
                            Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Notify.Freed"), Format.Size(freed)), "quarantine");
                            RefreshQuarantine();
                            RefreshVolumes();
                        });
                }
                if (!busyNow) await RemindIfDue();
                await Task.Delay(busyNow ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(6), exit.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Error($"autoclean: {Fs.Describe(e)}"); }
    }

    /// <summary>
    /// Weekly reminder (v1.0, off by default): a quiet read-only scan, then "≈N GB can be reclaimed" if it is 1 GB or more.
    /// Counts what "Mark recommended" would pick plus old models, i.e. everything selectable and older than the threshold.
    /// </summary>
    async Task RemindIfDue()
    {
        var s = Settings.Current;
        if (!s.RemindWeekly || s.LastReminderUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromDays(7)) return;
        long sum = 0;
        // ponytail: the scan runs at normal priority on pool threads; fine once a week, lower it if someone notices
        await Task.Run(() => Scan.Run(u => { if (u.CanSelect && u.AgeDays >= s.AgeDays) Interlocked.Add(ref sum, u.Bytes); }, _ => { }, exit.Token));
        s.LastReminderUtc = DateTime.UtcNow;
        Settings.Save();
        if (sum >= 1L << 30) Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Notify.Remind"), Format.Size(sum)), "findings");
    }

    // ------------------------------------------------------------ views

    public void RefreshQuarantine()
    {
        if (!Args.Current.Demo)
        {
            Entries.Clear();
            try { foreach (var e in Quarantine.List().OrderByDescending(e => e.CreatedUtc)) Entries.Add(e); }
            catch (Exception x) { Log.Error($"quarantine list: {Fs.Describe(x)}"); }
        }
        Changed(nameof(QuarantineBytes));
        Changed(nameof(QuarantineText));
        QuarantineChanged?.Invoke();
    }

    public void RefreshHistory()
    {
        if (Args.Current.Demo) return;
        History.Clear();
        foreach (var r in Quarantine.ReadHistory()) History.Add(r);
    }

    public void RefreshVolumes()
    {
        if (Args.Current.Demo) return;
        Volumes.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var mine = Entries.Where(e => Paths.IsUnder(d.Name, e.Dir)).ToList();
            Volumes.Add(View(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.TotalFreeSpace, mine.Sum(e => e.TotalBytes),
                mine.Count > 0 ? mine.Min(e => e.ExpiresUtc) : null));
        }
    }

    public static VolumeView View(string name, string label, long total, long free, long quarantined, DateTime? firstExpiry)
    {
        var used = total - free;
        return new(
            label.Length > 0 ? $"{name} {label}" : name,
            total > 0 ? 100.0 * used / total : 0,
            string.Format(Loc.T("Overview.Volume"), Format.Size(free), Format.Size(total)),
            quarantined > 0 && firstExpiry is { } x ? string.Format(Loc.T("Overview.VolumeQuarantine"), Format.Size(quarantined), Format.Date(x)) : "");
    }

    public void RefreshTiles()
    {
        Tiles.Clear();
        foreach (var c in new[] { Category.Models, Category.Caches, Category.Projects })
        {
            var units = Units.Where(u => u.Category == c && u.Status != UnitStatus.ReportOnly).ToList();
            Tiles.Add(new(c, Loc.T("Category." + c), Units.Count == 0 ? "—" : "≈ " + Format.Size(units.Sum(u => u.Bytes)), string.Format(Loc.T("Overview.Count"), units.Count)));
        }
        Tiles.Add(new(Category.Disks, Loc.T("Category.Disks"), Disks.Count == 0 ? "—" : Format.Size(Disks.Sum(d => d.Bytes)), string.Format(Loc.T("Overview.Report"), Disks.Count)));
    }

    /// <summary>A reason key as text, with the owner's name where it needs one.</summary>
    public static string Reason(string key, Unit? u = null) =>
        key == "Status.OwnerRunning" ? string.Format(Loc.T("Status.OwnerRunning"), u?.Owner) : Loc.T(key);

    static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
}
