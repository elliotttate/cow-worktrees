using System.Text.Json;
using System.Text.RegularExpressions;

namespace CowWorktrees.Services;

/// <summary>
/// Where cow-worktrees keeps its machine-wide files: %LOCALAPPDATA%\cow-worktrees, or COW_WORKTREES_HOME
/// (the same rule as scripts\CowCommon.ps1). pools.json is written by setup.ps1; this app only reads it.
/// </summary>
public static class CowHome
{
    public static string Folder =>
        Environment.GetEnvironmentVariable("COW_WORKTREES_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cow-worktrees");

    public static string PoolsFile => Path.Combine(Folder, "pools.json");
}

/// <summary>pools.json: {"version":1, "toolDir":"...", "pools":[{"name":"...","root":"..."}]}</summary>
public sealed class PoolRegistry
{
    public int Version { get; init; }
    public string ToolDir { get; init; } = "";
    public List<Pool> Pools { get; init; } = new();

    /// <summary>scripts\cow_worktree.ps1 under toolDir.</summary>
    public string ScriptPath => string.IsNullOrWhiteSpace(ToolDir) ? "" : Path.Combine(ToolDir, "scripts", "cow_worktree.ps1");

    /// <summary>Reads pools.json; a missing file is an empty registry, a broken one an error.</summary>
    public static (PoolRegistry Registry, string? Error) Load()
    {
        var file = CowHome.PoolsFile;
        if (!File.Exists(file)) return (new PoolRegistry(), null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = doc.RootElement;
            var pools = new List<Pool>();
            if (Json.Get(r, "pools") is { ValueKind: JsonValueKind.Array } arr)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    var root = Json.String(p, "root");
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    pools.Add(new Pool(Json.String(p, "name") ?? "", root));
                }
            }
            else if (Json.Get(r, "pools") is { ValueKind: JsonValueKind.Object } single)   // a one-element array written as an object
            {
                var root = Json.String(single, "root");
                if (!string.IsNullOrWhiteSpace(root)) pools.Add(new Pool(Json.String(single, "name") ?? "", root));
            }
            return (new PoolRegistry
            {
                Version = Json.Get(r, "version") is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : 1,
                ToolDir = Json.String(r, "toolDir") ?? "",
                Pools = pools,
            }, null);
        }
        catch (Exception ex)
        {
            return (new PoolRegistry(), $"{file} could not be read: {ex.Message}");
        }
    }
}

/// <summary>One repository's copy-on-write setup: a root folder holding base\, the cloned worktrees and cow-config.json.</summary>
public sealed class Pool
{
    public Pool(string name, string root)
    {
        Root = FullPath(root);
        RegisteredName = name;
    }

    public string RegisteredName { get; }
    public string Root { get; }
    public PoolConfig? Config { get; private set; }
    public string? ConfigError { get; private set; }

    public string Name => !string.IsNullOrWhiteSpace(Config?.Name) ? Config!.Name! : !string.IsNullOrWhiteSpace(RegisteredName) ? RegisteredName : System.IO.Path.GetFileName(Root);
    public string BasePath => System.IO.Path.Combine(Root, "base");
    public string ConfigFile => System.IO.Path.Combine(Root, "cow-config.json");
    public string Repo => Config?.Repo ?? "";
    public bool IsUsable => Config != null && ConfigError == null;

    /// <summary>The root's full path, lower-cased, with everything but [a-z0-9] turned into '_' (D:\Pools\My-Repo -> d__pools_my_repo).</summary>
    public string Id => Regex.Replace(Root.ToLowerInvariant(), "[^a-z0-9]", "_");
    public string StateMutexName => $@"Global\cow-worktrees.state.{Id}";
    public string BuildMutexName => $@"Global\cow-worktrees.build.{Id}";
    public string TaskFolder => @"\CowWorktrees";
    public string TaskName => $"Refresh-{Name}";
    public string TaskFullPath => $@"{TaskFolder}\{TaskName}";

    /// <summary>(Re)reads cow-config.json; afterwards <see cref="ConfigError"/> says what is wrong, if anything.</summary>
    public void LoadConfig()
    {
        try
        {
            if (!Directory.Exists(Root)) throw new InvalidOperationException($"the pool folder {Root} does not exist");
            if (!File.Exists(ConfigFile)) throw new InvalidOperationException($"there is no cow-config.json in {Root} (run setup.ps1 for this repository)");
            Config = PoolConfig.Parse(File.ReadAllText(ConfigFile), Root);
            if (string.IsNullOrWhiteSpace(Config.Repo)) throw new InvalidOperationException($"{ConfigFile} has no repo");
            ConfigError = null;
        }
        catch (Exception ex)
        {
            Config = null;
            ConfigError = ex is JsonException ? $"{ConfigFile} is not valid JSON: {ex.Message}" : ex.Message;
        }
    }

    public static string FullPath(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path.TrimEnd('\\'); }
    }

    public override string ToString() => Name;
}

/// <summary>
/// cow-config.json, with the defaults CowCommon.ps1 applies: name = the root's folder name, baseRef origin/main,
/// branchPrefix worktree-, fallbackMinFreeGB 120, pruneDays 7. "build" is optional.
/// </summary>
public sealed class PoolConfig
{
    public string? Name { get; init; }
    public string Repo { get; init; } = "";
    public string BaseRef { get; init; } = "origin/main";
    public string BranchPrefix { get; init; } = "worktree-";
    public double FallbackMinFreeGB { get; init; } = 120;
    public int PruneDays { get; init; } = 7;
    public BuildConfig? Build { get; init; }

    public static PoolConfig Parse(string json, string root)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var r = doc.RootElement;
        BuildConfig? build = null;
        if (Json.Get(r, "build") is { ValueKind: JsonValueKind.Object } b)
        {
            build = new BuildConfig
            {
                Prepare = Json.Strings(b, "prepare"),
                Command = Json.String(b, "command"),
                Configs = Json.Strings(b, "configs"),
            };
        }
        return new PoolConfig
        {
            Name = Json.String(r, "name") ?? System.IO.Path.GetFileName(root.TrimEnd('\\')),
            Repo = Pool.FullPath(Json.String(r, "repo") ?? ""),
            BaseRef = Json.String(r, "baseRef") is { Length: > 0 } br ? br : "origin/main",
            BranchPrefix = Json.String(r, "branchPrefix") ?? "worktree-",
            FallbackMinFreeGB = Json.Get(r, "fallbackMinFreeGB") is { ValueKind: JsonValueKind.Number } f ? f.GetDouble() : 120,
            PruneDays = Json.Get(r, "pruneDays") is { ValueKind: JsonValueKind.Number } d ? (int)Math.Round(d.GetDouble()) : 7,
            Build = build,
        };
    }
}

public sealed class BuildConfig
{
    public IReadOnlyList<string> Prepare { get; init; } = Array.Empty<string>();
    public string? Command { get; init; }
    public IReadOnlyList<string> Configs { get; init; } = Array.Empty<string>();
}

/// <summary>Case-insensitive, forgiving reads from what PowerShell's ConvertTo-Json writes.</summary>
internal static class Json
{
    public static JsonElement? Get(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public static string? String(JsonElement obj, string name) =>
        Get(obj, name) is { } v ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        } : null;

    /// <summary>An array of strings, or a single string (PowerShell writes a one-element array as a plain value).</summary>
    public static IReadOnlyList<string> Strings(JsonElement obj, string name) =>
        Get(obj, name) switch
        {
            { ValueKind: JsonValueKind.Array } a => a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList(),
            { ValueKind: JsonValueKind.String } s => new[] { s.GetString()! },
            _ => Array.Empty<string>(),
        };
}
