using System.IO;
using CosmicWin.App.Diagnostics;

namespace CosmicWin.App.Tests.Diagnostics;

/// <summary>
/// A crash on 2026-10-28 left nothing behind but a WER bucket for <c>0xe0434352</c>: no
/// <c>.NET Runtime</c> event, no kept minidump, no stack. <see cref="CrashLog"/> exists so the next
/// unhandled exception names the frame it came from.
/// </summary>
public sealed class CrashLogTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 6, 4, 17, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "CosmicWinCrashLog", Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_directory, "crash.log");

    [Fact]
    public void Record_WritesTheTimestampTheTerminationFlagAndTheWholeException()
    {
        var exception = Thrown(new InvalidOperationException("outer", new ArgumentException("inner cause")));

        CrashLog.Record(Path_, exception, isTerminating: true, At);

        var text = File.ReadAllText(Path_);
        Assert.Contains("2026-10-28T06:04:17.0000000+00:00", text);
        Assert.Contains("terminating=True", text);
        Assert.Contains("System.InvalidOperationException: outer", text);
        Assert.Contains("System.ArgumentException: inner cause", text);
        Assert.Contains(nameof(Thrown), text);
    }

    [Fact]
    public void Record_AppendsRatherThanOverwriting()
    {
        CrashLog.Record(Path_, new InvalidOperationException("first"), isTerminating: false, At);
        CrashLog.Record(Path_, new InvalidOperationException("second"), isTerminating: true, At);

        var text = File.ReadAllText(Path_);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void Record_WhenTheObjectIsNotAnException_StillWritesWhatItIs()
    {
        CrashLog.Record(Path_, "a thrown non-exception", isTerminating: true, At);

        Assert.Contains("a thrown non-exception", File.ReadAllText(Path_));
    }

    [Fact]
    public void Record_WhenTheFileCannotBeWritten_SwallowsTheFailure()
    {
        Directory.CreateDirectory(_directory);

        var failure = Record.Exception(
            () => CrashLog.Record(_directory, new InvalidOperationException("x"), isTerminating: true, At));

        Assert.Null(failure);
    }

    [Fact]
    public void ResolveDefaultPath_SitsBesideTheOtherLocalAppDataArtifacts()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CosmicWin",
                "crash.log"),
            CrashLog.ResolveDefaultPath());
    }

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
