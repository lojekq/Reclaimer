using System.Diagnostics;
using System.IO;
using System.Windows;
using Reclaimer.Demo;
using Reclaimer.Features;
using Reclaimer.Shell;

namespace Reclaimer;

public partial class App : Application
{
    /// <summary>Synchronous hook before exit: tray Exit, Windows session end, crash handler (best effort, before the error box).</summary>
    public static event Action? Exiting;

    /// <summary>Asked on a user exit (tray, restart); return false to stay. May ask the user.</summary>
    public static Func<bool>? ConfirmExit;

    /// <summary>"Remove data &amp; autostart" and uninstall: undo what the app registered and return null, or return why not (shown to the user).
    /// Set it before Installer.HandleAsync: --uninstall calls it there.</summary>
    public static Func<string?>? RemoveData;

    /// <summary>Installer turns autostart on for a fresh install: Reclaimer's autoclean runs in the tray app (07 §2).</summary>
    public static bool AutostartOnInstall = true;

    static MainWindow? window;
    static bool quitting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var a = Args.Current;
        Crash.Install(this); // Settings, Loc and the Debug sandbox variables are already set in Program.Main
        // App hooks that --uninstall needs go here, before the installer: uninstalling must refuse while the quarantine holds data
        RemoveData = RemoveQuarantine;
        if (await Installer.HandleAsync()) // install, update, switch to the installed copy or uninstall: this process is done
        {
            Shutdown();
            return;
        }
        if (!SingleInstance.Acquire(() => Dispatcher.BeginInvoke(() => ShowMain(null)), () => Dispatcher.BeginInvoke(() => Quit(confirm: false))))
        {
            Shutdown();
            return;
        }
        Log.Info($"start {AppInfo.Version}{(a.Unknown.Count > 0 ? $", {a.Unknown.Count} unknown argument(s)" : "")}");

        if (a.Demo) DemoData.Fill();
        else if (Install.IsInstalled) Installer.InstalledStart(AutostartOnInstall); // Debug builds and exes elsewhere write nothing by themselves

        // App features: settings section, quarantine hooks, tray items
        SettingsPage.AppSection = SettingsSection.Create;
        Exiting += Model.I.Stop;

        window = new MainWindow();
        if (a.Width > 0) (window.Width, window.Height) = (a.Width, a.Height);
        Theme.Apply(window, Settings.Current.Theme);
        window.Closing += (_, ce) =>
        {
            if (quitting) return;
            ce.Cancel = true; // the cross hides to the tray
            window.Hide();
            if (Settings.Current.TrayHintShown) return;
            Settings.Current.TrayHintShown = true;
            Settings.Save();
            Tray.Notify(AppInfo.DisplayName, Loc.T("Tray.Hidden"));
        };
        Capture.Attach(window, () => { if (a.Autoplay) Autoplay.Start(window); });

        if (a.Screenshot is null)
        {
            Tray.Create(ShowMain, () => Quit(), TrayMenu.Items());
            Model.I.QuarantineChanged += TrayMenu.Update;
            TrayMenu.Update();
        }
        Model.I.Start();
        SessionEnding += (_, _) => RaiseExiting();
        Updates.Found += (version, url) =>
        {
            window.ShowUpdate(version, url);
            if (Settings.Current.NotifiedVersion == version) return;
            Settings.Current.NotifiedVersion = version;
            Settings.Save();
            Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Update.Message"), version), "about");
        };
        Updates.Start();

        if (!a.Background) ShowMain(a.Page);
    }

    public static void ShowMain(string? page)
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (page is not null) window.Navigate(page);
    }

    /// <summary>"Remove data &amp; autostart" would delete the journal with the data: refused until the quarantine is empty (07 §8.12).</summary>
    static string? RemoveQuarantine()
    {
        if (Args.Current.Demo) return string.Format(Loc.T("Remove.NotEmpty"), Model.I.QuarantineText);
        var entries = Quarantine.List();
        if (entries.Count > 0) return string.Format(Loc.T("Remove.NotEmpty"), Format.Size(entries.Sum(e => e.TotalBytes)));
        foreach (var root in Quarantine.KnownRoots().Where(Directory.Exists))
            try
            {
                File.Delete(Path.Combine(root, "README.txt"));
                Directory.Delete(root); // empty by now; fails harmlessly if not
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static void RaiseExiting()
    {
        var hook = Exiting;
        Exiting = null; // once, whoever comes first
        foreach (var h in hook?.GetInvocationList() ?? [])
            try { ((Action)h)(); }
            catch (Exception e) { Log.Error("Exiting hook failed", e); } // the other hooks still run
    }

    public static void Quit(bool confirm = true)
    {
        if (confirm && ConfirmExit?.Invoke() == false) return;
        quitting = true;
        RaiseExiting();
        Tray.Dispose();
        Log.Info("exit");
        Current.Shutdown();
    }

    /// <summary>After a language change. The mutex is released first, so the new process does not just wake this one.</summary>
    public static void Restart()
    {
        if (ConfirmExit?.Invoke() == false) return;
        SingleInstance.Release();
        Process.Start(AppInfo.ExePath);
        Quit(confirm: false);
    }
}
