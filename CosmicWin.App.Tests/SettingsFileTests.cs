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
}
