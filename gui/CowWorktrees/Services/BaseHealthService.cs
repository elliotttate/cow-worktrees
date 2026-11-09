using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CowWorktrees.Services;

public enum Severity { Ok, Info, Warning, Error, Busy }

public sealed record HealthItem(string Title, string Value, string Detail, Severity Severity);

public sealed class BaseHealthSnapshot
{
    public List<HealthItem> Items { get; } = new();
    public string HookTail { get; set; } = "";
    public string RefreshTail { get; set; } = "";
    public string BuildTail { get; set; } = "";
    public bool RefreshRunning { get; set; }
    public bool BuildRunning { get; set; }
    public string? BaseHead { get; set; }
}

/// <summary>
/// Everything about a pool's base (&lt;root&gt;\base) that decides whether new worktrees are quick and current: its
/// commit against the pool's baseRef, its build stamp, the lock files, the scheduled refresh task, free space,
/// and the logs.
/// </summary>
public sealed class BaseHealthService
{
    private readonly Pool _pool;
    private readonly GitService _git;

    public BaseHealthService(Pool pool, GitService git)
    {
        _pool = pool;
        _git = git;
    }

    public async Task<BaseHealthSnapshot> ReadAsync(CancellationToken ct = default)
    {
        var snap = new BaseHealthSnapshot();
        string root = _pool.Root;
        string basePath = _pool.BasePath;
        string repo = _pool.Repo;
        var config = _pool.Config!;

        // ---- base commit and the ref it follows ----------------------------------------------------------
        string trackedRef = config.BaseRef;

        var headTask = _git.RunAsync(basePath, new[] { "log", "-1", "--no-show-signature", "--format=%H%x1f%h%x1f%s%x1f%ct" }, TimeSpan.FromSeconds(30), ct);
        var refTask = _git.RunAsync(repo, new[] { "rev-parse", "--verify", "--quiet", trackedRef + "^{commit}" }, TimeSpan.FromSeconds(30), ct);
        var commonTask = _git.RunAsync(repo, new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" }, TimeSpan.FromSeconds(30), ct);

        string? baseSha = null;
        var head = await headTask.ConfigureAwait(false);
        if (head.Ok)
        {
            var p = head.StdOut.TrimEnd('\r', '\n').Split('\x1f');
            if (p.Length >= 4)
            {
                baseSha = p[0];
                string when = long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                    ? Fmt.Relative(DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime) : "";
                snap.Items.Add(new HealthItem("Base commit", $"{p[1]}  {p[2]}", $"{basePath} · committed {when}", Severity.Info));
            }
        }
        else
        {
            snap.Items.Add(new HealthItem("Base commit", "unreadable", $"{basePath}: {head.Problem}", Severity.Error));
        }
        snap.BaseHead = baseSha;

        string? fetchAge = null;
        var common = await commonTask.ConfigureAwait(false);
        if (common.Ok)
        {
            var fetchHead = Path.Combine(GitService.NormalizePath(common.StdOut.Trim()), "FETCH_HEAD");
            try { if (File.Exists(fetchHead)) fetchAge = Fmt.Relative(File.GetLastWriteTime(fetchHead)); } catch { }
        }
        string fetchNote = fetchAge == null ? "as of the last fetch" : $"as of the last fetch, {fetchAge}";

        {
            var rr = await refTask.ConfigureAwait(false);
            string? refSha = rr.Ok ? rr.StdOut.Trim() : null;
            if (refSha == null)
            {
                snap.Items.Add(new HealthItem("Tracked ref", trackedRef, $"not a commit in {repo} (fetch it, or fix baseRef in cow-config.json)", Severity.Error));
            }
            else if (baseSha == null)
            {
                snap.Items.Add(new HealthItem("Tracked ref", $"{trackedRef} at {refSha[..9]}", "the base's commit could not be read", Severity.Warning));
            }
            else if (refSha == baseSha)
            {
                snap.Items.Add(new HealthItem("Tracked ref", $"base is at {trackedRef}", $"{refSha[..9]}, {fetchNote}", Severity.Ok));
            }
            else
            {
                var lr = await _git.RunAsync(repo, new[] { "rev-list", "--left-right", "--count", $"{baseSha}...{refSha}" }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                var parts = lr.Ok ? lr.StdOut.Trim().Split('\t', ' ') : Array.Empty<string>();
                if (parts.Length >= 2 && int.TryParse(parts[0], out var onlyBase) && int.TryParse(parts[^1], out var onlyRef))
                {
                    string value = onlyBase == 0
                        ? $"base is {onlyRef} commit{(onlyRef == 1 ? "" : "s")} behind {trackedRef}"
                        : $"base and {trackedRef} have diverged ({onlyBase} / {onlyRef})";
                    snap.Items.Add(new HealthItem("Tracked ref", value, $"{trackedRef} is {refSha[..9]}, {fetchNote}. -RefreshBase -Fetch -Build moves the base.", Severity.Warning));
                }
                else
                {
                    snap.Items.Add(new HealthItem("Tracked ref", $"{trackedRef} at {refSha[..9]}", $"base at {baseSha[..9]}", Severity.Warning));
                }
            }
        }

        // ---- build stamp ---------------------------------------------------------------------------------
        snap.BuildRunning = MutexHeld(_pool.BuildMutexName);
        var buildFile = Path.Combine(root, "base-build.json");
        try
        {
            if (File.Exists(buildFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(buildFile));
                var r = doc.RootElement;
                string commit = r.TryGetProperty("commit", out var c) ? c.GetString() ?? "" : "";
                string builtAt = r.TryGetProperty("builtAt", out var b) ? b.GetString() ?? "" : "";
                string configs = r.TryGetProperty("configs", out var cf) && cf.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", cf.EnumerateArray().Select(x => x.GetString())) : "";
                string minutes = r.TryGetProperty("minutes", out var m) && m.ValueKind == JsonValueKind.Number ? $"{m.GetDouble():0.#} min" : "";
                string when = DateTime.TryParse(builtAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var bt) ? $"{bt:MMM d HH:mm} ({Fmt.Relative(bt)})" : builtAt;
                string shortCommit = commit.Length >= 9 ? commit[..9] : commit;
                if (baseSha != null && commit == baseSha)
                    snap.Items.Add(new HealthItem("Build output", $"current: {configs} built {when}", $"commit {shortCommit}, took {minutes}. New worktrees start with a warm build.", Severity.Ok));
                else
                    snap.Items.Add(new HealthItem("Build output", $"stale: built for {shortCommit}", $"the base is at {baseSha?[..9] ?? "?"}; new worktrees build in full until -RefreshBase -Build finishes", snap.BuildRunning ? Severity.Busy : Severity.Warning));
            }
            else if (config.Build == null && !snap.BuildRunning)
            {
                snap.Items.Add(new HealthItem("Build output", "no build configured",
                    "cow-config.json has no \"build\" section, so the base is not built and -RefreshBase -Build only moves it", Severity.Info));
            }
            else
            {
                snap.Items.Add(new HealthItem("Build output", snap.BuildRunning ? "building now" : "no finished build",
                    snap.BuildRunning ? $"a base build holds {_pool.BuildMutexName}; base-build.json appears when it succeeds"
                                      : "base-build.json is missing: the last build failed or never ran, so new worktrees build in full",
                    snap.BuildRunning ? Severity.Busy : Severity.Warning));
            }
        }
        catch (Exception ex)
        {
            snap.Items.Add(new HealthItem("Build output", "unreadable", $"{buildFile}: {ex.Message}", Severity.Error));
        }

        // ---- locks -----------------------------------------------------------------------------------------
        var lockDir = Path.Combine(root, ".locks");
        var refreshFile = Path.Combine(lockDir, "refresh");
        var lockNotes = new List<string>();
        Severity lockSev = Severity.Ok;
        string lockValue = "free";
        if (File.Exists(refreshFile))
        {
            var (alive, desc) = Holder(refreshFile);
            if (alive)
            {
                snap.RefreshRunning = true;
                lockSev = Severity.Busy;
                lockValue = "a refresh is moving the base";
                lockNotes.Add($"refresh lock held by {desc}; new worktrees wait for it");
            }
            else
            {
                lockNotes.Add($"stale refresh lock ({desc}): its holder is gone, so the script ignores it");
            }
        }
        try
        {
            var readersDir = Path.Combine(lockDir, "readers");
            if (Directory.Exists(readersDir))
            {
                int live = 0, dead = 0;
                foreach (var f in Directory.GetFiles(readersDir))
                {
                    if (Holder(f).Alive) live++; else dead++;
                }
                if (live > 0)
                {
                    if (lockSev == Severity.Ok) { lockSev = Severity.Busy; lockValue = $"{live} worktree creation{(live == 1 ? "" : "s")} cloning"; }
                    lockNotes.Add($"{live} creation{(live == 1 ? "" : "s")} hold the base shared");
                }
                if (dead > 0) lockNotes.Add($"{dead} stale reader file{(dead == 1 ? "" : "s")} (ignored)");
            }
        }
        catch { }
        snap.Items.Add(new HealthItem("Base lock", lockValue, lockNotes.Count == 0 ? $"no holders in {lockDir}" : string.Join("; ", lockNotes), lockSev));

        // ---- scheduled refresh -------------------------------------------------------------------------------
        snap.Items.Add(ReadScheduledTask(_pool.TaskFolder, _pool.TaskName));

        // ---- free space ----------------------------------------------------------------------------------------
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            if (drive.IsReady)
            {
                double freeGb = drive.AvailableFreeSpace / 1e9, totalGb = drive.TotalSize / 1e9;
                double floor = config.FallbackMinFreeGB;
                bool refs = string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase);
                var sev = !refs ? Severity.Error : freeGb < floor ? Severity.Warning : Severity.Ok;
                string detail = !refs
                    ? $"{drive.DriveFormat}, not ReFS: files on it cannot be block-cloned"
                    : freeGb < floor
                        ? $"below the pool's fallbackMinFreeGB ({floor:0} GB). ReFS, label \"{drive.VolumeLabel}\"."
                        : $"ReFS, label \"{drive.VolumeLabel}\". Clones share blocks with the base until files change.";
                snap.Items.Add(new HealthItem($"Drive {drive.Name.TrimEnd('\\')}", $"{freeGb:N0} GB free of {totalGb:N0} GB", detail, sev));
            }
        }
        catch { }

        // ---- logs ----------------------------------------------------------------------------------------------
        snap.HookTail = Tail(Path.Combine(root, "hook.log"));
        snap.RefreshTail = Tail(Path.Combine(root, "refresh.log"));
        snap.BuildTail = Tail(Path.Combine(root, "base-build.log"));
        return snap;
    }

    private static bool MutexHeld(string name)
    {
        try
        {
            if (Mutex.TryOpenExisting(name, out var m))
            {
                m.Dispose();
                return true;
            }
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;   // exists, made by an elevated process
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A lock file holds "&lt;pid&gt; &lt;process start ticks&gt;"; the holder is alive when that very process still runs.</summary>
    private static (bool Alive, string Desc) Holder(string file)
    {
        try
        {
            string text;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                text = sr.ReadLine() ?? "";
            var parts = text.Trim().Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var ticks))
                return (false, "unreadable holder");
            var since = new DateTime(ticks);
            try
            {
                using var p = Process.GetProcessById(pid);
                bool same;
                try { same = p.StartTime.Ticks == ticks; }
                catch { same = true; }   // cannot read an elevated process's start time; assume it is the holder
                return (same, $"pid {pid}, started {since:HH:mm}");
            }
            catch (ArgumentException)
            {
                return (false, $"pid {pid} has exited");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static HealthItem ReadScheduledTask(string folderPath, string name)
    {
        string taskPath = $@"{folderPath}\{name}";
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return new HealthItem("Scheduled refresh", "unknown", "Task Scheduler is unavailable", Severity.Warning);
            service = Activator.CreateInstance(type);
            dynamic svc = service!;
            svc.Connect();
            dynamic folder = svc.GetFolder(folderPath);
            dynamic task = folder.GetTask(name);
            int state = (int)task.State;
            bool enabled = (bool)task.Enabled;
            DateTime last = (DateTime)task.LastRunTime;
            DateTime next = (DateTime)task.NextRunTime;
            int result = (int)task.LastTaskResult;
            string stateText = state switch { 1 => "disabled", 2 => "queued", 3 => "ready", 4 => "running", _ => "unknown" };
            string lastText = last.Year < 2000 ? "never run" : $"last run {last:MMM d HH:mm} ({Fmt.Relative(last)}), result {ResultText(result)}";
            string nextText = next.Year < 2000 ? "no next run" : $"next {next:MMM d HH:mm}";
            var sev = state == 4 ? Severity.Busy
                : !enabled || state == 1 ? Severity.Warning
                : last.Year >= 2000 && result != 0 && result != 0x41301 && result != 0x41303 ? Severity.Warning
                : Severity.Ok;
            return new HealthItem("Scheduled refresh", $"{stateText} · {nextText}", $"{taskPath}: {lastText}", sev);
        }
        // Through dynamic, a missing task or folder arrives as FileNotFoundException / DirectoryNotFoundException, not COMException.
        catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x80070003)
        {
            return new HealthItem("Scheduled refresh", "not registered", $"{taskPath} does not exist; setup.ps1 can register a daily refresh of the base", Severity.Info);
        }
        catch (Exception ex)
        {
            return new HealthItem("Scheduled refresh", "unreadable", $"{taskPath}: {ex.Message}", Severity.Warning);
        }
        finally
        {
            if (service != null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
    }

    private static string ResultText(int result) => result switch
    {
        0 => "success (0x0)",
        0x41301 => "still running (0x41301)",
        0x41303 => "has not run yet (0x41303)",
        0x41306 => "terminated (0x41306)",
        _ => $"0x{result:X}",
    };

    /// <summary>
    /// The last lines of a log that someone may be writing to. Reads the BOM to tell UTF-16 (PowerShell 5.1's
    /// Out-File / ">>") from UTF-8 / ANSI, then decodes only the end of the file.
    /// </summary>
    public static string Tail(string path, int maxLines = 120, int maxBytes = 256 * 1024, int maxLineLength = 700)
    {
        try
        {
            if (!File.Exists(path)) return $"({path} does not exist)";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bom = new byte[3];
            int got = fs.Read(bom, 0, 3);
            Encoding enc = Encoding.UTF8;
            int headerLen = 0;
            if (got >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) { enc = Encoding.Unicode; headerLen = 2; }
            else if (got >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) { enc = Encoding.BigEndianUnicode; headerLen = 2; }
            else if (got >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) { headerLen = 3; }

            long start = Math.Max(headerLen, fs.Length - maxBytes);
            if (headerLen == 2 && (start - headerLen) % 2 != 0) start++;
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            var text = enc.GetString(buf, 0, read);
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (start > headerLen && lines.Count > 0) lines.RemoveAt(0);   // partial first line
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            var tail = lines.Skip(Math.Max(0, lines.Count - maxLines))
                .Select(l => l.Length > maxLineLength ? l[..maxLineLength] + " …" : l);
            var result = string.Join("\n", tail);
            return result.Length == 0 ? "(empty)" : result;
        }
        catch (Exception ex)
        {
            return $"(could not read {path}: {ex.Message})";
        }
    }
}
