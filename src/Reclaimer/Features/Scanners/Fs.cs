using System.IO;
using System.IO.Enumeration;
using Microsoft.Win32;

namespace Reclaimer.Features;

public readonly record struct Stats(long Bytes, DateTime Modified, DateTime Accessed);

public static class Fs
{
    /// <summary>Into folders, never through links: a junction from node_modules into another project is not counted or followed.</summary>
    public static readonly EnumerationOptions Deep = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
    };

    public static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>
    /// NtfsDisableLastAccessUpdate: lowest bit 0 → NTFS writes atime (0/2 and 0x80000000|… on Windows 11), 1/3 → it doesn't.
    /// Without the value we don't trust atime. Written with up to 1 h of delay.
    /// </summary>
    public static readonly bool AtimeTrusted =
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", null) is int v && (v & 1) == 0;

    /// <summary>Size of a file or a folder and the newest mtime/atime of the files in it. Reads metadata only, opens no file.</summary>
    public static Stats Measure(string path)
    {
        if (File.Exists(path))
        {
            var f = new FileInfo(path);
            return new(f.Length, f.LastWriteTimeUtc, f.LastAccessTimeUtc);
        }
        if (!Directory.Exists(path)) return default;
        long bytes = 0;
        DateTime m = default, a = default;
        var files = new FileSystemEnumerable<(long, DateTime, DateTime)>(path,
            (ref FileSystemEntry e) => (e.Length, e.LastWriteTimeUtc.UtcDateTime, e.LastAccessTimeUtc.UtcDateTime), Deep)
        { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory };
        foreach (var (len, mt, at) in files)
        {
            bytes += len;
            if (mt > m) m = mt;
            if (at > a) a = at;
        }
        if (m == default) m = Directory.GetLastWriteTimeUtc(path);
        return new(bytes, m, a);
    }

    public static long Size(string path) => Measure(path).Bytes;

    /// <summary>The newer of mtime and atime, atime only when NTFS writes it.</summary>
    public static (DateTime? At, UsedSource Source) LastUsed(Stats s) =>
        s.Modified == default ? (null, UsedSource.Unknown)
        : AtimeTrusted && s.Accessed > s.Modified.AddMinutes(1) ? (s.Accessed, UsedSource.Accessed)
        : (s.Modified, UsedSource.Modified);

    /// <summary>
    /// For the log: type, HRESULT and stack, never the message. IO messages carry paths, and logs get attached to public issues (00-shared §2.3).
    /// </summary>
    public static string Describe(Exception e) => $"{e.GetType().Name} 0x{e.HResult:X8}{Environment.NewLine}{e.StackTrace}";

    /// <summary>"node_modules" next to "package.json" etc. Missing or unreadable = false.</summary>
    public static bool Any(string dir, string pattern)
    {
        try { return Directory.EnumerateFileSystemEntries(dir, pattern, Flat).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
