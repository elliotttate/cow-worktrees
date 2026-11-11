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
        Disk = new DiskSpaceViewModel(this, dispatcher);
        Settings = AppSettings.Load();
        _git = new GitService(Settings.MaxParallelGit);
        LoadRegistry(preferredPool ?? Settings.SelectedPool);
        (EditorPath, EditorName) = Launcher.FindEditor(Settings.EditorPath);
        if (Enum.TryParse<RowSort>(Settings.Sort, out var sort)) _sort = sort;
        _sortReversed = Settings.SortReversed;
        if (Enum.TryParse<RowFilter>(Settings.Filter, out var filter)) _filter = filter;
        ColumnLayout.Current.SetWeights(Settings.ColumnWeights);

        _refreshTimer = dispatcher.CreateTimer();
        _refreshTimer.Tick += async (_, _) =>
        {
            // Not looked at (minimised, or hidden while a job finishes): a look every five minutes is enough.
            if (!WindowVisible && LastRefresh is { } last && DateTime.Now - last < TimeSpan.FromMinutes(5)) return;
            if (!_refreshing) await RefreshAsync();
        };
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

    /// <summary>What really takes the space on the pool's drive (Base tab, Disk space).</summary>
    public DiskSpaceViewModel Disk { get; }
    public string? EditorPath { get; private set; }
    public string EditorName { get; private set; }

    /// <summary>After the editor setting changed: look for it again.</summary>
    public void ReloadEditor()
    {
        (EditorPath, EditorName) = Launcher.FindEditor(Settings.EditorPath);
        Raise(nameof(EditorPath), nameof(EditorName), nameof(OpenInEditorLabel), nameof(EditorAvailable));
    }
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

    /// <summary>Set by the window. Coming back into view after a pause looks again at once.</summary>
    public bool WindowVisible { get; private set; } = true;

    public void SetWindowVisible(bool visible)
    {
        if (WindowVisible == visible) return;
        WindowVisible = visible;
        if (visible && !_refreshing && _refreshTimer.IsRunning && LastRefresh is { } last && DateTime.Now - last > _refreshTimer.Interval) _ = RefreshAsync();
    }

    /// <summary>What the listing script said last, and the state of the pool's worktrees it said it about.</summary>
    private sealed record CowAnswer(string Root, string Key, List<CowListRow> Rows, DateTime At);
    private CowAnswer? _cowAnswer;
    /// <summary>How long the last look took until every row was complete and shown (the whole look: <see cref="LastDuration"/>).</summary>
    public TimeSpan LastRowsDuration { get; private set; }
    /// <summary>The last look did not need the listing script: nothing it reports had changed.</summary>
    public bool LastScriptSkipped { get; private set; }

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
    public bool HasNoRepositories => _registryError == null && Pools.Count == 0;
    public string EmptyTitle =>
        _registryError != null ? "The list of repositories could not be read"
        : Pools.Count == 0 ? "Set up your first repository"
        : $"\"{_pool?.Name}\" cannot be used right now";
    public string EmptyText =>
        _registryError
        ?? (Pools.Count == 0
            ? "A worktree is a second working copy of your project, so you (or an AI agent) can work on several things at once. This app makes each one in seconds and without using more disk space. The walkthrough checks this PC, sets up one of your projects and connects Claude Code and Codex. It takes a few minutes."
            : $"{_pool?.ConfigError}\n\nFolder: {_pool?.Root}");
    public string EmptyPrimaryLabel => HasNoRepositories ? "Get started" : "Look again";
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
        Disk.SetPool(_pool is { IsUsable: true } ? _pool : null);
        RaiseSummary();
        Raise(nameof(HookTail), nameof(RefreshTail), nameof(BuildTail), nameof(HealthSummary), nameof(HealthBrush), nameof(HealthUpdated), nameof(StatusText));
    }

    private void RaisePool() => Raise(nameof(Pool), nameof(SelectedPool), nameof(HasUsablePool), nameof(PoolPickerVisibility), nameof(PoolContentVisibility),
        nameof(EmptyStateVisibility), nameof(EmptyTitle), nameof(EmptyText), nameof(EmptyPrimaryLabel), nameof(HasNoRepositories), nameof(HasAnyPool),
        nameof(PoolCaption), nameof(DefaultPruneDays), nameof(BranchPrefix), nameof(ScriptAvailable), nameof(ToolDir));

    public bool HasAnyPool => _pool != null;

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
        // The listing script takes about three seconds (PowerShell, then Git again for every worktree). With an
        // answer from an earlier look it is not started here: after the probes, only if what it reports changed.
        bool cowKnown = cow.ScriptExists && _cowAnswer != null && _cowAnswer.Root == pool.Root && DateTime.Now - _cowAnswer.At < TimeSpan.FromMinutes(10);
        var cowTask = cow.ScriptExists && !cowKnown ? Task.Run(() => cow.ListAsync()) : null;
        if (!cow.ScriptExists)
            problems.Add(string.IsNullOrEmpty(cow.ScriptPath)
                ? "The cow-worktrees scripts were not found next to this app, so create, remove, prune and refresh are unavailable"
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
            Disk.Sync();
            await Task.WhenAll(entries.Where(e => !e.Bare).Select(e => ProbeRowAsync(_rows[e.Path], e)));
            if (generation != _generation) return;
            // Every row is complete now: show them in their order, with the counts, without waiting for the rest.
            ApplyView();
            RaiseSummary();
            LastRowsDuration = sw.Elapsed;
        }

        // What the script's answer depends on: which copy-on-write worktrees there are, where each stands and how
        // many changes it has. (Its idle days move on by themselves; ten minutes old is close enough.)
        string cowKey = string.Join("|", _rows.Values.Where(r => r.IsCow).OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(r => $"{r.Path}*{r.FullSha ?? r.HeadFromList}*{r.Branch}*{r.Changed}*{r.Untracked}*{r.Phantom}*{r.Unpushed}*{r.IsLocked}*{r.IsMissing}"));
        if (cowKnown && _cowAnswer!.Key != cowKey) cowTask = Task.Run(() => cow.ListAsync());
        LastScriptSkipped = cow.ScriptExists && cowTask == null;

        if (cowTask != null)
        {
            try
            {
                var cowRows = await cowTask;
                if (generation != _generation) return;
                _cowAnswer = new CowAnswer(pool.Root, cowKey, cowRows, DateTime.Now);
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

        if (entries != null)
        {
            try
            {
                // setup.ps1 writes its hooks into every checkout of the repository
                var checkouts = entries.Where(e => !e.Bare && Directory.Exists(e.Path)).Select(e => e.Path).ToList();
                string? toolDir = ToolDir;
                var report = await Task.Run(() => IntegrationStatus.Read(pool, toolDir, checkouts));
                if (generation != _generation) return;
                Integrations = report;
                Raise(nameof(Integrations), nameof(DriftText), nameof(DriftVisibility));
            }
            catch (Exception ex)
            {
                AppLog.Write($"integration status: {ex.Message}");
            }
        }

        ApplyView();
        RaiseSummary();
        LastRefresh = DateTime.Now;
        LastDuration = sw.Elapsed;
        Disk.Sync();   // the rows get the numbers of the last measurement; nothing is measured here
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
                ? new HealthItem("Base worktree", baseRow.IsLocked ? "clean and locked" : "clean, NOT locked", baseRow.IsLocked ? $"{baseRow.Path}: no tracked changes{(baseRow.Untracked > 0 ? $" ({Fmt.Plural(baseRow.Untracked, "untracked path")}, which cloning ignores)" : "")}{(baseRow.Phantom > 0 ? $". {baseRow.PhantomNote}" : "")}" : "the base should be locked so `git worktree prune` and agents leave it alone", baseRow.IsLocked ? Severity.Ok : Severity.Warning)
                : new HealthItem("Base worktree", $"{Fmt.Plural(baseRow.Changed, "changed path")}", "New worktrees cannot be made until the base matches the project again. Something edited or built in it: put the files back with `git checkout -- <file>` in the base folder.", Severity.Error));

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

    /// <summary>The cow-worktrees folder scripts run from (the one this app sits in, else the one pools.json names); null when there is none.</summary>
    public string? ToolDir => _registry.ResolvedToolDir;
    public string SetupScript => ToolFolder.Script(ToolDir, "setup.ps1") ?? "";
    public string UninstallScript => ToolFolder.Script(ToolDir, "uninstall.ps1") ?? "";
    public string MigrateScript => ToolFolder.Script(ToolDir, "scripts", "migrate_worktree.ps1") ?? "";
    public bool SetupAvailable => File.Exists(SetupScript);
    public GitService Git => _git;

    /// <summary>What is connected for the selected repository, as found on disk at the last refresh.</summary>
    public IntegrationReport? Integrations { get; private set; }
    public string DriftText => Integrations?.Drift ?? "";
    public Visibility DriftVisibility => Theme.Vis(HasUsablePool && Integrations?.Drift != null && !SetupRunning);
    public bool SetupRunning => Operations.Any(o => o.IsRunning && o.Kind is OperationKind.Setup or OperationKind.Uninstall);
    public bool MigrateRunning => Operations.Any(o => o.IsRunning && o.Kind is OperationKind.Migrate or OperationKind.Retire);

    public Task<(List<string> Local, List<string> Remote)> GetRefsAsync() => Task.Run(() => _git.ListRefsAsync(_pool?.Repo ?? ""));

    public string CommandLineFor(IEnumerable<string> args) => _cow?.CommandLine(args) ?? "";

    public OperationViewModel StartCreate(string name, string? branch, string? from)
    {
        var args = new List<string> { "-Name", name };
        if (!string.IsNullOrWhiteSpace(branch)) { args.Add("-Branch"); args.Add(branch.Trim()); }
        if (!string.IsNullOrWhiteSpace(from)) { args.Add("-From"); args.Add(from.Trim()); }
        var pool = _pool!;
        string target = System.IO.Path.Combine(pool.Root, name);
        bool existed = Directory.Exists(target);
        var op = new OperationViewModel(OperationKind.Create, $"Create {name}", _cow!.CommandLine(args)) { Target = target, PoolRoot = pool.Root };
        // A clone that is stopped holds nothing of yours yet: what was made is removed again (see CleanUpUnfinishedCloneAsync).
        op.CanCancel = !existed;
        _ = RunScriptAsync(op, args, TimeSpan.FromHours(2), refreshAfter: true,
            afterCancel: existed ? null : () => CleanUpUnfinishedCloneAsync(pool, target, "cow.startCommit"));
        return op;
    }

    public OperationViewModel StartRemove(WorktreeViewModel row) =>
        StartScript(OperationKind.Remove, $"Remove {row.Name}", new[] { "-Remove", "-Name", row.Name }, TimeSpan.FromMinutes(30));

    public OperationViewModel StartPrune(int days) =>
        StartScript(OperationKind.Prune, $"Prune (idle ≥ {days} d)", new[] { "-Prune", "-OlderThanDays", days.ToString() }, TimeSpan.FromHours(1));

    public Task<OperationViewModel> PrunePreviewAsync(int days) =>
        RunScriptAsync(new OperationViewModel(OperationKind.PrunePreview, $"Prune preview (idle ≥ {days} d)",
            _cow!.CommandLine(new[] { "-Prune", "-OlderThanDays", days.ToString(), "-DryRun" })),
            new[] { "-Prune", "-OlderThanDays", days.ToString(), "-DryRun" }, TimeSpan.FromMinutes(10), refreshAfter: false);

    /// <summary>The base is built after it moves only when the repository has a build command; without one, asking for a build is an error.</summary>
    public bool RefreshBuilds => _pool?.Config?.Build != null;
    public IReadOnlyList<string> RefreshBaseArgs => RefreshBuilds ? new[] { "-RefreshBase", "-Fetch", "-Build" } : new[] { "-RefreshBase", "-Fetch" };

    public OperationViewModel StartRefreshBase()
    {
        bool build = RefreshBuilds;
        var args = RefreshBaseArgs;
        var op = new OperationViewModel(OperationKind.RefreshBase, build ? "Refresh base (fetch + build)" : "Refresh base (fetch)", _cow!.CommandLine(args)) { PoolRoot = _pool?.Root };
        // Moving the base is never cut off. Once it has moved, only the build is left, and a stopped build just
        // leaves the base without build output until the next one.
        if (build)
            op.LineAdded += line =>
            {
                if (line.Contains("base moved from", StringComparison.Ordinal) || line.Contains("base already at", StringComparison.Ordinal)) op.CanCancel = true;
            };
        _ = RunScriptAsync(op, args, TimeSpan.FromHours(4), refreshAfter: true, afterCancel: () => Task.FromResult("Stopped. The base is where it should be; it has no build output until the next build finishes."));
        return op;
    }

    // ---- setup, removal and moving existing worktrees ------------------------------------------------------------

    /// <summary>Runs setup.ps1: a first setup from the walkthrough, or again with other choices from Repository settings.</summary>
    public OperationViewModel StartSetup(SetupOptions o, string? title = null)
    {
        var args = SetupRunner.SetupArgs(o);
        string script = SetupScript;
        string root = Services.Pool.FullPath(o.Root);
        var op = new OperationViewModel(OperationKind.Setup, title ?? $"Set up {System.IO.Path.GetFileName(o.Repo.TrimEnd('\\'))}", SetupRunner.CommandLine(script, args)) { Target = root, PoolRoot = root };
        var steps = new List<string> { "Checking prerequisites", "Base worktree", "Pool config" };
        if (o.Claude) steps.Add("Claude Code hooks");
        if (o.Codex) steps.Add("Codex instructions");
        if (o.Schedule) steps.Add("Daily upkeep task");
        if (o.BuildNow && !o.NoBuild) steps.Add("Building the base (warm clones)");
        op.ExpectSteps(steps);

        // The one long step is the first full copy of the project. It can be stopped, and what it made is removed
        // again, but only when this run is the one making it.
        string basePath = System.IO.Path.Combine(root, "base");
        bool baseExisted = Directory.Exists(basePath);
        bool rootExisted = Directory.Exists(root);
        if (!baseExisted)
            op.LineAdded += line =>
            {
                if (line.StartsWith("==> ", StringComparison.Ordinal)) op.CanCancel = line.Trim() == "==> Base worktree";
            };
        _ = RunOperationAsync(op,
            (onLine, ct) => SetupRunner.RunAsync(script, args, Directory.Exists(o.Repo) ? o.Repo : null, onLine, TimeSpan.FromHours(8), ct),
            refreshAfter: true,
            afterCancel: baseExisted ? null : () => CleanUpStoppedSetupAsync(o.Repo, root, basePath, rootExisted),
            afterSuccess: () =>
            {
                LoadRegistry();
                var added = Pools.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
                if (added != null && !ReferenceEquals(added, _pool)) SwitchPool(added);
                return Task.CompletedTask;
            });
        return op;
    }

    /// <summary>Runs uninstall.ps1: disconnects the repository; worktrees and branches stay. removePool also deletes the base copy.</summary>
    public OperationViewModel StartUninstall(Pool pool, bool removePool)
    {
        var args = SetupRunner.UninstallArgs(pool.Root, removePool);
        string script = UninstallScript;
        var op = new OperationViewModel(OperationKind.Uninstall, $"Remove {pool.Name}", SetupRunner.CommandLine(script, args)) { PoolRoot = pool.Root };
        _ = RunOperationAsync(op, (onLine, ct) => SetupRunner.RunAsync(script, args, null, onLine, TimeSpan.FromHours(1), ct), refreshAfter: true);
        return op;
    }

    /// <summary>
    /// Runs migrate_worktree.ps1 for a full worktree outside the pool: copies it into a copy-on-write clone and
    /// checks the copy. With <paramref name="retireAfter"/>, a second run (-RemoveOld) follows when the first
    /// succeeded: it checks again, removes the old folder and leaves a junction in its place.
    /// </summary>
    public OperationViewModel StartMigrate(string from, string name, bool retireAfter)
    {
        var pool = _pool!;
        string script = MigrateScript;
        string leaf = System.IO.Path.GetFileName(from.TrimEnd('\\'));
        string target = System.IO.Path.Combine(pool.Root, name);
        bool existed = Directory.Exists(target);
        var args = SetupRunner.MigrateArgs(from, name, pool.Root, removeOld: false);
        var op = new OperationViewModel(OperationKind.Migrate, $"Move {leaf} to copy-on-write", SetupRunner.CommandLine(script, args)) { Target = target, PoolRoot = pool.Root };
        // The copy can be stopped at any point: a clone that had begun copying is picked up by the next run, one
        // that had not is removed again.
        op.CanCancel = true;
        _ = RunOperationAsync(op,
            (onLine, ct) => SetupRunner.RunAsync(script, args, pool.Repo, onLine, TimeSpan.FromHours(12), ct),
            refreshAfter: true,
            afterCancel: existed ? () => Task.FromResult("Stopped. Run it again to pick up where it stopped; nothing was removed.") : () => CleanUpUnfinishedCloneAsync(pool, target, "cow.migrateFrom"),
            afterSuccess: () =>
            {
                if (retireAfter) StartRetire(from, name, pool);
                return Task.CompletedTask;
            });
        return op;
    }

    /// <summary>The second half of a move: verify the copy again, remove the old folder, leave a junction. Never cut off.</summary>
    public OperationViewModel StartRetire(string from, string name, Pool? pool = null)
    {
        pool ??= _pool!;
        string script = MigrateScript;
        var args = SetupRunner.MigrateArgs(from, name, pool.Root, removeOld: true);
        var op = new OperationViewModel(OperationKind.Retire, $"Retire the old {System.IO.Path.GetFileName(from.TrimEnd('\\'))}", SetupRunner.CommandLine(script, args)) { Target = System.IO.Path.Combine(pool.Root, name), PoolRoot = pool.Root };
        _ = RunOperationAsync(op, (onLine, ct) => SetupRunner.RunAsync(script, args, pool.Repo, onLine, TimeSpan.FromHours(12), ct), refreshAfter: true);
        return op;
    }

    public string MigrateCommandLine(string from, string name, bool removeOld) =>
        _pool == null ? "" : SetupRunner.CommandLine(MigrateScript, SetupRunner.MigrateArgs(from, name, _pool.Root, removeOld));

    /// <summary>
    /// After a create or a move was stopped: removes the clone it was making, unless the clone had got far enough to
    /// keep (<paramref name="keepKey"/> is set in its config: cow.startCommit once a create has finished,
    /// cow.migrateFrom once a move has begun copying your files). Only ever called for a folder that did not exist
    /// when the run started, so nothing that was there before can be touched.
    /// </summary>
    private async Task<string> CleanUpUnfinishedCloneAsync(Pool pool, string path, string keepKey)
    {
        try
        {
            var entries = await Task.Run(() => _git.ListWorktreesAsync(pool.Repo));
            bool registered = entries.Any(e => e.Path.Equals(GitService.NormalizePath(path), StringComparison.OrdinalIgnoreCase));
            if (!registered) return Directory.Exists(path) ? $"Stopped. A folder was left at {path}; Git does not know it, so it can simply be deleted." : "Stopped before anything was made.";
            if (Directory.Exists(path) && (await Task.Run(() => _git.WorktreeConfigAsync(path, keepKey))).Length > 0)
                return keepKey == "cow.migrateFrom"
                    ? "Stopped. Run it again to pick up where it stopped; nothing was removed."
                    : $"Stopped, but {path} had already been finished, so it is kept.";
            var r = await Task.Run(() => _git.ForceRemoveWorktreeAsync(pool.Repo, path));
            return r.Ok
                ? "Stopped. The partly made worktree was removed again; nothing else changed."
                : $"Stopped, but the partly made folder could not be removed ({r.Problem}). Remove it with:  git -C \"{pool.Repo}\" worktree remove --force \"{path}\"";
        }
        catch (Exception ex)
        {
            return $"Stopped. What was left at {path} could not be checked: {ex.Message}";
        }
    }

    /// <summary>After setup was stopped while it made the base copy: removes that copy again (it did not exist before the run).</summary>
    private async Task<string> CleanUpStoppedSetupAsync(string repo, string root, string basePath, bool rootExisted)
    {
        try
        {
            if (File.Exists(System.IO.Path.Combine(root, "cow-config.json"))) return "Stopped. The repository had been set up already; nothing was removed.";
            var entries = await Task.Run(() => _git.ListWorktreesAsync(repo));
            if (entries.Any(e => e.Path.Equals(GitService.NormalizePath(basePath), StringComparison.OrdinalIgnoreCase)))
            {
                var r = await Task.Run(() => _git.ForceRemoveWorktreeAsync(repo, basePath));
                if (!r.Ok) return $"Stopped, but the partly made base copy could not be removed ({r.Problem}). Remove it with:  git -C \"{repo}\" worktree remove --force \"{basePath}\"";
            }
            foreach (var probe in new[] { ".clone-probe-a", ".clone-probe-b" })
            {
                try { File.Delete(System.IO.Path.Combine(root, probe)); } catch { }
            }
            try
            {
                if (!rootExisted && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            }
            catch { }
            return "Stopped. The partly made base copy was removed again; nothing else changed.";
        }
        catch (Exception ex)
        {
            return $"Stopped. What was left at {basePath} could not be checked: {ex.Message}";
        }
    }

    private OperationViewModel StartScript(OperationKind kind, string title, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var op = new OperationViewModel(kind, title, _cow!.CommandLine(args)) { PoolRoot = _pool?.Root };
        _ = RunScriptAsync(op, args, timeout, refreshAfter: true);
        return op;
    }

    /// <summary>Runs cow_worktree.ps1 for the selected repository.</summary>
    private Task<OperationViewModel> RunScriptAsync(OperationViewModel op, IReadOnlyList<string> args, TimeSpan timeout, bool refreshAfter, Func<Task<string>>? afterCancel = null)
    {
        var cow = _cow!;
        return RunOperationAsync(op, (onLine, ct) => cow.RunAsync(args, onLine, null, timeout, ct), refreshAfter, afterCancel);
    }

    /// <summary>
    /// Runs one script as an operation: its lines stream into the Activity list, it can be stopped where the
    /// operation allows it, and the worktree list is read again afterwards.
    /// </summary>
    private async Task<OperationViewModel> RunOperationAsync(OperationViewModel op, Func<Action<string, bool>, CancellationToken, Task<ProcessResult>> run,
        bool refreshAfter, Func<Task<string>>? afterCancel = null, Func<Task>? afterSuccess = null)
    {
        Operations.Insert(0, op);
        RaiseRunning();
        OperationStarted?.Invoke(op);
        var dq = _dispatcher;
        try
        {
            var r = await Task.Run(() => run((line, isErr) => dq.TryEnqueue(() => op.AddLine(line, isErr)), op.CancellationToken));
            op.Complete(r);
        }
        catch (OperationCanceledException)
        {
            string note;
            try { note = afterCancel != null ? await afterCancel() : "Stopped."; }
            catch (Exception ex) { note = "Stopped. " + ex.Message; }
            op.CompleteCancelled(note);
        }
        catch (Exception ex)
        {
            op.Fail(ex.Message);
        }
        AppLog.Write($"operation '{op.Title}' {op.StatusText} in {op.ElapsedText}: {op.CommandLine}");
        RaiseRunning();
        if (op.Succeeded && afterSuccess != null)
        {
            try { await afterSuccess(); }
            catch (Exception ex) { AppLog.Write($"after '{op.Title}': {ex.Message}"); }
        }
        if (refreshAfter)
        {
            await RefreshAsync();
            OperationCompleted?.Invoke(op);
        }
        op.MarkSettled();
        return op;
    }

    private void RaiseRunning() => Raise(nameof(RunningCount), nameof(RunningText), nameof(RunningVisibility), nameof(RefreshBaseRunning), nameof(PruneRunning),
        nameof(SetupRunning), nameof(MigrateRunning), nameof(DriftVisibility));

    public WorktreeViewModel? FindRow(string pathOrName) =>
        _rows.TryGetValue(GitService.NormalizePath(pathOrName), out var r) ? r
        : _rows.Values.FirstOrDefault(x => x.Name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>What every row shows, for --dump.</summary>
    public object Dump() => new
    {
        poolsFile = CowHome.PoolsFile,
        registryError = _registryError,
        script = _registry.ScriptPath,
        toolFolder = ToolDir,
        integrations = Integrations == null ? null : new { claude = Integrations.Claude.State.ToString(), codex = Integrations.Codex.State.ToString(), schedule = Integrations.Schedule.State.ToString(), Integrations.ScheduleAt, Integrations.Drift },
        pools = Pools.Select(p => new { p.Name, p.Root, p.Repo, p.ConfigError, p.Id, p.StateMutexName, p.BuildMutexName, Task = p.TaskFullPath }),
        pool = _pool?.Name,
        refreshedAt = LastRefresh,
        seconds = LastDuration.TotalSeconds,
        rowsSeconds = LastRowsDuration.TotalSeconds,
        scriptSkipped = LastScriptSkipped,
        problem = Problem,
        health = HealthItems.Select(h => new { h.Title, h.Value, h.Detail, Severity = h.Severity.ToString() }),
        diskSpace = Disk.Dump(),
        worktrees = _rows.Values.OrderBy(r => r.Path).Select(r => new
        {
            r.Name,
            r.Path,
            Kind = r.Kind.ToString(),
            r.Branch,
            r.ShortSha,
            r.Subject,
            r.Changed,
            r.Phantom,
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
            r.OnPoolDrive,
            DiskOwnBytes = r.Disk?.OwnBytes,
            DiskSharedBytes = r.Disk?.SharedBytes,
            DiskDetail = r.DiskDetailVisibility == Visibility.Visible ? r.DetailDisk : null,
            DiskNote = r.DiskDetailVisibility == Visibility.Visible ? r.DetailDiskNote : null,
        }),
    };
}
