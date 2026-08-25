using System.IO;

namespace CosmicWin.App;

/// <summary>
/// Owns the on-disk settings read and write, mirroring <see cref="ExceptionListFile"/>: the pure
/// format lives in <see cref="Settings"/>, and this App-layer type owns the file.
/// </summary>
/// <remarks>
/// Every failure here degrades to the defaults rather than throwing. A settings file is a
/// convenience; a window manager that refuses to start because one could not be read has turned a
/// convenience into a liability.
/// </remarks>
public static class SettingsFile
{
    /// <summary>
    /// Beside <c>exceptions.conf</c> and the Scheduled Task XML, in <c>%LOCALAPPDATA%\CosmicWin</c>.
    /// One directory for everything CosmicWin writes, so a user cleaning up finds all of it at once.
    /// </summary>
    public static string ResolvePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin",
            "settings.conf");

    /// <summary>Reads the settings at <see cref="ResolvePath"/>.</summary>
    public static Settings Load() => Load(ResolvePath());

    /// <summary>
    /// Reads the settings at <paramref name="path"/>. A missing or unreadable file yields
    /// <see cref="Settings.Default"/> -- first run happens before the file exists, and startup must
    /// not depend on it.
    /// </summary>
    public static Settings Load(string path)
    {
        try
        {
            return File.Exists(path) ? Settings.Parse(File.ReadAllText(path)) : Settings.Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Settings.Default;
        }
    }

    /// <summary>Writes <paramref name="settings"/> to <see cref="ResolvePath"/>.</summary>
    public static void Save(Settings settings) => Save(ResolvePath(), settings);

    /// <summary>
    /// Writes <paramref name="settings"/> to <paramref name="path"/>, creating the directory if it
    /// is not there yet.
    /// </summary>
    /// <remarks>
    /// Returns quietly on an IO failure rather than throwing. This runs from a tray menu click: the
    /// toggle the user just made has ALREADY taken effect on screen, and taking the process down
    /// because the preference could not be recorded would be a wildly disproportionate answer.
    /// </remarks>
    public static void Save(string path, Settings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, settings.Serialize());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
