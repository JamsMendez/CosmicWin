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

    /// <summary>Reads, or first writes and then reads, the settings at <see cref="ResolvePath"/>. See the <see cref="LoadOrCreate(string)"/> overload for what that means.</summary>
    public static Settings LoadOrCreate() => LoadOrCreate(ResolvePath());

    /// <summary>
    /// S9 (wallpaper-scene-http-endpoint, 2026-09-27, maintainer's decision: "en la instalacion cree
    /// un settings.conf con los valores por defecto" -- CosmicWin has no installer, so first start IS
    /// the install moment): the one caller <see cref="Load(string)"/> keeps side-effect-free for.
    /// When <paramref name="path"/> does not exist yet, this WRITES <see cref="Settings.Default"/>
    /// there (the same commented template a tray-menu save produces) before returning it, so a fresh
    /// machine ends up with a real settings.conf to hand-edit instead of only in-memory defaults. When
    /// it already exists, this is exactly <see cref="Load(string)"/> -- the file is read, never
    /// rewritten, not even to normalise its formatting.
    /// </summary>
    /// <remarks>
    /// The write reuses <see cref="Save(string, Settings)"/> rather than duplicating its try/catch: a
    /// first run that cannot create the directory or write the file (no permission, a file sitting
    /// where the directory should be, disk full) must not crash startup any more than an ordinary
    /// save from the tray does -- it silently keeps running on the in-memory defaults, and the next
    /// successful <see cref="Save(string, Settings)"/> (the first toggle the user makes) is what
    /// finally puts the file there.
    /// </remarks>
    public static Settings LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            return Load(path);
        }

        Save(path, Settings.Default);
        return Settings.Default;
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
