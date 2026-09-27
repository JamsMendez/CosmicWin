using System.IO;

namespace CosmicWin.App.Tests;

/// <summary>
/// The settings file on disk: where it lives, and that a save can be read back.
/// </summary>
/// <remarks>
/// Against a temporary path, never <see cref="SettingsFile.ResolvePath"/>. A fact that wrote the
/// real file would rewrite the maintainer's own settings on every run -- the same lesson the border
/// spike taught the hard way, where a test mutated machine state that outlived the testhost.
/// </remarks>
public sealed class SettingsFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"cosmicwin-settings-{Guid.NewGuid():N}");

    private string Path_ => Path.Combine(_directory, "settings.conf");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>First run has no file, and that must not be an error or a disabled feature.</summary>
    [Fact]
    public void AMissingFile_LoadsTheDefaults()
    {
        Assert.Equal(Settings.Default, SettingsFile.Load(Path_));
    }

    [Fact]
    public void SaveThenLoad_ReturnsWhatWasSaved()
    {
        SettingsFile.Save(Path_, new Settings(FocusBorder: false));

        Assert.False(SettingsFile.Load(Path_).FocusBorder);
    }

    /// <summary>
    /// The directory is created on the way. <c>%LOCALAPPDATA%\CosmicWin</c> exists in practice
    /// because the Scheduled Task XML lands there, but "in practice" is not a guarantee to save on.
    /// </summary>
    [Fact]
    public void Save_CreatesTheDirectoryWhenItIsMissing()
    {
        Assert.False(Directory.Exists(_directory));

        SettingsFile.Save(Path_, new Settings(FocusBorder: false));

        Assert.True(File.Exists(Path_));
    }

    [Fact]
    public void SavingTwice_LeavesOnlyTheSecondValue()
    {
        SettingsFile.Save(Path_, new Settings(FocusBorder: false));
        SettingsFile.Save(Path_, new Settings(FocusBorder: true));

        Assert.True(SettingsFile.Load(Path_).FocusBorder);
    }

    /// <summary>
    /// An unreadable file degrades to the defaults rather than blocking startup, exactly as
    /// <see cref="ExceptionListFile.Load(string)"/> treats a missing exception list.
    /// </summary>
    [Fact]
    public void ADirectoryWhereTheFileShouldBe_LoadsTheDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path_);

        Assert.Equal(Settings.Default, SettingsFile.Load(Path_));
    }

    [Fact]
    public void TheDefaultPath_SitsBesideTheOtherCosmicWinFiles()
    {
        var path = SettingsFile.ResolvePath();

        Assert.Equal("settings.conf", System.IO.Path.GetFileName(path));
        Assert.Equal(
            System.IO.Path.GetDirectoryName(ExceptionListFile.ResolvePath()),
            System.IO.Path.GetDirectoryName(path));
    }

    /// <summary>
    /// S9 (wallpaper-scene-http-endpoint, 2026-09-27, maintainer's decision: "en la instalacion cree
    /// un settings.conf con los valores por defecto" -- CosmicWin has no installer, so first start IS
    /// the install moment). A missing file is not just answered with the defaults in memory: it gets
    /// written, so the machine has a settings.conf to hand-edit from the very first run.
    /// </summary>
    [Fact]
    public void MissingFile_LoadOrCreate_WritesTheDefaultsToDisk()
    {
        Assert.False(File.Exists(Path_));

        var settings = SettingsFile.LoadOrCreate(Path_);

        Assert.Equal(Settings.Default, settings);
        Assert.True(File.Exists(Path_));
        Assert.Equal(Settings.Default, Settings.Parse(File.ReadAllText(Path_)));
    }

    /// <summary>
    /// The written file is a real settings.conf, not a bare value dump -- the same comments a save
    /// from the tray menu would produce, so a first-run file reads exactly like a hand-edited one.
    /// </summary>
    [Fact]
    public void MissingFile_LoadOrCreate_WritesTheFullCommentedTemplate()
    {
        SettingsFile.LoadOrCreate(Path_);

        var written = File.ReadAllText(Path_);

        Assert.Equal(Settings.Default.Serialize(), written, StringComparer.Ordinal);
    }

    /// <summary>
    /// S9's other maintainer decision folded into the same first-run write: html mode, the scene
    /// route, alerts and now alert-http itself all come up on for a fresh install.
    /// </summary>
    [Fact]
    public void MissingFile_LoadOrCreate_TheWrittenDefaultsMatchTheS9Decisions()
    {
        var settings = SettingsFile.LoadOrCreate(Path_);

        Assert.Equal(WallpaperMode.Html, settings.WallpaperMode);
        Assert.True(settings.WallpaperSceneHttpEnabled);
        Assert.True(settings.AlertHttpEnabled);
        Assert.True(settings.AlertsEnabled);
    }

    /// <summary>
    /// The other half of "created once": a file that already exists is data the maintainer may have
    /// hand-edited, and LoadOrCreate must never overwrite it -- not even to normalise formatting.
    /// </summary>
    [Fact]
    public void ExistingFile_LoadOrCreate_IsNeverRewritten()
    {
        var original = "focus-border = off\nalert-http = off\n";
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, original);
        var writeTimeBefore = File.GetLastWriteTimeUtc(Path_);

        var settings = SettingsFile.LoadOrCreate(Path_);

        Assert.Equal(original, File.ReadAllText(Path_), StringComparer.Ordinal);
        Assert.Equal(writeTimeBefore, File.GetLastWriteTimeUtc(Path_));
        Assert.False(settings.FocusBorder);
        Assert.False(settings.AlertHttpEnabled);
    }

    /// <summary>
    /// A path LoadOrCreate cannot write to (here: something already sits there as a directory, so
    /// writing the file fails the same way <c>Save</c> already tolerates elsewhere) must not crash
    /// startup -- it degrades to the in-memory defaults exactly like an unreadable existing file does.
    /// </summary>
    [Fact]
    public void AnUnwritablePath_LoadOrCreate_ReturnsTheDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path_);

        var settings = SettingsFile.LoadOrCreate(Path_);

        Assert.Equal(Settings.Default, settings);
    }
}
