using System.IO;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Reclaimer.Features;

public static partial class Native
{
    public const int ErrorAccessDenied = 5, ErrorNotSameDevice = 17, ErrorSharingViolation = 32, ErrorLockViolation = 33;

    /// <summary>
    /// Rename, never copy: no MOVEFILE_COPY_ALLOWED, so another volume gives ERROR_NOT_SAME_DEVICE instead of a silent copy
    /// (File.Move copies across volumes). No MOVEFILE_REPLACE_EXISTING either. Returns 0 or the Win32 error.
    /// </summary>
    public static int Move(string from, string to) =>
        PInvoke.MoveFileEx(from, to, (MOVE_FILE_FLAGS)0) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>A second name for an existing file on the same NTFS volume (v1.0 duplicates). Returns 0 or the Win32 error.</summary>
    public static int HardLink(string link, string existing) => CreateHardLinkW(link, existing, 0) ? 0 : Marshal.GetLastPInvokeError();

    // CsWin32 only offers the raw pointer overload here; one plain LibraryImport is simpler (00-shared §2.1)
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string link, string existing, nint security);

    /// <summary>Same file on disk (volume serial + file index), i.e. hardlinks of each other or the same path. False if either can't be opened.</summary>
    public static bool SameFile(string a, string b)
    {
        static (uint, uint, uint)? Id(string p)
        {
            try
            {
                using var f = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return PInvoke.GetFileInformationByHandle(f, out var i) ? (i.dwVolumeSerialNumber, i.nFileIndexHigh, i.nFileIndexLow) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return Id(a) is { } x && Id(b) is { } y && x == y;
    }

    /// <summary>"C:\", or the folder a volume is mounted in. Works for paths that don't exist yet.</summary>
    public static string VolumeOf(string path)
    {
        Span<char> buf = stackalloc char[1024];
        if (!PInvoke.GetVolumePathName(path, buf)) throw new System.ComponentModel.Win32Exception();
        return buf[..buf.IndexOf('\0')].ToString();
    }
}
