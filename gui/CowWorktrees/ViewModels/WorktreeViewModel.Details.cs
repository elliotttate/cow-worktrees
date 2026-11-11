using System.Collections.ObjectModel;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

/// <summary>One commit in the details pane: its short name, what it says and when it was made.</summary>
public sealed class CommitItem
{
    public string Sha { get; init; } = "";
    public string Subject { get; init; } = "";
    public string When { get; init; } = "";

    /// <summary>A line of GitService.UnpushedLogAsync: "abc1234  the subject  (5 hours ago)".</summary>
    public static CommitItem Parse(string line)
    {
        int a = line.IndexOf("  ", StringComparison.Ordinal);
        if (a <= 0) return new CommitItem { Subject = line };
        string rest = line[(a + 2)..];
        int b = rest.EndsWith(')') ? rest.LastIndexOf("  (", StringComparison.Ordinal) : -1;
        return b < 0 ? new CommitItem { Sha = line[..a], Subject = rest }
            : new CommitItem { Sha = line[..a], Subject = rest[..b], When = rest[(b + 3)..^1] };
    }
}

/// <summary>One changed or new path in the details pane, from a line of `git status --porcelain`.</summary>
public sealed class StatusItem
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Badge { get; init; } = "M";
    public string What { get; init; } = "modified";
    public ChangeKind Kind { get; init; }
    public Brush KindBrush => DiffPalette.Text(Kind);
    public Brush KindFill => DiffPalette.Fill(Kind);
    public Visibility FolderVisibility => Theme.Vis(Folder.Length > 0);
    public string Tip => $"{Path}\n{What}. Click to see its changes.";

    /// <summary>"XY path", "R  old -> new", "?? path", and the app's own " ~ path" for a file that only looks changed.</summary>
    public static StatusItem Parse(string line)
    {
        string code = line.Length >= 2 ? line[..2] : "  ";
        string path = line.Length > 3 ? line[3..] : line;
        int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow >= 0) path = path[(arrow + 4)..];
        path = path.Trim('"');
        bool folder = path.EndsWith('/');
        string trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        var (kind, badge, what) = code switch
        {
            "??" => (ChangeKind.Untracked, "N", folder ? "a new folder" : "a new file"),
            " ~" => (ChangeKind.Phantom, "≈", "only looks changed to Git"),
            _ when code.Contains('U') || code is "AA" or "DD" => (ChangeKind.Conflict, "!", "in conflict"),
            _ when code.Contains('R') => (ChangeKind.Renamed, "R", "renamed"),
            _ when code.Contains('D') => (ChangeKind.Deleted, "D", "deleted"),
            _ when code.Contains('A') => (ChangeKind.Added, "A", "added"),
            _ => (ChangeKind.Modified, "M", "modified"),
        };
        return new StatusItem
        {
            Path = trimmed,
            Name = (slash < 0 ? trimmed : trimmed[(slash + 1)..]) + (folder ? "/" : ""),
            Folder = slash < 0 ? "" : trimmed[..(slash + 1)],
            Kind = kind,
            Badge = badge,
            What = what + (code[0] is not (' ' or '?') && kind != ChangeKind.Conflict ? ", staged" : ""),
        };
    }
}

/// <summary>What the details pane shows of a worktree beyond the list's columns: the numbers, the commits and changes as rows, and the summary.</summary>
public sealed partial class WorktreeViewModel
{
    // ---- the four numbers -------------------------------------------------------------------------------------------
    public string ChangedCount => HasProbe ? Changed.ToString() : "–";
    public Brush ChangedCountBrush => Changed > 0 ? Theme.Caution : Theme.Secondary;
    public string UntrackedCount => HasProbe ? Untracked.ToString() : "–";
    public Brush UntrackedCountBrush => Untracked > 0 ? DiffPalette.Text(ChangeKind.Untracked) : Theme.Secondary;
    public string UnpushedCount => Unpushed is { } n ? n.ToString() : "–";
    public string ActivityShort => LastActivity == null ? "–" : Fmt.Relative(LastActivity) is "just now" ? "now" : Fmt.Relative(LastActivity).Replace(" ago", "");
    public string HeadShort => ShortSha ?? (HeadFromList?[..Math.Min(9, HeadFromList.Length)] ?? "");

    // ---- commits and changes as rows --------------------------------------------------------------------------------
    public const int StatusRowCap = 60;
    private ObservableCollection<CommitItem>? _commitItems;
    private ObservableCollection<StatusItem>? _statusItems;

    /// <summary>The commits on no remote, as rows. Follows <see cref="UnpushedLog"/>.</summary>
    public ObservableCollection<CommitItem> CommitItems
    {
        get
        {
            if (_commitItems != null) return _commitItems;
            _commitItems = new ObservableCollection<CommitItem>();
            UnpushedLog.CollectionChanged += (_, _) => Later(ref _commitSyncQueued, () => { _commitSyncQueued = false; Sync(_commitItems, UnpushedLog.Select(CommitItem.Parse)); });
            Sync(_commitItems, UnpushedLog.Select(CommitItem.Parse));
            return _commitItems;
        }
    }

    /// <summary>The first changed and new paths, as rows. Follows <see cref="StatusLines"/>.</summary>
    public ObservableCollection<StatusItem> StatusItems
    {
        get
        {
            if (_statusItems != null) return _statusItems;
            _statusItems = new ObservableCollection<StatusItem>();
            StatusLines.CollectionChanged += (_, _) => Later(ref _statusSyncQueued, () =>
            {
                _statusSyncQueued = false;
                Sync(_statusItems, StatusLines.Take(StatusRowCap).Select(StatusItem.Parse));
                Raise(nameof(StatusMoreText), nameof(StatusMoreVisibility));
            });
            Sync(_statusItems, StatusLines.Take(StatusRowCap).Select(StatusItem.Parse));
            return _statusItems;
        }
    }

    private bool _commitSyncQueued, _statusSyncQueued;

    /// <summary>The lists are filled one line at a time: the rows are rebuilt once, after the last line, not at every one.</summary>
    private static void Later(ref bool queued, Action act)
    {
        if (queued) return;
        if (System.Threading.SynchronizationContext.Current is { } ui)
        {
            queued = true;
            ui.Post(_ => act(), null);
        }
        else act();
    }

    private static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    public string StatusMoreText => StatusLines.Count > StatusRowCap ? $"and {StatusLines.Count - StatusRowCap} more: see them all in View changes" : "";
    public Visibility StatusMoreVisibility => Theme.Vis(StatusLines.Count > StatusRowCap);
    public Visibility NoUnpushedVisibility => Theme.Vis(!HasUnpushed);

    // ---- the summary ------------------------------------------------------------------------------------------------
    private bool _summaryBusy;
    private string _summaryBusyText = "";
    private string? _summaryError;

    public WorktreeSummary? Summary { get; private set; }
    /// <summary>Set once the stored summary was looked up, so it is not looked up at every selection.</summary>
    public bool SummaryLookedUp { get; set; }

    private static readonly string[] SummaryProperties =
    {
        nameof(Summary), nameof(SummaryHeadline), nameof(SummaryPoints), nameof(SummaryOpen), nameof(SummaryOpenVisibility), nameof(SummaryMeta), nameof(SummaryStale),
        nameof(SummaryReadyVisibility), nameof(SummaryEmptyVisibility), nameof(SummaryBusyVisibility), nameof(SummaryErrorVisibility), nameof(SummaryBusyText), nameof(SummaryError),
        nameof(SummaryButtonLabel), nameof(SummaryIdle),
    };

    public void SetSummary(WorktreeSummary? s)
    {
        Summary = s;
        _summaryError = null;
        Raise(SummaryProperties);
    }

    public void SetSummaryBusy(bool busy, string text = "")
    {
        _summaryBusy = busy;
        _summaryBusyText = text;
        if (busy) _summaryError = null;
        Raise(SummaryProperties);
    }

    public void SetSummaryError(string? error)
    {
        _summaryError = error;
        Raise(SummaryProperties);
    }

    /// <summary>What the summary was written about: the commit, and how many files are changed and new.</summary>
    public string SummarySignature => $"{FullSha ?? HeadFromList}|{Changed}|{Untracked}";
    public bool SummaryStale => Summary != null && HasProbe && Summary.Signature != SummarySignature;
    public bool SummaryBusy => _summaryBusy;
    public bool SummaryIdle => !_summaryBusy;
    public string SummaryBusyText => _summaryBusyText;
    public string SummaryError => _summaryError ?? "";
    public string SummaryHeadline => Summary?.Headline ?? "";
    public IReadOnlyList<string> SummaryPoints => Summary?.Points ?? Array.Empty<string>();
    public string SummaryOpen => Summary?.Open ?? "";
    public Visibility SummaryOpenVisibility => Theme.Vis(!_summaryBusy && Summary is { Open.Length: > 0 });
    public string SummaryMeta => Summary == null ? "" : $"Written by {Summary.Tool}, {Fmt.Relative(Summary.At)}" + (SummaryStale ? " · the worktree has changed since" : "");
    public Brush SummaryMetaBrush => SummaryStale ? Theme.Caution : Theme.Secondary;
    public string SummaryButtonLabel => Summary == null ? "Summarize" : "Update";
    public Visibility SummaryReadyVisibility => Theme.Vis(Summary != null && !_summaryBusy);
    public Visibility SummaryEmptyVisibility => Theme.Vis(Summary == null && !_summaryBusy && _summaryError == null);
    public Visibility SummaryBusyVisibility => Theme.Vis(_summaryBusy);
    public Visibility SummaryErrorVisibility => Theme.Vis(_summaryError != null && !_summaryBusy);
}
