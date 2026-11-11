using System.Diagnostics;
using CowWorktrees.Services;
using CowWorktrees.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace CowWorktrees.Views;

/// <summary>Small things several views do: the clipboard, opening a link or a Settings page, picking a folder.</summary>
public static class Ui
{
    public static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    /// <summary>Opens a web address, a folder, or a Windows Settings page (ms-settings:...).</summary>
    public static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write($"could not open {target}: {ex.Message}");
        }
    }

    public static async Task<string?> PickFolderAsync(Window window)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            AppLog.Write($"folder picker: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Installs Git with winget in a console window the person can see and answer; without winget, opens the
    /// download page. The walkthrough finds the new Git by itself when its window is active again.
    /// </summary>
    public static void InstallGit()
    {
        if (EnvironmentPath.Find("winget.exe") == null)
        {
            Launch(FriendlyErrors.GitDownloadUrl);
            return;
        }
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true };
            psi.ArgumentList.Add("/k");
            psi.ArgumentList.Add("winget install --id Git.Git -e --source winget");
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write($"winget: {ex.Message}");
            Launch(FriendlyErrors.GitDownloadUrl);
        }
    }

    public static void Do(FriendlyError? error)
    {
        if (error == null) return;
        switch (error.Action)
        {
            case FriendlyAction.InstallGit: InstallGit(); break;
            case FriendlyAction.OpenDiskSettings: Launch("ms-settings:disks-and-volumes"); break;
            case FriendlyAction.OpenTroubleshooting: Launch(FriendlyErrors.TroubleshootingUrl); break;
            case FriendlyAction.OpenFolder when error.ActionTarget is { } folder && Directory.Exists(folder): Launch(folder); break;
        }
    }
}

/// <summary>The get-started walkthrough, shown over the main window's pages.</summary>
public sealed partial class SetupWizard : UserControl
{
    private readonly Window _window;

    public SetupWizardViewModel VM { get; }

    /// <summary>Asks whether to leave when something was entered; the main window shows the question.</summary>
    public Func<Task<bool>>? ConfirmCancel { get; set; }
    /// <summary>"Create one for me" for a missing Dev Drive (experimental).</summary>
    public event Action? CreateDevDriveRequested;

    public SetupWizard(SetupWizardViewModel vm, Window window)
    {
        VM = vm;
        _window = window;
        InitializeComponent();
        SchedulePicker.SelectedTime = vm.ScheduleTime;
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SetupWizardViewModel.Step)) return;
            Scroller.ChangeView(null, 0, null, true);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusForStep);
        };
        Loaded += (_, _) => FocusForStep();
    }

    /// <summary>Coming back from Windows Settings or an installer: look at the PC again.</summary>
    public void OnWindowActivated()
    {
        if (VM.Step == WizardStep.Checks) _ = VM.RunChecksAsync();
    }

    private void FocusForStep()
    {
        try
        {
            if (VM.Step == WizardStep.Project) RepoBox.Focus(FocusState.Programmatic);
            else if (NextButton.Visibility == Visibility.Visible && NextButton.IsEnabled) NextButton.Focus(FocusState.Programmatic);
        }
        catch
        {
            // focus is a convenience
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e) => VM.Next();
    private void Back_Click(object sender, RoutedEventArgs e) => VM.Back();

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await CancelAsync();

    private async Task CancelAsync()
    {
        if (VM.IsRunning) return;
        if (VM.ShouldConfirmCancel && ConfirmCancel != null && !await ConfirmCancel()) return;
        VM.Cancel();
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && VM.CancelVisibility == Visibility.Visible)
        {
            e.Handled = true;
            await CancelAsync();
        }
        else if (e.Key == VirtualKey.Enter && VM.CanGoNext && e.OriginalSource is TextBox or ToggleSwitch)
        {
            // Enter in a field is Next; on a button it presses that button
            e.Handled = true;
            VM.Next();
        }
    }

    private void Fix_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "windows-update": Ui.Launch("ms-settings:windowsupdate"); break;
            case "install-git": Ui.InstallGit(); break;
            case "git-download": Ui.Launch(FriendlyErrors.GitDownloadUrl); break;
            case "disk-settings": Ui.Launch("ms-settings:disks-and-volumes"); break;
            case "create-dev-drive": CreateDevDriveRequested?.Invoke(); break;
        }
    }

    private async void CheckAgain_Click(object sender, RoutedEventArgs e) => await VM.RunChecksAsync();

    private async void BrowseRepo_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.PickFolderAsync(_window) is { } path) VM.RepoPath = path;
    }

    private async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        if (await Ui.PickFolderAsync(_window) is not { } path) return;
        VM.RootEditedByUser();
        VM.RootPath = path;
    }

    private void UseSuggestedRepo_Click(object sender, RoutedEventArgs e) => VM.UseSuggestedRepo();

    private void BaseRefBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        VM.BaseRefEditedByUser();
        sender.ItemsSource = VM.BaseRefChoices.Where(r => r.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).Take(30).ToList();
    }

    private void BaseRefBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (BaseRefBox.ItemsSource == null) BaseRefBox.ItemsSource = VM.BaseRefChoices.Take(30).ToList();
    }

    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveBox.SelectedItem is DriveChoice d) VM.SelectedDrive = d;
    }

    private void RootBox_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        // typed by the person (not filled in by choosing a drive): keep it when the drive changes
        if (sender.FocusState != FocusState.Unfocused) VM.RootEditedByUser();
    }

    private void SchedulePicker_SelectedTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        if (args.NewTime is { } t) VM.ScheduleTime = t;
    }

    private async void CopyInstructions_Click(object sender, RoutedEventArgs e)
    {
        var text = VM.AgentInstructions;
        if (text.Length == 0) return;
        Ui.Copy(text);
        CopyInstructionsLink.Content = "Copied";
        await Task.Delay(1800);
        CopyInstructionsLink.Content = "Copy the instructions";
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => VM.Stop();

    private void ErrorAction_Click(object sender, RoutedEventArgs e)
    {
        var error = VM.Error;
        if (error is { Action: FriendlyAction.OpenFolder, ActionTarget: null })
            error = error with { ActionTarget = Directory.Exists(VM.Options.Root) ? VM.Options.Root : Path.GetPathRoot(VM.Options.Root) };
        Ui.Do(error);
    }

    private void FirstWorktree_Click(object sender, RoutedEventArgs e) => VM.RequestFirstWorktree();

    private void CopyClaudeCommand_Click(object sender, RoutedEventArgs e) => Ui.Copy(VM.ClaudeCommand);
}
