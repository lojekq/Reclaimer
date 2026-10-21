using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>
/// Compacting a WSL or Docker disk (07 §3, v1.0): optional fstrim inside the distribution → Docker Desktop closed by the user →
/// wsl --shutdown → the file is free → "Reclaimer.exe --compact-vhdx path" with UAC → diskpart attach readonly, compact, detach.
/// Only the diskpart step is elevated; the window never is (00-shared §2.3).
/// </summary>
public static class VhdxCompact
{
    public const string Verb = "--compact-vhdx";

    /// <summary>attach readonly + compact (stops at the first error), then a separate detach that always runs.</summary>
    internal static (string Compact, string Detach) Scripts(string vhdx) =>
        ($"select vdisk file=\"{vhdx}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\n", $"select vdisk file=\"{vhdx}\"\r\ndetach vdisk noerr\r\n");

    /// <summary>
    /// The elevated verb trusts nothing on its command line: a full path to an existing .vhdx without quotes (it goes into a diskpart
    /// script) that is one of the disks Reclaimer itself finds for this user.
    /// </summary>
    internal static bool Allowed(string path, IEnumerable<string> known) =>
        path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) && !path.Contains('"') && Path.IsPathFullyQualified(path) && File.Exists(path)
        && known.Any(k => string.Equals(Path.GetFullPath(k), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>Exit codes: 0 compacted, 2 refused (unknown path), 3 not elevated, 4 file in use, 5 diskpart failed.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 1 || !Allowed(args[0], VhdxScanner.Candidates().Select(c => c.Path))) { Log.Warn("compact refused: not a known disk"); return 2; }
        if (!AppInfo.IsElevated) return 3;
        var path = Path.GetFullPath(args[0]);
        if (InUse(path)) return 4;
        var (compact, detach) = Scripts(path);
        var code = Diskpart(compact);
        Diskpart(detach); // a failed compact must not leave the disk attached
        Log.Info($"compact: diskpart exit {code}");
        return code == 0 ? 0 : 5;
    }

    static int Diskpart(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"reclaimer-diskpart-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(file, script, Encoding.ASCII.GetByteCount(script) == script.Length ? Encoding.ASCII : Encoding.Unicode);
            return ConsoleTool.Run("diskpart.exe", ["/s", file], TimeSpan.FromHours(2)).ExitCode;
        }
        finally { File.Delete(file); }
    }

    /// <summary>Opened with FileShare.None: if that fails, WSL, Docker or something else still holds the disk.</summary>
    public static bool InUse(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// The steps after the user agreed (and closed Docker Desktop). Returns null or the reason as a Loc key.
    /// Debug builds refuse: wsl --shutdown stops every distribution on this PC (00-shared §9.4, UAC slot).
    /// </summary>
    public static string? Steps(Disk d, bool fstrim)
    {
#if DEBUG
        if (Quarantine.Sandbox is not null) return "Compact.Debug";
#endif
        if (fstrim && d.Name != VhdxScanner.DockerName)
            try
            {
                var (code, _) = ConsoleTool.Run("wsl.exe", ["-d", d.Name, "-u", "root", "fstrim", "-av"], TimeSpan.FromMinutes(5), Encoding.UTF8);
                Log.Info($"compact: fstrim exit {code}"); // optional: a distribution without fstrim is still compacted
            }
            catch (Exception e) when (e is Win32Exception or TimeoutException) { Log.Info($"compact: fstrim {e.GetType().Name}"); }
        try
        {
            if (ConsoleTool.Run("wsl.exe", ["--shutdown"], TimeSpan.FromMinutes(2), Encoding.Unicode).ExitCode != 0) return "Compact.ShutdownFailed";
        }
        catch (Exception e) when (e is Win32Exception or TimeoutException) { return "Compact.ShutdownFailed"; }
        for (var i = 0; i < 30 && InUse(d.Path); i++) Thread.Sleep(500);
        if (InUse(d.Path)) return "Compact.InUse";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(AppInfo.ExePath, $"{Verb} \"{d.Path}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit();
            return p.ExitCode switch { 0 => null, 2 => "Compact.Refused", 4 => "Compact.InUse", _ => "Compact.Failed" };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return "Compact.Cancelled"; } // "No" in the UAC prompt
    }
}
