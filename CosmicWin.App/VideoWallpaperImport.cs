using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CosmicWin.App;

/// <summary>
/// Imports the video the user picked from the tray menu (or the HTTP endpoint) into
/// <c>%LOCALAPPDATA%\CosmicWin\</c>, so playback survives the user moving or deleting the original
/// file. When the source lives on the same volume, the import is a HARD LINK rather than a byte
/// copy -- see decision 5 in <c>odd/tasks/video-wallpaper-http-endpoint.md</c> -- so switching to a
/// multi-gigabyte file is instant instead of taking minutes; a different volume, or any link
/// failure, falls back to the copy this class always did.
/// </summary>
/// <remarks>
/// Mirrors <see cref="SettingsFile"/>'s testable-path convention: a parameterless convenience
/// overload plus a path-taking one a test can point at a temporary directory. The link attempt
/// itself is injected through a third, internal overload (<see cref="Import(string, string,
/// Func{string, string, bool})"/>), the same convention applied one level deeper: production code
/// calls the two-argument overload, which supplies the real <c>CreateHardLinkW</c>-backed
/// attempt, and a test supplies a fake one to exercise the fallback without needing a second
/// volume.
///
/// CRITICAL invariant: after a linked import, <c>video-wallpaper&lt;ext&gt;</c> is not a copy of
/// the user's source video -- it IS the same file's data, reached through a second directory
/// entry. Nothing downstream may ever open that destination for writing, or copy onto it in
/// place: doing so would silently rewrite the user's own source file. The temp-then-move scheme
/// below already guarantees this for playback (the destination is only ever produced by moving a
/// freshly-created temp file into place, never edited after the fact), and the copy fallback keeps
/// exactly the same shape -- it only ever writes bytes to its own fresh temp name, never to the
/// destination directly -- so this invariant holds on both paths.
///
/// Holding the SOURCE open elsewhere, even without <see cref="FileShare.Delete"/>, does not block
/// that move: NTFS checks delete sharing against the name that was opened, not against other hard
/// links to the same file (measured 2026-09-25, and pinned by a test). So a linked import is never
/// worse than the copy it replaced, and there is deliberately no "link succeeded, move failed,
/// retry as a copy" branch.
/// </remarks>
public static class VideoWallpaperImport
{
    /// <summary>Beside <c>settings.conf</c> and the other files CosmicWin writes.</summary>
    public static string ResolveDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin");

    /// <summary>Imports <paramref name="sourcePath"/> into <see cref="ResolveDirectory"/>.</summary>
    public static string Import(string sourcePath) => Import(ResolveDirectory(), sourcePath);

    /// <summary>
    /// Imports <paramref name="sourcePath"/> into <paramref name="directory"/> under a FIXED
    /// destination name (<c>video-wallpaper</c> plus the source's own extension), not the original
    /// file name -- v1 supports exactly one active video, so re-picking must cleanly overwrite the
    /// previous import rather than accumulating orphan files here. When <paramref name="sourcePath"/>
    /// already IS that fixed destination -- picking the file CosmicWin already imported, e.g. via a
    /// file picker's own "recent files" list, or the tray closure re-playing a previous import on a
    /// restore -- the import is skipped entirely and the destination is returned unchanged: asking
    /// <see cref="File.Copy(string, string, bool)"/> to copy a file onto itself throws, and linking
    /// a file to itself makes no sense either.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT catch and swallow, unlike <see cref="SettingsFile.Save(string,
    /// Settings)"/>. A failed settings write is invisible and not worth crashing a running window
    /// manager over; a failed video import is something the user picking a file right now needs to
    /// know about immediately -- the source could be on removable media that was just ejected, or
    /// locked by another process -- so <see cref="File.Copy(string, string, bool)"/>, <see
    /// cref="File.Move(string, string, bool)"/> and <see cref="Directory.CreateDirectory(string)"/>
    /// are left to throw normally. The one exception is the same-file short-circuit above: that is
    /// not an import failure, it is an import that was never necessary in the first place.
    /// </remarks>
    public static string Import(string directory, string sourcePath) =>
        Import(directory, sourcePath, TryCreateHardLink);

    /// <summary>
    /// The same import, with the hard-link attempt injected as <paramref name="tryCreateHardLink"/>
    /// -- <c>internal</c> so <c>CosmicWin.App.Tests</c> can exercise the copy fallback (an access
    /// failure, a non-NTFS volume, a different volume from a second real drive) deterministically,
    /// without needing a second volume attached to the test machine.
    /// </summary>
    internal static string Import(string directory, string sourcePath, Func<string, string, bool> tryCreateHardLink)
    {
        Directory.CreateDirectory(directory);

        // A temp file can outlive a failed import when its own cleanup below could not run (the
        // process died mid-copy, or the delete itself failed). Best effort, and never worth failing
        // today's import over: it only ever removes names matching the temp pattern this method
        // itself produces, never the fixed destination or anything a caller put here.
        // REQUIRES Import calls into one directory to be serialized: the sweep would delete another
        // in-flight import's temp. Production guarantees it -- AppComposition only imports inside
        // SwitchVideoWallpaper's work item on the single video wallpaper thread, or inline on the
        // tray thread when there is no video wallpaper thread at all, never both. A new caller
        // (the HTTP route included) must go through SwitchVideoWallpaper, not call this directly.
        SweepLeftoverTempFiles(directory);

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var destination = Path.Combine(directory, "video-wallpaper" + extension);

        // Compared as full, normalised paths -- Windows paths are case-insensitive, and the raw
        // path handed in here need not already be in the same casing or use the same directory
        // separators as Path.Combine produced above.
        if (string.Equals(
                Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }

        // F2 (video-wallpaper-review-followups, R3-restore-replays-partial-copy): copying (or
        // linking) STRAIGHT onto the fixed destination used to mean a copy that failed midway --
        // the source ejected, or an I/O error partway through a multi-gigabyte file -- left a
        // partially overwritten destination in place, and the constraint's own restore path would
        // then happily replay that half-written file as if it were the previous, working import.
        // Importing to a temporary file BESIDE the destination first, then moving it into place,
        // means the destination is only ever replaced by a file that imported completely: a
        // failure before the move leaves the previous import exactly as it was. This holds
        // regardless of which of the two paths below produced the temp file.
        var temp = NewTempPath(directory, extension);
        try
        {
            // Decision 5: try the hard link first -- it is instant no matter the file size, because
            // it only adds a second directory entry pointing at the source's existing data, rather
            // than reading and rewriting every byte. It only succeeds on the same volume as
            // `directory` (NTFS enforces this), so this naturally covers "different volume" too;
            // TryCreateHardLink swallows every other failure (non-NTFS, access denied, anything)
            // the same way, because a link failure here is never the caller's problem to see --
            // the byte copy below is the well-understood fallback that already worked before this
            // task existed.
            if (!tryCreateHardLink(temp, sourcePath))
            {
                File.Copy(sourcePath, temp, overwrite: true);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            // Best effort, and deliberately never lets a cleanup failure mask the original one --
            // the caller needs to see WHY the import failed, not why the leftover temp file could
            // not be removed.
            TryDeleteBestEffort(temp);
            throw;
        }

        return destination;
    }

    /// <summary>
    /// noop-followups, F1: a point-in-time fingerprint of a file's NTFS identity (volume serial
    /// number plus file index) AND its size and last-write time -- everything
    /// <c>GetFileInformationByHandle</c> reports that can tell two moments of the SAME file apart.
    /// Value equality (a <see langword="record struct"/>) is exactly what a same-video comparison
    /// needs: two snapshots taken of the same on-disk state must compare equal, one taken before
    /// and one taken after an in-place edit must not.
    /// </summary>
    /// <remarks>
    /// Identity ALONE (what the superseded <c>IsSameFile</c> compared) cannot detect an in-place
    /// edit -- a video re-encoded or edited without ever being renamed keeps its volume serial and
    /// file index. Comparing size/last-write of the REQUESTED path against a FRESH read of the
    /// imported destination cannot detect it either: with a hard-linked import the two names are
    /// the same file, so a fresh read of either always agrees with a fresh read of the other,
    /// edited or not -- both readings simply describe whatever the file currently is. Detecting an
    /// edit requires comparing a fresh read against a STALE snapshot taken before the edit, which
    /// is why <c>AppComposition</c> caches one of these the moment playback actually starts,
    /// rather than re-reading the imported path at compare time.
    /// </remarks>
    // `public`, not `internal` like TryReadSnapshot below and the rest of this class: this type
    // appears in AppComposition.Wire's own public readVideoFileSnapshot parameter (a Func<string,
    // VideoFileSnapshot?>), and C# requires a public member's signature to expose nothing less
    // accessible than the member itself. The reader method that PRODUCES one stays internal --
    // only the shape of the value needs to be visible outside this assembly.
    public readonly record struct VideoFileSnapshot(
        uint VolumeSerialNumber, ulong FileIndex, long Size, long LastWriteTime);

    /// <summary>
    /// Reads <paramref name="path"/>'s current <see cref="VideoFileSnapshot"/>, or
    /// <see langword="null"/> on any failure -- a missing file, a locked file, or anything else
    /// that keeps <c>GetFileInformationByHandle</c> from answering. Never throws: the caller
    /// (<c>AppComposition</c>'s <c>SafeReadSnapshot</c>) additionally wraps every call to the
    /// INJECTED reader in a try/catch of its own, since a test double is free to throw where this
    /// real implementation never does; either way, a failure here reads as "no snapshot", which
    /// <c>SwitchVideoWallpaper</c>'s skip check treats the same as "different" -- a false "same"
    /// would silently swallow a real switch, while a false "different" only costs the reload this
    /// check exists to avoid.
    /// </summary>
    /// <remarks>
    /// Opened for read attributes only, sharing read/write/delete with every other handle -- this
    /// check must never itself block a concurrent import's Stop/copy/move sequence, or hold a
    /// delete open against a file some other code is about to replace.
    /// </remarks>
    internal static VideoFileSnapshot? TryReadSnapshot(string path)
    {
        try
        {
            using var handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info))
            {
                return null;
            }

            return new VideoFileSnapshot(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                ((long)info.FileSizeHigh << 32) | info.FileSizeLow,
                info.LastWriteTime);
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    private static string NewTempPath(string directory, string extension) =>
        Path.Combine(directory, $"video-wallpaper{extension}.tmp-{Guid.NewGuid():N}");

    private static void TryDeleteBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort, see the callers above.
        }
    }

    /// <summary>
    /// Sweeps leftover <c>video-wallpaper*.tmp-*</c> files -- the fixed name this method's own temp
    /// files always start with -- so an import that could not clean up after itself does not leave
    /// them accumulating forever. Matches only that pattern: never the fixed
    /// destination itself, and never anything else a caller might keep in this directory.
    /// </summary>
    private static void SweepLeftoverTempFiles(string directory)
    {
        try
        {
            foreach (var leftover in Directory.EnumerateFiles(directory, "video-wallpaper*.tmp-*"))
            {
                TryDeleteBestEffort(leftover);
            }
        }
        catch
        {
            // Best effort -- see TryDeleteBestEffort. Enumerating the directory itself can throw
            // too (e.g. a momentary I/O hiccup), and that is never worth failing an import over.
        }
    }

    /// <summary>
    /// The real hard-link attempt behind the injectable overload above: true on success, false on
    /// ANY failure -- <c>CreateHardLinkW</c> already reports a different volume (<c>
    /// ERROR_NOT_SAME_DEVICE</c>), a non-NTFS destination, or a permission problem the same way,
    /// as a plain <see langword="false"/> return plus <c>GetLastError</c>, so there is nothing here
    /// worth distinguishing; the catch guards only the unexpected (e.g. marshalling) case, so this
    /// never throws past the caller either way.
    /// </summary>
    private static bool TryCreateHardLink(string linkPath, string existingFilePath)
    {
        try
        {
            return CreateHardLinkW(linkPath, existingFilePath, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    // Single-use, like WebViewAlertLayerController's own inline DllImport -- this file is the only
    // caller in the solution, so a shared NativeMethods home in CosmicWin.Interop would just add an
    // indirection for one P/Invoke.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
