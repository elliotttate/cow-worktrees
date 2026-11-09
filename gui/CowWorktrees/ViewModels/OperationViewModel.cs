using System.Collections.ObjectModel;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

public enum OperationKind { Create, Remove, Prune, PrunePreview, RefreshBase }

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

/// <summary>One run of cow_worktree.ps1 with its streamed output.</summary>
public sealed class OperationViewModel : ObservableObject
{
    private const int MaxLines = 20000;

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
    public string? ResultLine { get; private set; }
    public ObservableCollection<OutputLine> Lines { get; } = new();

    public string StartedText => Started.ToString("HH:mm:ss");
    public string ElapsedText => Fmt.Duration((Finished ?? DateTime.Now) - Started);
    public string StatusText => IsRunning ? "running" : Succeeded ? "done" : $"failed (exit {ExitCode})";
    public string Glyph => IsRunning ? "" : Succeeded ? "" : "";
    public Brush StatusBrush => IsRunning ? Theme.Attention : Succeeded ? Theme.Success : Theme.Critical;
    public Visibility RunningVisibility => Theme.Vis(IsRunning);
    public Visibility DoneVisibility => Theme.Vis(!IsRunning);
    public string Summary => $"{StatusText} · {ElapsedText}" + (ResultLine != null && Succeeded ? $" · {ResultLine}" : "");

    public void AddLine(string text, bool isStdErr)
    {
        // The script reports progress on stderr and its result on stdout; only "cow_worktree: ..." lines are failures.
        var brush = text.StartsWith("cow_worktree:", StringComparison.Ordinal) ? Theme.Critical
            : isStdErr ? Theme.Primary : Theme.Attention;
        if (Lines.Count >= MaxLines) Lines.RemoveAt(0);
        Lines.Add(new OutputLine(text, brush));
    }

    public void Complete(ProcessResult r)
    {
        ExitCode = r.TimedOut ? -1 : r.ExitCode;
        Finished = DateTime.Now;
        var outLines = r.OutLines;
        ResultLine = outLines.Length > 0 ? outLines[^1].Trim() : null;
        if (r.TimedOut) AddLine($"cow_worktree: timed out after {Fmt.Duration(r.Elapsed)}", true);
        RaiseState();
    }

    public void Fail(string message)
    {
        ExitCode = -1;
        Finished = DateTime.Now;
        AddLine("cow_worktree: " + message, true);
        RaiseState();
    }

    public void Tick() => Raise(nameof(ElapsedText), nameof(Summary));

    public override string ToString() => $"{Title}: {StatusText}";

    private void RaiseState() => Raise(nameof(Finished), nameof(IsRunning), nameof(ExitCode), nameof(Succeeded), nameof(ResultLine),
        nameof(ElapsedText), nameof(StatusText), nameof(Glyph), nameof(StatusBrush), nameof(RunningVisibility), nameof(DoneVisibility), nameof(Summary));
}
