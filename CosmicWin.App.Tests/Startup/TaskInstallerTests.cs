using System.Xml;
using CosmicWin.App.Startup;
using CosmicWin.Interop;

namespace CosmicWin.App.Tests.Startup;

public sealed class TaskInstallerTests
{
    // Fixed argv array, never a composed command-line string.
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

    // Injection-shaped task names rejected before reaching argv.
    [Theory]
    [InlineData("CosmicWin & del /f /q C:\\")]
    [InlineData("CosmicWin\" /Create /TN evil")]
    [InlineData("")]
    public void Constructor_RejectsInjectionShapedTaskName(string taskName)
    {
        Assert.Throws<ArgumentException>(() =>
            new TaskInstaller(taskName, @"C:\CosmicWin.exe", @"C:\Task.xml", new FakeProcessRunner()));
    }

    // Non-zero schtasks exit propagates, never silently swallowed.
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
        var runner = new FakeProcessRunner
        {
            ExitCodeToReturn = 5,
            ResultByVerb = new() { ["/Query"] = new ProcessRunResult(0, "TaskName: CosmicWin.Test", string.Empty) },
        };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        var ex = Assert.Throws<InvalidOperationException>(installer.Uninstall);
        Assert.Contains("exit code 5", ex.Message);
    }

    // Install writes the XML and invokes schtasks with the exact fixed argv (ES-2).
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

    // Uninstall invokes schtasks /Delete /F with the exact fixed argv.
    [Fact]
    public void Uninstall_Success_InvokesSchtasksDeleteWithFixedArgv()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 0 };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        installer.Uninstall();

        Assert.Equal("schtasks.exe", runner.LastFileName);
        Assert.Equal(TaskInstaller.BuildUninstallArgs("CosmicWin.Test"), runner.LastArguments);
    }

    // Schtasks /Delete against a task that was never installed exits 1 with this exact
    // stderr (measured against real schtasks in verify-report). ES-4 requires removal
    // "cleanly, restoring stock behavior" -- there is nothing to restore here, so this is success,
    // not a failure. A follow-up /Query confirms absence (: stderr text is never read).
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

        Assert.Equal(TaskInstaller.BuildQueryArgs("CosmicWin.Test"), runner.LastArguments);
    }

    // A genuine failure (unrelated stderr, e.g. Access denied) MUST still throw
    // the idempotency fix must not swallow real errors.
    [Fact]
    public void Uninstall_GenuineFailure_StillThrows_NotSwallowedByIdempotencyFix()
    {
        var runner = new FakeProcessRunner
        {
            ExitCodeToReturn = 5,
            StandardErrorToReturn = "ERROR: Access is denied.\r\n",
            ResultByVerb = new() { ["/Query"] = new ProcessRunResult(0, "TaskName: CosmicWin.Test", string.Empty) },
        };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        var ex = Assert.Throws<InvalidOperationException>(installer.Uninstall);
        Assert.Contains("exit code 5", ex.Message);
    }

    // The "task already absent" signature is an OS-localised FormatMessage string
    // proven language-dependent by direct FormatMessageW measurement in verify-report. On
    // Spanish Windows /Delete's stderr never contains the English fragment, so a stderr-only guard
    // throws for an absent task. The locale-independent fix asks schtasks itself via /Query -- it
    // never reads /Delete's stderr text at all, so this must succeed regardless of language.
    [Fact]
    public void Uninstall_TaskAlreadyAbsent_NonEnglishLocale_IsIdempotent_DoesNotThrow()
    {
        var runner = new FakeProcessRunner
        {
            ExitCodeToReturn = 1,
            StandardErrorToReturn = "ERROR: El sistema no puede encontrar el archivo especificado.\r\n",
            ResultByVerb = new() { ["/Query"] = new ProcessRunResult(1, string.Empty, "ERROR: El sistema no puede encontrar el archivo especificado.\r\n") },
        };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        installer.Uninstall(); // Must not throw -- no English text is ever consulted.
    }

    // Pins the discriminator itself, not just its outcome. Exit code 1 alone is
    // ambiguous -- schtasks returns it both for "task not found" AND for unrelated genuine
    // failures -- so a guard keyed on exit code alone (mutation M7 / the naive "ExitCode == 1"
    // discriminator) would wrongly swallow this. Only a real /Query check, which here reports the
    // task STILL EXISTS, can tell the two apart.
    [Fact]
    public void Uninstall_GenuineFailure_SameExitCodeAsAbsent_StillThrows()
    {
        var runner = new FakeProcessRunner
        {
            ExitCodeToReturn = 1,
            StandardErrorToReturn = "ERROR: Access is denied.\r\n",
            ResultByVerb = new() { ["/Query"] = new ProcessRunResult(0, "TaskName: CosmicWin.Test", string.Empty) },
        };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        var ex = Assert.Throws<InvalidOperationException>(installer.Uninstall);
        Assert.Contains("exit code 1", ex.Message);
    }

    [Fact]
    public void BuildQueryArgs_ReturnsExactFixedArgv()
    {
        var args = TaskInstaller.BuildQueryArgs("CosmicWin");

        Assert.Equal(new[] { "/Query", "/TN", "CosmicWin" }, args);
    }

    // The declared encoding
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

    // TC-3-W1 RED #1 (threat matrix, same rule as every other verb here): a fixed argv array, never
    // a composed command-line string. TC-3 says "disable the Scheduled Task trigger", which is
    // deliberately different wording from ES-4's "remove the Scheduled Task ... restoring stock
    // behavior" -- so this is a genuine /Change /DISABLE, not a reuse of /Delete. The task stays
    // registered and the user's own installation is not silently thrown away by quitting once.
    [Fact]
    public void BuildDisableArgs_ReturnsExactFixedArgv()
    {
        var args = TaskInstaller.BuildDisableArgs("CosmicWin");

        Assert.Equal(new[] { "/Change", "/TN", "CosmicWin", "/DISABLE" }, args);
    }

    [Fact]
    public void Disable_Success_InvokesSchtasksChangeDisableWithFixedArgv()
    {
        var runner = new FakeProcessRunner { ExitCodeToReturn = 0 };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        installer.Disable();

        Assert.Equal("schtasks.exe", runner.LastFileName);
        Assert.Equal(TaskInstaller.BuildDisableArgs("CosmicWin.Test"), runner.LastArguments);
    }

    // Mirrors Uninstall's shape exactly: existence decides idempotency, never stderr text,
    // which is a per-language MUI resource. Disabling a task that was never installed is nothing to
    // do, not a failure -- and Salir must never fail because of it.
    [Fact]
    public void Disable_TaskAlreadyAbsent_IsIdempotent_DoesNotThrow()
    {
        var runner = new FakeProcessRunner
        {
            ExitCodeToReturn = 1,
            StandardErrorToReturn = "ERROR: The system cannot find the file specified.\r\n",
        };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        installer.Disable(); // Must not throw.

        Assert.Equal(TaskInstaller.BuildQueryArgs("CosmicWin.Test"), runner.LastArguments);
    }

    // The discriminator: the idempotency guard must not swallow a real failure. /Query reports the
    // task IS still there, so /Change's failure was genuine and has to surface.
    [Fact]
    public void Disable_GenuineFailure_StillThrows_NotSwallowedByIdempotencyGuard()
    {
        var runner = new FakeProcessRunner
        {
            ExitCodeToReturn = 5,
            StandardErrorToReturn = "ERROR: Access is denied.\r\n",
            ResultByVerb = new() { ["/Query"] = new ProcessRunResult(0, "TaskName: CosmicWin.Test", string.Empty) },
        };
        var installer = new TaskInstaller("CosmicWin.Test", @"C:\CosmicWin.exe", @"C:\Task.xml", runner);

        var ex = Assert.Throws<InvalidOperationException>(installer.Disable);
        Assert.Contains("exit code 5", ex.Message);
    }
}
