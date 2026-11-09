// CowCopy - copy a directory tree with CopyFileExW, which block-clones on ReFS.
// Loaded by cow_worktree.ps1 and migrate_worktree.ps1 through Add-Type (C# 5, .NET Framework). Every path is used in its
// \\?\ form, so paths longer than 260 characters work regardless of PowerShell's own limits.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

public static class CowCopy
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileExW(string pattern, int infoLevel, out FindData data, int searchOp, IntPtr filter, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextFileW(IntPtr handle, out FindData data);
    [DllImport("kernel32.dll")]
    static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CopyFileExW(string existing, string target, IntPtr progress, IntPtr data, IntPtr cancel, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, ref long input, int inputSize,
                                       byte[] output, int outputSize, out int returned, IntPtr overlapped);

    // The physical cluster (LCN) where a file's first extent starts, or -1 if it has none. Two files
    // that start on the same cluster share storage: that is what a block clone looks like.
    public static long FirstCluster(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long startingVcn = 0;
            var buffer = new byte[4096];
            int returned;
            // FSCTL_GET_RETRIEVAL_POINTERS; ERROR_MORE_DATA (234) still fills the first extents.
            if (!DeviceIoControl(fs.SafeFileHandle, 0x00090073, ref startingVcn, 8, buffer, buffer.Length, out returned, IntPtr.Zero)
                && Marshal.GetLastWin32Error() != 234) return -1;
            if (BitConverter.ToInt32(buffer, 0) == 0) return -1;
            return BitConverter.ToInt64(buffer, 24);   // ExtentCount, StartingVcn, then {NextVcn, Lcn}
        }
    }

    const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;
    const uint CopyFailIfExists = 0x1;
    const int FindExInfoBasic = 1, FindExLargeFetch = 2, ErrorNoMoreFiles = 18, ErrorAlreadyExists = 183;
    static readonly IntPtr InvalidHandle = new IntPtr(-1);

    static string Extended(string path)
    {
        return path.StartsWith(@"\\?\") ? path.TrimEnd('\\') : @"\\?\" + Path.GetFullPath(path).TrimEnd('\\');
    }

    static IOException Error(string what, string path)
    {
        return new IOException(what + " " + path + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    const int ErrorSharingViolation = 32, ErrorLockViolation = 33, ErrorFileNotFound = 2, ErrorPathNotFound = 3;

    // Files that disappeared between listing and copying (a build in the source deleting them).
    public static int SkippedGone;

    // Copies one file (long paths allowed), creating its parent directories; overwrite replaces an
    // existing target. Keeps the source's last-write time, as CopyFileExW always does.
    public static void CopyOne(string source, string destination, bool overwrite)
    {
        string src = Extended(source), dst = Extended(destination);
        int cut = dst.LastIndexOf('\\');
        string parent = dst.Substring(0, cut);
        var missing = new Stack<string>();
        while (parent.Length > 7 && !Directory.Exists(parent))   // "\\?\E:\" is 7 characters
        {
            missing.Push(parent);
            parent = parent.Substring(0, parent.LastIndexOf('\\'));
        }
        while (missing.Count > 0)
        {
            string dir = missing.Pop();
            if (!CreateDirectoryW(dir, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorAlreadyExists) throw Error("cannot create", dir);
        }
        if (!CopyFileExW(src, dst, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, overwrite ? 0u : CopyFailIfExists))
            throw Error("cannot copy", source);
    }

    // Files left out because another process held them open (a build writing into the source while it
    // is cloned). A tracked file among them shows up as deleted in git status, which the caller checks.
    public static int SkippedBusy;

    // Copies everything under source into destination (which must exist), except the top-level entry
    // named skip. Returns the number of files copied. Refuses reparse points rather than guess.
    public static int Run(string source, string destination, string skip, int threads)
    {
        return Run(source, destination, skip, threads, false);
    }

    // overwrite: replace files that already exist in destination (migration copies nested entries twice).
    public static int Run(string source, string destination, string skip, int threads, bool overwrite)
    {
        uint copyFlags = overwrite ? 0u : CopyFailIfExists;
        SkippedBusy = 0;
        SkippedGone = 0;
        string src = Extended(source), dst = Extended(destination);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.Count > 0)
        {
            string rel = pending.Pop();
            string dir = rel.Length == 0 ? src : src + "\\" + rel;
            FindData data;
            IntPtr handle = FindFirstFileExW(dir + "\\*", FindExInfoBasic, out data, 0, IntPtr.Zero, FindExLargeFetch);
            if (handle == InvalidHandle) throw Error("cannot list", dir);
            try
            {
                do
                {
                    if (data.Name == "." || data.Name == "..") continue;
                    if (rel.Length == 0 && string.Equals(data.Name, skip, StringComparison.OrdinalIgnoreCase)) continue;
                    string child = rel.Length == 0 ? data.Name : rel + "\\" + data.Name;
                    if ((data.Attributes & AttributeReparsePoint) != 0)
                        throw new IOException("reparse point in the source, not supported: " + child);
                    if ((data.Attributes & AttributeDirectory) != 0)
                    {
                        if (!CreateDirectoryW(dst + "\\" + child, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorAlreadyExists)
                            throw Error("cannot create", dst + "\\" + child);
                        pending.Push(child);
                    }
                    else
                    {
                        files.Add(child);
                    }
                } while (FindNextFileW(handle, out data));
                if (Marshal.GetLastWin32Error() != ErrorNoMoreFiles) throw Error("cannot list", dir);
            }
            finally
            {
                FindClose(handle);
            }
        }

        Exception failure = null;
        int copied = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = threads }, (rel, state) =>
        {
            for (int attempt = 0; ; attempt++)
            {
                if (CopyFileExW(src + "\\" + rel, dst + "\\" + rel, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, copyFlags))
                {
                    Interlocked.Increment(ref copied);
                    return;
                }
                int error = Marshal.GetLastWin32Error();
                bool busy = error == ErrorSharingViolation || error == ErrorLockViolation;
                // Gone since it was listed: a build running in the source deleted it. Nothing to copy.
                if (error == ErrorFileNotFound || error == ErrorPathNotFound) { Interlocked.Increment(ref SkippedGone); return; }
                if (busy && attempt < 5) { Thread.Sleep(200); continue; }
                if (busy) { Interlocked.Increment(ref SkippedBusy); return; }
                Interlocked.CompareExchange(ref failure, new IOException("cannot copy " + rel + ": " + new Win32Exception(error).Message), null);
                state.Stop();
                return;
            }
        });
        if (failure != null) throw failure;
        return copied;
    }
}
