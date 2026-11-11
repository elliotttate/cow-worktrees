using System.Collections.ObjectModel;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

public enum WorktreeKind { Main, Cow, Base, Codex, Claude, Checkout }

// Codex and Claude are recognised by where those tools put their own (non copy-on-write) worktrees:
// Codex under $CODEX_HOME\worktrees or Documents\Codex, Claude Code under <repo>\.claude\worktrees.

/// <summary>One worktree row. Updated in place on the UI thread so the list keeps its selection and scroll.</summary>
public sealed partial class WorktreeViewModel : ObservableObject
{
    private static readonly string[] AllProperties =
        typeof(WorktreeViewModel).GetProperties().Select(p => p.Name).Where(n => n != nameof(StatusLines) && n != nameof(UnpushedLog)).ToArray();

    public WorktreeViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
    }

    public string Path { get; }
    public string Name { get; private set; }
    public WorktreeKind Kind { get; private set; }
    public string? Branch { get; private set; }
    public string? HeadFromList { get; private set; }
    public bool IsLocked { get; private set; }
    public string? LockReason { get; private set; }
    public bool IsMissing { get; private set; }
    public string? MissingReason { get; private set; }

    // ---- from the probe --------------------------------------------------------------------------------------
    public bool HasProbe { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }
    public string? ShortSha { get; private set; }
    public string? FullSha { get; private set; }
    public string? Subject { get; private set; }
    public string? Author { get; private set; }
    public DateTimeOffset? CommitTime { get; private set; }
    public string? Upstream { get; private set; }
    public bool UpstreamGone { get; private set; }
    public int? Ahead { get; private set; }
    public int? Behind { get; private set; }
    public int Changed { get; private set; }
    public int Untracked { get; private set; }
    /// <summary>Files that only look changed to Git (identical to the commit); not part of <see cref="Changed"/>.</summary>
    public int Phantom { get; private set; }
    public string PhantomNote => Phantom == 0 ? "" : $"{(Phantom == 1 ? "1 file only looks" : $"{Phantom} files only look")} changed to Git: identical to the commit, so not counted";
    public Visibility PhantomVisibility => Theme.Vis(Phantom > 0);
    public int? Unpushed { get; private set; }
    public DateTime? LastActivity { get; private set; }
    public TimeSpan StatusTime { get; private set; }
    public DateTime? CheckedAt { get; private set; }

    /// <summary>`git status` lines (capped for display).</summary>
    public ObservableCollection<string> StatusLines { get; } = new();

    /// <summary>Commits on no remote, loaded when the row is selected.</summary>
    public ObservableCollection<string> UnpushedLog { get; } = new();

    // ---- from cow_worktree.ps1 -List -Json -------------------------------------------------------------------
    public CowListRow? Cow { get; private set; }
    public List<string> PruneKeepReasons { get; private set; } = new();
    public bool WouldPrune => IsCow && Cow != null && PruneKeepReasons.Count == 0;

    // ---- derived ---------------------------------------------------------------------------------------------
    public bool IsCow => Kind == WorktreeKind.Cow;
    public bool IsBase => Kind == WorktreeKind.Base;
    public bool IsDetached => Branch == null;
    public bool IsDirty => Changed + Untracked > 0;
    // The base is detached on purpose and sits on the commit of the branch it follows: nothing of it can be lost,
    // also in a project that has no remote (where every commit counts as "on no remote").
    public bool HasUnpushed => !IsBase && (Unpushed ?? 0) > 0;
    public bool AtRisk => IsDetached && HasUnpushed;   // commits reachable from no branch and no remote
    public bool CanRemove => IsCow && !IsMissing;
    /// <summary>A full worktree outside the pool (not the project's main folder): it can be moved into a copy-on-write clone.</summary>
    public bool CanMigrate => !IsMissing && Kind is WorktreeKind.Checkout or WorktreeKind.Codex or WorktreeKind.Claude;
    public Visibility MigrateVisibility => Theme.Vis(CanMigrate);
    /// <summary>The pool's branchPrefix (worktree- by default): branches its agent hooks create, deleted on remove when unused.</summary>
    public string BranchPrefix { get; private set; } = "worktree-";
    public bool IsAgentBranch => BranchPrefix.Length > 0 && Branch?.StartsWith(BranchPrefix, StringComparison.Ordinal) == true;

    public string KindLabel => Kind switch
    {
        WorktreeKind.Main => "main checkout",
        WorktreeKind.Cow => IsAgentBranch ? "CoW · agent" : "CoW",
        WorktreeKind.Base => "CoW base",
        WorktreeKind.Codex => "Codex",
        WorktreeKind.Claude => "Claude",
        _ => "checkout",
    };

    public Brush KindBrush => Kind switch
    {
        WorktreeKind.Cow or WorktreeKind.Base => Theme.Attention,
        WorktreeKind.Main => Theme.Success,
        _ => Theme.Neutral,
    };

    public string BranchDisplay => Branch ?? "detached HEAD";
    public Brush BranchBrush => IsDetached ? Theme.Secondary : Theme.Primary;

    public string UpstreamDisplay
    {
        get
        {
            if (!HasProbe) return "";
            if (UpstreamGone) return "upstream gone";
            if (Upstream == null) return IsDetached ? "" : "no upstream";
            var parts = new List<string>();
            if (Ahead > 0) parts.Add($"↑{Ahead}");
            if (Behind > 0) parts.Add($"↓{Behind}");
            return (parts.Count == 0 ? "= " : string.Join(" ", parts) + "  ") + Upstream;
        }
    }

    /// <summary>"↑2 ↓6" (coloured in the row), "=" when level with the upstream, "" without one.</summary>
    public string AheadBehindText
    {
        get
        {
            if (!HasProbe || Upstream == null || UpstreamGone) return "";
            var parts = new List<string>();
            if (Ahead > 0) parts.Add($"↑{Ahead}");
            if (Behind > 0) parts.Add($"↓{Behind}");
            return parts.Count == 0 ? "=" : string.Join(" ", parts);
        }
    }

    /// <summary>The upstream's name after the counts, or why there is none.</summary>
    public string UpstreamName =>
        !HasProbe ? "" : UpstreamGone ? "upstream gone" : Upstream != null ? "  " + Upstream : IsDetached ? "" : "no upstream";

    public string CommitLine => ShortSha == null ? (HeadFromList?[..Math.Min(9, HeadFromList.Length)] ?? "") : $"{ShortSha}  {Subject}";
    public string CommitAge => CommitTime is { } t ? $"{Author} · {Fmt.Relative(t.LocalDateTime)}" : "";

    public string ChangedText => $"{Changed} changed";
    public string UntrackedText => $"{Untracked} untracked";
    public Visibility ChangedVisibility => Theme.Vis(Changed > 0);
    public Visibility UntrackedVisibility => Theme.Vis(Untracked > 0);
    public Visibility CleanVisibility => Theme.Vis(HasProbe && Error == null && !IsDirty && !IsMissing);
    public Visibility DirtyVisibility => Theme.Vis(IsDirty);
    public Visibility ErrorVisibility => Theme.Vis(Error != null || IsMissing);
    public string ErrorText => IsMissing ? "missing" + (MissingReason != null ? $": {MissingReason}" : "") : Error ?? "";

    public string UnpushedText => Unpushed is { } n ? (n == 0 ? "0" : n.ToString()) : "–";
    public Brush UnpushedBrush => AtRisk ? Theme.Critical : HasUnpushed ? Theme.Attention : Theme.Secondary;
    public string UnpushedCaption => AtRisk ? "no branch!" : "";
    public Visibility UnpushedCaptionVisibility => Theme.Vis(AtRisk);

    public string LastActivityText => Fmt.Relative(LastActivity);
    public string LastActivityCaption => IsCow && Cow != null ? $"idle {Cow.IdleDays:0.#} d" : "";
    public Visibility LoadingVisibility => Theme.Vis(IsLoading);
    public Visibility LockedVisibility => Theme.Vis(IsLocked);

    /// <summary>The bar at the left of the row: red = at risk or broken, orange = dirty, blue = unpushed commits.</summary>
    public Brush StatusBrush =>
        IsMissing || Error != null || AtRisk ? Theme.Critical
        : IsDirty ? Theme.Caution
        : HasUnpushed ? Theme.Attention
        : HasProbe ? Theme.Transparent : Theme.Transparent;

    public Visibility WouldPruneVisibility => Theme.Vis(WouldPrune);

    // ---- details pane --------------------------------------------------------------------------------------
    public string DetailHead => FullSha == null ? (HeadFromList ?? "") : $"{FullSha}";
    public string DetailCommitWhen => CommitTime is { } t ? $"{Author}, {t.LocalDateTime:yyyy-MM-dd HH:mm} ({Fmt.Relative(t.LocalDateTime)})" : "";
    public string DetailChanges => !HasProbe ? "…" : (IsDirty ? $"{Fmt.Plural(Changed, "changed file")}, {Fmt.Plural(Untracked, "untracked path")}" : "clean") + (Phantom > 0 ? $"\n{PhantomNote}" : "");
    public string DetailUpstream => !HasProbe ? "…" : UpstreamGone ? $"its upstream was deleted" : Upstream == null ? (IsDetached ? "none (detached HEAD)" : "none") : $"{Upstream}: {Ahead ?? 0} ahead, {Behind ?? 0} behind";
    public string DetailUnpushed => Unpushed is { } n ? (n == 0 ? "none: every commit is on a remote" : $"{Fmt.Plural(n, "commit")} on no remote{(AtRisk ? " and no branch (detached HEAD): removing the worktree would leave them only in the reflog" : "")}") : "…";
    public string DetailActivity => LastActivity is { } t ? $"{t:yyyy-MM-dd HH:mm} ({Fmt.Relative(t)})" : "–";
    public string DetailLock => IsLocked ? $"locked{(string.IsNullOrEmpty(LockReason) ? "" : $": {LockReason}")}" : "not locked";
    public string DetailStatusTime => HasProbe ? $"git status took {StatusTime.TotalSeconds:0.00} s, checked {CheckedAt:HH:mm:ss}" : "";
    public string DetailCow
    {
        get
        {
            if (Cow == null) return IsCow ? "not in the script's list yet" : "";
            var commits = Cow.Commits is { } c ? Fmt.Plural(c, "commit") + " since it was made" : "no start commit recorded";
            var start = Cow.Start is { Length: >= 9 } s ? $" (started at {s[..9]})" : "";
            return $"{commits}{start}; idle {Cow.IdleDays:0.#} days by the script's clock";
        }
    }
    public string DetailPrune(int days) => !IsCow || Cow == null ? "" :
        PruneKeepReasons.Count == 0 ? $"-Prune -OlderThanDays {days} would remove it" : $"-Prune keeps it: {string.Join(", ", PruneKeepReasons)}";
    public Visibility CowDetailVisibility => Theme.Vis(IsCow);
    public Visibility RemoveVisibility => Theme.Vis(CanRemove);
    public Visibility StatusLinesVisibility => Theme.Vis(StatusLines.Count > 0);
    public Visibility UnpushedLogVisibility => Theme.Vis(UnpushedLog.Count > 0);
    public string StatusLinesHeader => StatusLines.Count >= StatusLineCap ? $"Changes (first {StatusLineCap})" : $"Changes ({StatusLines.Count})";
    public string PruneText { get; private set; } = "";

    public const int StatusLineCap = 400;

    public void UpdateEntry(WorktreeEntry e, Pool pool)
    {
        BranchPrefix = pool.Config?.BranchPrefix ?? "worktree-";
        HeadFromList = e.Head;
        Branch = e.Branch;
        IsLocked = e.Locked;
        LockReason = e.LockReason;
        IsMissing = e.Prunable || !Directory.Exists(e.Path);
        MissingReason = e.PrunableReason;
        Kind = Classify(e, pool);
        Name = System.IO.Path.GetFileName(Path.TrimEnd('\\'));
        RaiseAll();
    }

    public void BeginProbe()
    {
        IsLoading = true;
        Raise(nameof(IsLoading), nameof(LoadingVisibility));
    }

    public void ApplyProbe(WorktreeProbe p)
    {
        HasProbe = true;
        IsLoading = false;
        Error = p.Error;
        if (p.Error == null || p.ShortSha != null)
        {
            ShortSha = p.ShortSha;
            FullSha = p.FullSha;
            Subject = p.Subject;
            Author = p.Author;
            CommitTime = p.CommitTime;
        }
        if (p.Error == null)
        {
            Upstream = p.Upstream;
            UpstreamGone = p.UpstreamGone;
            Ahead = p.Ahead;
            Behind = p.Behind;
            Changed = p.Changed;
            Untracked = p.Untracked;
            Phantom = p.Phantom;
            StatusTime = p.StatusTime;
            // real changes first; files that only look changed after them, marked "~"
            var lines = p.StatusLines.Concat(p.PhantomLines.Select(f => " ~ " + f)).Take(StatusLineCap).ToList();
            if (!lines.SequenceEqual(StatusLines))
            {
                StatusLines.Clear();
                foreach (var l in lines) StatusLines.Add(l);
            }
        }
        Unpushed = p.Unpushed ?? Unpushed;
        LastActivity = p.LastActivity ?? LastActivity;
        CheckedAt = DateTime.Now;
        RaiseAll();
    }

    public void ApplyCow(CowListRow? row, int pruneDays)
    {
        Cow = row;
        PruneKeepReasons = row?.PruneKeepReasons(pruneDays) ?? new List<string>();
        PruneText = DetailPrune(pruneDays);
        RaiseAll();
    }

    public void SetUnpushedLog(IEnumerable<string> lines)
    {
        UnpushedLog.Clear();
        foreach (var l in lines) UnpushedLog.Add(l);
        Raise(nameof(UnpushedLogVisibility));
    }

    /// <summary>Lower sorts first: broken, at risk, dirty with unpushed, dirty, unpushed, clean.</summary>
    public int AttentionRank =>
        IsMissing || Error != null ? 0
        : AtRisk ? 1
        : IsDirty && HasUnpushed ? 2
        : IsDirty ? 3
        : HasUnpushed ? 4
        : IsBase ? 6 : 5;

    public bool Matches(string text) =>
        string.IsNullOrWhiteSpace(text)
        || Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Path.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (Branch?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
        || KindLabel.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>What screen readers and UI Automation call the row.</summary>
    public override string ToString() => Name;

    private void RaiseAll()
    {
        foreach (var n in AllProperties) OnPropertyChanged(n);
    }

    public static WorktreeKind Classify(WorktreeEntry e, Pool pool)
    {
        if (e.IsMain) return WorktreeKind.Main;
        var p = GitService.NormalizePath(e.Path);
        if (p.Equals(GitService.NormalizePath(pool.BasePath), StringComparison.OrdinalIgnoreCase)) return WorktreeKind.Base;
        if (p.StartsWith(pool.Root + "\\", StringComparison.OrdinalIgnoreCase)) return WorktreeKind.Cow;
        if (p.Contains(@"\.codex\worktrees\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\Documents\Codex\", StringComparison.OrdinalIgnoreCase)) return WorktreeKind.Codex;
        if (p.Contains(@"\.claude\worktrees\", StringComparison.OrdinalIgnoreCase)) return WorktreeKind.Claude;
        return WorktreeKind.Checkout;
    }
}
