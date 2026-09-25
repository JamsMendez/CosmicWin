using System.IO;
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
