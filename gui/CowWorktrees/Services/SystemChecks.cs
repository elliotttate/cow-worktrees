using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CowWorktrees.Services;

public enum CheckState { Ok, Missing, TooOld, Unknown }

/// <summary>One line of "Check this PC": a state, a sentence for a person, and the detail behind it.</summary>
public sealed record CheckResult(string Id, string Title, CheckState State, string Summary, string Detail)
{
    public bool IsOk => State == CheckState.Ok;
}

/// <summary>A ReFS volume worktrees can live on.</summary>
public sealed record DriveChoice(string Root, string Label, double FreeGB, double TotalGB)
{
    public string Letter => Root.TrimEnd('\\');
    public string Display => $"{Letter}  {(string.IsNullOrWhiteSpace(Label) ? "Dev Drive" : Label)}  ·  {FreeGB:N0} GB free of {TotalGB:N0} GB";
    public override string ToString() => Display;
}

public sealed class SystemReport
{
    public List<CheckResult> Checks { get; } = new();
    public List<DriveChoice> RefsDrives { get; } = new();
    public bool ClaudeFound { get; set; }
    public bool CodexFound { get; set; }
    public string? GitPath { get; set; }
    public string? GitVersion { get; set; }

    public CheckResult? this[string id] => Checks.FirstOrDefault(c => c.Id == id);

    /// <summary>Setup can run: Git is there and new enough, and there is a ReFS volume. An old Windows is a warning only.</summary>
    public bool CanSetUp => this["git"]?.IsOk == true && this["devdrive"]?.IsOk == true;
}

/// <summary>
/// What setup.ps1 needs, looked at before it runs: Windows 11 24H2 or later (block cloning in CopyFile), Git 2.38
/// or later, a ReFS volume (a Dev Drive); and whether Claude Code and Codex look installed.
/// </summary>
public static class SystemChecks
{
    public const int MinWindowsBuild = 26100;   // Windows 11 24H2

    /// <summary>--fake-checks &lt;file&gt; (unattended runs only): replaces results, for screenshots and tests.</summary>
    public static string? FakeFile { get; set; }

    public static async Task<SystemReport> RunAsync()
    {
        var report = new SystemReport();
        EnvironmentPath.RefreshFromRegistry();

        // ---- Windows ---------------------------------------------------------------------------------------
        int build = Environment.OSVersion.Version.Build;
        report.Checks.Add(build >= MinWindowsBuild
            ? new CheckResult("windows", "Windows", CheckState.Ok, "Windows is new enough", $"Build {build}. Windows 11 version 24H2 (build {MinWindowsBuild}) or later copies files on a Dev Drive without using more space.")
            : new CheckResult("windows", "Windows", CheckState.TooOld, "Windows needs an update",
                $"This PC has build {build}. Windows 11 version 24H2 (build {MinWindowsBuild}) or later is needed for copies that take no extra space. Worktrees still work on this version, but each one takes the full size of your project."));

        // ---- Git -------------------------------------------------------------------------------------------
        var git = FindGit();
        if (git == null)
        {
            report.Checks.Add(new CheckResult("git", "Git", CheckState.Missing, "Git is not installed",
                "Git for Windows 2.38 or later is needed. It was not found on PATH or in the usual install folders."));
        }
        else
        {
            report.GitPath = git;
            string version = "";
            try
            {
                var r = await ProcessRunner.RunAsync(git, new[] { "--version" }, null, null, null, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                version = r.StdOut.Trim();
            }
            catch (Exception ex)
            {
                version = ex.Message;
            }
            report.GitVersion = version;
            var m = Regex.Match(version, @"(\d+)\.(\d+)");
            if (!m.Success)
                report.Checks.Add(new CheckResult("git", "Git", CheckState.Unknown, "Git was found, but its version could not be read", $"{git}: {version}"));
            else
            {
                int major = int.Parse(m.Groups[1].Value), minor = int.Parse(m.Groups[2].Value);
                bool ok = major > 2 || (major == 2 && minor >= 38);
                report.Checks.Add(ok
                    ? new CheckResult("git", "Git", CheckState.Ok, $"Git {major}.{minor} is installed", $"{version}\n{git}")
                    : new CheckResult("git", "Git", CheckState.TooOld, $"Git {major}.{minor} is too old", $"{version} at {git}. Git 2.38 or later is needed."));
            }
        }

        // ---- a ReFS volume -----------------------------------------------------------------------------------
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady && string.Equals(d.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase))
                    report.RefsDrives.Add(new DriveChoice(d.Name, d.VolumeLabel, d.AvailableFreeSpace / 1e9, d.TotalSize / 1e9));
            }
            catch
            {
                // a drive that went away while we looked
            }
        }
        AddDriveCheck(report);

        // ---- agents ----------------------------------------------------------------------------------------
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        report.ClaudeFound = EnvironmentPath.Find("claude.exe", "claude.cmd") != null
                             || File.Exists(Path.Combine(profile, @".local\bin\claude.exe"))
                             || Directory.Exists(Path.Combine(profile, ".claude"));
        report.CodexFound = EnvironmentPath.Find("codex.exe", "codex.cmd") != null
                            || Directory.Exists(Path.Combine(local, @"Programs\OpenAI\Codex"))
                            || Directory.Exists(Path.Combine(profile, ".codex"));

        ApplyFake(report);
        return report;
    }

    private static void AddDriveCheck(SystemReport report)
    {
        report.Checks.RemoveAll(c => c.Id == "devdrive");
        report.Checks.Add(report.RefsDrives.Count > 0
            ? new CheckResult("devdrive", "Dev Drive", CheckState.Ok,
                report.RefsDrives.Count == 1 ? $"Dev Drive {report.RefsDrives[0].Letter} is ready" : $"{report.RefsDrives.Count} Dev Drives are ready",
                string.Join("\n", report.RefsDrives.Select(d => d.Display)))
            : new CheckResult("devdrive", "Dev Drive", CheckState.Missing, "No Dev Drive yet",
                "Worktrees live on a Dev Drive: a drive formatted with ReFS, where a copy of a file takes no extra space until the copy is changed. No ReFS drive was found on this PC."));
    }

    /// <summary>git.exe on PATH (the registry's PATH included), else in the folders Git for Windows installs to.</summary>
    private static string? FindGit()
    {
        var onPath = EnvironmentPath.Find("git.exe");
        if (onPath != null) return onPath;
        foreach (var dir in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Git\cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\cmd"),
        })
        {
            var exe = Path.Combine(dir, "git.exe");
            if (!File.Exists(exe)) continue;
            EnvironmentPath.Append(dir);   // so the scripts this app starts find it too
            return exe;
        }
        return null;
    }

    private static void ApplyFake(SystemReport report)
    {
        if (string.IsNullOrEmpty(FakeFile) || !File.Exists(FakeFile)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FakeFile));
            var r = doc.RootElement;
            void Replace(string id, string title, string okText, string missingText, string oldText)
            {
                var s = Json.String(r, id == "devdrive" ? "devDrive" : id);
                if (s == null) return;
                var state = s.ToLowerInvariant() switch { "ok" => CheckState.Ok, "missing" => CheckState.Missing, "tooold" => CheckState.TooOld, _ => CheckState.Unknown };
                var old = report[id];
                report.Checks.RemoveAll(c => c.Id == id);
                var summary = state switch { CheckState.Ok => okText, CheckState.Missing => missingText, CheckState.TooOld => oldText, _ => $"{title} could not be checked" };
                string detail = old != null && old.State == state ? old.Detail : (id, state) switch
                {
                    ("windows", CheckState.TooOld) => $"Windows 11 version 24H2 (build {MinWindowsBuild}) or later is needed for copies that take no extra space. Worktrees still work on older versions, but each one takes the full size of your project.",
                    ("git", CheckState.Missing) => "Git for Windows 2.38 or later is needed. It was not found on PATH or in the usual install folders.",
                    ("git", CheckState.TooOld) => "Git 2.38 or later is needed.",
                    ("devdrive", CheckState.Missing) => "Worktrees live on a Dev Drive: a drive formatted with ReFS, where a copy of a file takes no extra space until the copy is changed. No ReFS drive was found on this PC.",
                    _ => summary + ".",
                };
                var result = new CheckResult(id, title, state, summary, detail);
                int at = id == "windows" ? 0 : id == "git" ? Math.Min(1, report.Checks.Count) : report.Checks.Count;
                report.Checks.Insert(at, result);
            }
            if (Json.Get(r, "drives") is { ValueKind: JsonValueKind.Array } drives)
            {
                report.RefsDrives.Clear();
                foreach (var d in drives.EnumerateArray())
                {
                    double Num(string n) => Json.Get(d, n) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : 0;
                    var letter = (Json.String(d, "letter") ?? "E:").TrimEnd('\\') + "\\";
                    report.RefsDrives.Add(new DriveChoice(letter, Json.String(d, "label") ?? "", Num("freeGB"), Num("totalGB")));
                }
                AddDriveCheck(report);
            }
            Replace("windows", "Windows", "Windows is new enough", "Windows needs an update", "Windows needs an update");
            Replace("git", "Git", "Git is installed", "Git is not installed", "Git is too old");
            Replace("devdrive", "Dev Drive", "A Dev Drive is ready", "No Dev Drive yet", "No Dev Drive yet");
            if (report["devdrive"]?.IsOk != true) report.RefsDrives.Clear();
            if (Json.Get(r, "claude") is { } c && c.ValueKind is JsonValueKind.True or JsonValueKind.False) report.ClaudeFound = c.GetBoolean();
            if (Json.Get(r, "codex") is { } x && x.ValueKind is JsonValueKind.True or JsonValueKind.False) report.CodexFound = x.GetBoolean();
        }
        catch (Exception ex)
        {
            AppLog.Write($"fake checks: {ex.Message}");
        }
    }
}

/// <summary>
/// The PATH this process (and every script it starts) searches. A program installed while the app is open is on
/// the PATH in the registry but not in this process's copy; RefreshFromRegistry adds what is missing.
/// </summary>
public static class EnvironmentPath
{
    public static void RefreshFromRegistry()
    {
        try
        {
            var current = Entries();
            var known = new HashSet<string>(current.Select(Normalize), StringComparer.OrdinalIgnoreCase);
            var added = new List<string>();
            foreach (var value in new[]
            {
                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")?.GetValue("Path") as string,
                Registry.CurrentUser.OpenSubKey("Environment")?.GetValue("Path") as string,
            })
            {
                foreach (var raw in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var dir = Environment.ExpandEnvironmentVariables(raw.Trim());
                    if (dir.Length > 0 && known.Add(Normalize(dir))) added.Add(dir);
                }
            }
            if (added.Count > 0) Environment.SetEnvironmentVariable("PATH", string.Join(";", current.Concat(added)));
        }
        catch (Exception ex)
        {
            AppLog.Write($"PATH refresh: {ex.Message}");
        }
    }

    public static void Append(string dir)
    {
        var current = Entries();
        if (current.Any(e => Normalize(e).Equals(Normalize(dir), StringComparison.OrdinalIgnoreCase))) return;
        Environment.SetEnvironmentVariable("PATH", string.Join(";", current.Append(dir)));
    }

    /// <summary>The first of <paramref name="files"/> found in a PATH folder.</summary>
    public static string? Find(params string[] files)
    {
        foreach (var dir in Entries())
        {
            foreach (var file in files)
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), file);
                    if (File.Exists(full)) return full;
                }
                catch
                {
                    // an entry that is not a path
                }
            }
        }
        return null;
    }

    private static List<string> Entries() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Normalize(string dir) => dir.Trim().TrimEnd('\\');
}
