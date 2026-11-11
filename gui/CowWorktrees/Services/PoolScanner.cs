namespace CowWorktrees.Services;

/// <summary>A cow-worktrees setup found on a drive that is not in the app's list of repositories.</summary>
public sealed class FoundPool
{
    public required Pool Pool { get; init; }
    public string Root => Pool.Root;
    public string Name => Pool.Name;
    public string Repo => Pool.Repo;
    /// <summary>Worktrees next to the base.</summary>
    public int Worktrees { get; init; }
    /// <summary>Why it cannot be used as it is, in a sentence; null when it can.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// Looks for setups made before: a folder with cow-config.json and a base folder in it, on a drive where
/// worktrees can live (ReFS, a Dev Drive). After the app was installed again, or its own folder was lost,
/// they are still there, with their worktrees; only the app's list has to be told about them.
/// Looking only reads folder names and each cow-config.json.
/// </summary>
public static class PoolScanner
{
    /// <summary>
    /// The folders to look in, two levels deep: every fixed ReFS drive. COW_SCAN_ROOTS (folders, separated
    /// by semicolons) replaces them, so that a test finds its own setups and none of the real ones.
    /// </summary>
    public static List<string> Roots()
    {
        string? forced = Environment.GetEnvironmentVariable("COW_SCAN_ROOTS");
        if (forced != null) return forced.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).Where(Directory.Exists).ToList();
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady && drive.DriveFormat.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) roots.Add(drive.RootDirectory.FullName);
            }
            catch { /* a drive that cannot be read */ }
        }
        return roots;
    }

    private static bool IsPool(string dir) => File.Exists(Path.Combine(dir, "cow-config.json")) && Directory.Exists(Path.Combine(dir, "base"));

    private static IEnumerable<string> Folders(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Where(d => { var n = Path.GetFileName(d); return n.Length > 0 && n[0] != '$' && !n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase); })
                .ToList();
        }
        catch { return Array.Empty<string>(); }   // no access: not ours to look in
    }

    public static List<FoundPool> Scan(IEnumerable<string> registeredRoots)
    {
        var known = new HashSet<string>(registeredRoots.Select(Pool.FullPath), StringComparer.OrdinalIgnoreCase);
        var found = new List<FoundPool>();
        foreach (var root in Roots())
        {
            foreach (var first in Folders(root))
            {
                // E:\MyGame-cow, or one level down: E:\cow\MyGame. A pool's own folders are worktrees, not pools.
                var candidates = IsPool(first) ? new List<string> { first } : Folders(first).Where(IsPool).ToList();
                foreach (var dir in candidates)
                {
                    string full = Pool.FullPath(dir);
                    if (!known.Add(full)) continue;
                    try
                    {
                        var pool = new Pool(Path.GetFileName(full), full);
                        pool.LoadConfig();
                        string? problem = !pool.IsUsable ? "its settings file cannot be read" + (pool.ConfigError != null ? $" ({pool.ConfigError})" : "")
                            : string.IsNullOrWhiteSpace(pool.Repo) || !Directory.Exists(pool.Repo) ? $"its project folder is not there any more ({pool.Repo})"
                            : !Directory.Exists(Path.Combine(pool.Repo, ".git")) && !File.Exists(Path.Combine(pool.Repo, ".git")) ? $"its project folder is not a Git repository any more ({pool.Repo})"
                            : null;
                        int worktrees = Folders(full).Count(d => !Path.GetFileName(d).Equals("base", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(d, ".git")));
                        found.Add(new FoundPool { Pool = pool, Worktrees = worktrees, Problem = problem });
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"scan: {dir}: {ex.Message}");
                    }
                }
            }
        }
        return found.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
