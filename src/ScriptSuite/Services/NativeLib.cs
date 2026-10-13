using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScriptSuite.Services;

/// <summary>Minimal libc bindings for privilege-confinement checks. The
/// elevated child (pkexec → root) must validate every path it opens WITHOUT
/// following attacker-planted symlinks and WITHOUT clobbering pre-existing
/// files (symlink/hardlink plants). .NET's File APIs follow symlinks and
/// truncate, so raw open(2) with O_NOFOLLOW|O_EXCL plus realpath(3)/stat(2)
/// is the auditable primitive here.
/// Linux x86-64 / glibc only (matches the published linux-x64 target):
/// struct-stat field offsets and flag values are ABI constants.</summary>
internal static class NativeLib
{
    private const int O_RDONLY = 0;
    private const int O_WRONLY = 1;
    private const int O_CREAT = 64;      // 0100
    private const int O_EXCL = 128;      // 0200
    private const int O_APPEND = 1024;   // 02000
    private const int O_NOFOLLOW = 131072; // 0400000
    private const int S_IRUSR_S_IWUSR = 0x180; // 0600 for created files

    // glibc x86-64 struct stat: st_dev(0:8) st_ino(8:8) st_nlink(16:8)
    // st_mode(24:4) st_uid(28:4) st_gid(32:4). Only mode/uid/gid are mapped.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct StatBuf
    {
        [FieldOffset(24)] public uint Mode;
        [FieldOffset(28)] public uint Uid;
        [FieldOffset(32)] public uint Gid;
    }

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc.so.6")]
    private static extern uint getegid();

    [DllImport("libc.so.6", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int stat(string pathname, out StatBuf buf);

    [DllImport("libc.so.6", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int open(string pathname, int flags, uint mode);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int fchown(int fd, uint owner, uint group);

    [DllImport("libc.so.6")]
    private static extern IntPtr getpwuid(uint uid);

    [DllImport("libc.so.6", CharSet = CharSet.Ansi)]
    private static extern IntPtr realpath(string path, IntPtr resolved);

    [DllImport("libc.so.6")]
    private static extern void free(IntPtr p);

    public static uint EffectiveUid() => geteuid();

    public static uint EffectiveGid() => getegid();

    /// <summary>Canonical path with all symlinks resolved (realpath(3)).
    /// Returns null when the path cannot be resolved.</summary>
    public static string? RealPath(string path)
    {
        IntPtr p = realpath(path, IntPtr.Zero);
        if (p == IntPtr.Zero)
            return null;
        try { return Marshal.PtrToStringAnsi(p); }
        finally { free(p); }
    }

    /// <summary>Owner uid + zero low-permission bits of mode, or null.</summary>
    public static (uint Uid, uint Mode)? StatOwner(string path)
    {
        if (stat(path, out var buf) != 0)
            return null;
        return (buf.Uid, buf.Mode);
    }

    /// <summary>True when the final component is a symlink (lstat-equivalent
    /// via .NET link resolution — no syscalls beyond what FileInfo does).</summary>
    public static bool IsSymlink(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).LinkTarget is not null;
            if (Directory.Exists(path))
                return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch { return true; } // unreadable: treat as unsafe
        return false; // missing: not a symlink (existence is checked separately)
    }

    /// <summary>Open for writing, creating exclusively: fails when the file
    /// already exists (EEXIST — hardlink/symlink/regular plant) or is a
    /// symlink (ELOOP via O_NOFOLLOW). The returned stream holds the fd.</summary>
    public static FileStream CreateExclusive(string path, bool append = false)
    {
        int flags = O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | (append ? O_APPEND : 0);
        int fd = open(path, flags, S_IRUSR_S_IWUSR);
        if (fd < 0)
            throw new InvalidOperationException(
                $"Refusing to create '{path}' (already exists or is a symlink; errno={Marshal.GetLastWin32Error()}).");
        return new FileStream(new SafeFileHandle((nint)fd, ownsHandle: true), FileAccess.Write);
    }

    /// <summary>Open an existing file for reading without following a
    /// trailing symlink (O_NOFOLLOW — fails ELOOP on symlinks).</summary>
    public static FileStream OpenNoFollow(string path)
    {
        int fd = open(path, O_RDONLY | O_NOFOLLOW, 0);
        if (fd < 0)
            throw new InvalidOperationException(
                $"Refusing to open '{path}' (missing or is a symlink; errno={Marshal.GetLastWin32Error()}).");
        return new FileStream(new SafeFileHandle((nint)fd, ownsHandle: true), FileAccess.Read);
    }

    /// <summary>Change owner:group of an OPEN file descriptor (fchown(2) —
    /// never path-based: no TOCTOU between validation and handoff, no
    /// symlink race by construction). Used by the root child to hand its
    /// O_EXCL|O_NOFOLLOW rendezvous files to the pkexec caller.</summary>
    public static void FChown(SafeFileHandle handle, uint uid, uint gid)
    {
        int fd = unchecked((int)handle.DangerousGetHandle().ToInt64());
        if (fchown(fd, uid, gid) != 0)
            throw new InvalidOperationException(
                $"fchown({uid},{gid}) failed (errno={Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Primary gid of a uid via getpwuid(3), or null when unknown.
    /// glibc x86-64 struct passwd: pw_gid sits at offset 20 (two pointers +
    /// two ints precede it). Single-threaded child use only (static storage).</summary>
    public static uint? PrimaryGid(uint uid)
    {
        IntPtr p = getpwuid(uid);
        if (p == IntPtr.Zero)
            return null;
        return unchecked((uint)Marshal.ReadInt32(p, 20));
    }
}
