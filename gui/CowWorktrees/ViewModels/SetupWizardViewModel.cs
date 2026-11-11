using System.Collections.ObjectModel;
using System.Text.Json;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

/// <summary>The screens of the walkthrough, in order. The values are the indexes of the step rail.</summary>
public enum WizardStep { Welcome = 0, Checks = 1, Project = 2, Location = 3, Tools = 4, Builds = 5, Review = 6, Done = 7 }

/// <summary>One line of the step rail at the left of the walkthrough.</summary>
public sealed class RailItem : ObservableObject
{
    private int _current;

    public RailItem(int index, string label)
    {
        Index = index;
        Label = label;
    }

    public int Index { get; }
    public string Label { get; }
    public string Number => (Index + 1).ToString();

    public void SetCurrent(int current)
    {
        _current = current;
        Raise(nameof(IsDone), nameof(IsCurrent), nameof(DoneVisibility), nameof(NumberVisibility), nameof(CircleBrush), nameof(NumberBrush), nameof(LabelBrush), nameof(LabelWeight));
    }

    public bool IsDone => Index < _current;
    public bool IsCurrent => Index == _current;
    public Visibility DoneVisibility => Theme.Vis(IsDone);
    public Visibility NumberVisibility => Theme.Vis(!IsDone);
    public Brush CircleBrush => IsDone ? Theme.Success : IsCurrent ? Theme.Accent : Theme.SubtleFill;
    public Brush NumberBrush => IsCurrent ? Theme.OnAccent : Theme.Secondary;
    public Brush LabelBrush => IsCurrent ? Theme.Primary : Theme.Secondary;
    public Windows.UI.Text.FontWeight LabelWeight => IsCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
}

/// <summary>One row of "Check this PC".</summary>
public sealed class CheckRowViewModel
{
    public CheckRowViewModel(CheckResult r, string? fixLabel, string? fixId, string? secondLabel = null, string? secondId = null, string? steps = null)
    {
        Id = r.Id;
        Title = r.Title;
        Summary = r.Summary;
        Detail = r.Detail;
        State = r.State;
        FixLabel = fixLabel ?? "";
        FixId = fixId ?? "";
        SecondLabel = secondLabel ?? "";
        SecondId = secondId ?? "";
        Steps = steps ?? "";
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Detail { get; }
    public CheckState State { get; }
    public string FixLabel { get; }
    public string FixId { get; }
    public string SecondLabel { get; }
    public string SecondId { get; }
    /// <summary>Numbered steps shown under a row that needs the person to do something in Windows.</summary>
    public string Steps { get; }

    public string Glyph => State switch { CheckState.Ok => "", CheckState.Unknown => "", _ => "" };   // check, info, warning
    public Brush Brush => State switch { CheckState.Ok => Theme.Success, CheckState.Unknown => Theme.Neutral, CheckState.TooOld when Id == "windows" => Theme.Caution, _ => Theme.Critical };
    public Visibility FixVisibility => Theme.Vis(FixLabel.Length > 0);
    public Visibility SecondVisibility => Theme.Vis(SecondLabel.Length > 0);
    public Visibility StepsVisibility => Theme.Vis(Steps.Length > 0);
    public string AutomationName => $"{Title}: {Summary}";
}

/// <summary>
/// The get-started walkthrough: one question per screen, and at the end one run of setup.ps1. Nothing on this PC
/// changes before "Set up" is pressed; setup.ps1 stays the only place where setup is implemented.
/// </summary>
public sealed class SetupWizardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _step;
    private bool _touched;

    public SetupWizardViewModel(MainViewModel main)
    {
        _main = main;
        string[] labels = { "Welcome", "Check this PC", "Choose your project", "Where worktrees live", "Connect your AI tools", "Faster first builds", "Review and set up", "All set" };
        for (int i = 0; i < labels.Length; i++) Rail.Add(new RailItem(i, labels[i]));
        foreach (var r in Rail) r.SetCurrent(0);
    }

    public ObservableCollection<RailItem> Rail { get; } = new();

    /// <summary>Raised when the walkthrough should close (finished, or cancelled).</summary>
    public event Action? Closed;
    /// <summary>Raised when "Create your first worktree" is pressed on the last screen.</summary>
    public event Action? CreateFirstWorktree;

    // ---- navigation --------------------------------------------------------------------------------------------
    public WizardStep Step => (WizardStep)_step;

    public void GoTo(WizardStep step)
    {
        _step = (int)step;
        foreach (var r in Rail) r.SetCurrent(_step);
        RaiseNavigation();
        if (step == WizardStep.Checks) _ = RunChecksAsync();
        if (step == WizardStep.Location) EnsureRootSuggestion();
        if (step == WizardStep.Tools) ApplyToolDefaults();
        if (step == WizardStep.Builds) _ = LookForSolutionAsync();
        if (step == WizardStep.Review) BuildReview();
    }

    private void RaiseNavigation() => Raise(nameof(Step), nameof(WelcomeVisibility), nameof(ChecksVisibility), nameof(ProjectVisibility), nameof(LocationVisibility),
        nameof(ToolsVisibility), nameof(BuildsVisibility), nameof(ReviewVisibility), nameof(DoneVisibility), nameof(CanGoNext), nameof(CanGoBack), nameof(NextLabel),
        nameof(NextBlockedReason), nameof(NextBlockedVisibility), nameof(BackVisibility), nameof(CancelVisibility), nameof(CancelLabel), nameof(NextVisibility),
        nameof(ReviewSummaryVisibility), nameof(RunningVisibility), nameof(FailedVisibility), nameof(StopVisibility),
        nameof(DoneText), nameof(ClaudeDoneVisibility), nameof(CodexDoneVisibility));

    public Visibility WelcomeVisibility => Theme.Vis(Step == WizardStep.Welcome);
    public Visibility ChecksVisibility => Theme.Vis(Step == WizardStep.Checks);
    public Visibility ProjectVisibility => Theme.Vis(Step == WizardStep.Project);
    public Visibility LocationVisibility => Theme.Vis(Step == WizardStep.Location);
    public Visibility ToolsVisibility => Theme.Vis(Step == WizardStep.Tools);
    public Visibility BuildsVisibility => Theme.Vis(Step == WizardStep.Builds);
    public Visibility ReviewVisibility => Theme.Vis(Step == WizardStep.Review);
    public Visibility DoneVisibility => Theme.Vis(Step == WizardStep.Done);

    public bool IsRunning => Operation?.IsRunning == true;
    public bool HasFailed => Operation != null && !Operation.IsRunning && !Operation.Succeeded;

    /// <summary>Why Next is disabled on this screen, or "" when it is not.</summary>
    public string NextBlockedReason => Step switch
    {
        WizardStep.Checks => IsChecking ? "Checking this PC…" : Report?.CanSetUp == true ? "" : "Fix the items marked in red first. This screen checks again when you come back to it.",
        WizardStep.Project => RepoOk ? "" : RepoChecking ? "Looking at the folder…" : RepoPath.Trim().Length == 0 ? "Choose your project's folder to continue." : "Choose a folder that can be used to continue.",
        WizardStep.Location => RootOk ? "" : "Choose a folder on a Dev Drive to continue.",
        WizardStep.Builds => !BuildOn || BuildCommand.Trim().Length > 0 ? "" : "Enter the build command, or switch this off.",
        WizardStep.Review => IsRunning ? "" : SetupBlockedReason,
        _ => "",
    };

    public Visibility NextBlockedVisibility => Theme.Vis(NextBlockedReason.Length > 0 && !IsRunning);
    public bool CanGoNext => !IsRunning && NextBlockedReason.Length == 0;
    public bool CanGoBack => _step > 0 && Step != WizardStep.Done && !IsRunning;
    public string NextLabel => Step switch
    {
        WizardStep.Welcome => "Get started",
        WizardStep.Review => HasFailed ? "Try again" : "Set up",
        WizardStep.Done => "Finish",
        _ => "Next",
    };
    public Visibility NextVisibility => Theme.Vis(!IsRunning);
    public Visibility BackVisibility => Theme.Vis(CanGoBack);
    public Visibility CancelVisibility => Theme.Vis(Step != WizardStep.Done && !IsRunning);
    public string CancelLabel => _main.Pools.Count == 0 ? "Not now" : "Cancel";
    /// <summary>Whether closing the walkthrough should ask first: something was entered and nothing is set up yet.</summary>
    public bool ShouldConfirmCancel => _touched && Step != WizardStep.Done && Step != WizardStep.Welcome;

    public void Next()
    {
        if (!CanGoNext) return;
        switch (Step)
        {
            case WizardStep.Review:
                StartSetup();
                break;
            case WizardStep.Done:
                Closed?.Invoke();
                break;
            default:
                GoTo((WizardStep)(_step + 1));
                break;
        }
    }

    public void Back()
    {
        if (!CanGoBack) return;
        if (Step == WizardStep.Review) Operation = null;
        GoTo((WizardStep)(_step - 1));
    }

    public void Cancel() => Closed?.Invoke();

    // ---- 2. check this PC ----------------------------------------------------------------------------------------
    public ObservableCollection<CheckRowViewModel> Checks { get; } = new();
    public SystemReport? Report { get; private set; }
    public bool IsChecking { get; private set; }
    public string ChecksHeadline => IsChecking && Report == null ? "Checking…" : Report == null ? "" : Report.CanSetUp
        ? (Report["windows"]?.IsOk == true ? "This PC is ready." : "This PC can be used, with one thing to know.")
        : "A few things are needed first.";
    public string AgentsFoundText => Report == null ? "" :
        (Report.ClaudeFound, Report.CodexFound) switch
        {
            (true, true) => "Claude Code and Codex were both found on this PC.",
            (true, false) => "Claude Code was found on this PC. Codex was not; you can still connect it for later.",
            (false, true) => "Codex was found on this PC. Claude Code was not; you can still connect it for later.",
            _ => "Neither Claude Code nor Codex was found. You can still use the app by itself, and connect them later.",
        };
    public bool ExperimentalDevDrive => _main.Settings.ExperimentalDevDrive;

    public async Task RunChecksAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        Raise(nameof(IsChecking), nameof(ChecksHeadline), nameof(NextBlockedReason), nameof(NextBlockedVisibility), nameof(CanGoNext));
        try
        {
            var report = await Task.Run(SystemChecks.RunAsync);
            Report = report;
            Checks.Clear();
            foreach (var c in report.Checks) Checks.Add(RowFor(c));
        }
        catch (Exception ex)
        {
            AppLog.Write($"checks: {ex}");
        }
        IsChecking = false;
        Raise(nameof(IsChecking), nameof(Report), nameof(ChecksHeadline), nameof(AgentsFoundText), nameof(NextBlockedReason), nameof(NextBlockedVisibility), nameof(CanGoNext));
    }

    private CheckRowViewModel RowFor(CheckResult c) => (c.Id, c.State) switch
    {
        ("windows", CheckState.TooOld) => new(c, "Open Windows Update", "windows-update"),
        ("git", CheckState.Missing) => new(c, "Install Git", "install-git", "Download page", "git-download"),
        ("git", CheckState.TooOld) => new(c, "Update Git", "install-git", "Download page", "git-download"),
        ("devdrive", CheckState.Missing) => new(c, "Open disk settings", "disk-settings",
            ExperimentalDevDrive ? "Create one for me" : null, ExperimentalDevDrive ? "create-dev-drive" : null,
            "1.  In the Settings page that opens, choose Create Dev Drive.\n2.  Choose Create new VHD, give it a name and a size (50 GB or more; it only uses what is stored on it).\n3.  Choose Dynamic, then Create, and pick a drive letter when Windows asks.\n4.  Come back here: this screen checks again by itself."),
        _ => new(c, null, null),
    };

    // ---- 3. choose your project ----------------------------------------------------------------------------------
    private string _repoPath = "";
    private int _repoCheck;
    public string RepoPath
    {
        get => _repoPath;
        set
        {
            if (!SetProperty(ref _repoPath, value ?? "")) return;
            _touched = true;
            _ = InspectRepoAsync();
        }
    }

    public RepoInfo? Repo { get; private set; }
    public bool RepoChecking { get; private set; }
    public bool RepoOk { get; private set; }
    public string RepoStatus { get; private set; } = "";
    public Brush RepoStatusBrush => RepoOk ? Theme.Success : Theme.Critical;
    public string RepoStatusGlyph => RepoOk ? "" : "";
    public Visibility RepoStatusVisibility => Theme.Vis(RepoStatus.Length > 0 && !RepoChecking);
    /// <summary>The project's main folder, offered when an extra worktree of it was chosen.</summary>
    public string? RepoSuggestion { get; private set; }
    public Visibility RepoSuggestionVisibility => Theme.Vis(RepoSuggestion != null);
    public string RepoSuggestionLabel => RepoSuggestion == null ? "" : $"Use {RepoSuggestion}";
    /// <summary>The name the repository gets in this app and in its folder on the Dev Drive.</summary>
    public string RepoName { get; private set; } = "";

    private string _baseRef = "";
    public string BaseRef { get => _baseRef; set { if (SetProperty(ref _baseRef, value ?? "")) _touched = true; } }
    public ObservableCollection<string> BaseRefChoices { get; } = new();

    public void UseSuggestedRepo()
    {
        if (RepoSuggestion != null) RepoPath = RepoSuggestion;
    }

    public async Task InspectRepoAsync()
    {
        int mine = ++_repoCheck;
        string path = RepoPath.Trim().Trim('"');
        RepoOk = false;
        RepoSuggestion = null;
        Repo = null;
        if (path.Length == 0)
        {
            RepoStatus = "";
            RepoChecking = false;
            RaiseRepo();
            return;
        }
        RepoChecking = true;
        RaiseRepo();
        RepoInfo info;
        try
        {
            info = await Task.Run(() => _main.Git.InspectAsync(path));
        }
        catch (Exception ex)
        {
            info = new RepoInfo { Problem = SystemChecksGitMissing(ex) };
        }
        if (mine != _repoCheck) return;   // the path changed while Git ran
        RepoChecking = false;
        Repo = info;

        string? problem = info.Problem;
        string full = Pool.FullPath(path);
        if (problem == null && info.MainCheckout is { } main)
        {
            var registered = _main.Pools.FirstOrDefault(p => p.IsUsable && p.Repo.Equals(main, StringComparison.OrdinalIgnoreCase));
            var insideRoot = _main.Pools.FirstOrDefault(p => (full + "\\").StartsWith(p.Root + "\\", StringComparison.OrdinalIgnoreCase));
            if (insideRoot != null)
                problem = $"This folder is one of the worktrees of \"{insideRoot.Name}\", which is already set up. Choose the project's own folder instead.";
            else if (registered != null)
                problem = $"This project is already set up as \"{registered.Name}\". Close this walkthrough and pick it at the top of the window.";
            else if (_main.ToolDir is { } tool && (full + "\\").StartsWith(tool + "\\", StringComparison.OrdinalIgnoreCase))
                problem = "This is the folder of cow-worktrees itself. Choose the project you work on.";
            else if (!info.IsMainCheckout)
            {
                problem = $"This is an extra worktree of the project at {main}. Setup starts from the project's main folder.";
                RepoSuggestion = main;
            }
        }
        if (problem != null)
        {
            RepoStatus = problem;
        }
        else
        {
            RepoOk = true;
            RepoName = UniqueName(info.Name ?? "project");
            RepoStatus = $"Git repository \"{info.Name}\"" + (info.Branch != null ? $", on the branch {info.Branch}." : ", with no branch checked out right now.");
            if (string.IsNullOrWhiteSpace(BaseRef) || !_baseRefEdited) _baseRef = info.SuggestedBaseRef ?? "";
            try
            {
                var (local, remote) = await Task.Run(() => _main.Git.ListRefsAsync(info.MainCheckout!));
                if (mine == _repoCheck)
                {
                    BaseRefChoices.Clear();
                    foreach (var r in remote.Concat(local).Take(200)) BaseRefChoices.Add(r);
                }
            }
            catch { }
            _rootEdited = false;
        }
        RaiseRepo();
    }

    private bool _baseRefEdited;
    public void BaseRefEditedByUser() => _baseRefEdited = true;

    private static string SystemChecksGitMissing(Exception ex) =>
        ex is System.ComponentModel.Win32Exception ? "Git could not be started. Go back to Check this PC." : ex.Message;

    /// <summary>setup.ps1 replaces a registered repository that has the same name, so a second "MyGame" becomes "MyGame-2".</summary>
    private string UniqueName(string name)
    {
        var clean = CowScript.SanitizeName(name);
        if (clean.Length == 0) clean = "project";
        string candidate = clean;
        for (int i = 2; _main.Pools.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) || p.RegisteredName.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{clean}-{i}";
        return candidate;
    }

    private void RaiseRepo() => Raise(nameof(Repo), nameof(RepoChecking), nameof(RepoOk), nameof(RepoStatus), nameof(RepoStatusBrush), nameof(RepoStatusGlyph), nameof(RepoStatusVisibility),
        nameof(RepoSuggestion), nameof(RepoSuggestionVisibility), nameof(RepoSuggestionLabel), nameof(RepoName), nameof(BaseRef), nameof(BaseRefVisibility),
        nameof(NextBlockedReason), nameof(NextBlockedVisibility), nameof(CanGoNext));

    public Visibility BaseRefVisibility => Theme.Vis(RepoOk);

    // ---- 4. where worktrees live -----------------------------------------------------------------------------------
    public ObservableCollection<DriveChoice> Drives { get; } = new();
    private DriveChoice? _drive;
    private string _rootPath = "";
    private bool _rootEdited;

    public DriveChoice? SelectedDrive
    {
        get => _drive;
        set
        {
            if (value == null || !SetProperty(ref _drive, value)) return;
            if (!_rootEdited || !RootPath.StartsWith(value.Root, StringComparison.OrdinalIgnoreCase))
            {
                _rootPath = SuggestedRoot(value);
                _rootEdited = false;
            }
            ValidateRoot();
        }
    }

    public string RootPath
    {
        get => _rootPath;
        set
        {
            if (!SetProperty(ref _rootPath, value ?? "")) return;
            _touched = true;
            ValidateRoot();
        }
    }

    public void RootEditedByUser() => _rootEdited = true;

    public bool RootOk { get; private set; }
    public string RootStatus { get; private set; } = "";
    public Brush RootStatusBrush => RootOk ? Theme.Success : Theme.Critical;
    public string RootStatusGlyph => RootOk ? "" : "";
    public Visibility RootStatusVisibility => Theme.Vis(RootStatus.Length > 0);
    public string BaseFolderText => RootOk ? $"{Path.Combine(Pool.FullPath(RootPath), "base")} will hold one full copy of your project (the base). Every worktree is a copy of it that takes no extra space until you change files." : "";

    private string SuggestedRoot(DriveChoice d) => Path.Combine(d.Root, "cow", RepoName.Length > 0 ? RepoName : "project");

    private void EnsureRootSuggestion()
    {
        var drives = Report?.RefsDrives ?? new List<DriveChoice>();
        if (!drives.Select(d => d.Root).SequenceEqual(Drives.Select(d => d.Root)))
        {
            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
        }
        if (_drive == null || !Drives.Contains(_drive)) _drive = Drives.OrderByDescending(d => d.FreeGB).FirstOrDefault();
        if (_drive != null && (!_rootEdited || _rootPath.Length == 0)) _rootPath = SuggestedRoot(_drive);
        Raise(nameof(SelectedDrive));
        ValidateRoot();
    }

    private void ValidateRoot()
    {
        RootOk = false;
        string raw = RootPath.Trim().Trim('"');
        if (raw.Length == 0) RootStatus = "";
        else
        {
            string full;
            try { full = Pool.FullPath(raw); } catch { full = raw; }
            string? driveRoot = Path.GetPathRoot(full);
            var known = Drives.FirstOrDefault(d => d.Root.Equals(driveRoot, StringComparison.OrdinalIgnoreCase));
            string? main = Repo?.MainCheckout;
            if (!Path.IsPathRooted(raw) || string.IsNullOrEmpty(driveRoot) || driveRoot.Length < 3)
                RootStatus = "Enter a full path, such as E:\\cow\\MyProject.";
            else if (known == null)
                RootStatus = $"{driveRoot.TrimEnd('\\')} is not a Dev Drive. On a normal drive every worktree would take the full size of your project. Choose a folder on one of the drives above.";
            else if (full.Equals(driveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || full.Length <= 3)
                RootStatus = "Choose a folder on the drive, not the drive itself.";
            else if (main != null && ((full + "\\").StartsWith(main + "\\", StringComparison.OrdinalIgnoreCase) || full.Equals(main, StringComparison.OrdinalIgnoreCase)))
                RootStatus = "This is inside your project. Worktrees have to live outside the project's own folder.";
            else if (_main.Pools.Any(p => p.Root.Equals(full, StringComparison.OrdinalIgnoreCase)))
                RootStatus = "Another repository already uses this folder.";
            else if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, "cow-config.json")))
                RootStatus = "This folder already has files in it. Choose a new or empty folder.";
            else
            {
                RootOk = true;
                RootStatus = Directory.Exists(full) ? "This folder can be used." : "This folder will be created.";
            }
        }
        Raise(nameof(RootPath), nameof(RootOk), nameof(RootStatus), nameof(RootStatusBrush), nameof(RootStatusGlyph), nameof(RootStatusVisibility), nameof(BaseFolderText),
            nameof(NextBlockedReason), nameof(NextBlockedVisibility), nameof(CanGoNext));
    }

    // ---- 5. connect your AI tools ----------------------------------------------------------------------------------
    private bool _claudeOn = true, _codexOn = true, _scheduleOn = true, _toolDefaultsApplied;
    private TimeSpan _scheduleTime = new(5, 0, 0);

    public bool ClaudeOn { get => _claudeOn; set { if (SetProperty(ref _claudeOn, value)) _touched = true; } }
    public bool CodexOn { get => _codexOn; set { if (SetProperty(ref _codexOn, value)) _touched = true; } }
    public bool ScheduleOn { get => _scheduleOn; set { if (SetProperty(ref _scheduleOn, value)) { _touched = true; OnPropertyChanged(nameof(ScheduleDetail)); } } }
    public TimeSpan ScheduleTime { get => _scheduleTime; set { if (SetProperty(ref _scheduleTime, value)) OnPropertyChanged(nameof(ScheduleDetail)); } }
    public string ScheduleAt => $"{_scheduleTime.Hours:00}:{_scheduleTime.Minutes:00}";

    public string ClaudeFoundText => Report?.ClaudeFound == true ? "Found on this PC" : "Not found on this PC";
    public string CodexFoundText => Report?.CodexFound == true ? "Found on this PC" : "Not found on this PC";
    public string ClaudeDetail =>
        $"Adds three hooks to {Path.Combine(Repo?.MainCheckout ?? "<your project>", ".claude", "settings.local.json")} (a file that stays out of Git). " +
        "When Claude Code asks for a worktree (claude --worktree, subagents, desktop sessions), it gets a copy-on-write clone in seconds instead of a full copy. A plain \"git worktree add\" typed by an agent is redirected the same way.";
    public string CodexDetail =>
        $"Adds a marked section to {SetupRunner.CodexAgentsFile}, the instructions Codex reads, telling it to make worktrees of this project as copy-on-write clones. Nothing else in that file is touched.";
    public string ScheduleDetail =>
        "A Windows scheduled task, once a day when the PC is idle: it removes worktrees that have not been used for a week and have nothing to lose (no changes, no commits that exist nowhere else), and brings the base copy up to date with your main branch.";

    /// <summary>The instructions for an agent other than Claude Code and Codex: the Codex template with this repository's paths.</summary>
    public string AgentInstructions => AgentInstructionsFor(_main.ToolDir, RepoName, Repo?.MainCheckout ?? "", RootOk ? Pool.FullPath(RootPath) : "");

    public static string AgentInstructionsFor(string? toolDir, string name, string repo, string root)
    {
        try
        {
            if (toolDir == null) return "";
            string template = File.ReadAllText(Path.Combine(toolDir, "templates", "codex-AGENTS.md"));
            return template.Replace("{name}", name).Replace("{repo}", repo).Replace("{root}", root)
                .Replace("{script}", Path.Combine(toolDir, "scripts", "cow_worktree.ps1"))
                .Replace("{migrate}", Path.Combine(toolDir, "scripts", "migrate_worktree.ps1")).Trim();
        }
        catch (Exception ex)
        {
            AppLog.Write($"agent instructions: {ex.Message}");
            return "";
        }
    }

    private void ApplyToolDefaults()
    {
        if (!_toolDefaultsApplied && Report != null)
        {
            _toolDefaultsApplied = true;
            // on when found; both stay available as switches either way
            _claudeOn = Report.ClaudeFound;
            _codexOn = Report.CodexFound;
        }
        Raise(nameof(ClaudeOn), nameof(CodexOn), nameof(ScheduleOn), nameof(ClaudeFoundText), nameof(CodexFoundText), nameof(ClaudeDetail), nameof(CodexDetail), nameof(ScheduleDetail));
    }

    // ---- 6. faster first builds ------------------------------------------------------------------------------------
    private bool _buildOn;
    private string _buildCommand = "", _buildConfigs = "Debug";
    private bool _buildSuggested;

    public bool BuildOn
    {
        get => _buildOn;
        set
        {
            if (!SetProperty(ref _buildOn, value)) return;
            _touched = true;
            Raise(nameof(BuildFieldsVisibility), nameof(NextBlockedReason), nameof(NextBlockedVisibility), nameof(CanGoNext));
        }
    }

    public string BuildCommand { get => _buildCommand; set { if (SetProperty(ref _buildCommand, value ?? "")) Raise(nameof(NextBlockedReason), nameof(NextBlockedVisibility), nameof(CanGoNext)); } }
    public string BuildConfigs { get => _buildConfigs; set => SetProperty(ref _buildConfigs, value ?? ""); }
    public Visibility BuildFieldsVisibility => Theme.Vis(BuildOn);
    public string SolutionText { get; private set; } = "";
    public Visibility SolutionTextVisibility => Theme.Vis(SolutionText.Length > 0);

    private async Task LookForSolutionAsync()
    {
        if (_buildSuggested || Repo?.MainCheckout is not { } main) return;
        _buildSuggested = true;
        try
        {
            var sln = await Task.Run(() => Directory.EnumerateFiles(main, "*.sln", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OrderBy(n => n).ToList());
            if (sln.Count > 0)
            {
                SolutionText = sln.Count == 1
                    ? $"Found {sln[0]} in your project: it looks like it builds with MSBuild, so a command is filled in for you."
                    : $"Found {sln.Count} solution files in your project; a command for {sln[0]} is filled in. Change it if another one is the right one.";
                if (_buildCommand.Length == 0) _buildCommand = $"msbuild {(sln[0]!.Contains(' ') ? "\"" + sln[0] + "\"" : sln[0])} /m /p:Configuration={{config}} /p:Platform=x64";
            }
            else SolutionText = "No Visual Studio solution (.sln) was found in your project's folder, so this is probably not for you. You can turn it on later in Repository settings.";
        }
        catch (Exception ex)
        {
            AppLog.Write($"solution search: {ex.Message}");
        }
        Raise(nameof(SolutionText), nameof(SolutionTextVisibility), nameof(BuildCommand));
    }

    // ---- 7. review, then set up --------------------------------------------------------------------------------------
    public ObservableCollection<string> ReviewLines { get; } = new();
    public string CommandLine { get; private set; } = "";
    public OperationViewModel? Operation { get; private set; }
    public FriendlyError? Error => HasFailed ? Operation?.Friendly : null;
    public string ErrorTitle => Operation is { Cancelled: true } ? "Setup was stopped" : Error?.Title ?? "";
    public string ErrorText => Operation is { Cancelled: true } ? Operation.Lines.LastOrDefault()?.Text ?? "" : Error?.Explanation ?? "";
    public string ErrorRaw => Operation is { Cancelled: true } ? "" : Error?.Raw ?? "";
    public Visibility ErrorRawVisibility => Theme.Vis(ErrorRaw.Length > 0);
    public string ErrorActionLabel => Error?.ActionLabel ?? "";
    public Visibility ErrorActionVisibility => Theme.Vis(Error is { Action: not FriendlyAction.None } && Operation is not { Cancelled: true });
    public Visibility ReviewSummaryVisibility => Theme.Vis(Step == WizardStep.Review && Operation == null);
    public Visibility RunningVisibility => Theme.Vis(Step == WizardStep.Review && Operation != null);
    public Visibility FailedVisibility => Theme.Vis(Step == WizardStep.Review && HasFailed);
    public Visibility StopVisibility => Theme.Vis(IsRunning && Operation?.CanCancel == true);
    public string RunningHeadline => Operation == null ? "" : Operation.IsRunning ? "Setting up…" : Operation.Succeeded ? "Done" : Operation.Cancelled ? "Stopped" : "Setup did not finish";
    public string RunningElapsed => Operation == null ? "" : Operation.ElapsedText;

    /// <summary>Why "Set up" cannot be pressed (the tool folder is missing or in a place that will not last), or "".</summary>
    public string SetupBlockedReason
    {
        get
        {
            if (!_main.SetupAvailable) return "The cow-worktrees scripts were not found next to this app, so there is nothing to run. Install cow-worktrees again, or start the app from its own folder.";
            if (ToolFolder.RiskyLocation(_main.ToolDir) is { } why)
                return $"cow-worktrees is running from {_main.ToolDir}, and {why}. Setup stores this folder's path in several places, so it has to stay where it is. Install cow-worktrees with the installer, or move the folder somewhere permanent (for example C:\\Tools\\cow-worktrees) and start the app from there.";
            return "";
        }
    }

    public SetupOptions Options => new()
    {
        Repo = Repo?.MainCheckout ?? RepoPath.Trim(),
        Root = Pool.FullPath(RootPath.Trim().Trim('"')),
        Name = RepoName,
        BaseRef = string.IsNullOrWhiteSpace(BaseRef) ? null : BaseRef.Trim(),
        Claude = ClaudeOn,
        Codex = CodexOn,
        Schedule = ScheduleOn,
        ScheduleAt = ScheduleOn ? ScheduleAt : null,
        BuildCommand = BuildOn && BuildCommand.Trim().Length > 0 ? BuildCommand.Trim() : null,
        BuildConfigs = BuildOn && BuildConfigs.Trim().Length > 0 ? BuildConfigs.Trim() : null,
    };

    private void BuildReview()
    {
        var o = Options;
        ReviewLines.Clear();
        ReviewLines.Add($"Your project:  {o.Repo}");
        ReviewLines.Add($"Worktrees will live in:  {o.Root}");
        ReviewLines.Add($"New worktrees start from:  {o.BaseRef ?? "the project's main branch"}");
        ReviewLines.Add($"Claude Code:  {(o.Claude ? "connected" : "not connected")}");
        ReviewLines.Add($"Codex:  {(o.Codex ? "connected" : "not connected")}");
        ReviewLines.Add($"Daily upkeep:  {(o.Schedule ? $"every day at {o.ScheduleAt}, when the PC is idle" : "off")}");
        ReviewLines.Add($"Faster first builds:  {(o.BuildCommand != null ? o.BuildCommand : "off")}");
        CommandLine = SetupRunner.CommandLine(_main.SetupScript.Length > 0 ? _main.SetupScript : "setup.ps1", SetupRunner.SetupArgs(o));
        Raise(nameof(CommandLine), nameof(SetupBlockedReason), nameof(NextBlockedReason), nameof(NextBlockedVisibility), nameof(CanGoNext));
    }

    public void StartSetup()
    {
        if (IsRunning || SetupBlockedReason.Length > 0) return;
        var op = _main.StartSetup(Options, $"Set up {RepoName}");
        Operation = op;
        op.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OperationViewModel.ElapsedText)) OnPropertyChanged(nameof(RunningElapsed));
            else if (e.PropertyName is nameof(OperationViewModel.CanCancel)) OnPropertyChanged(nameof(StopVisibility));
        };
        RaiseOperation();
        _ = WatchAsync(op);
    }

    private async Task WatchAsync(OperationViewModel op)
    {
        await op.Settled;
        if (!ReferenceEquals(op, Operation)) return;
        if (op.Succeeded) GoTo(WizardStep.Done);
        RaiseOperation();
    }

    public void Stop() => Operation?.RequestCancel();

    private void RaiseOperation()
    {
        Raise(nameof(Operation), nameof(IsRunning), nameof(HasFailed), nameof(Error), nameof(ErrorTitle), nameof(ErrorText), nameof(ErrorRaw), nameof(ErrorRawVisibility),
            nameof(ErrorActionLabel), nameof(ErrorActionVisibility), nameof(RunningHeadline), nameof(RunningElapsed));
        RaiseNavigation();
    }

    // ---- 8. all set -------------------------------------------------------------------------------------------------
    public string DoneText => $"\"{RepoName}\" is ready. New worktrees of it appear in {(RootPath.Length > 0 ? Pool.FullPath(RootPath) : "its folder")} in seconds.";
    public Visibility ClaudeDoneVisibility => Theme.Vis(ClaudeOn);
    public Visibility CodexDoneVisibility => Theme.Vis(CodexOn);
    public string ClaudeCommand => "claude --worktree";
    public string CodexTip => "In Codex, ask for a worktree as usual: the instructions that were added tell it to make a copy-on-write clone instead of a full copy.";

    public void RequestFirstWorktree()
    {
        Closed?.Invoke();
        CreateFirstWorktree?.Invoke();
    }

    // ---- prefilled answers, for tests and screenshots (--wizard-state) -----------------------------------------------
    public async Task ApplyStateAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        await RunChecksAsync();
        if (Json.String(r, "repo") is { } repo)
        {
            _repoPath = repo;
            await InspectRepoAsync();
        }
        if (Json.String(r, "baseRef") is { } baseRef) _baseRef = baseRef;
        EnsureRootSuggestion();
        if (Json.String(r, "root") is { } root)
        {
            _rootPath = root;
            _rootEdited = true;
            ValidateRoot();
        }
        _toolDefaultsApplied = true;
        if (Json.Get(r, "claude") is { } c && c.ValueKind is JsonValueKind.True or JsonValueKind.False) _claudeOn = c.GetBoolean();
        if (Json.Get(r, "codex") is { } x && x.ValueKind is JsonValueKind.True or JsonValueKind.False) _codexOn = x.GetBoolean();
        if (Json.Get(r, "schedule") is { } s && s.ValueKind is JsonValueKind.True or JsonValueKind.False) _scheduleOn = s.GetBoolean();
        if (Json.String(r, "buildCommand") is { Length: > 0 } build)
        {
            _buildOn = true;
            _buildCommand = build;
            _buildSuggested = true;
        }
        Raise(nameof(RepoPath), nameof(BaseRef), nameof(RootPath), nameof(ClaudeOn), nameof(CodexOn), nameof(ScheduleOn), nameof(BuildOn), nameof(BuildCommand), nameof(BuildFieldsVisibility));
    }
}
