using CosmicWin.App.Startup;

namespace CosmicWin.App.Tests.Startup;

/// <summary>Task 3.20: <see cref="AppComposition.TryHandleTaskCommand"/> is the ONE delegating call <c>App.OnStartup</c> makes for <c>--install-task</c>/<c>--uninstall-task</c> (pinned by <c>AppEntryPointThinnessTests</c>).</summary>
public sealed class AppCompositionTaskCommandTests
{
    [Fact]
    public void TryHandleTaskCommand_NoArgs_ReturnsFalse_AndInvokesNoProcess()
    {
        var runner = new FakeProcessRunner();
        Assert.False(AppComposition.TryHandleTaskCommand(Array.Empty<string>(), runner));
        Assert.Null(runner.LastFileName);
    }

    [Fact]
    public void TryHandleTaskCommand_UnknownArg_ReturnsFalse()
    {
        var runner = new FakeProcessRunner();
        Assert.False(AppComposition.TryHandleTaskCommand(new[] { "--something-else" }, runner));
        Assert.Null(runner.LastFileName);
    }

    [Fact]
    public void TryHandleTaskCommand_InstallTask_InvokesSchtasksCreate_ReturnsTrue()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 0 };

        var handled = AppComposition.TryHandleTaskCommand(new[] { "--install-task" }, runner);

        Assert.True(handled);
        Assert.Equal("schtasks.exe", runner.LastFileName);
        Assert.Equal("/Create", runner.LastArguments![0]);
    }

    [Fact]
    public void TryHandleTaskCommand_UninstallTask_InvokesSchtasksDelete_ReturnsTrue()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 0 };

        var handled = AppComposition.TryHandleTaskCommand(new[] { "--uninstall-task" }, runner);

        Assert.True(handled);
        Assert.Equal(TaskInstaller.BuildUninstallArgs("CosmicWin"), runner.LastArguments);
    }
}
