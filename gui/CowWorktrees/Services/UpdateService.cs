using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CowWorktrees.Services;

/// <summary>A release that is newer than this app, and its installer if the release has a usable one.</summary>
public sealed record UpdateInfo(string Version, string Notes, string PageUrl, string? AssetUrl, string? AssetName, string? Digest, long Size)
{
    public bool HasInstaller => AssetUrl != null;
}

/// <summary>What became of a download: a verified installer, or the reason there is none.</summary>
public sealed record UpdateDownload(bool Verified, string Message, string? File);

/// <summary>
/// Looks at the latest release on GitHub and, when asked, downloads its installer. Rules that are not relaxed:
/// the check only reads; nothing is downloaded or started without a click; the installer must be the asset
/// named CowWorktrees-Setup-&lt;version&gt;.exe from this project's release downloads; its SHA-256 must equal the
/// digest GitHub publishes for the asset, or the file is deleted; only an installed copy starts it. The installer
/// stays in the updates folder while it runs; the version it installed removes it at its first start.
/// </summary>
public sealed class UpdateService
{
    public const string LatestReleaseApi = "https://api.github.com/repos/elliotttate/cow-worktrees/releases/latest";
    public const string DownloadPrefix = "https://github.com/elliotttate/cow-worktrees/releases/download/";
    public const string ReleasesPage = "https://github.com/elliotttate/cow-worktrees/releases";

    private readonly string _api;
    private readonly string _downloadPrefix;

    /// <summary>True for the test feed (--update-feed on localhost): a download is verified but never started.</summary>
    public bool IsTestFeed { get; }

    /// <param name="testFeed">--update-feed: honoured only for http://127.0.0.1 and http://localhost, and only in an unattended run.</param>
    public UpdateService(string? testFeed = null)
    {
        if (testFeed != null && Uri.TryCreate(testFeed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            IsTestFeed = true;
            _api = testFeed;
            _downloadPrefix = uri.GetLeftPart(UriPartial.Authority) + "/";
        }
        else
        {
            _api = LatestReleaseApi;
            _downloadPrefix = DownloadPrefix;
        }
    }

    /// <summary>This app's version, for example "1.1.0" (the build's commit hash is not part of it).</summary>
    public static string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString(3) ?? "0.0.0";
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
    }

    public static string UpdatesFolder => Path.Combine(CowHome.Folder, "updates");

    /// <summary>The check is switched off by the setting, or by COW_NO_UPDATE_CHECK=1.</summary>
    public static bool DisabledByEnvironment => Environment.GetEnvironmentVariable("COW_NO_UPDATE_CHECK") == "1";

    /// <summary>"1.2.3", "v1.2.3" and "1.2.3-test" all read as 1.2.3; anything else as null.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) t = t[..cut];
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    public static bool IsNewer(string? candidate, string? current) =>
        Parse(candidate) is { } c && Parse(current) is { } now && c > now;

    /// <summary>
    /// The latest release when it is newer than this app and was not skipped; otherwise null, with the reason in
    /// <paramref name="why"/>. Never throws: a failure is logged and is not an error anyone has to see.
    /// </summary>
    public async Task<(UpdateInfo? Update, string Why)> CheckAsync(string? skippedVersion)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"cow-worktrees/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(_api).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string tag = Json.String(r, "tag_name") ?? "";
            var version = Parse(tag);
            if (version == null) return (null, $"the latest release has no usable version ({tag})");
            string v = version.ToString(3);
            if (!IsNewer(v, CurrentVersion)) return (null, $"up to date ({CurrentVersion}; latest is {v})");
            if (!string.IsNullOrEmpty(skippedVersion) && Parse(skippedVersion) == version) return (null, $"version {v} was skipped");
            if (Json.Get(r, "draft") is { ValueKind: JsonValueKind.True } || Json.Get(r, "prerelease") is { ValueKind: JsonValueKind.True })
                return (null, $"version {v} is a draft or a pre-release");

            string? assetUrl = null, assetName = null, digest = null;
            long size = 0;
            string wanted = $"CowWorktrees-Setup-{v}.exe";
            if (Json.Get(r, "assets") is { ValueKind: JsonValueKind.Array } assets)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = Json.String(a, "name") ?? "";
                    string url = Json.String(a, "browser_download_url") ?? "";
                    // the installer by its exact name, from this project's release downloads: anything else is ignored
                    if (!name.Equals(wanted, StringComparison.Ordinal) || !url.StartsWith(_downloadPrefix, StringComparison.Ordinal)) continue;
                    assetUrl = url;
                    assetName = name;
                    digest = Json.String(a, "digest");
                    size = Json.Get(a, "size") is { ValueKind: JsonValueKind.Number } s ? s.GetInt64() : 0;
                    break;
                }
            }
            string page = Json.String(r, "html_url") is { } h && h.StartsWith("https://github.com/elliotttate/cow-worktrees/", StringComparison.Ordinal) ? h : ReleasesPage;
            return (new UpdateInfo(v, Json.String(r, "body") ?? "", page, assetUrl, assetName, digest, size), "newer");
        }
        catch (Exception ex)
        {
            AppLog.Write($"update check: {ex.GetType().Name}: {ex.Message}");
            return (null, "the check failed (logged)");
        }
    }

    /// <summary>Downloads the installer and compares its SHA-256 with the release's digest. A file that fails is deleted.</summary>
    public async Task<UpdateDownload> DownloadAsync(UpdateInfo info, Action<double>? progress = null, CancellationToken ct = default)
    {
        if (info.AssetUrl == null || info.AssetName == null)
            return new UpdateDownload(false, "This release has no installer for this app. Get it from the release page.", null);
        if (info.Digest == null || !info.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || info.Digest.Length != 7 + 64)
            return new UpdateDownload(false, "The update could not be verified: the release does not publish a checksum for the installer. Get it from the release page.", null);

        string file = Path.Combine(UpdatesFolder, info.AssetName);
        try
        {
            Directory.CreateDirectory(UpdatesFolder);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"cow-worktrees/{CurrentVersion}");
            using (var response = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? info.Size;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Invoke(Math.Min(1.0, (double)done / total));
                }
            }
            string actual;
            await using (var fs = File.OpenRead(file))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            string expected = info.Digest[7..];
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
                AppLog.Write($"update: checksum mismatch for {info.AssetName}: expected {expected}, got {actual}");
                return new UpdateDownload(false, "The update could not be verified: the downloaded file does not match the release's checksum, so it was deleted. Get it from the release page.", null);
            }
            return new UpdateDownload(true, $"Version {info.Version} was downloaded and verified.", file);
        }
        catch (Exception ex)
        {
            TryDelete(file);
            AppLog.Write($"update download: {ex.GetType().Name}: {ex.Message}");
            return new UpdateDownload(false, ex is OperationCanceledException ? "The download was stopped." : $"The update could not be downloaded ({ex.Message}).", null);
        }
    }

    /// <summary>
    /// Removes the installers a finished update left in the updates folder: files named
    /// CowWorktrees-Setup-&lt;version&gt;.exe whose version is not newer than this app's. A newer one stays (it may be
    /// about to be started), and so does every other file. Returns how many could not be removed, which happens while
    /// the installer that started this app is still closing.
    /// </summary>
    public static int RemoveFinishedDownloads()
    {
        const string prefix = "CowWorktrees-Setup-";
        int inUse = 0;
        try
        {
            if (!Directory.Exists(UpdatesFolder) || Parse(CurrentVersion) is not { } now) return 0;
            foreach (string file in Directory.EnumerateFiles(UpdatesFolder, prefix + "*.exe"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (Parse(name[prefix.Length..]) is not { } version || version > now) continue;
                try
                {
                    File.Delete(file);
                    AppLog.Write($"update: removed {Path.GetFileName(file)}, which is no longer needed");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    inUse++;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"update: tidying the updates folder: {ex.GetType().Name}: {ex.Message}");
        }
        return inUse;
    }

    /// <summary>The same at once, and again after 20 seconds and a minute more for a file that was still in use.</summary>
    public static async Task RemoveFinishedDownloadsAsync()
    {
        foreach (int seconds in new[] { 0, 20, 60 })
        {
            if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            if (await Task.Run(RemoveFinishedDownloads).ConfigureAwait(false) == 0) return;
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { }
    }
}
