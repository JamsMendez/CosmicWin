using CosmicWin.App.Startup;

namespace CosmicWin.App.Tests.Startup;

public sealed class TaskXmlBuilderTests
{
    [Fact]
    public void Build_IncludesHighestPrivilegeLogonTrigger_AndExePath()
    {
        var xml = TaskXmlBuilder.Build(@"C:\Program Files\CosmicWin\CosmicWin.exe");

        Assert.Contains("<LogonTrigger>", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains(@"C:\Program Files\CosmicWin\CosmicWin.exe", xml);
    }

    [Fact]
    public void Build_EscapesXmlSpecialCharactersInExePath()
    {
        var xml = TaskXmlBuilder.Build(@"C:\A & B\CosmicWin.exe");

        Assert.Contains("C:\\A &amp; B\\CosmicWin.exe", xml);
        Assert.DoesNotContain("C:\\A & B\\CosmicWin.exe", xml);
    }

    // V25-W6: without an explicit <ExecutionTimeLimit>, Task Scheduler applies the schema default
    // PT72H, killing the WM after 3 days of uptime. PT0S means "no limit". Inspection-based (a real
    // Task Scheduler run cannot be exercised here -- registering a real task is forbidden), so this
    // pins the XML content, the strongest proof available without an elevated desktop session.
    [Fact]
    public void Build_SetsUnlimitedExecutionTimeLimit_SoTaskSchedulerNeverKillsTheWm()
    {
        var xml = TaskXmlBuilder.Build(@"C:\CosmicWin.exe");

        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
    }
}
