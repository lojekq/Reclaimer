using System.ComponentModel;
using System.Runtime.CompilerServices;
using Reclaimer.Shell;

namespace Reclaimer.Features;

public enum Category { Models, OwnModels, Caches, Projects, Disks }

/// <summary>How "last used" is known: last commit, newest mtime, newest atime (only when NTFS writes it), Ollama's log, or not at all.</summary>
public enum UsedSource { Unknown, Commit, Modified, Accessed, Ran }

public enum UnitStatus { Ready, OwnerRunning, InUse, FilesBusy, NotIgnored, CloudFolder, NoQuarantine, ReportOnly }

/// <summary>One path of a unit. Mtime is the path's own LastWriteTimeUtc at scan time: a change before the move = skip.</summary>
public sealed record Part(string Path, bool IsDir, long Bytes, DateTime MtimeUtc = default);

/// <summary>
/// What moves as one: an Ollama model is its manifest plus the blobs no other manifest uses, a project's bin+obj is one unit.
/// Root is the whitelisted folder the unit was found in; PathGuard checks every part against it, at scan and right before the move.
/// </summary>
public sealed class Unit : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Location { get; init; }      // Catalog id, "project", "models-custom", "cache-custom"
    public required Category Category { get; init; }
    public required string Title { get; init; }
    public required string Root { get; init; }
    public required IReadOnlyList<Part> Parts { get; init; }
    public long Bytes { get; init; }
    public DateTime? LastUsed { get; init; }            // UTC
    public UsedSource Source { get; init; }
    public string[] Owners { get; init; } = [];          // process name prefixes that block the move
    public Func<string?>? Holder { get; init; }         // v1.0 own models: who holds the file now (process, launcher script), null = free
    public string? Rebuild { get; init; }               // Loc key: how it comes back
    public string? DedupeOf { get; init; }              // v1.0 duplicate: after the move a hardlink to this kept file takes the part's place
    public DateTime KeepMtimeUtc { get; init; }         // the kept file must not change between the scan and the move
    public string? Marker { get; init; }                // project marker found, e.g. package.json
    public List<string> Notes { get; init; } = [];      // already localized lines for the details panel
    public bool Approximate { get; init; }              // hardlinks, block cloning: freed space may be smaller

    UnitStatus status;
    public UnitStatus Status
    {
        get => status;
        set { status = value; Changed(); Changed(nameof(CanSelect)); Changed(nameof(StatusText)); if (!CanSelect) Selected = false; }
    }

    string? owner;
    public string? Owner { get => owner; set { owner = value; Changed(); Changed(nameof(StatusText)); } } // the running owner, for "Close Ollama"

    bool selected;
    public bool Selected { get => selected; set { if (value && !CanSelect || value == selected) return; selected = value; Changed(); } }

    public bool CanSelect => Status == UnitStatus.Ready;

    readonly string? path;
    public string Path { get => path ?? Parts[^1].Path; init => path = value; } // what people recognise; by default the last part

    public int? AgeDays => LastUsed is { } t ? (int)Math.Max(0, (DateTime.UtcNow - t).TotalDays) : null;

    // For the table
    public string ShortPath => Dialog.Short(Path);
    public string SizeText => (Approximate ? "≈ " : "") + Format.Size(Bytes);
    public string UsedText => Format.Ago(LastUsed);
    public string SourceText => Loc.T("Source." + Source);
    public string StatusText => Model.Reason("Status." + Status, this);
    /// <summary>Where it can be downloaded again: a page or file URL, or "ollama pull name". Null = nobody knows (Origin).</summary>
    public string? Origin { get; init; }
    public bool OriginUnknown => Rebuild == "Rebuild.Download" && Origin is null;
    public string RebuildText => Rebuild is null ? "" : Loc.T(Rebuild);
    /// <summary>The caption under "Download again": the site, or "Source unknown".</summary>
    public string RebuildSub => Rebuild != "Rebuild.Download" ? "" : Origin is null ? Loc.T("Rebuild.Unknown") : Features.Origin.Site(Origin);
    public string RebuildTip => OriginUnknown ? Loc.T("Rebuild.UnknownTip") : Origin ?? RebuildText;
    public string CategoryText => Loc.T("Category." + Category);

    public override string ToString() => Title; // screen readers and UI Automation name the row by it

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
}
