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

    public static List<Unit> Scan(string models, Location l, bool ollamaRunning, CancellationToken ct)
    {
        var manifestDir = Path.Combine(models, "manifests");
        var blobDir = Path.Combine(models, "blobs");
        var manifests = Directory.Exists(manifestDir)
            ? Directory.EnumerateFiles(manifestDir, "*", Fs.Deep).Select(f => (File: f, Blobs: BlobsOf(f, blobDir))).Where(m => m.Blobs is not null).ToList()
            : [];
        var users = manifests.SelectMany(m => m.Blobs!).GroupBy(b => b, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var runs = LastRuns(LogDir);

        var units = new List<Unit>();
        foreach (var (file, blobs) in manifests)
        {
            ct.ThrowIfCancellationRequested();
            var own = blobs!.Where(b => users[b] == 1 && File.Exists(b)).ToList();
            var shared = blobs!.Where(b => users[b] > 1 && File.Exists(b)).Sum(b => new FileInfo(b).Length);
            var parts = own.Select(b => new Part(b, false, new FileInfo(b).Length, File.GetLastWriteTimeUtc(b))).ToList();
            parts.Add(new(file, false, new FileInfo(file).Length, File.GetLastWriteTimeUtc(file)));
            var stats = new Stats(0, File.GetLastWriteTimeUtc(file), blobs!.Where(File.Exists).Select(File.GetLastAccessTimeUtc).DefaultIfEmpty().Max());
            var (at, source) = Fs.LastUsed(stats); // manifest mtime = pulled; blob atime = loaded (when NTFS writes atime)
            var ran = blobs!.Select(b => runs.GetValueOrDefault(Path.GetFileName(b)[7..])).DefaultIfEmpty().Max();
            if (ran > (at ?? default)) (at, source) = (ran, UsedSource.Ran); // the log knows the last run; unknown format = "downloaded" stays
            var freed = parts.Sum(p => p.Bytes);
            units.Add(new Unit
            {
                Id = $"{l.Id}|{file}", Location = l.Id, Category = l.Category, Title = NameOf(manifestDir, file), Root = models,
                Parts = parts, Bytes = freed, LastUsed = at, Source = source, Owners = l.Owners, Rebuild = l.Rebuild,
                Notes = [string.Format(Loc.T("Note.OllamaShared"), Format.Size(freed), Format.Size(shared))],
            });
        }

        // During a pull a blob appears before its manifest: orphans are shown only while Ollama is not running
        if (!ollamaRunning && Directory.Exists(blobDir))
        {
            var orphans = Directory.EnumerateFiles(blobDir, "sha256-*", Fs.Flat)
                .Where(b => Digest().IsMatch(Path.GetFileName(b)) && !users.ContainsKey(b))
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

    /// <summary>registry.ollama.ai\library\llama3.1\70b → llama3.1:70b; hf.co\bartowski\Qwen3-GGUF\Q4_K_M → hf.co/bartowski/Qwen3-GGUF:Q4_K_M.</summary>
    internal static string NameOf(string manifestDir, string file)
    {
        var s = Path.GetRelativePath(manifestDir, file).Split('\\').ToList();
        if (s.Count > 2 && s[0] == "registry.ollama.ai") s.RemoveAt(0);
        if (s.Count > 2 && s[0] == "library") s.RemoveAt(0);
        return string.Join('/', s[..^1]) + ":" + s[^1];
    }
}
