using System.IO;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>Runs the scanners over the whitelist (catalog + the user's folders), up to 4 at once. Reads only: this is the dry run.</summary>
public static class Scan
{
    static readonly Location CustomModels = new("models-custom", Category.Models, Kind.ModelFiles, [], [], [], "Rebuild.Download");
    static readonly Location CustomCache = new("cache-custom", Category.Caches, Kind.Whole, [], [], [], "Rebuild.Cache");

    /// <summary>
    /// Every whitelisted root, existing or not (a whole cache in the quarantine no longer exists, and restoring it must pass PathGuard).
    /// </summary>
    public static IEnumerable<string> AllowedRoots()
    {
        foreach (var l in Catalog.All)
        {
            foreach (var (v, sub) in l.EnvVars)
                if (Catalog.Env(v) is { Length: > 0 } value) yield return Path.Combine(value, sub);
            foreach (var d in l.Defaults) yield return Environment.ExpandEnvironmentVariables(d);
        }
        var s = Settings.Current;
        foreach (var p in s.ProjectRoots.Concat(s.ModelDirs).Concat(s.ExtraCacheDirs)) yield return p;
    }

    /// <summary><paramref name="found"/> is called from worker threads as units are ready.</summary>
    public static async Task<List<Disk>> Run(Action<Unit> found, Action<string> step, CancellationToken ct)
    {
        var names = OwnerCheck.ProcessNames();
        var ollamaUp = OwnerCheck.Running(["ollama"], names) is not null;
        var jobs = new List<(string Name, Func<IEnumerable<Unit>> Run)>();
        foreach (var l in Catalog.All)
            foreach (var dir in Catalog.PathsOf(l))
                jobs.Add((l.Id, () => Source(l, dir, ollamaUp, ct)));
        var s = Settings.Current;
        foreach (var dir in s.ModelDirs.Where(Directory.Exists)) jobs.Add(("models-custom", () => Source(CustomModels, dir, ollamaUp, ct)));
        foreach (var dir in s.ExtraCacheDirs.Where(Directory.Exists)) jobs.Add(("cache-custom", () => Source(CustomCache, dir, ollamaUp, ct)));
        foreach (var dir in s.ProjectRoots.Where(Directory.Exists)) jobs.Add(("projects", () => ProjectScanner.Scan(dir, ct)));

        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (job, _) =>
        {
            step(job.Name);
            try
            {
                foreach (var u in job.Run())
                    if (Finish(u, names)) found(u);
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"scan {job.Name} failed", e); }
            return ValueTask.CompletedTask;
        });
        return VhdxScanner.Scan(names);
    }

    static IEnumerable<Unit> Source(Location l, string dir, bool ollamaUp, CancellationToken ct)
    {
        switch (l.Kind)
        {
            case Kind.Whole:
                // the folder is the unit: refuse a variable pointing at the profile or Windows before measuring it
                if (PathGuard.Check(dir, dir) is { } why) { Log.Info($"{l.Id} skipped: {why}"); return []; }
                return [WholeDirScanner.Scan(dir, l)];
            case Kind.Hf: return HfScanner.Scan(dir, l, ct);
            case Kind.Ollama: return OllamaScanner.Scan(dir, l, ollamaUp, ct);
            case Kind.ModelFiles: return ModelFileScanner.Scan(dir, l, ct);
            default: return [];
        }
    }

    /// <summary>PathGuard on every part (a part that fails drops the unit), then the statuses: cloud, volume, owner.</summary>
    internal static bool Finish(Unit u, string[] names)
    {
        foreach (var p in u.Parts)
            if (PathGuard.Check(p.Path, u.Root) is { } why)
            {
                Log.Info($"{u.Location} unit skipped: {why}");
                return false;
            }
        if (u.Status != UnitStatus.Ready) return true;
        if (u.Parts.Any(p => PathGuard.IsCloud(p.Path))) u.Status = UnitStatus.CloudFolder;
        else if (!FixedVolume(u.Parts[0].Path)) u.Status = UnitStatus.NoQuarantine;
        else if (OwnerCheck.Running(u.Owners, names) is { } o) (u.Status, u.Owner) = (UnitStatus.OwnerRunning, o);
        return true;
    }

    static bool FixedVolume(string path)
    {
        try { return new DriveInfo(Native.VolumeOf(PathGuard.Real(path))).DriveType == DriveType.Fixed; }
        catch { return false; } // network share, mounted folder DriveInfo can't read: report only
    }

    /// <summary>Debug: the package and model variables point into the sandbox (07 §9); the process variable wins over HKCU/HKLM.</summary>
    public static void UseSandboxInDebug()
    {
#if DEBUG
        var s = Quarantine.SandboxDir;
        foreach (var (v, sub) in new[]
        {
            ("OLLAMA_MODELS", @"ollama\models"), ("HF_HUB_CACHE", @"huggingface\hub"), ("HF_XET_CACHE", @"huggingface\xet"),
            ("NUGET_PACKAGES", @"nuget\packages"), ("NUGET_HTTP_CACHE_PATH", @"nuget\v3-cache"), ("npm_config_cache", "npm-cache"),
            ("PIP_CACHE_DIR", @"pip\Cache"), ("UV_CACHE_DIR", @"uv\cache"), ("PNPM_HOME", "pnpm"), ("CARGO_HOME", "cargo"),
        })
            Environment.SetEnvironmentVariable(v, Path.Combine(s, sub));
#endif
    }
}
