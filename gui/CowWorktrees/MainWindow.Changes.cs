using System.Text.Json;
using CowWorktrees.ViewModels;
using CowWorktrees.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CowWorktrees;

/// <summary>
/// The Changes view: what changed in the selected worktree, over the whole window (and, with F11, the whole
/// screen). It only reads the worktree.
/// </summary>
public sealed partial class MainWindow
{
    private ChangesView? _changesView;
    /// <summary>The views opened last, newest last; older ones are let go.</summary>
    private readonly List<ChangesView> _changesKept = new();
    private const int ChangesKeptMax = 3;
    private Task? _changesLoad;
    private bool _fullScreen;

    private bool ChangesOpen => _changesView != null && OverlayHost.Children.Contains(_changesView);

    private void ViewChanges_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null) OpenChanges(Selected);
    }

    private void ViewChanges_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (ChangesOpen || VM.SelectedWorktree is not { } row) return;
        e.Handled = true;
        OpenChanges(row);
    }

    /// <summary>Opens the view on <paramref name="row"/>; <paramref name="scope"/> is "uncommitted", "all" or the start of a commit's name.</summary>
    private ChangesViewModel? OpenChanges(WorktreeViewModel row, string? scope = null, string? showFile = null)
    {
        if (row.IsMissing || (OverlayOpen && !ChangesOpen)) return null;   // the walkthrough or the settings page is open
        // The same worktree again: its view is still there with what it read, and reads again only when the
        // worktree changed since, or when another scope is asked for.
        var view = _changesKept.FirstOrDefault(v => ReferenceEquals(v.VM.Row, row));
        ChangesViewModel vm;
        if (view != null)
        {
            vm = view.VM;
            _changesKept.Remove(view);
            _changesKept.Add(view);
            var wanted = scope == null ? null : vm.FindScope(scope);
            _changesLoad = vm.Stale || vm.Problem != null || (scope != null && wanted == null) ? vm.InitAsync(scope)
                : wanted != null ? vm.ShowScopeAsync(wanted) : Task.CompletedTask;
        }
        else
        {
            vm = new ChangesViewModel(VM, row);
            view = new ChangesView(vm, VM.EditorPath);
            view.Closed += CloseChanges;
            view.FullScreenRequested += () => SetFullScreen(!_fullScreen);
            _changesKept.Add(view);
            while (_changesKept.Count > ChangesKeptMax)
            {
                _changesKept[0].VM.Detach();
                _changesKept.RemoveAt(0);
            }
            _changesLoad = vm.InitAsync(scope);
        }
        ShowOverlay(view);
        _changesView = view;
        view.IsFullScreen = _fullScreen;
        if (showFile != null) _ = ShowFileWhenLoadedAsync(view, _changesLoad, showFile);
        return vm;
    }

    private static async Task ShowFileWhenLoadedAsync(ChangesView view, Task load, string file)
    {
        try
        {
            await load;
            await Task.Delay(250);   // the rows are laid out
            view.ShowFile(file);
        }
        catch (Exception ex) { AppLog.Write($"changes view: {ex.Message}"); }
    }

    private void CloseChanges()
    {
        if (_fullScreen) SetFullScreen(false);
        if (ChangesOpen) HideOverlay();
        _changesView = null;
        _changesLoad = null;
        WorktreeList.Focus(FocusState.Programmatic);
    }

    /// <summary>The whole screen for the Changes view: no title bar, no taskbar. Esc or F11 goes back.</summary>
    private void SetFullScreen(bool on)
    {
        _fullScreen = on;
        try { AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default); }
        catch (Exception ex) { AppLog.Write($"full screen: {ex.Message}"); }
        // The view also takes the title bar's row, which is empty on a full screen.
        Grid.SetRow(OverlayHost, on ? 0 : 1);
        Grid.SetRowSpan(OverlayHost, on ? 5 : 4);
        if (_changesView != null) _changesView.IsFullScreen = on;
    }

    /// <summary>--select name --changes [scope] on a normal start: the app opens on that worktree's changes.</summary>
    private void OpenChangesAtStart()
    {
        var row = (_options.Select != null ? VM.FindRow(_options.Select) : null) ?? VM.Visible.FirstOrDefault(r => r.IsDirty) ?? VM.Visible.FirstOrDefault();
        if (row == null) return;
        WorktreeList.SelectedItem = row;
        OpenChanges(row, _options.Changes!.Length > 0 ? _options.Changes : null);
    }

    /// <summary>--changes [scope], --changes-file, --changes-dump: open the view for the snapshot and write what it holds.</summary>
    private async Task OpenChangesUnattendedAsync()
    {
        await AdoptFoundUnattendedAsync();
        if (_options.RefreshAgain) await VM.RefreshAsync();
        await SummarizeUnattendedAsync();
        if (_options.Changes == null) return;
        var row = VM.SelectedWorktree ?? VM.Visible.FirstOrDefault(r => r.IsDirty) ?? VM.Visible.FirstOrDefault();
        if (row == null) return;
        var vm = OpenChanges(row, _options.Changes.Length > 0 ? _options.Changes : null);
        if (vm == null || _changesLoad == null) return;
        await _changesLoad;
        await Task.Delay(500);
        if (_options.ChangesFile is { } file && _changesView != null)
        {
            _changesView.ShowFile(file);
            await Task.Delay(500);
        }
        if (_options.ChangesDumpPath is { } dump)
            await File.WriteAllTextAsync(dump, JsonSerializer.Serialize(vm.Dump(), new JsonSerializerOptions { WriteIndented = true }));
    }
}
