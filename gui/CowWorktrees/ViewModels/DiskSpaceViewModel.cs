using System.Collections.ObjectModel;
using CowWorktrees.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CowWorktrees.ViewModels;

/// <summary>
/// Column widths of the Disk space list, shared by its header and every row (as <see cref="ColumnLayout"/> is for
/// the worktree list): folder name, the bar, and three numbers.
/// </summary>
public sealed class DiskLayout : ObservableObject
{
    public static DiskLayout Current { get; } = new();

    private double _name = 280, _number = 84;

    public GridLength NameColumn => new(_name);
    public GridLength NumberColumn => new(_number);

    /// <summary>
    /// Fits the columns into <paramref name="available"/>: the name gets two fifths of what the numbers leave,
    /// within limits, and the bar the rest. On a narrow pane the numbers move closer and the bar stays at least short.
    /// </summary>
    public void SetWidth(double available, double spacing)
    {
        if (available <= 0) return;
        double number = available < 620 ? 76 : 84;
        double rest = available - 3 * number - 4 * spacing;
        double name = Math.Clamp(rest * 0.4, 150, 320);
        if (rest - name < 70) name = Math.Max(100, rest - 70);
        if (Math.Abs(name - _name) < 0.5 && number == _number) return;
        _name = name;
        _number = number;
        Raise(nameof(NameColumn), nameof(NumberColumn));
    }
}

/// <summary>One folder of the Disk space list: a bar of own against shared space, and the three numbers.</summary>
public sealed class DiskFolderViewModel
{
    /// <param name="largest">The largest folder's own plus shared space: the full length of a bar.</param>
    /// <param name="hasBase">The base was measured too, so "shares nothing with the base" can be said.</param>
    /// <param name="gone">The folder has been removed since the measurement.</param>
    public DiskFolderViewModel(DiskFolderUsage usage, long largest, bool hasBase, bool gone)
    {
        Usage = usage;
        Name = usage.Name;
        Path = usage.Path;
        KindLabel = usage.IsWorktree ? usage.Kind : "not a worktree";
        // the colours of the worktree list: the pool's own in the accent colour, the main folder green
        KindBrush = !usage.IsWorktree ? Theme.Neutral : usage.InPool ? Theme.Attention : usage.IsMain ? Theme.Success : Theme.Neutral;
        // The usual surprise: a checkout that looks like any other worktree and shares nothing with the base.
        bool surprising = hasBase && usage.IsFullCopy && usage.IsWorktree && !usage.IsMain;
        bool leftover = !usage.IsWorktree && usage.OwnBytes >= DiskSpaceService.FullCopyMinBytes;
        Flag = usage.Error != null ? "not measured" : gone ? "removed" : !usage.IsWorktree ? "not a worktree" : surprising ? "full copy" : null;

        OwnText = usage.Error != null ? "–" : Fmt.Bytes(usage.OwnBytes);
        SharedText = usage.Error != null ? "–" : Fmt.Bytes(usage.SharedBytes);
        SizeText = usage.Error != null ? "–" : Fmt.Bytes(usage.SizeBytes);

        // The bar is as long as the folder is large next to the largest one; the solid part is its own space.
        double scale = Math.Max(1, largest);
        double own = usage.OwnBytes / scale, shared = usage.SharedBytes / scale;
        OwnStar = new GridLength(own, GridUnitType.Star);
        SharedStar = new GridLength(shared, GridUnitType.Star);
        RestStar = new GridLength(Math.Max(0, 1 - own - shared), GridUnitType.Star);
        OwnMin = usage.OwnBytes > 0 ? 3 : 0;   // own space is never so small that it cannot be seen
        OwnBrush = !gone && (surprising || leftover) ? Theme.Caution : Theme.Attention;
        Opacity = gone ? 0.5 : 1;

        if (usage.Error != null)
        {
            Caption = $"Not measured: {usage.Error}. {usage.Path}";
            CaptionBrush = Theme.Caution;
        }
        else if (gone)
        {
            Caption = $"Removed since this was measured. {usage.Path}";
            CaptionBrush = Theme.Secondary;
        }
        else if (!usage.IsWorktree)
        {
            Caption = leftover
                ? $"Git does not know this folder, so it is not a worktree: perhaps a leftover. Deleting it would free {Fmt.Bytes(usage.OwnBytes)}. {usage.Path}"
                : $"Git does not know this folder, so it is not a worktree. {usage.Path}";
            CaptionBrush = leftover ? Theme.Caution : Theme.Secondary;
        }
        else if (surprising)
        {
            Caption = $"A full copy: it shares nothing with the base, so all {Fmt.Bytes(usage.OwnBytes + usage.SharedBytes)} of it take space. A worktree made with plain \"git worktree add\" is like this."
                      + (usage.InPool ? "" : " Move to copy-on-write (Worktrees tab) gets the space back.");
            CaptionBrush = Theme.Caution;
        }
        else
        {
            Caption = usage.IsBase ? $"The base copy: worktrees start as a copy of it and share its files until they change. {usage.Path}"
                : usage.IsMain ? $"Your project's main folder, with Git's history in it. {usage.Path}"
                : usage.Path;
            CaptionBrush = Theme.Secondary;
        }

        var tip = new List<string>
        {
            usage.Path,
            $"Folder size: {Fmt.Bytes(usage.SizeBytes)}, as Explorer shows it (every file counted in full)",
            $"Own space: {Fmt.Bytes(usage.OwnBytes)}. No other folder here uses it; removing this folder frees it",
            $"Shared: {Fmt.Bytes(usage.SharedBytes)}" + (hasBase && !usage.IsBase ? $", of which {Fmt.Bytes(usage.SharedWithBaseBytes)} with the base" : ""),
            $"{usage.Files:N0} files",
        };
        if (usage.RepeatedBytes * 100 > usage.SizeBytes && usage.RepeatedBytes >= DiskSpaceService.FullCopyMinBytes)
            tip.Add($"{Fmt.Bytes(usage.RepeatedBytes)} is counted more than once inside this folder: copies of a file that share their space");
        if (usage.PendingBytes > 0) tip.Add($"{Fmt.Bytes(usage.PendingBytes)} of its own space was saved moments ago and is still on its way to the drive");
        if (usage.Unreadable > 0) tip.Add($"{Fmt.Plural(usage.Unreadable, "file")} could not be read and {(usage.Unreadable == 1 ? "is" : "are")} not counted");
        Tip = string.Join("\n", tip);
    }

    public DiskFolderUsage Usage { get; }
    public string Name { get; }
    public string Path { get; }
    public string KindLabel { get; }
    public Brush KindBrush { get; }
    /// <summary>"full copy", "not a worktree", "removed", "not measured", or null when there is nothing to say.</summary>
    public string? Flag { get; }
    public string OwnText { get; }
    public string SharedText { get; }
    public string SizeText { get; }
    public GridLength OwnStar { get; }
    public GridLength SharedStar { get; }
    public GridLength RestStar { get; }
    public double OwnMin { get; }
    public Brush OwnBrush { get; }
    public double Opacity { get; }
    public string Caption { get; }
    public Brush CaptionBrush { get; }
    public string Tip { get; }

    /// <summary>What screen readers call the row.</summary>
    public override string ToString() => $"{Name}: own {OwnText}, shared {SharedText}, folder size {SizeText}";
}

/// <summary>
/// The Disk space view of the Base tab: the last measurement of the selected repository (kept on disk, so it is
/// there at once), and the running one. A measurement starts only when asked for; a refresh never starts one.
/// </summary>
public sealed class DiskSpaceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherQueue _dispatcher;
    private Pool? _pool;
    private CancellationTokenSource? _cts;
    private int _run;   // bumped when the pool changes, so a measurement of the old one is dropped
    private HashSet<string> _gone = new(StringComparer.OrdinalIgnoreCase);   // measured folders that are no longer there

    public DiskSpaceViewModel(MainViewModel main, DispatcherQueue dispatcher)
    {
        _main = main;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<DiskFolderViewModel> Folders { get; } = new();
    public DiskSpaceResult? Result { get; private set; }
    public bool IsMeasuring { get; private set; }
    public string? Problem { get; private set; }
    public string StaleText { get; private set; } = "";
    public string ProgressText { get; private set; } = "";
    public double ProgressValue { get; private set; }
    public bool ProgressIndeterminate { get; private set; } = true;

    private string DriveName => DiskSpaceService.DriveOf(_pool?.Root ?? "").TrimEnd('\\');

    public bool CanMeasure => !IsMeasuring && _pool is { IsUsable: true } && _main.LastRefresh != null && DriveName.Length > 0;
    public Visibility EmptyVisibility => Theme.Vis(Result == null);
    public Visibility ResultVisibility => Theme.Vis(Result != null);
    public Visibility MeasureAgainVisibility => Theme.Vis(Result != null && !IsMeasuring);
    public Visibility FirstMeasureVisibility => Theme.Vis(Result == null && !IsMeasuring);
    public Visibility StopVisibility => Theme.Vis(IsMeasuring);
    public Visibility ProgressVisibility => Theme.Vis(IsMeasuring);
    public Visibility ProblemVisibility => Theme.Vis(Problem != null);
    public Visibility StaleVisibility => Theme.Vis(StaleText.Length > 0 && !IsMeasuring);

    public string Headline => Result is { } r
        ? $"{Fmt.Bytes(r.FolderBytes)} of folders, held in {Fmt.Bytes(r.HeldBytes)}"
        : "What takes the space?";

    public string MeasuredText => Result is { } r
        ? $"Measured {When(r.MeasuredAt)} in {Fmt.Duration(TimeSpan.FromSeconds(r.Seconds))} · {r.Files:N0} files in {Fmt.Plural(r.Folders.Count, "folder")}"
          + (r.Unreadable > 0 ? $" · {Fmt.Plural(r.Unreadable, "file")} could not be read" : "")
        : DriveName.Length == 0 ? "" : $"Not measured yet · drive {DriveName}";

    public string ExplainText
    {
        get
        {
            if (Result is not { } r) return "";
            string drive = r.Drive.TrimEnd('\\');
            if (!string.Equals(r.FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase))
                return $"Drive {drive} is {r.FileSystem}, not a Dev Drive: folders cannot share files there, so each one takes its full size.";
            if ((r.FolderBytes - r.HeldBytes) * 100 < r.FolderBytes)
                return $"These folders share nothing with each other, so each one takes its full size on drive {drive}. A worktree made by this app shares the files it has not changed with the base. A folder's own space is what removing it gives back.";
            return $"Explorer counts every folder in full, which adds up to {Fmt.Bytes(r.FolderBytes)}. Worktrees share the files they have not changed, so on drive {drive} these folders really take {Fmt.Bytes(r.HeldBytes)}. A folder's own space is what removing it gives back.";
        }
    }

    public string EmptyText =>
        $"Worktrees made by this app share their unchanged files with the base copy, so the folder sizes Explorer shows add up to far more than the drive really holds. Measure looks at where every file sits on drive {DriveName} and shows, for each folder, how much is its own and how much it shares.";

    // ---- the drive: these folders, everything else on it, free ------------------------------------------------
    private long OtherBytes => Result is { } r ? Math.Max(0, r.DriveTotalBytes - r.DriveFreeBytes - r.HeldBytes) : 0;
    public GridLength DriveHeldStar => new(Result?.HeldBytes ?? 0, GridUnitType.Star);
    public GridLength DriveOtherStar => new(OtherBytes, GridUnitType.Star);
    public GridLength DriveFreeStar => new(Math.Max(1, Result?.DriveFreeBytes ?? 1), GridUnitType.Star);
    public string DriveHeldText => Result is { } r ? $"These folders {Fmt.Bytes(r.HeldBytes)}" : "";
    public string DriveOtherText => Result is { } r ? $"Everything else on {r.Drive.TrimEnd('\\')} {Fmt.Bytes(OtherBytes)}" : "";
    public string DriveFreeText => Result is { } r ? $"Free {Fmt.Bytes(r.DriveFreeBytes)} of {Fmt.Bytes(r.DriveTotalBytes)}" : "";
    public Visibility DriveVisibility => Theme.Vis(Result is { DriveTotalBytes: > 0 });

    private static string When(DateTime t)
    {
        var today = DateTime.Today;
        string day = t.Date == today ? "today" : t.Date == today.AddDays(-1) ? "yesterday" : t.ToString(t.Year == today.Year ? "MMM d" : "MMM d yyyy");
        return $"{day} at {t:HH:mm}";
    }

    // ---- which repository ---------------------------------------------------------------------------------------

    /// <summary>The repository shown changed (or went away): stop what was running and show that one's last measurement.</summary>
    public void SetPool(Pool? pool)
    {
        if (ReferenceEquals(pool, _pool)) return;
        _pool = pool;
        _run++;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        IsMeasuring = false;   // the measurement of the other repository is dropped when it returns
        Result = pool == null ? null : DiskSpaceService.LoadLast(pool);
        Problem = null;
        StaleText = "";
        Rebuild();
    }

    /// <summary>After the worktree list was read: give every row its numbers, and say what changed since the measurement.</summary>
    public void Sync()
    {
        var rows = _main.AllRows;
        var byPath = Result?.Folders.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            row.ApplyDisk(byPath?.GetValueOrDefault(row.Path), Result?.MeasuredAt, Result?.HasBase == true);

        string stale = "";
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Result is { } r && _pool != null && _main.LastRefresh != null)
        {
            int added = rows.Count(w => !w.IsMissing && w.OnPoolDrive && !byPath!.ContainsKey(w.Path));
            foreach (var f in r.Folders)
                if (f.Error == null && !Directory.Exists(f.Path)) gone.Add(f.Path);
            var parts = new List<string>();
            if (added > 0) parts.Add($"{Fmt.Plural(added, "worktree")} {(added == 1 ? "was" : "were")} made");
            if (gone.Count > 0) parts.Add($"{Fmt.Plural(gone.Count, "folder")} {(gone.Count == 1 ? "was" : "were")} removed");
            if (parts.Count > 0) stale = $"Since this was measured, {string.Join(" and ", parts)}. Measure again for the numbers as they are now.";
        }
        if (!gone.SetEquals(_gone))
        {
            _gone = gone;
            BuildFolders();
        }
        if (stale != StaleText)
        {
            StaleText = stale;
            Raise(nameof(StaleText), nameof(StaleVisibility));
        }
        Raise(nameof(CanMeasure), nameof(MeasuredText));
    }

    private void BuildFolders()
    {
        Folders.Clear();
        if (Result is not { } r) return;
        long largest = r.Folders.Count == 0 ? 1 : r.Folders.Max(f => f.OwnBytes + f.SharedBytes);
        // the base first, then whatever would give back the most
        foreach (var f in r.Folders.OrderByDescending(f => f.IsBase).ThenByDescending(f => f.OwnBytes).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            Folders.Add(new DiskFolderViewModel(f, largest, r.HasBase, _gone.Contains(f.Path)));
    }

    private void Rebuild()
    {
        _gone.Clear();
        BuildFolders();
        RaiseAll();
        Sync();
    }

    private void RaiseAll() => Raise(nameof(Result), nameof(IsMeasuring), nameof(Problem), nameof(CanMeasure), nameof(EmptyVisibility),
        nameof(ResultVisibility), nameof(MeasureAgainVisibility), nameof(FirstMeasureVisibility), nameof(StopVisibility), nameof(ProgressVisibility),
        nameof(ProblemVisibility), nameof(StaleVisibility), nameof(Headline), nameof(MeasuredText), nameof(ExplainText), nameof(EmptyText),
        nameof(DriveHeldStar), nameof(DriveOtherStar), nameof(DriveFreeStar), nameof(DriveHeldText), nameof(DriveOtherText), nameof(DriveFreeText),
        nameof(DriveVisibility), nameof(ProgressText), nameof(ProgressValue), nameof(ProgressIndeterminate));

    // ---- measuring ----------------------------------------------------------------------------------------------

    /// <summary>The checkouts of the repository on the pool's drive, and whatever else sits in the pool root.</summary>
    private List<DiskFolder> FoldersToMeasure(Pool pool)
    {
        var list = _main.AllRows.Where(r => !r.IsMissing && r.OnPoolDrive)
            .Select(r => new DiskFolder(r.Path, r.Name, r.KindLabel, r.IsBase, IsWorktree: true, IsMain: r.Kind == WorktreeKind.Main, InPool: r.IsBase || r.IsCow)).ToList();
        list.AddRange(DiskSpaceService.OtherFoldersIn(pool.Root, list.Select(f => f.Path)));
        return list;
    }

    /// <summary>Measures now. Returns when it is done, was stopped, or failed; the outcome is in the properties.</summary>
    public async Task MeasureAsync()
    {
        if (!CanMeasure || _pool is not { } pool) return;
        int run = _run;
        var folders = FoldersToMeasure(pool);
        var cts = new CancellationTokenSource();
        _cts = cts;
        int expected = Result?.Files ?? 0;
        IsMeasuring = true;
        Problem = null;
        ProgressText = $"Looking at {Fmt.Plural(folders.Count, "folder")} on drive {DriveName}…";
        ProgressIndeterminate = expected == 0;
        ProgressValue = 0;
        RaiseAll();
        try
        {
            var result = await Task.Run(() => DiskSpaceService.Measure(pool.Root, folders, p => _dispatcher.TryEnqueue(() =>
            {
                if (run != _run || !IsMeasuring || cts.IsCancellationRequested) return;
                ProgressText = $"{p.Files:N0} files so far · {p.FoldersDone} of {Fmt.Plural(p.FolderCount, "folder")} done · now in {p.Folder}";
                if (expected > 0) ProgressValue = Math.Min(97, 100.0 * p.Files / expected);
                Raise(nameof(ProgressText), nameof(ProgressValue));
            }), cts.Token));
            if (run != _run) return;   // another repository is shown by now
            Result = result;
            DiskSpaceService.SaveLast(pool, result);
            AppLog.Write($"disk space: {pool.Name}: {result.Folders.Count} folders, {result.Files} files, {result.FolderBytes} bytes of folders held in {result.HeldBytes}, {result.Seconds:0.0} s");
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            if (run == _run) Problem = Result == null ? "Stopped. Nothing was measured." : "Stopped. The numbers below are from the measurement before.";
        }
        catch (Exception ex)
        {
            AppLog.Write($"disk space: {ex}");
            if (run == _run) Problem = $"The measurement did not work: {(ex as AggregateException)?.InnerException?.Message ?? ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            if (run == _run)
            {
                IsMeasuring = false;
                Rebuild();
            }
        }
    }

    public void Stop()
    {
        if (!IsMeasuring) return;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        ProgressText = "Stopping…";
        Raise(nameof(ProgressText));
    }

    /// <summary>The last measurement, for --dump; null when there is none.</summary>
    public object? Dump() => Result is not { } r ? null : new
    {
        r.Root,
        r.Drive,
        r.FileSystem,
        r.MeasuredAt,
        r.Seconds,
        r.ClusterSize,
        r.FolderBytes,
        r.HeldBytes,
        r.DriveFreeBytes,
        r.DriveTotalBytes,
        r.Files,
        r.Unreadable,
        r.HasBase,
        Headline,
        Explain = ExplainText,
        Measured = MeasuredText,
        Stale = StaleText,
        Problem,
        Folders = Folders.Select(f => new
        {
            f.Name,
            f.Path,
            Kind = f.KindLabel,
            f.Usage.IsBase,
            f.Usage.IsWorktree,
            f.Usage.IsMain,
            f.Usage.InPool,
            f.Usage.SizeBytes,
            f.Usage.LogicalBytes,
            f.Usage.OwnBytes,
            f.Usage.SharedBytes,
            f.Usage.SharedWithBaseBytes,
            f.Usage.PendingBytes,
            f.Usage.Files,
            f.Usage.Unreadable,
            f.Usage.Error,
            f.Flag,
            Own = f.OwnText,
            Shared = f.SharedText,
            Size = f.SizeText,
            f.Caption,
        }),
    };
}
