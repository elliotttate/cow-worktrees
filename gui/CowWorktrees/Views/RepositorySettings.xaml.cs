using CowWorktrees.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace CowWorktrees.Views;

/// <summary>Repository settings, shown over the main window's pages.</summary>
public sealed partial class RepositorySettings : UserControl
{
    public RepositorySettingsViewModel VM { get; }

    public event Action? Closed;
    /// <summary>"Remove…": the main window asks what to remove and runs it.</summary>
    public event Action? RemoveRequested;
    /// <summary>Asks whether to leave with changes that were not applied; the main window shows the question.</summary>
    public Func<Task<bool>>? ConfirmDiscard { get; set; }

    public RepositorySettings(RepositorySettingsViewModel vm)
    {
        VM = vm;
        InitializeComponent();
        SchedulePicker.SelectedTime = vm.ScheduleTime;
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RepositorySettingsViewModel.ScheduleTime) && SchedulePicker.SelectedTime != VM.ScheduleTime) SchedulePicker.SelectedTime = VM.ScheduleTime;
            if (e.PropertyName == nameof(RepositorySettingsViewModel.ResultOk)) ShowResultIcon();
        };
        ShowResultIcon();
    }

    private void ShowResultIcon()
    {
        ResultIcon.Glyph = VM.ResultOk ? "" : "";
        ResultIcon.Foreground = VM.ResultOk ? Theme.Success : Theme.Critical;
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await CloseAsync();

    private async Task CloseAsync()
    {
        if (VM.HasChanges && ConfirmDiscard != null && !await ConfirmDiscard()) return;
        Closed?.Invoke();
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        await CloseAsync();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => VM.Apply();
    private void Discard_Click(object sender, RoutedEventArgs e) => VM.Discard();
    private void Repair_Click(object sender, RoutedEventArgs e) => VM.Repair();
    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke();

    private void SchedulePicker_SelectedTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args)
    {
        if (args.NewTime is { } t) VM.ScheduleTime = t;
    }

    private void BaseRefBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            sender.ItemsSource = VM.BaseRefChoices.Where(r => r.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).Take(30).ToList();
    }

    private void BaseRefBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (BaseRefBox.ItemsSource == null) BaseRefBox.ItemsSource = VM.BaseRefChoices.Take(30).ToList();
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
}
