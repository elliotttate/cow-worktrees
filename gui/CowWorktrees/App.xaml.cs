using Microsoft.UI.Xaml;

namespace CowWorktrees;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // --theme light|dark: the theme can only be chosen here, before the first window exists
        var argv = Environment.GetCommandLineArgs();
        int t = Array.FindIndex(argv, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        if (t >= 0 && t + 1 < argv.Length)
        {
            if (argv[t + 1].Equals("light", StringComparison.OrdinalIgnoreCase)) RequestedTheme = ApplicationTheme.Light;
            else if (argv[t + 1].Equals("dark", StringComparison.OrdinalIgnoreCase)) RequestedTheme = ApplicationTheme.Dark;
        }
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
        Services.SystemChecks.FakeFile = options.FakeChecksPath;
        if (!options.Hidden && !options.Unattended && !ClaimSingleInstance())
        {
            // Another window of this app is open (perhaps hidden while a job finishes): it has been asked to come forward.
            Environment.Exit(0);
            return;
        }
        _window = new MainWindow(options);
        _window.Start();
    }

    // The installer waits for this mutex to go away before it replaces files (AppMutex in installer\CowWorktrees.iss).
    public const string InstanceMutexName = "cow-worktrees.app";
    private const string ShowEventName = "cow-worktrees.app.show";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    /// <summary>One window per person. A second start asks the first to show itself. Unattended runs take no part.</summary>
    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(true, InstanceMutexName, out bool first);
            if (!first)
            {
                try
                {
                    using var show = EventWaitHandle.OpenExisting(ShowEventName);
                    show.Set();
                }
                catch { }
                return false;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => dq.TryEnqueue(() => _window?.BringToFront()), null, Timeout.Infinite, false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"single instance: {ex.Message}");
            return true;   // never keep the app from starting over this
        }
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

    // ---- for tests and screenshots of the walkthrough, setup and updates (see gui\README.md) -------------------
    /// <summary>--wizard [step]: open the walkthrough on that screen (welcome, checks, project, location, tools, builds, review, done).</summary>
    public string? Wizard { get; private set; }
    /// <summary>--wizard-state &lt;json&gt;: answers to fill in (repo, root, baseRef, claude, codex, schedule, buildCommand, autoStart).</summary>
    public string? WizardStatePath { get; private set; }
    /// <summary>--fake-checks &lt;json&gt;: pretend Git or the Dev Drive is missing; honoured only with --hidden.</summary>
    public string? FakeChecksPath { get; private set; }
    /// <summary>--selftest-checks &lt;out.json&gt;: write what "Check this PC" finds and where the tool folder is, then exit.</summary>
    public string? SelfTestChecksPath { get; private set; }
    /// <summary>--run-setup &lt;options.json&gt;: run setup.ps1 or uninstall.ps1 the way the app does, write the outcome, then exit.</summary>
    public string? RunSetupPath { get; private set; }
    /// <summary>--settings: open Repository settings (for a snapshot).</summary>
    public bool OpenSettings { get; private set; }
    /// <summary>--update-feed &lt;url&gt;: a stand-in for the GitHub address; only http://127.0.0.1 or http://localhost, only with --hidden.</summary>
    public string? UpdateFeed { get; private set; }
    /// <summary>--update-result &lt;out.json&gt;: check the test feed, verify the download, write what would have happened; never starts a file.</summary>
    public string? UpdateResultPath { get; private set; }
    /// <summary>
    /// --measure [stop[:ms]]: before the snapshot or dump, measure the disk space of the repository shown (Base tab,
    /// Disk space) and keep the result as the app does. With "stop", press Stop that many milliseconds after the start.
    /// </summary>
    public MeasureOption? Measure { get; private set; }
    public sealed record MeasureOption(int? StopAfterMs);

    public bool Unattended => SnapshotPath != null || DumpPath != null || SelfTestChecksPath != null || RunSetupPath != null || UpdateResultPath != null;

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
                case "--wizard":
                    o.Wizard = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal) ? Next() : "welcome";
                    break;
                case "--wizard-state": o.WizardStatePath = Next(); break;
                case "--fake-checks": o.FakeChecksPath = Next(); break;
                case "--selftest-checks": o.SelfTestChecksPath = Next(); o.ExitAfter = true; break;
                case "--run-setup": o.RunSetupPath = Next(); o.ExitAfter = true; break;
                case "--settings": o.OpenSettings = true; break;
                case "--measure":
                    string how = i + 1 < argv.Length && argv[i + 1].StartsWith("stop", StringComparison.OrdinalIgnoreCase) ? Next()! : "";
                    var stop = how.Split(':');
                    o.Measure = new MeasureOption(how.Length == 0 ? null : stop.Length > 1 && int.TryParse(stop[1], out var stopMs) ? Math.Max(0, stopMs) : 0);
                    break;
                case "--theme": Next(); break;   // read in App's constructor
                case "--update-feed": o.UpdateFeed = Next(); break;
                case "--update-result": o.UpdateResultPath = Next(); o.ExitAfter = true; break;
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
        // The stand-ins are for tests only: without --hidden they are ignored.
        if (!o.Hidden)
        {
            o.FakeChecksPath = null;
            o.UpdateFeed = null;
        }
        return o;
    }
}
