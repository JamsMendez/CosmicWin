using System.Xml;
using CosmicWin.App.Startup;

namespace CosmicWin.App.Tests.Startup;

public sealed class TaskInstallerTests
{
    // Task 3.19 RED #1 (threat matrix): fixed argv array, never a composed command-line string.
    [Fact]
    public void BuildInstallArgs_ReturnsExactFixedArgv()
    {
        var args = TaskInstaller.BuildInstallArgs("CosmicWin", @"C:\Users\x\AppData\Local\CosmicWin\Task.xml");

        Assert.Equal(
            new[] { "/Create", "/TN", "CosmicWin", "/XML", @"C:\Users\x\AppData\Local\CosmicWin\Task.xml", "/F" },
            args);
    }

    [Fact]
    public void BuildUninstallArgs_ReturnsExactFixedArgv()
    {
        var args = TaskInstaller.BuildUninstallArgs("CosmicWin");

        Assert.Equal(new[] { "/Delete", "/TN", "CosmicWin", "/F" }, args);
    }

    // Task 3.19 RED #2 (threat matrix): injection-shaped task names rejected before reaching argv.
    [Theory]
    [InlineData("CosmicWin & del /f /q C:\\")]
    [InlineData("CosmicWin\" /Create /TN evil")]
    [InlineData("")]
    public void Constructor_RejectsInjectionShapedTaskName(string taskName)
    {
        Assert.Throws<ArgumentException>(() =>
            new TaskInstaller(taskName, @"C:\CosmicWin.exe", @"C:\Task.xml", new FakeProcessRunner()));
    }

    // Task 3.19 RED #3 (threat matrix): non-zero schtasks exit propagates, never silently swallowed.
    [Fact]
    public void Install_NonZeroExit_ThrowsAndNeverSwallows()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 1 };
        var xmlPath = Path.Combine(Path.GetTempPath(), $"cosmicwin-{Guid.NewGuid():N}.xml");
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", xmlPath, runner);

        var ex = Assert.Throws<InvalidOperationException>(installer.Install);
        Assert.Contains("exit code 1", ex.Message);
        File.Delete(xmlPath);
    }

    [Fact]
    public void Uninstall_NonZeroExit_ThrowsAndNeverSwallows()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 5 };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        var ex = Assert.Throws<InvalidOperationException>(installer.Uninstall);
        Assert.Contains("exit code 5", ex.Message);
    }

    // Task 3.20 GREEN: install writes the XML and invokes schtasks with the exact fixed argv (ES-2).
    [Fact]
    public void Install_Success_WritesXmlAndInvokesSchtasksWithFixedArgv()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 0 };
        var xmlPath = Path.Combine(Path.GetTempPath(), $"cosmicwin-{Guid.NewGuid():N}.xml");
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", xmlPath, runner);

        installer.Install();

        Assert.Equal("schtasks.exe", runner.LastFileName);
        Assert.Equal(TaskInstaller.BuildInstallArgs("CosmicWin.Test", xmlPath), runner.LastArguments);
        Assert.True(File.Exists(xmlPath));
        Assert.Contains("HighestAvailable", File.ReadAllText(xmlPath));
        File.Delete(xmlPath);
    }

    // Task 3.20 GREEN (ES-4): uninstall invokes schtasks /Delete /F with the exact fixed argv.
    [Fact]
    public void Uninstall_Success_InvokesSchtasksDeleteWithFixedArgv()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 0 };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        installer.Uninstall();

        Assert.Equal("schtasks.exe", runner.LastFileName);
        Assert.Equal(TaskInstaller.BuildUninstallArgs("CosmicWin.Test"), runner.LastArguments);
    }

    // V25-W2 RED: schtasks /Delete against a task that was never installed exits 1 with this exact
    // stderr (measured against real schtasks in verify-report V25-W2). ES-4 requires removal
    // "cleanly, restoring stock behavior" -- there is nothing to restore here, so this is success,
    // not a failure. Only THIS specific absence signature is swallowed.
    [Fact]
    public void Uninstall_TaskAlreadyAbsent_IsIdempotent_DoesNotThrow()
    {
        var runner = new FakeProcessRunner
        {
            ExitCodeToReturn = 1,
            StandardErrorToReturn = "ERROR: The system cannot find the file specified.\r\n",
        };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        installer.Uninstall(); // Must not throw.

        Assert.Equal(TaskInstaller.BuildUninstallArgs("CosmicWin.Test"), runner.LastArguments);
    }

    // V25-W2 companion: a genuine failure (unrelated stderr, e.g. access denied) MUST still throw --
    // the idempotency fix must not swallow real errors.
    [Fact]
    public void Uninstall_GenuineFailure_StillThrows_NotSwallowedByIdempotencyFix()
    {
        var runner = new FakeProcessRunner
        {
            ExitCodeToReturn = 5,
            StandardErrorToReturn = "ERROR: Access is denied.\r\n",
        };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        var ex = Assert.Throws<InvalidOperationException>(installer.Uninstall);
        Assert.Contains("exit code 5", ex.Message);
    }

    // V25-C1 RED (unconditional, no trait gate, no elevation, no real task): the declared encoding
    // and the on-disk bytes must agree, or MSXML6 (Task Scheduler's own parser) rejects the file
    // with "Switch from current encoding to specified encoding not supported." A real XmlDocument
    // parse against the ACTUAL WRITTEN BYTES is the only assertion that would have caught this --
    // asserting string content via File.ReadAllText (encoding-detecting) does not.
    [Fact]
    public void Install_WritesXmlWhoseDeclaredEncodingMatchesItsOnDiskBytes_AndParsesWithARealXmlParser()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 0 };
        var xmlPath = Path.Combine(Path.GetTempPath(), $"cosmicwin-{Guid.NewGuid():N}.xml");
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", xmlPath, runner);

        try
        {
            installer.Install();

            var bytes = File.ReadAllBytes(xmlPath);
            Assert.True(bytes.Length >= 2, "Written XML file is too short to carry a byte order mark.");
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xFE, bytes[1]);

            using var stream = File.OpenRead(xmlPath);
            var document = new XmlDocument();
            document.Load(stream); // Throws XmlException if the declared/actual encodings disagree.
            Assert.Equal("Task", document.DocumentElement!.LocalName);
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }
}
