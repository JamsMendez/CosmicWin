using System.IO;

namespace CosmicWin.App;

/// <summary>
/// Copies the video the user picked from the tray menu into <c>%LOCALAPPDATA%\CosmicWin\</c>, so
/// playback survives the user moving or deleting the original file.
/// </summary>
/// <remarks>
/// Mirrors <see cref="SettingsFile"/>'s testable-path convention: a parameterless convenience
/// overload plus a path-taking one a test can point at a temporary directory.
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
    /// Copies <paramref name="sourcePath"/> into <paramref name="directory"/> under a FIXED
    /// destination name (<c>video-wallpaper</c> plus the source's own extension), not the original
    /// file name -- v1 supports exactly one active video, so re-picking must cleanly overwrite the
    /// previous import rather than accumulating orphan files here. When <paramref name="sourcePath"/>
    /// already IS that fixed destination -- picking the file CosmicWin already imported, e.g. via a
    /// file picker's own "recent files" list, or the tray closure re-playing a previous import on a
    /// restore -- the copy is skipped entirely and the destination is returned unchanged: asking
    /// <see cref="File.Copy(string, string, bool)"/> to copy a file onto itself throws.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT catch and swallow, unlike <see cref="SettingsFile.Save(string,
    /// Settings)"/>. A failed settings write is invisible and not worth crashing a running window
    /// manager over; a failed video import is something the user picking a file right now needs to
    /// know about immediately -- the source could be on removable media that was just ejected, or
    /// locked by another process -- so <see cref="File.Copy(string, string, bool)"/> and <see
    /// cref="Directory.CreateDirectory(string)"/> are left to throw normally. The one exception is
    /// the same-file short-circuit above: that is not a copy failure, it is a copy that was never
    /// necessary in the first place.
    /// </remarks>
    public static string Import(string directory, string sourcePath)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "video-wallpaper" + Path.GetExtension(sourcePath).ToLowerInvariant());

        // Compared as full, normalised paths -- Windows paths are case-insensitive, and the raw
        // path handed in here need not already be in the same casing or use the same directory
        // separators as Path.Combine produced above.
        if (string.Equals(
                Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }

        File.Copy(sourcePath, destination, overwrite: true);
        return destination;
    }
}
