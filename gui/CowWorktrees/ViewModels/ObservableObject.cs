using System.ComponentModel;
using System.Runtime.CompilerServices;
using CowWorktrees.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}

/// <summary>Theme brushes looked up on the UI thread.</summary>
public static class Theme
{
    private static Brush Get(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public static Brush Caution => Get("SystemFillColorCautionBrush");
    public static Brush Critical => Get("SystemFillColorCriticalBrush");
    public static Brush Success => Get("SystemFillColorSuccessBrush");
    public static Brush Attention => Get("SystemFillColorAttentionBrush");
    public static Brush Neutral => Get("SystemFillColorNeutralBrush");
    public static Brush Secondary => Get("TextFillColorSecondaryBrush");
    public static Brush Primary => Get("TextFillColorPrimaryBrush");
    public static Brush Transparent { get; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static Brush For(Severity s) => s switch
    {
        Severity.Ok => Success,
        Severity.Warning => Caution,
        Severity.Error => Critical,
        Severity.Busy => Attention,
        _ => Neutral,
    };

    public static string Glyph(Severity s) => s switch
    {
        Severity.Ok => "",       // CheckMark
        Severity.Warning => "",  // Warning
        Severity.Error => "",    // ErrorBadge
        Severity.Busy => "",     // Sync
        _ => "",                 // Info
    };

    public static Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A health line as the Base page shows it.</summary>
public sealed class HealthItemViewModel
{
    public HealthItemViewModel(HealthItem item)
    {
        Title = item.Title;
        Value = item.Value;
        Detail = item.Detail;
        Severity = item.Severity;
        Brush = Theme.For(item.Severity);
        Glyph = Theme.Glyph(item.Severity);
    }

    public string Title { get; }
    public string Value { get; }
    public string Detail { get; }
    public Severity Severity { get; }
    public Brush Brush { get; }
    public string Glyph { get; }
}
