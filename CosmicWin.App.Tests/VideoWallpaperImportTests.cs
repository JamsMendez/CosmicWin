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

    /// <summary>
    /// Picking the file CosmicWin already imported -- the fixed destination itself, reachable
    /// again through a file picker's own "recent files" list, or because the tray re-pick closure
    /// hands back the previously imported path on a restore -- must not ask <see
    /// cref="File.Copy(string, string, bool)"/> to copy a file onto itself, which throws.
    /// </summary>
    [Fact]
    public void Import_SourceEqualsDestination_ReturnsDestinationWithoutCopying()
    {
        Directory.CreateDirectory(_destinationDirectory);
        var destination = Path.Combine(_destinationDirectory, "video-wallpaper.mp4");
        File.WriteAllText(destination, "already imported");

        var result = VideoWallpaperImport.Import(_destinationDirectory, destination);

        Assert.Equal(destination, result);
        Assert.Equal("already imported", File.ReadAllText(destination));
    }

    [Fact]
    public void Import_MissingSourceFile_Throws()
    {
        var missing = Path.Combine(_sourceDirectory, "does-not-exist.mp4");

        Assert.Throws<FileNotFoundException>(() => VideoWallpaperImport.Import(_destinationDirectory, missing));
    }

    /// <summary>
    /// F2 (video-wallpaper-review-followups, <c>R3-restore-replays-partial-copy</c>): a missing
    /// source must not touch an already-imported destination at all, and must leave no temporary
    /// file behind under the fixed destination's own name.
    /// </summary>
    [Fact]
    public void Import_MissingSourceFile_LeavesAnExistingDestinationUntouchedAndNoTempFileBehind()
    {
        Directory.CreateDirectory(_destinationDirectory);
        var destination = Path.Combine(_destinationDirectory, "video-wallpaper.mp4");
        File.WriteAllText(destination, "previously imported");
        var missing = Path.Combine(_sourceDirectory, "does-not-exist.mp4");

        Assert.Throws<FileNotFoundException>(() => VideoWallpaperImport.Import(_destinationDirectory, missing));

        Assert.Equal("previously imported", File.ReadAllText(destination));
        Assert.Equal([destination], Directory.GetFiles(_destinationDirectory));
    }

    /// <summary>
    /// F2 (video-wallpaper-review-followups, <c>R3-restore-replays-partial-copy</c>): the previous
    /// residual was copying straight onto the fixed destination, so a copy that failed midway (the
    /// source ejected, or -- as simulated here -- locked by another process) left a partially
    /// overwritten destination, which the restore path would then happily replay. Copying to a
    /// temporary file first and moving it into place means a failed copy cannot touch the
    /// destination at all, and the temporary file must not survive the failure either.
    /// </summary>
    [Fact]
    public void Import_WhenTheSourceCannotBeRead_LeavesAnExistingDestinationUntouchedAndNoTempFileBehind()
    {
        Directory.CreateDirectory(_destinationDirectory);
        var destination = Path.Combine(_destinationDirectory, "video-wallpaper.mp4");
        File.WriteAllText(destination, "previously imported");
        var source = WriteSourceFile("clip.mp4", "new content that must never land");

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => VideoWallpaperImport.Import(_destinationDirectory, source));
        }

        Assert.Equal("previously imported", File.ReadAllText(destination));
        Assert.Equal([destination], Directory.GetFiles(_destinationDirectory));
    }

    /// <summary>
    /// F2's own temp-file mechanism has a failure mode the two tests above cannot exercise -- the
    /// copy to the temp file itself succeeds, and it is the FINAL move into place that fails (here,
    /// because the destination is held open elsewhere and cannot be replaced). The temp file must
    /// not be left behind either way.
    /// </summary>
    [Fact]
    public void Import_WhenMovingIntoPlaceFails_LeavesTheExistingDestinationUntouchedAndNoTempFileBehind()
    {
        Directory.CreateDirectory(_destinationDirectory);
        var destination = Path.Combine(_destinationDirectory, "video-wallpaper.mp4");
        File.WriteAllText(destination, "previously imported");
        var source = WriteSourceFile("clip.mp4", "new content that must never land");

        using (new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            // File.Move onto a file held open elsewhere surfaces as UnauthorizedAccessException on
            // this runtime, not IOException -- the observable contract this test cares about is
            // that SOME exception propagates and neither the destination nor a temp file is left
            // in a bad state, not which exact type Windows reports for a locked replace.
            Assert.ThrowsAny<Exception>(() => VideoWallpaperImport.Import(_destinationDirectory, source));
        }

        Assert.Equal("previously imported", File.ReadAllText(destination));
        Assert.Equal([destination], Directory.GetFiles(_destinationDirectory));
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
