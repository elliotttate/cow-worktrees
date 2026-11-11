using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CowWorktrees.Services;

/// <summary>A folder to measure: a checkout of the repository, or another folder inside the pool root.</summary>
public sealed record DiskFolder(string Path, string Name, string Kind, bool IsBase, bool IsWorktree, bool IsMain = false, bool InPool = true);

/// <summary>What one folder takes. All sizes are bytes on the drive (whole clusters).</summary>
public sealed class DiskFolderUsage
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The worktree's kind as the list shows it ("CoW base", "CoW", "checkout" ...), or "folder" for one Git does not know.</summary>
    public string Kind { get; set; } = "";
    public bool IsBase { get; set; }
    public bool IsWorktree { get; set; }
    /// <summary>The project's main folder: a full copy by nature, with Git's history in it.</summary>
    public bool IsMain { get; set; }
    /// <summary>Inside the pool root (the base, its worktrees, other folders there), as against a checkout elsewhere on the drive.</summary>
    public bool InPool { get; set; }
    /// <summary>Every file counted in full: what Explorer shows as the folder's size on disk.</summary>
    public long SizeBytes { get; set; }
    /// <summary>The lengths of the files added up: what Explorer shows as the folder's size.</summary>
    public long LogicalBytes { get; set; }
    /// <summary>Clusters no other measured folder uses: what deleting the folder gives back.</summary>
    public long OwnBytes { get; set; }
    /// <summary>Clusters it has in common with at least one other measured folder.</summary>
    public long SharedBytes { get; set; }
    /// <summary>The part of <see cref="SharedBytes"/> that the base has too.</summary>
    public long SharedWithBaseBytes { get; set; }
    /// <summary>
    /// The part of <see cref="OwnBytes"/> that Windows has not written to the drive yet (files saved in the last
    /// seconds). It has no place on the drive so far, and will get one that nothing else uses.
    /// </summary>
    public long PendingBytes { get; set; }
    public int Files { get; set; }
    /// <summary>Files whose place on the drive could not be read (gone meanwhile, or no access).</summary>
    public int Unreadable { get; set; }
    /// <summary>Why the folder was not measured at all.</summary>
    public string? Error { get; set; }

    /// <summary>Space counted more than once inside the folder itself: copies of a file that share their clusters.</summary>
    public long RepeatedBytes => Math.Max(0, SizeBytes - OwnBytes - SharedBytes);

    /// <summary>
    /// A checkout that has (next to) nothing in common with the base: a full copy, such as one made with plain
    /// `git worktree add`. Tiny folders are left out, since a few files prove nothing.
    /// </summary>
    public bool IsFullCopy => !IsBase && Error == null && OwnBytes + SharedBytes >= DiskSpaceService.FullCopyMinBytes
                              && SharedWithBaseBytes * 100 < OwnBytes + SharedBytes;
}

public sealed class DiskSpaceResult
{
    public string Root { get; set; } = "";
    /// <summary>"E:\"</summary>
    public string Drive { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public DateTime MeasuredAt { get; set; }
    public double Seconds { get; set; }
    public long ClusterSize { get; set; }
    /// <summary>The folders' sizes added up, as Explorer would.</summary>
    public long FolderBytes { get; set; }
    /// <summary>What the folders really hold on the drive: every cluster counted once.</summary>
    public long HeldBytes { get; set; }
    public long DriveFreeBytes { get; set; }
    public long DriveTotalBytes { get; set; }
    public int Files { get; set; }
    public int Unreadable { get; set; }
    public bool HasBase { get; set; }
    public List<DiskFolderUsage> Folders { get; set; } = new();
}

public readonly record struct DiskSpaceProgress(int Files, string Folder, int FoldersDone, int FolderCount);

/// <summary>
/// What really takes the space on a pool's drive. On ReFS a block-cloned file shares its clusters with the file
/// it was cloned from, so a folder's size says little: ten worktrees of a 100 GB project show as 1,000 GB and
/// hold little more than 100. This asks the file system where every file sits (FSCTL_GET_RETRIEVAL_POINTERS:
/// runs of clusters by their number on the volume), then walks the volume's cluster numbers once: a stretch
/// only one folder uses is that folder's own, one that several use is shared.
///
/// ReFS gives a file its clusters when its data is written out, not when a program saves it: for some seconds a
/// new file's run has no cluster number. Such data is counted as the folder's own, which it will be.
///
/// Only reads: every file is opened for its attributes alone, which also works while something else has it
/// open. "Own" is own among the measured folders; clusters shared with a file outside them (a copy made in
/// Explorer, another repository on the same drive) are not seen.
/// </summary>
public static class DiskSpaceService
{
    /// <summary>Below this a folder is too small to call a full copy.</summary>
    public const long FullCopyMinBytes = 1024 * 1024;

    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_ALL = 7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;
    private const int ERROR_HANDLE_EOF = 38;
    private const int ERROR_MORE_DATA = 234;
    private static readonly IntPtr InvalidHandle = new(-1);

    // RETRIEVAL_POINTERS_BUFFER read as 64-bit words: [0] = ExtentCount (low half), [1] = StartingVcn,
    // then one (NextVcn, Lcn) pair per extent.
    private const int ExtentsPerCall = 510;
    private const int BufferWords = 2 + 2 * ExtentsPerCall;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, ref long input, int inputSize, [Out] long[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    /// <summary>"E:\" for a path on drive E:; "" when the path has no drive letter.</summary>
    public static string DriveOf(string path)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            return root is { Length: 3 } && root[1] == ':' ? root.ToUpperInvariant() : "";
        }
        catch
        {
            return "";
        }
    }

    public static bool SameDrive(string a, string b) => DriveOf(a) is { Length: > 0 } d && d == DriveOf(b);

    /// <summary>
    /// The folders directly inside the pool root that are not among <paramref name="known"/> (the checkouts):
    /// leftovers of a removal, something put there by hand. The scripts' own lock folder is left out, and so are
    /// links to other places.
    /// </summary>
    public static List<DiskFolder> OtherFoldersIn(string poolRoot, IEnumerable<string> known)
    {
        var have = new HashSet<string>(known.Select(Pool.FullPath), StringComparer.OrdinalIgnoreCase);
        var list = new List<DiskFolder>();
        try
        {
            foreach (var dir in new DirectoryInfo(poolRoot).EnumerateDirectories())
            {
                if (have.Contains(dir.FullName.TrimEnd('\\')) || dir.Name.Equals(".locks", StringComparison.OrdinalIgnoreCase)) continue;
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                list.Add(new DiskFolder(dir.FullName.TrimEnd('\\'), dir.Name, "folder", IsBase: false, IsWorktree: false));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"disk space: could not list {poolRoot}: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// Measures <paramref name="folders"/>, which must all be on the drive of <paramref name="poolRoot"/> (others
    /// are reported with an error and count for nothing). Runs on the calling thread and a few workers; call it
    /// off the UI thread. Throws <see cref="OperationCanceledException"/> when stopped.
    /// </summary>
    public static DiskSpaceResult Measure(string poolRoot, IReadOnlyList<DiskFolder> folders, Action<DiskSpaceProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        string drive = DriveOf(poolRoot);
        if (drive.Length == 0) throw new InvalidOperationException($"{poolRoot} is not on a drive with a letter, so its space cannot be measured.");
        if (!GetDiskFreeSpaceW(drive, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
            throw new InvalidOperationException($"Drive {drive.TrimEnd('\\')} could not be read (error {Marshal.GetLastWin32Error()}).");
        long clusterSize = (long)sectorsPerCluster * bytesPerSector;

        var scans = new FolderScan[folders.Count];
        var all = new HashSet<string>(folders.Select(f => Pool.FullPath(f.Path)), StringComparer.OrdinalIgnoreCase);
        int filesSeen = 0, foldersDone = 0;
        long lastReport = 0;
        void Report(string folder, bool force)
        {
            if (progress == null) return;
            long now = clock.ElapsedMilliseconds;
            long last = Interlocked.Read(ref lastReport);
            if (!force && now - last < 120) return;
            if (Interlocked.CompareExchange(ref lastReport, now, last) != last && !force) return;
            progress(new DiskSpaceProgress(Volatile.Read(ref filesSeen), folder, Volatile.Read(ref foldersDone), folders.Count));
        }

        // The drive does the work, not the processor: a few folders at once is as fast as it gets.
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = ct };
        Parallel.For(0, folders.Count, options, i =>
        {
            var folder = folders[i];
            var scan = new FolderScan();
            scans[i] = scan;
            string path = Pool.FullPath(folder.Path);
            if (!string.Equals(DriveOf(path), drive, StringComparison.Ordinal)) scan.Error = $"not on drive {drive.TrimEnd('\\')}";
            else if (!Directory.Exists(path)) scan.Error = "the folder is gone";
            else if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) scan.Error = "a link to another folder";
            else
            {
                // A measured folder inside this one (Claude Code's worktrees live inside the project) is its own row.
                var inside = all.Where(p => p.Length > path.Length + 1 && p.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
                                .Select(p => @"\\?\" + p).ToHashSet(StringComparer.OrdinalIgnoreCase);
                try
                {
                    ScanFolder(path, inside, scan, ct, () =>
                    {
                        Interlocked.Add(ref filesSeen, ReportEvery);
                        Report(folder.Name, false);
                    }, out int rest);
                    Interlocked.Add(ref filesSeen, rest);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    scan.Error = ex.Message;
                    AppLog.Write($"disk space: {path}: {ex.Message}");
                }
                scan.Merge();
            }
            Interlocked.Increment(ref foldersDone);
            Report(folder.Name, true);
        });
        ct.ThrowIfCancellationRequested();

        int baseIndex = -1;
        for (int i = 0; i < folders.Count; i++)
            if (folders[i].IsBase && scans[i].Error == null) baseIndex = i;
        var (own, shared, withBase, held) = Sweep(scans, baseIndex, ct);
        foreach (var s in scans) held += s.PendingClusters;

        var result = new DiskSpaceResult
        {
            Root = Pool.FullPath(poolRoot),
            Drive = drive,
            MeasuredAt = DateTime.Now,
            ClusterSize = clusterSize,
            HeldBytes = held * clusterSize,
            HasBase = baseIndex >= 0,
        };
        try
        {
            var info = new DriveInfo(drive);
            result.FileSystem = info.DriveFormat;
            result.DriveFreeBytes = info.AvailableFreeSpace;
            result.DriveTotalBytes = info.TotalSize;
        }
        catch (Exception ex)
        {
            AppLog.Write($"disk space: drive {drive}: {ex.Message}");
        }
        for (int i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            var s = scans[i];
            var usage = new DiskFolderUsage
            {
                Path = Pool.FullPath(f.Path),
                Name = f.Name,
                Kind = f.Kind,
                IsBase = f.IsBase,
                IsWorktree = f.IsWorktree,
                IsMain = f.IsMain,
                InPool = f.InPool,
                SizeBytes = s.SizeClusters * clusterSize,
                LogicalBytes = s.LogicalBytes,
                OwnBytes = (own[i] + s.PendingClusters) * clusterSize,
                PendingBytes = s.PendingClusters * clusterSize,
                SharedBytes = shared[i] * clusterSize,
                SharedWithBaseBytes = withBase[i] * clusterSize,
                Files = s.Files,
                Unreadable = s.Unreadable,
                Error = s.Error,
            };
            result.Folders.Add(usage);
            result.FolderBytes += usage.SizeBytes;
            result.Files += usage.Files;
            result.Unreadable += usage.Unreadable;
        }
        result.Seconds = clock.Elapsed.TotalSeconds;
        return result;
    }

    private const int ReportEvery = 512;

    /// <summary>Where every file under <paramref name="path"/> sits on the volume. Links (junctions, symlinks) are not followed.</summary>
    private static void ScanFolder(string path, HashSet<string> skip, FolderScan scan, CancellationToken ct, Action tick, out int uncounted)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        var files = new FileSystemEnumerable<(string Path, long Length, FileAttributes Attributes)>(@"\\?\" + path, (ref FileSystemEntry e) => (e.ToFullPath(), e.Length, e.Attributes), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0,
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                (e.Attributes & FileAttributes.ReparsePoint) == 0 && (skip.Count == 0 || !skip.Contains(e.ToFullPath())),
        };
        var buffer = new long[BufferWords];
        int n = 0;
        foreach (var (file, length, attributes) in files)
        {
            if (++n == ReportEvery)
            {
                n = 0;
                ct.ThrowIfCancellationRequested();
                tick();
            }
            scan.Files++;
            scan.LogicalBytes += length;
            if (length == 0) continue;
            // In a sparse or compressed file a run without a cluster number is a hole; in any other it is data still to be written out.
            bool holes = (attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0;
            if (!ReadRuns(file, buffer, scan, holes)) scan.Unreadable++;
        }
        uncounted = n;
    }

    /// <summary>Adds the file's runs of clusters to <paramref name="scan"/>; false when the file could not be asked.</summary>
    private static bool ReadRuns(string file, long[] buffer, FolderScan scan, bool holes)
    {
        IntPtr handle = CreateFileW(file, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle == InvalidHandle) return false;
        try
        {
            long startVcn = 0;
            while (true)
            {
                bool ok = DeviceIoControl(handle, FSCTL_GET_RETRIEVAL_POINTERS, ref startVcn, sizeof(long), buffer, BufferWords * sizeof(long), out _, IntPtr.Zero);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error == ERROR_HANDLE_EOF) return true;   // nothing (more) on the volume: an empty file, or one kept inside its own record
                if (!ok && error != ERROR_MORE_DATA) return false;
                int count = (int)(buffer[0] & 0xFFFFFFFF);
                long vcn = buffer[1];
                for (int i = 0; i < count; i++)
                {
                    long next = buffer[2 + 2 * i];
                    long lcn = buffer[3 + 2 * i];
                    if (next > vcn)
                    {
                        if (lcn >= 0) scan.Add(lcn, next - vcn);
                        else if (!holes) scan.AddPending(next - vcn);
                    }
                    vcn = next;
                }
                if (ok || count == 0) return true;
                startVcn = vcn;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// One pass over the volume's cluster numbers. Every folder's runs are disjoint by now (<see cref="FolderScan.Merge"/>),
    /// so at any cluster a folder is either there or not; between two neighbouring run edges the set of folders
    /// does not change, and that stretch is counted for them: own when there is one, shared when there are more.
    /// </summary>
    private static (long[] Own, long[] Shared, long[] WithBase, long Held) Sweep(FolderScan[] scans, int baseIndex, CancellationToken ct)
    {
        int n = scans.Length;
        var own = new long[n];
        var shared = new long[n];
        var withBase = new long[n];
        long held = 0;

        long total = 0;
        foreach (var s in scans) total += s.Count;
        if (total == 0) return (own, shared, withBase, 0);
        if (total > int.MaxValue / 2 - 16) throw new InvalidOperationException("There are too many file fragments to measure in one go.");

        // An edge is a cluster number and what happens there: folder f starts (f) or ends (~f).
        var position = new long[total * 2];
        var what = new int[total * 2];
        int e = 0;
        for (int f = 0; f < n; f++)
        {
            var s = scans[f];
            for (int i = 0; i < s.Count; i++)
            {
                position[e] = s.Starts[i];
                what[e++] = f;
                position[e] = s.Starts[i] + s.Lengths[i];
                what[e++] = ~f;
            }
        }
        Array.Sort(position, what);
        ct.ThrowIfCancellationRequested();

        var active = new int[n];       // the folders present at the current cluster
        var slot = new int[n];         // where each one is in `active`
        int activeCount = 0;
        bool baseActive = false;
        long previous = position[0];
        int k = 0;
        while (k < position.Length)
        {
            long here = position[k];
            long length = here - previous;
            if (length > 0 && activeCount > 0)
            {
                held += length;
                if (activeCount == 1) own[active[0]] += length;
                else
                {
                    for (int a = 0; a < activeCount; a++)
                    {
                        shared[active[a]] += length;
                        if (baseActive && active[a] != baseIndex) withBase[active[a]] += length;
                    }
                }
            }
            // every edge at this cluster number, ends and starts alike, before the next stretch is counted
            while (k < position.Length && position[k] == here)
            {
                int w = what[k++];
                if (w >= 0)
                {
                    slot[w] = activeCount;
                    active[activeCount++] = w;
                    if (w == baseIndex) baseActive = true;
                }
                else
                {
                    int f = ~w;
                    int last = active[--activeCount];
                    active[slot[f]] = last;
                    slot[last] = slot[f];
                    if (f == baseIndex) baseActive = false;
                }
            }
            previous = here;
        }
        return (own, shared, withBase, held);
    }

    /// <summary>The runs of clusters of one folder's files.</summary>
    private sealed class FolderScan
    {
        public long[] Starts = new long[1024];
        public long[] Lengths = new long[1024];
        public int Count;
        public long SizeClusters;
        /// <summary>Clusters of data that has no place on the drive yet (see the class summary).</summary>
        public long PendingClusters;
        public long LogicalBytes;
        public int Files;
        public int Unreadable;
        public string? Error;

        public void Add(long lcn, long length)
        {
            if (Count == Starts.Length)
            {
                Array.Resize(ref Starts, Count * 2);
                Array.Resize(ref Lengths, Count * 2);
            }
            Starts[Count] = lcn;
            Lengths[Count++] = length;
            SizeClusters += length;
        }

        public void AddPending(long length)
        {
            PendingClusters += length;
            SizeClusters += length;
        }

        /// <summary>
        /// Sorts the runs and joins the ones that overlap or touch, so each cluster is in the folder once. Two files
        /// in one folder can sit on the same clusters: a copy made on a Dev Drive is a block clone, and a hard
        /// link is one file under two names.
        /// </summary>
        public void Merge()
        {
            if (Count == 0) return;
            Array.Sort(Starts, Lengths, 0, Count);
            int w = 0;
            long start = Starts[0], end = start + Lengths[0];
            for (int i = 1; i < Count; i++)
            {
                long s = Starts[i], e = s + Lengths[i];
                if (s <= end)
                {
                    if (e > end) end = e;
                    continue;
                }
                Starts[w] = start;
                Lengths[w++] = end - start;
                start = s;
                end = e;
            }
            Starts[w] = start;
            Lengths[w++] = end - start;
            Count = w;
        }
    }

    // ---- the last result, kept so the view has something to show at once ------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>disk-space\&lt;pool id&gt;.json next to pools.json.</summary>
    public static string ResultFile(Pool pool) => System.IO.Path.Combine(CowHome.Folder, "disk-space", pool.Id + ".json");

    public static DiskSpaceResult? LoadLast(Pool pool)
    {
        string file = ResultFile(pool);
        try
        {
            if (!File.Exists(file)) return null;
            var result = JsonSerializer.Deserialize<DiskSpaceResult>(File.ReadAllText(file), JsonOptions);
            return result != null && string.Equals(result.Root, pool.Root, StringComparison.OrdinalIgnoreCase) ? result : null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"disk space: could not read {file}: {ex.Message}");
            return null;
        }
    }

    public static void SaveLast(Pool pool, DiskSpaceResult result)
    {
        string file = ResultFile(pool);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Write($"disk space: could not write {file}: {ex.Message}");
        }
    }
}
