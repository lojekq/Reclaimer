using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>
/// Ollama: a model = its manifest + the blobs no other manifest uses; shared layers stay. Manifests are walked whole
/// (manifests\&lt;host&gt;\&lt;ns&gt;\&lt;model&gt;\&lt;tag&gt;, also hf.co\…). Blobs first, manifest last: a manifest moved without its blobs
/// would make Ollama prune them for good on its next start (OLLAMA_NOPRUNE is off by default).
/// </summary>
public static partial class OllamaScanner
{
    [GeneratedRegex("^sha256[:-]([0-9a-f]{64})$")]
    private static partial Regex Digest();

    /// <summary>A model load in server.log: "time=… starting llama-server cmd=… --model …\sha256-…" or "loading model … model=…\sha256-…".</summary>
    [GeneratedRegex(@"^time=(\S+) .*?(?:--model |model=)\S*?sha256-([0-9a-f]{64})")]
    private static partial Regex RunLine();

    /// <summary>Where Ollama writes server.log and server-1..5.log. Tests replace it.</summary>
    internal static string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama");

    /// <summary>
    /// A model = every manifest that names it in both layouts (manifests\&lt;host&gt;\… up to 0.39, manifests-v2\&lt;host&gt;\… from 0.40),
    /// the blobs no other model uses, and the metadata\sha256-&lt;blob&gt;.json files of those blobs. Parts go blobs, metadata, manifests:
    /// a manifest without its blobs would make Ollama prune the blobs for good on its next start (OLLAMA_NOPRUNE is off by default).
    /// </summary>
    public static List<Unit> Scan(string models, Location l, bool ollamaRunning, CancellationToken ct)
    {
        var blobDir = Path.Combine(models, "blobs");
        var metaDir = Path.Combine(models, "metadata");
        var byName = new Dictionary<string, List<(string File, HashSet<string> Blobs)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, v2) in new[] { (Path.Combine(models, "manifests"), false), (Path.Combine(models, "manifests-v2"), true) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", Fs.Deep))
            {
                ct.ThrowIfCancellationRequested();
                var blobs = v2 ? BlobsOfV2(f, blobDir) : BlobsOf(f, blobDir);
                if (blobs is null) continue;
                var name = NameOf(dir, f);
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
                list.Add((f, blobs));
            }
        }
        var users = byName.Values.SelectMany(m => m.SelectMany(x => x.Blobs).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(b => b, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var runs = LastRuns(LogDir);

        var units = new List<Unit>();
        foreach (var (name, manifests) in byName)
        {
            ct.ThrowIfCancellationRequested();
            var blobs = manifests.SelectMany(m => m.Blobs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var own = blobs.Where(b => users[b] == 1 && File.Exists(b)).ToList();
            var shared = blobs.Where(b => users[b] > 1 && File.Exists(b)).Sum(b => new FileInfo(b).Length);
            var parts = own.Select(b => new Part(b, false, new FileInfo(b).Length, File.GetLastWriteTimeUtc(b))).ToList();
            parts.AddRange(own.Select(b => Path.Combine(metaDir, Path.GetFileName(b) + ".json")).Where(File.Exists)
                .Select(m => new Part(m, false, new FileInfo(m).Length, File.GetLastWriteTimeUtc(m))));
            parts.AddRange(manifests.Select(m => new Part(m.File, false, new FileInfo(m.File).Length, File.GetLastWriteTimeUtc(m.File))));
            var stats = new Stats(0, manifests.Max(m => File.GetLastWriteTimeUtc(m.File)), blobs.Where(File.Exists).Select(File.GetLastAccessTimeUtc).DefaultIfEmpty().Max());
            var (at, source) = Fs.LastUsed(stats); // manifest mtime = pulled; blob atime = loaded (when NTFS writes atime)
            var ran = blobs.Select(b => runs.GetValueOrDefault(Path.GetFileName(b)[7..])).DefaultIfEmpty().Max();
            if (ran > (at ?? default)) (at, source) = (ran, UsedSource.Ran); // the log knows the last run; unknown format = "downloaded" stays
            var freed = parts.Sum(p => p.Bytes);
            units.Add(new Unit
            {
                Id = $"{l.Id}|{name}", Location = l.Id, Category = l.Category, Title = name, Root = models, Path = manifests[0].File, Origin = "ollama pull " + name,
                Parts = parts, Bytes = freed, LastUsed = at, Source = source, Owners = l.Owners, Rebuild = l.Rebuild,
                Notes = [string.Format(Loc.T("Note.OllamaShared"), Format.Size(freed), Format.Size(shared))],
            });
        }

        // During a pull a blob appears before its manifest: orphans are shown only while Ollama is not running.
        // A blob with a metadata file is not an orphan either: 0.40 keeps metadata only for models it knows.
        if (!ollamaRunning && Directory.Exists(blobDir))
        {
            var orphans = Directory.EnumerateFiles(blobDir, "sha256-*", Fs.Flat)
                .Where(b => Digest().IsMatch(Path.GetFileName(b)) && !users.ContainsKey(b) && !File.Exists(Path.Combine(metaDir, Path.GetFileName(b) + ".json")))
                .Select(b => new Part(b, false, new FileInfo(b).Length, File.GetLastWriteTimeUtc(b))).ToList();
            if (orphans.Count > 0)
                units.Add(new Unit
                {
                    Id = $"{l.Id}|orphans", Location = l.Id, Category = l.Category, Title = Loc.T("Title.OllamaOrphans"), Root = models,
                    Parts = orphans, Path = blobDir, Bytes = orphans.Sum(p => p.Bytes), LastUsed = orphans.Max(p => p.MtimeUtc), Source = UsedSource.Modified,
                    Owners = l.Owners, Rebuild = "Rebuild.Nothing",
                });
        }
        return units;
    }

    /// <summary>
    /// A manifests-v2 file (Ollama 0.40) is a list: every entry's digest is a manifest stored as a blob, which in turn names the
    /// config and layers. The manifest blobs and everything they name are the model's blobs; an entry whose blob is missing
    /// (a runner that was never pulled) is skipped. Null when nothing valid is in it.
    /// </summary>
    internal static HashSet<string>? BlobsOfV2(string file, string blobDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!doc.RootElement.TryGetProperty("manifests", out var list)) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in list.EnumerateArray())
            {
                if (!m.TryGetProperty("digest", out var d) || d.GetString() is not { } digest || Digest().Match(digest) is not { Success: true } hex) return null;
                var blob = Path.Combine(blobDir, "sha256-" + hex.Groups[1].Value);
                set.Add(blob);
                if (File.Exists(blob) && new FileInfo(blob).Length < 1 << 20 && BlobsOf(blob, blobDir) is { } inner) set.UnionWith(inner);
            }
            return set.Count > 0 ? set : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The last time each weights blob was loaded, by digest, from Ollama's server logs (v1.0). The logs are read shared:
    /// Ollama keeps server.log open. A format it doesn't recognise gives nothing, and the age stays the download date.
    /// </summary>
    internal static Dictionary<string, DateTime> LastRuns(string dir)
    {
        var runs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(dir)) return runs;
            foreach (var f in Directory.EnumerateFiles(dir, "server*.log"))
            {
                using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                while (r.ReadLine() is { } line)
                    if (line.Contains("sha256-") && RunLine().Match(line) is { Success: true } m && DateTimeOffset.TryParse(m.Groups[1].Value, out var t)
                        && t.UtcDateTime > runs.GetValueOrDefault(m.Groups[2].Value))
                        runs[m.Groups[2].Value] = t.UtcDateTime;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Info($"ollama logs unreadable: {e.GetType().Name}"); }
        return runs;
    }

    /// <summary>Blob paths of a manifest (config + layers). Not a manifest or a digest that isn't sha256 hex → null (skipped).</summary>
    internal static HashSet<string>? BlobsOf(string manifest, string blobDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            var digests = new List<string?>();
            if (doc.RootElement.TryGetProperty("config", out var c) && c.TryGetProperty("digest", out var cd)) digests.Add(cd.GetString());
            if (doc.RootElement.TryGetProperty("layers", out var layers))
                foreach (var layer in layers.EnumerateArray())
                    if (layer.TryGetProperty("digest", out var d)) digests.Add(d.GetString());
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in digests)
            {
                if (d is null || Digest().Match(d) is not { Success: true } m) return null; // never a path from the file itself
                set.Add(Path.Combine(blobDir, "sha256-" + m.Groups[1].Value));
            }
            return set.Count > 0 ? set : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    /// <summary>registry.ollama.ai\library\llama3.1\70b and ollama.com\library\llama3.1\70b → llama3.1:70b; hf.co\bartowski\Qwen3-GGUF\Q4_K_M → hf.co/bartowski/Qwen3-GGUF:Q4_K_M.</summary>
    internal static string NameOf(string manifestDir, string file)
    {
        var s = Path.GetRelativePath(manifestDir, file).Split('\\').ToList();
        if (s.Count > 2 && s[0] is "registry.ollama.ai" or "ollama.com") s.RemoveAt(0); // the default registries of both layouts
        if (s.Count > 2 && s[0] == "library") s.RemoveAt(0);
        return string.Join('/', s[..^1]) + ":" + s[^1];
    }
}
