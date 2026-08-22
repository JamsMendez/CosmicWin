using CosmicWin.Launcher;

namespace CosmicWin.App.Tests.Startup;

/// <summary>Task 3.22: <see cref="Program.Run"/> is the entire shim (D6) -- runs the registered elevated Scheduled Task via <c>schtasks /Run</c> and returns its exit code.</summary>
public sealed class LauncherProgramTests
{
    [Fact]
    public void Run_InvokesSchtasksRunWithFixedArgv_ForTheProductionTaskName()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 0 };

        var exitCode = Program.Run(runner);

        Assert.Equal("schtasks.exe", runner.LastFileName);
        Assert.Equal(new[] { "/Run", "/TN", Program.TaskName }, runner.LastArguments);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Run_PropagatesSchtasksExitCode_NeverSwallowsFailure()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 9 };

        var exitCode = Program.Run(runner);

        Assert.Equal(9, exitCode);
    }
}
