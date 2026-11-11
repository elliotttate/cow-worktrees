using System.Collections.ObjectModel;
using System.Globalization;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

/// <summary>The Changes view's colours, looked up on the UI thread (see Themes\DiffColors.xaml).</summary>
public static class DiffPalette
{
    private static Brush Get(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : Theme.Secondary;

    private static string Stem(ChangeKind k) => k switch
    {
        ChangeKind.Added => "DiffAdded",
        ChangeKind.Deleted => "DiffRemoved",
        ChangeKind.Renamed => "DiffRenamed",
        ChangeKind.Untracked => "DiffNew",
        ChangeKind.Conflict => "DiffConflict",
        ChangeKind.Phantom => "DiffQuiet",
        _ => "DiffModified",
    };

    public static Brush Text(ChangeKind k) => Get(Stem(k) + "TextBrush");

    private static readonly string[] TokenKeys = { "SyntaxCommentBrush", "SyntaxStringBrush", "SyntaxNumberBrush", "SyntaxKeywordBrush", "SyntaxMetaBrush" };
    private static Brush?[] _tokenBrushes = new Brush?[5];
    private static ApplicationTheme _tokenTheme;
    /// <summary>The colour of a kind of token; kept until the app's theme changes.</summary>
    public static Brush Token(TokenKind k)
    {
        var theme = Application.Current.RequestedTheme;
        if (theme != _tokenTheme) { _tokenTheme = theme; _tokenBrushes = new Brush?[5]; }
        return _tokenBrushes[(int)k] ??= Get(TokenKeys[(int)k]);
    }
    public static Brush Fill(ChangeKind k) => Get(Stem(k) + "FillBrush");
}

public enum ScopeKind { Uncommitted, Everything, Commit }

/// <summary>One thing the Changes view can compare: the uncommitted edits, all work in the worktree, or one commit.</summary>
public sealed class ChangeScope
{
    public ScopeKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Caption { get; init; } = "";
    public string Glyph { get; init; } = "";
    /// <summary>The commit the comparison starts from (Everything), or the commit shown (Commit).</summary>
    public string? Sha { get; init; }
    /// <summary>What --changes takes to choose it: "uncommitted", "all", or a commit's short name.</summary>
    public string Key { get; init; } = "";
    public override string ToString() => Title;
}

/// <summary>A changed file as the list on the left and its header in the diff show it.</summary>
public sealed class ChangeFileItem : ObservableObject
{
    private bool _collapsed;

    public ChangeFileItem(ChangedFile file)
    {
        File = file;
        int slash = file.Path.LastIndexOf('/');
        Name = slash < 0 ? file.Path : file.Path[(slash + 1)..];
        Folder = slash < 0 ? "" : file.Path[..(slash + 1)];
        Header = new FileHeaderRow { File = this };
        Family = SyntaxTint.FamilyOf(file.Path);
        _collapsed = file.Kind == ChangeKind.Phantom;
    }

    public ChangedFile File { get; }
    public FileHeaderRow Header { get; }
    public string Path => File.Path;
    public string Name { get; }
    public string Folder { get; }
    public Visibility FolderVisibility => Theme.Vis(Folder.Length > 0);
    /// <summary>How the file's text is coloured (comments, strings, keywords), from its name.</summary>
    public SyntaxFamily Family { get; }

    public string Badge => File.Kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Untracked => "N",
        ChangeKind.TypeChanged => "T",
        ChangeKind.Conflict => "!",
        ChangeKind.Phantom => "≈",
        _ => "M",
    };

    public string KindText => File.Kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Untracked => "new file",
        ChangeKind.TypeChanged => "type changed",
        ChangeKind.Conflict => "conflict",
        ChangeKind.Phantom => "only looks changed",
        _ => "modified",
    };

    public Brush KindBrush => DiffPalette.Text(File.Kind);
    public Brush KindFill => DiffPalette.Fill(File.Kind);

    /// <summary>The line under the path in the diff: where it came from, that it is staged, that it is binary.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { KindText };
            if (File.OldPath != null) parts.Add("was " + File.OldPath);
            if (File.Binary) parts.Add("binary");
            if (File.Staged) parts.Add("staged");
            return string.Join("  ·  ", parts);
        }
    }

    public string Tip => File.Path + "\n" + Detail + (File.Added + File.Deleted > 0 ? $"\n{Fmt.Plural(File.Added, "line")} added, {File.Deleted} removed" : "");

    public static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
    public string AddedText => File.Added > 0 ? "+" + N(File.Added) : "";
    public string DeletedText => File.Deleted > 0 ? "−" + N(File.Deleted) : "";
    public Visibility AddedVisibility => Theme.Vis(File.Added > 0);
    public Visibility DeletedVisibility => Theme.Vis(File.Deleted > 0);

    // The small bar after the counts: five parts, green for added and red for removed, in proportion.
    private (int Green, int Red) Parts()
    {
        int total = File.Added + File.Deleted;
        if (total == 0) return (0, 0);
        int g = (int)Math.Round(5.0 * File.Added / total), r = 5 - g;
        if (File.Added > 0 && g == 0) { g = 1; r = 4; }
        if (File.Deleted > 0 && r == 0) { r = 1; g = 4; }
        return (g, r);
    }
    public double GreenWidth => Parts().Green * 8;
    public double RedWidth => Parts().Red * 8;
    public double EmptyWidth => 40 - GreenWidth - RedWidth;

    public bool IsCollapsed
    {
        get => _collapsed;
        set { if (SetProperty(ref _collapsed, value)) Raise(nameof(Chevron), nameof(CollapseTip)); }
    }
    public string Chevron => _collapsed ? "" : "";
    public string CollapseTip => _collapsed ? "Show this file's lines" : "Hide this file's lines";
    /// <summary>Show every line that was read, not only the first <see cref="ChangesViewModel.LinesShownPerFile"/>.</summary>
    public bool ShowAll { get; set; }
}

// ---- rows of the diff: one flat, virtualised list of file headers, line ranges, lines and notes ----------------------

public abstract class DiffRow
{
    public ChangeFileItem File { get; init; } = null!;   // always set; not "required" because XAML type info must be able to construct the row
}

public sealed class FileHeaderRow : DiffRow { }

/// <summary>"@@ -12,7 +12,9 @@" and the function the lines are in.</summary>
public sealed class HunkRow : DiffRow
{
    public string Range { get; init; } = "";
    public string Context { get; init; } = "";
}

public sealed class LineRow : DiffRow
{
    public DiffLine Line { get; init; } = null!;
    public DiffLineKind Kind => Line.Kind;
    public string OldNo => Line.OldNo > 0 ? Line.OldNo.ToString(CultureInfo.InvariantCulture) : "";
    public string NewNo => Line.NewNo > 0 ? Line.NewNo.ToString(CultureInfo.InvariantCulture) : "";
    public string Marker => Line.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "−", _ => "" };
    /// <summary>A space for an empty line, so the row keeps its height.</summary>
    public string Text => Line.Text.Length == 0 ? " " : Line.Text;
    public int HighlightStart => Line.HighlightStart;
    public int HighlightLength => Line.HighlightLength;
    private IReadOnlyList<SyntaxToken>? _tokens;
    /// <summary>The comments, strings, numbers and keywords in the line; worked out the first time the line is shown.</summary>
    public IReadOnlyList<SyntaxToken> Tokens => _tokens ??= SyntaxTint.Tokens(Line.Text, File.Family);
    public Visibility NoNewlineVisibility => Theme.Vis(Line.NoNewline);
}

public sealed class NoteRow : DiffRow
{
    public string Text { get; init; } = "";
    public string? ActionLabel { get; init; }
    public Action? Run { get; init; }
    public Visibility ActionVisibility => Theme.Vis(Run != null);
}

/// <summary>The closing edge under a file's lines.</summary>
public sealed class FileEndRow : DiffRow { }

/// <summary>
/// What changed in one worktree: a list of files with how much each changed, and every changed line. It
/// only reads; nothing in the worktree is touched.
/// </summary>
public sealed class ChangesViewModel : ObservableObject
{
    /// <summary>Lines of one file that are put in the list at first; "Show them" adds the rest.</summary>
    public const int LinesShownPerFile = 1500;

    private readonly MainViewModel _main;
    private readonly DiffService _diff;
    private CancellationTokenSource? _cts;
    private ChangeSet _set = new();
    private List<ChangeFileItem> _all = new();
    private ChangeScope? _scope;
    private string _filter = "";
    private bool _loading;
    private bool _stale;
    private string _loadedSignature = "";
    private string? _problem;
    private IReadOnlyList<DiffRow> _rows = Array.Empty<DiffRow>();
    private bool _settingUp;

    public ChangesViewModel(MainViewModel main, WorktreeViewModel row)
    {
        _main = main;
        Row = row;
        _diff = new DiffService(main.Git);
        _rowChanged = (_, _) => CheckStale();
        Row.PropertyChanged += _rowChanged;
    }

    private readonly System.ComponentModel.PropertyChangedEventHandler _rowChanged;

    /// <summary>Stops following the row. Called when the view is let go, so that it can be collected.</summary>
    public void Detach()
    {
        Row.PropertyChanged -= _rowChanged;
        _cts?.Cancel();
    }

    /// <summary>The scope --changes or a commit's row names: "uncommitted", "all", or the start of a commit's name.</summary>
    public ChangeScope? FindScope(string key) =>
        Scopes.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? Scopes.FirstOrDefault(s => s.Kind == ScopeKind.Commit && s.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Switches to a scope of the list and reads it; the task ends when it is read.</summary>
    public Task ShowScopeAsync(ChangeScope scope)
    {
        if (ReferenceEquals(scope, _scope)) return Task.CompletedTask;
        _settingUp = true;
        try { Scope = scope; } finally { _settingUp = false; }
        return LoadAsync();
    }

    public WorktreeViewModel Row { get; }
    public string Title => Row.Name;
    public string Where => Row.Path;
    public string Branch => Row.BranchDisplay;

    public ObservableCollection<ChangeScope> Scopes { get; } = new();
    private List<ChangeFileItem> _files = new();
    /// <summary>The files the filter lets through. A new list each time, so the view takes two thousand files in one step.</summary>
    public List<ChangeFileItem> Files
    {
        get => _files;
        private set { _files = value; OnPropertyChanged(); }
    }

    /// <summary>Raised after <see cref="Rows"/> was rebuilt; the argument is the row to bring to the top, if any.</summary>
    public event Action<DiffRow?>? RowsChanged;

    public ChangeScope? Scope
    {
        get => _scope;
        set
        {
            if (value == null || !SetProperty(ref _scope, value)) return;
            Raise(nameof(ScopeCaption));
            if (!_settingUp) _ = LoadAsync();
        }
    }
    public string ScopeCaption => _scope?.Caption ?? "";

    public IReadOnlyList<DiffRow> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public bool IsLoading
    {
        get => _loading;
        private set { if (SetProperty(ref _loading, value)) Raise(nameof(LoadingVisibility), nameof(EmptyVisibility), nameof(BodyVisibility)); }
    }
    public Visibility LoadingVisibility => Theme.Vis(_loading);

    public string? Problem
    {
        get => _problem;
        private set { if (SetProperty(ref _problem, value)) Raise(nameof(ProblemVisibility), nameof(EmptyVisibility), nameof(BodyVisibility)); }
    }
    public Visibility ProblemVisibility => Theme.Vis(_problem != null);

    public bool HasFiles => _all.Count > 0;
    public Visibility EmptyVisibility => Theme.Vis(!_loading && _problem == null && _all.Count == 0);
    public Visibility BodyVisibility => Theme.Vis(_all.Count > 0);
    public string EmptyTitle => _scope?.Kind == ScopeKind.Uncommitted ? "Nothing is waiting to be committed" : "No changes";
    public string EmptyText => _scope?.Kind switch
    {
        ScopeKind.Uncommitted => "Every file in this worktree is as its last commit has it, and there are no new files." + (Scopes.Count > 1 ? " Choose another view at the top to see its commits." : ""),
        ScopeKind.Everything => "This worktree is exactly where it started.",
        _ => "This commit changes no files.",
    };

    /// <summary>The row changed (new edits, a new commit) after this list was read.</summary>
    public bool Stale
    {
        get => _stale;
        private set { if (SetProperty(ref _stale, value)) Raise(nameof(StaleVisibility)); }
    }
    public Visibility StaleVisibility => Theme.Vis(_stale);

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? "")) return;
            ApplyFilter();
            BuildRows(null);
        }
    }

    // ---- the totals in the header --------------------------------------------------------------------------------------

    private int Counted => _all.Count(f => f.File.Kind != ChangeKind.Phantom);
    public string FilesText => Fmt.Plural(Counted, "file");
    public string AddedText => "+" + ChangeFileItem.N(_set.Added);
    public string DeletedText => "−" + ChangeFileItem.N(_set.Deleted);
    public Visibility TotalsVisibility => Theme.Vis(_all.Count > 0);
    /// <summary>Green share of the totals bar, as a star width.</summary>
    public GridLength AddedShare => new(Math.Max(_set.Added, _set.Added + _set.Deleted == 0 ? 1 : 0), GridUnitType.Star);
    public GridLength DeletedShare => new(_set.Deleted, GridUnitType.Star);
    public string FilesCaption
    {
        get
        {
            var kinds = _all.GroupBy(f => f.File.Kind).OrderBy(g => g.Key).Select(g => g.Key switch
            {
                ChangeKind.Untracked => $"{g.Count()} new",
                ChangeKind.Conflict => $"{g.Count()} in conflict",
                ChangeKind.Phantom => g.Count() == 1 ? "1 only looks changed" : $"{g.Count()} only look changed",
                _ => $"{g.Count()} {g.First().KindText}",
            }).ToList();
            return string.Join(", ", kinds);
        }
    }
    public string Footnote
    {
        get
        {
            var notes = new List<string>();
            if (_set.UntrackedLeftOut > 0) notes.Add($"{ChangeFileItem.N(_set.UntrackedLeftOut)} more new files are not listed");
            if (_set.LinesCutOff) notes.Add($"the comparison is very large: lines were read up to {ChangeFileItem.N(DiffService.MaxLinesTotal)}");
            if (Files.Count != _all.Count) notes.Add($"showing {Files.Count} of {_all.Count} files");
            return notes.Count == 0 ? $"read in {_set.Elapsed.TotalSeconds:0.0} s" : string.Join(" · ", notes);
        }
    }

    // ---- loading -----------------------------------------------------------------------------------------------------

    /// <summary>Works out what can be compared, picks the most useful one (or <paramref name="want"/>), and reads it.</summary>
    public async Task InitAsync(string? want = null)
    {
        IsLoading = true;
        _settingUp = true;
        try
        {
            await BuildScopesAsync();
            ChangeScope? pick = want == null ? null : Scopes.FirstOrDefault(s => s.Key.Equals(want, StringComparison.OrdinalIgnoreCase))
                ?? Scopes.FirstOrDefault(s => s.Kind == ScopeKind.Commit && s.Key.StartsWith(want, StringComparison.OrdinalIgnoreCase));
            // A commit that is not in the list (it is on a remote already): it can be looked at all the same.
            if (pick == null && want is { Length: >= 4 } && await _diff.ResolveAsync(Row.Path, want) is { } sha)
            {
                var one = await _main.Git.RunAsync(Row.Path, new[] { "log", "-1", "--no-show-signature", "--format=%h%x1f%s%x1f%an", sha });
                var parts = one.StdOut.TrimEnd('\r', '\n').Split('\x1f');
                pick = new ChangeScope
                {
                    Kind = ScopeKind.Commit,
                    Key = parts.Length >= 3 ? parts[0] : want,
                    Sha = sha,
                    Title = parts.Length >= 3 ? $"{parts[0]}  {parts[1]}" : want,
                    Caption = parts.Length >= 3 ? $"Commit by {parts[2]}" : "Commit",
                    Glyph = "\uE73E",
                };
                Scopes.Add(pick);
            }
            // Nothing uncommitted but there is work in commits: open on all of it instead of an empty list.
            pick ??= !Row.IsDirty && Scopes.FirstOrDefault(s => s.Kind == ScopeKind.Everything) is { } everything && Scopes.Any(s => s.Kind == ScopeKind.Commit) ? everything : Scopes[0];
            Scope = pick;
        }
        catch (Exception ex)
        {
            AppLog.Write($"changes view: {ex}");
            if (Scopes.Count == 0) Scopes.Add(Uncommitted());
            _scope = Scopes[0];
            OnPropertyChanged(nameof(Scope));
        }
        finally { _settingUp = false; }
        await LoadAsync();
    }

    private static ChangeScope Uncommitted() => new()
    {
        Kind = ScopeKind.Uncommitted,
        Key = "uncommitted",
        Title = "Uncommitted changes",
        Caption = "Edits and new files that are not in a commit yet",
        Glyph = "",
    };

    private async Task BuildScopesAsync()
    {
        Scopes.Clear();
        Scopes.Add(Uncommitted());
        string dir = Row.Path;

        // Where this worktree's own work starts: the commit it was made at, else where its branch left its
        // upstream, else where it left the pool's base ref.
        string? start = null, startWhat = null;
        if (!string.IsNullOrWhiteSpace(Row.Cow?.Start) && await _diff.ResolveAsync(dir, Row.Cow!.Start!) is { } made)
        {
            start = made;
            startWhat = "since this worktree was made";
        }
        else if (Row.Upstream != null && !Row.UpstreamGone && await _diff.MergeBaseAsync(dir, Row.Upstream) is { } fork)
        {
            start = fork;
            startWhat = $"that is not on {Row.Upstream}";
        }
        else if (!Row.IsBase && _main.Pool?.Config?.BaseRef is { Length: > 0 } baseRef && await _diff.MergeBaseAsync(dir, baseRef) is { } fromBase)
        {
            start = fromBase;
            startWhat = $"that is not on {baseRef}";
        }

        var commits = await _diff.CommitsAsync(dir, start);
        if (start != null && (commits.Count > 0 || Row.IsDirty))
        {
            Scopes.Add(new ChangeScope
            {
                Kind = ScopeKind.Everything,
                Key = "all",
                Sha = start,
                Title = "Everything " + startWhat,
                Caption = commits.Count == 0 ? $"Compared with {start[..9]}: no commits yet, only uncommitted edits"
                    : $"{Fmt.Plural(commits.Count, "commit")} and the uncommitted edits, compared with {start[..9]}",
                Glyph = "",
            });
        }
        foreach (var c in commits)
        {
            Scopes.Add(new ChangeScope
            {
                Kind = ScopeKind.Commit,
                Key = c.ShortSha,
                Sha = c.Sha,
                Title = $"{c.ShortSha}  {c.Subject}",
                Caption = $"Commit by {c.Author}, {Fmt.Relative(c.When.LocalDateTime)}",
                Glyph = "",
            });
        }
    }

    public Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        if (_scope is not { } scope) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        Problem = null;
        string signature = Signature();
        try
        {
            DiffSpec spec = scope.Kind switch
            {
                ScopeKind.Everything => new DiffSpec(scope.Sha, null),
                ScopeKind.Commit => new DiffSpec(await _diff.ParentAsync(Row.Path, scope.Sha!, cts.Token), scope.Sha),
                _ => new DiffSpec("HEAD", null),
            };
            // Paths the list already knows only look changed (it marks them " ~ ").
            var phantoms = Row.StatusLines.Where(l => l.StartsWith(" ~ ", StringComparison.Ordinal)).Select(l => l[3..]).ToList();
            var set = await _diff.LoadAsync(Row.Path, spec, phantoms, cts.Token);
            if (cts.IsCancellationRequested) return;
            _set = set;
            _all = set.Files.Select(f => new ChangeFileItem(f)).ToList();
            // A long list opens folded, so the file list is the first thing read; a short one opens with every line.
            if (_all.Count(f => f.File.Kind != ChangeKind.Phantom) > 150) foreach (var f in _all) f.IsCollapsed = true;
            Problem = set.Problem;
            _loadedSignature = signature;
            Stale = false;
            ApplyFilter();
            BuildRows(null);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            AppLog.Write($"changes view: {ex}");
            Problem = "The changes could not be read: " + ex.Message;
        }
        finally
        {
            if (_cts == cts)
            {
                IsLoading = false;
                Raise(nameof(HasFiles), nameof(EmptyVisibility), nameof(BodyVisibility), nameof(EmptyTitle), nameof(EmptyText), nameof(FilesText), nameof(AddedText), nameof(DeletedText),
                    nameof(TotalsVisibility), nameof(AddedShare), nameof(DeletedShare), nameof(FilesCaption), nameof(Footnote));
            }
        }
    }

    private string Signature() => $"{Row.FullSha}|{Row.Changed}|{Row.Untracked}|{Row.LastActivity?.Ticks}";

    private void CheckStale()
    {
        if (_loading || _loadedSignature.Length == 0) return;
        Stale = Signature() != _loadedSignature;
    }

    private void ApplyFilter()
    {
        var words = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Files = words.Length == 0 ? new List<ChangeFileItem>(_all)
            : _all.Where(f => words.All(w => f.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || f.KindText.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        Raise(nameof(Footnote));
    }

    // ---- the rows ----------------------------------------------------------------------------------------------------

    private void BuildRows(DiffRow? anchor, ChangeFileItem? anchorFile = null, int anchorOffset = 0)
    {
        var rows = new List<DiffRow>(Files.Sum(f => f.IsCollapsed ? 2 : f.File.LineCount + f.File.Hunks.Count + 3));
        foreach (var f in Files)
        {
            rows.Add(f.Header);
            if (!f.IsCollapsed) AddLines(rows, f);
            rows.Add(new FileEndRow { File = f });
        }
        Rows = rows;
        if (anchorFile != null)
        {
            int header = rows.IndexOf(anchorFile.Header);
            if (header >= 0) anchor = rows[Math.Min(header + anchorOffset, rows.Count - 1)];
        }
        RowsChanged?.Invoke(anchor);
    }

    private void AddLines(List<DiffRow> rows, ChangeFileItem f)
    {
        var file = f.File;
        if (file.Note != null) rows.Add(new NoteRow { File = f, Text = file.Note });
        int cap = f.ShowAll ? int.MaxValue : LinesShownPerFile, shown = 0, total = file.LineCount;
        // A file that is all new or all gone has one range, the whole file: its header line would say nothing.
        bool wholeFile = file.Hunks.Count == 1 && (file.Hunks[0].OldCount == 0 || file.Hunks[0].NewCount == 0);
        foreach (var h in file.Hunks)
        {
            if (shown >= cap) break;
            if (!wholeFile)
            {
                rows.Add(new HunkRow
                {
                    File = f,
                    Range = $"@@ −{h.OldStart},{h.OldCount} +{h.NewStart},{h.NewCount} @@",
                    Context = h.Context,
                });
            }
            foreach (var l in h.Lines)
            {
                if (shown >= cap) break;
                rows.Add(new LineRow { File = f, Line = l });
                shown++;
            }
        }
        if (shown < total)
        {
            rows.Add(new NoteRow
            {
                File = f,
                Text = $"{ChangeFileItem.N(total - shown)} more lines of this file are not shown.",
                ActionLabel = "Show them",
                Run = () => ShowAllLines(f),
            });
        }
        if (file.LinesLeftOut > 0)
            rows.Add(new NoteRow { File = f, Text = $"{ChangeFileItem.N(file.LinesLeftOut)} more changed lines were not read: this file's comparison is very long. Open the file to see all of it." });
    }

    /// <summary>Adds the lines that were held back; the last line that was already there stays in view.</summary>
    private void ShowAllLines(ChangeFileItem f)
    {
        int header = -1, last = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (header < 0) { if (ReferenceEquals(_rows[i], f.Header)) header = i; continue; }
            if (!ReferenceEquals(_rows[i].File, f)) break;
            if (_rows[i] is LineRow) last = i - header;
        }
        f.ShowAll = true;
        BuildRows(null, f, last);
    }

    public void Toggle(ChangeFileItem f)
    {
        f.IsCollapsed = !f.IsCollapsed;
        BuildRows(f.Header);
    }

    public void SetAllCollapsed(bool collapsed)
    {
        foreach (var f in _all) f.IsCollapsed = collapsed || f.File.Kind == ChangeKind.Phantom;
        BuildRows(null);
    }

    public bool AnyExpanded => _all.Any(f => !f.IsCollapsed);

    /// <summary>Opens a folded file when it is chosen in the list, so its lines are there to scroll to.</summary>
    public void Reveal(ChangeFileItem f)
    {
        if (!f.IsCollapsed || f.File.Kind == ChangeKind.Phantom) return;
        f.IsCollapsed = false;
        BuildRows(f.Header);
    }

    public string FullPath(ChangeFileItem f) => System.IO.Path.Combine(Row.Path, f.Path.Replace('/', '\\'));

    /// <summary>The file's changes as text, in the form `git diff` prints them.</summary>
    public static string PatchText(ChangeFileItem f)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("--- ").Append(f.File.Kind is ChangeKind.Added or ChangeKind.Untracked ? "/dev/null" : "a/" + (f.File.OldPath ?? f.File.Path)).Append('\n');
        sb.Append("+++ ").Append(f.File.Kind == ChangeKind.Deleted ? "/dev/null" : "b/" + f.File.Path).Append('\n');
        foreach (var h in f.File.Hunks)
        {
            sb.Append($"@@ -{h.OldStart},{h.OldCount} +{h.NewStart},{h.NewCount} @@ {h.Context}".TrimEnd()).Append('\n');
            foreach (var l in h.Lines)
                sb.Append(l.Kind switch { DiffLineKind.Added => '+', DiffLineKind.Removed => '-', _ => ' ' }).Append(l.Text).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>For --changes-dump: what the view holds, so a test can check it without looking at the screen.</summary>
    public object Dump() => new
    {
        worktree = Row.Path,
        scope = _scope?.Key,
        scopeTitle = _scope?.Title,
        scopes = Scopes.Select(s => new { s.Key, kind = s.Kind.ToString(), s.Title, s.Caption }).ToList(),
        problem = _problem,
        seconds = _set.Elapsed.TotalSeconds,
        added = _set.Added,
        deleted = _set.Deleted,
        filesText = FilesText,
        rows = _rows.Count,
        lineRows = _rows.Count(r => r is LineRow),
        files = _all.Select(f => new
        {
            path = f.Path,
            oldPath = f.File.OldPath,
            kind = f.File.Kind.ToString(),
            badge = f.Badge,
            added = f.File.Added,
            deleted = f.File.Deleted,
            binary = f.File.Binary,
            staged = f.File.Staged,
            hunks = f.File.Hunks.Count,
            lines = f.File.LineCount,
            highlighted = f.File.Hunks.Sum(h => h.Lines.Count(l => l.HighlightLength > 0)),
            note = f.File.Note,
            collapsed = f.IsCollapsed,
            firstAdded = f.File.Hunks.SelectMany(h => h.Lines).FirstOrDefault(l => l.Kind == DiffLineKind.Added)?.Text,
            firstRemoved = f.File.Hunks.SelectMany(h => h.Lines).FirstOrDefault(l => l.Kind == DiffLineKind.Removed)?.Text,
        }).ToList(),
    };
}
