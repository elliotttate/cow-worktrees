using System.Collections.ObjectModel;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;

namespace CowWorktrees.ViewModels;

/// <summary>One setup from before, as the walkthrough's first screen lists it.</summary>
public sealed class FoundPoolItem : ObservableObject
{
    private bool? _checked;
    private string _result = "";

    public FoundPoolItem(FoundPool found)
    {
        Found = found;
        _checked = found.Problem == null;
    }

    public FoundPool Found { get; }
    public string Name => Found.Name;
    public bool CanUse => Found.Problem == null;
    public string Where => $"{Found.Root}  ·  for {Found.Repo}  ·  {(Found.Worktrees == 0 ? "no worktrees yet" : Fmt.Plural(Found.Worktrees, "worktree"))}";
    public string ProblemText => Found.Problem == null ? "" : "Cannot be used as it is: " + Found.Problem + ".";
    public Visibility ProblemVisibility => Theme.Vis(Found.Problem != null);

    /// <summary>Ticked: "Use them" takes this one. (bool? because that is what a check box holds.)</summary>
    public bool? IsChecked
    {
        get => _checked;
        set => SetProperty(ref _checked, value);
    }

    /// <summary>"added" or what went wrong, after "Use them".</summary>
    public string Result
    {
        get => _result;
        set { if (SetProperty(ref _result, value)) Raise(nameof(ResultVisibility)); }
    }
    public Visibility ResultVisibility => Theme.Vis(_result.Length > 0);
}

/// <summary>
/// The walkthrough's first screen also looks for setups made before (see <see cref="PoolScanner"/>) and
/// offers to use them: after installing again there is nothing to set up, only to connect.
/// </summary>
public sealed partial class SetupWizardViewModel
{
    private ObservableCollection<FoundPoolItem>? _found;
    private bool _adopting;
    private string _adoptStatus = "";

    /// <summary>Setups from before that are not in the app's list. Looked for when the screen first asks.</summary>
    public ObservableCollection<FoundPoolItem> Found
    {
        get
        {
            if (_found != null) return _found;
            _found = new ObservableCollection<FoundPoolItem>();
            _ = ScanAsync();
            return _found;
        }
    }

    public Visibility FoundVisibility => Theme.Vis(_found is { Count: > 0 });
    public string FoundTitle => _found is { Count: 1 } ? "Found a setup from before on this PC" : $"Found {_found?.Count ?? 0} setups from before on this PC";
    public string UseFoundLabel => _found is { Count: 1 } ? "Use it" : "Use them";

    public bool Adopting
    {
        get => _adopting;
        private set { if (SetProperty(ref _adopting, value)) Raise(nameof(CanAdopt)); }
    }
    public bool CanAdopt => !_adopting && _main.SetupAvailable && !_main.SetupRunning;
    public string AdoptStatus
    {
        get => _adoptStatus;
        private set => SetProperty(ref _adoptStatus, value);
    }

    public async Task ScanAsync()
    {
        _found ??= new ObservableCollection<FoundPoolItem>();
        try
        {
            var registered = _main.Pools.Select(p => p.Root).ToList();
            var found = await Task.Run(() => PoolScanner.Scan(registered));
            _found.Clear();
            foreach (var f in found) _found.Add(new FoundPoolItem(f));
        }
        catch (Exception ex)
        {
            AppLog.Write($"scan for setups: {ex.Message}");
        }
        Raise(nameof(FoundVisibility), nameof(FoundTitle), nameof(UseFoundLabel), nameof(CanAdopt));
    }

    /// <summary>
    /// Puts the ticked setups back: setup.ps1 runs again for each with the choices its own settings record,
    /// which adds it to the app's list and points Claude Code, Codex and the daily upkeep at this copy of the
    /// tool. Nothing is copied or built, and the worktrees are not touched.
    /// </summary>
    public async Task AdoptAsync()
    {
        if (!CanAdopt || _found == null) return;
        var chosen = _found.Where(f => f.IsChecked == true && f.CanUse).ToList();
        if (chosen.Count == 0) return;
        Adopting = true;
        var failed = new List<string>();
        try
        {
            foreach (var item in chosen)
            {
                AdoptStatus = $"Connecting {item.Name}…";
                var pool = item.Found.Pool;
                var want = pool.Config?.Integrations;   // what was chosen then; a setup from before 1.1.0 has none recorded and gets all three
                var options = new SetupOptions
                {
                    Repo = pool.Repo,
                    Root = pool.Root,
                    Name = pool.Name,
                    Claude = want?.Claude ?? true,
                    Codex = want?.Codex ?? true,
                    Schedule = want == null || want.ScheduleAt != null,
                    ScheduleAt = want?.ScheduleAt,
                };
                var op = _main.StartSetup(options, $"Add {item.Name}");
                await op.Settled;
                if (op.Succeeded) item.Result = "added";
                else
                {
                    item.Result = "could not be added: the Activity tab has what setup said";
                    failed.Add(item.Name);
                }
            }
        }
        finally { Adopting = false; }
        if (failed.Count > 0)
        {
            AdoptStatus = $"{string.Join(", ", failed)} could not be added.";
            return;
        }
        AdoptStatus = "";
        Closed?.Invoke();   // the list now shows them
    }
}
