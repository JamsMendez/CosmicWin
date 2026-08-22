using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests;

public sealed class Win32ProcessRunnerTests
{
    [Fact]
    public void Run_UsesArgumentList_AndReturnsRealExitCode()
    {
        var runner = new Win32ProcessRunner();

        var result = runner.Run("cmd.exe", new[] { "/c", "exit 7" });

        Assert.Equal(7, result.ExitCode);
    }
}
