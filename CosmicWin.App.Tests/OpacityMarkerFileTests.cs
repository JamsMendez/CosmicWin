using System.IO;

namespace CosmicWin.App.Tests;

/// <summary>
/// The crash marker: present exactly while CosmicWin may have windows layered, so the next start
/// knows whether there is anything to repair.
/// </summary>
/// <remarks>
/// Against a temporary path, never <see cref="OpacityMarkerFile.ResolvePath"/>, for the same reason
/// as <see cref="SettingsFileTests"/>: a fact must not change the maintainer's real machine state.
/// </remarks>
public sealed class OpacityMarkerFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"cosmicwin-marker-{Guid.NewGuid():N}");

    private string Path_ => Path.Combine(_directory, "opacity-in-use");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void NoMarker_MeansThePreviousRunLeftNothingLayered()
    {
        Assert.False(OpacityMarkerFile.Exists(Path_));
    }

    /// <summary>The directory may not exist yet on a first run; setting the marker creates it.</summary>
    [Fact]
    public void SettingTheMarker_CreatesIt_AndClearingItRemovesIt()
    {
        OpacityMarkerFile.Set(Path_, inUse: true);

        Assert.True(OpacityMarkerFile.Exists(Path_));

        OpacityMarkerFile.Set(Path_, inUse: false);

        Assert.False(OpacityMarkerFile.Exists(Path_));
    }

    /// <summary>Clearing a marker that is not there is the ordinary clean-exit case, not an error.</summary>
    [Fact]
    public void ClearingAMissingMarker_DoesNotThrow()
    {
        var exception = Record.Exception(() => OpacityMarkerFile.Set(Path_, inUse: false));

        Assert.Null(exception);
        Assert.False(OpacityMarkerFile.Exists(Path_));
    }
}
