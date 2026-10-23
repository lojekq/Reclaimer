using System.Reflection;
using System.Windows.Controls;
using H.NotifyIcon;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>Tray: "Reclaimer: 12.4 GB in quarantine"; Open · Scan · Quarantine (12.4 GB) · Clean quarantine now… · Exit (07 §7).</summary>
public static class TrayMenu
{
    static readonly List<MenuItem> QuarantineItems = [];

    public static List<Control> Items()
    {
        var q = Tray.Item(Header(), () => App.ShowMain("quarantine"));
        QuarantineItems.Add(q);
        return
        [
            Tray.Item(Loc.T("Tray.Scan"), () => { App.ShowMain("findings"); _ = Model.I.ScanAsync(); }),
            q,
            Tray.Item(Loc.T("Tray.Clean"), () => { App.ShowMain("quarantine"); _ = QuarantinePage.CleanAll(); }),
        ];
    }

    public static void Update()
    {
        foreach (var i in QuarantineItems) i.Header = Header();
        // Shell's Tray keeps its TaskbarIcon private and the shell is frozen (00-shared §9.2): the tooltip is set through reflection
        if (typeof(Tray).GetField("icon", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is TaskbarIcon icon)
            icon.ToolTipText = Model.I.QuarantineBytes > 0 ? string.Format(Loc.T("Tray.Tooltip"), AppInfo.Title, Model.I.QuarantineText) : AppInfo.Title;
    }

    static string Header() => string.Format(Loc.T("Tray.Quarantine"), Model.I.QuarantineText);
}
