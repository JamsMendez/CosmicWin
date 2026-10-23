using System.IO;

namespace CosmicWin.App;

/// <summary>
/// The crash marker for <c>unfocused-opacity</c>: an empty file that exists while CosmicWin may
/// have windows layered. Found at startup, it means the previous run did not exit cleanly and may
/// have left windows translucent, so the windows adopted then are repaired.
/// </summary>
/// <remarks>
/// Best-effort both ways. A marker that cannot be written costs the automatic repair after a crash
/// (setting the key to 100 and restarting still leaves nothing worse than before); one that cannot
/// be deleted costs a harmless repair pass at the next start. Neither is worth failing over.
/// </remarks>
public static class OpacityMarkerFile
{
    /// <summary>Beside <c>settings.conf</c>, in CosmicWin's own folder.</summary>
    public static string ResolvePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin",
            "opacity-in-use");

    public static bool Exists(string path) => File.Exists(path);

    public static void Set(string path, bool inUse)
    {
        try
        {
            if (inUse)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, []);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
