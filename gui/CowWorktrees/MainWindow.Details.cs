using System.Text.Json;
using CowWorktrees.Services;
using CowWorktrees.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CowWorktrees;

/// <summary>
/// The details pane: the summary Claude or Codex writes of a worktree when asked, and the commit and file
/// rows that open the Changes view.
/// </summary>
public sealed partial class MainWindow
{
    private SummaryController? _summaries;
    private bool _selectedAtStart;

    private void InitializeDetails()
    {
        _summaries = new SummaryController(VM);
        VisibilityChanged += (_, e) => VM.SetWindowVisible(e.Visible);
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedWorktree) && VM.SelectedWorktree is { } row) _summaries.Attach(row);
            // The option "keep every summary up to date": after each look at the worktrees. Never in a hidden or test run.
            if (e.PropertyName == nameof(MainViewModel.LastRefresh) && VM.LastRefresh != null && !_options.Hidden && !_options.Unattended) _summaries.Kick();
            // --select on a normal start: that worktree is selected once the list is there.
            if (e.PropertyName == nameof(MainViewModel.LastRefresh) && VM.LastRefresh != null && !_selectedAtStart && _options.Select != null && !_options.Unattended)
            {
                _selectedAtStart = true;
                if (VM.SelectedWorktree == null && VM.FindRow(_options.Select) is { } wanted)
                {
                    WorktreeList.SelectedItem = wanted;
                    WorktreeList.ScrollIntoView(wanted);
                }
            }
        };
        UpdateSummaryHint();
    }

    private void Summarize(SummaryTool tool)
    {
        if (Selected is { } row && _summaries != null) _ = _summaries.SummarizeAsync(row, tool);
    }

    private void Summarize_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (_summaries != null) Summarize(_summaries.DefaultTool);
    }

    private void SummarizeClaude_Click(object sender, RoutedEventArgs e) => Summarize(SummaryTool.Claude);

    private void SummarizeCodex_Click(object sender, RoutedEventArgs e) => Summarize(SummaryTool.Codex);

    private void SummaryCancel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) _summaries?.Cancel(row);
    }

    private void SummaryCopy_Click(object sender, RoutedEventArgs e) => CopyText(Selected?.Summary?.Text);

    private void SummaryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) _summaries?.Remove(row);
    }

    /// <summary>The menu says which of the two tools is on this PC, and which one the button itself uses.</summary>
    private void SummaryMenu_Opening(object? sender, object e)
    {
        if (_summaries == null) return;
        bool has = Selected?.Summary != null;
        string again = has ? "Update" : "Summarize";
        SummaryClaudeItem.Text = _summaries.ClaudePath != null ? $"{again} with Claude" : "Claude Code was not found on this PC";
        SummaryClaudeItem.IsEnabled = _summaries.ClaudePath != null;
        SummaryCodexItem.Text = _summaries.CodexPath != null ? $"{again} with Codex" : "Codex was not found on this PC";
        SummaryCodexItem.IsEnabled = _summaries.CodexPath != null;
        SummaryCopyItem.IsEnabled = has;
        SummaryRemoveItem.IsEnabled = has;
        SummaryAutoItem.IsChecked = _summaries.Auto;
        SummaryAutoItem.IsEnabled = _summaries.AnyTool;
        SummaryAutoItem.Text = $"Keep every worktree's summary up to date (with {_summaries.DefaultTool})";
    }

    /// <summary>The option: summaries are written without being asked, whenever a worktree has changed and gone quiet.</summary>
    private void SummaryAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_summaries == null) return;
        _summaries.Auto = SummaryAutoItem.IsChecked;
        UpdateSummaryHint();
    }

    private void UpdateSummaryHint()
    {
        if (_summaries == null) return;
        SummaryHintText.Text = !_summaries.AnyTool
            ? "Claude or Codex can write a few lines here about what a worktree is for and where it stands. Neither Claude Code nor Codex was found on this PC: install one and sign in, then start this app again."
            : _summaries.Auto
                ? $"Summaries are kept up to date by {_summaries.DefaultTool}: one is written here when this worktree has work of its own and has been quiet for two minutes. Its commit titles and changed lines are sent to that service."
                : "Have Claude or Codex write a few lines about what this worktree is for and where it stands. Its commit titles and changed lines are sent to that service, through the tool on this PC.";
    }

    /// <summary>A commit's row: the Changes view on that commit.</summary>
    private void CommitRow_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row && (sender as FrameworkElement)?.Tag is string { Length: > 0 } sha) OpenChanges(row, sha);
    }

    /// <summary>A changed file's row: the Changes view on the uncommitted changes, at that file.</summary>
    private void StatusRow_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row && (sender as FrameworkElement)?.Tag is StatusItem item) OpenChanges(row, "uncommitted", item.Path);
    }

    /// <summary>--found-dump and --wizard-adopt: what the walkthrough found from before, and using it.</summary>
    private async Task AdoptFoundUnattendedAsync()
    {
        if (_options.FoundDumpPath == null && !_options.WizardAdopt) return;
        var vm = _wizard?.VM ?? OpenWizard();
        await vm.ScanAsync();
        var items = vm.Found.ToList();
        var found = items.Select(f => new { name = f.Name, root = f.Found.Root, repo = f.Found.Repo, worktrees = f.Found.Worktrees, problem = f.Found.Problem, ticked = f.IsChecked == true }).ToList();
        if (_options.WizardAdopt) await vm.AdoptAsync();
        await Task.Delay(800);   // the list of repositories is read again after a setup
        if (_options.FoundDumpPath is not { } dump) return;
        await File.WriteAllTextAsync(dump, JsonSerializer.Serialize(new
        {
            found,
            results = items.Select(i => new { name = i.Name, result = i.Result }).ToList(),
            status = vm.AdoptStatus,
            wizardOpen = _wizard != null,
            pools = VM.Pools.Select(p => new { name = p.Name, root = p.Root }).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>--summarize claude|codex and --summary-dump: write the summary of the --select worktree and what the pane holds.</summary>
    private async Task SummarizeUnattendedAsync()
    {
        if (_options.Summarize == null || _summaries == null) return;
        var row = VM.SelectedWorktree ?? VM.Visible.FirstOrDefault();
        if (row == null) return;
        _summaries.Attach(row);
        if (_options.Summarize.Equals("auto", StringComparison.OrdinalIgnoreCase)) await _summaries.RunAutoAsync(now: true);   // one pass of the option, without its two quiet minutes
        else if (Enum.TryParse<SummaryTool>(_options.Summarize, true, out var tool)) await _summaries.SummarizeAsync(row, tool);
        if (_options.SummaryDumpPath is not { } dump) return;
        await File.WriteAllTextAsync(dump, JsonSerializer.Serialize(new
        {
            worktree = row.Path,
            claude = _summaries.ClaudePath,
            codex = _summaries.CodexPath,
            defaultTool = _summaries.DefaultTool.ToString(),
            tool = row.Summary?.Tool,
            headline = row.SummaryHeadline,
            points = row.SummaryPoints,
            open = row.SummaryOpen,
            meta = row.SummaryMeta,
            stale = row.SummaryStale,
            error = row.SummaryError,
            text = row.Summary?.Text,
            numbers = new { changed = row.ChangedCount, untracked = row.UntrackedCount, unpushed = row.UnpushedCount, activity = row.ActivityShort },
            commits = row.CommitItems.Select(c => new { c.Sha, c.Subject, c.When }).ToList(),
            auto = _summaries.Auto,
            all = VM.AllRows.Select(r => new { name = r.Name, tool = r.Summary?.Tool, headline = r.SummaryHeadline, stale = r.SummaryStale, error = r.SummaryError }).ToList(),
            changes = row.StatusItems.Select(s => new { s.Path, s.Name, s.Folder, s.Badge, kind = s.Kind.ToString() }).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
