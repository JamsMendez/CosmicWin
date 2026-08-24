using CosmicWin.App.Startup;
using CosmicWin.Interop;

namespace CosmicWin.App.Tests.Startup;

/// <summary>Real <c>schtasks</c> round-trip (ES-2/ES-4); skips, never fakes elevation, unless both <c>COSMICWIN_RUN_DESKTOP_TESTS=1</c> is set AND the process is actually elevated.</summary>
[Collection(Desktop.RealDesktopCollection.Name)]
public sealed class TaskInstallerElevatedTests
{
    [RequiresElevatedFact]
    [Trait("Category", "RequiresDesktop")]
    public void InstallThenUninstall_RealSchtasks_RoundTrips()
    {
        var runner = new Win32ProcessRunner();
        var taskName = $"CosmicWin.Test.{Guid.NewGuid():N}";
        var xmlPath = Path.Combine(Path.GetTempPath(), $"{taskName}.xml");
        var installer = new TaskInstaller(taskName, Environment.ProcessPath!, xmlPath, runner);

        try
        {
            installer.Install();
            var query = runner.Run("schtasks.exe", new[] { "/Query", "/TN", taskName });
            Assert.Equal(0, query.ExitCode);

            installer.Uninstall();
            var queryAfter = runner.Run("schtasks.exe", new[] { "/Query", "/TN", taskName });
            Assert.NotEqual(0, queryAfter.ExitCode);
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }
}
