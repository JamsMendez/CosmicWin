using System.IO;
using System.Runtime.InteropServices;

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
/// volume. A fourth, internal overload injects the final move-into-place the same way, so V1c's
/// own fallback (below) can be driven deterministically by a test instead of depending on a real
/// sharing violation that, measured directly, does not reproduce through ordinary <see
/// cref="FileShare"/> locks on this filesystem.
///
/// CRITICAL invariant: after a linked import, <c>video-wallpaper&lt;ext&gt;</c> is not a copy of
/// the user's source video -- it IS the same file's data, reached through a second directory
/// entry. Nothing downstream may ever open that destination for writing, or copy onto it in
/// place: doing so would silently rewrite the user's own source file. The temp-then-move scheme
/// below already guarantees this for playback (the destination is only ever produced by moving a
/// freshly-created temp file into place, never edited after the fact), and both the copy fallback
/// and V1c's own copy-after-failed-link fallback keep exactly the same shape -- each only ever
/// writes bytes to its own fresh temp name, never to the destination directly -- so this invariant
/// holds on every path.
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
    internal static string Import(string directory, string sourcePath, Func<string, string, bool> tryCreateHardLink) =>
        Import(directory, sourcePath, tryCreateHardLink, MoveIntoPlace);

    /// <summary>
    /// The same import again, with the final move ALSO injected as <paramref name="moveIntoPlace"/>
    /// -- <c>internal</c>, for the same reason as <paramref name="tryCreateHardLink"/> one level up:
    /// V1c's own fallback below (drop a link whose move into place failed, and retry with an
    /// ordinary copy) needs a deterministic way to simulate that failure, because a real sharing
    /// violation on the linked move turned out NOT to reproduce through ordinary <see
    /// cref="FileShare"/> locks when measured directly against this filesystem (see the remarks on
    /// <see cref="VideoWallpaperImport"/> and the tests this seam exists for).
    /// </summary>
    internal static string Import(
        string directory,
        string sourcePath,
        Func<string, string, bool> tryCreateHardLink,
        Action<string, string> moveIntoPlace)
    {
        Directory.CreateDirectory(directory);

        // V1c: a move into place that fails AFTER a successful link (see below) may leave its temp
        // link behind forever, because the very thing that blocked the move -- something else
        // holding the file open without delete access -- is just as likely to block deleting that
        // same temp name right afterwards. Best effort, and never worth failing today's import
        // over: it only ever removes names matching the temp pattern this method itself produces,
        // never the fixed destination or anything a caller put here.
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
        var linked = false;
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
            linked = tryCreateHardLink(temp, sourcePath);
            if (!linked)
            {
                File.Copy(sourcePath, temp, overwrite: true);
            }

            moveIntoPlace(temp, destination);
        }
        catch when (linked)
        {
            // V1c (R3-linked-destination-inherits-source-sharing): once linked, `temp` IS the
            // source's own file object, so the one thing that can make THIS move fail where a
            // plain copy's move never would is something else holding that data open in a way that
            // blocks the rename -- and a link must never end up worse than the copy this class
            // always did. `temp` itself is likely stuck for the very same reason, so its best-effort
            // delete below is expected to sometimes fail quietly; SweepLeftoverTempFiles above is
            // what actually reclaims it, on a LATER import once whatever was holding it lets go.
            TryDeleteBestEffort(temp);

            var copyTemp = NewTempPath(directory, extension);
            try
            {
                File.Copy(sourcePath, copyTemp, overwrite: true);
                moveIntoPlace(copyTemp, destination);
            }
            catch
            {
                // Same best-effort cleanup contract as the outer catch below: never mask why the
                // fallback itself failed.
                TryDeleteBestEffort(copyTemp);
                throw;
            }

            return destination;
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
    /// files always start with -- so a move-into-place failure that could not even clean up after
    /// itself (V1c, above) does not accumulate forever. Matches only that pattern: never the fixed
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

    private static void MoveIntoPlace(string temp, string destination) =>
        File.Move(temp, destination, overwrite: true);

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
