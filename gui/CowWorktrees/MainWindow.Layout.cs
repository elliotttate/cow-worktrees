using System.Text.RegularExpressions;
using CowWorktrees.Controls;
using CowWorktrees.Services;
using CowWorktrees.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace CowWorktrees;

/// <summary>Column widths, the details pane, window placement, keyboard shortcuts and the result banner.</summary>
public sealed partial class MainWindow
{
    private double[]? _columnDragStart;
    private double _detailsDragStart;
    private bool _syncingSort;
    private RectInt32? _restoredBounds;
    private bool _placementSaved;
    private OperationViewModel? _bannerOp;
    private DispatcherQueueTimer? _bannerTimer;

    private const double DetailsMinWidth = 280;
    // Narrowest window: the list at its column minimums (about 740), the details pane at its minimum, gaps.
    private const double MinWindowWidth = 1100;
    private const double MinWindowHeight = 640;

    private double ListMinWidth => ColumnLayout.MinimumContentWidth + HeaderGrid.Padding.Left + HeaderGrid.Padding.Right + 2;

    private void InitializeLayout()
    {
        foreach (var sizer in new[] { ColumnSizer0, ColumnSizer1, ColumnSizer2, ColumnSizer3, ColumnSizer4 })
        {
            sizer.DragStarted += (_, _) => _columnDragStart = ColumnLayout.Current.Widths;
            sizer.DragDelta += (s, dx) =>
            {
                if (_columnDragStart != null) ColumnLayout.Current.Drag(((Sizer)s!).Index, _columnDragStart, dx);
            };
            sizer.DragCompleted += (_, _) =>
            {
                _columnDragStart = null;
                SaveColumns();
            };
            sizer.ResetRequested += (s, _) =>
            {
                ColumnLayout.Current.ResetPair(((Sizer)s!).Index);
                SaveColumns();
            };
        }

        DetailsSizer.DragStarted += (_, _) => _detailsDragStart = DetailsColumn.ActualWidth;
        DetailsSizer.DragDelta += (_, dx) =>
        {
            VM.Settings.DetailsWidth = _detailsDragStart - dx;   // dragging left widens the pane
            ApplyDetailsLayout();
        };
        DetailsSizer.DragCompleted += (_, _) =>
        {
            VM.Settings.DetailsWidth = DetailsColumn.Width.Value;
            VM.Settings.Save();
        };
        DetailsSizer.ResetRequested += (_, _) =>
        {
            VM.Settings.DetailsWidth = 0;
            VM.Settings.Save();
            ApplyDetailsLayout();
        };
        ToolTipService.SetToolTip(DetailsSizer, "Drag to resize the details pane · double-click to fit it to the window");

        DetailsToggle.IsChecked = VM.Settings.DetailsVisible;
        ApplyDetailsLayout();

        RootGrid.Loaded += (_, _) =>
        {
            ApplyMinimumSize();
            Content.XamlRoot.Changed += (_, _) => ApplyMinimumSize();   // moved to a monitor with other scaling
        };

        if (!_options.Hidden)
        {
            AppWindow.Changed += (_, e) =>
            {
                if ((e.DidPositionChange || e.DidSizeChange) && AppWindow.IsVisible
                    && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
                    _restoredBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            };
            AppWindow.Closing += (_, _) => SavePlacement();
            Closed += (_, _) => SavePlacement();
        }
    }

    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && Content?.XamlRoot is { } root)
        {
            double scale = root.RasterizationScale;
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinWindowWidth * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinWindowHeight * scale);
        }
    }

    // ---- columns -------------------------------------------------------------------------------------------------

    private void SaveColumns()
    {
        VM.Settings.ColumnWeights = ColumnLayout.Current.Weights;
        VM.Settings.Save();
    }

    private void HeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ColumnLayout.Current.SetContentWidth(e.NewSize.Width - HeaderGrid.Padding.Left - HeaderGrid.Padding.Right);

    /// <summary>
    /// Pads the header so its columns start and end exactly where a row's do. A row sits inside the list item's
    /// own padding, margins and scroll bar space, so this measures the first row instead of guessing those.
    /// </summary>
    private void WorktreeList_LayoutUpdated(object? sender, object e)
    {
        if (WorktreeList.Items.Count == 0 || WorktreeList.ContainerFromIndex(0) is not ListViewItem { ContentTemplateRoot: FrameworkElement row } item
            || row.ActualWidth <= 0 || ListCard.ActualWidth <= 0)
            return;
        // The row's left edge is measured; its right edge comes from the list's own width, never from the row's
        // (a row wider than its slot would otherwise widen the header, which widens the row again).
        var origin = new Windows.Foundation.Point(0, 0);
        double rowLeft = row.TransformToVisual(ListCard).TransformPoint(origin).X;
        double listLeft = WorktreeList.TransformToVisual(ListCard).TransformPoint(origin).X;
        double insetLeft = rowLeft - listLeft;                                   // item margin + padding (+ presenter)
        double insetRight = item.Margin.Right + item.Padding.Right + Math.Max(0, insetLeft - item.Margin.Left - item.Padding.Left);
        double right = ListCard.ActualWidth - (listLeft + WorktreeList.ActualWidth - insetRight);
        var p = HeaderGrid.Padding;
        if (Math.Abs(p.Left - rowLeft) > 0.5 || Math.Abs(p.Right - right) > 0.5)
        {
            if (_options.Unattended)
                AppLog.Write($"layout: card {ListCard.ActualWidth:0.#}, list at {listLeft:0.#} w {WorktreeList.ActualWidth:0.#}, row at {rowLeft:0.#} w {row.ActualWidth:0.#}, item margin {item.Margin}, padding {item.Padding} -> header padding {rowLeft:0.#}/{right:0.#}");
            HeaderGrid.Padding = new Thickness(rowLeft, 0, Math.Max(0, right), 0);
            ColumnLayout.Current.SetContentWidth(HeaderGrid.ActualWidth - rowLeft - Math.Max(0, right));
        }
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<RowSort>(tag, out var column)) return;
        VM.SortByColumn(column);
        SyncSortBox();
    }

    private void SyncSortBox()
    {
        _syncingSort = true;
        SortBox.SelectedIndex = (int)VM.Sort;
        _syncingSort = false;
    }

    private void HeaderGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var reset = new MenuFlyoutItem { Text = "Reset column widths", Icon = new FontIcon { Glyph = "" } };
        reset.Click += (_, _) =>
        {
            ColumnLayout.Current.ResetAll();
            SaveColumns();
        };
        var attention = new MenuFlyoutItem { Text = "Sort by needs attention", Icon = new FontIcon { Glyph = "" } };
        attention.Click += (_, _) =>
        {
            VM.Sort = RowSort.Attention;
            SyncSortBox();
        };
        menu.Items.Add(reset);
        menu.Items.Add(attention);
        menu.ShowAt(HeaderGrid, e.GetPosition(HeaderGrid));
        e.Handled = true;
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<RowFilter>(tag, out var filter))
        {
            SelectTab("worktrees");
            FilterBox.SelectedIndex = (int)filter;   // its handler sets VM.Filter
        }
    }

    // ---- details pane -------------------------------------------------------------------------------------------------

    private void WorktreesBody_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // On a narrow window the search box and the toggle's label give up room for the count chips.
        bool narrow = e.NewSize.Width < 1250;
        SearchBox.Width = narrow ? 170 : 230;
        DetailsToggleLabel.Visibility = Theme.Vis(!narrow);
        ApplyDetailsLayout();
    }

    /// <summary>Width the user dragged to (kept within what the list leaves), or a width that suits the window.</summary>
    private void ApplyDetailsLayout()
    {
        bool show = VM.Settings.DetailsVisible;
        DetailsCard.Visibility = Theme.Vis(show);
        DetailsSizer.Visibility = Theme.Vis(show);
        WorktreesBody.ColumnSpacing = show ? 12 : 0;
        if (!show)
        {
            DetailsColumn.Width = new GridLength(0);
            return;
        }
        double body = WorktreesBody.ActualWidth;
        double want = VM.Settings.DetailsWidth > 0 ? VM.Settings.DetailsWidth : body >= 1450 ? 400 : body >= 1250 ? 340 : 300;
        double max = Math.Max(DetailsMinWidth, body - 12 - ListMinWidth);
        double width = Math.Clamp(want, DetailsMinWidth, max);
        if (Math.Abs(DetailsColumn.Width.Value - width) > 0.5) DetailsColumn.Width = new GridLength(width);
    }

    private void SetDetailsVisible(bool visible)
    {
        VM.Settings.DetailsVisible = visible;
        VM.Settings.Save();
        DetailsToggle.IsChecked = visible;
        ApplyDetailsLayout();
    }

    private void DetailsToggle_Click(object sender, RoutedEventArgs e) => SetDetailsVisible(DetailsToggle.IsChecked == true);

    // ---- window placement ----------------------------------------------------------------------------------------------

    /// <summary>Reopens where the window was last closed, if that spot is still on a screen.</summary>
    private void RestorePlacement()
    {
        var b = VM.Settings.WindowBounds;
        if (b is not { Length: 4 } || b[2] < 300 || b[3] < 200) return;
        var rect = new RectInt32(b[0], b[1], b[2], b[3]);
        var titleStrip = new RectInt32(rect.X + 60, rect.Y, Math.Max(1, rect.Width - 120), 32);
        if (DisplayArea.GetFromRect(titleStrip, DisplayAreaFallback.None) is null) return;
        AppWindow.MoveAndResize(rect);
        _restoredBounds = rect;
        if (VM.Settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
            return;
        }
        // Landing on a monitor with other scaling rescales the window once it is shown; put the size back after that.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => AppWindow.MoveAndResize(rect));
    }

    private void SavePlacement()
    {
        if (_placementSaved || _options.Hidden) return;
        _placementSaved = true;
        try
        {
            bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (_restoredBounds is { } r) VM.Settings.WindowBounds = new[] { r.X, r.Y, r.Width, r.Height };
            VM.Settings.WindowMaximized = maximized;
            VM.Settings.Save();
        }
        catch (Exception ex)
        {
            AppLog.Write($"save placement: {ex.Message}");
        }
    }

    // ---- keyboard ------------------------------------------------------------------------------------------------------

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        SelectTab("worktrees");
        SearchBox.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void NewWorktree_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        New_Click(this, new RoutedEventArgs());
    }

    private void ToggleDetails_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        SetDetailsVisible(!VM.Settings.DetailsVisible);
    }

    private void Tab_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        SelectTab(sender.Key switch { VirtualKey.Number2 => "base", VirtualKey.Number3 => "activity", _ => "worktrees" });
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
    }

    private async void WorktreeList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (VM.SelectedWorktree is not { } row) return;
        if (e.Key == VirtualKey.Delete && row.CanRemove)
        {
            e.Handled = true;
            await ConfirmRemoveAsync(row);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Open(row, Launcher.Explorer);
        }
    }

    private void CopyPathAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        CopyText(VM.SelectedWorktree?.Path);
    }

    // ---- result banner ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// After a create or remove you were watching, go back to the list (the new worktree selected); either way say
    /// how it went in the banner, which stays for failures and fades for successes.
    /// </summary>
    private void OnOperationCompleted(OperationViewModel op)
    {
        bool watching = ActivityPage.Visibility == Visibility.Visible && _watchedOp == op;
        if (op.Succeeded && watching && op.Kind is OperationKind.Create or OperationKind.Remove or OperationKind.Retire or OperationKind.Setup or OperationKind.Uninstall) SelectTab("worktrees");
        if (op.Succeeded && op.Kind == OperationKind.Create && op.ResultLine != null && VM.FindRow(op.ResultLine) is { } created)
        {
            if (!VM.Visible.Contains(created)) FilterBox.SelectedIndex = (int)RowFilter.All;
            WorktreeList.SelectedItem = created;
            WorktreeList.ScrollIntoView(created);
        }
        ShowBanner(op);
    }

    private static readonly Regex ProgressPrefix = new(@"^\s*\d+(\.\d+)?s\s+", RegexOptions.Compiled);

    private static string BannerMessage(OperationViewModel op)
    {
        var lines = op.Lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (op.Cancelled) return lines.LastOrDefault() ?? "";
        if (!op.Succeeded)
        {
            // what happened in plain words when the message is a known one, and always the script's own line
            var f = op.Friendly;
            if (f == null) return lines.LastOrDefault() ?? "";
            string raw = f.Raw.Split('\n')[0];
            return f.Known ? $"{f.Explanation}\n{raw}" : raw;
        }
        switch (op.Kind)
        {
            case OperationKind.Create:
                return op.ResultLine ?? "";
            case OperationKind.Setup:
                return op.Title.StartsWith("Set up", StringComparison.Ordinal)
                    ? "The repository is set up. Restart open Claude Code sessions so they read the new settings."
                    : "The repository was updated. If Claude Code's connection changed, restart open sessions so they read it.";
            case OperationKind.Uninstall:
                return "The repository was removed from the app. Its worktrees and branches are untouched.";
            case OperationKind.Migrate:
                return $"Copied and checked: {op.ResultLine}";
            case OperationKind.Prune:
                int removed = lines.Count(l => ProgressPrefix.Replace(l, "").StartsWith("removed ", StringComparison.Ordinal));
                return removed == 0 ? "Nothing was removed." : $"Removed {Fmt.Plural(removed, "worktree")}.";
            default:
                var last = lines.LastOrDefault(l => ProgressPrefix.IsMatch(l)) ?? lines.LastOrDefault() ?? "";
                return ProgressPrefix.Replace(last, "");
        }
    }

    private void ShowBanner(OperationViewModel op)
    {
        _bannerOp = op;
        OpInfoBar.Severity = op.Succeeded ? InfoBarSeverity.Success : op.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
        OpInfoBar.Title = op.Succeeded ? $"{op.Title}: done in {op.ElapsedText}"
            : op.Cancelled ? $"{op.Title}: stopped"
            : op.Friendly is { Known: true } f ? $"{op.Title}: {f.Title}" : $"{op.Title}: failed";
        OpInfoBar.Message = BannerMessage(op);
        OpInfoBar.IsOpen = true;

        if (_bannerTimer == null)
        {
            _bannerTimer = DispatcherQueue.CreateTimer();
            _bannerTimer.IsRepeating = false;
            _bannerTimer.Interval = TimeSpan.FromSeconds(12);
            _bannerTimer.Tick += (_, _) =>
            {
                if (_bannerOp is { Succeeded: true }) OpInfoBar.IsOpen = false;
            };
        }
        _bannerTimer.Stop();
        if (op.Succeeded) _bannerTimer.Start();
    }

    private void OpInfoAction_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerOp == null) return;
        SelectTab("activity");
        OpsList.SelectedItem = _bannerOp;
    }
}
