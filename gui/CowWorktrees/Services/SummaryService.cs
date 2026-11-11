using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CowWorktrees.Services;

public enum SummaryTool { Claude, Codex }

/// <summary>A few lines about one worktree, written by Claude or Codex when the person asked for them.</summary>
public sealed class WorktreeSummary
{
    public string Path { get; set; } = "";
    /// <summary>"Claude" or "Codex".</summary>
    public string Tool { get; set; } = "";
    public DateTime At { get; set; }
    /// <summary>What the worktree looked like when this was written (commit, changed and new files); see WorktreeViewModel.SummarySignature.</summary>
    public string Signature { get; set; } = "";
    /// <summary>The reply as it came.</summary>
    public string Text { get; set; } = "";

    private bool _parsed;
    private string _headline = "", _open = "";
    private List<string> _points = new();

    /// <summary>The first line: what the worktree is for.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Headline { get { Parse(); return _headline; } }
    /// <summary>The lines that started with "- ".</summary>
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<string> Points { get { Parse(); return _points; } }
    /// <summary>What the "Open:" line says is unfinished; empty when there is none.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Open { get { Parse(); return _open; } }

    private void Parse()
    {
        if (_parsed) return;
        _parsed = true;
        var rest = new List<string>();
        foreach (var raw in Text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim().Replace("**", "").Replace("`", "");
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal)) continue;
            // "- Open: ..." is the open line too, although it was written as a point.
            if (line.Length > 7 && (line[0] is '-' or '*' or '•') && line[1] == ' ' && line[2..].StartsWith("Open:", StringComparison.OrdinalIgnoreCase)) line = line[2..];
            if (line.StartsWith("Open:", StringComparison.OrdinalIgnoreCase)) _open = (_open.Length > 0 ? _open + " " : "") + line[5..].Trim();
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("• ", StringComparison.Ordinal)) _points.Add(line[2..].Trim());
            else if (_headline.Length == 0 && _points.Count == 0) _headline = line.TrimStart('#', ' ');
            else rest.Add(line);
        }
        // A reply that is not in the shape asked for: its other lines are still shown, as points.
        if (rest.Count > 0) _points.AddRange(rest);
        if (_points.Count > 8) _points = _points.Take(8).ToList();
    }
}

/// <summary>The summaries that were written, in summaries.json next to pools.json, and the tool chosen last.</summary>
public sealed class SummaryStore
{
    public int Version { get; set; } = 1;
    /// <summary>"Claude" or "Codex": the one the Summarize button uses.</summary>
    public string Tool { get; set; } = "";
    /// <summary>Keep every worktree's summary up to date without being asked (off until the person turns it on).</summary>
    public bool Auto { get; set; }
    /// <summary>The model Claude writes summaries with; a small, fast one is enough. Empty = Claude Code's own choice.</summary>
    public string ClaudeModel { get; set; } = "haiku";
    /// <summary>The model Codex writes summaries with. Empty = the one Codex is set up with.</summary>
    public string CodexModel { get; set; } = "";
    public Dictionary<string, WorktreeSummary> Summaries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath => System.IO.Path.Combine(CowHome.Folder, "summaries.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static SummaryStore Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<SummaryStore>(File.ReadAllText(FilePath), JsonOptions) is { } s)
            {
                s.Summaries = new Dictionary<string, WorktreeSummary>(s.Summaries, StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch (Exception ex) { AppLog.Write($"summaries: could not read {FilePath}: {ex.Message}"); }
        return new SummaryStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(CowHome.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) { AppLog.Write($"summaries: could not write {FilePath}: {ex.Message}"); }
    }

    public WorktreeSummary? Get(string path) => Summaries.TryGetValue(GitService.NormalizePath(path), out var s) ? s : null;
    public void Set(WorktreeSummary s) => Summaries[GitService.NormalizePath(s.Path)] = s;
    public void Remove(string path) => Summaries.Remove(GitService.NormalizePath(path));
}

/// <summary>
/// Asks the Claude Code or the Codex command line tool on this PC for a short summary. The text to summarise
/// (commit titles, changed files, changed lines) is given on standard input; the tool runs in an empty folder
/// of the app's own, with its file and shell tools off or read-only, so it can only read what it was given.
/// </summary>
public static class SummaryService
{
    /// <summary>Full path of the tool's command, or null when it is not installed. COW_CLAUDE_CLI / COW_CODEX_CLI name a stand-in (for tests).</summary>
    public static string? Find(SummaryTool tool)
    {
        string name = tool == SummaryTool.Claude ? "claude" : "codex";
        string? forced = Environment.GetEnvironmentVariable(tool == SummaryTool.Claude ? "COW_CLAUDE_CLI" : "COW_CODEX_CLI");
        if (!string.IsNullOrWhiteSpace(forced)) return File.Exists(forced) ? forced : null;
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim().Trim('"')).ToList();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // where the installers put them, for an app started with an older PATH
        folders.Add(System.IO.Path.Combine(home, ".local", "bin"));
        folders.Add(System.IO.Path.Combine(roaming, "npm"));
        if (tool == SummaryTool.Codex) folders.Add(System.IO.Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"));
        foreach (var folder in folders)
        {
            foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            {
                try
                {
                    string full = System.IO.Path.Combine(folder, name + ext);
                    if (File.Exists(full)) return full;
                }
                catch { /* a PATH entry that is not a path */ }
            }
        }
        return null;
    }

    private const string Ask = "Write the summary that the text on standard input asks for. Reply with the summary only.";

    public static IReadOnlyList<string> Arguments(SummaryTool tool, string model, string workFolder, string replyFile)
    {
        var args = tool == SummaryTool.Claude
            ? new List<string> { "-p", Ask, "--output-format", "text", "--tools", "", "--no-session-persistence", "--strict-mcp-config", "--disable-slash-commands" }
            : new List<string> { "exec", "--skip-git-repo-check", "--sandbox", "read-only", "--ephemeral", "--color", "never", "-c", "model_reasoning_effort=\"low\"", "-C", workFolder, "-o", replyFile, Ask };
        if (!string.IsNullOrWhiteSpace(model)) args.InsertRange(tool == SummaryTool.Claude ? 2 : 1, new[] { tool == SummaryTool.Claude ? "--model" : "-m", model.Trim() });
        return args;
    }

    /// <summary>Runs the tool. Returns its reply, or why there is none.</summary>
    public static async Task<(string? Text, string? Error)> RunAsync(SummaryTool tool, string command, string model, string prompt, CancellationToken ct)
    {
        string work = System.IO.Path.Combine(CowHome.Folder, "summary-work");
        Directory.CreateDirectory(work);
        string replyFile = System.IO.Path.Combine(work, "reply-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var args = Arguments(tool, model, work, replyFile);
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = work,
        };
        if (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // A batch file needs cmd.exe. None of the arguments holds a character cmd treats specially.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/d /s /c \"\"" + command + "\" " + string.Join(" ", args.Select(a => a.Length == 0 || a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a)) + "\"";
        }
        else
        {
            psi.FileName = command;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await proc.StandardInput.BaseStream.WriteAsync(new UTF8Encoding(false).GetBytes(prompt), ct).ConfigureAwait(false);
                proc.StandardInput.Close();
            }
            catch (IOException) { /* it stopped reading: its exit code and output say why */ }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(5));
            try { await proc.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return (null, $"{tool} did not answer within 5 minutes.");
            }
            string stdout = "", stderr = "";
            try { stdout = await outTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
            try { stderr = await errTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }

            string reply = File.Exists(replyFile) ? await File.ReadAllTextAsync(replyFile, CancellationToken.None).ConfigureAwait(false) : stdout;
            reply = reply.Trim();
            AppLog.Write($"summary: {tool} exit {proc.ExitCode} in {sw.Elapsed.TotalSeconds:0.0} s, prompt {prompt.Length} chars, reply {reply.Length} chars");
            if (proc.ExitCode != 0 || reply.Length == 0)
            {
                string why = LastLine(stderr) ?? LastLine(stdout) ?? $"exit code {proc.ExitCode}";
                // The services answer errors as JSON: its "message" is the sentence meant for a person.
                var said = System.Text.RegularExpressions.Regex.Match(why, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (said.Success) why = said.Groups[1].Value.Replace("\\\"", "\"");
                if (why.Contains("model", StringComparison.OrdinalIgnoreCase) && (why.Contains("not supported", StringComparison.OrdinalIgnoreCase) || why.Contains("not found", StringComparison.OrdinalIgnoreCase)))
                    why += $" This is {tool}'s own setup: the model it is set to use was refused. Another one can be named in {SummaryStore.FilePath} ({(tool == SummaryTool.Claude ? "ClaudeModel" : "CodexModel")}).";
                return (null, $"{tool} could not write the summary: {why}");
            }
            return (reply, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (null, $"{tool} could not be started: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(replyFile)) File.Delete(replyFile); } catch { }
        }
    }

    private static string? LastLine(string text)
    {
        var lines = ProcessResult.SplitLines(text);
        if (lines.Length == 0) return null;
        string l = lines[^1].Trim();
        return l.Length > 300 ? l[..300] + "…" : l;
    }

    // ---- what is sent ---------------------------------------------------------------------------------------

    /// <summary>Characters of changed lines given at most; the whole text stays near twenty thousand tokens.</summary>
    public const int ExcerptBudget = 60_000;

    /// <summary>The text given to the tool: what to write, then the worktree's facts, its commits, its files and excerpts of its changed lines.</summary>
    public static string BuildPrompt(string name, string branch, string comparedWith, IReadOnlyList<CommitInfo> commits, ChangeSet changes, int uncommittedChanged, int uncommittedNew)
    {
        var sb = new StringBuilder(8192);
        sb.Append("Summarize the work in one Git worktree for its owner. They have several worktrees, often worked on by coding agents, and want to see at a glance what each one is for and where it stands. Use only what is below. Do not use tools and do not ask questions.\n\n");
        sb.Append("Reply in plain text, in exactly this shape:\n");
        sb.Append("- First line: one sentence of at most 25 words saying what this worktree is for. Begin with what the work does (\"Adds\", \"Fixes\", \"Explores\" ...), not with \"A worktree\" or its name.\n");
        sb.Append("- Then 2 to 5 lines that each start with \"- \" and have at most 16 words: the main things that were done.\n");
        sb.Append("- Then, only if something is unfinished, uncommitted or looks risky, one last line that starts with \"Open: \".\n");
        sb.Append("No headings, no Markdown, no code blocks, nothing before or after. If nothing was changed, say so in the first line and stop.\n\n");

        sb.Append("WORKTREE\n");
        sb.Append("name: ").Append(name).Append('\n');
        sb.Append("branch: ").Append(branch).Append('\n');
        sb.Append("compared with: ").Append(comparedWith).Append('\n');
        sb.Append("commits: ").Append(commits.Count).Append("; not committed yet: ").Append(uncommittedChanged).Append(" changed files, ").Append(uncommittedNew).Append(" new files\n\n");

        if (commits.Count > 0)
        {
            sb.Append("COMMITS (newest first)\n");
            foreach (var c in commits.Take(60)) sb.Append(c.ShortSha).Append(' ').Append(c.Subject).Append(" (").Append(Fmt.Relative(c.When.LocalDateTime)).Append(")\n");
            sb.Append('\n');
        }

        var files = changes.Files.Where(f => f.Kind != ChangeKind.Phantom).ToList();
        sb.Append("FILES CHANGED (").Append(files.Count).Append("; lines added and removed)\n");
        foreach (var f in files.OrderByDescending(f => f.Added + f.Deleted).Take(150))
        {
            sb.Append(f.Kind switch { ChangeKind.Added => "added", ChangeKind.Deleted => "deleted", ChangeKind.Renamed => "renamed", ChangeKind.Untracked => "new, not committed", _ => "modified" });
            sb.Append("  +").Append(f.Added).Append(" -").Append(f.Deleted).Append("  ").Append(f.Path);
            if (f.Binary) sb.Append("  (binary)");
            sb.Append('\n');
        }
        if (files.Count > 150) sb.Append("and ").Append(files.Count - 150).Append(" more files\n");
        if (changes.UntrackedLeftOut > 0) sb.Append("and ").Append(changes.UntrackedLeftOut).Append(" more new files that are not listed\n");

        // Excerpts: the changed lines of the files that changed most, each file up to a share of the budget.
        sb.Append("\nCHANGED LINES (excerpts; + added, - removed)\n");
        int budget = ExcerptBudget;
        var withLines = files.Where(f => f.Hunks.Count > 0).OrderByDescending(f => Math.Min(f.Added + f.Deleted, 400)).ToList();
        int perFile = Math.Clamp(withLines.Count == 0 ? 0 : ExcerptBudget / Math.Min(withLines.Count, 40), 600, 12_000);
        foreach (var f in withLines)
        {
            if (budget < 400) break;
            int left = Math.Min(perFile, budget), start = sb.Length;
            sb.Append("=== ").Append(f.Path).Append('\n');
            foreach (var h in f.Hunks)
            {
                if (sb.Length - start > left) break;
                if (h.Context.Length > 0) sb.Append("@@ ").Append(h.Context).Append('\n');
                foreach (var l in h.Lines)
                {
                    if (l.Kind == DiffLineKind.Context) continue;
                    if (sb.Length - start > left) break;
                    string text = l.Text.Trim();
                    if (text.Length == 0) continue;
                    sb.Append(l.Kind == DiffLineKind.Added ? '+' : '-').Append(text.Length > 200 ? text[..200] : text).Append('\n');
                }
            }
            budget -= sb.Length - start;
        }
        return sb.ToString();
    }
}
