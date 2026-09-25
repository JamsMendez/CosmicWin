using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    /// <remarks>
    /// V1b surprise: <c>CreateHardLinkW</c> only adds a directory entry -- it never opens the
    /// source's data stream -- so the exclusive lock below no longer blocks the link the way it
    /// used to block <see cref="File.Copy(string, string, bool)"/> (confirmed by re-running this
    /// exact test against the real, uninjected two-argument overload: the RED run this test was
    /// written to reproduce came back GREEN instead, because the link quietly succeeded). Forcing
    /// the link to fail through the injectable overload is what actually keeps this test testing
    /// the scenario it is named for: a failure while copying BYTES must not touch the existing
    /// destination or leave a temp file behind.
    /// </remarks>
    [Fact]
    public void Import_WhenTheSourceCannotBeRead_LeavesAnExistingDestinationUntouchedAndNoTempFileBehind()
    {
        Directory.CreateDirectory(_destinationDirectory);
        var destination = Path.Combine(_destinationDirectory, "video-wallpaper.mp4");
        File.WriteAllText(destination, "previously imported");
        var source = WriteSourceFile("clip.mp4", "new content that must never land");

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() =>
                VideoWallpaperImport.Import(_destinationDirectory, source, (_, _) => false));
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

    /// <summary>
    /// V1b (video-wallpaper-http-endpoint, decision 5): both temp directories above live under
    /// <see cref="Path.GetTempPath"/>, i.e. the same volume, so the real, uninjected two-argument
    /// <see cref="VideoWallpaperImport.Import(string, string)"/> should link rather than copy --
    /// verified here by file IDENTITY (same volume serial and file index), not merely equal bytes,
    /// which a copy would also produce.
    /// </summary>
    [Fact]
    public void Import_WhenSourceAndDestinationShareAVolume_LinksInsteadOfCopyingBytes()
    {
        var source = WriteSourceFile("clip.mp4", "identity-checked-payload");

        var destination = VideoWallpaperImport.Import(_destinationDirectory, source);

        Assert.Equal(GetFileIdentity(source), GetFileIdentity(destination));
        Assert.True(GetLinkCount(source) >= 2);
    }

    /// <summary>
    /// The injectable third overload (the same seam <see cref="SettingsFile"/>'s convention
    /// extends one level deeper) stands in for a link failure -- a different volume,
    /// <c>ERROR_NOT_SAME_DEVICE</c>, a non-NTFS destination, access denied -- without needing a
    /// second real volume attached to the test machine. The destination must still end up with
    /// the source's bytes, by an ordinary copy this time, and it must NOT share the source's file
    /// identity.
    /// </summary>
    [Fact]
    public void Import_WhenTheLinkFails_FallsBackToCopyingBytes()
    {
        var source = WriteSourceFile("clip.mp4", "copied-payload");

        var destination = VideoWallpaperImport.Import(_destinationDirectory, source, (_, _) => false);

        Assert.Equal("copied-payload", File.ReadAllText(destination));
        Assert.NotEqual(GetFileIdentity(source), GetFileIdentity(destination));
    }

    /// <summary>
    /// The CRITICAL invariant from <see cref="VideoWallpaperImport"/>'s remarks: once
    /// <c>video-wallpaper.mp4</c> is a hard link to <paramref name="first"/>'s data, re-picking a
    /// DIFFERENT source must not touch <paramref name="first"/>'s own bytes, reachable through its
    /// own, still-separate directory entry -- overwriting the fixed destination only ever retires
    /// the destination's own directory entry, never the source's.
    /// </summary>
    [Fact]
    public void Import_AfterALinkedImport_ReplacingItWithADifferentSourceLeavesTheFirstSourceUntouched()
    {
        var first = WriteSourceFile("first.mp4", "first-must-stay-untouched");
        var second = WriteSourceFile("second.mp4", "second-payload");

        VideoWallpaperImport.Import(_destinationDirectory, first);
        var destination = VideoWallpaperImport.Import(_destinationDirectory, second);

        Assert.Equal("second-payload", File.ReadAllText(destination));
        Assert.Equal("first-must-stay-untouched", File.ReadAllText(first));
    }

    private static (uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow) GetFileIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException($"GetFileInformationByHandle failed for '{path}'.");
        }

        return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    private static uint GetLinkCount(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException($"GetFileInformationByHandle failed for '{path}'.");
        }

        return info.NumberOfLinks;
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
}
