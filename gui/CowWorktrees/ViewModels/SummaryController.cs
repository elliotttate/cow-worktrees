using CowWorktrees.Services;

namespace CowWorktrees.ViewModels;

/// <summary>
/// The summaries in the details pane: finds the Claude Code and Codex command line tools, gathers what a
/// worktree's summary is written from, runs the tool, and keeps what it wrote. A summary is only ever
/// written when the person presses the button: it sends the worktree's commit titles and changed lines to
/// the tool's service, through the tool as it is set up on this PC.
/// </summary>
public sealed class SummaryController
{
    private readonly MainViewModel _main;
    private readonly DiffService _diff;
    private readonly SummaryStore _store = SummaryStore.Load();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);

    public SummaryController(MainViewModel main)
    {
        _main = main;
        _diff = new DiffService(main.Git);
        ClaudePath = SummaryService.Find(SummaryTool.Claude);
        CodexPath = SummaryService.Find(SummaryTool.Codex);
    }

    public string? ClaudePath { get; }
    public string? CodexPath { get; }
    public bool AnyTool => ClaudePath != null || CodexPath != null;
    public string? PathOf(SummaryTool tool) => tool == SummaryTool.Claude ? ClaudePath : CodexPath;

    /// <summary>The tool the Summarize button uses: the one chosen last when it is installed, else whichever is.</summary>
    public SummaryTool DefaultTool =>
        Enum.TryParse<SummaryTool>(_store.Tool, true, out var t) && PathOf(t) != null ? t
        : ClaudePath != null || CodexPath == null ? SummaryTool.Claude : SummaryTool.Codex;

    /// <summary>Shows the summary that was written for this worktree earlier, if there is one.</summary>
    public void Attach(WorktreeViewModel row)
    {
        if (row.SummaryLookedUp) return;
        row.SummaryLookedUp = true;
        if (_store.Get(row.Path) is { } s) row.SetSummary(s);
    }

    public void Cancel(WorktreeViewModel row)
    {
        if (_running.TryGetValue(row.Path, out var cts)) cts.Cancel();
    }

    public void Remove(WorktreeViewModel row)
    {
        _store.Remove(row.Path);
        _store.Save();
        row.SetSummary(null);
    }

    // ---- kept up to date by itself ------------------------------------------------------------------------------

    /// <summary>A worktree is left alone until it has been quiet this long, so one being worked on is not summarised at every step.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);
    private bool _autoRunning;
    private int _autoFailures;
    private readonly Dictionary<string, string> _autoFailedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The option "keep every worktree's summary up to date": after each look at the worktrees, any that has work
    /// of its own and no summary of its present state gets one, one at a time, with the tool chosen last.
    /// </summary>
    public bool Auto
    {
        get => _store.Auto;
        set
        {
            if (_store.Auto == value) return;
            _store.Auto = value;
            _store.Save();
            _autoFailures = 0;
            _autoFailedAt.Clear();
            if (value) Kick();
        }
    }

    /// <summary>Why the option is not writing anything right now; empty when it is at work or has nothing to do.</summary>
    public string AutoPausedWhy => !Auto ? "" : !AnyTool ? "Neither Claude Code nor Codex was found on this PC."
        : _autoFailures >= 3 ? "Automatic summaries paused after three failures in a row; they start again with the app, or when the option is switched off and on." : "";

    /// <summary>Called after every look at the worktrees. Does nothing when the option is off or a pass is under way.</summary>
    public void Kick()
    {
        if (Auto && !_autoRunning && _autoFailures < 3 && AnyTool) _ = RunAutoAsync(false);
    }

    /// <summary>One pass over the worktrees. <paramref name="now"/> skips the wait for a worktree to be quiet (tests).</summary>
    public async Task RunAutoAsync(bool now)
    {
        if (_autoRunning) return;
        _autoRunning = true;
        try
        {
            foreach (var row in _main.AllRows.ToList())
            {
                if (!now && !Auto) break;
                Attach(row);
                if (!Wants(row, now)) continue;
                string state = row.SummarySignature;
                await SummarizeAsync(row, DefaultTool);
                if (row.Summary?.Signature == state) _autoFailures = 0;
                else
                {
                    _autoFailedAt[row.Path] = state;   // not again for this very state
                    if (++_autoFailures >= 3)
                    {
                        AppLog.Write("summaries: three failures in a row, automatic summaries paused");
                        break;
                    }
                }
            }
        }
        finally { _autoRunning = false; }
    }

    /// <summary>Has work of its own, is not being worked on this minute, and has no summary of what it is now.</summary>
    private bool Wants(WorktreeViewModel row, bool now)
    {
        if (row.IsMissing || row.IsBase || !row.HasProbe || row.Error != null || row.SummaryBusy) return false;
        bool hasWork = row.IsDirty || (row.Cow?.Commits ?? row.Ahead ?? row.Unpushed ?? 0) > 0 || (row.Unpushed ?? 0) > 0;
        if (!hasWork) return false;
        string state = row.SummarySignature;
        if (row.Summary?.Signature == state) return false;
        if (_autoFailedAt.TryGetValue(row.Path, out var failed) && failed == state) return false;
        if (!now && row.LastActivity is { } t && DateTime.Now - t < Quiet) return false;
        return true;
    }

    public async Task SummarizeAsync(WorktreeViewModel row, SummaryTool tool)
    {
        if (row.SummaryBusy || row.IsMissing) return;
        if (PathOf(tool) is not { } command)
        {
            row.SetSummaryError($"{Product(tool)} was not found on this PC. Install it and sign in, then start this app again.");
            return;
        }
        var cts = new CancellationTokenSource();
        _running[row.Path] = cts;
        string signature = row.SummarySignature;
        row.SetSummaryBusy(true, $"Gathering the changes for {tool}…");
        try
        {
            string prompt = await BuildPromptAsync(row, cts.Token);
            row.SetSummaryBusy(true, $"{tool} is writing the summary…");
            var (text, error) = await SummaryService.RunAsync(tool, command, tool == SummaryTool.Claude ? _store.ClaudeModel : _store.CodexModel, prompt, cts.Token);
            if (text == null)
            {
                row.SetSummaryBusy(false);
                row.SetSummaryError(error);
                return;
            }
            var summary = new WorktreeSummary { Path = row.Path, Tool = tool.ToString(), At = DateTime.Now, Signature = signature, Text = text };
            _store.Set(summary);
            _store.Tool = tool.ToString();
            _store.Save();
            row.SetSummaryBusy(false);
            row.SetSummary(summary);
        }
        catch (OperationCanceledException)
        {
            row.SetSummaryBusy(false);
        }
        catch (Exception ex)
        {
            AppLog.Write($"summary of {row.Path}: {ex}");
            row.SetSummaryBusy(false);
            row.SetSummaryError("The summary could not be written: " + ex.Message);
        }
        finally
        {
            _running.Remove(row.Path);
            cts.Dispose();
        }
    }

    private static string Product(SummaryTool tool) => tool == SummaryTool.Claude ? "Claude Code" : "Codex";

    /// <summary>What the summary is written from: the worktree's own work since it started, as the Changes view's "Everything" shows it.</summary>
    public async Task<string> BuildPromptAsync(WorktreeViewModel row, CancellationToken ct)
    {
        string dir = row.Path;
        // The same start as ChangesViewModel.BuildScopesAsync: where the worktree was made, else where its branch
        // left its upstream, else where it left the repository's base ref.
        string? start = null;
        string what = "its last commit (only uncommitted edits are known)";
        if (!string.IsNullOrWhiteSpace(row.Cow?.Start) && await _diff.ResolveAsync(dir, row.Cow!.Start!, ct) is { } made)
        {
            start = made;
            what = $"{made[..9]}, the commit this worktree was made at";
        }
        else if (row.Upstream != null && !row.UpstreamGone && await _diff.MergeBaseAsync(dir, row.Upstream, ct) is { } fork)
        {
            start = fork;
            what = $"{fork[..9]}, where the branch left {row.Upstream}";
        }
        else if (!row.IsBase && _main.Pool?.Config?.BaseRef is { Length: > 0 } baseRef && await _diff.MergeBaseAsync(dir, baseRef, ct) is { } fromBase)
        {
            start = fromBase;
            what = $"{fromBase[..9]}, where the branch left {baseRef}";
        }
        var commits = await _diff.CommitsAsync(dir, start, 60, ct);
        var phantoms = row.StatusLines.Where(l => l.StartsWith(" ~ ", StringComparison.Ordinal)).Select(l => l[3..]).ToList();
        var changes = await _diff.LoadAsync(dir, new DiffSpec(start ?? "HEAD", null), phantoms, ct);
        return SummaryService.BuildPrompt(row.Name, row.BranchDisplay, what, commits, changes, row.Changed, row.Untracked);
    }
}
