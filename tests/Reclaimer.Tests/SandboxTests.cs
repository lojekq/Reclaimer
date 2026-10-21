using System.IO;
using Reclaimer.Features;

namespace Reclaimer.Tests;

/// <summary>A read-only scan of C:\Demo\reclaimer-sandbox (tools/sandbox.ps1). Skipped where the sandbox doesn't exist (CI).</summary>
[TestClass]
[DoNotParallelize]
public class SandboxTests
{
    [TestMethod]
    public async Task ScanFindsTheSandboxUnits()
    {
        if (!Directory.Exists(Quarantine.SandboxDir)) Assert.Inconclusive("no sandbox: pwsh tools/sandbox.ps1");
#if !DEBUG
        Assert.Inconclusive("the sandbox roots are the Debug defaults");
#endif
        Scan.UseSandboxInDebug();
        PathGuard.Excluded = () => [];
        Quarantine.Roots = () => [Path.Combine(Quarantine.SandboxDir, ".reclaimer-quarantine")];
        var units = new List<Unit>();
        await Scan.Run(u => { lock (units) units.Add(u); }, _ => { }, default);
        var t = units.ToDictionary(u => u.Title, u => u);

        Assert.AreEqual(UnitStatus.NotIgnored, t[@"tracked-deps\node_modules"].Status);
        Assert.AreEqual(UnitStatus.Ready, t[@"old-telegram-bot\node_modules"].Status);
        Assert.AreEqual(UsedSource.Commit, t[@"old-telegram-bot\node_modules"].Source);
        Assert.IsTrue(t[@"old-telegram-bot\node_modules"].AgeDays >= 213);
        Assert.HasCount(2, t[@"shop-api\bin + obj"].Parts);
        Assert.IsTrue(t.ContainsKey(@"rust-raytracer\target"));
        Assert.IsTrue(t.ContainsKey(@"nogit-scraper\.venv"));
        Assert.IsTrue(t.ContainsKey("tinyllama:latest") && t.ContainsKey("tinyllama:q8"));
        Assert.HasCount(3, t["tinyllama:latest"].Parts);                         // weights + config + manifest; the license layer is shared
        Assert.AreEqual(300L << 20, t["tinyllama:latest"].Parts.Where(p => p.Bytes >= 1 << 20).Sum(p => p.Bytes));
        Assert.IsTrue(t.ContainsKey("models--demo--tiny-llama"));
        Assert.IsTrue(t.ContainsKey("demo-sd15.safetensors"));
        Assert.IsFalse(t.ContainsKey("tiny.safetensors"));                       // under 100 MB
        Assert.IsTrue(units.Any(u => u.Location == "npm") && units.Any(u => u.Location == "nuget"));
        // v1.0
        Assert.AreEqual(70L << 20, units.Single(u => u.Id.Contains("|detached|")).Bytes);
        Assert.HasCount(1, units.Single(u => u.Id.Contains("|incomplete|")).Parts);
        foreach (var id in new[] { "torch-hub", "llama-cpp", "gradle", "go-mod", "yarn" })
            Assert.IsTrue(units.Any(u => u.Location == id), id);
        Assert.IsTrue(units.All(u => u.Parts.All(p => p.Path.StartsWith(Quarantine.SandboxDir, StringComparison.OrdinalIgnoreCase) || u.Location == "lmstudio")));
    }
}
