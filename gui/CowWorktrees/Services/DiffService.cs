using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CowWorktrees.Services;

/// <summary>
/// The two sides of a comparison. <see cref="From"/> is a commit ("HEAD" when null); <see cref="To"/> is a
/// commit, or null for the files as they are in the folder now (then new, untracked files are listed too).
/// </summary>
public sealed record DiffSpec(string? From, string? To)
{
    public bool ToWorkingTree => To == null;
}

public sealed class CommitInfo
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset When { get; init; }
}

public sealed class ChangeSet
{
    public List<ChangedFile> Files { get; } = new();
    /// <summary>Why nothing could be read, in a sentence; null when it worked.</summary>
    public string? Problem { get; set; }
    /// <summary>New files that are not listed because there are very many.</summary>
    public int UntrackedLeftOut { get; set; }
    /// <summary>The changed lines stopped being read at the limit; later files have counts but no lines.</summary>
    public bool LinesCutOff { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int Added => Files.Sum(f => f.Added);
    public int Deleted => Files.Sum(f => f.Deleted);
}

/// <summary>
/// Reads what changed in a worktree, for the Changes view. Read-only, like <see cref="GitService"/>: every
/// call runs with --no-optional-locks, so looking never takes index.lock in a folder an agent is working in.
/// </summary>
public sealed class DiffService
{
    /// <summary>Lines of one file's diff that are kept; the rest is counted and offered as "open the file".</summary>
    public const int MaxLinesPerFile = 6000;
    /// <summary>Lines kept over the whole comparison. Reading stops there: a 2 GB diff must not fill memory.</summary>
    public const int MaxLinesTotal = 200_000;
    public const int MaxUntrackedListed = 2000;
    private const int MaxUntrackedRead = 400;
    private const long MaxUntrackedBytes = 768 * 1024;
    /// <summary>The commit a diff starts from when there is no commit before it.</summary>
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private readonly GitService _git;

    public DiffService(GitService git) => _git = git;

    private static string[] Revs(DiffSpec spec) => spec.To == null ? new[] { spec.From ?? "HEAD" } : new[] { spec.From ?? "HEAD", spec.To };

    public async Task<ChangeSet> LoadAsync(string worktree, DiffSpec spec, IReadOnlyCollection<string>? phantoms = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var set = new ChangeSet();
        try
        {
            var revs = Revs(spec);
            var common = new[] { "diff", "--no-ext-diff", "-M" };
            var statusTask = _git.RunAsync(worktree, common.Concat(new[] { "--name-status", "-z" }).Concat(revs).Append("--"), TimeSpan.FromMinutes(3), ct);
            var numTask = _git.RunAsync(worktree, common.Concat(new[] { "--numstat", "-z" }).Concat(revs).Append("--"), TimeSpan.FromMinutes(3), ct);
            // The changed lines are read at the same time as the lists, not after them.
            var patchTask = ReadPatchAsync(worktree, common.Concat(new[] { "-U3" }).Concat(revs).Append("--"), ct);
            Task<ProcessResult>? indexTask = spec.ToWorkingTree
                ? _git.RunAsync(worktree, new[] { "status", "--porcelain=v1", "-z", "--untracked-files=no" }, TimeSpan.FromMinutes(3), ct) : null;
            Task<ProcessResult>? othersTask = spec.ToWorkingTree
                ? _git.RunAsync(worktree, new[] { "ls-files", "--others", "--exclude-standard", "-z" }, TimeSpan.FromMinutes(3), ct) : null;
            var all = new List<Task> { statusTask, numTask };
            if (indexTask != null) all.Add(indexTask);
            if (othersTask != null) all.Add(othersTask);
            await Task.WhenAll(all).ConfigureAwait(false);

            var status = statusTask.Result;
            if (!status.Ok)
            {
                set.Problem = "Git could not compare these: " + status.Problem;
                try { await patchTask.ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { }
                return set;
            }
            var byPath = new Dictionary<string, ChangedFile>(StringComparer.Ordinal);
            ParseNameStatus(Z(status.StdOut), set.Files, byPath);
            if (numTask.Result.Ok) ParseNumstat(Z(numTask.Result.StdOut), byPath);
            if (indexTask is { Result.Ok: true }) ParseIndexState(Z(indexTask.Result.StdOut), byPath);

            // Files that only look changed: no lines to show, and not counted.
            if (spec.ToWorkingTree && phantoms is { Count: > 0 })
            {
                foreach (var name in phantoms)
                {
                    if (!byPath.TryGetValue(name, out var f) || f.Kind != ChangeKind.Modified) continue;
                    f.Kind = ChangeKind.Phantom;
                    f.Added = f.Deleted = 0;
                    f.Note = "Only looks changed to Git: the file is identical to the commit. It was committed as raw content while a filter (Git LFS, line endings) is set for it.";
                }
            }

            var (parsed, cutOff) = await patchTask.ConfigureAwait(false);
            set.LinesCutOff = cutOff;
            foreach (var one in parsed)
            {
                if (!byPath.TryGetValue(one.Path, out var file)) continue;
                file.Hunks.AddRange(one.Hunks);
                file.LinesLeftOut = one.LinesLeftOut;
                if (one.Binary) file.Binary = true;
            }
            foreach (var f in set.Files)
            {
                if (f.Kind == ChangeKind.Phantom) f.Hunks.Clear();
                else if (f.Binary) f.Note ??= "A binary file: there are no lines to compare.";
                else if (f.Hunks.Count == 0 && f.Added + f.Deleted > 0) f.Note ??= "This file's lines were not read: the comparison is very large. Open the file to see it.";
                else if (f.Hunks.Count == 0) f.Note ??= f.Kind == ChangeKind.Renamed ? "Moved without changes." : "No lines differ (only the file's mode or type changed).";
            }

            if (othersTask is { Result.Ok: true })
                await Task.Run(() => AddUntracked(worktree, Z(othersTask.Result.StdOut), set), ct).ConfigureAwait(false);

            set.Files.Sort((a, b) =>
            {
                int pa = a.Kind == ChangeKind.Phantom ? 1 : 0, pb = b.Kind == ChangeKind.Phantom ? 1 : 0;
                return pa != pb ? pa - pb : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Write($"changes in {worktree}: {ex}");
            set.Problem = "The changes could not be read: " + ex.Message;
        }
        set.Elapsed = sw.Elapsed;
        return set;
    }

    /// <summary>The records of a -z listing. The process runner adds a line break at the end; it is not part of a name.</summary>
    private static string[] Z(string stdout) => stdout.TrimEnd('\n', '\r').Split('\0');

    private static ChangeKind KindOf(char c) => c switch
    {
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'R' or 'C' => ChangeKind.Renamed,
        'T' => ChangeKind.TypeChanged,
        'U' => ChangeKind.Conflict,
        _ => ChangeKind.Modified,
    };

    // "M\0path\0", "R100\0old\0new\0"
    public static void ParseNameStatus(string[] rec, List<ChangedFile> files, Dictionary<string, ChangedFile> byPath)
    {
        for (int i = 0; i + 1 < rec.Length; i++)
        {
            string st = rec[i];
            if (st.Length == 0) continue;
            ChangedFile f;
            if ((st[0] == 'R' || st[0] == 'C') && i + 2 < rec.Length)
            {
                f = new ChangedFile { Path = rec[i + 2], OldPath = st[0] == 'R' ? rec[i + 1] : null, Kind = st[0] == 'R' ? ChangeKind.Renamed : ChangeKind.Added };
                i += 2;
            }
            else
            {
                f = new ChangedFile { Path = rec[i + 1], Kind = KindOf(st[0]) };
                i += 1;
            }
            if (byPath.TryAdd(f.Path, f)) files.Add(f);
        }
    }

    // "12\t3\tpath\0", "-\t-\tbinary\0", and for a rename "1\t1\t\0old\0new\0"
    public static void ParseNumstat(string[] rec, Dictionary<string, ChangedFile> byPath)
    {
        for (int i = 0; i < rec.Length; i++)
        {
            var parts = rec[i].Split('\t');
            if (parts.Length < 3) continue;
            string path = parts[2];
            if (path.Length == 0 && i + 2 < rec.Length)
            {
                path = rec[i + 2];
                i += 2;
            }
            if (!byPath.TryGetValue(path, out var f)) continue;
            if (parts[0] == "-") f.Binary = true;
            else
            {
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a);
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d);
                f.Added = a;
                f.Deleted = d;
            }
        }
    }

    // `git status --porcelain=v1 -z`: "XY path\0", and for a rename "XY new\0old\0"
    private static void ParseIndexState(string[] rec, Dictionary<string, ChangedFile> byPath)
    {
        for (int i = 0; i < rec.Length; i++)
        {
            string r = rec[i];
            if (r.Length < 4) continue;
            char x = r[0], y = r[1];
            string path = r[3..];
            if (x == 'R' || x == 'C') i++;
            if (!byPath.TryGetValue(path, out var f)) continue;
            if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D')) f.Kind = ChangeKind.Conflict;
            else if (x != ' ' && x != '?') f.Staged = true;
        }
    }

    /// <summary>Runs the patch through the parser as it arrives and stops Git at the total limit. CutOff says it was stopped there.</summary>
    private static async Task<(List<DiffParser.ParsedFile> Files, bool CutOff)> ReadPatchAsync(string worktree, IEnumerable<string> args, CancellationToken ct)
    {
        var parser = new DiffParser(MaxLinesPerFile);
        bool cutOff = false;
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--no-optional-locks", "-c", "core.quotepath=off", "-c", "color.ui=false", "-C", worktree }.Concat(args)) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        try { proc.StandardInput.Close(); } catch { }
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                parser.Feed(line);
                if (parser.TotalLines >= MaxLinesTotal)
                {
                    cutOff = true;
                    break;
                }
            }
        }
        finally
        {
            if (cutOff || ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); } catch { }
            try { await errTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
        }
        ct.ThrowIfCancellationRequested();
        return (parser.Finish(), cutOff);
    }

    /// <summary>New files Git does not track: listed one by one, and text files of a sensible size are read so their lines show as added.</summary>
    private static void AddUntracked(string worktree, string[] names, ChangeSet set)
    {
        var list = names.Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        set.UntrackedLeftOut = Math.Max(0, list.Count - MaxUntrackedListed);
        int read = 0;
        foreach (var name in list.Take(MaxUntrackedListed))
        {
            var f = new ChangedFile { Path = name, Kind = ChangeKind.Untracked };
            set.Files.Add(f);
            string full = Path.Combine(worktree, name.Replace('/', '\\'));
            try
            {
                var fi = new FileInfo(full);
                if (!fi.Exists) { f.Note = "The file is gone again."; continue; }
                if (fi.Length == 0) { f.Note = "An empty file."; continue; }
                if (fi.Length > MaxUntrackedBytes) { f.Note = $"A new file of {Size(fi.Length)}: too large to show here. Open the file to see it."; f.Binary = LooksBinary(full); continue; }
                // Its first 8 KB tell a binary file: only text files are read whole, and only they count toward the limit.
                if (LooksBinary(full))
                {
                    f.Binary = true;
                    f.Note = $"A new binary file of {Size(fi.Length)}.";
                    continue;
                }
                if (read >= MaxUntrackedRead) { f.Note = "A new file. Its lines are not shown because there are very many new files; open the file to see it."; continue; }
                read++;
                byte[] bytes = File.ReadAllBytes(full);
                string text = new UTF8Encoding(false).GetString(bytes);
                if (text.Length > 0 && text[0] == '﻿') text = text[1..];
                var lines = text.Split('\n');
                int count = lines.Length - (text.EndsWith('\n') ? 1 : 0);
                f.Added = count;
                var hunk = new DiffHunk { OldStart = 0, OldCount = 0, NewStart = 1, NewCount = count };
                int keep = Math.Min(count, MaxLinesPerFile);
                for (int i = 0; i < keep; i++)
                    hunk.Lines.Add(new DiffLine { Kind = DiffLineKind.Added, NewNo = i + 1, Text = DiffParser.Expand(lines[i].TrimEnd('\r')) });
                f.LinesLeftOut = count - keep;
                f.Hunks.Add(hunk);
            }
            catch (Exception ex)
            {
                f.Note = "A new file that could not be read: " + ex.Message;
            }
        }
    }

    private static bool LooksBinary(string full)
    {
        try
        {
            using var s = File.OpenRead(full);
            var buf = new byte[8000];
            int n = s.Read(buf, 0, buf.Length);
            return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
        }
        catch { return false; }
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} bytes";

    /// <summary>Commits of HEAD after <paramref name="after"/> (or, without it, the ones on no remote), newest first.</summary>
    public async Task<List<CommitInfo>> CommitsAsync(string worktree, string? after, int max = 60, CancellationToken ct = default)
    {
        var args = new List<string> { "log", "--no-show-signature", $"-n{max}", "--format=%H%x1f%h%x1f%s%x1f%ct%x1f%an" };
        if (after != null) args.Add($"{after}..HEAD");
        else args.AddRange(new[] { "HEAD", "--not", "--remotes" });
        var r = await _git.RunAsync(worktree, args, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        var list = new List<CommitInfo>();
        if (!r.Ok) return list;
        foreach (var line in r.OutLines)
        {
            var p = line.Split('\x1f');
            if (p.Length < 5) continue;
            long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t);
            list.Add(new CommitInfo { Sha = p[0], ShortSha = p[1], Subject = p[2], When = DateTimeOffset.FromUnixTimeSeconds(t), Author = p[4] });
        }
        return list;
    }

    /// <summary>The full name of a commit, or null when <paramref name="rev"/> names none in this repository.</summary>
    public async Task<string?> ResolveAsync(string worktree, string rev, CancellationToken ct = default)
    {
        var r = await _git.RunAsync(worktree, new[] { "rev-parse", "--verify", "--quiet", rev + "^{commit}" }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return r.Ok && r.StdOut.Trim().Length > 0 ? r.StdOut.Trim() : null;
    }

    /// <summary>Where HEAD and <paramref name="other"/> last had the same history; null when they share none.</summary>
    public async Task<string?> MergeBaseAsync(string worktree, string other, CancellationToken ct = default)
    {
        var r = await _git.RunAsync(worktree, new[] { "merge-base", "HEAD", other }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return r.Ok && r.StdOut.Trim().Length > 0 ? r.StdOut.Trim() : null;
    }

    /// <summary>The commit before <paramref name="sha"/>, or the empty tree for a first commit.</summary>
    public async Task<string> ParentAsync(string worktree, string sha, CancellationToken ct = default) =>
        await ResolveAsync(worktree, sha + "^", ct).ConfigureAwait(false) ?? EmptyTree;
}
