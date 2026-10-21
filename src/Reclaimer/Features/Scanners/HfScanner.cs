using System.IO;
using System.IO.Enumeration;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>
/// Hugging Face hub: a unit per models--/datasets--/spaces-- repo. Files in the shared store (hub\blobs\&lt;prefix&gt;\&lt;hash&gt;)
/// belong to a repo only when its symlinks are the only ones pointing at them; then they move with it, with their .refs.
/// Counted from the real symlinks of every repo, not from the .refs format. Without symlinks (no developer mode) the files
/// sit in snapshots\ and the repo folder is everything.
/// </summary>
public static class HfScanner
{
    public static List<Unit> Scan(string hub, Location l, CancellationToken ct)
    {
        var repos = Directory.EnumerateDirectories(hub, "*", Fs.Flat)
            .Where(d => Path.GetFileName(d) is var n && (n.StartsWith("models--") || n.StartsWith("datasets--") || n.StartsWith("spaces--")))
            .ToList();
        var linked = repos.ToDictionary(r => r, r => SharedTargets(hub, r), StringComparer.OrdinalIgnoreCase);
        var users = linked.Values.SelectMany(t => t).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var units = new List<Unit>();
        foreach (var repo in repos)
        {
            ct.ThrowIfCancellationRequested();
            var own = linked[repo].Where(t => users[t] == 1 && File.Exists(t)).ToList();
            var shared = linked[repo].Where(t => users[t] > 1 && File.Exists(t)).ToList();
            var parts = new List<Part>();
            foreach (var f in own)
            {
                parts.Add(new(f, false, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)));
                if (File.Exists(f + ".refs")) parts.Add(new(f + ".refs", false, new FileInfo(f + ".refs").Length, File.GetLastWriteTimeUtc(f + ".refs")));
            }
            var stats = Fs.Measure(repo);
            parts.Add(new(repo, true, stats.Bytes, Directory.GetLastWriteTimeUtc(repo)));
            foreach (var f in linked[repo].Where(File.Exists)) stats = Newer(stats, Fs.Measure(f)); // reading a model through its link touches the blob
            var (at, source) = Fs.LastUsed(stats);
            var notes = new List<string>();
            if (shared.Count > 0) notes.Add(string.Format(Loc.T("Note.HfShared"), Format.Size(shared.Sum(f => new FileInfo(f).Length))));
            units.Add(new Unit
            {
                Id = $"{l.Id}|{repo}", Location = l.Id, Category = l.Category, Title = Path.GetFileName(repo), Root = hub,
                Parts = parts, Bytes = parts.Sum(p => p.Bytes), LastUsed = at, Source = source, Owners = l.Owners, Rebuild = l.Rebuild, Notes = notes,
            });
        }
        return units;
    }

    /// <summary>Files outside the repo but inside the hub that the repo's snapshot symlinks point at.</summary>
    static HashSet<string> SharedTargets(string hub, string repo)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = Path.Combine(repo, "snapshots");
        if (!Directory.Exists(snapshots)) return set;
        var links = new FileSystemEnumerable<string?>(snapshots, (ref FileSystemEntry e) =>
            (e.Attributes & FileAttributes.ReparsePoint) != 0 ? e.ToFullPath() : null,
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };
        foreach (var link in links)
        {
            if (link is null || new FileInfo(link).LinkTarget is not { } target) continue;
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, target));
            if (Paths.IsUnder(hub, full) && !Paths.IsUnder(repo, full)) set.Add(full);
        }
        return set;
    }

    static Stats Newer(Stats a, Stats b) => new(a.Bytes, a.Modified > b.Modified ? a.Modified : b.Modified, a.Accessed > b.Accessed ? a.Accessed : b.Accessed);
}
