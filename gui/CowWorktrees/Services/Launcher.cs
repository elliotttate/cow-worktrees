using System.Diagnostics;

namespace CowWorktrees.Services;

/// <summary>Opens a worktree in Explorer, a terminal, or an editor.</summary>
public static class Launcher
{
    public static void Explorer(string path) => Start("explorer.exe", path);

    /// <summary>Windows Terminal when it is installed, else a Windows PowerShell console.</summary>
    public static void Terminal(string path)
    {
        var wt = FindOnPath("wt.exe");
        if (wt != null)
        {
            Start(wt, "-d", path);
            return;
        }
        var psi = new ProcessStartInfo(CowScript.PowerShellExe) { UseShellExecute = true, WorkingDirectory = path };
        psi.ArgumentList.Add("-NoExit");
        Process.Start(psi)?.Dispose();
    }

    /// <summary>The editor "Open in editor" starts, and its display name.</summary>
    public static (string? Path, string Name) FindEditor(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return (configured, System.IO.Path.GetFileNameWithoutExtension(configured));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var (p, n) in new[]
        {
            (System.IO.Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe"), "VS Code"),
            (System.IO.Path.Combine(pf, @"Microsoft VS Code\Code.exe"), "VS Code"),
            (System.IO.Path.Combine(local, @"Programs\Microsoft VS Code Insiders\Code - Insiders.exe"), "VS Code Insiders"),
        })
        {
            if (File.Exists(p)) return (p, n);
        }
        var codeCmd = FindOnPath("code.cmd");
        if (codeCmd != null) return (codeCmd, "VS Code");
        var cursor = System.IO.Path.Combine(local, @"Programs\cursor\Cursor.exe");
        if (File.Exists(cursor)) return (cursor, "Cursor");
        return (null, "editor");
    }

    public static void Editor(string editorPath, string folder)
    {
        if (editorPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || editorPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(editorPath);
            psi.ArgumentList.Add(folder);
            Process.Start(psi)?.Dispose();
            return;
        }
        Start(editorPath, folder);
    }

    private static void Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    private static string? FindOnPath(string file)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = System.IO.Path.Combine(dir.Trim(), file);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}

public static class Fmt
{
    public static string Relative(DateTime? t)
    {
        if (t is not { } time) return "–";
        var d = DateTime.Now - time;
        if (d.TotalSeconds < 0) return "just now";
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 14) return $"{(int)d.TotalDays} d ago";
        return time.ToString(time.Year == DateTime.Now.Year ? "MMM d" : "MMM d yyyy");
    }

    public static string Duration(TimeSpan d) =>
        d.TotalSeconds < 60 ? $"{d.TotalSeconds:0.0} s" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}:{d.Seconds:00} min" : $"{(int)d.TotalHours} h {d.Minutes} min";

    public static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
}
