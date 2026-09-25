namespace CosmicWin.Interop;

/// <summary>
/// The three filesystem probes <see cref="VideoWallpaperHttpProtocol.TryValidate"/> needs,
/// injectable so its tests are deterministic without touching disk. <see cref="Default"/> wires the
/// real filesystem: <see cref="File.Exists(string)"/>, <see cref="Directory.Exists(string)"/>, and
/// <see cref="DriveInfo.DriveType"/>. Any exception from constructing a <see cref="DriveInfo"/> (an
/// unmapped drive letter, a momentarily unreachable one) is treated as a network drive, so the
/// caller answers 400 instead of crashing: this class never throws.
/// </summary>
public sealed class VideoWallpaperFileProbes
{
    /// <summary>The real filesystem.</summary>
    public static readonly VideoWallpaperFileProbes Default = new(
        fileExists: File.Exists,
        directoryExists: Directory.Exists,
        isNetworkDrive: IsNetworkDriveByDriveInfo);

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _isNetworkDrive;

    public VideoWallpaperFileProbes(
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, bool> isNetworkDrive)
    {
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        _isNetworkDrive = isNetworkDrive ?? throw new ArgumentNullException(nameof(isNetworkDrive));
    }

    /// <summary>Whether a regular file exists at the given absolute path.</summary>
    public bool FileExists(string path) => _fileExists(path);

    /// <summary>Whether a directory exists at the given absolute path.</summary>
    public bool DirectoryExists(string path) => _directoryExists(path);

    /// <summary>Whether <paramref name="driveRoot"/> (for example <c>"C:\"</c>) is a mapped network drive.</summary>
    public bool IsNetworkDrive(string driveRoot) => _isNetworkDrive(driveRoot);

    private static bool IsNetworkDriveByDriveInfo(string driveRoot)
    {
        try
        {
            return new DriveInfo(driveRoot).DriveType == DriveType.Network;
        }
        catch
        {
            return true;
        }
    }
}
