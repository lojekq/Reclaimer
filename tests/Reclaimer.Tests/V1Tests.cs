using System.IO;
using Reclaimer.Features;

namespace Reclaimer.Tests;

/// <summary>v1.0 scanners and helpers, in temp folders.</summary>
[TestClass]
[DoNotParallelize]
public class V1Tests
{
    public TestContext TestContext { get; set; } = null!;
    string dir = null!;

    [TestInitialize]
    public void Init()
    {
        dir = Path.Combine(Path.GetTempPath(), "Reclaimer.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
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

    [TestMethod]
    public void OllamaLastRunComesFromTheLog()
    {
        var a = new string('a', 64);
        var b = new string('b', 64);
        Mk("server-1.log", $"""
            time=2026-09-14T15:35:00.673+05:00 level=INFO source=llama_server.go:433 msg="starting llama-server" cmd="C:\\x\\llama-server.exe --model C:\\Users\\demo\\.ollama\\models\\blobs\\sha256-{a} --port 1"
            time=2026-09-20T10:00:00.000+05:00 level=INFO source=llama_server.go:1048 msg="loading model via llama-server" model=C:\Users\demo\.ollama\models\blobs\sha256-{a}
            garbage line sha256-{b}
            """);
        Mk("server.log", $"time=2026-09-01T00:00:00.000Z level=INFO msg=\"starting llama server\" cmd=\"runner --model D:\\m\\blobs\\sha256-{b} --ctx-size 2048\"");
        Mk("app.log", $"time=2027-01-01T00:00:00.000Z model=x\\sha256-{b}");  // not a server log

        var runs = OllamaScanner.LastRuns(dir);
        Assert.AreEqual(new DateTime(2026, 9, 20, 5, 0, 0, DateTimeKind.Utc), runs[a]);
        Assert.AreEqual(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), runs[b]);
        Assert.HasCount(2, runs);
        Assert.IsEmpty(OllamaScanner.LastRuns(Path.Combine(dir, "missing")));
    }

    [TestMethod]
    public void HistoryReadsNewestFirstAndReportEscapesPipes()
    {
        Quarantine.HistoryPath = Mk("history.jsonl", """
            {"ts":"2026-10-01T00:00:00Z","action":"move","paths":["C:\\a\\node_modules"],"bytes":10,"result":"ok","freedBytes":0}
            half a line {"ts":
            {"ts":"2026-10-08T00:00:00Z","action":"purge","paths":["C:\\q\\0-node_modules"],"bytes":10,"result":"ok","freedBytes":8}
            """);
        var rows = Quarantine.ReadHistory();
        Assert.HasCount(2, rows);
        Assert.AreEqual("purge", rows[0].Action);
        Assert.AreEqual(8, rows[0].Freed);

        var u = new Unit
        {
            Id = "x", Location = "test", Category = Category.Projects, Title = "a|b", Root = @"C:\r", Parts = [new(@"C:\r\a|b", true, 3 << 20)], Bytes = 3 << 20,
        };
        var md = Report.Markdown([u], [], [], new DateTime(2026, 10, 21, 14, 30, 0));
        StringAssert.Contains(md, @"a\|b");
        StringAssert.Contains(md, "2026-10-21 14:30");
    }

    [TestMethod]
    public void LaunchersExtractModelPathsFromScripts()
    {
        var d = Path.Combine(dir, "mlink");
        Directory.CreateDirectory(d);
        var bat = """
            @echo off
            setlocal
            set "MODEL_DIR=D:\llm\models"
            set "PREFIX=Qwen3-27B"
            set "QUANT=%~1"
            if "%QUANT%"=="" set "QUANT=Q2_K"
            set "MODEL=%MODEL_DIR%\%PREFIX%-%QUANT%.gguf"
            set "TEMPLATE=%~dp0scripts\chat.jinja"
            start "" llama-server.exe -m "%MODEL%"
            """;
        var ps1 = """
            $orca = 'D:\llm\orca'
            $profiles = @{ Orca = @{ Model = "$orca\OrcaRouter-IQ3.gguf"; Mmproj = "${orca}\mmproj\mmproj-Q8.gguf" } }
            $local = "$PSScriptRoot\local.safetensors"
            """;
        var json = """{ "model": "E:\\weights\\sd_xl.safetensors", "other": "x.gguf" }""";
        CollectionAssert.AreEquivalent(new[] { @"D:\llm\models\Qwen3-27B-Q2_K.gguf" }, Launchers.Extract(bat, ".bat", d).ToList());
        CollectionAssert.AreEquivalent(new[] { @"D:\llm\orca\OrcaRouter-IQ3.gguf", @"D:\llm\orca\mmproj\mmproj-Q8.gguf", Path.Combine(d, "local.safetensors") },
            Launchers.Extract(ps1, ".ps1", d).ToList());
        CollectionAssert.AreEquivalent(new[] { @"E:\weights\sd_xl.safetensors" }, Launchers.Extract(json, ".json", d).ToList());
    }

    [TestMethod]
    public void OwnModelHeldByScriptProcessOrOpenHandle()
    {
        var uniq = Guid.NewGuid().ToString("N")[..8];
        var free = Mk($@"models\free-{uniq}.gguf", "weights");
        var scripted = Mk($@"models\scripted-{uniq}.gguf", "weights");
        var opened = Mk($@"models\opened-{uniq}.gguf", "weights");
        var byCmd = Mk($@"models\cmd-{uniq}.gguf", "weights");
        Mk(@"launch\run.bat", $"set \"MODEL={scripted}\"\r\nllama-server -m \"%MODEL%\"\r\n");
        var dirs = Launchers.Dirs;
        Launchers.Dirs = () => [("test", Path.Combine(dir, "launch"))];
        try
        {
            using var handle = new FileStream(opened, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping -n 6 127.0.0.1 >nul & rem {byCmd}")
                { CreateNoWindow = true, UseShellExecute = false })!;
            try
            {
                Thread.Sleep(300);
                var h = Holders.Snapshot();
                Assert.IsNull(h.Of(free));
                Assert.AreEqual("run.bat (test)", h.Of(scripted));
                Assert.AreEqual("cmd", h.Of(byCmd));                             // llama-server -m <file>: the command line names it
                Assert.IsNotNull(h.Of(opened));                                   // ComfyUI, LM Studio: an open handle
            }
            finally { p.Kill(entireProcessTree: true); }
        }
        finally { Launchers.Dirs = dirs; }
    }

    [TestMethod]
    public void OriginOfModels()
    {
        Assert.AreEqual("https://huggingface.co/black-forest-labs/FLUX.1-dev", Origin.Hf(@"C:\hub\models--black-forest-labs--FLUX.1-dev"));
        Assert.AreEqual("https://huggingface.co/datasets/org/data", Origin.Hf(@"C:\hub\datasets--org--data"));
        Assert.IsNull(Origin.Hf(@"C:\hub\blobs"));
        Assert.AreEqual("https://huggingface.co/Qwen/Qwen3-32B-GGUF", Origin.LmStudio(@"C:\u\.lmstudio\models", @"C:\u\.lmstudio\models\Qwen\Qwen3-32B-GGUF\Qwen3-32B-Q4_K_M.gguf"));
        Assert.IsNull(Origin.LmStudio(@"C:\u\.lmstudio\models", @"C:\u\.lmstudio\models\loose.gguf"));

        var civ = Mk(@"m\lora.safetensors", "w");
        Mk(@"m\lora.civitai.info", """{"id":3362156,"modelId":2967285,"files":[{"downloadUrl":"https://civitai.com/api/download/models/3362156?fileId=1"}]}""");
        Assert.AreEqual("https://civitai.com/models/2967285?modelVersionId=3362156", Origin.OfFile(civ));     // .civitai.info: the model page
        var meta = Mk(@"m\other.safetensors", "w");
        Mk(@"m\other.metadata.json", """{"from_civitai":true,"preview_url":"","civitai":{"id":10,"modelId":20,"images":[{"url":"https://image.civitai.com/x.jpeg"}]}}""");
        Assert.AreEqual("https://civitai.com/models/20?modelVersionId=10", Origin.OfFile(meta));              // .metadata.json: ids under "civitai"
        var llama = Mk(@"m\org_repo_q4.gguf", "w");
        Mk(@"m\org_repo_q4.gguf.json", """{"url":"https://huggingface.co/org/repo/resolve/main/q4.gguf?download=true","etag":"x"}""");
        Assert.AreEqual("https://huggingface.co/org/repo/resolve/main/q4.gguf", Origin.OfFile(llama));       // llama.cpp's sidecar
        var deleted = Mk(@"m\deleted.safetensors", "w");
        Mk(@"m\deleted.metadata.json", """{"from_civitai":true,"civitai_deleted":true,"preview_url":"","images":[{"url":"https://image.civitai.com/y.jpeg"}]}""");
        Assert.IsNull(Origin.OfFile(deleted));                                                                // no ids, no source key: previews don't count
        var zone = Mk(@"m\downloaded.safetensors", "w");
        File.WriteAllText(zone + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://civitai.red/\r\nHostUrl=https://b2.civitai.com/file/x.safetensors?Authorization=secret&b2=1 \r\n");
        Assert.AreEqual("https://b2.civitai.com/file/x.safetensors", Origin.OfFile(zone));                   // the token in the query never shows
        var referred = Mk(@"m\referred.safetensors", "w");
        File.WriteAllText(referred + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://example.com/page?x=1\r\n");
        Assert.AreEqual("https://example.com/page", Origin.OfFile(referred));
        var cdn = Mk(@"m\cdn.safetensors", "w");
        File.WriteAllText(cdn + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://huggingface.co/org/repo/tree/main\r\nHostUrl=https://us.aws.cdn.hf.co/xet-bridge-us/690dc0e4/fb01e4?X-Xet-Signed=1\r\n");
        Assert.AreEqual("https://huggingface.co/org/repo/tree/main", Origin.OfFile(cdn));                     // the page beats a hashed CDN object
        Assert.IsNull(Origin.OfFile(Mk(@"m\own.safetensors", "w")));                                          // trained at home

        Assert.AreEqual("civitai.com", Origin.Site("https://civitai.com/models/1"));
        Assert.AreEqual("huggingface.co", Origin.Site("https://www.huggingface.co/x"));
        Assert.AreEqual("ollama pull", Origin.Site("ollama pull llama3.1:70b"));
        Assert.AreEqual("huggingface.co", Origin.Site("https://us.aws.cdn.hf.co/xet-bridge-us/x"));
        Assert.AreEqual("civitai.com", Origin.Site("https://b2.civitai.com/file/x.safetensors"));
        var unknown = new Unit { Id = "x", Location = "own-models", Category = Category.OwnModels, Title = "own.safetensors", Root = dir, Parts = [], Rebuild = "Rebuild.Download" };
        Assert.IsTrue(unknown.OriginUnknown);
        var known = new Unit { Id = "y", Location = "own-models", Category = Category.OwnModels, Title = "lora.safetensors", Root = dir, Parts = [], Rebuild = "Rebuild.Download", Origin = "https://civitai.com/models/1" };
        Assert.IsFalse(known.OriginUnknown);
        Assert.AreEqual("civitai.com", known.RebuildSub);
        Assert.AreEqual("https://civitai.com/models/1", known.RebuildTip);
        var cache = new Unit { Id = "z", Location = "npm", Category = Category.Caches, Title = "npm", Root = dir, Parts = [], Rebuild = "Rebuild.Cache" };
        Assert.IsFalse(cache.OriginUnknown);                                                                  // only downloads can have an unknown source
    }

    [TestMethod]
    public void DockerOutputIsParsed()
    {
        var rows = Docker.Parse("""
            {"Active":"3","Reclaimable":"8.2GB (78%)","Size":"10.5GB","TotalCount":"12","Type":"Images"}
            not json
            {"Active":"0","Reclaimable":"1.1GB","Size":"1.1GB","TotalCount":"40","Type":"Build Cache"}
            """);
        Assert.HasCount(2, rows);
        Assert.AreEqual(new DockerRow("Images", "10.5GB", "8.2GB (78%)"), rows[0]);
        Assert.AreEqual("1.2GB", Docker.Reclaimed("Deleted build cache objects:\nabc\n\nTotal reclaimed space: 1.2GB\n"));
        Assert.IsNull(Docker.Reclaimed("error during connect"));
    }

    [TestMethod]
    public void CompactVerbTrustsOnlyKnownDisks()
    {
        var vhdx = Mk(@"wsl\ext4.vhdx", "not really a disk");
        Assert.IsTrue(VhdxCompact.Allowed(vhdx, [vhdx.ToUpperInvariant()]));
        Assert.IsFalse(VhdxCompact.Allowed(vhdx, []));                                    // not a disk Reclaimer found
        Assert.IsFalse(VhdxCompact.Allowed(Mk(@"wsl\x.vhd", "x"), [Path.Combine(dir, @"wsl\x.vhd")]));
        var quoted = Path.Combine(dir, "a\"b.vhdx");
        Assert.IsFalse(VhdxCompact.Allowed(quoted, [quoted]));                            // would break out of the diskpart script
        Assert.IsFalse(VhdxCompact.Allowed(@"wsl\ext4.vhdx", [@"wsl\ext4.vhdx"]));
        Assert.AreEqual(2, VhdxCompact.Run([vhdx]));                                      // the verb refuses before anything else
        Assert.AreEqual(2, VhdxCompact.Run([]));

        var (compact, detach) = VhdxCompact.Scripts(@"C:\wsl\ext4.vhdx");
        Assert.AreEqual("select vdisk file=\"C:\\wsl\\ext4.vhdx\"\r\nattach vdisk readonly\r\ncompact vdisk\r\n", compact);
        StringAssert.Contains(detach, "detach vdisk noerr");
    }

    [TestMethod]
    public void HfOldRevisionsAndUnfinishedDownloads()
    {
        var hub = Path.Combine(dir, "hub");
        Mk(@"hub\models--a--b\refs\main", "rev2\n");
        Mk(@"hub\models--a--b\snapshots\rev1\model.bin", new string('1', 1000)); // no symlinks: files sit in snapshots
        Mk(@"hub\models--a--b\snapshots\rev2\model.bin", new string('2', 2000));
        var old = Mk(@"hub\models--a--b\blobs\deadbeef.incomplete", "partial");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        Mk(@"hub\models--c--d\blobs\cafe.incomplete", "downloading now");     // fresh: a download may be running
        Mk(@"hub\models--c--d\snapshots\only\x.bin", "x");                    // no refs: left alone
        var l = Catalog.All.Single(x => x.Id == "hf-hub");

        var detached = HfScanner.Detached(hub, Path.Combine(hub, "models--a--b"), l)!;
        Assert.AreEqual(Path.Combine(hub, @"models--a--b\snapshots\rev1"), detached.Parts.Single().Path);
        Assert.AreEqual(1000, detached.Bytes);
        Assert.IsNull(HfScanner.Detached(hub, Path.Combine(hub, "models--c--d"), l));

        var incomplete = HfScanner.Incomplete(hub, l)!;
        Assert.AreEqual(old, incomplete.Parts.Single().Path);
    }
}
