using System.IO;
using System.Text;
using Microsoft.Win32;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>"Export report" (v1.0): the findings, the WSL/Docker disks and the quarantine as one Markdown file.</summary>
public static class Report
{
    public static string Markdown(IReadOnlyCollection<Unit> units, IReadOnlyCollection<Disk> disks, IReadOnlyCollection<Entry> entries, DateTime now)
    {
        var sb = new StringBuilder();
        sb.Append($"# {string.Format(Loc.T("Report.Title"), now.ToString("yyyy-MM-dd HH:mm"))}\n\n");
        foreach (var g in units.GroupBy(u => u.Category).OrderBy(g => g.Key))
        {
            sb.Append($"## {Loc.T("Category." + g.Key)} · {Format.Size(g.Sum(u => u.Bytes))}\n\n");
            sb.Append($"| {Loc.T("Findings.Name")} | {Loc.T("Report.Path")} | {Loc.T("Findings.Size")} | {Loc.T("Findings.Used")} | {Loc.T("Findings.Status")} | {Loc.T("Findings.Rebuild")} |\n|---|---|---:|---|---|---|\n");
            foreach (var u in g.OrderByDescending(u => u.Bytes))
                sb.Append($"| {Cell(u.Title)} | `{Cell(u.Path)}` | {u.SizeText} | {u.UsedText} ({u.SourceText}) | {Cell(u.StatusText)} | {Cell(u.RebuildText)}{(u.Origin is not null ? $" ({Cell(u.Origin)})" : u.OriginUnknown ? $" ({Loc.T("Rebuild.Unknown")})" : "")} |\n");
            sb.Append('\n');
        }
        if (disks.Count > 0)
        {
            sb.Append($"## {Loc.T("Page.Disks")} · {Format.Size(disks.Sum(d => d.Bytes))}\n\n");
            sb.Append($"| {Loc.T("Findings.Name")} | {Loc.T("Report.Path")} | {Loc.T("Findings.Size")} | {Loc.T("Disks.Sparse")} | |\n|---|---|---:|---|---|\n");
            foreach (var d in disks) sb.Append($"| {Cell(d.Name)} | `{Cell(d.Path)}` | {d.SizeText} | {d.SparseText} | {d.RunningText} |\n");
            sb.Append('\n');
        }
        sb.Append($"## {Loc.T("Page.Quarantine")} · {Format.Size(entries.Sum(e => e.TotalBytes))}\n\n");
        foreach (var e in entries) sb.Append($"- {Cell(e.Title)} — {e.SizeText}, {e.LeftText}\n");
        return sb.ToString();
    }

    static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    /// <summary>Asks where to save. Under --demo nothing is written (00-shared §2.3).</summary>
    public static void Export()
    {
        var d = new SaveFileDialog { FileName = $"reclaimer-report-{DateTime.Now:yyyyMMdd}.md", Filter = "Markdown|*.md", DefaultExt = ".md" };
        if (d.ShowDialog() != true || Args.Current.Demo) return;
        var m = Model.I;
        try { File.WriteAllText(d.FileName, Markdown(m.Units, m.Disks, m.Entries, DateTime.Now)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"report not saved: {Fs.Describe(e)}"); }
    }
}
