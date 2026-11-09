using System.Diagnostics;
using System.Text;

namespace CowWorktrees.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    public string[] OutLines => SplitLines(StdOut);

    public static string[] SplitLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    /// <summary>A one-line reason for a failure, for status text.</summary>
    public string Problem
    {
        get
        {
            if (TimedOut) return $"timed out after {Elapsed.TotalSeconds:N0} s";
            var err = SplitLines(StdErr);
            return err.Length > 0 ? err[^1] : $"exit code {ExitCode}";
        }
    }
}

/// <summary>
/// Runs a console process with no window and no stdin, collecting stdout and stderr and optionally
/// handing each line to a callback as it arrives. Only the process this class started (and its children)
/// is ever killed, on timeout or cancellation.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string, bool>? onLine = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        if (encoding != null)
        {
            psi.StandardOutputEncoding = encoding;
            psi.StandardErrorEncoding = encoding;
        }
        if (environment != null)
        {
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;
        }

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        try { proc.StandardInput.Close(); } catch { /* already gone */ }

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        Task Pump(StreamReader reader, StringBuilder sb, bool isErr) => Task.Run(async () =>
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lock (sb) sb.Append(line).Append('\n');
                try { onLine?.Invoke(line, isErr); } catch { /* a UI callback must not stop the pump */ }
            }
        });
        var outTask = Pump(proc.StandardOutput, outSb, false);
        var errTask = Pump(proc.StandardError, errSb, true);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } t && t > TimeSpan.Zero) cts.CancelAfter(t);
        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); } catch { /* exited meanwhile */ }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
            if (!timedOut) throw;
        }

        // A grandchild that inherited the pipes can keep them open after the process exits; don't wait forever.
        try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }

        string stdout, stderr;
        lock (outSb) stdout = outSb.ToString();
        lock (errSb) stderr = errSb.ToString();
        int code = proc.HasExited ? proc.ExitCode : -1;
        return new ProcessResult(code, stdout, stderr, sw.Elapsed, timedOut);
    }

    /// <summary>A command line for display (what you would type in a console).</summary>
    public static string Display(string fileName, IEnumerable<string> args) =>
        string.Join(" ", new[] { fileName }.Concat(args).Select(Quote));

    private static string Quote(string a) =>
        a.Length == 0 ? "\"\"" : a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
}
