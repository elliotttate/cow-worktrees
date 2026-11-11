using System.Text.Json;
using System.Text.Json.Serialization;

namespace CowWorktrees.Services;

/// <summary>One row of `cow_worktree.ps1 -List -Json`.</summary>
public sealed class CowListRow
{
    public string Name { get; set; } = "";
    public string? Branch { get; set; }
    public double IdleDays { get; set; }
    public int Changed { get; set; }
    /// <summary>Files that only look changed to Git; the script leaves them out of <see cref="Changed"/> (1.0.1 and later).</summary>
    public int Phantom { get; set; }
    public int Untracked { get; set; }
    /// <summary>Commits since the worktree was made (null when it has no cow.startCommit).</summary>
    public int? Commits { get; set; }
    public int Unpushed { get; set; }
    public bool Locked { get; set; }
    public string Path { get; set; } = "";
    /// <summary>cow.startCommit; null when the worktree has none (1.0.x wrote {} for that, which is read as null too).</summary>
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Start { get; set; }

    /// <summary>The reasons `-Prune -OlderThanDays n` would keep this worktree, the same tests the script makes.</summary>
    public List<string> PruneKeepReasons(int olderThanDays)
    {
        var why = new List<string>();
        if (IdleDays < olderThanDays) why.Add($"used {IdleDays:0.#} days ago");
        if (Locked) why.Add("locked");
        if (Changed > 0) why.Add($"{Changed} changed files");
        if (Untracked > 0) why.Add($"{Untracked} untracked files");
        if (Unpushed > 0 && Commits != 0) why.Add($"{Unpushed} commits not on any remote");
        return why;
    }
}

/// <summary>A string, or null for anything that is not one (PowerShell 5.1 writes "no value" as an empty object).</summary>
public sealed class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString() is { Length: > 0 } s ? s : null;
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>
/// Runs scripts\cow_worktree.ps1 from the tool folder in pools.json, under Windows PowerShell 5.1:
/// powershell.exe -NoProfile -ExecutionPolicy Bypass -File cow_worktree.ps1 ... -Root &lt;pool root&gt;
/// </summary>
public sealed class CowScript
{
    private readonly string _scriptPath;
    private readonly Pool _pool;

    public CowScript(string scriptPath, Pool pool)
    {
        _scriptPath = scriptPath;
        _pool = pool;
    }

    public static string PowerShellExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

    public string ScriptPath => _scriptPath;
    public bool ScriptExists => !string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath);

    private List<string> FullArgs(IEnumerable<string> scriptArgs)
    {
        var all = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", _scriptPath };
        all.AddRange(scriptArgs);
        all.Add("-Root");
        all.Add(_pool.Root);
        return all;
    }

    public string CommandLine(IEnumerable<string> scriptArgs) => ProcessRunner.Display("powershell.exe", FullArgs(scriptArgs));

    /// <summary>
    /// Runs the script from the pool's repository (so a ref such as HEAD means that checkout's, as when run there).
    /// Progress arrives on stderr; the result (a path) is the last stdout line; exit 1 means failure.
    /// </summary>
    public Task<ProcessResult> RunAsync(IEnumerable<string> scriptArgs, Action<string, bool>? onLine = null,
        IReadOnlyDictionary<string, string>? environment = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        return ProcessRunner.RunAsync(PowerShellExe, FullArgs(scriptArgs), _pool.Repo, environment, onLine, timeout, ct);
    }

    /// <summary>
    /// `-List -Json`. GIT_OPTIONAL_LOCKS=0 keeps the script's `git status` calls from refreshing (and so
    /// re-dating) each worktree's index, which would reset the idle time that -Prune goes by.
    /// </summary>
    public async Task<List<CowListRow>> ListAsync(CancellationToken ct = default)
    {
        var env = new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" };
        var r = await RunAsync(new[] { "-List", "-Json" }, null, env, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (!r.Ok) throw new InvalidOperationException("cow_worktree.ps1 -List -Json: " + r.Problem);
        var text = r.StdOut.Trim();
        int start = text.IndexOfAny(new[] { '[', '{' });
        if (start < 0) return new List<CowListRow>();
        text = text[start..];
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
        if (text.StartsWith('{'))
        {
            var one = JsonSerializer.Deserialize<CowListRow>(text, opts);
            return one == null ? new List<CowListRow>() : new List<CowListRow> { one };
        }
        return JsonSerializer.Deserialize<List<CowListRow>>(text, opts) ?? new List<CowListRow>();
    }

    /// <summary>The name the script will actually use: anything outside [A-Za-z0-9._-] becomes '-', ends trimmed of '-' and '.'.</summary>
    public static string SanitizeName(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '-').ToArray();
        return new string(chars).Trim('-', '.');
    }
}
