using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    /// R3-token-create-race: several callers racing to create the SAME missing token file must all
    /// converge on one winner rather than some of them failing the check-then-act between the
    /// existence check and the destructive move/replace. Repeated a few times, and with a
    /// <see cref="Barrier"/> so every caller genuinely starts at once, to give the race a real chance
    /// to happen rather than relying on scheduler luck.
    /// </summary>
    [Fact]
    public void ConcurrentFirstRun_AllCallersConvergeOnTheSameToken()
    {
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var racePath = Path.Combine(_directory, $"race-{iteration}.token");
            const int callerCount = 8;
            using var barrier = new Barrier(callerCount);
            var results = new string?[callerCount];

            Parallel.For(0, callerCount, i =>
            {
                barrier.SignalAndWait();
                results[i] = AlertHttpTokenFile.LoadOrCreate(racePath);
            });

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
}
