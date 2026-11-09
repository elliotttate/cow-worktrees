using Microsoft.UI.Xaml;

namespace CowWorktrees;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppLog.Write($"unhandled: {e.Exception}");
            e.Handled = true;   // keep the monitor alive; the error is logged and shown in the status bar
            _window?.ShowError(e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"unobserved task: {e.Exception}");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = StartupOptions.Parse(Environment.GetCommandLineArgs());
        AppSettings.ReadOnly = options.Unattended;
        _window = new MainWindow(options);
        _window.Start();
    }
}

/// <summary>Errors that would otherwise vanish, and one line per script run, go to gui.log next to pools.json.</summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                File.AppendAllText(Path.Combine(AppSettings.Folder, "gui.log"), $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}

/// <summary>
/// Command line for unattended checks: --snapshot &lt;png&gt; renders the window after the first full refresh,
/// --dump &lt;json&gt; writes what every row shows, --exit quits afterwards, --hidden never shows or activates the
/// window (so a check does not take focus from whoever is at the PC), --tab worktrees|base|activity and
/// --select &lt;name&gt; pick what the snapshot shows.
/// </summary>
public sealed class StartupOptions
{
    public string? SnapshotPath { get; private set; }
    public string? DumpPath { get; private set; }
    public bool ExitAfter { get; private set; }
    public bool Hidden { get; private set; }
    public string? Tab { get; private set; }
    public string? Select { get; private set; }
    /// <summary>--pool &lt;name or root&gt;: the pool to show first (otherwise the one shown last).</summary>
    public string? Pool { get; private set; }
    /// <summary>--size 1100x700: window size in layout pixels (for checking narrow layouts).</summary>
    public (int Width, int Height)? Size { get; private set; }
    /// <summary>--drag 2:+80: before the snapshot, drag the edge after column 2 by 80 pixels, as the header sizer does.</summary>
    public (int Index, double Delta)? Drag { get; private set; }
    /// <summary>--sort Activity: before the snapshot, click that column header (twice with "--sort Activity:2").</summary>
    public (string Column, int Clicks)? SortClick { get; private set; }

    public bool Unattended => SnapshotPath != null || DumpPath != null;

    public static StartupOptions Parse(string[] argv)
    {
        var o = new StartupOptions();
        for (int i = 1; i < argv.Length; i++)
        {
            string a = argv[i];
            string? Next() => i + 1 < argv.Length ? argv[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--snapshot": o.SnapshotPath = Next(); break;
                case "--dump": o.DumpPath = Next(); break;
                case "--exit": o.ExitAfter = true; break;
                case "--hidden": o.Hidden = true; break;
                case "--tab": o.Tab = Next(); break;
                case "--select": o.Select = Next(); break;
                case "--pool": o.Pool = Next(); break;
                case "--drag":
                    var d = (Next() ?? "").Split(':');
                    if (d.Length == 2 && int.TryParse(d[0], out var di) && double.TryParse(d[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dx))
                        o.Drag = (di, dx);
                    break;
                case "--sort":
                    var sc = (Next() ?? "").Split(':');
                    o.SortClick = (sc[0], sc.Length > 1 && int.TryParse(sc[1], out var clicks) ? clicks : 1);
                    break;
                case "--size":
                    var parts = (Next() ?? "").Split('x', 'X');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)) o.Size = (w, h);
                    break;
            }
        }
        return o;
    }
}
