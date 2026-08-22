using System.IO;
using System.Text.RegularExpressions;
using CosmicWin.Interop;

namespace CosmicWin.App.Startup;

/// <summary>
/// Tasks 3.19/3.20 (ES-2/ES-4): registers/removes the elevated logon Scheduled Task via
/// <c>schtasks.exe</c>. Threat matrix: task name is allow-list validated, every call passes a
/// FIXED argv array, and a non-zero exit always throws, never swallowed.
/// </summary>
public sealed class TaskInstaller
{
    private static readonly Regex ValidTaskName = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private const string SchTasksExe = "schtasks.exe";

    private readonly string _taskName;
    private readonly string _exePath;
    private readonly string _xmlPath;
    private readonly IProcessRunner _runner;

    public TaskInstaller(string taskName, string exePath, string xmlPath, IProcessRunner runner)
    {
        if (string.IsNullOrEmpty(taskName) || !ValidTaskName.IsMatch(taskName))
        {
            throw new ArgumentException(
                $"Task name '{taskName}' contains characters unsafe for a schtasks argument; only letters, digits, '.', '_' and '-' are allowed.",
                nameof(taskName));
        }

        _taskName = taskName;
        _exePath = exePath;
        _xmlPath = xmlPath;
        _runner = runner;
    }

    /// <summary>The exact, fixed argv <c>schtasks /Create</c> receives -- task 3.19's RED "fixed argv array" proof target.</summary>
    public static IReadOnlyList<string> BuildInstallArgs(string taskName, string xmlPath) =>
        new[] { "/Create", "/TN", taskName, "/XML", xmlPath, "/F" };

    /// <summary>The exact, fixed argv <c>schtasks /Delete</c> receives (ES-4).</summary>
    public static IReadOnlyList<string> BuildUninstallArgs(string taskName) =>
        new[] { "/Delete", "/TN", taskName, "/F" };

    /// <summary>ES-2: registers the Scheduled Task with highest privileges, trigger "at log on".</summary>
    public void Install()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_xmlPath)!);
        File.WriteAllText(_xmlPath, TaskXmlBuilder.Build(_exePath));

        var result = _runner.Run(SchTasksExe, BuildInstallArgs(_taskName, _xmlPath));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks /Create failed with exit code {result.ExitCode}: {result.StandardError}");
        }
    }

    /// <summary>ES-4: removes the Scheduled Task cleanly via <c>schtasks /Delete /F</c>.</summary>
    public void Uninstall()
    {
        var result = _runner.Run(SchTasksExe, BuildUninstallArgs(_taskName));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks /Delete failed with exit code {result.ExitCode}: {result.StandardError}");
        }
    }
}
