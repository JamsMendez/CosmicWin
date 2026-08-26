using System.IO;
using CosmicWin.App.Diagnostics;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests.Diagnostics;

/// <summary>
/// The on-disk half of the MR-2 diagnostic: the user reads this file
/// after one supervised run, so every field the two candidates differ on must survive to disk, the
/// sink must APPEND (a run is many keypresses), and it must never throw -- a diagnostic that can
/// crash the app it is diagnosing is worse than no diagnostic.
/// </summary>
public sealed class FileFocusTraceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "CosmicWinFocusTrace", Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_directory, "focus-trace.log");

    private static FocusTraceEntry Entry(
        Direction direction = Direction.Right,
        nint foreground = 1,
        nint focused = 1,
        nint target = 2,
        FocusTraceOutcome outcome = FocusTraceOutcome.Activated,
        ActivationOutcome? activation = ActivationOutcome.Direct) =>
        new(direction, foreground, focused, target, outcome, activation);

    [Fact]
    public void Record_WritesEveryFieldTheTwoMr2CandidatesDifferOn()
    {
        var trace = new FileFocusTrace(Path_, () => new DateTimeOffset(2026, 8, 22, 10, 11, 12, TimeSpan.Zero));

        trace.Record(Entry(Direction.Left, 0x5E6F, 0x1A2B, 0x3C4D, FocusTraceOutcome.ActivateFailed));

        var line = Assert.Single(File.ReadAllLines(Path_));
        Assert.Contains("2026-08-22T10:11:12", line);
        Assert.Contains("Left", line);
        Assert.Contains("foreground=0x5E6F", line);
        Assert.Contains("focused=0x1A2B", line);
        Assert.Contains("target=0x3C4D", line);
        Assert.Contains("ActivateFailed", line);
    }

    /// <summary>
    /// The rung reaches DISK, because the file is the only thing that survives a supervised run.
    /// </summary>
    /// <remarks>
    /// A field recorded in memory and dropped at the sink is a field nobody will ever read. This is
    /// the same lesson the outcome field taught: the trace is worth exactly what comes back out of
    /// <c>%LOCALAPPDATA%\CosmicWin\focus-trace.log</c> afterwards.
    /// </remarks>
    [Fact]
    public void Record_WritesTheActivationRung_SoASupervisedRunCanCompareItAgainstTheDesktopChord()
    {
        var trace = new FileFocusTrace(Path_);

        trace.Record(Entry(activation: ActivationOutcome.AttachedInput));

        Assert.Contains("activation=AttachedInput", Assert.Single(File.ReadAllLines(Path_)));
    }

    /// <summary>A chord that activated nothing writes no rung, rather than an invented one.</summary>
    [Fact]
    public void Record_WhenNothingWasActivated_WritesNoRung()
    {
        var trace = new FileFocusTrace(Path_);

        trace.Record(Entry(outcome: FocusTraceOutcome.NoMatch, target: 0, activation: null));

        Assert.Contains("activation=none", Assert.Single(File.ReadAllLines(Path_)));
    }

    [Fact]
    public void Record_AppendsOneLinePerKeypress_RatherThanOverwriting()
    {
        var trace = new FileFocusTrace(Path_);

        trace.Record(Entry(outcome: FocusTraceOutcome.NoMatch));
        trace.Record(Entry(outcome: FocusTraceOutcome.Activated));

        var lines = File.ReadAllLines(Path_);
        Assert.Equal(2, lines.Length);
        Assert.Contains("NoMatch", lines[0]);
        Assert.Contains("Activated", lines[1]);
    }

    [Fact]
    public void Record_CreatesTheContainingDirectoryOnFirstWrite()
    {
        var trace = new FileFocusTrace(Path_);

        trace.Record(Entry());

        Assert.True(File.Exists(Path_));
    }

    [Fact]
    public void Record_WhenTheFileCannotBeWritten_SwallowsTheFailure()
    {
        Directory.CreateDirectory(_directory);
        var trace = new FileFocusTrace(_directory);

        var exception = Record.Exception(() => trace.Record(Entry()));

        Assert.Null(exception);
    }

    [Fact]
    public void ResolveDefaultPath_SitsBesideTheOtherLocalAppDataArtifacts()
    {
        var path = FileFocusTrace.ResolveDefaultPath();

        Assert.Equal(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CosmicWin",
                "focus-trace.log"),
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
