namespace CowWorktrees.Services;

/// <summary>
/// The cow-worktrees folder this app belongs to: the one holding setup.ps1 and scripts\cow_worktree.ps1.
/// Found by walking up from the exe (app\CowWorktrees.exe in the zip and the installed copy, gui\publish in a
/// checkout); when the exe sits somewhere else, the folder the last setup.ps1 run recorded in pools.json.
/// </summary>
public static class ToolFolder
{
    private static string? _fromExe;
    private static bool _searched;

    /// <summary>The folder found next to (above) the exe, or null.</summary>
    public static string? NextToExe
    {
        get
        {
            if (_searched) return _fromExe;
            _searched = true;
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    if (IsToolFolder(dir.FullName))
                    {
                        _fromExe = dir.FullName.TrimEnd('\\');
                        break;
                    }
                }
            }
            catch
            {
                // an unreadable parent folder: fall back to pools.json
            }
            return _fromExe;
        }
    }

    public static bool IsToolFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "setup.ps1")) && File.Exists(Path.Combine(dir, "scripts", "cow_worktree.ps1"));

    /// <summary>The folder to run scripts from: next to the exe first, else <paramref name="registeredToolDir"/> when it is real.</summary>
    public static string? Resolve(string? registeredToolDir) =>
        NextToExe ?? (IsToolFolder(registeredToolDir) ? registeredToolDir!.TrimEnd('\\') : null);

    public static string? Script(string? toolFolder, params string[] relative) =>
        toolFolder == null ? null : Path.Combine(new[] { toolFolder }.Concat(relative).ToArray());

    /// <summary>The installer writes this file; only an installed copy updates itself.</summary>
    public static bool IsInstalled(string? toolFolder) => toolFolder != null && File.Exists(Path.Combine(toolFolder, "installed.marker"));

    /// <summary>
    /// Why this folder is a bad home for the tool, or null. Setup stores absolute paths to the scripts (in the
    /// Claude Code hooks, the Codex instructions and the daily task), so the folder must stay where it is.
    /// </summary>
    public static string? RiskyLocation(string? toolFolder)
    {
        if (string.IsNullOrEmpty(toolFolder)) return null;
        try
        {
            string full = Path.GetFullPath(toolFolder).TrimEnd('\\') + "\\";
            bool Under(string? parent) =>
                !string.IsNullOrEmpty(parent) && full.StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

            if (Under(Path.GetTempPath())) return "it is inside the temporary files folder, which Windows empties";
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Under(Path.Combine(profile, "Downloads"))) return "it is inside Downloads, where it is easy to delete or move by accident";
            if (full.Contains(".zip\\", StringComparison.OrdinalIgnoreCase)) return "it is running from inside a zip file";
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && root.StartsWith(@"\\", StringComparison.Ordinal)) return "it is on a network share";
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.DriveType == DriveType.Removable) return "it is on a removable drive";
                if (drive.DriveType == DriveType.Network) return "it is on a network drive";
            }
        }
        catch
        {
            // cannot tell: not a reason to refuse
        }
        return null;
    }
}
