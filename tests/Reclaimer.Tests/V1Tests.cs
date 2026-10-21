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
