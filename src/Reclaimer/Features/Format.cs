using System.Globalization;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>Sizes (1024-based, like Explorer) and ages in the UI language: "58,3 ГБ", "214 days ago".</summary>
public static class Format
{
    static CultureInfo C => CultureInfo.CurrentUICulture;

    public static string Size(long bytes) => bytes switch
    {
        >= 1000L << 30 => string.Format(C, Loc.T("Size.TB"), bytes / (double)(1L << 40)),
        >= 1L << 30 => string.Format(C, Loc.T("Size.GB"), bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(C, Loc.T("Size.MB"), bytes / (double)(1L << 20)),
        _ => string.Format(C, Loc.T("Size.KB"), bytes == 0 ? 0 : Math.Max(1, bytes / 1024)),
    };

    public static string Ago(DateTime? utc)
    {
        if (utc is not { } t) return Loc.T("Age.Unknown");
        var days = (int)Math.Max(0, (DateTime.UtcNow - t).TotalDays);
        return days switch
        {
            0 => Loc.T("Age.Today"),
            1 => Loc.T("Age.Yesterday"),
            < 90 => string.Format(C, Loc.T("Age.Days"), days),
            < 730 => string.Format(C, Loc.T("Age.Months"), days / 30),
            _ => string.Format(C, Loc.T("Age.Years"), days / 365),
        };
    }

    public static string Date(DateTime utc) => utc.ToLocalTime().ToString("d MMM", C);
}
