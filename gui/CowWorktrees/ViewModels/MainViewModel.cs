using System.Collections.ObjectModel;
using System.Diagnostics;
using CowWorktrees.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

// The order of both enums is the order of the Filter / Sort combo box items.
public enum RowFilter { All, Attention, Dirty, Unpushed, Prunable, Cow, Other }
public enum RowSort { Attention, Name, Branch, Commit, Changes, Unpushed, Activity }

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly GitService _git;
    private CowScript? _cow;
    private BaseHealthService? _health;
    private PoolRegistry _registry = new();
    private string? _registryError;
    private Pool? _pool;
    private int _generation;   // bumped on every pool switch, so a refresh of the old pool drops its results
    private readonly Dictionary<string, WorktreeViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherQueueTimer _refreshTimer;
    private readonly DispatcherQueueTimer _tickTimer;
    private bool _refreshing;
    private bool _again;
    private WorktreeViewModel? _selected;
    private RowFilter _filter = RowFilter.All;
    private RowSort _sort = RowSort.Attention;
    private bool _sortReversed;
    private string _search = "";

    public MainViewModel(DispatcherQueue dispatcher, string? preferredPool = null)
    {
        _dispatcher = dispatcher;
        Settings = AppSettings.Load();
        _git = new GitService(Settings.MaxParallelGit);
        LoadRegistry(preferredPool ?? Settings.SelectedPool);
        (EditorPath, EditorName) = Launcher.FindEditor(Settings.EditorPath);
        if (Enum.TryParse<RowSort>(Settings.Sort, out var sort)) _sort = sort;
        _sortReversed = Settings.SortReversed;
        if (Enum.TryParse<RowFilter>(Settings.Filter, out var filter)) _filter = filter;
        ColumnLayout.Current.SetWeights(Settings.ColumnWeights);

        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.Tick += async (_, _) => { if (!_refreshing) await RefreshAsync(); };
        SetInterval(Settings.RefreshIntervalSeconds, save: false);

        _tickTimer = dispatcher.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(1);
        _tickTimer.Tick += (_, _) =>
        {
            foreach (var op in Operations.Where(o => o.IsRunning)) op.Tick();
        };
        _tickTimer.Start();
    }

    public AppSettings Settings { get; }
    public string? EditorPath { get; }
    public string EditorName { get; }
    public string OpenInEditorLabel => EditorPath == null ? "No editor found" : EditorName;
    public bool EditorAvailable => EditorPath != null;

    private OperationViewModel? _selectedOperation;
    public OperationViewModel? SelectedOperation { get => _selectedOperation; set => SetProperty(ref _selectedOperation, value); }

    public ObservableCollection<WorktreeViewModel> Visible { get; } = new();
    public IReadOnlyCollection<WorktreeViewModel> AllRows => _rows.Values;
    public ObservableCollection<HealthItemViewModel> HealthItems { get; } = new();
    public ObservableCollection<OperationViewModel> Operations { get; } = new();

    public event Action<OperationViewModel>? OperationStarted;
    /// <summary>A create / remove / prune / refresh-base run finished and the list has been refreshed after it.</summary>
    public event Action<OperationViewModel>? OperationCompleted;
    public event Action? RefreshCompleted;

    // ---- pools -----------------------------------------------------------------------------------------------
    public ObservableCollection<Pool> Pools { get; } = new();
    public Pool? Pool => _pool;

    /// <summary>The pool picker's selection; switching clears the list and refreshes for the new pool.</summary>
    public Pool? SelectedPool
    {
        get => _pool;
        set
        {
            if (value == null || ReferenceEquals(value, _pool)) return;
            SwitchPool(value);
            _ = RefreshAsync();
        }
    }

    public bool HasUsablePool => _pool?.IsUsable == true;
    public Visibility PoolPickerVisibility => Theme.Vis(Pools.Count > 1);
    public Visibility PoolContentVisibility => Theme.Vis(HasUsablePool);
    public Visibility EmptyStateVisibility => Theme.Vis(!HasUsablePool);
    public string EmptyTitle =>
        _registryError != null ? "pools.json could not be read"
        : Pools.Count == 0 ? "No pools set up yet"
        : $"The pool \"{_pool?.Name}\" cannot be used";
    public string EmptyText =>
        _registryError
        ?? (Pools.Count == 0
            ? $"A pool is one repository's copy-on-write setup: a folder on a ReFS drive holding the base worktree and the worktrees cloned from it. Run setup.ps1 from the cow-worktrees folder to create one (see its README). Pools are listed in {CowHome.PoolsFile}, which this app reads."
            : $"{_pool?.ConfigError}\n\nPool folder: {_pool?.Root}");
    public string PoolCaption =>
        _pool == null ? "" : _pool.IsUsable ? $"{_pool.Name}  ·  {_pool.Repo}  ·  {_pool.Root}" : $"{_pool.Name}  ·  {_pool.Root}";
    public int DefaultPruneDays => _pool?.Config?.PruneDays ?? 7;
    public string BranchPrefix => _pool?.Config?.BranchPrefix ?? "worktree-";

    /// <summary>
    /// Re-reads pools.json and every pool's cow-config.json (cheap, and it means a pool that setup.ps1 adds, or a
    /// config someone fixes, shows up at the next refresh). Keeps the same Pool objects for roots that stay.
    /// </summary>
    private void LoadRegistry(string? preferred = null)
    {
        (_registry, _registryError) = PoolRegistry.Load();
        foreach (var old in Pools.ToList())
            if (!_registry.Pools.Any(p => SameRoot(p, old))) Pools.Remove(old);
        foreach (var p in _registry.Pools)
            if (!Pools.Any(x => SameRoot(x, p))) Pools.Add(p);
        foreach (var p in Pools) p.LoadConfig();

        if (_pool == null || !Pools.Contains(_pool))
        {
            var pick = Pools.FirstOrDefault(p => !string.IsNullOrEmpty(preferred) &&
                           (p.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase) || p.RegisteredName.Equals(preferred, StringComparison.OrdinalIgnoreCase)
                            || p.Root.Equals(Pool.FullPath(preferred), StringComparison.OrdinalIgnoreCase)))
                       ?? Pools.FirstOrDefault();
            if (pick != null) SwitchPool(pick, save: false);
            else
            {
                _pool = null;
                ClearPoolState();
            }
        }
        RaisePool();
    }

    private static bool SameRoot(Pool a, Pool b) => string.Equals(a.Root, b.Root, StringComparison.OrdinalIgnoreCase);

    private void SwitchPool(Pool pool, bool save = true)
    {
        _pool = pool;
        _generation++;
        if (save)
        {
            Settings.SelectedPool = pool.Name;
            Settings.Save();
        }
        ClearPoolState();
        RaisePool();
    }

    private void ClearPoolState()
    {
        _rows.Clear();
        Visible.Clear();
        SelectedWorktree = null;
        HealthItems.Clear();
        HookTail = RefreshTail = BuildTail = "";
        HealthSummary = "Base: …";
        HealthBrush = Theme.Neutral;
        HealthUpdated = "";
        LastRefresh = null;
        RaiseSummary();
        Raise(nameof(HookTail), nameof(RefreshTail), nameof(BuildTail), nameof(HealthSummary), nameof(HealthBrush), nameof(HealthUpdated), nameof(StatusText));
    }

    private void RaisePool() => Raise(nameof(Pool), nameof(SelectedPool), nameof(HasUsablePool), nameof(PoolPickerVisibility), nameof(PoolContentVisibility),
        nameof(EmptyStateVisibility), nameof(EmptyTitle), nameof(EmptyText), nameof(PoolCaption), nameof(DefaultPruneDays), nameof(BranchPrefix), nameof(ScriptAvailable));

    // ---- header and status -----------------------------------------------------------------------------------
    public bool IsRefreshing { get; private set; }
    public DateTime? LastRefresh { get; private set; }
    public TimeSpan LastDuration { get; private set; }
    public string? Problem { get; private set; }
    public string StatusText =>
        (LastRefresh == null ? "Reading worktrees…" : $"Updated {LastRefresh:HH:mm:ss} · {Fmt.Plural(_rows.Count, "worktree")} checked in {Fmt.Duration(LastDuration)}")
        + (Settings.RefreshIntervalSeconds > 0 ? $" · every {Settings.RefreshIntervalSeconds} s" : " · auto-refresh off");
    public Visibility ProblemVisibility => Theme.Vis(Problem != null);
    public Visibility RefreshingVisibility => Theme.Vis(IsRefreshing);

    public int RunningCount => Operations.Count(o => o.IsRunning);
    public string RunningText => RunningCount == 0 ? "" : $"{Fmt.Plural(RunningCount, "operation")} running";
    public Visibility RunningVisibility => Theme.Vis(RunningCount > 0);
    public bool RefreshBaseRunning => Operations.Any(o => o.IsRunning && o.Kind == OperationKind.RefreshBase);
    public bool PruneRunning => Operations.Any(o => o.IsRunning && (o.Kind == OperationKind.Prune || o.Kind == OperationKind.PrunePreview));

    // ---- summary chips ------------------------------------------------------------------------------------------
    public string TotalText => Fmt.Plural(_rows.Count, "worktree");
    public int DirtyCount => _rows.Values.Count(r => r.IsDirty);
    public int UnpushedCount => _rows.Values.Count(r => r.HasUnpushed);
    public int CowCount => _rows.Values.Count(r => r.IsCow);
    public int PrunableCount => _rows.Values.Count(r => r.WouldPrune);
    public int BrokenCount => _rows.Values.Count(r => r.IsMissing || r.Error != null);
    public string DirtyText => $"{DirtyCount} dirty";
    public string UnpushedText => $"{UnpushedCount} unpushed";
    public string CowText => $"{CowCount} copy-on-write";
    public string PrunableText => $"{PrunableCount} prunable";
    public string BrokenText => $"{BrokenCount} broken";
    public Visibility DirtyVisibility => Theme.Vis(DirtyCount > 0);
    public Visibility UnpushedVisibility => Theme.Vis(UnpushedCount > 0);
    public Visibility PrunableVisibility => Theme.Vis(PrunableCount > 0);
    public Visibility BrokenVisibility => Theme.Vis(BrokenCount > 0);

    // ---- base health ------------------------------------------------------------------------------------------
    public string HookTail { get; private set; } = "";
    public string RefreshTail { get; private set; } = "";
    public string BuildTail { get; private set; } = "";
    public string HealthSummary { get; private set; } = "Base: …";
    public Brush HealthBrush { get; private set; } = Theme.Neutral;
    public string HealthUpdated { get; private set; } = "";
    public bool BaseRefreshLockHeld { get; private set; }

    // ---- selection --------------------------------------------------------------------------------------------
    public WorktreeViewModel? SelectedWorktree
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            Raise(nameof(DetailsVisibility), nameof(NoSelectionVisibility));
            if (value != null) _ = LoadUnpushedLogAsync(value);
        }
    }

    public Visibility DetailsVisibility => Theme.Vis(_selected != null);
    public Visibility NoSelectionVisibility => Theme.Vis(_selected == null);

    private async Task LoadUnpushedLogAsync(WorktreeViewModel row)
    {
        if (!row.HasUnpushed || row.IsMissing)
        {
            row.SetUnpushedLog(Array.Empty<string>());
            return;
        }
        var lines = await Task.Run(() => _git.UnpushedLogAsync(row.Path));
        row.SetUnpushedLog(lines);
    }

    // ---- filter / sort ------------------------------------------------------------------------------------------
    public RowFilter Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            Settings.Filter = value.ToString();
            Settings.Save();
            ApplyView();
        }
    }

    /// <summary>Picking a sort from the combo box: that column in its natural direction.</summary>
    public RowSort Sort
    {
        get => _sort;
        set
        {
            if (_sort == value) return;
            _sort = value;
            _sortReversed = false;
            SortChanged();
        }
    }

    /// <summary>Clicking a column header: sort by it, or flip the direction when it already is the sort.</summary>
    public void SortByColumn(RowSort column)
    {
        if (_sort == column) _sortReversed = !_sortReversed;
        else
        {
            _sort = column;
            _sortReversed = false;
        }
        SortChanged();
    }

    /// <summary>Names and branches read A→Z first; times and counts newest / largest first.</summary>
    private static bool NaturallyDescending(RowSort s) => s is RowSort.Commit or RowSort.Changes or RowSort.Unpushed or RowSort.Activity;
    public bool SortDescending => NaturallyDescending(_sort) ^ _sortReversed;

    private string SortGlyph(RowSort column) => _sort != column ? "" : SortDescending ? "" : "";   // chevron down / up
    public string SortGlyphName => SortGlyph(RowSort.Name);
    public string SortGlyphBranch => SortGlyph(RowSort.Branch);
    public string SortGlyphCommit => SortGlyph(RowSort.Commit);
    public string SortGlyphChanges => SortGlyph(RowSort.Changes);
    public string SortGlyphUnpushed => SortGlyph(RowSort.Unpushed);
    public string SortGlyphActivity => SortGlyph(RowSort.Activity);

    private void SortChanged()
    {
        Settings.Sort = _sort.ToString();
        Settings.SortReversed = _sortReversed;
        Settings.Save();
        Raise(nameof(Sort), nameof(SortDescending), nameof(SortGlyphName), nameof(SortGlyphBranch), nameof(SortGlyphCommit),
            nameof(SortGlyphChanges), nameof(SortGlyphUnpushed), nameof(SortGlyphActivity));
        ApplyView();
    }

    public string Search { get => _search; set { if (SetProperty(ref _search, value ?? "")) ApplyView(); } }

    public void SetInterval(int seconds, bool save = true)
    {
        Settings.RefreshIntervalSeconds = Math.Max(0, seconds);
        _refreshTimer.Stop();
        if (seconds > 0)
        {
            _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, seconds));
            _refreshTimer.Start();
        }
        if (save) Settings.Save();
        OnPropertyChanged(nameof(StatusText));
    }

    private bool Passes(WorktreeViewModel r) =>
        r.Matches(_search) && _filter switch
        {
            RowFilter.Attention => r.IsDirty || r.HasUnpushed || r.IsMissing || r.Error != null || r.WouldPrune,
            RowFilter.Dirty => r.IsDirty,
            RowFilter.Unpushed => r.HasUnpushed,
            RowFilter.Prunable => r.WouldPrune,
            RowFilter.Cow => r.IsCow || r.IsBase,
            RowFilter.Other => !r.IsCow && !r.IsBase,
            _ => true,
        };

    private IEnumerable<WorktreeViewModel> Ordered(IEnumerable<WorktreeViewModel> rows)
    {
        bool desc = SortDescending;
        IOrderedEnumerable<WorktreeViewModel> ordered = _sort switch
        {
            RowSort.Name => By(rows, r => r.Name, desc, StringComparer.OrdinalIgnoreCase),
            RowSort.Branch => By(rows, r => r.Branch ?? "￿", desc, StringComparer.OrdinalIgnoreCase),   // detached last
            RowSort.Commit => By(rows, r => r.CommitTime ?? DateTimeOffset.MinValue, desc),
            RowSort.Changes => By(rows, r => r.Changed + r.Untracked, desc).ThenByDescending(r => r.Changed),
            RowSort.Unpushed => By(rows, r => r.Unpushed ?? -1, desc),
            RowSort.Activity => By(rows, r => r.LastActivity ?? DateTime.MinValue, desc),
            _ => By(rows, r => r.AttentionRank, desc).ThenByDescending(r => r.LastActivity ?? DateTime.MinValue),
        };
        return ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IOrderedEnumerable<T> By<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    /// <summary>Brings Visible in line with filter and sort using moves, so the selection and scroll position survive.</summary>
    public void ApplyView()
    {
        var desired = Ordered(_rows.Values.Where(Passes)).ToList();
        for (int i = 0; i < desired.Count; i++)
        {
            int cur = Visible.IndexOf(desired[i]);
            if (cur < 0) Visible.Insert(i, desired[i]);
            else if (cur != i) Visible.Move(cur, i);
        }
        while (Visible.Count > desired.Count) Visible.RemoveAt(Visible.Count - 1);
    }

    private void RaiseSummary() => Raise(nameof(TotalText), nameof(DirtyCount), nameof(UnpushedCount), nameof(CowCount), nameof(PrunableCount),
        nameof(BrokenCount), nameof(DirtyText), nameof(UnpushedText), nameof(CowText), nameof(PrunableText), nameof(BrokenText),
        nameof(DirtyVisibility), nameof(UnpushedVisibility), nameof(PrunableVisibility), nameof(BrokenVisibility));

    // ---- refresh ------------------------------------------------------------------------------------------------
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _again = true;
            return;
        }
        _refreshing = true;
        IsRefreshing = true;
        Raise(nameof(IsRefreshing), nameof(RefreshingVisibility));
        try
        {
            do
            {
                _again = false;
                await RefreshOnceAsync();
            } while (_again);
        }
        catch (Exception ex)
        {
            AppLog.Write($"refresh: {ex}");
            SetProblem(ex.Message);
        }
        finally
        {
            _refreshing = false;
            IsRefreshing = false;
            Raise(nameof(IsRefreshing), nameof(RefreshingVisibility), nameof(StatusText));
            RefreshCompleted?.Invoke();
        }
    }

    public void SetProblem(string? message)
    {
        Problem = message;
        Raise(nameof(Problem), nameof(ProblemVisibility));
    }

    private async Task RefreshOnceAsync()
    {
        var sw = Stopwatch.StartNew();
        var problems = new List<string>();
        LoadRegistry();
        var pool = _pool;
        int generation = _generation;
        if (pool == null || !pool.IsUsable)
        {
            _cow = null;
            _health = null;
            ClearPoolState();
            SetProblem(null);
            LastRefresh = DateTime.Now;
            LastDuration = sw.Elapsed;
            Raise(nameof(LastRefresh), nameof(StatusText));
            return;
        }

        _cow = new CowScript(_registry.ScriptPath, pool);
        _health = new BaseHealthService(pool, _git);
        var cow = _cow;
        var health = _health;
        var healthTask = Task.Run(() => health.ReadAsync());
        var cowTask = cow.ScriptExists ? Task.Run(() => cow.ListAsync()) : null;
        if (!cow.ScriptExists)
            problems.Add(string.IsNullOrEmpty(cow.ScriptPath)
                ? $"{CowHome.PoolsFile} has no toolDir: create, remove, prune and refresh are unavailable"
                : $"{cow.ScriptPath} not found: create, remove, prune and refresh are unavailable");
        Raise(nameof(ScriptAvailable));

        List<WorktreeEntry>? entries = null;
        try
        {
            entries = await Task.Run(() => _git.ListWorktreesAsync(pool.Repo));
        }
        catch (Exception ex)
        {
            problems.Add(ex.Message);
        }
        if (generation != _generation) return;   // the pool changed while git ran

        if (entries != null)
        {
            SyncRows(entries, pool);
            ApplyView();
            await Task.WhenAll(entries.Where(e => !e.Bare).Select(e => ProbeRowAsync(_rows[e.Path], e)));
            if (generation != _generation) return;
        }

        if (cowTask != null)
        {
            try
            {
                var cowRows = await cowTask;
                if (generation != _generation) return;
                var byPath = cowRows.ToDictionary(c => GitService.NormalizePath(c.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var row in _rows.Values.Where(r => r.IsCow))
                    row.ApplyCow(byPath.GetValueOrDefault(row.Path), DefaultPruneDays);
            }
            catch (Exception ex)
            {
                problems.Add(ex.Message);
            }
        }

        try
        {
            var snap = await healthTask;
            if (generation != _generation) return;
            ApplyHealth(snap, pool);
        }
        catch (Exception ex)
        {
            problems.Add("base health: " + ex.Message);
        }

        ApplyView();
        RaiseSummary();
        LastRefresh = DateTime.Now;
        LastDuration = sw.Elapsed;
        SetProblem(problems.Count == 0 ? null : string.Join(" · ", problems));
        Raise(nameof(LastRefresh), nameof(LastDuration), nameof(StatusText));
        if (_selected != null && _selected.HasUnpushed) _ = LoadUnpushedLogAsync(_selected);
    }

    private void SyncRows(List<WorktreeEntry> entries, Pool pool)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(e => !e.Bare))
        {
            seen.Add(e.Path);
            if (!_rows.TryGetValue(e.Path, out var row))
            {
                row = new WorktreeViewModel(e.Path);
                _rows[e.Path] = row;
            }
            row.UpdateEntry(e, pool);
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var row = _rows[gone];
            _rows.Remove(gone);
            if (_selected == row) SelectedWorktree = null;
        }
    }

    private async Task ProbeRowAsync(WorktreeViewModel row, WorktreeEntry e)
    {
        if (row.IsMissing || e.Bare) return;
        row.BeginProbe();
        WorktreeProbe probe;
        try
        {
            probe = await Task.Run(() => _git.ProbeAsync(e));
        }
        catch (Exception ex)
        {
            probe = new WorktreeProbe { Error = ex.Message };
        }
        row.ApplyProbe(probe);
    }

    private void ApplyHealth(BaseHealthSnapshot snap, Pool pool)
    {
        var items = snap.Items.ToList();
        // Whether the base is clean comes from its row (the same status the list shows); the script refuses to
        // clone from a base with tracked changes.
        var baseRow = _rows.Values.FirstOrDefault(r => r.IsBase);
        if (baseRow == null)
            items.Insert(0, new HealthItem("Base worktree", "not registered", $"{pool.BasePath} is not a worktree of {pool.Repo}", Severity.Error));
        else if (baseRow.HasProbe)
            items.Insert(Math.Min(1, items.Count), baseRow.Changed == 0
                ? new HealthItem("Base worktree", baseRow.IsLocked ? "clean and locked" : "clean, NOT locked", baseRow.IsLocked ? $"{baseRow.Path}: no tracked changes{(baseRow.Untracked > 0 ? $" ({Fmt.Plural(baseRow.Untracked, "untracked path")}, which cloning ignores)" : "")}" : "the base should be locked so `git worktree prune` and agents leave it alone", baseRow.IsLocked ? Severity.Ok : Severity.Warning)
                : new HealthItem("Base worktree", $"{Fmt.Plural(baseRow.Changed, "changed path")}", "cow_worktree.ps1 refuses to clone from a base with tracked changes; new worktrees fail until it is clean", Severity.Error));

        HealthItems.Clear();
        foreach (var i in items) HealthItems.Add(new HealthItemViewModel(i));

        HookTail = snap.HookTail;
        RefreshTail = snap.RefreshTail;
        BuildTail = snap.BuildTail;
        BaseRefreshLockHeld = snap.RefreshRunning;

        var worst = items.FirstOrDefault(i => i.Severity == Severity.Error)
                    ?? items.FirstOrDefault(i => i.Severity == Severity.Busy)
                    ?? items.FirstOrDefault(i => i.Severity == Severity.Warning);
        if (worst == null)
        {
            HealthSummary = "Base: current";
            HealthBrush = Theme.Success;
        }
        else
        {
            HealthSummary = $"Base: {worst.Value}";
            HealthBrush = Theme.For(worst.Severity);
        }
        HealthUpdated = $"Checked {DateTime.Now:HH:mm:ss}";
        Raise(nameof(HookTail), nameof(RefreshTail), nameof(BuildTail), nameof(HealthSummary), nameof(HealthBrush), nameof(HealthUpdated), nameof(BaseRefreshLockHeld));
    }

    // ---- script operations ----------------------------------------------------------------------------------------
    public bool ScriptAvailable => _cow?.ScriptExists == true;
    public string ScriptPath => _registry.ScriptPath;

    public Task<(List<string> Local, List<string> Remote)> GetRefsAsync() => Task.Run(() => _git.ListRefsAsync(_pool?.Repo ?? ""));

    public string CommandLineFor(IEnumerable<string> args) => _cow?.CommandLine(args) ?? "";

    public OperationViewModel StartCreate(string name, string? branch, string? from)
    {
        var args = new List<string> { "-Name", name };
        if (!string.IsNullOrWhiteSpace(branch)) { args.Add("-Branch"); args.Add(branch.Trim()); }
        if (!string.IsNullOrWhiteSpace(from)) { args.Add("-From"); args.Add(from.Trim()); }
        return StartScript(OperationKind.Create, $"Create {name}", args, TimeSpan.FromHours(2));
    }

    public OperationViewModel StartRemove(WorktreeViewModel row) =>
        StartScript(OperationKind.Remove, $"Remove {row.Name}", new[] { "-Remove", "-Name", row.Name }, TimeSpan.FromMinutes(30));

    public OperationViewModel StartPrune(int days) =>
        StartScript(OperationKind.Prune, $"Prune (idle ≥ {days} d)", new[] { "-Prune", "-OlderThanDays", days.ToString() }, TimeSpan.FromHours(1));

    public Task<OperationViewModel> PrunePreviewAsync(int days) =>
        RunScriptAsync(new OperationViewModel(OperationKind.PrunePreview, $"Prune preview (idle ≥ {days} d)",
            _cow!.CommandLine(new[] { "-Prune", "-OlderThanDays", days.ToString(), "-DryRun" })),
            new[] { "-Prune", "-OlderThanDays", days.ToString(), "-DryRun" }, TimeSpan.FromMinutes(10), refreshAfter: false);

    public OperationViewModel StartRefreshBase() =>
        StartScript(OperationKind.RefreshBase, "Refresh base (fetch + build)", new[] { "-RefreshBase", "-Fetch", "-Build" }, TimeSpan.FromHours(4));

    private OperationViewModel StartScript(OperationKind kind, string title, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var op = new OperationViewModel(kind, title, _cow!.CommandLine(args));
        _ = RunScriptAsync(op, args, timeout, refreshAfter: true);
        return op;
    }

    private async Task<OperationViewModel> RunScriptAsync(OperationViewModel op, IReadOnlyList<string> args, TimeSpan timeout, bool refreshAfter)
    {
        Operations.Insert(0, op);
        RaiseRunning();
        OperationStarted?.Invoke(op);
        var dq = _dispatcher;
        var cow = _cow!;
        try
        {
            var r = await Task.Run(() => cow.RunAsync(args, (line, isErr) => dq.TryEnqueue(() => op.AddLine(line, isErr)), null, timeout));
            op.Complete(r);
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
        }
        AppLog.Write($"operation '{op.Title}' {op.StatusText} in {op.ElapsedText}: {op.CommandLine}");
        RaiseRunning();
        if (refreshAfter)
        {
            await RefreshAsync();
            OperationCompleted?.Invoke(op);
        }
        return op;
    }

    private void RaiseRunning() => Raise(nameof(RunningCount), nameof(RunningText), nameof(RunningVisibility), nameof(RefreshBaseRunning), nameof(PruneRunning));

    public WorktreeViewModel? FindRow(string pathOrName) =>
        _rows.TryGetValue(GitService.NormalizePath(pathOrName), out var r) ? r
        : _rows.Values.FirstOrDefault(x => x.Name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>What every row shows, for --dump.</summary>
    public object Dump() => new
    {
        poolsFile = CowHome.PoolsFile,
        registryError = _registryError,
        script = _registry.ScriptPath,
        pools = Pools.Select(p => new { p.Name, p.Root, p.Repo, p.ConfigError, p.Id, p.StateMutexName, p.BuildMutexName, Task = p.TaskFullPath }),
        pool = _pool?.Name,
        refreshedAt = LastRefresh,
        seconds = LastDuration.TotalSeconds,
        problem = Problem,
        health = HealthItems.Select(h => new { h.Title, h.Value, h.Detail, Severity = h.Severity.ToString() }),
        worktrees = _rows.Values.OrderBy(r => r.Path).Select(r => new
        {
            r.Name,
            r.Path,
            Kind = r.Kind.ToString(),
            r.Branch,
            r.ShortSha,
            r.Subject,
            r.Changed,
            r.Untracked,
            r.Unpushed,
            r.Upstream,
            r.Ahead,
            r.Behind,
            r.UpstreamGone,
            r.IsLocked,
            r.IsMissing,
            r.Error,
            r.LastActivity,
            StatusSeconds = r.StatusTime.TotalSeconds,
            CowCommits = r.Cow?.Commits,
            CowIdleDays = r.Cow?.IdleDays,
            r.WouldPrune,
            PruneKeep = r.PruneKeepReasons,
        }),
    };
}
