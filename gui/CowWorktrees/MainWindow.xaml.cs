using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using CowWorktrees.Services;
using CowWorktrees.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;

namespace CowWorktrees;

public sealed partial class MainWindow : Window
{
    private readonly StartupOptions _options;
    private OperationViewModel? _watchedOp;
    private bool _scrollQueued;
    private bool _dialogOpen;
    private bool _unattendedDone;
    private bool _logTabChanged = true;

    public MainViewModel VM { get; }

    public MainWindow(StartupOptions options)
    {
        _options = options;
        VM = new MainViewModel(DispatcherQueue, _options.Pool);
        InitializeComponent();

        Title = "CoW Worktrees";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); } catch { }
        SizeAndCenter(_options.Size?.Width ?? 1600, _options.Size?.Height ?? 960);

        VM.OperationStarted += OnOperationStarted;
        VM.OperationCompleted += OnOperationCompleted;
        VM.RefreshCompleted += OnRefreshCompleted;
        VM.PropertyChanged += OnViewModelPropertyChanged;
        VM.Operations.CollectionChanged += (_, _) => NoOpsText.Visibility = Theme.Vis(VM.Operations.Count == 0);

        FilterBox.SelectedIndex = (int)VM.Filter;
        SortBox.SelectedIndex = (int)VM.Sort;
        IntervalBox.SelectedItem = IntervalBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == VM.Settings.RefreshIntervalSeconds.ToString())
                                   ?? IntervalBox.Items[1];

        if (_options.Unattended)
            RootGrid.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];

        InitializeLayout();
        UpdatePages();
    }

    public void Start()
    {
        if (_options.Hidden)
        {
            // Off every screen, out of the taskbar and Alt+Tab, and never activated: an unattended check must not
            // take the keyboard from whoever is at the PC.
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Move(new PointInt32(-6000, 60));
            AppWindow.Show(false);
            // x:Bind in a Window initializes when the root loads, which needs Activate(); do it by hand instead.
            Bindings.Update();
        }
        else
        {
            RestorePlacement();
            Activate();
        }
        _ = VM.RefreshAsync();
    }

    public void ShowError(string message) => VM.SetProblem(message);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SizeAndCenter(int width, int height)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int w = Math.Min((int)(width * scale), area.Width - 40);
            int h = Math.Min((int)(height * scale), area.Height - 40);
            AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        }
        catch (Exception ex)
        {
            AppLog.Write($"window size: {ex.Message}");
        }
    }

    // ---- tabs --------------------------------------------------------------------------------------------------

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (WorktreesPage == null) return;   // during InitializeComponent
        UpdatePages();
        if (sender.SelectedItem == TabBase)
        {
            _logTabChanged = true;
            UpdateLogText();
        }
    }

    /// <summary>The selected tab's page, unless there is no usable pool: then the empty state says why.</summary>
    private void UpdatePages()
    {
        if (WorktreesPage == null) return;
        var item = Tabs.SelectedItem;
        bool usable = VM.HasUsablePool;
        WorktreesPage.Visibility = Theme.Vis(usable && item == TabWorktrees);
        BasePage.Visibility = Theme.Vis(usable && item == TabBase);
        ActivityPage.Visibility = Theme.Vis(item == TabActivity);   // past runs stay viewable either way
        EmptyState.Visibility = Theme.Vis(!usable && item != TabActivity);
    }

    private void PoolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PoolBox.SelectedItem is Pool pool) VM.SelectedPool = pool;
    }

    private void OpenEmptyStateFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = VM.Pool is { } p && Directory.Exists(p.Root) ? p.Root : CowHome.Folder;
            Directory.CreateDirectory(folder);
            Launcher.Explorer(folder);
        }
        catch (Exception ex)
        {
            VM.SetProblem(ex.Message);
        }
    }

    private void SelectTab(string name)
    {
        Tabs.SelectedItem = name.ToLowerInvariant() switch
        {
            "base" => TabBase,
            "activity" => TabActivity,
            _ => TabWorktrees,
        };
    }

    private void HealthChip_Click(object sender, RoutedEventArgs e) => SelectTab("base");

    private void RunningOps_Click(object sender, RoutedEventArgs e)
    {
        SelectTab("activity");
        var running = VM.Operations.FirstOrDefault(o => o.IsRunning);
        if (running != null) OpsList.SelectedItem = running;
    }

    // ---- refresh, filter, sort ---------------------------------------------------------------------------------

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await VM.RefreshAsync();

    private void IntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalBox.SelectedItem is ComboBoxItem item && int.TryParse((string)item.Tag, out var seconds) && seconds != VM.Settings.RefreshIntervalSeconds)
            VM.SetInterval(seconds);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => VM.Search = sender.Text;

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => VM.Filter = (RowFilter)Math.Max(0, FilterBox.SelectedIndex);

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSort) VM.Sort = (RowSort)Math.Max(0, SortBox.SelectedIndex);
    }

    private void WorktreeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A move inside Visible (re-sorting) briefly deselects; keep the details on the row until something else is picked.
        if (WorktreeList.SelectedItem is WorktreeViewModel row) VM.SelectedWorktree = row;
        else if (e.AddedItems.Count == 0 && e.RemovedItems.Count > 0 && e.RemovedItems[0] is WorktreeViewModel old && VM.Visible.Contains(old))
            WorktreeList.SelectedItem = old;
        else VM.SelectedWorktree = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HookTail) or nameof(MainViewModel.RefreshTail) or nameof(MainViewModel.BuildTail))
            UpdateLogText();
        else if (e.PropertyName == nameof(MainViewModel.HasUsablePool))
            UpdatePages();
    }

    private void OnRefreshCompleted()
    {
        if (_options.Unattended && !_unattendedDone)
        {
            _unattendedDone = true;
            _ = RunUnattendedAsync();
        }
    }

    // ---- row actions ---------------------------------------------------------------------------------------------

    private WorktreeViewModel? Selected => VM.SelectedWorktree;

    private void OpenExplorer_Click(object sender, RoutedEventArgs e) => Open(Selected, Launcher.Explorer);
    private void OpenTerminal_Click(object sender, RoutedEventArgs e) => Open(Selected, Launcher.Terminal);
    private void OpenEditor_Click(object sender, RoutedEventArgs e) => Open(Selected, p => Launcher.Editor(VM.EditorPath!, p));
    private void CopyPath_Click(object sender, RoutedEventArgs e) => CopyText(Selected?.Path);

    private void Open(WorktreeViewModel? row, Action<string> how)
    {
        if (row == null) return;
        try
        {
            how(row.Path);
        }
        catch (Exception ex)
        {
            VM.SetProblem($"could not open {row.Path}: {ex.Message}");
        }
    }

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private void WorktreeList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is WorktreeViewModel row) Open(row, Launcher.Explorer);
    }

    private void WorktreeList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not WorktreeViewModel row) return;
        WorktreeList.SelectedItem = row;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action act, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        Add("Open in Explorer", "", () => Open(row, Launcher.Explorer));
        Add("Open terminal here", "", () => Open(row, Launcher.Terminal));
        Add($"Open in {VM.EditorName}", "", () => Open(row, p => Launcher.Editor(VM.EditorPath!, p)), VM.EditorAvailable);
        Add("Copy path", "", () => CopyText(row.Path));
        Add("Copy branch", "", () => CopyText(row.Branch), row.Branch != null);
        if (row.CanRemove)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Remove worktree…", "", () => _ = ConfirmRemoveAsync(row));
        }
        menu.ShowAt(WorktreeList, e.GetPosition(WorktreeList));
        e.Handled = true;
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null) await ConfirmRemoveAsync(Selected);
    }

    private void OpenCowRoot_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Pool == null) return;
        try { Launcher.Explorer(VM.Pool.Root); } catch (Exception ex) { VM.SetProblem(ex.Message); }
    }

    // ---- dialogs ---------------------------------------------------------------------------------------------------

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = Content.XamlRoot;
            dialog.Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
            dialog.Resources["ContentDialogMaxWidth"] = 760.0;   // the default 548 clips paths and command lines
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task<bool> EnsureScriptAsync()
    {
        if (VM.ScriptAvailable) return true;
        if (!VM.HasUsablePool) return false;   // the empty state already says why
        await ShowDialogAsync(new ContentDialog
        {
            Title = "cow_worktree.ps1 not found",
            Content = string.IsNullOrEmpty(VM.ScriptPath)
                ? $"{CowHome.PoolsFile} names no toolDir. Run setup.ps1 again from the cow-worktrees folder."
                : $"{VM.ScriptPath} does not exist. Run setup.ps1 again from the cow-worktrees folder, so pools.json names where it is.",
            CloseButtonText = "OK",
        });
        return false;
    }

    private static TextBlock Para(string text, Brush? brush = null, double size = 14)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size };
        if (brush != null) t.Foreground = brush;
        return t;
    }

    private static TextBlock Mono(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Foreground = Theme.Secondary,
    };

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureScriptAsync()) return;

        var refsTask = VM.GetRefsAsync();
        var pool = VM.Pool!;
        var name = new TextBox { Header = $"Name (folder under {pool.Root})", PlaceholderText = "my-feature" };
        var namePreview = Para("", Theme.Secondary, 12);
        var branch = new AutoSuggestBox { Header = "Branch (optional)", PlaceholderText = "new or existing branch; empty for a detached HEAD" };
        var branchNote = Para("", Theme.Secondary, 12);
        var from = new AutoSuggestBox { Header = "From (optional)", PlaceholderText = "commit or ref to start at; empty for the base's commit" };
        var fromNote = Para($"A ref such as HEAD is resolved in {pool.Repo}, as when the script runs there.", Theme.Secondary, 12);
        var command = Mono("");
        var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
        panel.Children.Add(Para($"Block-clones the base of the pool \"{pool.Name}\", then switches the clone to the branch or ref. Progress streams to Activity."));
        panel.Children.Add(name);
        panel.Children.Add(namePreview);
        panel.Children.Add(branch);
        panel.Children.Add(branchNote);
        panel.Children.Add(from);
        panel.Children.Add(fromNote);
        panel.Children.Add(command);

        var dialog = new ContentDialog
        {
            Title = "New copy-on-write worktree",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        List<string> local = new(), remote = new();
        var checkedOut = VM.AllRows.Where(r => r.Branch != null).ToDictionary(r => r.Branch!, r => r.Path, StringComparer.Ordinal);

        void Validate()
        {
            var clean = CowScript.SanitizeName(name.Text);
            bool exists = clean.Length > 0 && Directory.Exists(Path.Combine(pool.Root, clean));
            string? problem = clean.Length == 0 ? (name.Text.Length == 0 ? null : "the name has no usable characters")
                : clean.Equals("base", StringComparison.OrdinalIgnoreCase) ? "\"base\" is the base itself"
                : exists ? $"{Path.Combine(pool.Root, clean)} already exists"
                : null;
            namePreview.Text = problem ?? (clean.Length == 0 ? "" : $"→ {Path.Combine(pool.Root, clean)}");
            namePreview.Foreground = problem != null ? Theme.Critical : Theme.Secondary;

            var b = branch.Text.Trim();
            if (b.Length == 0) branchNote.Text = "No branch: the worktree gets a detached HEAD.";
            else if (checkedOut.TryGetValue(b, out var where))
                branchNote.Text = $"{b} is checked out in {where}; git will refuse to check it out twice, so the switch would fail.";
            else if (local.Contains(b)) branchNote.Text = $"{b} exists: it is used as it is and From is ignored.";
            else branchNote.Text = $"{b} is new: it is created at From.";
            branchNote.Foreground = b.Length > 0 && checkedOut.ContainsKey(b) ? Theme.Caution : Theme.Secondary;

            var args = new List<string> { "-Name", clean.Length == 0 ? "<name>" : clean };
            if (b.Length > 0) { args.Add("-Branch"); args.Add(b); }
            if (from.Text.Trim().Length > 0) { args.Add("-From"); args.Add(from.Text.Trim()); }
            command.Text = VM.CommandLineFor(args);
            dialog.IsPrimaryButtonEnabled = clean.Length > 0 && problem == null;
        }

        static List<string> Matching(IEnumerable<string> items, string text) =>
            items.Where(i => i.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(30).ToList();

        name.TextChanged += (_, _) => Validate();
        branch.TextChanged += (s, a) =>
        {
            if (a.Reason == AutoSuggestionBoxTextChangeReason.UserInput) s.ItemsSource = Matching(local, s.Text);
            Validate();
        };
        from.TextChanged += (s, a) =>
        {
            if (a.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                s.ItemsSource = Matching(new[] { "HEAD" }.Concat(remote).Concat(local), s.Text);
            Validate();
        };
        dialog.Opened += async (_, _) =>
        {
            try { (local, remote) = await refsTask; } catch { }
            Validate();
        };
        Validate();

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        VM.StartCreate(CowScript.SanitizeName(name.Text), branch.Text, from.Text);
    }

    private async Task ConfirmRemoveAsync(WorktreeViewModel row)
    {
        if (!await EnsureScriptAsync()) return;
        if (!row.CanRemove) return;

        var panel = new StackPanel { Spacing = 10, MinWidth = 480 };
        panel.Children.Add(Para($"{row.Path}\nbranch {row.BranchDisplay} · HEAD {row.ShortSha} {row.Subject}"));
        var warnings = new List<string>();
        if (row.IsDirty) warnings.Add($"It has {Fmt.Plural(row.Changed, "changed file")} and {Fmt.Plural(row.Untracked, "untracked path")}. git worktree remove refuses a worktree with changes, and this app never forces it.");
        if (row.IsLocked) warnings.Add($"It is locked ({row.LockReason}); git worktree remove refuses locked worktrees.");
        if (row.AtRisk) warnings.Add($"Its HEAD is detached with {Fmt.Plural(row.Unpushed ?? 0, "commit")} on no branch and no remote. After removal they are reachable only from the reflog.");
        else if (row.HasUnpushed) warnings.Add($"Its branch {row.Branch} has {Fmt.Plural(row.Unpushed ?? 0, "commit")} on no remote. The branch is kept, so they stay in the repository.");
        if (row.LastActivity is { } t && DateTime.Now - t < TimeSpan.FromMinutes(15)) warnings.Add($"It was active {Fmt.Relative(t)}: a Claude or Codex session may still be working in it.");
        foreach (var w in warnings) panel.Children.Add(Para("⚠  " + w, Theme.Caution));
        panel.Children.Add(Para(row.IsAgentBranch
            ? $"The branch {row.Branch} is deleted too if it never gained a commit; otherwise it is kept."
            : "Its branch is kept.", Theme.Secondary, 12));
        panel.Children.Add(Mono(VM.CommandLineFor(new[] { "-Remove", "-Name", row.Name })));

        var dialog = new ContentDialog
        {
            Title = $"Remove worktree {row.Name}?",
            Content = panel,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        VM.StartRemove(row);
    }

    private async void Prune_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureScriptAsync()) return;
        if (VM.PruneRunning)
        {
            SelectTab("activity");
            return;
        }

        var days = new NumberBox
        {
            Header = "Idle for at least (days)",
            Value = VM.DefaultPruneDays,
            Minimum = 0,
            Maximum = 365,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var previewButton = new Button { Content = "Preview again", VerticalAlignment = VerticalAlignment.Bottom };
        var ring = new ProgressRing { Width = 18, Height = 18, IsActive = false, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(days);
        row.Children.Add(previewButton);
        row.Children.Add(ring);
        var output = Mono("");
        output.TextWrapping = TextWrapping.NoWrap;
        var scroller = new ScrollViewer { Content = output, MaxHeight = 300, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto };
        var panel = new StackPanel { Spacing = 10, MinWidth = 560 };
        panel.Children.Add(Para($"Removes worktrees under {VM.Pool?.Root} that have been idle that long, are not locked, have no changed or untracked files, and have no commits that exist nowhere else. The dry run below shows what it would do."));
        panel.Children.Add(row);
        panel.Children.Add(scroller);

        var dialog = new ContentDialog
        {
            Title = "Prune idle copy-on-write worktrees",
            Content = panel,
            PrimaryButtonText = "Prune",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };

        int previewed = -1;
        bool busy = false;
        int DaysValue() => double.IsNaN(days.Value) ? VM.DefaultPruneDays : (int)Math.Round(days.Value);
        async Task PreviewAsync()
        {
            if (busy) return;
            busy = true;
            int d = DaysValue();
            dialog.IsPrimaryButtonEnabled = false;
            ring.IsActive = true;
            previewButton.IsEnabled = false;
            output.Text = $"Running -Prune -OlderThanDays {d} -DryRun …";
            var op = await VM.PrunePreviewAsync(d);
            var lines = op.Lines.Select(l => l.Text).ToList();
            int wouldRemove = lines.Count(l => l.Contains("would remove ", StringComparison.Ordinal));
            output.Text = lines.Count == 0 ? "(no output)" : string.Join("\n", lines);
            previewed = d;
            ring.IsActive = false;
            previewButton.IsEnabled = true;
            busy = false;
            dialog.PrimaryButtonText = !op.Succeeded ? "Preview failed" : wouldRemove == 0 ? "Nothing to prune" : $"Prune {Fmt.Plural(wouldRemove, "worktree")}";
            dialog.IsPrimaryButtonEnabled = op.Succeeded && wouldRemove > 0 && DaysValue() == previewed;
        }
        days.ValueChanged += (_, _) =>
        {
            if (DaysValue() != previewed && !busy)
            {
                dialog.IsPrimaryButtonEnabled = false;
                dialog.PrimaryButtonText = "Prune";
                output.Text = "The number of days changed: preview again.";
            }
        };
        previewButton.Click += async (_, _) => await PreviewAsync();
        dialog.Opened += async (_, _) => await PreviewAsync();

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || previewed < 0) return;

        VM.StartPrune(previewed);
    }

    private async void RefreshBase_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureScriptAsync()) return;
        if (VM.RefreshBaseRunning)
        {
            SelectTab("activity");
            OpsList.SelectedItem = VM.Operations.FirstOrDefault(o => o.IsRunning && o.Kind == OperationKind.RefreshBase);
            return;
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        var config = VM.Pool!.Config!;
        string build = config.Build is { } b
            ? $"then builds it{(b.Configs.Count > 0 ? $" ({string.Join(", ", b.Configs)})" : "")}, which can take a while; worktrees made during the build build in full the first time"
            : "and stops there: this pool has no build configured";
        panel.Children.Add(Para($"Fetches {config.BaseRef}, moves the base of the pool \"{VM.Pool.Name}\" to it, {build}. Moving the base takes it exclusively for a few seconds, so new worktrees wait that long. Output streams to Activity."));
        if (VM.BaseRefreshLockHeld) panel.Children.Add(Para("⚠  A refresh already holds the base lock (perhaps the scheduled task); the script will refuse to start a second one.", Theme.Caution));
        panel.Children.Add(Mono(VM.CommandLineFor(new[] { "-RefreshBase", "-Fetch", "-Build" })));
        var dialog = new ContentDialog
        {
            Title = "Refresh the base?",
            Content = panel,
            PrimaryButtonText = "Refresh base",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        VM.StartRefreshBase();
    }

    // ---- activity ----------------------------------------------------------------------------------------------------

    private void OnOperationStarted(OperationViewModel op)
    {
        if (op.Kind == OperationKind.PrunePreview) return;
        SelectTab("activity");
        OpsList.SelectedItem = op;
    }

    private void OpsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_watchedOp != null) _watchedOp.Lines.CollectionChanged -= OnOpLinesChanged;
        _watchedOp = OpsList.SelectedItem as OperationViewModel;
        VM.SelectedOperation = _watchedOp;
        OpLines.ItemsSource = _watchedOp?.Lines;
        if (_watchedOp != null)
        {
            _watchedOp.Lines.CollectionChanged += OnOpLinesChanged;
            QueueScrollToEnd();
        }
    }

    private void OnOpLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (AutoScrollBox.IsChecked == true) QueueScrollToEnd();
    }

    private void QueueScrollToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;
            if (_watchedOp != null && _watchedOp.Lines.Count > 0) OpLines.ScrollIntoView(_watchedOp.Lines[^1]);
        });
    }

    private void CopyOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_watchedOp == null) return;
        CopyText(_watchedOp.CommandLine + "\n" + string.Join("\n", _watchedOp.Lines.Select(l => l.Text)));
    }

    // ---- logs ----------------------------------------------------------------------------------------------------------

    private void LogTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (LogText == null) return;
        _logTabChanged = true;
        UpdateLogText();
    }

    private void UpdateLogText()
    {
        if (LogText == null || LogTabs == null) return;
        var sel = LogTabs.SelectedItem;
        string text = sel == LogRefresh ? VM.RefreshTail : sel == LogBuild ? VM.BuildTail : VM.HookTail;
        if (LogText.Text == text && !_logTabChanged) return;
        bool atEnd = LogScroller.ScrollableHeight - LogScroller.VerticalOffset < 8;
        LogText.Text = text;
        if (atEnd || _logTabChanged)
        {
            _logTabChanged = false;
            LogScroller.UpdateLayout();
            LogScroller.ChangeView(0, LogScroller.ScrollableHeight, null, true);
        }
    }

    // ---- unattended: --snapshot / --dump / --exit -------------------------------------------------------------------

    private async Task RunUnattendedAsync()
    {
        try
        {
            if (_options.Tab != null) SelectTab(_options.Tab);
            if (_options.Select != null && VM.FindRow(_options.Select) is { } row)
            {
                WorktreeList.SelectedItem = row;
                WorktreeList.ScrollIntoView(row);
            }
            await Task.Delay(600);
            if (_options.SortClick is { } sc && HeaderGrid.Children.OfType<Button>().FirstOrDefault(b => (b.Tag as string) == sc.Column) is { } header)
            {
                for (int i = 0; i < sc.Clicks; i++) Header_Click(header, new RoutedEventArgs());
                AppLog.Write($"unattended: clicked header {sc.Column} x{sc.Clicks} -> sort {VM.Sort}, descending {VM.SortDescending}, order {string.Join(", ", VM.Visible.Select(r => r.Name))}");
            }
            if (_options.Drag is { } drag)
            {
                var sizers = new[] { ColumnSizer0, ColumnSizer1, ColumnSizer2, ColumnSizer3, ColumnSizer4 };
                string before = string.Join("/", ColumnLayout.Current.Widths);
                sizers[Math.Clamp(drag.Index, 0, sizers.Length - 1)].SimulateDrag(drag.Delta);
                AppLog.Write($"unattended: drag edge {drag.Index} by {drag.Delta}: widths {before} -> {string.Join("/", ColumnLayout.Current.Widths)}");
            }
            await Task.Delay(1200);   // layout, and the selected row's unpushed log
            if (_options.SnapshotPath != null) await SaveSnapshotAsync(_options.SnapshotPath);
            if (_options.DumpPath != null)
                await File.WriteAllTextAsync(_options.DumpPath, JsonSerializer.Serialize(VM.Dump(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Write($"unattended: {ex}");
        }
        if (_options.ExitAfter) Close();
    }

    private async Task SaveSnapshotAsync(string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(RootGrid);
        var pixels = (await rtb.GetPixelsAsync()).ToArray();
        var dpi = 96.0 * (Content.XamlRoot?.RasterizationScale ?? 1.0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, dpi, dpi, pixels);
        await encoder.FlushAsync();
    }
}
