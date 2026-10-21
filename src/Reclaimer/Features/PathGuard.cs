using System.IO;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>
/// Every check on a path before anything touches it (07 §8.1). Called by the scanner and again right before a move or a restore.
/// IsUnder and IsReparsePoint come from Shell.Paths; resolving links in the ancestors and the stop-list live here.
/// </summary>
public static class PathGuard
{
    /// <summary>Extra folders that are never touched: Settings.Excluded, plus the repositories folder in Debug. Tests replace it.</summary>
    internal static Func<IEnumerable<string>> Excluded = () => Settings.Current.Excluded.Concat(DebugExcluded());

    static readonly string[] StopUnder = Known(
        Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData);

    static readonly string[] StopExact = [.. Known(
        Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic,
        Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData), DownloadsOrNull()];

    /// <summary>
    /// Null when <paramref name="path"/> may be moved (mustExist) or restored to (!mustExist), otherwise the reason as a Loc key.
    /// The path must be inside root after links in both are resolved; the target itself must not be a link.
    /// </summary>
    public static string? Check(string path, string root, bool mustExist = true)
    {
        string real, realRoot;
        try
        {
            if (mustExist && !Path.Exists(path)) return "Guard.Missing";
            if (mustExist && Paths.IsReparsePoint(path)) return "Guard.Link"; // moving a junction moves the link, not the data
            real = Real(path);
            realRoot = Resolve(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return "Guard.Unresolvable"; }

        if (!Paths.IsUnder(realRoot, real)) return "Guard.OutsideRoot";
        if (real.TrimEnd('\\').Split('\\').Length < 3) return "Guard.TooShort";
        if (StopUnder.Any(s => Paths.IsUnder(s, real)) || StopExact.Any(s => Same(s, real))) return "Guard.StopList";
        if (Quarantine.KnownRoots().Any(q => Paths.IsUnder(q, real) || Paths.IsUnder(real, q))) return "Guard.Quarantine";
        if (Excluded().Any(e => Paths.IsUnder(e, real))) return "Guard.Excluded";
        if (mustExist && Directory.Exists(real) && Path.Exists(Path.Combine(real, ".git"))) return "Guard.Git";
        return null;
    }

    /// <summary>The path with links in its ancestors resolved; the last segment is kept as is (it may be a link we refuse, or not exist yet).</summary>
    public static string Real(string path)
    {
        var full = Path.GetFullPath(Strip(path)).TrimEnd('\\');
        var parent = Path.GetDirectoryName(full);
        return parent is null ? full + '\\' : Path.Combine(Resolve(parent), Path.GetFileName(full));
    }

    /// <summary>Every existing component that is a junction or symlink is replaced by its final target. Throws on a link it can't resolve (cloud placeholder).</summary>
    public static string Resolve(string path, int depth = 0)
    {
        if (depth > 8) throw new IOException("link loop");
        var full = Path.GetFullPath(Strip(path));
        var root = Path.GetPathRoot(full)!;
        var cur = root;
        foreach (var seg in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, seg);
            var d = new DirectoryInfo(cur);
            if (!d.Exists || !d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var target = d.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException("unresolvable reparse point");
            cur = Resolve(target.FullName, depth + 1);
        }
        return cur;
    }

    /// <summary>
    /// OneDrive folders and cloud placeholders (RecallOnOpen 0x40000, RecallOnDataAccess 0x400000, Offline):
    /// moving a placeholder out of a sync root breaks it or starts a download.
    /// </summary>
    public static bool IsCloud(string path)
    {
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } od && Paths.IsUnder(od, path)) return true;
        try { return ((int)File.GetAttributes(path) & (0x40000 | 0x400000 | (int)FileAttributes.Offline)) != 0; }
        catch (IOException) { return false; }
    }

    static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    static string Strip(string p) =>
        p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + p[8..] : p.StartsWith(@"\\?\") ? p[4..] : p;

    static string[] Known(params Environment.SpecialFolder[] folders) =>
        folders.Select(Environment.GetFolderPath).Where(p => p.Length > 0).ToArray();

    static string DownloadsOrNull()
    {
        try { return Paths.Downloads; }
        catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); }
    }

    /// <summary>Debug: the folder that holds this repository (the root of all eight repos on the owner's PC) is never touched.</summary>
    static IEnumerable<string> DebugExcluded()
    {
#if DEBUG
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Reclaimer.slnx")))
            {
                yield return d.Parent?.FullName ?? d.FullName;
                yield break;
            }
#endif
        yield break;
    }
}
