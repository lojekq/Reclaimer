using System.IO;
using System.Security.Cryptography;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>
/// The same weights kept by two programs (v1.0, 07 §8): a GGUF in Ollama and in LM Studio, a safetensors in HF and in ComfyUI.
/// Only files ≥ 1 GB, only .gguf/.safetensors or content-addressed blobs, only between different programs on one volume.
/// The copy goes to the quarantine and a hardlink to the kept file takes its place: both programs keep working, the space comes
/// back when the quarantine is cleaned. Ollama and HF blob names are already sha256, other files are hashed only when their size
/// matches a file of another program.
/// </summary>
public static class Dupes
{
    internal static long MinBytes = 1L << 30;

    /// <summary>A weights file of a program; Sha is known for content-addressed stores, null otherwise.</summary>
    public sealed record Copy(string Program, string Path, long Bytes, string? Sha, string Root, string[] Owners);

    /// <summary>Content-addressed stores first: Ollama and HF never edit a blob in place, so they keep the data.</summary>
    static int Rank(string program) => program switch { "Ollama" => 0, "Hugging Face" => 1, _ => 2 };

    public static List<Unit> Find(IEnumerable<Copy> copies, CancellationToken ct)
    {
        var units = new List<Unit>();
        foreach (var bySize in copies.Where(c => c.Bytes >= MinBytes).GroupBy(c => c.Bytes))
        {
            if (bySize.Select(c => c.Program).Distinct().Count() < 2) continue;
            var hashed = bySize.Select(c => c with { Sha = c.Sha ?? Hash(c.Path, ct) }).Where(c => c.Sha is not null).ToList();
            foreach (var same in hashed.GroupBy(c => c.Sha!, StringComparer.OrdinalIgnoreCase))
            {
                var list = same.ToList();
                if (list.Select(c => c.Program).Distinct().Count() < 2) continue;
                var keep = list.OrderBy(c => Rank(c.Program)).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).First();
                foreach (var dup in list.Where(c => c.Program != keep.Program))
                {
                    if (Native.SameFile(dup.Path, keep.Path) || !SameVolume(dup.Path, keep.Path)) continue; // already linked, or no hardlink possible
                    units.Add(new Unit
                    {
                        Id = $"dupe|{dup.Path}", Location = "dupe", Category = Category.Models, Root = dup.Root, Path = dup.Path,
                        Title = string.Format(Loc.T("Title.Dupe"), Path.GetFileName(dup.Path), dup.Program, keep.Program),
                        Parts = [new(dup.Path, false, dup.Bytes, File.GetLastWriteTimeUtc(dup.Path))], Bytes = dup.Bytes,
                        LastUsed = File.GetLastWriteTimeUtc(dup.Path), Source = UsedSource.Modified, Owners = dup.Owners, Rebuild = "Rebuild.Hardlink",
                        DedupeOf = keep.Path, KeepMtimeUtc = File.GetLastWriteTimeUtc(keep.Path),
                        Notes = [string.Format(Loc.T("Note.Dupe"), keep.Path)],
                    });
                }
            }
        }
        return units;
    }

    /// <summary>SHA-256 of a file; reading must not count as "used", so its atime is put back.</summary>
    internal static string? Hash(string path, CancellationToken ct)
    {
        try
        {
            var atime = File.GetLastAccessTimeUtc(path);
            string hex;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                hex = Convert.ToHexStringLower(SHA256.HashDataAsync(f, ct).AsTask().GetAwaiter().GetResult());
            try { File.SetLastAccessTimeUtc(path, atime); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return hex;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static bool SameVolume(string a, string b)
    {
        try { return string.Equals(Native.VolumeOf(PathGuard.Real(a)), Native.VolumeOf(PathGuard.Real(b)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static readonly HashSet<string> Weights = new([".gguf", ".safetensors"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The weights files of every model location (Ollama blobs, HF repo blobs, LM Studio, llama.cpp, the user's model folders).</summary>
    public static IEnumerable<Copy> Collect()
    {
        foreach (var l in Catalog.All.Where(l => l.Category == Category.Models))
            foreach (var root in Catalog.PathsOf(l))
                foreach (var c in CopiesIn(l, root)) yield return c;
        foreach (var dir in Settings.Current.ModelDirs.Where(Directory.Exists))
            foreach (var c in Files(Path.GetFileName(dir.TrimEnd('\\')), dir, [])) yield return c;
    }

    static IEnumerable<Copy> CopiesIn(Location l, string root)
    {
        switch (l.Id)
        {
            case "ollama":
                var blobs = Path.Combine(root, "blobs");
                if (!Directory.Exists(blobs)) yield break;
                foreach (var f in new DirectoryInfo(blobs).EnumerateFiles("sha256-*", Fs.Flat))
                    if (f.Length >= MinBytes && f.Name.Length == 71) yield return new("Ollama", f.FullName, f.Length, f.Name[7..], root, l.Owners);
                break;
            case "hf-hub":
                foreach (var repo in Directory.EnumerateDirectories(root, "models--*", Fs.Flat))
                {
                    var dir = Path.Combine(repo, "blobs");
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Fs.Flat))
                        if (f.Length >= MinBytes && f.Name.Length == 64 && f.Name.All(Uri.IsHexDigit)) // LFS blobs are named by their sha256
                            yield return new("Hugging Face", f.FullName, f.Length, f.Name, root, l.Owners);
                }
                break;
            case "lmstudio": foreach (var c in Files("LM Studio", root, l.Owners)) yield return c; break;
            case "llama-cpp": foreach (var c in Files("llama.cpp", root, l.Owners)) yield return c; break;
        }
    }

    static IEnumerable<Copy> Files(string program, string root, string[] owners) =>
        new DirectoryInfo(root).EnumerateFiles("*", Fs.Deep)
            .Where(f => Weights.Contains(f.Extension) && f.Length >= MinBytes)
            .Select(f => new Copy(program, f.FullName, f.Length, null, root, owners));
}
