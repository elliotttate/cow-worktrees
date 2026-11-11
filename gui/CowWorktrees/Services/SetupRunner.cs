using System.Text.Json;

namespace CowWorktrees.Services;

/// <summary>
/// What the walkthrough and Repository settings ask setup.ps1 to do. A null value means "do not pass it":
/// on a first run setup.ps1 uses its default, on a later run it keeps what the repository already has.
/// </summary>
public sealed record SetupOptions
{
    public string Repo { get; init; } = "";
    public string Root { get; init; } = "";
    public string? Name { get; init; }
    public string? BaseRef { get; init; }
    public bool Claude { get; init; } = true;
    public bool Codex { get; init; } = true;
    public bool Schedule { get; init; } = true;
    /// <summary>"HH:mm"; only used when <see cref="Schedule"/> is on.</summary>
    public string? ScheduleAt { get; init; }
    public string? BuildCommand { get; init; }
    /// <summary>Comma separated, for example "Debug,Release".</summary>
    public string? BuildConfigs { get; init; }
    /// <summary>One command run before the build; "" clears what is there.</summary>
    public string? BuildPrepare { get; init; }
    /// <summary>Remove the build section (stop making warm builds).</summary>
    public bool NoBuild { get; init; }
    public int? PruneDays { get; init; }
    public string? BranchPrefix { get; init; }
    public bool BuildNow { get; init; }

    public static SetupOptions FromJson(string json) =>
        JsonSerializer.Deserialize<SetupOptions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SetupOptions();
}

/// <summary>
/// Runs the tool folder's scripts the way CowScript runs cow_worktree.ps1:
/// powershell.exe -NoProfile -ExecutionPolicy Bypass -File &lt;script&gt; &lt;arguments&gt;, each argument passed on its own
/// (so paths with spaces and quotes survive), and gives the same thing as one line of text for people who
/// prefer PowerShell. setup.ps1 and uninstall.ps1 stay the only place where setup is implemented.
/// </summary>
public static class SetupRunner
{
    public static List<string> PowerShellFile(string script, IEnumerable<string> scriptArgs)
    {
        var all = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script };
        all.AddRange(scriptArgs);
        return all;
    }

    public static string CommandLine(string script, IEnumerable<string> scriptArgs) =>
        ProcessRunner.Display("powershell.exe", PowerShellFile(script, scriptArgs));

    public static Task<ProcessResult> RunAsync(string script, IEnumerable<string> scriptArgs, string? workingDirectory,
        Action<string, bool>? onLine, TimeSpan? timeout, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(CowScript.PowerShellExe, PowerShellFile(script, scriptArgs), workingDirectory, null, onLine, timeout, ct);

    public static List<string> SetupArgs(SetupOptions o)
    {
        var a = new List<string> { "-Repo", o.Repo, "-Root", o.Root };
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) { a.Add(name); a.Add(value.Trim()); }
        }
        Add("-Name", o.Name);
        Add("-BaseRef", o.BaseRef);
        Add("-BranchPrefix", o.BranchPrefix);
        if (o.PruneDays is { } days) { a.Add("-PruneDays"); a.Add(days.ToString()); }
        if (o.NoBuild) a.Add("-NoBuild");
        else
        {
            Add("-BuildCommand", o.BuildCommand);
            Add("-BuildConfigs", o.BuildConfigs);
            if (o.BuildPrepare != null) { a.Add("-BuildPrepare"); a.Add(o.BuildPrepare.Trim()); }
        }
        if (!o.Claude) a.Add("-NoClaude");
        if (!o.Codex) a.Add("-NoCodex");
        if (!o.Schedule) a.Add("-NoSchedule");
        else Add("-ScheduleAt", o.ScheduleAt);
        if (o.BuildNow && !o.NoBuild) a.Add("-BuildNow");
        return a;
    }

    public static List<string> UninstallArgs(string root, bool removePool)
    {
        var a = new List<string> { "-Root", root };
        if (removePool) a.Add("-RemovePool");
        return a;
    }

    public static List<string> MigrateArgs(string from, string name, string root, bool removeOld)
    {
        var a = new List<string> { "-From", from, "-Name", name, "-Root", root };
        if (removeOld) a.Add("-RemoveOld");
        return a;
    }

    /// <summary>The Codex instructions file, resolved the way the scripts do (COW_CODEX_AGENTS_FILE, else ~\.codex\AGENTS.md).</summary>
    public static string CodexAgentsFile =>
        Environment.GetEnvironmentVariable("COW_CODEX_AGENTS_FILE") is { Length: > 0 } f
            ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "AGENTS.md");
}
