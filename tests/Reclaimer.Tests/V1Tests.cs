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
