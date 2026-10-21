using System.IO;
using Microsoft.Win32;

namespace Reclaimer.Features;

public enum Kind { Whole, Hf, Ollama, ModelFiles, Vhdx }

/// <summary>A known place. EnvVars in order of precedence, each with a sub-folder: ("HF_HOME", "hub") means %HF_HOME%\hub.</summary>
public sealed record Location(string Id, Category Category, Kind Kind, string[] Defaults, (string Var, string Sub)[] EnvVars, string[] Owners, string? Rebuild);

/// <summary>The whitelist (07 §5). Editing the catalog = editing this code, no JSON.</summary>
public static class Catalog
{
    static readonly string[] NuGetOwners = ["dotnet", "MSBuild", "devenv"];

    public static readonly Location[] All =
    [
        new("hf-hub", Category.Models, Kind.Hf, [@"%USERPROFILE%\.cache\huggingface\hub"], [("HF_HUB_CACHE", ""), ("HF_HOME", "hub")], [], "Rebuild.Download"),
        new("ollama", Category.Models, Kind.Ollama, [@"%USERPROFILE%\.ollama\models"], [("OLLAMA_MODELS", "")], ["ollama"], "Rebuild.OllamaPull"),
        new("lmstudio", Category.Models, Kind.ModelFiles, [@"%USERPROFILE%\.lmstudio\models", @"%USERPROFILE%\.cache\lm-studio\models"], [], ["LM Studio", "lms"], "Rebuild.Download"),
        new("hf-xet", Category.Caches, Kind.Whole, [@"%USERPROFILE%\.cache\huggingface\xet"], [("HF_XET_CACHE", ""), ("HF_HOME", "xet")], [], "Rebuild.Cache"),
        new("pip", Category.Caches, Kind.Whole, [@"%LOCALAPPDATA%\pip\Cache"], [("PIP_CACHE_DIR", "")], [], "Rebuild.Cache"),
        new("uv", Category.Caches, Kind.Whole, [@"%LOCALAPPDATA%\uv\cache"], [("UV_CACHE_DIR", "")], ["uv"], "Rebuild.Cache"),
        new("npm", Category.Caches, Kind.Whole, [@"%LOCALAPPDATA%\npm-cache"], [("npm_config_cache", "")], [], "Rebuild.Cache"),
        new("pnpm", Category.Caches, Kind.Whole, [@"%LOCALAPPDATA%\pnpm\store"], [("PNPM_HOME", "store")], [], "Rebuild.Cache"),
        new("nuget", Category.Caches, Kind.Whole, [@"%USERPROFILE%\.nuget\packages"], [("NUGET_PACKAGES", "")], NuGetOwners, "Rebuild.Restore"),
        new("nuget-http", Category.Caches, Kind.Whole, [@"%LOCALAPPDATA%\NuGet\v3-cache"], [("NUGET_HTTP_CACHE_PATH", "")], NuGetOwners, "Rebuild.Restore"),
        new("cargo-cache", Category.Caches, Kind.Whole, [@"%USERPROFILE%\.cargo\registry\cache"], [("CARGO_HOME", @"registry\cache")], ["cargo", "rustc"], "Rebuild.Cache"),
        new("cargo-src", Category.Caches, Kind.Whole, [@"%USERPROFILE%\.cargo\registry\src"], [("CARGO_HOME", @"registry\src")], ["cargo", "rustc"], "Rebuild.Cache"),
    ];

    /// <summary>Existing folders of a location: the first set variable wins, otherwise the defaults.</summary>
    public static List<string> PathsOf(Location l)
    {
        foreach (var (v, sub) in l.EnvVars)
            if (Env(v) is { Length: > 0 } value)
                return Existing([Path.Combine(value, sub)]);
        return Existing(l.Defaults.Select(Environment.ExpandEnvironmentVariables));
    }

    /// <summary>
    /// Process first, then HKCU\Environment, then the machine: the user may have changed a variable after logging in,
    /// and the process variable wins, which is what the development sandbox relies on (07 §9).
    /// </summary>
    public static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } p ? p
        : Registry.GetValue(@"HKEY_CURRENT_USER\Environment", name, null) as string is { Length: > 0 } u ? u
        : Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", name, null) as string;

    static List<string> Existing(IEnumerable<string> paths) =>
        paths.Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p))).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
