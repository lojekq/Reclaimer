using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>A line of "docker system df": Images, Containers, Local Volumes, Build Cache.</summary>
public sealed record DockerRow(string Type, string Size, string Reclaimable)
{
    public string ReclaimableText => string.Format(Loc.T("Docker.Reclaimable"), Reclaimable);
}

/// <summary>
/// Docker (v1.0): "docker system df" in the report and two prunes that are clearly labelled "no quarantine": the build cache and
/// dangling images. Volumes are never touched, and there is no "system prune -a" (07 §3, §4). Not checked against a live Docker.
/// </summary>
public static class Docker
{
    /// <summary>Null when there is no docker CLI or the engine isn't running.</summary>
    public static List<DockerRow>? Df()
    {
        try
        {
            var (code, output) = ConsoleTool.Run("docker", ["system", "df", "--format", "{{json .}}"], TimeSpan.FromSeconds(20), Encoding.UTF8);
            return code == 0 ? Parse(output) : null;
        }
        catch (Exception e) when (e is Win32Exception or TimeoutException) { return null; }
    }

    internal static List<DockerRow> Parse(string output)
    {
        var rows = new List<DockerRow>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            try
            {
                using var j = JsonDocument.Parse(line);
                var r = j.RootElement;
                rows.Add(new(r.GetProperty("Type").GetString() ?? "", r.GetProperty("Size").GetString() ?? "", r.GetProperty("Reclaimable").GetString() ?? ""));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        return rows;
    }

    /// <summary>"docker builder prune -f" or "docker image prune -f" (dangling only). Returns docker's "Total reclaimed space" line, or null.</summary>
    public static string? Prune(bool buildCache)
    {
        string[] args = buildCache ? ["builder", "prune", "-f"] : ["image", "prune", "-f"];
        try
        {
            var (code, output) = ConsoleTool.Run("docker", args, TimeSpan.FromMinutes(10), Encoding.UTF8);
            var total = Reclaimed(output);
            Quarantine.History("docker " + string.Join(' ', args), [], 0, code == 0 ? "ok" : $"error {code}", 0, total);
            return code == 0 ? total ?? "" : null;
        }
        catch (Exception e) when (e is Win32Exception or TimeoutException)
        {
            Log.Warn($"docker prune: {Fs.Describe(e)}");
            return null;
        }
    }

    internal static string? Reclaimed(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith("Total reclaimed space", StringComparison.OrdinalIgnoreCase)) is { } l
            ? l[(l.IndexOf(':') + 1)..].Trim() : null;
}
