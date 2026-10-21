using System.IO;
using System.Text;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>
/// Build folders in the user's project roots (07 §5): only next to their marker and, in git, only when git says they are ignored.
/// Depth ≤ 6, never into build folders, .git or reparse points. dist, build and out are never touched: users keep their own files there.
/// </summary>
public static class ProjectScanner
{
    static readonly HashSet<string> NoDescend = new(["node_modules", "target", "bin", "obj", ".venv", "venv", ".next", ".git", ".reclaimer-quarantine"], StringComparer.OrdinalIgnoreCase);

    public static List<Unit> Scan(string root, CancellationToken ct)
    {
        var units = new List<Unit>();
        var commits = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir, "*", Fs.Flat); } // Flat skips reparse points
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var names = subdirs.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (parts, marker, rebuild, owners) in Candidates(dir, names))
                units.Add(Make(root, dir, parts, marker, rebuild, owners, commits));
            if (depth >= 6) continue;
            foreach (var sub in subdirs)
                if (!NoDescend.Contains(Path.GetFileName(sub)) && !PathGuard.Excluded().Any(x => Paths.IsUnder(x, sub)))
                    stack.Push((sub, depth + 1));
        }
        return units;
    }

    /// <summary>The marker rules of 07 §5. Public for the tests.</summary>
    public static IEnumerable<(string[] Parts, string Marker, string Rebuild, string[] Owners)> Candidates(string dir, ISet<string?> names)
    {
        string P(string n) => Path.Combine(dir, n);
        if (names.Contains("node_modules") && File.Exists(P("package.json")))
            yield return ([P("node_modules")], "package.json", "Rebuild.Npm", []);
        if (names.Contains("target") && File.Exists(P("Cargo.toml")) && File.Exists(Path.Combine(dir, "target", "CACHEDIR.TAG")))
            yield return ([P("target")], "Cargo.toml", "Rebuild.Cargo", ["cargo", "rustc"]);
        if (names.Contains("bin") && names.Contains("obj") && File.Exists(Path.Combine(dir, "obj", "project.assets.json"))
            && (Fs.Any(dir, "*.csproj") || Fs.Any(dir, "*.fsproj") || Fs.Any(dir, "*.vbproj")))
            yield return ([P("bin"), P("obj")], "*.csproj", "Rebuild.Dotnet", []);
        foreach (var v in new[] { ".venv", "venv" })
            if (names.Contains(v) && File.Exists(Path.Combine(dir, v, "pyvenv.cfg")))
                yield return ([P(v)], "pyvenv.cfg", "Rebuild.Venv", []);
        if (names.Contains(".next") && Fs.Any(dir, "next.config.*"))
            yield return ([P(".next")], "next.config", "Rebuild.Next", []);
    }

    static Unit Make(string root, string dir, string[] paths, string marker, string rebuild, string[] owners, Dictionary<string, DateTime?> commits)
    {
        var parts = paths.Select(p => new Part(p, true, Fs.Size(p), Directory.GetLastWriteTimeUtc(p))).ToList();
        var notes = new List<string>();
        var status = UnitStatus.Ready;
        DateTime? commit = null;
        var top = GitTop(dir);
        if (top is null) notes.Add(Loc.T("Note.NoGit"));
        else
        {
            var ignored = paths.Select(p => Ignored(dir, Path.GetFileName(p))).ToList();
            if (ignored.Any(i => i is null)) notes.Add(Loc.T("Note.GitFailed")); // dubious ownership, no git.exe: by mtime, like no git
            else if (ignored.All(i => i == true)) notes.Add(Loc.T("Note.GitIgnored"));
            else status = UnitStatus.NotIgnored;
            if (!commits.TryGetValue(top, out commit)) commits[top] = commit = LastCommit(top);
        }

        // Activity = max(last commit, .git\index, files in the project folder); files touched by that commit itself (same day) keep "commit"
        var activity = commit;
        var source = commit is null ? UsedSource.Unknown : UsedSource.Commit;
        void Seen(DateTime t)
        {
            if (t <= (activity ?? DateTime.MinValue)) return;
            if (source == UsedSource.Commit && t < commit!.Value.AddDays(1)) return;
            (activity, source) = (t, UsedSource.Modified);
        }
        if (top is not null && File.Exists(Path.Combine(top, ".git", "index"))) Seen(File.GetLastWriteTimeUtc(Path.Combine(top, ".git", "index")));
        try { foreach (var f in Directory.EnumerateFiles(dir, "*", Fs.Flat)) Seen(File.GetLastWriteTimeUtc(f)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var name = Path.GetFileName(dir);
        return new Unit
        {
            Id = $"project|{paths[0]}", Location = "project", Category = Category.Projects, Root = root, Parts = parts,
            Title = paths.Length == 2 ? $@"{name}\bin + obj" : $@"{name}\{Path.GetFileName(paths[0])}", Path = paths.Length == 2 ? dir : paths[0],
            Bytes = parts.Sum(p => p.Bytes), LastUsed = activity, Source = source, Owners = owners, Rebuild = rebuild, Marker = marker,
            Notes = notes, Status = status,
        };
    }

    static string? GitTop(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (Path.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return null;
    }

    /// <summary>core.fsmonitor=false: a foreign .git\config could otherwise make git start a program.</summary>
    static (int Code, string Out)? Git(string dir, params string[] args)
    {
        try { return ConsoleTool.Run("git", ["-c", "core.fsmonitor=false", "-C", dir, .. args], TimeSpan.FromSeconds(5), Encoding.UTF8); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException) { return null; }
    }

    /// <summary>true = ignored, false = tracked or not ignored, null = git failed.</summary>
    static bool? Ignored(string dir, string name) => Git(dir, "check-ignore", "-q", name) switch
    {
        { Code: 0 } => true,
        { Code: 1 } => false,
        _ => null,
    };

    static DateTime? LastCommit(string top) =>
        Git(top, "log", "-1", "--format=%ct") is { Code: 0, Out: var o } && long.TryParse(o.Trim(), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
}
