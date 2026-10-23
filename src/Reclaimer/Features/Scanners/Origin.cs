using System.IO;
using System.Text.Json;
using Reclaimer.Shell;

namespace Reclaimer.Features;

/// <summary>
/// Where a model came from, for "Download again: &lt;site&gt;" (v1.1). By reliability: the Hugging Face repo from the cache folder name,
/// the Ollama model name, LM Studio's publisher\repo folders, the description files model managers leave next to a file
/// (.civitai.info, .metadata.json, the .json llama.cpp writes), then the browser's Zone.Identifier stream (HostUrl, ReferrerUrl).
/// A file with none of these is "source unknown": a LoRA trained at home or a download that is gone from the site.
/// </summary>
public static class Origin
{
    /// <summary>models--org--repo → https://huggingface.co/org/repo; datasets-- and spaces-- likewise. Null for anything else.</summary>
    public static string? Hf(string repoDir)
    {
        var name = Path.GetFileName(repoDir.TrimEnd('\\'));
        var dash = name.IndexOf("--", StringComparison.Ordinal);
        if (dash < 0) return null;
        var kind = name[..dash];
        var rest = name[(dash + 2)..].Split("--", 2);
        if (rest.Length != 2 || rest[0].Length == 0 || rest[1].Length == 0) return null;
        var prefix = kind switch { "models" => "", "datasets" => "datasets/", "spaces" => "spaces/", _ => null };
        return prefix is null ? null : $"https://huggingface.co/{prefix}{rest[0]}/{rest[1]}";
    }

    /// <summary>LM Studio keeps &lt;root&gt;\&lt;publisher&gt;\&lt;repo&gt;\file.gguf, mirroring Hugging Face: that repo is the source.</summary>
    public static string? LmStudio(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file).Split('\\');
        return rel.Length >= 3 && !rel[0].StartsWith('.') ? $"https://huggingface.co/{rel[0]}/{rel[1]}" : null;
    }

    /// <summary>Description files next to the file, then the download stream. Null when nothing says where it came from.</summary>
    public static string? OfFile(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(file));
        foreach (var side in new[] { stem + ".civitai.info", file + ".civitai.info", stem + ".metadata.json", file + ".metadata.json", stem + ".cm-info.json", file + ".json", stem + ".json" })
            if (Sidecar(side) is { } url) return url;
        return Zone(file);
    }

    /// <summary>"civitai.com", "huggingface.co", "ollama pull": the short form for the table. A model site's CDN host is shown as the site.</summary>
    public static string Site(string origin)
    {
        if (origin.StartsWith("ollama pull ", StringComparison.Ordinal)) return "ollama pull";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return origin;
        var host = u.Host.StartsWith("www.") ? u.Host[4..] : u.Host;
        foreach (var site in new[] { "huggingface.co", "hf.co", "civitai.com" })
            if (host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase)) return site == "hf.co" ? "huggingface.co" : site;
        return host;
    }

    /// <summary>A JSON file of a model manager: Civitai ids make the model page; otherwise a download or source URL under a known key.</summary>
    internal static string? Sidecar(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 2 << 20) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (Civitai(doc.RootElement) is { } page) return page;
            return Url(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The first object that carries modelId (civitai.info at the top, metadata.json under "civitai"): the model page, with the version when there is an id.</summary>
    static string? Civitai(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("modelId", out var m) && Scalar(m) is { Length: > 0 } modelId)
                return e.TryGetProperty("id", out var v) && Scalar(v) is { Length: > 0 } version
                    ? $"https://civitai.com/models/{modelId}?modelVersionId={version}" : $"https://civitai.com/models/{modelId}";
            foreach (var p in e.EnumerateObject())
                if (Civitai(p.Value) is { } found) return found;
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray())
                if (Civitai(i) is { } found) return found;
        return null;
    }

    static readonly string[] UrlKeys = ["downloadUrl", "download_url", "url", "sourceUrl", "source_url", "source", "modelUrl", "model_url", "homepage", "repo", "repository"];

    static readonly string[] SkipKeys = ["images", "image", "previews", "preview", "thumbnails", "thumbnail", "samples"];

    /// <summary>An http(s) value under one of the usual keys, query string dropped: in the object itself or in nested objects, never in lists or under preview images.</summary>
    static string? Url(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in UrlKeys)
            if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Clean(v.GetString()) is { } url) return url;
        foreach (var p in e.EnumerateObject())
            if (!SkipKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && Url(p.Value) is { } found) return found;
        return null;
    }

    static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.String => e.GetString() is { } s && s.All(char.IsAsciiDigit) ? s : null,
        _ => null,
    };

    /// <summary>
    /// The Zone.Identifier stream a browser writes on a download: HostUrl is the file's address, ReferrerUrl the page.
    /// The query string goes: a Civitai HostUrl carries a one-time Authorization token, not a thing to show or copy.
    /// A HostUrl on a CDN or object store (a hash, not a name) gives way to the page it was downloaded from, when there is one.
    /// </summary>
    internal static string? Zone(string file)
    {
        string text;
        try { text = File.ReadAllText(file + ":Zone.Identifier"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
        string? host = null, referrer = null;
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase)) host = line[8..];
            else if (line.StartsWith("ReferrerUrl=", StringComparison.OrdinalIgnoreCase)) referrer = line[12..];
        }
        var h = Clean(host);
        var r = Clean(referrer);
        if (h is null) return r;
        if (r is not null && IsCdn(h) && new Uri(r).AbsolutePath.Length > 1) return r; // a page beats a hashed CDN object
        return h;
    }

    static bool IsCdn(string url)
    {
        var u = new Uri(url);
        return u.Host.Contains("cdn.", StringComparison.OrdinalIgnoreCase) || u.Host.Contains("cloudflarestorage", StringComparison.OrdinalIgnoreCase)
            || u.Host.Contains("delivery", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.Contains("xet-bridge", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An absolute http(s) URL without its query and fragment, or null.</summary>
    internal static string? Clean(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        var cut = url.IndexOfAny(['?', '#']);
        if (cut >= 0) url = url[..cut];
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url : null;
    }
}
