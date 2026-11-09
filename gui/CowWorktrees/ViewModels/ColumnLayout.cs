using Microsoft.UI.Xaml;

namespace CowWorktrees.ViewModels;

/// <summary>
/// Widths of the worktree list's columns, shared by the header and every row (rows bind to
/// <see cref="Current"/>), so dragging a header edge resizes the whole column.
///
/// Columns keep relative weights: the list fills its width, a resize trades width between the two columns
/// either side of the dragged edge, and each column keeps its minimum. The last column (Last activity) takes
/// what is left, which absorbs the few pixels a row is narrower than the header.
/// </summary>
public sealed class ColumnLayout : ObservableObject
{
    public const int Count = 6;
    public static readonly double[] Defaults = { 300, 230, 360, 150, 84, 110 };
    public static readonly double[] Mins = { 150, 100, 120, 110, 64, 90 };

    // After the arrays: static initializers run in source order, and the constructor reads them.
    public static ColumnLayout Current { get; } = new();

    /// <summary>The 4-pixel status bar and the six 12-pixel gaps between the seven grid columns.</summary>
    public const double FixedWidth = 4 + 6 * 12;

    /// <summary>Narrowest list the columns fit in (content width, without the card's padding).</summary>
    public static double MinimumContentWidth => FixedWidth + Mins.Sum();

    private double[] _weights = (double[])Defaults.Clone();
    private double[] _widths = (double[])Defaults.Clone();
    private double _available = Defaults.Sum();

    public GridLength Col0 => new(_widths[0]);
    public GridLength Col1 => new(_widths[1]);
    public GridLength Col2 => new(_widths[2]);
    public GridLength Col3 => new(_widths[3]);
    public GridLength Col4 => new(_widths[4]);

    public double[] Widths => (double[])_widths.Clone();
    public double[] Weights => (double[])_weights.Clone();

    public void SetWeights(double[]? weights)
    {
        if (weights is not { Length: Count } || weights.Any(w => double.IsNaN(w) || w <= 0)) return;
        _weights = (double[])weights.Clone();
        Recompute();
    }

    /// <summary>The header's content width changed (window resize, details pane resize).</summary>
    public void SetContentWidth(double contentWidth)
    {
        double available = Math.Max(Mins.Sum(), contentWidth - FixedWidth);
        if (Math.Abs(available - _available) < 0.5) return;
        _available = available;
        Recompute();
    }

    /// <summary>Drag of the edge between column <paramref name="i"/> and i+1, <paramref name="delta"/> pixels from where it started.</summary>
    public void Drag(int i, double[] startWidths, double delta)
    {
        if (i < 0 || i >= Count - 1) return;
        double pair = startWidths[i] + startWidths[i + 1];
        double left = Math.Clamp(startWidths[i] + delta, Mins[i], pair - Mins[i + 1]);
        var w = (double[])startWidths.Clone();
        w[i] = left;
        w[i + 1] = pair - left;
        _weights = w;
        Recompute();
    }

    /// <summary>Double-click on an edge: the two columns beside it go back to their default share.</summary>
    public void ResetPair(int i)
    {
        if (i < 0 || i >= Count - 1) return;
        var w = (double[])_widths.Clone();
        double pair = w[i] + w[i + 1];
        double share = Defaults[i] / (Defaults[i] + Defaults[i + 1]);
        w[i] = Math.Max(Mins[i], pair * share);
        w[i + 1] = pair - w[i];
        _weights = w;
        Recompute();
    }

    public void ResetAll()
    {
        _weights = (double[])Defaults.Clone();
        Recompute();
    }

    private void Recompute()
    {
        // Share the space by weight; a column that would fall below its minimum is pinned there and the rest
        // is shared again among the others.
        var w = new double[Count];
        var pinned = new bool[Count];
        for (int pass = 0; pass < Count; pass++)
        {
            double free = _available - Enumerable.Range(0, Count).Where(k => pinned[k]).Sum(k => Mins[k]);
            double weightSum = Enumerable.Range(0, Count).Where(k => !pinned[k]).Sum(k => _weights[k]);
            bool changed = false;
            for (int k = 0; k < Count; k++)
            {
                if (pinned[k]) { w[k] = Mins[k]; continue; }
                w[k] = weightSum > 0 ? _weights[k] / weightSum * free : Mins[k];
                if (w[k] < Mins[k]) { pinned[k] = true; changed = true; }
            }
            if (!changed) break;
        }
        for (int k = 0; k < Count; k++) w[k] = Math.Floor(Math.Max(Mins[k], w[k]));
        _widths = w;
        Raise(nameof(Col0), nameof(Col1), nameof(Col2), nameof(Col3), nameof(Col4));
    }
}
