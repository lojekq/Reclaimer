using System.Diagnostics;
using System.Text;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>Is the program that owns a folder running? By process name prefix (07 §8.5). We never close it ourselves.</summary>
public static class OwnerCheck
{
    internal static Func<string[]> ProcessNames = () => Process.GetProcesses().Select(p => { using (p) return p.ProcessName; }).ToArray();

    /// <summary>The running process that matches one of the owner prefixes, or null.</summary>
    public static string? Running(string[] owners, string[]? names = null)
    {
        if (owners.Length == 0) return null;
        names ??= ProcessNames();
        return names.FirstOrDefault(n => owners.Any(o => n.StartsWith(o, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Running WSL distributions. wsl.exe writes UTF-16; no WSL or none running = empty.</summary>
    public static HashSet<string> WslRunning()
    {
        try
        {
            var (code, output) = ConsoleTool.Run("wsl.exe", ["--list", "--running", "--quiet"], TimeSpan.FromSeconds(5), Encoding.Unicode);
            if (code == 0)
                return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException) { Log.Info($"wsl list: {e.GetType().Name}"); }
        return new(StringComparer.OrdinalIgnoreCase);
    }
}
