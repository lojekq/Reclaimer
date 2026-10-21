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
            if (Detached(hub, repo, l) is { } d) units.Add(d);
        }
        if (Incomplete(hub, l) is { } inc) units.Add(inc);
        return units;
    }

    /// <summary>Files outside the repo but inside the hub that the repo's snapshot symlinks point at.</summary>
    static HashSet<string> SharedTargets(string hub, string repo) =>
        Targets(Path.Combine(repo, "snapshots")).Where(t => Paths.IsUnder(hub, t) && !Paths.IsUnder(repo, t)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the file symlinks under a folder point, as full paths. Never follows a linked folder.</summary>
    static HashSet<string> Targets(string dir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return set;
        var links = new FileSystemEnumerable<string?>(dir, (ref FileSystemEntry e) =>
            (e.Attributes & FileAttributes.ReparsePoint) != 0 ? e.ToFullPath() : null,
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
        };
        foreach (var link in links)
            if (link is not null && new FileInfo(link).LinkTarget is { } target)
                set.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link)!, target)));
        return set;
    }

    /// <summary>
    /// Revisions no ref points at (an old "main" after an update): their snapshot folders plus the blobs only they use (v1.0).
    /// A repo without refs is left alone: there is no telling which revision is current.
    /// </summary>
    internal static Unit? Detached(string hub, string repo, Location l)
    {
        var refsDir = Path.Combine(repo, "refs");
        var snapsDir = Path.Combine(repo, "snapshots");
        if (!Directory.Exists(refsDir) || !Directory.Exists(snapsDir)) return null;
        var current = Directory.EnumerateFiles(refsDir, "*", Fs.Deep).Select(f => File.ReadAllText(f).Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snaps = Directory.GetDirectories(snapsDir, "*", Fs.Flat);
        var old = snaps.Where(d => !current.Contains(Path.GetFileName(d))).ToList();
        if (old.Count == 0 || old.Count == snaps.Length) return null; // nothing old, or no current revision on disk: keep it all
        var blobs = Path.Combine(repo, "blobs");
        var keep = snaps.Except(old).SelectMany(Targets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free = old.SelectMany(Targets).Where(t => Paths.IsUnder(blobs, t) && !keep.Contains(t) && File.Exists(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parts = old.Select(d => new Part(d, true, Fs.Size(d), Directory.GetLastWriteTimeUtc(d)))
            .Concat(free.Select(f => new Part(f, false, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)))).ToList();
        var stats = old.Select(Fs.Measure).Aggregate(Newer);
        var (at, source) = Fs.LastUsed(stats);
        return new Unit
        {
            Id = $"{l.Id}|detached|{repo}", Location = l.Id, Category = l.Category, Title = string.Format(Loc.T("Title.HfDetached"), Path.GetFileName(repo), old.Count),
            Root = hub, Path = snapsDir, Parts = parts, Bytes = parts.Sum(p => p.Bytes), LastUsed = at, Source = source, Owners = l.Owners, Rebuild = "Rebuild.Download",
        };
    }

    /// <summary>"*.incomplete" downloads untouched for a day: a download in progress is younger and holds its file open.</summary>
    internal static Unit? Incomplete(string hub, Location l)
    {
        var day = DateTime.UtcNow.AddDays(-1);
        var files = Directory.EnumerateFiles(hub, "*.incomplete", Fs.Deep).Select(f => new FileInfo(f)).Where(f => f.LastWriteTimeUtc < day).ToList();
        if (files.Count == 0) return null;
        return new Unit
        {
            Id = $"{l.Id}|incomplete|{hub}", Location = l.Id, Category = l.Category, Title = Loc.T("Title.HfIncomplete"), Root = hub, Path = hub,
            Parts = files.Select(f => new Part(f.FullName, false, f.Length, f.LastWriteTimeUtc)).ToList(), Bytes = files.Sum(f => f.Length),
            LastUsed = files.Max(f => f.LastWriteTimeUtc), Source = UsedSource.Modified, Owners = l.Owners, Rebuild = "Rebuild.Nothing",
        };
    }

    static Stats Newer(Stats a, Stats b) => new(a.Bytes, a.Modified > b.Modified ? a.Modified : b.Modified, a.Accessed > b.Accessed ? a.Accessed : b.Accessed);
}
