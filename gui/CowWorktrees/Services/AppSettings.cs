using System.Text.Json;
using CowWorktrees.Services;

namespace CowWorktrees;

/// <summary>
/// The app's own settings and view state, in gui.json next to pools.json (%LOCALAPPDATA%\cow-worktrees, or
/// COW_WORKTREES_HOME). Which repositories and pools exist is not here: that is pools.json and each pool's
/// cow-config.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Name of the pool shown last; the first pool when it is gone.</summary>
    public string SelectedPool { get; set; } = "";

    /// <summary>0 turns the periodic refresh off.</summary>
    public int RefreshIntervalSeconds { get; set; } = 45;

    /// <summary>How many git processes may run at once across all worktrees.</summary>
    public int MaxParallelGit { get; set; } = 4;

    /// <summary>Editor for "Open in editor"; empty means VS Code if installed, else Cursor.</summary>
    public string EditorPath { get; set; } = "";

    /// <summary>Look for a new version on GitHub once a day. The check never downloads or installs anything by itself.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>A version the person chose to skip: its banner is not shown again.</summary>
    public string SkippedVersion { get; set; } = "";
    /// <summary>The version that ran last, so the first start of a new one can say what changed.</summary>
    public string LastRunVersion { get; set; } = "";
    /// <summary>Shows "Create one for me" for a missing Dev Drive (the new_dev_drive.ps1 script). Off until that script has been proven.</summary>
    public bool ExperimentalDevDrive { get; set; }
    /// <summary>The walkthrough opens by itself on the first start with no repository; this remembers that it did.</summary>
    public bool WalkthroughOffered { get; set; }

    // ---- remembered view state (written by the app) ----------------------------------------------------------
    /// <summary>Relative widths of the list columns (Worktree, Branch, HEAD, Working tree, Unpushed, Last activity).</summary>
    public double[]? ColumnWeights { get; set; }
    /// <summary>Width of the details pane in layout pixels; 0 = fit to the window.</summary>
    public double DetailsWidth { get; set; }
    public bool DetailsVisible { get; set; } = true;
    public string Sort { get; set; } = "Attention";
    public bool SortReversed { get; set; }
    public string Filter { get; set; } = "All";
    /// <summary>Last restored (not maximized) window rectangle in physical pixels: x, y, width, height.</summary>
    public int[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }

    public static string Folder => CowHome.Folder;
    public static string FilePath => Path.Combine(Folder, "gui.json");

    /// <summary>Set for unattended runs (--snapshot / --dump), so a check never changes the user's settings.</summary>
    public static bool ReadOnly { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Write($"settings: could not read {FilePath}: {ex.Message}; using defaults");
        }
        return new AppSettings();
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Write($"settings: could not write {FilePath}: {ex.Message}");
        }
    }
}
