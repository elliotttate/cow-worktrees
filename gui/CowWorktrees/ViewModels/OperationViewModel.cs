using System.Collections.ObjectModel;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

public enum OperationKind { Create, Remove, Prune, PrunePreview, RefreshBase, Setup, Uninstall, Migrate, Retire, CleanUp, SelfTest }

public sealed class OutputLine
{
    public OutputLine(string text, Brush brush)
    {
        Text = text;
        Brush = brush;
    }

    public string Text { get; }
    public Brush Brush { get; }
}

public enum StepState { Pending, Running, Done, Failed }

/// <summary>One "==> ..." step of setup.ps1 or uninstall.ps1, as a line of the checklist.</summary>
public sealed class StepItem : ObservableObject
{
    private StepState _state;

    public StepItem(string key, string label)
    {
        Key = key;
        Label = label;
    }

    /// <summary>The script's own step title.</summary>
    public string Key { get; }
    /// <summary>The same step in plain words.</summary>
    public string Label { get; }

    public StepState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            Raise(nameof(Glyph), nameof(Brush), nameof(RunningVisibility), nameof(GlyphVisibility), nameof(Opacity));
        }
    }

    public string Glyph => State switch { StepState.Done => "", StepState.Failed => "", _ => "" };   // check, error, circle
    public Brush Brush => State switch { StepState.Done => Theme.Success, StepState.Failed => Theme.Critical, _ => Theme.Secondary };
    public Visibility RunningVisibility => Theme.Vis(State == StepState.Running);
    public Visibility GlyphVisibility => Theme.Vis(State != StepState.Running);
    public double Opacity => State == StepState.Pending ? 0.55 : 1.0;

    /// <summary>The scripts' step titles in plain words; an unknown title is shown as it is.</summary>
    public static string Friendly(string key) => key switch
    {
        "Checking prerequisites" => "Checking this PC",
        "Base worktree" => "Making the base copy of your project",
        "Pool config" => "Saving the settings",
        "Claude Code hooks" => "Connecting Claude Code",
        "Codex instructions" => "Connecting Codex",
        "Daily upkeep task" => "Scheduling the daily upkeep",
        "Building the base (warm clones)" => "Building the base copy",
        "Scheduled task" => "Removing the daily upkeep",
        "Pool registry" => "Forgetting the repository",
        "Base and pool files" => "Deleting the base copy",
        "Done" => "Done",
        _ when key.StartsWith("Claude Code hooks for pool", StringComparison.Ordinal) => "Disconnecting Claude Code",
        _ => key,
    };
}

/// <summary>One run of a script with its streamed output.</summary>
public sealed class OperationViewModel : ObservableObject
{
    private const int MaxLines = 20000;
    private static readonly string[] FailurePrefixes = { "cow_worktree:", "setup:", "uninstall:", "migrate_worktree:" };
    private readonly CancellationTokenSource _cts = new();
    private bool _canCancel;

    public OperationViewModel(OperationKind kind, string title, string commandLine)
    {
        Kind = kind;
        Title = title;
        CommandLine = commandLine;
        Started = DateTime.Now;
    }

    public OperationKind Kind { get; }
    public string Title { get; }
    public string CommandLine { get; }
    public DateTime Started { get; }
    public DateTime? Finished { get; private set; }
    public bool IsRunning => Finished == null;
    public int? ExitCode { get; private set; }
    public bool Succeeded => ExitCode == 0;
    public bool Cancelled { get; private set; }
    public string? ResultLine { get; private set; }
    public ObservableCollection<OutputLine> Lines { get; } = new();
    public ObservableCollection<StepItem> Steps { get; } = new();

    /// <summary>The folder this run works on (a new worktree, a pool root): what a cleanup after a stop looks at.</summary>
    public string? Target { get; init; }
    /// <summary>The repository the run belongs to, for runs that are not about the selected one.</summary>
    public string? PoolRoot { get; init; }

    /// <summary>Why the run failed, in plain words; set when it finishes with an error.</summary>
    public FriendlyError? Friendly { get; private set; }

    public CancellationToken CancellationToken => _cts.Token;
    public bool CancelRequested => _cts.IsCancellationRequested;

    /// <summary>Stopping is offered only while it leaves things in a state that can be picked up again.</summary>
    public bool CanCancel
    {
        get => _canCancel && IsRunning && !CancelRequested;
        set
        {
            if (_canCancel == value) return;
            _canCancel = value;
            Raise(nameof(CanCancel), nameof(CancelVisibility));
        }
    }

    public Visibility CancelVisibility => Theme.Vis(CanCancel);

    public void RequestCancel()
    {
        if (!CanCancel) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        AddLine("Stopping…", true);
        Raise(nameof(CanCancel), nameof(CancelVisibility), nameof(StatusText), nameof(Summary));
    }

    public string StartedText => Started.ToString("HH:mm:ss");
    public string ElapsedText => Fmt.Duration((Finished ?? DateTime.Now) - Started);
    public string StatusText => IsRunning ? (CancelRequested ? "stopping" : "running") : Cancelled ? "stopped" : Succeeded ? "done" : $"failed (exit {ExitCode})";
    public string Glyph => IsRunning ? "" : Cancelled ? "" : Succeeded ? "" : "";
    public Brush StatusBrush => IsRunning ? Theme.Attention : Cancelled ? Theme.Caution : Succeeded ? Theme.Success : Theme.Critical;
    public Visibility RunningVisibility => Theme.Vis(IsRunning);
    public Visibility DoneVisibility => Theme.Vis(!IsRunning);
    public string Summary => $"{StatusText} · {ElapsedText}" + (ResultLine != null && Succeeded ? $" · {ResultLine}" : "");

    /// <summary>The step that is running now, in plain words.</summary>
    public string CurrentStep => Steps.FirstOrDefault(s => s.State == StepState.Running)?.Label ?? "";

    /// <summary>The steps this run is expected to print, shown as pending until they start.</summary>
    public void ExpectSteps(IEnumerable<string> keys)
    {
        foreach (var k in keys) Steps.Add(new StepItem(k, StepItem.Friendly(k)));
    }

    public void AddLine(string text, bool isStdErr)
    {
        Brush brush;
        if (FailurePrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal))) brush = Theme.Critical;
        else if (text.StartsWith("==> ", StringComparison.Ordinal))
        {
            brush = Theme.Attention;
            StartStep(text[4..].Trim());
        }
        else if (text.Contains("warning:", StringComparison.OrdinalIgnoreCase)) brush = Theme.Caution;
        // cow_worktree.ps1 and migrate_worktree.ps1 report progress on stderr and their result on stdout;
        // setup.ps1 and uninstall.ps1 write everything to stdout.
        else if (Kind is OperationKind.Setup or OperationKind.Uninstall or OperationKind.SelfTest) brush = Theme.Primary;
        else brush = isStdErr ? Theme.Primary : Theme.Attention;
        if (Lines.Count >= MaxLines) Lines.RemoveAt(0);
        Lines.Add(new OutputLine(text, brush));
        LineAdded?.Invoke(text);
    }

    /// <summary>Raised for every output line (on the UI thread), so a run's phase can be followed.</summary>
    public event Action<string>? LineAdded;

    private void StartStep(string key)
    {
        foreach (var s in Steps.Where(s => s.State == StepState.Running)) s.State = StepState.Done;
        if (key == "Done") return;
        var step = Steps.FirstOrDefault(s => s.Key == key && s.State == StepState.Pending);
        if (step == null)
        {
            step = new StepItem(key, StepItem.Friendly(key));
            Steps.Add(step);
        }
        else
        {
            // steps before it that never printed did not apply (already there, switched off)
            foreach (var skipped in Steps.TakeWhile(s => s != step).Where(s => s.State == StepState.Pending).ToList()) Steps.Remove(skipped);
        }
        step.State = StepState.Running;
        OnPropertyChanged(nameof(CurrentStep));
    }

    private void FinishSteps(bool ok)
    {
        foreach (var s in Steps.ToList())
        {
            if (s.State == StepState.Running) s.State = ok ? StepState.Done : StepState.Failed;
            else if (s.State == StepState.Pending) { if (ok) Steps.Remove(s); }
        }
        OnPropertyChanged(nameof(CurrentStep));
    }

    public void Complete(ProcessResult r)
    {
        ExitCode = r.TimedOut ? -1 : r.ExitCode;
        Finished = DateTime.Now;
        var outLines = r.OutLines;
        // cow_worktree.ps1 and migrate_worktree.ps1 end with the worktree's path on stdout; setup.ps1 and
        // uninstall.ps1 write everything there, so their last line is not a result.
        ResultLine = Kind is OperationKind.Setup or OperationKind.Uninstall ? null : outLines.Length > 0 ? outLines[^1].Trim() : null;
        if (r.TimedOut) AddLine($"cow_worktree: timed out after {Fmt.Duration(r.Elapsed)}", true);
        FinishSteps(Succeeded);
        if (!Succeeded) Friendly = FriendlyErrors.Explain(Lines.Select(l => l.Text).ToList(), ExitCode);
        RaiseState();
    }

    public void Fail(string message)
    {
        ExitCode = -1;
        Finished = DateTime.Now;
        AddLine("cow_worktree: " + message, true);
        FinishSteps(false);
        Friendly = FriendlyErrors.Explain(message);
        RaiseState();
    }

    /// <summary>The person stopped it: not a failure, and nothing more is said than what was left behind.</summary>
    public void CompleteCancelled(string note)
    {
        ExitCode = -2;
        Cancelled = true;
        Finished = DateTime.Now;
        AddLine(note, true);
        FinishSteps(false);
        RaiseState();
    }

    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the run has finished and the app has caught up with it (registry and list read again).</summary>
    public Task Settled => _settled.Task;
    public void MarkSettled() => _settled.TrySetResult();

    public void Tick() => Raise(nameof(ElapsedText), nameof(Summary));

    public override string ToString() => $"{Title}: {StatusText}";

    private void RaiseState() => Raise(nameof(Finished), nameof(IsRunning), nameof(ExitCode), nameof(Succeeded), nameof(Cancelled), nameof(ResultLine),
        nameof(ElapsedText), nameof(StatusText), nameof(Glyph), nameof(StatusBrush), nameof(RunningVisibility), nameof(DoneVisibility), nameof(Summary),
        nameof(Friendly), nameof(CanCancel), nameof(CancelVisibility));
}
