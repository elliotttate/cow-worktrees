using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CowWorktrees.Services;

public enum IntegrationState
{
    /// <summary>Connected, and pointing at this copy of the tool.</summary>
    On,
    Off,
    /// <summary>Connected in some of the project's folders but not all.</summary>
    Partial,
    /// <summary>Connected, but to another copy of the tool (another folder).</summary>
    Elsewhere,
    Unknown,
}

/// <summary>One connection of a repository (Claude Code, Codex, the daily upkeep) as it is on disk right now.</summary>
public sealed record IntegrationItem(string Id, IntegrationState State, string Summary, string Detail)
{
    public bool IsConnected => State is IntegrationState.On or IntegrationState.Partial or IntegrationState.Elsewhere;
}

public sealed class IntegrationReport
{
    public IntegrationItem Claude { get; init; } = new("claude", IntegrationState.Unknown, "", "");
    public IntegrationItem Codex { get; init; } = new("codex", IntegrationState.Unknown, "", "");
    public IntegrationItem Schedule { get; init; } = new("schedule", IntegrationState.Unknown, "", "");
    /// <summary>"HH:mm" the daily task starts at, when it exists.</summary>
    public string? ScheduleAt { get; init; }
    /// <summary>Something is wanted (by the saved choices, or by being half there) but not fully in place; null when all is well.</summary>
    public string? Drift { get; init; }
}

/// <summary>
/// Read-only look at what setup.ps1 wires up for one repository: its entries in .claude\settings.local.json of
/// every checkout, the marked block in the Codex instructions file, and the task \CowWorktrees\Refresh-&lt;name&gt;.
/// The matches are the ones the scripts use (cc_(worktree|guard)_hook\.ps1, "&lt;!-- cow-worktrees:name begin --&gt;").
/// </summary>
public static class IntegrationStatus
{
    private static readonly Regex HookScript = new(@"cc_(worktree|guard)_hook\.ps1", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="checkouts">The folders setup writes hooks into: every checkout of the repository that exists.</param>
    public static IntegrationReport Read(Pool pool, string? toolDir, IReadOnlyList<string> checkouts)
    {
        var config = pool.Config;
        string scripts = toolDir == null ? "" : Path.Combine(toolDir, "scripts") + "\\";
        var claude = ReadClaude(checkouts, scripts);
        // The two folders that matter most: sessions start in the project's main folder, and every new worktree
        // gets its settings from the base. A worktree some tool made a minute ago has no hooks until setup runs
        // again; that shows on the settings page, but is not worth a banner.
        bool essentialMissing = new[] { pool.Repo, pool.BasePath }.Any(dir => Directory.Exists(dir) && HookScriptPaths(ClaudeSettingsFile(dir)).Count == 0);
        var codex = ReadCodex(pool.Name, scripts);
        var (schedule, at) = ReadTask(pool, scripts);

        // What is wanted: the saved choices (1.1.0 and later); a repository set up by 1.0.x has none, so whatever
        // is there counts as wanted.
        var want = config?.Integrations;
        bool wantClaude = want?.Claude ?? claude.IsConnected;
        bool wantCodex = want?.Codex ?? codex.IsConnected;
        bool wantSchedule = want != null ? want.ScheduleAt != null : schedule.IsConnected;

        var problems = new List<string>();
        if (wantClaude && claude.State == IntegrationState.Off) problems.Add("Claude Code is not connected any more");
        if (wantClaude && claude.State == IntegrationState.Partial && essentialMissing) problems.Add(claude.Summary);
        if (wantCodex && codex.State == IntegrationState.Off) problems.Add("the Codex instructions are missing");
        if (wantSchedule && schedule.State == IntegrationState.Off) problems.Add("the daily upkeep task is missing");
        if (new[] { claude, codex, schedule }.Any(i => i.State == IntegrationState.Elsewhere))
            problems.Add("some connections point at another copy of cow-worktrees");

        return new IntegrationReport
        {
            Claude = claude,
            Codex = codex,
            Schedule = schedule,
            ScheduleAt = at,
            Drift = problems.Count == 0 ? null : char.ToUpperInvariant(problems[0][0]) + problems[0][1..] + (problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "") + ".",
        };
    }

    public static string ClaudeSettingsFile(string checkout) => Path.Combine(checkout, ".claude", "settings.local.json");

    private static IntegrationItem ReadClaude(IReadOnlyList<string> checkouts, string scripts)
    {
        int here = 0, elsewhere = 0, none = 0;
        var missing = new List<string>();
        string? otherPath = null;
        foreach (var c in checkouts)
        {
            var paths = HookScriptPaths(ClaudeSettingsFile(c));
            if (paths.Count == 0) { none++; missing.Add(c); }
            else if (scripts.Length > 0 && paths.All(p => p.StartsWith(scripts, StringComparison.OrdinalIgnoreCase))) here++;
            else { elsewhere++; otherPath ??= paths.FirstOrDefault(p => !p.StartsWith(scripts, StringComparison.OrdinalIgnoreCase)); }
        }
        int total = checkouts.Count;
        if (total == 0) return new("claude", IntegrationState.Unknown, "Could not be checked", "No checkout of the project was found.");
        if (here == total) return new("claude", IntegrationState.On, "Connected", $"The hooks are in .claude\\settings.local.json of {Fmt.Plural(total, "folder")} of this project.");
        if (none == total) return new("claude", IntegrationState.Off, "Not connected", "No folder of this project has the hooks.");
        if (elsewhere > 0)
            return new("claude", IntegrationState.Elsewhere, "Connected to another copy of the tool", $"The hooks run scripts from {Path.GetDirectoryName(otherPath ?? "")}. Repair points them at this copy.");
        return new("claude", IntegrationState.Partial, $"Claude Code is connected in {here} of {total} folders of this project",
            "Missing in: " + string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? " …" : "") + ". A folder made after setup does not have the hooks until setup runs again.");
    }

    /// <summary>The script paths of this tool's hook entries in one settings file.</summary>
    public static List<string> HookScriptPaths(string settingsFile)
    {
        var found = new List<string>();
        try
        {
            if (!File.Exists(settingsFile)) return found;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (Json.Get(doc.RootElement, "hooks") is not { ValueKind: JsonValueKind.Object } hooks) return found;
            foreach (var evt in hooks.EnumerateObject())
            {
                if (evt.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in evt.Value.EnumerateArray())
                {
                    if (Json.Get(entry, "hooks") is not { ValueKind: JsonValueKind.Array } inner) continue;
                    foreach (var h in inner.EnumerateArray())
                    {
                        var strings = new List<string>();
                        if (Json.String(h, "command") is { } cmd) strings.Add(cmd);
                        strings.AddRange(Json.Strings(h, "args"));
                        found.AddRange(strings.Where(s => HookScript.IsMatch(s)));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"integration status: {settingsFile}: {ex.Message}");
        }
        return found;
    }

    private static IntegrationItem ReadCodex(string name, string scripts)
    {
        string file = SetupRunner.CodexAgentsFile;
        try
        {
            if (!File.Exists(file)) return new("codex", IntegrationState.Off, "Not connected", $"{file} does not exist.");
            string text = File.ReadAllText(file);
            string begin = $"<!-- cow-worktrees:{name} begin -->", end = $"<!-- cow-worktrees:{name} end -->";
            int i = text.IndexOf(begin, StringComparison.Ordinal), j = text.IndexOf(end, StringComparison.Ordinal);
            if (i < 0 || j <= i) return new("codex", IntegrationState.Off, "Not connected", $"{file} has no section for this repository.");
            string block = text[i..j];
            if (scripts.Length > 0 && !block.Contains(scripts, StringComparison.OrdinalIgnoreCase))
                return new("codex", IntegrationState.Elsewhere, "Connected to another copy of the tool", $"The section in {file} names scripts in another folder. Repair points it at this copy.");
            return new("codex", IntegrationState.On, "Connected", $"A marked section in {file} tells Codex to use copy-on-write worktrees for this project.");
        }
        catch (Exception ex)
        {
            return new("codex", IntegrationState.Unknown, "Could not be checked", $"{file}: {ex.Message}");
        }
    }

    private static (IntegrationItem Item, string? At) ReadTask(Pool pool, string scripts)
    {
        string taskPath = pool.TaskFullPath;
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return (new("schedule", IntegrationState.Unknown, "Could not be checked", "Task Scheduler is unavailable."), null);
            service = Activator.CreateInstance(type);
            dynamic svc = service!;
            svc.Connect();
            dynamic task = svc.GetFolder(pool.TaskFolder).GetTask(pool.TaskName);
            string? at = null;
            string args = "";
            try
            {
                dynamic def = task.Definition;
                foreach (dynamic trigger in def.Triggers)
                {
                    string start = (string)trigger.StartBoundary;   // 2026-11-09T05:00:00
                    if (DateTime.TryParse(start, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)) at = t.ToString("HH:mm");
                    break;
                }
                foreach (dynamic action in def.Actions)
                {
                    args = (string)action.Arguments;
                    break;
                }
            }
            catch
            {
                // the task exists; its details are a bonus
            }
            bool enabled = (bool)task.Enabled;
            if (scripts.Length > 0 && args.Length > 0 && !args.Contains(scripts, StringComparison.OrdinalIgnoreCase))
                return (new("schedule", IntegrationState.Elsewhere, "Runs another copy of the tool", $"{taskPath} starts scripts in another folder. Repair points it at this copy."), at);
            return (new("schedule", IntegrationState.On, at != null ? $"Every day at {at}, when the PC is idle" : "Scheduled",
                $"{taskPath} removes worktrees that have been idle with nothing to lose, and brings the base copy up to date.{(enabled ? "" : " The task is disabled in Task Scheduler.")}"), at);
        }
        // Through dynamic, a missing task or folder arrives as FileNotFoundException / DirectoryNotFoundException.
        catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x80070003)
        {
            return (new("schedule", IntegrationState.Off, "Off", $"{taskPath} does not exist."), null);
        }
        catch (Exception ex)
        {
            return (new("schedule", IntegrationState.Unknown, "Could not be checked", $"{taskPath}: {ex.Message}"), null);
        }
        finally
        {
            if (service != null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
    }
}
