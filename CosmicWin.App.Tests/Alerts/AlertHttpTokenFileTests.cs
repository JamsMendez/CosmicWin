using System.IO;
using System.Threading;
using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// <see cref="AlertHttpTokenFile"/> against a temporary path, never <see
/// cref="AlertHttpTokenFile.ResolvePath"/> -- the same lesson <see cref="SettingsFileTests"/> and
/// <see cref="ExceptionListFileTests"/> already learned: a fact that wrote the real file would
/// rewrite the maintainer's own machine state on every run.
/// </summary>
public sealed class AlertHttpTokenFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"cosmicwin-alert-http-token-{Guid.NewGuid():N}");

    private string TokenPath => Path.Combine(_directory, "alert-http.token");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>First run has no file: a fresh 32-byte token is created, base64url, no padding.</summary>
    [Fact]
    public void FirstRun_CreatesAValidBase64UrlToken()
    {
        var token = AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.NotNull(token);
        Assert.Equal(AlertHttpTokenFile.TokenLength, token!.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }

    [Fact]
    public void FirstRun_WritesTheTokenToDisk()
    {
        AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.True(File.Exists(TokenPath));
    }

    /// <summary>The token survives restarts: a second call reads back exactly what the first wrote.</summary>
    [Fact]
    public void SecondCall_ReturnsTheSameTokenUnchanged()
    {
        var first = AlertHttpTokenFile.LoadOrCreate(TokenPath);
        var second = AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnEmptyFile_IsReplacedWithAFreshToken()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(TokenPath, string.Empty);

        var token = AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.NotNull(token);
        Assert.Equal(AlertHttpTokenFile.TokenLength, token!.Length);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("has spaces in it even though it is long enough to maybe pass a length check!")]
    [InlineData("../../etc/passwd")]
    public void AMalformedFile_IsReplacedWithAFreshToken(string malformed)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(TokenPath, malformed);

        var token = AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.NotNull(token);
        Assert.NotEqual(malformed, token);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }

    /// <summary>
    /// R3-replace-persistence-untested: <see cref="AnEmptyFile_IsReplacedWithAFreshToken"/> only
    /// checks the FIRST call's return value, which the <c>Replace</c> branch could satisfy without
    /// actually persisting anything (e.g. by just returning the freshly generated token without
    /// writing it). A SECOND call reading the same path is the only thing that proves the write
    /// really landed on disk.
    /// </summary>
    [Fact]
    public void AfterAMalformedFileIsReplaced_ASecondCallReturnsTheSamePersistedToken()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(TokenPath, "not-a-valid-token");

        var first = AlertHttpTokenFile.LoadOrCreate(TokenPath);
        var second = AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    /// <summary>A trimmed valid token round-trips even if the file has trailing whitespace.</summary>
    [Fact]
    public void ATrailingNewline_DoesNotCountAsMalformed()
    {
        var token = AlertHttpTokenFile.LoadOrCreate(TokenPath);
        File.WriteAllText(TokenPath, token + "\r\n");

        var reread = AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.Equal(token, reread);
    }

    /// <summary>No shared randomness: two fresh files must not land on the same token.</summary>
    [Fact]
    public void TwoFreshFiles_GetDifferentTokens()
    {
        var otherDirectory = Path.Combine(Path.GetTempPath(), $"cosmicwin-alert-http-token-{Guid.NewGuid():N}");
        var otherPath = Path.Combine(otherDirectory, "alert-http.token");
        try
        {
            var first = AlertHttpTokenFile.LoadOrCreate(TokenPath);
            var second = AlertHttpTokenFile.LoadOrCreate(otherPath);

            Assert.NotEqual(first, second);
        }
        finally
        {
            if (Directory.Exists(otherDirectory))
            {
                Directory.Delete(otherDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Save_CreatesTheDirectoryWhenItIsMissing()
    {
        Assert.False(Directory.Exists(_directory));

        AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.True(File.Exists(TokenPath));
    }

    /// <summary>
    /// An unreadable path -- here, a FILE sitting where the directory should be -- fails closed:
    /// <see langword="null"/> and a diagnostic, never a throw, so the caller can leave the HTTP
    /// endpoint off while the pipe keeps working.
    /// </summary>
    [Fact]
    public void AnUnwritablePath_ReturnsNullAndReportsADiagnosticWithoutThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "this is a file, not a directory");
        try
        {
            string? diagnostic = null;

            var token = AlertHttpTokenFile.LoadOrCreate(TokenPath, message => diagnostic = message);

            Assert.Null(token);
            Assert.NotNull(diagnostic);
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    /// <summary>
    /// R3-vacuous-token-leak-test: <see cref="ADiagnostic_NeverContainsTheTokenValue"/> below only
    /// exercises a FAILURE path that never produces a token at all, which would pass just as well if
    /// the leak check were vacuous. This one exercises the one path that reports a diagnostic AND
    /// still hands back a token to the caller -- replacing a malformed file -- so the assertion has
    /// something real to fail against.
    /// </summary>
    [Fact]
    public void WhenAMalformedFileIsReplaced_TheDiagnosticNeverContainsTheNewTokenValue()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(TokenPath, "not-a-valid-token");
        var diagnostics = new List<string>();

        var token = AlertHttpTokenFile.LoadOrCreate(TokenPath, diagnostics.Add);

        Assert.NotNull(token);
        Assert.NotEmpty(diagnostics); // proves this scenario really does emit one, unlike the failure path below
        Assert.All(diagnostics, message => Assert.DoesNotContain(token!, message, StringComparison.Ordinal));
    }

    /// <summary>No diagnostic message ever carries the token itself.</summary>
    [Fact]
    public void ADiagnostic_NeverContainsTheTokenValue()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "this is a file, not a directory");
        try
        {
            var diagnostics = new List<string>();

            AlertHttpTokenFile.LoadOrCreate(TokenPath, diagnostics.Add);

            Assert.All(diagnostics, message => Assert.DoesNotContain("token=", message, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    [Fact]
    public void ADefaultDiagnosticCallback_IsNotRequired()
    {
        var token = AlertHttpTokenFile.LoadOrCreate(TokenPath);

        Assert.NotNull(token);
    }

    /// <summary>
    /// R3-never-throws-filter: a shape of path that fails for a reason other than
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> -- an empty path throws
    /// <see cref="ArgumentException"/> from <see cref="Path.GetDirectoryName(string)"/> once a fresh
    /// token needs writing -- must still fail closed: <see langword="null"/> and a diagnostic, never
    /// an escaping exception.
    /// </summary>
    [Fact]
    public void AnEmptyPath_ReturnsNullAndReportsADiagnosticWithoutThrowing()
    {
        string? diagnostic = null;

        var token = AlertHttpTokenFile.LoadOrCreate(string.Empty, message => diagnostic = message);

        Assert.Null(token);
        Assert.NotNull(diagnostic);
    }

    /// <summary>
    /// Same shape of proof as <see cref="AnEmptyPath_ReturnsNullAndReportsADiagnosticWithoutThrowing"/>
    /// for a colon inside a filename segment (not a drive letter position) -- a reserved character on
    /// Windows. MEASURED: this one already threw an <see cref="IOException"/> ("The parameter is
    /// incorrect") even under the class's ORIGINAL narrow <c>IOException or
    /// UnauthorizedAccessException</c> filter, so it was already handled before R3-never-throws-filter
    /// -- kept here as coverage of a path shape the class had no test for, not as a regression proof.
    /// </summary>
    [Fact]
    public void APathWithAColonInsideASegment_ReturnsNullAndReportsADiagnosticWithoutThrowing()
    {
        var invalidPath = Path.Combine(_directory, "bad:name.token");
        string? diagnostic = null;

        var token = AlertHttpTokenFile.LoadOrCreate(invalidPath, message => diagnostic = message);

        Assert.Null(token);
        Assert.NotNull(diagnostic);
    }

    /// <summary>
    /// Runs <paramref name="work"/> concurrently on <paramref name="callerCount"/> DEDICATED
    /// <see cref="Thread"/>s (R3-race-test-barrier-in-parallel-for: not the thread-pool via
    /// <c>Parallel.For</c>, which can silently run fewer than <paramref name="callerCount"/>
    /// workers at once on a starved pool, hiding the very race this is meant to force), synchronised
    /// on a <see cref="Barrier"/> so every caller genuinely starts at once. Every wait is bounded: a
    /// caller that never reaches the barrier, or never finishes, FAILS the test with a clear timeout
    /// exception instead of hanging the run.
    /// </summary>
    private static void RunConcurrently(int callerCount, Action<int> work)
    {
        var timeout = TimeSpan.FromSeconds(10);
        using var barrier = new Barrier(callerCount);
        var threads = new Thread[callerCount];
        Exception? capturedException = null;

        for (var i = 0; i < callerCount; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    if (!barrier.SignalAndWait(timeout))
                    {
                        throw new TimeoutException($"Caller {index} timed out waiting at the start barrier.");
                    }

                    work(index);
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref capturedException, exception, null);
                }
            })
            { IsBackground = true, Name = $"AlertHttpTokenFileTests.Race.{index}" };
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            if (!thread.Join(timeout))
            {
                throw new TimeoutException("A concurrent caller thread did not finish within the bounded timeout.");
            }
        }

        if (capturedException is not null)
        {
            throw capturedException;
        }
    }

    /// <summary>
    /// R3-token-create-race: several callers racing to create the SAME missing token file must all
    /// converge on one winner rather than some of them losing the check-then-act between the
    /// existence check and the destructive move/replace. Repeated a few times to give the race a
    /// real chance to happen rather than relying on scheduler luck.
    /// </summary>
    [Fact]
    public void ConcurrentFirstRun_AllCallersConvergeOnTheSameToken()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var racePath = Path.Combine(_directory, $"race-{iteration}.token");
            const int callerCount = 8;
            var results = new string?[callerCount];

            RunConcurrently(callerCount, i => results[i] = AlertHttpTokenFile.LoadOrCreate(racePath));

            Assert.All(results, Assert.NotNull);

            var onDisk = File.ReadAllText(racePath).Trim();
            Assert.All(results, result => Assert.Equal(onDisk, result));
        }
    }

    /// <summary>
    /// R3-replace-branch-race-still-open: the SAME race as above, but every caller sees the file
    /// already THERE and MALFORMED, so every one of them takes the <c>File.Replace</c> branch rather
    /// than <c>File.Move</c>. <c>File.Replace</c> does not throw just because the destination
    /// already exists (that is its entire job), so a plain check-then-act around it lets a later
    /// caller silently clobber an earlier caller's already-committed, already-returned token with no
    /// exception at all -- a lost update, not a crash. Every caller must still converge on ONE token
    /// that matches what is actually on disk once the dust settles.
    /// </summary>
    [Fact]
    public void ConcurrentReplaceOfOneMalformedFile_AllCallersConvergeOnTheSameToken()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var racePath = Path.Combine(_directory, $"malformed-race-{iteration}.token");
            Directory.CreateDirectory(_directory);
            File.WriteAllText(racePath, "not-a-valid-token");

            const int callerCount = 8;
            var results = new string?[callerCount];

            RunConcurrently(callerCount, i => results[i] = AlertHttpTokenFile.LoadOrCreate(racePath));

            Assert.All(results, Assert.NotNull);

            var onDisk = File.ReadAllText(racePath).Trim();
            Assert.All(results, result => Assert.Equal(onDisk, result));
        }
    }

    [Fact]
    public void TheDefaultPath_SitsBesideTheOtherCosmicWinFiles()
    {
        var path = AlertHttpTokenFile.ResolvePath();

        Assert.Equal("alert-http.token", Path.GetFileName(path));
        Assert.Equal(
            Path.GetDirectoryName(SettingsFile.ResolvePath()),
            Path.GetDirectoryName(path));
    }

    /// <summary>
    /// Finding R3-lock-timeout-path-unproved: another holder keeps the lock past the timeout, so the
    /// call gives up with one diagnostic and <see langword="null"/> instead of hanging, and writes
    /// nothing. A private lock name keeps this from colliding with the real one or other facts.
    /// </summary>
    [Fact]
    public void LockHeldPastTheTimeout_ReturnsNullWithOneDiagnosticAndWritesNothing()
    {
        var lockName = $@"Local\CosmicWin.AlertHttpToken.Test.{Guid.NewGuid():N}";
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, lockName);
            held.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            mutex.ReleaseMutex();
        }) { IsBackground = true };
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var diagnostics = new List<string>();
            var token = AlertHttpTokenFile.LoadOrCreate(
                TokenPath, diagnostics.Add, lockName, TimeSpan.FromMilliseconds(100));

            Assert.Null(token);
            Assert.Single(diagnostics);
            Assert.Contains("timed out", diagnostics[0]);
            Assert.False(File.Exists(TokenPath));
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Finding R3-mutex-ctor-outside-never-throws: a synchronization object of another type already
    /// owns the lock name, so creating the mutex itself throws. The loader must still keep its
    /// contract: a diagnostic and <see langword="null"/>, never a throw.
    /// </summary>
    [Fact]
    public void LockNameTakenByAnotherObjectType_ReturnsNullWithADiagnosticInsteadOfThrowing()
    {
        var lockName = $@"Local\CosmicWin.AlertHttpToken.Test.{Guid.NewGuid():N}";
        using var squatter = new EventWaitHandle(false, EventResetMode.ManualReset, lockName);

        var diagnostics = new List<string>();
        string? token = null;
        var exception = Record.Exception(() =>
            token = AlertHttpTokenFile.LoadOrCreate(TokenPath, diagnostics.Add, lockName, TimeSpan.FromSeconds(1)));

        Assert.Null(exception);
        Assert.Null(token);
        Assert.Single(diagnostics);
        Assert.False(File.Exists(TokenPath));
    }
}
