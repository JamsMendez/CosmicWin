using System.IO;
using System.Text;
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

    // V25-C1: TaskXmlBuilder declares "encoding=UTF-16" (TaskXmlBuilder.cs:15) -- MSXML6, the parser
    // Task Scheduler itself uses, requires the on-disk bytes to actually be UTF-16LE with a leading
    // byte order mark, or it rejects the file with "Switch from current encoding to specified
    // encoding not supported." UTF-16+BOM is chosen over re-declaring utf-8 because it is the
    // verified-working route: confirmed against a real MSXML6 parse (see verify-report V25-C1).
    private static readonly UnicodeEncoding TaskXmlEncoding = new(bigEndian: false, byteOrderMark: true);

    // V25-W2: the exact stderr schtasks prints when /Delete targets a task that does not exist --
    // matched to make Uninstall idempotent (ES-4 "cleanly"), never to swallow a genuine failure.
    private const string TaskNotFoundErrorFragment = "cannot find the file specified";

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
        File.WriteAllText(_xmlPath, TaskXmlBuilder.Build(_exePath), TaskXmlEncoding);

        var result = _runner.Run(SchTasksExe, BuildInstallArgs(_taskName, _xmlPath));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks /Create failed with exit code {result.ExitCode}: {result.StandardError}");
        }
    }

    /// <summary>
    /// ES-4: removes the Scheduled Task cleanly via <c>schtasks /Delete /F</c>. Idempotent: a task
    /// that was never installed (or already removed) is treated as success, matching ES-4's "cleanly,
    /// restoring stock behavior" -- there is nothing to restore, so this is not an error. Any other
    /// non-zero exit still throws; only the specific "task does not exist" case is swallowed.
    /// </summary>
    public void Uninstall()
    {
        var result = _runner.Run(SchTasksExe, BuildUninstallArgs(_taskName));
        if (result.ExitCode != 0 && !IndicatesTaskAlreadyAbsent(result))
        {
            throw new InvalidOperationException(
                $"schtasks /Delete failed with exit code {result.ExitCode}: {result.StandardError}");
        }
    }

    private static bool IndicatesTaskAlreadyAbsent(ProcessRunResult result) =>
        result.StandardError.Contains(TaskNotFoundErrorFragment, StringComparison.OrdinalIgnoreCase);
}
