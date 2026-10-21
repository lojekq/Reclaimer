using System.IO;
using Microsoft.Win32;
using Reclaimer.Shell;

namespace Reclaimer.Features;

public static class WholeDirScanner
{
    /// <summary>A package cache moves as one folder; the tool fills it again on the next install.</summary>
    public static Unit Scan(string dir, Location l)
    {
        var stats = Fs.Measure(dir);
        var (at, source) = Fs.LastUsed(stats);
        var notes = new List<string>();
        var status = UnitStatus.Ready;
        if (l.Id == "uv" && string.Equals(Catalog.Env("UV_LINK_MODE"), "symlink", StringComparison.OrdinalIgnoreCase))
        {
            status = UnitStatus.ReportOnly; // venvs link into the cache: moving it breaks them
            notes.Add(Loc.T("Note.UvSymlink"));
        }
        if (l.Id is "uv" or "pnpm") notes.Add(Loc.T("Note.Hardlinks"));
        if (l.Id.StartsWith("cargo")) notes.Add(Loc.T("Note.CargoCleans"));
        if (l.Id == "cache-custom") notes.Add(Loc.T("Note.CustomCache"));
        return new Unit
        {
            Id = $"{l.Id}|{dir}", Location = l.Id, Category = l.Category, Title = Loc.T("Title." + (l.Id == "cache-custom" ? "custom" : l.Id)), Root = dir,
            Parts = [new(dir, true, stats.Bytes, Directory.GetLastWriteTimeUtc(dir))], Bytes = stats.Bytes, LastUsed = at, Source = source,
            Owners = l.Owners, Rebuild = l.Rebuild, Notes = notes, Status = status, Approximate = l.Id is "uv" or "pnpm",
        };
    }
}

/// <summary>LM Studio and the user's model folders (ComfyUI models\, GGUF folders): a unit per weights file ≥ 100 MB.</summary>
public static class ModelFileScanner
{
    static readonly HashSet<string> Exts = new([".gguf", ".safetensors", ".ckpt", ".pt", ".pth", ".bin"], StringComparer.OrdinalIgnoreCase);
    const long MinBytes = 100L << 20;

    public static List<Unit> Scan(string dir, Location l, CancellationToken ct)
    {
        var units = new List<Unit>();
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Fs.Deep))
        {
            ct.ThrowIfCancellationRequested();
            if (!Exts.Contains(f.Extension) || f.Length < MinBytes) continue;
            var (at, source) = Fs.LastUsed(new(f.Length, f.LastWriteTimeUtc, f.LastAccessTimeUtc));
            units.Add(new Unit
            {
                Id = $"{l.Id}|{f.FullName}", Location = l.Id, Category = Category.Models, Title = f.Name, Root = dir,
                Parts = [new(f.FullName, false, f.Length, f.LastWriteTimeUtc)], Bytes = f.Length, LastUsed = at, Source = source,
                Owners = l.Owners, Rebuild = l.Rebuild ?? "Rebuild.Download",
            });
        }
        return units;
    }
}

/// <summary>WSL distribution or Docker Desktop disk: report only in the MVP (compacting is v1.0).</summary>
public sealed record Disk(string Name, string Path, long Bytes, bool Sparse, bool Running);

public static class VhdxScanner
{
    public static List<Disk> Scan(string[] processNames)
    {
        var running = OwnerCheck.WslRunning();
        var disks = new List<Disk>();
        using (var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
            foreach (var id in lxss?.GetSubKeyNames() ?? [])
            {
                using var k = lxss!.OpenSubKey(id);
                if (k?.GetValue("BasePath") is not string basePath) continue;
                var name = k.GetValue("DistributionName") as string ?? id;
                var file = Path.Combine(basePath.StartsWith(@"\\?\") ? basePath[4..] : basePath, k.GetValue("VhdFileName") as string ?? "ext4.vhdx");
                Add(name, file, running.Contains(name));
            }
        var docker = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Docker\wsl";
        var dockerUp = running.Contains("docker-desktop") || OwnerCheck.Running(["Docker Desktop", "com.docker.backend"], processNames) is not null;
        Add("Docker Desktop", docker + @"\disk\docker_data.vhdx", dockerUp); // the older layout is wsl\data\ext4.vhdx: both are looked for
        Add("Docker Desktop", docker + @"\data\ext4.vhdx", dockerUp);
        return disks;

        void Add(string name, string file, bool up)
        {
            try
            {
                var f = new FileInfo(file);
                if (f.Exists) disks.Add(new(name, f.FullName, f.Length, f.Attributes.HasFlag(FileAttributes.SparseFile), up));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
