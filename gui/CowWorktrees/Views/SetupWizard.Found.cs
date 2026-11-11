using Microsoft.UI.Xaml;

namespace CowWorktrees.Views;

/// <summary>The first screen's "setups from before": using them.</summary>
public sealed partial class SetupWizard
{
    private void UseFound_Click(object sender, RoutedEventArgs e) => _ = VM.AdoptAsync();
}
