using System.IO;
using CosmicWin.App.Diagnostics;

namespace CosmicWin.App.Tests.Diagnostics;

/// <summary>
/// Covers F1 of <c>odd/tasks/video-endpoint-followups.md</c>: a reader that opens the desktop trace
/// with <see cref="FileShare.Read"/> (many editors, <c>Get-Content</c> without <c>-Wait</c>,
/// <see cref="File.ReadAllLines(string)"/>) denies writers for as long as it holds the handle. A
/// harness that polled the trace every 50 ms lost a real line this way; <see cref="FileDesktopTrace.Record"/>
/// must ride out a short-lived hold instead of dropping the line on the first sharing violation.
/// </summary>
public sealed class FileDesktopTraceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "CosmicWinDesktopTrace", Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_directory, "desktop-trace.log");

    /// <summary>
    /// The retry is bounded by TIME, not by a count of sleeps: File.ReadAllLines on the real 9 MB
    /// trace held it 22-47 ms, and a count of 5 ms sleeps shrinks to ~20 ms whenever something
    /// (Media Foundation playing the wallpaper) raises the timer resolution to 1 ms. A hold of
    /// 200 ms inside a 2 s window must still land its line, whatever the timer resolution.
    /// </summary>
    [Fact]
    public async Task Record_WhenAReaderHoldsTheFileLongerThanAFewSleeps_StillWritesTheLineWithinTheWindow()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, string.Empty);
        var trace = new FileDesktopTrace(Path_, retryWindow: TimeSpan.FromSeconds(2));

        using var reader = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200);
            reader.Dispose();
        });

        trace.Record("desktop switch=Left");
        await release;

        Assert.Contains("desktop switch=Left", Assert.Single(File.ReadAllLines(Path_)));
    }

    /// <summary>
    /// RED before the retry existed: a reader that denies writers for ~10 ms and then lets go must
    /// still see its line land, because the writer retries instead of giving up on the first attempt.
    /// </summary>
    [Fact]
    public async Task Record_WhenTheFileIsBrieflyHeldOpenByAReader_StillWritesTheLine()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, string.Empty);
        var trace = new FileDesktopTrace(Path_, () => new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));

        using var reader = new FileStream(
            Path_, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(10);
            reader.Dispose();
        });

        trace.Record("desktop switch=Right");
        await release;

        var line = Assert.Single(File.ReadAllLines(Path_));
        Assert.Contains("desktop switch=Right", line);
    }

    /// <summary>A hold that never releases must still let <see cref="FileDesktopTrace.Record"/> return, unthrown, within a bound well short of the caller noticing.</summary>
    [Fact]
    public void Record_WhenTheFileIsHeldOpenForTheWholeCall_GivesUpWithoutThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, string.Empty);
        var trace = new FileDesktopTrace(Path_);

        using var reader = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.Read);

        var started = DateTimeOffset.UtcNow;
        var exception = Record.Exception(() => trace.Record("desktop switch=Left"));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Null(exception);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"took {elapsed}");
        Assert.Empty(File.ReadAllLines(Path_));
    }

    /// <summary>
    /// Review R3-001: a reader that holds the file for a long time must cost ONE retry window, not
    /// one per line -- Record runs on chord and layout paths that write several lines at once.
    /// Once a line gives up, later lines fail at once until a write succeeds again.
    /// </summary>
    [Fact]
    public void Record_AfterALineGaveUpOnAPermanentHold_LaterLinesDoNotWaitAgain()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, string.Empty);
        var trace = new FileDesktopTrace(Path_, retryWindow: TimeSpan.FromMilliseconds(300));

        using var reader = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.Read);
        trace.Record("first");

        var second = System.Diagnostics.Stopwatch.StartNew();
        trace.Record("second");
        second.Stop();

        Assert.True(second.Elapsed < TimeSpan.FromMilliseconds(100), $"second line waited {second.Elapsed}");
    }

    /// <summary>
    /// The breaker closes again on the first write that succeeds, so a LATER short hold is ridden
    /// out with the full window again instead of dropping lines forever.
    /// </summary>
    [Fact]
    public async Task Record_OnceAWriteSucceedsAgain_ALaterShortHoldIsRiddenOutAgain()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path_, string.Empty);
        var trace = new FileDesktopTrace(Path_, retryWindow: TimeSpan.FromMilliseconds(300));

        using (new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            trace.Record("dropped");
        }

        trace.Record("lands");

        var reader = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(100);
            reader.Dispose();
        });
        trace.Record("rides out");
        await release;

        var lines = File.ReadAllLines(Path_);
        Assert.Equal(2, lines.Length);
        Assert.Contains("lands", lines[0]);
        Assert.Contains("rides out", lines[1]);
    }

    [Fact]
    public void Record_AppendsOneLinePerChord_RatherThanOverwriting()
    {
        var trace = new FileDesktopTrace(Path_);

        trace.Record("desktop switch=Right");
        trace.Record("desktop switch=Left");

        var lines = File.ReadAllLines(Path_);
        Assert.Equal(2, lines.Length);
        Assert.Contains("desktop switch=Right", lines[0]);
        Assert.Contains("desktop switch=Left", lines[1]);
    }

    [Fact]
    public void Record_CreatesTheContainingDirectoryOnFirstWrite()
    {
        var trace = new FileDesktopTrace(Path_);

        trace.Record("desktop switch=Right");

        Assert.True(File.Exists(Path_));
    }

    [Fact]
    public void Record_WhenTheFileCannotBeWritten_SwallowsTheFailure()
    {
        Directory.CreateDirectory(_directory);
        var trace = new FileDesktopTrace(_directory);

        var exception = Record.Exception(() => trace.Record("desktop switch=Right"));

        Assert.Null(exception);
    }

    [Fact]
    public void ResolveDefaultPath_SitsBesideTheOtherLocalAppDataArtifacts()
    {
        var path = FileDesktopTrace.ResolveDefaultPath();

        Assert.Equal(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CosmicWin",
                "desktop-trace.log"),
            path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
