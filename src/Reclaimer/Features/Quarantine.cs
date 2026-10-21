using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reclaimer.Shell;

namespace Reclaimer.Features;

public enum EntryState { Moving, Done, Restoring, Purging, Broken }

/// <summary>A line of history.jsonl for the History page.</summary>
public sealed record HistoryRow(DateTime Utc, string Action, string[] Paths, long Bytes, string Result, long Freed)
{
    public string WhenText => Utc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentUICulture);
    public string ActionText => Loc.T("History." + Action);
    public string PathText => Paths.Length == 0 ? "" : Dialog.Short(Paths[^1]) + (Paths.Length > 1 ? "  " + string.Format(Loc.T("Confirm.Parts"), Paths.Length) : "");
    public string SizeText => Format.Size(Bytes);
    public string ResultText => Result switch
    {
        "ok" => Freed > 0 ? string.Format(Loc.T("History.Freed"), Format.Size(Freed)) : Loc.T("History.Ok"),
        "rolled back" => Loc.T("History.RolledBack"),
        _ when Result.StartsWith("error ") => string.Format(Loc.T("History.Error"), Result[6..]),
        _ => Result,
    };
    public bool Failed => Result != "ok" && Result != "rolled back";
}

public sealed class EntryPart
{
    public string Original { get; set; } = "";  // real path (links in the ancestors resolved) it came from
    public string Stored { get; set; } = "";    // a plain name inside the entry folder: "0-node_modules"
    public bool IsDir { get; set; }
    public long Bytes { get; set; }
}

/// <summary>&lt;root&gt;\20261021-143005-a1b2c3\entry.json next to the renamed parts. The journal lives with the data: no database.</summary>
public sealed class Entry
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public EntryState State { get; set; }
    public Category Category { get; set; }
    public string Title { get; set; } = "";
    public DateTime? LastUsed { get; set; }
    public long TotalBytes { get; set; }
    public List<EntryPart> Parts { get; set; } = [];

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public string Root => Path.GetDirectoryName(Dir)!;
    [JsonIgnore] public string Original => Parts.Count > 0 ? Parts[^1].Original : "";

    public override string ToString() => Title;

    public bool IsExpired(DateTime nowUtc) => State == EntryState.Done && nowUtc >= ExpiresUtc;

    // For the list
    [JsonIgnore] public string SizeText => Format.Size(TotalBytes);
    [JsonIgnore] public string WhenText => Format.Ago(CreatedUtc);
    [JsonIgnore] public bool IsBroken => State == EntryState.Broken;
    [JsonIgnore] public bool CanAct => State is EntryState.Done or EntryState.Restoring;
    [JsonIgnore] public string LeftText => State switch
    {
        EntryState.Broken => Loc.T("Quarantine.BrokenState"),
        EntryState.Done or EntryState.Restoring => (int)Math.Ceiling((ExpiresUtc - DateTime.UtcNow).TotalDays) is var d && d > 1
            ? string.Format(Loc.T("Quarantine.Left"), d) : Loc.T("Quarantine.LastDay"),
        _ => Loc.T("Quarantine.State." + State),
    };
}

/// <summary>
/// The quarantine (07 §5, §8): a same-volume rename into &lt;root&gt;\&lt;entry&gt;, journal first, undo for 7 days.
/// Every method returns null on success or the reason as a Loc key.
/// </summary>
public static class Quarantine
{
    public const string SandboxDir = @"C:\Demo\reclaimer-sandbox";

    /// <summary>Debug: Move, Restore and Purge refuse every path outside the sandbox (00-shared §9.4). Tests point it at their temp folder.</summary>
    internal static string? Sandbox =
#if DEBUG
        SandboxDir;
#else
        null;
#endif

    /// <summary>Quarantine root for a real path: tests replace it.</summary>
    internal static Func<string, string> RootFor = DefaultRootFor;
    internal static Func<IEnumerable<string>> AllowedRoots = Scan.AllowedRoots;
    internal static string HistoryPath = Path.Combine(AppInfo.DataDir, "history.jsonl");

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static readonly JsonSerializerOptions Line = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    static readonly object HistoryLock = new();
    const string EntryFile = "entry.json";

    /// <summary>System volume: %LOCALAPPDATA%\Reclaimer\Quarantine; any other volume: &lt;volume&gt;\.reclaimer-quarantine. Debug: inside the sandbox.</summary>
    static string DefaultRootFor(string realPath)
    {
        if (Sandbox is not null) return Path.Combine(Sandbox, ".reclaimer-quarantine");
        var vol = Native.VolumeOf(realPath);
        return SameVolume(AppInfo.LocalDir, vol) ? LocalRoot : Path.Combine(vol, ".reclaimer-quarantine");
    }

    static string LocalRoot => Path.Combine(AppInfo.LocalDir, "Quarantine");

    /// <summary>Every root we may have used: the local one, the saved ones, .reclaimer-quarantine on fixed drives. Debug: only the sandbox one.</summary>
    public static IEnumerable<string> KnownRoots() => Roots();

    internal static Func<IEnumerable<string>> Roots = DefaultRoots;

    static IEnumerable<string> DefaultRoots()
    {
        if (Sandbox is not null) return [Path.Combine(Sandbox, ".reclaimer-quarantine")]; // Debug never touches a real quarantine
        var roots = new List<string> { LocalRoot };
        roots.AddRange(Settings.Current.QuarantineRoots);
        foreach (var d in DriveInfo.GetDrives())
            if (d.DriveType == DriveType.Fixed) roots.Add(Path.Combine(d.Name, ".reclaimer-quarantine"));
        return roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static List<Entry> List()
    {
        var list = new List<Entry>();
        foreach (var root in KnownRoots())
        {
            if (!Directory.Exists(root)) continue; // volume unplugged: its entries wait, autoclean skips them
            foreach (var dir in Directory.EnumerateDirectories(root))
                list.Add(Read(dir) ?? new Entry { Id = Path.GetFileName(dir), Dir = dir, State = EntryState.Broken, Title = Path.GetFileName(dir) });
        }
        return list;
    }

    // ---------------------------------------------------------------- Move

    /// <summary>
    /// Re-checks everything (PathGuard, cloud, owner, size ±10 %, not changed since the scan), writes entry.json (Moving) with
    /// Flush(true), renames the parts in order and writes Done. One failed part rolls the others back: a unit is atomic.
    /// </summary>
    public static string? Move(Unit u, int days)
    {
        if (u.Status != UnitStatus.Ready) return "Move.NotReady";
        foreach (var p in u.Parts)
        {
            if (PathGuard.Check(p.Path, u.Root) is { } why) return why;
            if (PathGuard.IsCloud(p.Path)) { u.Status = UnitStatus.CloudFolder; return "Status.CloudFolder"; }
            if (p.MtimeUtc != default && LastWrite(p.Path) > p.MtimeUtc.AddSeconds(2)) return "Move.Changed";
        }
        if (OwnerCheck.Running(u.Owners) is { } owner)
        {
            (u.Status, u.Owner) = (UnitStatus.OwnerRunning, owner);
            return "Status.OwnerRunning";
        }
        var now = u.Parts.Sum(p => Fs.Size(p.Path));
        if (Math.Abs(now - u.Bytes) > u.Bytes / 10) return "Move.SizeChanged";

        var reals = u.Parts.Select(p => PathGuard.Real(p.Path)).ToList();
        var root = RootFor(reals[0]);
        if (!reals.All(InSandbox) || !InSandbox(root)) return "Move.OutsideSandbox";
        if (EnsureRoot(root) is { } noRoot) { u.Status = UnitStatus.NoQuarantine; return noRoot; }
        if (reals.Any(r => !SameVolume(r, root))) { u.Status = UnitStatus.NoQuarantine; return "Status.NoQuarantine"; }

        var created = DateTime.UtcNow;
        var e = new Entry
        {
            Id = $"{created:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..22], CreatedUtc = created, ExpiresUtc = created.AddDays(days),
            State = EntryState.Moving, Category = u.Category, Title = u.Title, LastUsed = u.LastUsed, TotalBytes = u.Bytes,
            Parts = reals.Select((r, i) => new EntryPart { Original = r, Stored = $"{i}-{Path.GetFileName(r)}", IsDir = u.Parts[i].IsDir, Bytes = u.Parts[i].Bytes }).ToList(),
        };
        e.Dir = Path.Combine(root, e.Id);
        try
        {
            Directory.CreateDirectory(e.Dir);
            Write(e); // journal first: a full disk stops us here, before anything moved
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"journal not written: {Fs.Describe(x)}");
            TryDeleteTree(e.Dir, root);
            return "Move.JournalFailed";
        }

        for (var i = 0; i < e.Parts.Count; i++)
        {
            var err = MoveRetry(e.Parts[i].Original, StoredPath(e, e.Parts[i]));
            if (err == 0) continue;
            var back = RollBack(e, i);
            if (back) TryDeleteTree(e.Dir, root);
            else { e.State = EntryState.Broken; TryWrite(e); }
            History("move", e, $"error {err}");
            return MoveError(u, err, e.Parts[i].Original);
        }
        e.State = EntryState.Done;
        TryWrite(e); // if this write is lost, Reconcile sees Moving with every part moved and puts them back: no data lost
        History("move", e, "ok");
        return null;
    }

    static string MoveError(Unit u, int err, string path)
    {
        switch (err)
        {
            case Native.ErrorNotSameDevice: u.Status = UnitStatus.NoQuarantine; return "Status.NoQuarantine";
            case Native.ErrorAccessDenied when IsProtectedFolder(path): return "Move.Cfa";
            case Native.ErrorAccessDenied or Native.ErrorSharingViolation or Native.ErrorLockViolation: u.Status = UnitStatus.FilesBusy; return "Status.FilesBusy";
            default: return "Move.Failed";
        }
    }

    /// <summary>Controlled Folder Access protects these: a rename there is denied until the app is allowed in Windows Security.</summary>
    static bool IsProtectedFolder(string path) =>
        new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.Favorites }
        .Select(Environment.GetFolderPath).Any(f => f.Length > 0 && Paths.IsUnder(f, path));

    /// <summary>A file held by an antivirus or an indexer: 3 tries, 500 ms apart. A folder rename with an open file inside just fails, nothing half-moved.</summary>
    static int MoveRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            var err = Native.Move(from, to);
            if (err is not (Native.ErrorAccessDenied or Native.ErrorSharingViolation or Native.ErrorLockViolation) || attempt == 3) return err;
            Thread.Sleep(500);
        }
    }

    /// <summary>Puts parts [0, count) back. False if one could not go back (then the entry is Broken and nothing is ever auto-deleted).</summary>
    static bool RollBack(Entry e, int count)
    {
        var ok = true;
        for (var j = count - 1; j >= 0; j--)
        {
            var p = e.Parts[j];
            if (Path.Exists(StoredPath(e, p)) && Native.Move(StoredPath(e, p), p.Original) != 0) ok = false;
        }
        return ok;
    }

    // ---------------------------------------------------------------- Restore

    /// <summary>
    /// Back to the original paths, through PathGuard again (entry.json is a plain file anyone can edit). Original taken:
    /// returns "Restore.Conflict" unless <paramref name="beside"/>, which restores as "&lt;name&gt;.restored". Parts go back in
    /// reverse order: an Ollama manifest before its blobs, so Ollama never sees blobs without a manifest and prunes them.
    /// </summary>
    public static string? Restore(Entry e, bool beside = false)
    {
        if (FoldersBroken(e)) { MarkBroken(e); return "Quarantine.Broken"; }
        if (e.State is EntryState.Broken or EntryState.Purging) return "Quarantine.Broken";

        var targets = new string?[e.Parts.Count]; // null = this part is already back (a restore that crashed half-way)
        var allowed = AllowedRoots().ToList();
        for (var i = 0; i < e.Parts.Count; i++)
        {
            var p = e.Parts[i];
            if (!SafeName(p.Stored)) { MarkBroken(e); return "Quarantine.Broken"; }
            if (!Path.Exists(StoredPath(e, p)))
            {
                if (Path.Exists(p.Original)) continue;
                MarkBroken(e);
                return "Quarantine.Broken";
            }
            if (!allowed.Any(r => PathGuard.Check(p.Original, r, mustExist: false) is null)) return "Guard.OutsideRoot";
            if (!InSandbox(p.Original)) return "Move.OutsideSandbox";
            var target = p.Original;
            if (Path.Exists(target))
            {
                if (!beside) return "Restore.Conflict";
                target += ".restored";
                if (Path.Exists(target)) return "Restore.Conflict";
            }
            targets[i] = target;
        }

        e.State = EntryState.Restoring;
        if (TryWrite(e) is { } failed) return failed;
        var done = new List<int>();
        for (var i = e.Parts.Count - 1; i >= 0; i--)
        {
            if (targets[i] is not { } t) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            var err = MoveRetry(StoredPath(e, e.Parts[i]), t);
            if (err == 0) { done.Add(i); continue; }
            foreach (var j in done) Native.Move(targets[j]!, StoredPath(e, e.Parts[j]));
            e.State = EntryState.Done;
            TryWrite(e);
            History("restore", e, $"error {err}");
            return err is Native.ErrorAccessDenied or Native.ErrorSharingViolation ? "Status.FilesBusy" : "Move.Failed";
        }
        TryDeleteTree(e.Dir, e.Root);
        History("restore", e, "ok");
        return null;
    }

    // ---------------------------------------------------------------- Purge

    /// <summary>
    /// Deletes an entry for good. Own walk: a link is deleted as a link and never followed, read-only files lose the attribute first,
    /// every path is checked to be inside the entry folder. Cancelled between files, the entry stays Purging and is finished on the next start.
    /// Returns the freed bytes measured by the free space of the volume, or the reason.
    /// </summary>
    public static (long Freed, string? Error) Purge(Entry e, CancellationToken ct = default)
    {
        if (FoldersBroken(e)) { MarkBroken(e); return (0, "Quarantine.Broken"); }
        if (!InSandbox(e.Dir)) return (0, "Move.OutsideSandbox");
        if (e.Parts.Any(p => !SafeName(p.Stored))) { MarkBroken(e); return (0, "Quarantine.Broken"); }
        var before = FreeSpace(e.Root);
        if (e.State != EntryState.Broken)
        {
            e.State = EntryState.Purging;
            if (TryWrite(e) is { } failed) return (0, failed);
        }
        foreach (var p in e.Parts) DeleteTree(StoredPath(e, p), e.Dir, ct);
        DeleteTree(e.Dir, e.Root, ct);
        var freed = Math.Max(0, FreeSpace(e.Root) - before);
        History("purge", e, "ok", freed);
        return (freed, null);
    }

    static long FreeSpace(string root)
    {
        try { return new DriveInfo(Native.VolumeOf(root)).AvailableFreeSpace; }
        catch { return 0; } // mounted-folder volumes: DriveInfo wants a drive letter; freed then shows 0
    }

    /// <summary>The ~25-line walk of 07 §6: Directory.Delete(recursive) can't remove read-only files (.git/objects, Go).</summary>
    internal static void DeleteTree(string path, string boundary, CancellationToken ct)
    {
        if (!Paths.IsUnder(boundary, path)) throw new InvalidOperationException("outside the quarantine");
        FileAttributes a;
        try { a = File.GetAttributes(path); }
        catch (Exception x) when (x is FileNotFoundException or DirectoryNotFoundException) { return; }
        if ((a & FileAttributes.ReparsePoint) != 0)
        {
            if ((a & FileAttributes.Directory) != 0) Directory.Delete(path); // removes the junction/symlink itself, not its target
            else File.Delete(path);                                          // attributes of a link are left alone: they could reach the target
            return;
        }
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        if ((a & FileAttributes.Directory) == 0) { File.Delete(path); return; }
        foreach (var child in Directory.GetFileSystemEntries(path))
        {
            ct.ThrowIfCancellationRequested();
            DeleteTree(child, boundary, ct);
        }
        Directory.Delete(path);
    }

    static void TryDeleteTree(string path, string boundary)
    {
        try { DeleteTree(path, boundary, default); }
        catch (Exception x) { Log.Warn($"cleanup failed: {Fs.Describe(x)}"); }
    }

    // ---------------------------------------------------------------- Reconcile

    /// <summary>
    /// At start, for every root: Moving with the source still in place → the entry goes; a part already moved → it goes back;
    /// a payload gone → Broken (never auto-deleted). Restoring and Purging are finished.
    /// </summary>
    public static void Reconcile()
    {
        foreach (var e in List())
        {
            try
            {
                switch (e.State)
                {
                    case EntryState.Moving:
                        if (FoldersBroken(e) || e.Parts.Any(p => !SafeName(p.Stored))) { MarkBroken(e); break; }
                        var lost = e.Parts.Any(p => !Path.Exists(StoredPath(e, p)) && !Path.Exists(p.Original));
                        if (!lost && RollBack(e, e.Parts.Count)) { TryDeleteTree(e.Dir, e.Root); History("reconcile", e, "rolled back"); }
                        else MarkBroken(e);
                        break;
                    case EntryState.Done:
                        if (e.Parts.Any(p => !Path.Exists(StoredPath(e, p)))) MarkBroken(e);
                        break;
                    case EntryState.Restoring:
                        Restore(e);
                        break;
                    case EntryState.Purging:
                        Purge(e);
                        break;
                }
            }
            catch (Exception x) { Log.Error($"reconcile: {Fs.Describe(x)}"); }
        }
    }

    // ---------------------------------------------------------------- helpers

    static string StoredPath(Entry e, EntryPart p) => Path.Combine(e.Dir, p.Stored);

    /// <summary>entry.json can be edited by hand: a stored name with a separator or ".." would point outside the entry.</summary>
    static bool SafeName(string n) => n.Length > 0 && n.IndexOfAny(['\\', '/', ':']) < 0 && n != "." && n != "..";

    /// <summary>The root or the entry folder replaced by a junction would lead a purge into someone else's folder (07 §8.10).</summary>
    static bool FoldersBroken(Entry e)
    {
        try { return !Directory.Exists(e.Dir) || Paths.IsReparsePoint(e.Root) || Paths.IsReparsePoint(e.Dir); }
        catch (IOException) { return true; }
    }

    static void MarkBroken(Entry e)
    {
        e.State = EntryState.Broken;
        if (Directory.Exists(e.Dir) && !Paths.IsReparsePoint(e.Dir)) TryWrite(e);
    }

    static string? EnsureRoot(string root)
    {
        try
        {
            var existed = Directory.Exists(root);
            Directory.CreateDirectory(root);
            if (Paths.IsReparsePoint(root)) return "Quarantine.Broken";
            if (existed || Sandbox is not null || string.Equals(root, LocalRoot, StringComparison.OrdinalIgnoreCase)) return null;
            File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(root, "README.txt"),
                "Reclaimer quarantine: folders moved here wait before they are deleted. Restore or clean them in Reclaimer.\r\n" +
                "Отстойник Reclaimer: папки ждут здесь удаления. Вернуть или очистить их можно в Reclaimer.\r\n");
            if (!Settings.Current.QuarantineRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                Settings.Current.QuarantineRoots.Add(root);
                Settings.Save();
            }
            return null;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"no quarantine on this volume: {Fs.Describe(x)}");
            return "Status.NoQuarantine";
        }
    }

    static bool InSandbox(string path) => Sandbox is null || Paths.IsUnder(Sandbox, path);

    static bool SameVolume(string a, string b)
    {
        try { return string.Equals(Native.VolumeOf(a), Native.VolumeOf(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static DateTime LastWrite(string p) => Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p) : File.GetLastWriteTimeUtc(p);

    static Entry? Read(string dir)
    {
        try
        {
            var e = AtomicFile.Read(Path.Combine(dir, EntryFile), s => JsonSerializer.Deserialize<Entry>(s, Json));
            if (e is not null) e.Dir = dir;
            return e;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { Log.Warn($"entry unreadable: {Fs.Describe(x)}"); return null; }
    }

    static void Write(Entry e) => AtomicFile.WriteAllText(Path.Combine(e.Dir, EntryFile), JsonSerializer.Serialize(e, Json));

    static string? TryWrite(Entry e)
    {
        try { Write(e); return null; }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { Log.Warn($"entry not written: {Fs.Describe(x)}"); return "Move.JournalFailed"; }
    }

    /// <summary>The newest <paramref name="max"/> lines of history.jsonl, newest first. A line that doesn't parse is skipped.</summary>
    public static List<HistoryRow> ReadHistory(int max = 500)
    {
        var rows = new List<HistoryRow>();
        try
        {
            if (!File.Exists(HistoryPath)) return rows;
            foreach (var line in File.ReadLines(HistoryPath).TakeLast(max).Reverse())
                try
                {
                    using var j = JsonDocument.Parse(line);
                    var r = j.RootElement;
                    rows.Add(new(r.GetProperty("ts").GetDateTime(), r.GetProperty("action").GetString() ?? "",
                        r.GetProperty("paths").EnumerateArray().Select(p => p.GetString() ?? "").ToArray(),
                        r.GetProperty("bytes").GetInt64(), r.GetProperty("result").GetString() ?? "", r.GetProperty("freedBytes").GetInt64()));
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"history unreadable: {Fs.Describe(e)}"); }
        return rows;
    }

    /// <summary>history.jsonl: a line per action. Local only; paths are fine here (they never go to the log, 00-shared §2.3).</summary>
    static void History(string action, Entry e, string result, long freed = 0)
    {
        if (Args.Current.Demo) return;
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTime.UtcNow, action, paths = e.Parts.Select(p => p.Original), bytes = e.TotalBytes, result, freedBytes = freed,
        }, Line);
        try
        {
            lock (HistoryLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.AppendAllText(HistoryPath, line + "\n");
            }
        }
        catch (Exception x) { Log.Warn($"history not written: {Fs.Describe(x)}"); }
    }
}
