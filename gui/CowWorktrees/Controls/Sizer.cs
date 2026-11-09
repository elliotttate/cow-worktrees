using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace CowWorktrees.Controls;

/// <summary>
/// A vertical drag handle with the left-right resize cursor: a 1-pixel line that lights up on hover inside a
/// wider transparent hit area. Reports the drag as a total horizontal offset from where it started (in layout
/// pixels, so it is independent of monitor scaling), and a double-click as a reset.
/// </summary>
public sealed class Sizer : Grid
{
    private readonly Rectangle _line;
    private bool _dragging;
    private double _startX;

    public event EventHandler? DragStarted;
    public event EventHandler<double>? DragDelta;
    public event EventHandler? DragCompleted;
    public event EventHandler? ResetRequested;

    /// <summary>Which edge this is (the owner decides what it means).</summary>
    public int Index { get; set; }

    public Sizer()
    {
        Width = 12;
        Background = new SolidColorBrush(Colors.Transparent);   // hit-testable across the whole width
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        _line = new Rectangle
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            Fill = Res("DividerStrokeColorDefaultBrush"),
        };
        Children.Add(_line);
        ToolTipService.SetToolTip(this, "Drag to resize · double-click to reset");
        AutomationProperties.SetName(this, "Resize");

        PointerEntered += (_, _) => Highlight(true);
        PointerExited += (_, _) => { if (!_dragging) Highlight(false); };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => End(e.Pointer);
        PointerCaptureLost += (_, e) => End(null);
        DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            ResetRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Vertical inset of the visible line (the hit area stays full height).</summary>
    public Thickness LineMargin
    {
        get => _line.Margin;
        set => _line.Margin = value;
    }

    private static Brush Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : new SolidColorBrush(Colors.Gray);

    private void Highlight(bool on)
    {
        _line.Fill = on ? Res("AccentFillColorDefaultBrush") : Res("DividerStrokeColorDefaultBrush");
        _line.Width = on ? 2 : 1;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = CapturePointer(e.Pointer);
        if (!_dragging) return;
        _startX = e.GetCurrentPoint(null).Position.X;
        Highlight(true);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        DragDelta?.Invoke(this, e.GetCurrentPoint(null).Position.X - _startX);
        e.Handled = true;
    }

    /// <summary>For unattended checks: a whole drag of <paramref name="dx"/> pixels through the same events a mouse raises.</summary>
    internal void SimulateDrag(double dx)
    {
        DragStarted?.Invoke(this, EventArgs.Empty);
        DragDelta?.Invoke(this, dx);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void End(Pointer? pointer)
    {
        if (!_dragging) return;
        _dragging = false;
        if (pointer != null) ReleasePointerCapture(pointer);
        Highlight(false);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}
