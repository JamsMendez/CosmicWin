using System.IO;

namespace CosmicWin.App.Tests;

/// <summary>
/// Copying the picked video into <c>%LOCALAPPDATA%\CosmicWin\</c> under a fixed name.
/// </summary>
/// <remarks>
/// Against a temporary directory, never <see cref="VideoWallpaperImport.ResolveDirectory"/> -- the
/// same reason <see cref="SettingsFileTests"/> never touches the real settings path.
/// </remarks>
public sealed class VideoWallpaperImportTests : IDisposable
{
    private readonly string _sourceDirectory =
        Path.Combine(Path.GetTempPath(), $"cosmicwin-video-src-{Guid.NewGuid():N}");

    private readonly string _destinationDirectory =
        Path.Combine(Path.GetTempPath(), $"cosmicwin-video-dst-{Guid.NewGuid():N}");

    public VideoWallpaperImportTests()
    {
        Directory.CreateDirectory(_sourceDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceDirectory))
        {
            Directory.Delete(_sourceDirectory, recursive: true);
        }

        if (Directory.Exists(_destinationDirectory))
        {
            Directory.Delete(_destinationDirectory, recursive: true);
        }
    }

    private string WriteSourceFile(string fileName, string contents)
    {
        var path = Path.Combine(_sourceDirectory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Import_CopiesTheFile_ToAFixedDestinationName()
    {
        var source = WriteSourceFile("clip.mp4", "one");

        var destination = VideoWallpaperImport.Import(_destinationDirectory, source);

        Assert.Equal(Path.Combine(_destinationDirectory, "video-wallpaper.mp4"), destination);
        Assert.Equal("one", File.ReadAllText(destination));
    }

    /// <summary>
    /// v1 supports exactly one active video, so re-picking must cleanly replace the previous
    /// import rather than accumulating orphan files in the destination directory.
    /// </summary>
    [Fact]
    public void Import_ReImportingADifferentFile_OverwritesRatherThanDuplicates()
    {
        var first = WriteSourceFile("first.mp4", "first");
        var second = WriteSourceFile("second.mp4", "second");

        VideoWallpaperImport.Import(_destinationDirectory, first);
        var destination = VideoWallpaperImport.Import(_destinationDirectory, second);

        Assert.Equal("second", File.ReadAllText(destination));
        Assert.Single(Directory.GetFiles(_destinationDirectory));
    }

    [Fact]
    public void Import_CreatesTheDirectoryWhenItIsMissing()
    {
        var source = WriteSourceFile("clip.mp4", "contents");
        Assert.False(Directory.Exists(_destinationDirectory));

        VideoWallpaperImport.Import(_destinationDirectory, source);

        Assert.True(Directory.Exists(_destinationDirectory));
    }

    [Fact]
    public void Import_MissingSourceFile_Throws()
    {
        var missing = Path.Combine(_sourceDirectory, "does-not-exist.mp4");

        Assert.Throws<FileNotFoundException>(() => VideoWallpaperImport.Import(_destinationDirectory, missing));
    }

    [Fact]
    public void ResolveDirectory_SitsBesideTheOtherCosmicWinFiles()
    {
        var directory = VideoWallpaperImport.ResolveDirectory();

        Assert.Equal(
            System.IO.Path.GetDirectoryName(SettingsFile.ResolvePath()),
            directory);
    }
}
