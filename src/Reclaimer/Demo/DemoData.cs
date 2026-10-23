using Reclaimer.Features;
using Reclaimer.Shell;

namespace Reclaimer.Demo;

/// <summary>
/// --demo: believable data for screenshots and GIFs. Names like Alex, example.com, C:\Users\demo\…; never real data.
/// Under --demo no real services start, nothing is written, no hotkeys, no update check (00-shared §2.3).
/// Nothing here touches the disk: scan, move, restore and purge only change the lists.
/// </summary>
public static class DemoData
{
    const string Home = @"C:\Users\demo";
    public const string QuarantineRoot = @"C:\Users\demo\AppData\Local\Reclaimer\Quarantine";
    const long GB = 1L << 30;

    public static void Fill()
    {
        var m = Model.I;
        m.Volumes.Add(Model.View("C:", "NVMe", 954 * GB, (long)(954 * GB * 0.13), (long)(12.4 * GB), DateTime.UtcNow.AddDays(1)));
        m.Volumes.Add(Model.View("D:", "Data", 1863 * GB, 1100 * GB, 0, null));
        if (Args.Current.Autoplay) return; // the GIF starts from an empty table and an empty quarantine
        foreach (var u in Units()) m.Units.Add(u);
        foreach (var d in Disks()) m.Disks.Add(d);
        foreach (var r in DockerRows) m.DockerRows.Add(r);
        foreach (var e in Entries()) m.Entries.Add(e);
        // 7 rows, ≈ 58.3 GB: one model by hand, two caches, the old projects
        foreach (var u in m.Units.Where(u => u.Title == "llama3.1:70b" || u.Location is "uv" or "npm" || u.Category == Category.Projects && u.AgeDays > 90 && u.CanSelect))
            u.Selected = true;
        m.RefreshTiles();
        m.RefreshQuarantine();
        var now = DateTime.UtcNow;
        var p = Home + @"\source";
        foreach (var h in new HistoryRow[]
        {
            new(now.AddHours(-2), "restore", [p + @"\website\node_modules"], (long)(0.9 * GB), "ok", 0, null, @"website\node_modules"),
            new(now.AddDays(-1).AddHours(-1), "move", [Home + @"\.cache\huggingface\hub\models--stabilityai--sdxl-turbo"], (long)(6.9 * GB), "ok", 0, null, "models--stabilityai--sdxl-turbo"),
            new(now.AddDays(-1).AddHours(-1), "move", [p + @"\cuda-sandbox\.venv"], (long)(2.8 * GB), "ok", 0, null, @"cuda-sandbox\.venv"),
            new(now.AddDays(-2), "purge", [Home + @"\.ollama\models\blobs\sha256-3f9a", Home + @"\.ollama\models\manifests\registry.ollama.ai\library\phi3\mini"], (long)(2.2 * GB), "ok", (long)(2.2 * GB), null, "phi3:mini"),
            new(now.AddDays(-4), "move", [Home + @"\AppData\Local\NuGet\v3-cache"], (long)(1.1 * GB), "ok", 0, null, Loc.T("Title.nuget-http")),
            new(now.AddDays(-4), "move", [p + @"\website\node_modules"], (long)(0.9 * GB), "ok", 0, null, @"website\node_modules"),
            new(now.AddDays(-6), "move", [p + @"\bot\bin", p + @"\bot\obj"], (long)(0.7 * GB), "ok", 0, null, @"bot\bin + obj"),
            new(now.AddDays(-9), "purge", [Home + @"\AppData\Local\pip\Cache"], (long)(3.4 * GB), "ok", (long)(3.3 * GB), null, Loc.T("Title.pip")),
            new(now.AddDays(-12), "move", [p + @"\game\target"], (long)(4.1 * GB), "error 32", 0, null, @"game\target"),
        })
            m.History.Add(h);
    }

    /// <summary>The scan of the GIF: rows arrive over ~1.5 s.</summary>
    public static async Task FakeScan(CancellationToken ct)
    {
        foreach (var u in Units())
        {
            await Task.Delay(70, ct);
            Model.I.Units.Add(u);
        }
        foreach (var d in Disks()) Model.I.Disks.Add(d);
    }

    public static string? Move(Unit u, int days)
    {
        var now = DateTime.UtcNow;
        Model.I.Entries.Insert(0, new Entry
        {
            Id = u.Id, Title = u.Title, Category = u.Category, TotalBytes = u.Bytes, CreatedUtc = now, ExpiresUtc = now.AddDays(days),
            State = EntryState.Done, Parts = [new() { Original = u.Path }], Dir = QuarantineRoot + @"\demo",
        });
        return null;
    }

    static IEnumerable<Unit> Units()
    {
        var l = Catalog.All.ToDictionary(x => x.Id);
        Unit U(string loc, Category c, string title, string path, double gb, int days, UsedSource src, string? rebuild = null, string? marker = null,
            UnitStatus status = UnitStatus.Ready, string? owner = null, string[]? notes = null, bool approx = false, string? dedupe = null) => new()
        {
            Id = "demo|" + path, Location = loc, Category = c, Title = title, Root = Home, Path = path,
            Parts = [new(path, true, (long)(gb * GB))], Bytes = (long)(gb * GB), LastUsed = DateTime.UtcNow.AddDays(-days).AddHours(-3), Source = src,
            Rebuild = rebuild ?? (l.TryGetValue(loc, out var x) ? x.Rebuild : null), Marker = marker, Status = status, Owner = owner,
            Notes = [.. notes ?? []], Approximate = approx, DedupeOf = dedupe,
        };
        var p = Home + @"\source";
        return
        [
            U("ollama", Category.Models, "llama3.1:70b", Home + @"\.ollama\models\manifests\registry.ollama.ai\library\llama3.1\70b", 39.9, 124, UsedSource.Accessed,
                notes: [string.Format(Loc.T("Note.OllamaShared"), Format.Size((long)(39.9 * GB)), Format.Size(0))]),
            U("hf-hub", Category.Models, "models--black-forest-labs--FLUX.1-dev", Home + @"\.cache\huggingface\hub\models--black-forest-labs--FLUX.1-dev", 23.8, 96, UsedSource.Modified),
            U("lmstudio", Category.Models, "Qwen3-32B-Q4_K_M.gguf", Home + @"\.lmstudio\models\Qwen\Qwen3-32B-GGUF\Qwen3-32B-Q4_K_M.gguf", 19.8, 152, UsedSource.Modified,
                status: UnitStatus.OwnerRunning, owner: "LM Studio"),
            U("dupe", Category.Models, string.Format(Loc.T("Title.Dupe"), "gemma-3-12b-it-Q4_K_M.gguf", "LM Studio", "Ollama"),
                Home + @"\.lmstudio\models\lmstudio-community\gemma-3-12b-it-GGUF\gemma-3-12b-it-Q4_K_M.gguf", 7.3, 61, UsedSource.Modified, "Rebuild.Hardlink",
                notes: [string.Format(Loc.T("Note.Dupe"), Home + @"\.ollama\modelslobs\sha256-adca500f")], dedupe: Home + @"\.ollama\modelslobs\sha256-adca500f"),
            U("own-models", Category.OwnModels, "Qwen3-27B-Q4_K_M.gguf", @"D:\llm\models\Qwen3-27B-Q4_K_M.gguf", 15.9, 9, UsedSource.Accessed, "Rebuild.Download",
                status: UnitStatus.InUse, owner: "llama-server", notes: [string.Format(Loc.T("Note.OwnFolder"), @"D:\llm\models")]),
            U("own-models", Category.OwnModels, "Qwen3-8B-Q8_0.gguf", @"D:\llm\models\old\Qwen3-8B-Q8_0.gguf", 8.7, 140, UsedSource.Modified, "Rebuild.Download",
                notes: [string.Format(Loc.T("Note.OwnFolder"), @"D:\llm\models\old")]),
            U("own-models", Category.OwnModels, "gemma-3-12b-it-Q4_K_M.gguf", @"D:\llm\models\gemma-3-12b-it-Q4_K_M.gguf", 6.9, 58, UsedSource.Accessed, "Rebuild.Download",
                status: UnitStatus.InUse, owner: "autostart.ps1 (MLink)", notes: [string.Format(Loc.T("Note.OwnFolder"), @"D:\llm\models")]),
            U("ollama", Category.Models, "mistral-nemo:12b", Home + @"\.ollama\models\manifests\registry.ollama.ai\library\mistral-nemo\12b", 7.1, 41, UsedSource.Accessed),
            U("models-custom", Category.Models, "sd_xl_base_1.0.safetensors", @"D:\ComfyUI\models\checkpoints\sd_xl_base_1.0.safetensors", 5.4, 210, UsedSource.Modified, "Rebuild.Download"),
            U("uv", Category.Caches, Loc.T("Title.uv"), Home + @"\AppData\Local\uv\cache", 6.1, 112, UsedSource.Modified, notes: [Loc.T("Note.Hardlinks")], approx: true),
            U("nuget", Category.Caches, Loc.T("Title.nuget"), Home + @"\.nuget\packages", 4.7, 1, UsedSource.Modified),
            U("pnpm", Category.Caches, Loc.T("Title.pnpm"), Home + @"\AppData\Local\pnpm\store", 3.5, 64, UsedSource.Modified, notes: [Loc.T("Note.Hardlinks")], approx: true),
            U("npm", Category.Caches, Loc.T("Title.npm"), Home + @"\AppData\Local\npm-cache", 3.2, 97, UsedSource.Modified),
            U("pip", Category.Caches, Loc.T("Title.pip"), Home + @"\AppData\Local\pip\Cache", 2.4, 33, UsedSource.Modified),
            U("cargo-cache", Category.Caches, Loc.T("Title.cargo-cache"), Home + @"\.cargo\registry\cache", 1.1, 18, UsedSource.Modified, notes: [Loc.T("Note.CargoCleans")]),
            U("project", Category.Projects, @"rust-raytracer\target", p + @"\rust-raytracer\target", 6.4, 287, UsedSource.Commit, "Rebuild.Cargo", "Cargo.toml"),
            U("project", Category.Projects, @"ml-experiments\.venv", p + @"\ml-experiments\.venv", 4.3, 63, UsedSource.Commit, "Rebuild.Venv", "pyvenv.cfg"),
            U("project", Category.Projects, @"game-engine\target", p + @"\game-engine\target", 2.6, 12, UsedSource.Commit, "Rebuild.Cargo", "Cargo.toml"),
            U("project", Category.Projects, @"old-telegram-bot\node_modules", p + @"\old-telegram-bot\node_modules", 1.2, 214, UsedSource.Commit, "Rebuild.Npm", "package.json"),
            U("project", Category.Projects, @"shop-api\bin + obj", p + @"\shop-api", 0.9, 131, UsedSource.Commit, "Rebuild.Dotnet", "*.csproj"),
            U("project", Category.Projects, @"fresh-app\node_modules", p + @"\fresh-app\node_modules", 0.8, 3, UsedSource.Modified, "Rebuild.Npm", "package.json"),
            U("project", Category.Projects, @"blog\.next", p + @"\blog\.next", 0.7, 45, UsedSource.Commit, "Rebuild.Next", "next.config"),
            U("project", Category.Projects, @"scraper\.venv", p + @"\scraper\.venv", 0.6, 176, UsedSource.Modified, "Rebuild.Venv", "pyvenv.cfg", notes: [Loc.T("Note.NoGit")]),
            U("project", Category.Projects, @"tracked-deps\node_modules", p + @"\tracked-deps\node_modules", 0.5, 402, UsedSource.Commit, "Rebuild.Npm", "package.json",
                status: UnitStatus.NotIgnored),
        ];
    }

    static IEnumerable<Disk> Disks() =>
    [
        new("Ubuntu-24.04", Home + @"\AppData\Local\wsl\{3f2b7c1e-demo}\ext4.vhdx", (long)(48.2 * GB), false, true),
        new("Docker Desktop", Home + @"\AppData\Local\Docker\wsl\disk\docker_data.vhdx", (long)(31.7 * GB), false, false),
    ];

    static readonly DockerRow[] DockerRows =
    [
        new("Images", "18.4GB", "9.1GB (49%)"), new("Containers", "220MB", "0B (0%)"),
        new("Local Volumes", "6.3GB", "1.2GB (19%)"), new("Build Cache", "7.8GB", "7.8GB"),
    ];

    static IEnumerable<Entry> Entries()
    {
        Entry E(string title, Category c, string original, double gb, int daysLeft) => new()
        {
            Id = title, Title = title, Category = c, TotalBytes = (long)(gb * GB), State = EntryState.Done, Dir = QuarantineRoot + @"\demo",
            CreatedUtc = DateTime.UtcNow.AddDays(daysLeft - 7).AddMinutes(-30), ExpiresUtc = DateTime.UtcNow.AddDays(daysLeft).AddMinutes(-30),
            Parts = [new() { Original = original }],
        };
        var p = Home + @"\source";
        return
        [
            E("models--stabilityai--sdxl-turbo", Category.Models, Home + @"\.cache\huggingface\hub\models--stabilityai--sdxl-turbo", 6.9, 6),
            E(@"cuda-sandbox\.venv", Category.Projects, p + @"\cuda-sandbox\.venv", 2.8, 6),
            E(Loc.T("Title.nuget-http"), Category.Caches, Home + @"\AppData\Local\NuGet\v3-cache", 1.1, 3),
            E(@"website\node_modules", Category.Projects, p + @"\website\node_modules", 0.9, 3),
            E(@"bot\bin + obj", Category.Projects, p + @"\bot", 0.7, 1),
        ];
    }
}
