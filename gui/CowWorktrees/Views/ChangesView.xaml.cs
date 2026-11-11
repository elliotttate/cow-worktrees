using CowWorktrees.Services;
using CowWorktrees.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace CowWorktrees.Views;

/// <summary>Picks the row template: a file's header, a line range, a line (context, added, removed), a note.</summary>
public sealed class DiffRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FileHeader { get; set; }
    public DataTemplate? FileEnd { get; set; }
    public DataTemplate? Hunk { get; set; }
    public DataTemplate? ContextLine { get; set; }
    public DataTemplate? AddedLine { get; set; }
    public DataTemplate? RemovedLine { get; set; }
    public DataTemplate? Note { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => item switch
    {
        LineRow { Kind: DiffLineKind.Added } => AddedLine!,
        LineRow { Kind: DiffLineKind.Removed } => RemovedLine!,
        LineRow => ContextLine!,
        HunkRow => Hunk!,
        FileHeaderRow => FileHeader!,
        FileEndRow => FileEnd!,
        _ => Note!,
    };

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// Colours a line of code in a TextBlock: its comments, strings, numbers and keywords, and, on the brush set
/// for it, the part that changed. Rows are reused as the list scrolls, so every new line redraws the marks.
/// Marks are text highlighters, which must not overlap: the line is cut where a token or the changed part
/// starts and ends, and every piece goes to the highlighter of its colour.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty BrushProperty =
        DependencyProperty.RegisterAttached("Brush", typeof(Brush), typeof(Highlight), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty LineProperty =
        DependencyProperty.RegisterAttached("Line", typeof(LineRow), typeof(Highlight), new PropertyMetadata(null, Changed));

    public static Brush? GetBrush(DependencyObject o) => (Brush?)o.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject o, Brush? value) => o.SetValue(BrushProperty, value);
    public static LineRow? GetLine(DependencyObject o) => (LineRow?)o.GetValue(LineProperty);
    public static void SetLine(DependencyObject o, LineRow? value) => o.SetValue(LineProperty, value);

    private const int Plain = 5;   // after the five token kinds

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var marks = tb.TextHighlighters;
        if (marks.Count > 0) marks.Clear();
        if (GetLine(tb) is not { } line) return;
        var changed = GetBrush(tb);
        int from = line.HighlightStart, to = changed == null ? from : from + line.HighlightLength;
        var tokens = line.Tokens;
        if (to <= from && tokens.Count == 0) return;

        var slots = new TextHighlighter?[12];   // a colour (five kinds of token, or plain text), in the changed part or not
        void Add(int colour, bool inChanged, int start, int end)
        {
            if (end <= start) return;
            int slot = colour * 2 + (inChanged ? 1 : 0);
            var h = slots[slot];
            if (h == null)
            {
                h = new TextHighlighter
                {
                    Foreground = colour == Plain ? tb.Foreground : DiffPalette.Token((TokenKind)colour),
                    Background = inChanged ? changed! : Theme.Transparent,
                };
                slots[slot] = h;
                marks.Add(h);
            }
            h.Ranges.Add(new TextRange { StartIndex = start, Length = end - start });
        }
        int at = 0;
        foreach (var t in tokens)
        {
            Add(Plain, true, Math.Max(at, from), Math.Min(t.Start, to));   // plain text before the token, where it changed
            int end = t.Start + t.Length, colour = (int)t.Kind;
            Add(colour, false, t.Start, Math.Min(end, from));
            Add(colour, true, Math.Max(t.Start, from), Math.Min(end, to));
            Add(colour, false, Math.Max(t.Start, to), end);
            at = end;
        }
        Add(Plain, true, Math.Max(at, from), to);
    }
}

/// <summary>
/// What changed in a worktree, over the whole window: the changed files on the left, every changed line on
/// the right, in colour. Reading only.
/// </summary>
public sealed partial class ChangesView : UserControl
{
    private readonly string? _editorPath;
    private ScrollViewer? _scroller;
    private bool _syncing;
    private bool _fullScreen;
    private int _lastTop = -1;
    /// <summary>Until then the file chosen on the left stays chosen, whatever is at the top while the diff scrolls to it.</summary>
    private DateTime _holdSelectionUntil;

    public ChangesViewModel VM { get; }

    public event Action? Closed;
    /// <summary>F11 or the button: the main window switches between its normal size and the whole screen.</summary>
    public event Action? FullScreenRequested;

    public ChangesView(ChangesViewModel vm, string? editorPath)
    {
        VM = vm;
        _editorPath = editorPath;
        InitializeComponent();
        VM.RowsChanged += OnRowsChanged;
        // Room under the last file, so that any file can be scrolled to the top of the list.
        DiffList.SizeChanged += (_, e) => DiffList.Padding = new Thickness(0, 0, 0, Math.Max(28, e.NewSize.Height - 140));
        Loaded += (_, _) =>
        {
            HookScroller();
            DiffList.Focus(FocusState.Programmatic);
        };
    }

    /// <summary>Set by the main window, so the button says what it will do.</summary>
    public bool IsFullScreen
    {
        get => _fullScreen;
        set
        {
            _fullScreen = value;
            FullScreenIcon.Glyph = value ? "" : "";
            FullScreenLabel.Text = value ? "Leave full screen" : "Full screen";
        }
    }

    // ---- keys ----------------------------------------------------------------------------------------------------------

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool typing = e.OriginalSource is TextBox;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (typing && FilterBox.Text.Length > 0) FilterBox.Text = "";
                else if (_fullScreen) FullScreenRequested?.Invoke();
                else Closed?.Invoke();
                e.Handled = true;
                break;
            case VirtualKey.F5:
                _ = VM.ReloadAsync();
                e.Handled = true;
                break;
            case VirtualKey.F11:
                FullScreenRequested?.Invoke();
                e.Handled = true;
                break;
            case VirtualKey.F when ctrl:
                FilterBox.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
            case VirtualKey.Down when alt:
            case VirtualKey.J when !typing && !ctrl:
                StepFile(1);
                e.Handled = true;
                break;
            case VirtualKey.Up when alt:
            case VirtualKey.K when !typing && !ctrl:
                StepFile(-1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>The next or previous file, from the one at the top of the diff.</summary>
    private void StepFile(int by)
    {
        if (VM.Files.Count == 0) return;
        int at = FileList.SelectedItem is ChangeFileItem cur ? VM.Files.IndexOf(cur) : -1;
        int next = Math.Clamp(at + by, 0, VM.Files.Count - 1);
        if (next == at) return;
        FileList.SelectedItem = VM.Files[next];   // SelectionChanged scrolls the diff to it
    }

    // ---- header --------------------------------------------------------------------------------------------------------

    private void Back_Click(object sender, RoutedEventArgs e) => Closed?.Invoke();

    private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScopeBox.SelectedItem is ChangeScope scope && !ReferenceEquals(scope, VM.Scope)) VM.Scope = scope;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = VM.ReloadAsync();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke();

    private void FoldAll_Click(object sender, RoutedEventArgs e) => VM.SetAllCollapsed(VM.AnyExpanded);

    private void UpdateFoldButton()
    {
        bool any = VM.AnyExpanded;
        FoldLabel.Text = any ? "Fold all" : "Unfold all";
        FoldIcon.Glyph = any ? "" : "";
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => VM.Filter = sender.Text;

    // ---- the two lists follow each other ---------------------------------------------------------------------------------

    private void HookScroller()
    {
        if (_scroller != null) return;
        _scroller = FindChild<ScrollViewer>(DiffList);
        if (_scroller != null) _scroller.ViewChanged += (_, _) => SelectFileAtTop();
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>While the diff scrolls, the file whose lines are at the top is the selected one on the left.</summary>
    private void SelectFileAtTop()
    {
        if (DateTime.UtcNow < _holdSelectionUntil || DiffList.ItemsPanelRoot is not ItemsStackPanel panel) return;
        int top = panel.FirstVisibleIndex;
        if (top < 0 || top >= VM.Rows.Count || top == _lastTop) return;
        _lastTop = top;
        var file = VM.Rows[top].File;
        if (ReferenceEquals(FileList.SelectedItem, file)) return;
        _syncing = true;
        try
        {
            FileList.SelectedItem = file;
            FileList.ScrollIntoView(file);
        }
        finally { _syncing = false; }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || FileList.SelectedItem is not ChangeFileItem file) return;
        if (file.IsCollapsed && file.File.Kind != ChangeKind.Phantom) VM.Reveal(file);   // rebuilds the rows and scrolls to it
        else ScrollTo(file.Header);
    }

    private void OnRowsChanged(DiffRow? anchor)
    {
        _lastTop = -1;
        UpdateFoldButton();
        if (anchor == null)
        {
            // New rows from the top: the first file is the one being looked at.
            _syncing = true;
            try { FileList.SelectedItem = VM.Files.FirstOrDefault(); } finally { _syncing = false; }
            return;
        }
        // The list was just given new rows: wait until it has laid them out.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ScrollTo(anchor));
    }

    private void ScrollTo(DiffRow row)
    {
        HookScroller();
        _holdSelectionUntil = DateTime.UtcNow.AddMilliseconds(700);
        try { DiffList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading); } catch { /* the row left the list meanwhile */ }
    }

    /// <summary>For screenshots and tests: bring the file whose path ends with <paramref name="path"/> to the top.</summary>
    public bool ShowFile(string path)
    {
        string want = path.Replace('\\', '/').TrimEnd('/');
        // the file itself, or for a new folder the first file in it
        var file = VM.Files.FirstOrDefault(f => f.Path.EndsWith(want, StringComparison.OrdinalIgnoreCase))
            ?? VM.Files.FirstOrDefault(f => f.Path.StartsWith(want + "/", StringComparison.OrdinalIgnoreCase));
        if (file == null) return false;
        FileList.SelectedItem = file;
        return true;
    }

    // ---- one file's buttons --------------------------------------------------------------------------------------------

    private static ChangeFileItem? FileOf(object sender) => (sender as FrameworkElement)?.Tag as ChangeFileItem;

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is { } f) VM.Toggle(f);
    }

    private void NoteAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NoteRow note) note.Run?.Invoke();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is not { } f) return;
        string full = VM.FullPath(f);
        try
        {
            if (!File.Exists(full)) { Launcher.Explorer(Path.GetDirectoryName(full) ?? VM.Row.Path); return; }
            if (_editorPath != null) Launcher.Editor(_editorPath, full);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Write($"open {full}: {ex.Message}"); }
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is not { } f) return;
        string full = VM.FullPath(f);
        try
        {
            if (File.Exists(full)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + full } });
            else Launcher.Explorer(Path.GetDirectoryName(full) is { } dir && Directory.Exists(dir) ? dir : VM.Row.Path);
        }
        catch (Exception ex) { AppLog.Write($"show {full}: {ex.Message}"); }
    }

    private static void Copy(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is { } f) Copy(f.Path);
    }

    private void CopyFullPath_Click(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is { } f) Copy(VM.FullPath(f));
    }

    private void CopyPatch_Click(object sender, RoutedEventArgs e)
    {
        if (FileOf(sender) is { } f) Copy(ChangesViewModel.PatchText(f));
    }
}
