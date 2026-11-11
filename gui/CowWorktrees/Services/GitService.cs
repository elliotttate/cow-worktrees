using System.Globalization;
using System.Text;

namespace CowWorktrees.Services;

/// <summary>One entry of `git worktree list --porcelain`.</summary>
public sealed class WorktreeEntry
{
    public required string Path { get; init; }
    public string? Head { get; set; }
    /// <summary>Short branch name, or null when detached.</summary>
    public string? Branch { get; set; }
    public bool Detached { get; set; }
    public bool Bare { get; set; }
    public bool Locked { get; set; }
    public string? LockReason { get; set; }
    public bool Prunable { get; set; }
    public string? PrunableReason { get; set; }
    public bool IsMain { get; set; }
}

/// <summary>What a folder is to Git: not a repository, a bare one, a linked worktree, or a project's main checkout.</summary>
public sealed class RepoInfo
{
    public bool IsRepo { get; init; }
    /// <summary>The project's main folder (where its .git folder is).</summary>
    public string? MainCheckout { get; init; }
    public string? TopLevel { get; init; }
    public bool IsMainCheckout { get; init; }
    public string? Branch { get; init; }
    public string? SuggestedBaseRef { get; init; }
    public string? Name { get; init; }
    /// <summary>Why this folder cannot be used, in a sentence; null when it can.</summary>
    public string? Problem { get; init; }
}

/// <summary>What one look at a worktree found. Null fields mean "could not tell".</summary>
public sealed class WorktreeProbe
{
    public string? Error { get; set; }
    public int Changed { get; set; }
    public int Untracked { get; set; }
    /// <summary>Files that only look changed to Git (see MarkPhantomsAsync); not counted in <see cref="Changed"/>.</summary>
    public int Phantom { get; set; }
    public List<string> PhantomLines { get; } = new();
    public List<string> StatusLines { get; } = new();
    public string? Upstream { get; set; }
    public bool UpstreamGone { get; set; }
    public int? Ahead { get; set; }
    public int? Behind { get; set; }
    public string? ShortSha { get; set; }
    public string? FullSha { get; set; }
    public string? Subject { get; set; }
    public string? Author { get; set; }
    public DateTimeOffset? CommitTime { get; set; }
    public int? Unpushed { get; set; }
    public DateTime? LastActivity { get; set; }
    public TimeSpan StatusTime { get; set; }
}

/// <summary>
/// Read-only git. Every call runs with --no-optional-locks (and GIT_OPTIONAL_LOCKS=0) so a background
/// `git status` never takes index.lock in a worktree an agent is working in, and never rewrites its index.
/// A semaphore caps how many git processes run at once.
/// </summary>
public sealed class GitService
{
    private readonly SemaphoreSlim _gate;
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string>
    {
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    public GitService(int maxParallel) => _gate = new SemaphoreSlim(Math.Clamp(maxParallel, 1, 32));

    public async Task<ProcessResult> RunAsync(string directory, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = new List<string> { "--no-optional-locks", "-c", "core.quotepath=off", "-c", "color.ui=false", "-C", directory };
            all.AddRange(args);
            return await ProcessRunner.RunAsync("git", all, null, Env, null, timeout ?? TimeSpan.FromMinutes(3), ct, Encoding.UTF8).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string NormalizePath(string p) => p.Replace('/', '\\').TrimEnd('\\');

    public async Task<List<WorktreeEntry>> ListWorktreesAsync(string repo, CancellationToken ct = default)
    {
        var r = await RunAsync(repo, new[] { "worktree", "list", "--porcelain" }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!r.Ok) throw new InvalidOperationException($"git worktree list failed in {repo}: {r.Problem}");
        return ParseWorktreeList(r.StdOut);
    }

    public static List<WorktreeEntry> ParseWorktreeList(string porcelain)
    {
        var list = new List<WorktreeEntry>();
        WorktreeEntry? cur = null;
        foreach (var raw in porcelain.Split('\n').Append(""))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                cur = new WorktreeEntry { Path = NormalizePath(line[9..]), IsMain = list.Count == 0 };
            }
            else if (cur == null)
            {
                continue;
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) cur.Head = line[5..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var b = line[7..];
                cur.Branch = b.StartsWith("refs/heads/", StringComparison.Ordinal) ? b[11..] : b;
            }
            else if (line == "detached") cur.Detached = true;
            else if (line == "bare") cur.Bare = true;
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                cur.Locked = true;
                cur.LockReason = line.Length > 7 ? Unquote(line[7..]) : null;
            }
            else if (line == "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                cur.Prunable = true;
                cur.PrunableReason = line.Length > 9 ? line[9..] : null;
            }
            else if (line.Length == 0)
            {
                list.Add(cur);
                cur = null;
            }
        }
        return list;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Replace("\\\\", "\\").Replace("\\\"", "\"");
        return s;
    }

    /// <summary>Status, HEAD commit, commits not on any remote and last activity of one worktree.</summary>
    public async Task<WorktreeProbe> ProbeAsync(WorktreeEntry e, CancellationToken ct = default)
    {
        var p = new WorktreeProbe();
        if (!Directory.Exists(e.Path))
        {
            p.Error = "folder is missing";
            return p;
        }

        var statusTask = TimedAsync(() => RunAsync(e.Path, new[] { "status", "--porcelain=v1", "-b" }, TimeSpan.FromMinutes(3), ct));
        var logTask = RunAsync(e.Path, new[] { "log", "-1", "--no-show-signature", "--format=%h%x1f%H%x1f%s%x1f%ct%x1f%an" }, TimeSpan.FromSeconds(60), ct);
        var unpushedTask = RunAsync(e.Path, new[] { "rev-list", "--count", "HEAD", "--not", "--remotes" }, TimeSpan.FromSeconds(90), ct);
        await Task.WhenAll(statusTask, logTask, unpushedTask).ConfigureAwait(false);

        var (status, statusTime) = statusTask.Result;
        p.StatusTime = statusTime;
        if (status.Ok)
        {
            ParseStatus(status.StdOut, p);
            try { await MarkPhantomsAsync(e.Path, p, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Write($"phantom check in {e.Path}: {ex.Message}"); }
        }
        else p.Error = "git status: " + status.Problem;

        var log = logTask.Result;
        if (log.Ok)
        {
            var parts = log.StdOut.TrimEnd('\r', '\n').Split('\x1f');
            if (parts.Length >= 5)
            {
                p.ShortSha = parts[0];
                p.FullSha = parts[1];
                p.Subject = parts[2];
                if (long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ct2)) p.CommitTime = DateTimeOffset.FromUnixTimeSeconds(ct2);
                p.Author = parts[4];
            }
        }

        var unpushed = unpushedTask.Result;
        if (unpushed.Ok && int.TryParse(unpushed.StdOut.Trim(), out var n)) p.Unpushed = n;

        p.LastActivity = LastActivity(e.Path, p.StatusLines);
        return p;
    }

    private static async Task<(ProcessResult, TimeSpan)> TimedAsync(Func<Task<ProcessResult>> run)
    {
        var r = await run().ConfigureAwait(false);
        return (r, r.Elapsed);
    }

    /// <summary>Parses `git status --porcelain=v1 -b`: the "## " header and one line per changed or untracked path.</summary>
    public static void ParseStatus(string text, WorktreeProbe p)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseBranchHeader(line[3..], p);
                continue;
            }
            p.StatusLines.Add(line);
            if (line.StartsWith("?? ", StringComparison.Ordinal)) p.Untracked++;
            else if (line.StartsWith("!! ", StringComparison.Ordinal)) { /* ignored files are not shown without --ignored */ }
            else p.Changed++;
        }
    }

    // ---- files that only look changed -----------------------------------------------------------------------
    // A file committed as raw content while .gitattributes gives it a clean filter (Git LFS "files that should
    // have been pointers", CRLF files under `text`) can show as " M" although it is identical to the commit. The
    // scripts call such an entry a phantom (Get-CowStatus in scripts\CowCommon.ps1) and do not count it; this is
    // the same test: " M", the file exists, and its raw hash equals the blob the index holds.

    private const int MaxPhantomCandidates = 400;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Ticks, string Blob, bool Phantom)> PhantomCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task MarkPhantomsAsync(string worktree, WorktreeProbe p, CancellationToken ct)
    {
        // " M path": nothing staged, modified in the working tree. Quoted paths (unusual characters) are left as real changes.
        var candidates = p.StatusLines.Where(l => l.Length > 3 && l[0] == ' ' && l[1] == 'M' && l[2] == ' ' && l[3] != '"')
            .Select(l => l[3..]).Distinct(StringComparer.Ordinal).ToList();
        if (candidates.Count == 0 || candidates.Count > MaxPhantomCandidates) return;

        var phantoms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in candidates.Chunk(40))
        {
            // the blob each path has in the index
            var ls = await RunAsync(worktree, new[] { "--literal-pathspecs", "ls-files", "-s", "-z", "--" }.Concat(chunk), TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!ls.Ok) continue;
            var blob = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rec in ls.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = rec.IndexOf('\t');
                var head = tab > 0 ? rec[..tab].Split(' ') : Array.Empty<string>();
                if (head.Length == 3 && head[2] == "0") blob[rec[(tab + 1)..]] = head[1];
            }

            var toHash = new List<string>();
            foreach (var rel in chunk)
            {
                if (!blob.TryGetValue(rel, out var oid)) continue;
                var full = Path.Combine(worktree, rel.Replace('/', '\\'));
                FileInfo fi;
                try { fi = new FileInfo(full); } catch { continue; }
                if (!fi.Exists) continue;
                if (PhantomCache.TryGetValue(full, out var c) && c.Length == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks && c.Blob == oid)
                {
                    if (c.Phantom) phantoms.Add(rel);
                }
                else toHash.Add(rel);
            }
            if (toHash.Count == 0) continue;

            var hash = await RunAsync(worktree, new[] { "hash-object", "--no-filters", "--" }.Concat(toHash), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            var oids = hash.OutLines;
            if (!hash.Ok || oids.Length != toHash.Count) continue;   // an unreadable file: everything in this group stays a real change
            for (int i = 0; i < toHash.Count; i++)
            {
                var rel = toHash[i];
                var full = Path.Combine(worktree, rel.Replace('/', '\\'));
                bool same = string.Equals(oids[i].Trim(), blob[rel], StringComparison.OrdinalIgnoreCase);
                try
                {
                    var fi = new FileInfo(full);
                    PhantomCache[full] = (fi.Length, fi.LastWriteTimeUtc.Ticks, blob[rel], same);
                }
                catch { }
                if (same) phantoms.Add(rel);
            }
        }
        if (phantoms.Count == 0) return;

        // Not changes: take them out of the count, and list them last, marked.
        var real = p.StatusLines.Where(l => !(l.Length > 3 && l.StartsWith(" M ", StringComparison.Ordinal) && phantoms.Contains(l[3..]))).ToList();
        p.Phantom = p.StatusLines.Count - real.Count;
        p.Changed = Math.Max(0, p.Changed - p.Phantom);
        p.StatusLines.Clear();
        p.StatusLines.AddRange(real);
        p.PhantomLines.AddRange(phantoms.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    // "main...origin/main [ahead 1, behind 2]", "x...origin/x [gone]", "HEAD (no branch)", "No commits yet on x"
    private static void ParseBranchHeader(string s, WorktreeProbe p)
    {
        string? bracket = null;
        int b = s.IndexOf(" [", StringComparison.Ordinal);
        if (b >= 0 && s.EndsWith(']'))
        {
            bracket = s[(b + 2)..^1];
            s = s[..b];
        }
        int dots = s.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0) p.Upstream = s[(dots + 3)..];
        if (bracket != null)
        {
            if (bracket == "gone") p.UpstreamGone = true;
            else
            {
                foreach (var part in bracket.Split(", "))
                {
                    if (part.StartsWith("ahead ", StringComparison.Ordinal) && int.TryParse(part[6..], out var a)) p.Ahead = a;
                    else if (part.StartsWith("behind ", StringComparison.Ordinal) && int.TryParse(part[7..], out var bh)) p.Behind = bh;
                }
            }
        }
        if (p.Upstream != null && !p.UpstreamGone)
        {
            p.Ahead ??= 0;
            p.Behind ??= 0;
        }
    }

    /// <summary>
    /// The latest of: the folder itself, the worktree's index, HEAD and HEAD reflog (commits, switches,
    /// adds), and every changed or untracked path (edits that are not staged yet).
    /// </summary>
    public static DateTime? LastActivity(string path, IReadOnlyList<string> statusLines)
    {
        DateTime best = DateTime.MinValue;
        void Consider(DateTime t)
        {
            if (t.Year > 1990 && t > best) best = t;
        }

        try { Consider(Directory.GetLastWriteTime(path)); } catch { }
        var gitDir = GitDirOf(path);
        if (gitDir != null)
        {
            foreach (var f in new[] { "index", "HEAD", Path.Combine("logs", "HEAD") })
            {
                try
                {
                    var full = Path.Combine(gitDir, f);
                    if (File.Exists(full)) Consider(File.GetLastWriteTime(full));
                }
                catch { }
            }
        }
        foreach (var line in statusLines.Take(2000))
        {
            if (line.Length < 4) continue;
            var rel = line[3..];
            int arrow = rel.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) rel = rel[(arrow + 4)..];
            rel = rel.Trim('"').TrimEnd('/');
            try
            {
                var full = Path.Combine(path, rel.Replace('/', '\\'));
                if (File.Exists(full)) Consider(File.GetLastWriteTime(full));
                else if (Directory.Exists(full)) Consider(Directory.GetLastWriteTime(full));
            }
            catch { }
        }
        return best == DateTime.MinValue ? null : best;
    }

    /// <summary>The worktree's own git directory: .git itself, or where the .git file's "gitdir:" points.</summary>
    public static string? GitDirOf(string worktree)
    {
        try
        {
            var dotGit = Path.Combine(worktree, ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;
            var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line == null) return null;
            var dir = line[7..].Trim().Replace('/', '\\');
            return Path.IsPathRooted(dir) ? dir : Path.GetFullPath(Path.Combine(worktree, dir));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Local and remote-tracking branch names, for the new-worktree suggestions.</summary>
    public async Task<(List<string> Local, List<string> Remote)> ListRefsAsync(string repo)
    {
        var r = await RunAsync(repo, new[] { "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var local = new List<string>();
        var remote = new List<string>();
        foreach (var line in r.OutLines)
        {
            if (line.StartsWith("refs/heads/", StringComparison.Ordinal)) local.Add(line[11..]);
            else if (line.StartsWith("refs/remotes/", StringComparison.Ordinal) && !line.EndsWith("/HEAD", StringComparison.Ordinal)) remote.Add(line[13..]);
        }
        local.Sort(StringComparer.OrdinalIgnoreCase);
        remote.Sort(StringComparer.OrdinalIgnoreCase);
        return (local, remote);
    }

    /// <summary>A setting from the worktree's own config (cow.startCommit, cow.migrateFrom ...); "" when it is not set.</summary>
    public async Task<string> WorktreeConfigAsync(string worktree, string key)
    {
        var r = await RunAsync(worktree, new[] { "config", "--worktree", "--get", key }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return r.Ok ? r.StdOut.Trim() : "";
    }

    /// <summary>
    /// `git worktree remove -f -f`: only for a worktree this app was making and stopped, before anything of the
    /// user's could be in it (the scripts do the same when a clone fails halfway). Force twice: a worktree whose
    /// creation was cut off is still locked by Git itself ("initializing"), and one -f does not remove a locked one.
    /// </summary>
    public Task<ProcessResult> ForceRemoveWorktreeAsync(string repo, string path) =>
        RunAsync(repo, new[] { "worktree", "remove", "-f", "-f", path }, TimeSpan.FromMinutes(20));

    /// <summary>What a folder is to Git, for "Choose your project".</summary>
    public async Task<RepoInfo> InspectAsync(string folder)
    {
        if (!Directory.Exists(folder)) return new RepoInfo { Problem = "That folder does not exist." };
        var common = await RunAsync(folder, new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!common.Ok) return new RepoInfo { Problem = "This folder is not a Git repository. Choose the folder of a project that is under Git (it has a hidden .git folder in it)." };
        var bare = await RunAsync(folder, new[] { "rev-parse", "--is-bare-repository" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (bare.StdOut.Trim() == "true") return new RepoInfo { IsRepo = true, Problem = "This is a bare repository: it has no files checked out. Choose a normal checkout of the project." };

        string commonDir = NormalizePath(common.StdOut.Trim());
        string main = commonDir.EndsWith(@"\.git", StringComparison.OrdinalIgnoreCase) ? commonDir[..^5] : commonDir;
        var top = await RunAsync(folder, new[] { "rev-parse", "--show-toplevel" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        string topLevel = top.Ok ? NormalizePath(top.StdOut.Trim()) : main;
        var head = await RunAsync(main, new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        string? branch = head.Ok ? head.StdOut.Trim() : null;

        // The same order setup.ps1 uses: the remote's default branch, the current branch's upstream, the current branch.
        string? baseRef = null;
        var originHead = await RunAsync(main, new[] { "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (originHead.Ok && originHead.StdOut.Trim().Length > 0) baseRef = originHead.StdOut.Trim();
        if (baseRef == null)
        {
            var upstream = await RunAsync(main, new[] { "rev-parse", "--abbrev-ref", "@{u}" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (upstream.Ok && upstream.StdOut.Trim().Length > 0) baseRef = upstream.StdOut.Trim();
        }
        baseRef ??= branch;

        return new RepoInfo
        {
            IsRepo = true,
            MainCheckout = main,
            TopLevel = topLevel,
            IsMainCheckout = string.Equals(topLevel, main, StringComparison.OrdinalIgnoreCase),
            Branch = branch,
            SuggestedBaseRef = baseRef,
            Name = Path.GetFileName(main.TrimEnd('\\')),
        };
    }

    /// <summary>Up to <paramref name="max"/> commits of HEAD that are on no remote, newest first.</summary>
    public async Task<List<string>> UnpushedLogAsync(string worktree, int max = 25)
    {
        var r = await RunAsync(worktree, new[] { "log", "--no-show-signature", $"-n{max}", "--format=%h  %s  (%cr)", "HEAD", "--not", "--remotes" }, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return r.Ok ? r.OutLines.ToList() : new List<string> { "git log failed: " + r.Problem };
    }
}
