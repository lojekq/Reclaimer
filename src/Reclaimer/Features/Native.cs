using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Reclaimer.Features;

public static class Native
{
    public const int ErrorAccessDenied = 5, ErrorNotSameDevice = 17, ErrorSharingViolation = 32, ErrorLockViolation = 33;

    /// <summary>
    /// Rename, never copy: no MOVEFILE_COPY_ALLOWED, so another volume gives ERROR_NOT_SAME_DEVICE instead of a silent copy
    /// (File.Move copies across volumes). No MOVEFILE_REPLACE_EXISTING either. Returns 0 or the Win32 error.
    /// </summary>
    public static int Move(string from, string to) =>
        PInvoke.MoveFileEx(from, to, (MOVE_FILE_FLAGS)0) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>"C:\", or the folder a volume is mounted in. Works for paths that don't exist yet.</summary>
    public static string VolumeOf(string path)
    {
        Span<char> buf = stackalloc char[1024];
        if (!PInvoke.GetVolumePathName(path, buf)) throw new System.ComponentModel.Win32Exception();
        return buf[..buf.IndexOf('\0')].ToString();
    }
}
