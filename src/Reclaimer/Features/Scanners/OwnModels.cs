using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>
/// Weights files (.gguf, .safetensors, 1 GB and more) in the folders the user scans, outside the programs the catalog knows (v1.0):
/// a model kept for llama-server, ComfyUI or a script of one's own. A file a running program holds, or a launcher script mentions,
/// is "in use" and can't be picked. Moving goes through the quarantine like everything else.
/// </summary>
public static class OwnModels
{
    static readonly HashSet<string> Exts = new([".gguf", ".safetensors"], StringComparer.OrdinalIgnoreCase);
    internal static long MinBytes = 100L << 20; // LoRAs and VAEs are 100 MB and more; the duplicate search uses the same bar

    public static bool IsWeights(FileInfo f) => Exts.Contains(f.Extension) && f.Length >= MinBytes;

    public static Unit Make(string root, FileInfo f)
    {
        var (at, source) = Fs.LastUsed(new(f.Length, f.LastWriteTimeUtc, f.LastAccessTimeUtc));
        var path = f.FullName;
        return new Unit
        {
            Id = $"own|{path}", Location = "own-models", Category = Category.OwnModels, Title = f.Name, Root = root,
            Parts = [new(path, false, f.Length, f.LastWriteTimeUtc)], Bytes = f.Length, LastUsed = at, Source = source,
            Rebuild = "Rebuild.Download", Origin = Origin.OfFile(path), Notes = [string.Format(Loc.T("Note.OwnFolder"), f.DirectoryName)],
            Holder = () => Holders.Current().Of(path),
        };
    }
}

/// <summary>Who holds a model file right now: a launcher script that mentions it, a process whose command line names it, or an open handle.</summary>
public sealed class Holders
{
    readonly List<(string Name, string CmdLine)> processes;
    readonly Dictionary<string, string> launchers; // full path → "autostart.ps1 (MLink)"
    static Holders? cached;
    static DateTime cachedAt;
    static readonly Lock Gate = new();

    Holders(List<(string, string)> processes, Dictionary<string, string> launchers) => (this.processes, this.launchers) = (processes, launchers);

    /// <summary>A snapshot at most 3 s old: "Check again" over twenty rows takes one, not twenty.</summary>
    public static Holders Current()
    {
        lock (Gate)
        {
            if (cached is null || DateTime.UtcNow - cachedAt > TimeSpan.FromSeconds(3)) { cached = Snapshot(); cachedAt = DateTime.UtcNow; }
            return cached;
        }
    }

    public static Holders Snapshot() => new(Processes(), Launchers.References());

    /// <summary>Null when the file is free, otherwise the holder for "In use: …".</summary>
    public string? Of(string path)
    {
        if (launchers.TryGetValue(Path.GetFullPath(path), out var script)) return script;
        var name = Path.GetFileName(path);
        foreach (var (proc, cmd) in processes)
            if (cmd.Contains(path, StringComparison.OrdinalIgnoreCase) || cmd.Contains(name, StringComparison.OrdinalIgnoreCase)) return proc;
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Loc.T("InUse.OpenFile"); } // ComfyUI, LM Studio or anything else has it open
        return null;
    }

    static List<(string, string)> Processes()
    {
        var list = new List<(string, string)>();
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                if (p.Id == me) continue;
                try { if (ProcessInfo.CommandLine(p.Id) is { Length: > 0 } cmd) list.Add((p.ProcessName, cmd)); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } // gone, or protected
            }
        return list;
    }
}

/// <summary>
/// Model paths that launcher scripts and configs mention: the catalog's known launchers (MLink) and Settings.LauncherDirs, depth 1.
/// cmd "set" lines and PowerShell "$name = '…'" assignments are expanded; any literal path to a weights file counts.
/// </summary>
public static partial class Launchers
{
    static readonly string[] Patterns = ["*.bat", "*.cmd", "*.ps1", "*.json", "*.yaml", "*.yml", "*.toml", "*.env", "*.txt"];

    internal static Func<IEnumerable<(string Tag, string Dir)>> Dirs = () =>
        Catalog.Launchers.SelectMany(l => l.Dirs.Select(d => (l.Tag, Environment.ExpandEnvironmentVariables(d))))
            .Concat(Settings.Current.LauncherDirs.Select(d => (Path.GetFileName(d.TrimEnd('\\')), d)));

    public static Dictionary<string, string> References()
    {
        var refs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, dir) in Dirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pattern in Patterns)
                foreach (var file in Directory.EnumerateFiles(dir, pattern, Fs.Flat))
                    try
                    {
                        if (new FileInfo(file).Length > 1 << 20) continue;
                        foreach (var p in Extract(File.ReadAllText(file), Path.GetExtension(file), dir))
                            refs.TryAdd(p, $"{Path.GetFileName(file)} ({tag})");
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return refs;
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^""'<>|*?\r\n]*?\.(?:gguf|safetensors)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Literal();

    [GeneratedRegex(@"\bset\s+""?([A-Za-z_]\w*)=([^""\r\n]*)""?", RegexOptions.IgnoreCase)]
    private static partial Regex CmdSet();

    [GeneratedRegex(@"(?m)^\s*\$(\w+)\s*=\s*(['""])(.*?)\2")]
    private static partial Regex PsSet();

    [GeneratedRegex(@"""([^""\r\n]*\$[^""\r\n]*)""")]
    private static partial Regex PsString();

    /// <summary>Full paths to weights files a script refers to. <paramref name="dir"/> stands in for %~dp0 and $PSScriptRoot.</summary>
    internal static IEnumerable<string> Extract(string text, string ext, string dir)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Literal().Matches(text)) found.Add(m.Value);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var values = new List<string>();
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in CmdSet().Matches(text)) vars[m.Groups[1].Value] = m.Groups[2].Value; // the last set wins: a default after set X=%~1
            string Expand(string v) => Regex.Replace(v, @"%~dp0|%~\d|%([A-Za-z_]\w*)%", m =>
                m.Value.Equals("%~dp0", StringComparison.OrdinalIgnoreCase) ? dir.TrimEnd('\\') + '\\'
                : m.Value.StartsWith("%~") ? ""
                : vars.GetValueOrDefault(m.Groups[1].Value) ?? Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
            for (var i = 0; i < 4; i++) foreach (var k in vars.Keys.ToList()) vars[k] = Expand(vars[k]);
            values.AddRange(vars.Values);
        }
        else if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in PsSet().Matches(text)) vars[m.Groups[1].Value] = m.Groups[3].Value;
            string Expand(string v) => Regex.Replace(v, @"\$\{(\w+)\}|\$env:(\w+)|\$PSScriptRoot|\$(\w+)", m =>
                m.Value.Equals("$PSScriptRoot", StringComparison.OrdinalIgnoreCase) ? dir.TrimEnd('\\')
                : m.Groups[2].Success ? Environment.GetEnvironmentVariable(m.Groups[2].Value) ?? ""
                : vars.GetValueOrDefault(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value) ?? m.Value, RegexOptions.IgnoreCase);
            for (var i = 0; i < 4; i++) foreach (var k in vars.Keys.ToList()) vars[k] = Expand(vars[k]);
            values.AddRange(vars.Values);
            foreach (Match m in PsString().Matches(text)) values.Add(Expand(m.Groups[1].Value));
        }
        foreach (var v in values)
        {
            var t = v.Trim().Trim('"');
            if ((t.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) && Path.IsPathFullyQualified(t))
                found.Add(t);
        }
        var full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in found)
            try { full.Add(Path.GetFullPath(p)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        return full;
    }
}
