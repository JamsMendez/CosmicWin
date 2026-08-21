namespace CosmicWin.App.Tests;

/// <summary>Task 3.33/3.34: <see cref="ExceptionListFile"/> owns the on-disk manual exception-list read (spec WE-2). Plain BCL file I/O, no fake needed.</summary>
public sealed class ExceptionListFileTests
{
    [Fact]
    public void Load_MissingFile_ReturnsEmptyExceptionList()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"cosmicwin-exceptions-missing-{Guid.NewGuid():N}.conf");

        var exceptions = ExceptionListFile.Load(missingPath);

        Assert.False(exceptions.Matches(new CosmicWin.Layout.WindowDescriptor("AnyClass", "anything.exe", "Any Title", 0u, 0u, false)));
    }

    [Fact]
    public void Load_ExistingFile_ParsesContentViaExceptionListLoader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cosmicwin-exceptions-{Guid.NewGuid():N}.conf");
        File.WriteAllText(path, "process:Spotify.exe\n");
        try
        {
            var exceptions = ExceptionListFile.Load(path);

            var matchingDescriptor = new CosmicWin.Layout.WindowDescriptor("AnyClass", "Spotify.exe", "Any Title", 0u, 0u, false);
            var nonMatchingDescriptor = new CosmicWin.Layout.WindowDescriptor("AnyClass", "Notepad.exe", "Any Title", 0u, 0u, false);
            Assert.True(exceptions.Matches(matchingDescriptor));
            Assert.False(exceptions.Matches(nonMatchingDescriptor));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
