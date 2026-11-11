using System.Collections.ObjectModel;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

/// <summary>One connection on the Repository settings page: what is there now, and a switch for what should be.</summary>
public sealed class IntegrationRowViewModel : ObservableObject
{
    private bool _isOn;
    private IntegrationItem _item;

    public IntegrationRowViewModel(string title, string blurb, IntegrationItem item, bool isOn, Action changed)
    {
        Title = title;
        Blurb = blurb;
        _item = item;
        _isOn = isOn;
        Changed = changed;
    }

    private Action Changed { get; }
    public string Title { get; }
    public string Blurb { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value)) return;
            Changed();
        }
    }

    public void Update(IntegrationItem item, bool isOn)
    {
        _item = item;
        _isOn = isOn;
        Raise(nameof(IsOn), nameof(StateText), nameof(StateBrush), nameof(Detail), nameof(RepairVisibility));
    }

    public string StateText => _item.Summary;
    public Brush StateBrush => _item.State switch
    {
        IntegrationState.On => Theme.Success,
        IntegrationState.Partial or IntegrationState.Elsewhere => Theme.Caution,
        IntegrationState.Off => Theme.Neutral,
        _ => Theme.Neutral,
    };
    public string Detail => _item.Detail;
    /// <summary>Half there, or pointing at another copy of the tool: one click puts it right.</summary>
    public Visibility RepairVisibility => Theme.Vis(_item.State is IntegrationState.Partial or IntegrationState.Elsewhere);
}

/// <summary>
/// Repository settings: what a repository is connected to and the options setup.ps1 takes, read from
/// cow-config.json and from what is on disk. Apply runs setup.ps1 again with every value on the page, so nothing
/// that was set before is lost; the scripts stay the only place where setup is implemented.
/// </summary>
public sealed class RepositorySettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _baseRef = "", _branchPrefix = "", _buildCommand = "", _buildConfigs = "", _buildPrepare = "";
    private double _pruneDays;
    private bool _buildOn;
    private TimeSpan _scheduleTime = new(5, 0, 0);
    private string _snapshot = "";
    private OperationViewModel? _operation;

    public RepositorySettingsViewModel(MainViewModel main, Pool pool)
    {
        _main = main;
        Pool = pool;
        var none = new IntegrationItem("", IntegrationState.Unknown, "Checking…", "");
        Claude = new IntegrationRowViewModel("Claude Code", "claude --worktree, subagents and desktop sessions get a copy-on-write clone.", none, false, OnEdited);
        Codex = new IntegrationRowViewModel("Codex", "Codex is told to make worktrees of this project as clones.", none, false, OnEdited);
        Schedule = new IntegrationRowViewModel("Daily upkeep", "Once a day, when the PC is idle: tidy away idle worktrees and bring the base copy up to date.", none, false, OnEdited);
        Load();
    }

    public Pool Pool { get; }
    public string Title => $"Repository settings: {Pool.Name}";
    public string Subtitle => $"{Pool.Repo}  ·  worktrees in {Pool.Root}";

    public IntegrationRowViewModel Claude { get; }
    public IntegrationRowViewModel Codex { get; }
    public IntegrationRowViewModel Schedule { get; }
    public ObservableCollection<string> BaseRefChoices { get; } = new();

    public string BaseRef { get => _baseRef; set { if (SetProperty(ref _baseRef, value ?? "")) OnEdited(); } }
    public string BranchPrefix { get => _branchPrefix; set { if (SetProperty(ref _branchPrefix, value ?? "")) OnEdited(); } }
    public double PruneDays { get => _pruneDays; set { if (SetProperty(ref _pruneDays, double.IsNaN(value) ? 0 : Math.Clamp(Math.Round(value), 0, 365))) OnEdited(); } }
    public TimeSpan ScheduleTime { get => _scheduleTime; set { if (SetProperty(ref _scheduleTime, value)) OnEdited(); } }
    public string ScheduleAt => $"{_scheduleTime.Hours:00}:{_scheduleTime.Minutes:00}";
    public string PruneNote => _pruneDays <= 0 ? "Idle worktrees are never removed by the daily upkeep." : $"A worktree is removed by the daily upkeep after {Fmt.Plural((int)_pruneDays, "day")} without use, and only when it has nothing to lose: no changed or untracked files, and no commits that exist nowhere else.";

    public bool BuildOn
    {
        get => _buildOn;
        set
        {
            if (!SetProperty(ref _buildOn, value)) return;
            Raise(nameof(BuildFieldsVisibility));
            OnEdited();
        }
    }
    public string BuildCommand { get => _buildCommand; set { if (SetProperty(ref _buildCommand, value ?? "")) OnEdited(); } }
    public string BuildConfigs { get => _buildConfigs; set { if (SetProperty(ref _buildConfigs, value ?? "")) OnEdited(); } }
    public string BuildPrepare { get => _buildPrepare; set { if (SetProperty(ref _buildPrepare, value ?? "")) OnEdited(); } }
    public Visibility BuildFieldsVisibility => Theme.Vis(BuildOn);
    /// <summary>Several prepare commands can only be edited in cow-config.json; the page keeps them as they are.</summary>
    public bool PrepareEditable { get; private set; } = true;
    public string PrepareNote => PrepareEditable
        ? "Optional: one command that runs in the base copy before the build (for example a project generator)."
        : $"This repository has {Fmt.Plural(Pool.Config?.Build?.Prepare.Count ?? 0, "prepare command")}. They are kept as they are; to change them, edit cow-config.json in {Pool.Root}.";

    /// <summary>Reads the page's values from cow-config.json and from what the last refresh found on disk.</summary>
    public void Load()
    {
        Pool.LoadConfig();
        var c = Pool.Config;
        var report = ReferenceEquals(_main.Pool, Pool) ? _main.Integrations : null;
        var want = c?.Integrations;
        var unknown = new IntegrationItem("", IntegrationState.Unknown, "Not checked yet", "");

        Claude.Update(report?.Claude ?? unknown, want?.Claude ?? report?.Claude.IsConnected ?? false);
        Codex.Update(report?.Codex ?? unknown, want?.Codex ?? report?.Codex.IsConnected ?? false);
        bool scheduleOn = want != null ? want.ScheduleAt != null : report?.Schedule.IsConnected ?? false;
        Schedule.Update(report?.Schedule ?? unknown, scheduleOn);
        string at = report?.ScheduleAt ?? want?.ScheduleAt ?? "05:00";
        if (TimeSpan.TryParse(at, out var t)) _scheduleTime = t;

        _baseRef = c?.BaseRef ?? "";
        _branchPrefix = c?.BranchPrefix ?? "worktree-";
        _pruneDays = c?.PruneDays ?? 7;
        var build = c?.Build;
        _buildOn = build?.Command is { Length: > 0 };
        _buildCommand = build?.Command ?? "";
        _buildConfigs = build == null ? "Debug" : string.Join(",", build.Configs);
        PrepareEditable = (build?.Prepare.Count ?? 0) <= 1;
        _buildPrepare = build?.Prepare.Count == 1 ? build.Prepare[0] : "";

        _snapshot = Fingerprint();
        Raise(nameof(BaseRef), nameof(BranchPrefix), nameof(PruneDays), nameof(PruneNote), nameof(ScheduleTime), nameof(BuildOn), nameof(BuildCommand), nameof(BuildConfigs),
            nameof(BuildPrepare), nameof(BuildFieldsVisibility), nameof(PrepareEditable), nameof(PrepareNote), nameof(Subtitle), nameof(Title));
        RaiseState();
        _ = LoadRefsAsync();
    }

    private async Task LoadRefsAsync()
    {
        try
        {
            var (local, remote) = await Task.Run(() => _main.Git.ListRefsAsync(Pool.Repo));
            BaseRefChoices.Clear();
            foreach (var r in remote.Concat(local).Take(300)) BaseRefChoices.Add(r);
        }
        catch { }
    }

    private string Fingerprint() => string.Join("\u001f", Claude.IsOn, Codex.IsOn, Schedule.IsOn, Schedule.IsOn ? ScheduleAt : "", BaseRef.Trim(), BranchPrefix.Trim(), (int)_pruneDays,
        BuildOn, BuildOn ? BuildCommand.Trim() : "", BuildOn ? BuildConfigs.Trim() : "", BuildOn ? BuildPrepare.Trim() : "");

    private void OnEdited()
    {
        Raise(nameof(PruneNote));
        RaiseState();
    }

    // ---- apply -----------------------------------------------------------------------------------------------
    public bool HasChanges => Fingerprint() != _snapshot;
    public bool IsBusy => _operation?.IsRunning == true || _main.SetupRunning;
    public string Problem =>
        !_main.SetupAvailable ? "The cow-worktrees scripts were not found next to this app, so settings cannot be changed from here."
        : ToolFolder.RiskyLocation(_main.ToolDir) is { } why ? $"cow-worktrees is running from {_main.ToolDir}, and {why}. Install it, or move it somewhere permanent, before changing settings."
        : BaseRef.Trim().Length == 0 ? "Enter the branch new worktrees start from."
        : BuildOn && BuildCommand.Trim().Length == 0 ? "Enter the build command, or switch the build off."
        : "";
    public Visibility ProblemVisibility => Theme.Vis(Problem.Length > 0);
    public bool CanApply => HasChanges && !IsBusy && Problem.Length == 0;
    public bool CanRepair => !IsBusy && _main.SetupAvailable && ToolFolder.RiskyLocation(_main.ToolDir) == null;
    public string CommandLine => SetupRunner.CommandLine(_main.SetupScript.Length > 0 ? _main.SetupScript : "setup.ps1", SetupRunner.SetupArgs(Options));

    /// <summary>Every value on the page, as setup.ps1 arguments.</summary>
    public SetupOptions Options
    {
        get
        {
            bool hadBuild = Pool.Config?.Build != null;
            return new SetupOptions
            {
                Repo = Pool.Repo,
                Root = Pool.Root,
                Name = Pool.Name,
                BaseRef = BaseRef.Trim(),
                BranchPrefix = BranchPrefix.Trim().Length > 0 ? BranchPrefix.Trim() : null,
                PruneDays = (int)_pruneDays,
                Claude = Claude.IsOn,
                Codex = Codex.IsOn,
                Schedule = Schedule.IsOn,
                ScheduleAt = Schedule.IsOn ? ScheduleAt : null,
                NoBuild = !BuildOn && hadBuild,
                BuildCommand = BuildOn ? BuildCommand.Trim() : null,
                BuildConfigs = BuildOn ? BuildConfigs.Trim() : null,
                BuildPrepare = BuildOn && PrepareEditable ? BuildPrepare.Trim() : null,
            };
        }
    }

    /// <summary>The saved choices as they are, for Repair and for "Fix it": nothing on the page is applied.</summary>
    public static SetupOptions SavedOptions(Pool pool, IntegrationReport? report)
    {
        var want = pool.Config?.Integrations;
        bool schedule = want != null ? want.ScheduleAt != null : report?.Schedule.IsConnected ?? false;
        return new SetupOptions
        {
            Repo = pool.Repo,
            Root = pool.Root,
            Name = pool.Name,
            Claude = want?.Claude ?? report?.Claude.IsConnected ?? false,
            Codex = want?.Codex ?? report?.Codex.IsConnected ?? false,
            Schedule = schedule,
            ScheduleAt = schedule ? report?.ScheduleAt ?? want?.ScheduleAt : null,
        };
    }

    public event Action<OperationViewModel>? Applied;

    public void Apply()
    {
        if (!CanApply) return;
        Run(_main.StartSetup(Options, $"Update {Pool.Name}"));
    }

    public void Repair()
    {
        if (!CanRepair) return;
        Run(_main.StartSetup(SavedOptions(Pool, _main.Integrations), $"Repair {Pool.Name}"));
    }

    public void Discard() => Load();

    private void Run(OperationViewModel op)
    {
        _operation = op;
        RaiseState();
        _ = WatchAsync(op);
    }

    private async Task WatchAsync(OperationViewModel op)
    {
        await op.Settled;
        Load();
        Applied?.Invoke(op);
    }

    public string ResultTitle => _operation == null || _operation.IsRunning ? "" : _operation.Succeeded ? "Saved" : _operation.Friendly?.Title ?? "It did not work";
    public string ResultText => _operation == null || _operation.IsRunning ? "" : _operation.Succeeded ? "The repository was updated." : $"{_operation.Friendly?.Explanation}\n{_operation.Friendly?.Raw}";
    public bool ResultOk => _operation?.Succeeded == true;
    public Visibility ResultVisibility => Theme.Vis(_operation is { IsRunning: false });
    public Visibility BusyVisibility => Theme.Vis(IsBusy);

    private void RaiseState() => Raise(nameof(HasChanges), nameof(IsBusy), nameof(Problem), nameof(ProblemVisibility), nameof(CanApply), nameof(CanRepair), nameof(CommandLine),
        nameof(ResultTitle), nameof(ResultText), nameof(ResultOk), nameof(ResultVisibility), nameof(BusyVisibility));

    /// <summary>Instructions to paste into the instruction file of an agent other than Claude Code and Codex.</summary>
    public string AgentInstructions => SetupWizardViewModel.AgentInstructionsFor(_main.ToolDir, Pool.Name, Pool.Repo, Pool.Root);
}
