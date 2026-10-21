using System.IO;
using System.Text;
using Reclaimer.Features;
using Reclaimer.Shell;

namespace Reclaimer.Tests;

/// <summary>07 §9: everything in temp folders, no admin. The quarantine, its sandbox and the whitelist point into the test folder.</summary>
[TestClass]
[DoNotParallelize]
public class SafetyTests
{
    public TestContext TestContext { get; set; } = null!;
    string dir = null!;
    string Q => Path.Combine(dir, ".reclaimer-quarantine");

    [TestInitialize]
    public void Init()
    {
        dir = Path.Combine(Path.GetTempPath(), "Reclaimer.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Quarantine.Sandbox = dir;
        Quarantine.RootFor = _ => Q;
        Quarantine.Roots = () => [Q];
        Quarantine.AllowedRoots = () => [Path.Combine(dir, "src")];
        Quarantine.HistoryPath = Path.Combine(dir, "history.jsonl");
        PathGuard.Excluded = () => [];
    }

    [TestCleanup]
    public void Cleanup()
    {
        Quarantine.DeleteTree(dir, Path.GetTempPath(), default); // links go as links, read-only files too
    }

    string Mk(string rel, string? text = null)
    {
        var p = Path.Combine(dir, rel);
        if (text is null) Directory.CreateDirectory(p);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        return p;
    }

    static void Junction(string link, string target)
    {
        var (code, _) = ConsoleTool.Run("cmd.exe", ["/c", "mklink", "/J", link, target], TimeSpan.FromSeconds(10), Encoding.UTF8);
        Assert.AreEqual(0, code, "mklink /J");
    }

    Unit UnitOf(string root, params string[] paths) => new()
    {
        Id = paths[0], Location = "test", Category = Category.Projects, Title = Path.GetFileName(paths[0]), Root = root,
        Parts = paths.Select(p => new Part(p, Directory.Exists(p), Fs.Size(p))).ToList(), Bytes = paths.Sum(Fs.Size),
    };

    // ------------------------------------------------------------ PathGuard

    [TestMethod]
    public void GuardComparesWholeFolderNames()
    {
        var root = Mk("dev");
        var inside = Mk(@"dev\app\node_modules");
        var twin = Mk(@"dev2\app\node_modules");
        Assert.IsNull(PathGuard.Check(inside, root));
        Assert.IsNull(PathGuard.Check(inside, root + @"\"));                  // trailing backslash
        Assert.IsNull(PathGuard.Check(inside.ToUpperInvariant(), root));       // case
        Assert.AreEqual("Guard.OutsideRoot", PathGuard.Check(twin, root));     // C:\dev is not C:\dev2
        Assert.AreEqual("Guard.OutsideRoot", PathGuard.Check(Path.Combine(root, @"..\dev2\app\node_modules"), root));
    }

    [TestMethod]
    public void GuardStopList()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.AreEqual("Guard.StopList", PathGuard.Check(Path.Combine(win, "System32"), Path.GetPathRoot(win)!));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.AreEqual("Guard.StopList", PathGuard.Check(profile, Path.GetPathRoot(profile)!));
        Assert.AreEqual("Guard.TooShort", PathGuard.Check(@"C:\Users", @"C:\"));
    }

    [TestMethod]
    public void GuardRefusesLinksGitQuarantineAndExcluded()
    {
        var root = Mk("src");
        var victim = Mk(@"victim\data");
        var link = Path.Combine(root, "node_modules");
        Junction(link, victim);
        Assert.AreEqual("Guard.Link", PathGuard.Check(link, root));             // the target itself is a junction

        Junction(Path.Combine(root, "via"), Mk("elsewhere"));
        Mk(@"elsewhere\app\node_modules");
        Assert.AreEqual("Guard.OutsideRoot", PathGuard.Check(Path.Combine(root, @"via\app\node_modules"), root)); // a link in the ancestors leaves the root

        Mk(@"src\repo\.git");
        Assert.AreEqual("Guard.Git", PathGuard.Check(Path.Combine(root, "repo"), root));

        Assert.AreEqual("Guard.Quarantine", PathGuard.Check(Mk(@".reclaimer-quarantine\x"), dir));
        PathGuard.Excluded = () => [Path.Combine(dir, @"src\keep")];
        Assert.AreEqual("Guard.Excluded", PathGuard.Check(Mk(@"src\keep\node_modules"), root));
    }

    // ------------------------------------------------------------ project rules

    static ISet<string?> Names(string d) => Directory.GetDirectories(d).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void BuildFolderRules()
    {
        var rust = Mk(@"src\rust");
        Mk(@"src\rust\target\CACHEDIR.TAG", "x");
        Assert.IsEmpty(ProjectScanner.Candidates(rust, Names(rust)).ToList());  // target without Cargo.toml
        Mk(@"src\rust\Cargo.toml", "[package]");
        Assert.HasCount(1, ProjectScanner.Candidates(rust, Names(rust)).ToList());

        var cs = Mk(@"src\cs");
        Mk(@"src\cs\app.csproj", "<Project />");
        Mk(@"src\cs\bin\Debug\app.dll", "x");
        Mk(@"src\cs\obj");
        Assert.IsEmpty(ProjectScanner.Candidates(cs, Names(cs)).ToList());     // bin without obj\project.assets.json
        Mk(@"src\cs\obj\project.assets.json", "{}");
        var c = ProjectScanner.Candidates(cs, Names(cs)).Single();
        Assert.HasCount(2, c.Parts);                                           // bin + obj as one unit

        var js = Mk(@"src\js");
        Mk(@"src\js\node_modules\left-pad\index.js", "x");
        Assert.IsEmpty(ProjectScanner.Candidates(js, Names(js)).ToList());
        Mk(@"src\js\package.json", "{}");
        Assert.HasCount(1, ProjectScanner.Candidates(js, Names(js)).ToList());

        var py = Mk(@"src\py");
        Mk(@"src\py\.venv\Lib\x.py", "x");
        Assert.IsEmpty(ProjectScanner.Candidates(py, Names(py)).ToList());
        Mk(@"src\py\.venv\pyvenv.cfg", "home = x");
        Assert.HasCount(1, ProjectScanner.Candidates(py, Names(py)).ToList());
    }

    // ------------------------------------------------------------ Ollama, HF

    static string Manifest(params string[] hex) =>
        $$"""{"config":{"digest":"sha256:{{hex[0]}}","size":1},"layers":[{{string.Join(',', hex.Skip(1).Select(h => $$"""{"digest":"sha256:{{h}}","size":1}"""))}}]}""";

    [TestMethod]
    public void OllamaSharedLayerStays()
    {
        string H(char c) => new(c, 64);
        var models = Mk("ollama");
        foreach (var c in "abcd") Mk($@"ollama\blobs\sha256-{H(c)}", new string('x', 100 * (c - 'a' + 1)));
        Mk($@"ollama\blobs\sha256-{H('e')}", "orphan");
        Mk(@"ollama\manifests\registry.ollama.ai\library\llama3.1\70b", Manifest(H('a'), H('b'), H('d')));
        Mk(@"ollama\manifests\hf.co\bartowski\Qwen3-GGUF\Q4_K_M", Manifest(H('c'), H('d')));
        var l = Catalog.All.Single(x => x.Id == "ollama");

        var units = OllamaScanner.Scan(models, l, ollamaRunning: false, default);
        var llama = units.Single(u => u.Title == "llama3.1:70b");
        var qwen = units.Single(u => u.Title == "hf.co/bartowski/Qwen3-GGUF:Q4_K_M");
        CollectionAssert.AreEquivalent(new[] { $"sha256-{H('a')}", $"sha256-{H('b')}", "70b" }, llama.Parts.Select(p => Path.GetFileName(p.Path)).ToList());
        Assert.AreEqual("70b", Path.GetFileName(llama.Parts[^1].Path));        // manifest last: blobs move first
        Assert.IsFalse(qwen.Parts.Any(p => p.Path.EndsWith(H('d'))));          // the shared layer stays
        Assert.AreEqual(100 + 200 + new FileInfo(llama.Parts[^1].Path).Length, llama.Bytes);
        Assert.AreEqual(1, units.Single(u => u.Id.EndsWith("orphans")).Parts.Count);
        Assert.IsFalse(OllamaScanner.Scan(models, l, ollamaRunning: true, default).Any(u => u.Id.EndsWith("orphans"))); // a pull may be running
    }

    [TestMethod]
    public void OllamaManifestCannotPointOutside()
    {
        var models = Mk("ollama");
        Mk(@"ollama\manifests\x\y\z\t", """{"config":{"digest":"sha256:../../../../victim"},"layers":[]}""");
        Assert.IsNull(OllamaScanner.BlobsOf(Path.Combine(models, @"manifests\x\y\z\t"), Path.Combine(models, "blobs")));
    }

    [TestMethod]
    public void HfSharedBlobStaysOwnBlobMovesAndComesBack()
    {
        var hub = Mk(@"src\hub");
        var shared = Mk(@"src\hub\blobs\ab\shared", "shared weights");
        var own = Mk(@"src\hub\blobs\cd\own", "own weights");
        Mk(@"src\hub\blobs\cd\own.refs", "models--a--one");
        Mk(@"src\hub\models--a--one\refs\main", "abc");
        Mk(@"src\hub\models--b--two\refs\main", "def");
        try
        {
            Link(@"models--a--one\snapshots\abc\model.bin", @"..\..\..\blobs\cd\own");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("symlinks need developer mode or admin here");
        }
        Link(@"models--a--one\snapshots\abc\shared.bin", @"..\..\..\blobs\ab\shared");
        Link(@"models--b--two\snapshots\def\shared.bin", @"..\..\..\blobs\ab\shared");

        var l = Catalog.All.Single(x => x.Id == "hf-hub");
        var units = HfScanner.Scan(hub, l, default);
        var one = units.Single(u => u.Title == "models--a--one");
        CollectionAssert.AreEquivalent(new[] { own, own + ".refs", Path.Combine(hub, "models--a--one") }, one.Parts.Select(p => p.Path).ToList());
        Assert.HasCount(1, units.Single(u => u.Title == "models--b--two").Parts); // the shared blob is not anyone's

        Quarantine.AllowedRoots = () => [hub];
        Assert.IsNull(Quarantine.Move(one, 7));
        Assert.IsFalse(File.Exists(own));
        Assert.IsTrue(File.Exists(shared));
        Assert.IsNull(Quarantine.Restore(Quarantine.List().Single()));
        Assert.AreEqual("own weights", File.ReadAllText(Path.Combine(hub, @"models--a--one\snapshots\abc\model.bin"))); // through the link again
        Assert.AreEqual("shared weights", File.ReadAllText(Path.Combine(hub, @"models--b--two\snapshots\def\shared.bin")));

        void Link(string rel, string target)
        {
            var p = Path.Combine(hub, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.CreateSymbolicLink(p, target);
        }
    }

    // ------------------------------------------------------------ quarantine

    [TestMethod]
    public void MoveAndRestoreRoundTrip()
    {
        var src = Mk("src");
        var nm = Mk(@"src\app\node_modules");
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(nm, "lib.bin"), bytes);
        Mk(@"src\app\node_modules\.bin\deep\x.js", "console.log(1)");

        Assert.IsNull(Quarantine.Move(UnitOf(src, nm), 7));
        Assert.IsFalse(Directory.Exists(nm));
        var e = Quarantine.List().Single();
        Assert.AreEqual(EntryState.Done, e.State);
        Assert.IsTrue(Directory.Exists(Path.Combine(e.Dir, "0-node_modules")));

        Assert.IsNull(Quarantine.Restore(e));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(nm, "lib.bin")));
        Assert.AreEqual("console.log(1)", File.ReadAllText(Path.Combine(nm, @".bin\deep\x.js")));
        Assert.IsEmpty(Quarantine.List());
        Assert.HasCount(2, File.ReadAllLines(Quarantine.HistoryPath));
    }

    [TestMethod]
    public void RestoreConflictKeepsBoth()
    {
        var src = Mk("src");
        var cache = Mk(@"src\npm-cache");
        Mk(@"src\npm-cache\old.tgz", "old");
        Assert.IsNull(Quarantine.Move(UnitOf(src, cache), 7));
        Mk(@"src\npm-cache\new.tgz", "new");                                   // npm made a new cache meanwhile
        var e = Quarantine.List().Single();
        Assert.AreEqual("Restore.Conflict", Quarantine.Restore(e));
        Assert.IsFalse(File.Exists(Path.Combine(cache, "old.tgz")));          // nothing overwritten
        Assert.IsNull(Quarantine.Restore(e, beside: true));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(dir, @"src\npm-cache.restored\old.tgz")));
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(cache, "new.tgz")));
    }

    [TestMethod]
    public void RestoreRefusesATamperedOriginal()
    {
        var src = Mk("src");
        var nm = Mk(@"src\app\node_modules");
        Mk(@"src\app\node_modules\a.js", "a");
        Assert.IsNull(Quarantine.Move(UnitOf(src, nm), 7));
        var path = Path.Combine(Quarantine.List().Single().Dir, "entry.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(nm.Replace(@"\", @"\\"), Path.Combine(dir, @"other\node_modules").Replace(@"\", @"\\")));
        Assert.AreEqual("Guard.OutsideRoot", Quarantine.Restore(Quarantine.List().Single()));
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, @"other\node_modules")));
    }

    [TestMethod]
    public void FailedPartRollsBackTheUnit()
    {
        var src = Mk("src");
        var a = Mk(@"src\models\blob-a", "a");
        var b = Mk(@"src\models\manifest", "b");
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read)) // held without FILE_SHARE_DELETE: can't be renamed
        {
            Assert.AreEqual("Status.FilesBusy", Quarantine.Move(UnitOf(src, a, b), 7));
        }
        Assert.AreEqual("a", File.ReadAllText(a));                            // the first part came back
        Assert.IsEmpty(Quarantine.List());
    }

    [TestMethod]
    public void ReconcileAfterACrash()
    {
        var src = Mk("src");
        var nm = Mk(@"src\app\node_modules");
        Mk(@"src\app\node_modules\a.js", "a");
        var venv = Mk(@"src\app\.venv");
        Mk(@"src\app\.venv\pyvenv.cfg", "x");

        // crash right after the journal: source still in place → the entry goes
        Assert.IsNull(Quarantine.Move(UnitOf(src, nm), 7));
        var e = Quarantine.List().Single();
        Assert.IsNull(Quarantine.Restore(e));
        var crashed = Path.Combine(Q, "20261021-000000-crash1");
        Directory.CreateDirectory(crashed);
        File.WriteAllText(Path.Combine(crashed, "entry.json"), $$"""
            {"id":"x","state":"Moving","parts":[{"original":"{{nm.Replace(@"\", @"\\")}}","stored":"0-node_modules","isDir":true}]}
            """);
        Quarantine.Reconcile();
        Assert.IsFalse(Directory.Exists(crashed));
        Assert.IsTrue(File.Exists(Path.Combine(nm, "a.js")));

        // crash after the first of two renames → it goes back
        var half = Path.Combine(Q, "20261021-000000-crash2");
        Directory.CreateDirectory(half);
        Directory.Move(nm, Path.Combine(half, "0-node_modules"));
        File.WriteAllText(Path.Combine(half, "entry.json"), $$"""
            {"id":"y","state":"Moving","parts":[{"original":"{{nm.Replace(@"\", @"\\")}}","stored":"0-node_modules","isDir":true},
                                                {"original":"{{venv.Replace(@"\", @"\\")}}","stored":"1-.venv","isDir":true}]}
            """);
        Quarantine.Reconcile();
        Assert.IsTrue(File.Exists(Path.Combine(nm, "a.js")));
        Assert.IsTrue(File.Exists(Path.Combine(venv, "pyvenv.cfg")));
        Assert.IsEmpty(Quarantine.List());

        // payload gone → Broken, never purged automatically
        Assert.IsNull(Quarantine.Move(UnitOf(src, nm), 7));
        e = Quarantine.List().Single();
        Directory.Delete(Path.Combine(e.Dir, "0-node_modules"), true);
        Quarantine.Reconcile();
        Assert.AreEqual(EntryState.Broken, Quarantine.List().Single().State);
    }

    [TestMethod]
    public void RunningOwnerBlocksTheMove()
    {
        var src = Mk("src");
        var cache = Mk(@"src\cache");
        Mk(@"src\cache\a.bin", "a");
        // A real process whose name starts with the owner prefix: a copy of ping that ends by itself (nothing else is killed).
        // Not named "ollama": the VRAM Bouncer session on this PC watches for that name.
        var exe = Path.Combine(dir, "reclaimer-owner-test.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-n 4 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var u = new Unit
        {
            Id = cache, Location = "test", Category = Category.Caches, Title = "cache", Root = src, Owners = ["reclaimer-owner-test"],
            Parts = [new(cache, true, Fs.Size(cache))], Bytes = Fs.Size(cache),
        };
        Assert.AreEqual("Status.OwnerRunning", Quarantine.Move(u, 7));
        Assert.AreEqual(UnitStatus.OwnerRunning, u.Status);
        Assert.AreEqual("reclaimer-owner-test", u.Owner);
        Assert.IsTrue(File.Exists(Path.Combine(cache, "a.bin")));
        Assert.IsTrue(p.WaitForExit(10_000));
        u.Status = UnitStatus.Ready;                                           // "Check again"
        Assert.IsNull(Quarantine.Move(u, 7));
    }

    [TestMethod]
    public void DebugSandboxRefusesOutside()
    {
        var src = Mk("src");
        var nm = Mk(@"src\app\node_modules");
        Quarantine.Sandbox = Mk("sandbox");
        Assert.AreEqual("Move.OutsideSandbox", Quarantine.Move(UnitOf(src, nm), 7));
        Assert.IsTrue(Directory.Exists(nm));
    }

    // ------------------------------------------------------------ purge

    [TestMethod]
    public void PurgeDeletesLinksNotTargetsAndReadOnlyFiles()
    {
        var src = Mk("src");
        var victim = Mk("victim");
        Mk(@"victim\precious.txt", "keep me");
        var nm = Mk(@"src\app\node_modules");
        Junction(Path.Combine(nm, "linked-project"), victim);                  // pnpm/npm link into a neighbour
        var ro = Mk(@"src\app\node_modules\pkg\.git\objects\ab\cdef", "obj");
        File.SetAttributes(ro, FileAttributes.ReadOnly);

        Assert.IsNull(Quarantine.Move(UnitOf(src, nm), 7));
        var (_, error) = Quarantine.Purge(Quarantine.List().Single());
        Assert.IsNull(error);
        Assert.IsEmpty(Quarantine.List());
        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(victim, "precious.txt")));
    }

    [TestMethod]
    public void PurgeRefusesAQuarantineRootThatIsALink()
    {
        var src = Mk("src");
        var nm = Mk(@"src\app\node_modules");
        Mk(@"src\app\node_modules\a.js", "a");
        Assert.IsNull(Quarantine.Move(UnitOf(src, nm), 7));
        var e = Quarantine.List().Single();

        // the root is swapped for a junction to a victim that looks like the same entry
        var victim = Mk("victim");
        Directory.CreateDirectory(Path.Combine(victim, e.Id, "0-node_modules"));
        File.WriteAllText(Path.Combine(victim, e.Id, "0-node_modules", "precious.txt"), "keep me");
        File.Copy(Path.Combine(e.Dir, "entry.json"), Path.Combine(victim, e.Id, "entry.json"));
        Directory.Move(Q, Path.Combine(dir, "real-q"));
        Junction(Q, victim);

        var swapped = Quarantine.List().Single();
        Assert.AreEqual("Quarantine.Broken", Quarantine.Purge(swapped).Error);
        Assert.AreEqual("Quarantine.Broken", Quarantine.Restore(swapped));
        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(victim, e.Id, "0-node_modules", "precious.txt")));
        Directory.Delete(Q);                                                   // the junction only
    }

    [TestMethod]
    public void ExpiryIsSevenDaysInUtc()
    {
        var created = new DateTime(2026, 10, 21, 23, 30, 0, DateTimeKind.Utc);
        var e = new Entry { State = EntryState.Done, CreatedUtc = created, ExpiresUtc = created.AddDays(7) };
        Assert.IsFalse(e.IsExpired(created.AddDays(7).AddSeconds(-1)));
        Assert.IsTrue(e.IsExpired(created.AddDays(7)));
        e.State = EntryState.Broken;
        Assert.IsFalse(e.IsExpired(created.AddDays(30)));                      // Broken is never purged automatically
    }
}
