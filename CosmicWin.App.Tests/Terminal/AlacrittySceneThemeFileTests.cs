using System.IO;
using CosmicWin.App.Terminal;

namespace CosmicWin.App.Tests.Terminal;

public sealed class AlacrittySceneThemeFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cosmicwin-alacritty-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TryWrite_CreatesTheDirectoryAndWritesTheSceneWithoutABom()
    {
        var path = Path.Combine(_directory, "nested", "scene.toml");

        var written = AlacrittySceneThemeFile.TryWrite(path, WallpaperScene.Raphael, onDiagnostic: null);

        Assert.True(written);
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(AlacrittySceneTheme.Render(WallpaperScene.Raphael), System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void TryWrite_OverwritesWhenTheSceneChanges()
    {
        var path = Path.Combine(_directory, "scene.toml");
        AlacrittySceneThemeFile.TryWrite(path, WallpaperScene.Idle, null);

        var written = AlacrittySceneThemeFile.TryWrite(path, WallpaperScene.Explorer, null);

        Assert.True(written);
        Assert.Equal(AlacrittySceneTheme.Render(WallpaperScene.Explorer), File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    /// <summary>Alacritty reloads on every change, so identical content must not be rewritten.</summary>
    [Fact]
    public void TryWrite_LeavesAnIdenticalFileUntouched()
    {
        var path = Path.Combine(_directory, "scene.toml");
        AlacrittySceneThemeFile.TryWrite(path, WallpaperScene.Idle, null);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var written = AlacrittySceneThemeFile.TryWrite(path, WallpaperScene.Idle, null);

        Assert.True(written);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0name.toml")]
    public void TryWrite_InvalidPath_ReturnsFalseAndReportsWithoutThrowing(string path)
    {
        var diagnostics = new List<string>();

        var written = AlacrittySceneThemeFile.TryWrite(path, WallpaperScene.Idle, diagnostics.Add);

        Assert.False(written);
        Assert.Single(diagnostics);
    }

    [Fact]
    public void TryWrite_PathBlockedByAFile_ReturnsFalseAndReports()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file");
        File.WriteAllText(blocker, "x");
        var diagnostics = new List<string>();

        var written = AlacrittySceneThemeFile.TryWrite(
            Path.Combine(blocker, "scene.toml"), WallpaperScene.Idle, diagnostics.Add);

        Assert.False(written);
        Assert.Single(diagnostics);
    }
}
