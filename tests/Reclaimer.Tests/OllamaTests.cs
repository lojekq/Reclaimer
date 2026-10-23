using System.Diagnostics;
using System.IO;
using System.Text;
using Reclaimer.Features;

namespace Reclaimer.Tests;

/// <summary>Ollama 0.40 layouts (manifests-v2, metadata) and a live round trip against the real ollama.exe on a copy of a model.</summary>
[TestClass]
[DoNotParallelize]
public class OllamaTests
{
    public TestContext TestContext { get; set; } = null!;
    string dir = null!;
    static readonly Location Loc = Catalog.All.Single(x => x.Id == "ollama");

    [TestInitialize]
    public void Init()
    {
        dir = Path.Combine(Path.GetTempPath(), "Reclaimer.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        OllamaScanner.LogDir = Path.Combine(dir, "no-logs");
    }

    [TestCleanup]
    public void Cleanup() => Quarantine.DeleteTree(dir, Path.GetTempPath(), default);

    string Mk(string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    static string Hex(char c) => new(c, 64);
    static string Manifest(string config, params string[] layers) =>
        $$"""{"schemaVersion":2,"config":{"digest":"sha256:{{config}}","size":1},"layers":[{{string.Join(',', layers.Select(h => $$"""{"digest":"sha256:{{h}}","size":1}"""))}}]}""";

    [TestMethod]
    public void V2OnlyModelTakesItsManifestBlobsAndMetadata()
    {
        var models = Path.Combine(dir, "models");
        var weights = Mk($@"models\blobs\sha256-{Hex('a')}", new string('w', 5000));
        var config = Mk($@"models\blobs\sha256-{Hex('b')}", "{}");
        var manifestBlob = Mk($@"models\blobs\sha256-{Hex('c')}", Manifest(Hex('b'), Hex('a')));
        var meta = Mk($@"models\metadata\sha256-{Hex('a')}.json", """{"ollama_version":"0.40.9","kv":{}}""");
        var orphan = Mk($@"models\blobs\sha256-{Hex('d')}", "nobody names me");
        Mk($@"models\blobs\sha256-{Hex('e')}", "metadata knows me");
        Mk($@"models\metadata\sha256-{Hex('e')}.json", "{}");
        var v2 = Mk(@"models\manifests-v2\ollama.com\library\embeddinggemma-2\270m",
            $$"""{"schemaVersion":2,"manifests":[{"digest":"sha256:{{Hex('c')}}","runner":"llamacpp"},{"digest":"sha256:{{Hex('f')}}","runner":"mlx"}]}""");

        var units = OllamaScanner.Scan(models, Loc, ollamaRunning: false, default);
        var model = units.Single(u => u.Title == "embeddinggemma-2:270m");
        CollectionAssert.AreEquivalent(new[] { weights, config, manifestBlob, meta, v2 }, model.Parts.Select(p => p.Path).ToList());
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 1, 2 }, model.Parts.Select(p => Order(p.Path)).ToList()); // blobs, then metadata, manifest last
        var orphans = units.Single(u => u.Id.EndsWith("orphans"));
        Assert.AreEqual(orphan, orphans.Parts.Single().Path);                       // the blob with metadata is not an orphan
        static int Order(string p) => p.Contains(@"\metadata\") ? 1 : p.Contains(@"\manifests") ? 2 : 0;
    }

    [TestMethod]
    public void BothLayoutsOfOneModelAreOneUnit()
    {
        var models = Path.Combine(dir, "models");
        Mk($@"models\blobs\sha256-{Hex('a')}", "weights");
        Mk($@"models\blobs\sha256-{Hex('b')}", "{}");
        Mk($@"models\blobs\sha256-{Hex('c')}", Manifest(Hex('b'), Hex('a')));
        Mk($@"models\blobs\sha256-{Hex('1')}", "shared layer");
        var old = Mk(@"models\manifests\registry.ollama.ai\library\tiny\latest", Manifest(Hex('b'), Hex('a'), Hex('c'), Hex('1')));
        Mk(@"models\manifests-v2\ollama.com\library\tiny\latest", $$"""{"manifests":[{"digest":"sha256:{{Hex('c')}}"}]}""");
        Mk(@"models\manifests\registry.ollama.ai\library\other\latest", Manifest(Hex('b'), Hex('1')));

        var units = OllamaScanner.Scan(models, Loc, ollamaRunning: false, default);
        var tiny = units.Single(u => u.Title == "tiny:latest");
        Assert.AreEqual(old, tiny.Path);
        CollectionAssert.AreEquivalent(new[] { "sha256-" + Hex('a'), "sha256-" + Hex('c'), "latest", "latest" }, tiny.Parts.Select(p => Path.GetFileName(p.Path)).ToList());
        Assert.IsFalse(units.Any(u => u.Id.EndsWith("orphans")));                   // config b and layer 1 are shared, not orphans
    }

    /// <summary>
    /// Set RECLAIMER_LIVE_OLLAMA to the path of ollama.exe (0.40+) and have embeddinggemma-2:270m in your own store: the test copies that
    /// model into a temp store, serves it on 127.0.0.1:11444, moves it to a temp quarantine, checks "ollama list" forgets it,
    /// restores it and checks "ollama show" works again. The real store is only read.
    /// </summary>
    [TestMethod]
    public void LiveRoundTripWithOllama040()
    {
        var exe = Environment.GetEnvironmentVariable("RECLAIMER_LIVE_OLLAMA");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) Assert.Inconclusive("RECLAIMER_LIVE_OLLAMA is not set");
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".ollama\models");
        const string Name = "embeddinggemma-2:270m";
        var realV2 = Path.Combine(real, @"manifests-v2\ollama.com\library\embeddinggemma-2\270m");
        if (!File.Exists(realV2)) Assert.Inconclusive($"{Name} is not in {real}");

        // A copy of just that model, through the scanner's own notion of its parts (read-only on the real store)
        var models = Path.Combine(dir, "models");
        var src = OllamaScanner.Scan(real, Loc, ollamaRunning: true, default).Single(u => u.Title == Name);
        foreach (var p in src.Parts)
        {
            var dst = Path.Combine(models, Path.GetRelativePath(real, p.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(p.Path, dst);
        }
        Assert.IsTrue(src.Parts.Count >= 6, $"parts: {src.Parts.Count}"); // blobs, metadata, two manifests

        const string Host = "127.0.0.1:11444";
        Quarantine.Sandbox = dir;
        Quarantine.RootFor = _ => Path.Combine(dir, "q");
        Quarantine.Roots = () => [Path.Combine(dir, "q")];
        Quarantine.AllowedRoots = () => [models];
        Quarantine.HistoryPath = Path.Combine(dir, "history.jsonl");
        PathGuard.Excluded = () => [];
        var names = OwnerCheck.ProcessNames;
        OwnerCheck.ProcessNames = () => []; // the owner's own Ollama may be running; ours is the only one that sees this store
        try
        {
            using (Serve(exe, models, Host))
                StringAssert.Contains(Cli(exe, Host, "list"), Name);

            var unit = OllamaScanner.Scan(models, Loc, ollamaRunning: false, default).Single(u => u.Title == Name);
            Assert.IsFalse(OllamaScanner.Scan(models, Loc, ollamaRunning: false, default).Any(u => u.Id.EndsWith("orphans")));
            Assert.IsNull(Quarantine.Move(unit, 7));
            Assert.IsFalse(File.Exists(Path.Combine(models, @"manifests-v2\ollama.com\library\embeddinggemma-2\270m")));
            Assert.IsFalse(Directory.EnumerateFiles(Path.Combine(models, "metadata")).Any());

            using (Serve(exe, models, Host))
                Assert.DoesNotContain(Name, Cli(exe, Host, "list"));

            Assert.IsNull(Quarantine.Restore(Quarantine.List().Single()));
            using (Serve(exe, models, Host))
            {
                var show = Cli(exe, Host, "show", Name);
                StringAssert.Contains(show, "embedding", $"show: {show}");
                StringAssert.Contains(Cli(exe, Host, "list"), Name);
            }
            Assert.IsEmpty(OllamaScanner.Scan(models, Loc, ollamaRunning: false, default).Where(u => u.Id.EndsWith("orphans")));
        }
        finally { OwnerCheck.ProcessNames = names; }
    }

    /// <summary>ollama serve on our port with our store; disposed = killed with its children.</summary>
    static IDisposable Serve(string exe, string models, string host)
    {
        var psi = new ProcessStartInfo(exe, "serve") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["OLLAMA_MODELS"] = models;
        psi.Environment["OLLAMA_HOST"] = host;
        var p = Process.Start(psi)!;
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 60; i++)
        {
            try { if (http.GetAsync($"http://{host}/api/version").Result.IsSuccessStatusCode) return new Killer(p); }
            catch (Exception e) when (e is HttpRequestException or AggregateException or TaskCanceledException) { }
            Thread.Sleep(500);
        }
        p.Kill(entireProcessTree: true);
        throw new TimeoutException("ollama serve did not come up");
    }

    static string Cli(string exe, string host, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["OLLAMA_HOST"] = host;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    sealed class Killer(Process p) : IDisposable
    {
        public void Dispose()
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
            p.Dispose();
            Thread.Sleep(500); // the store is free again
        }
    }
}
