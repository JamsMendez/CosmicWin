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
}
