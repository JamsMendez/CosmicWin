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

    /// <summary>The exact, fixed argv <c>schtasks /Query</c> receives -- V26-W1's locale-independent existence check.</summary>
    public static IReadOnlyList<string> BuildQueryArgs(string taskName) =>
        new[] { "/Query", "/TN", taskName };

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
    /// that was never installed is treated as success -- ES-4's "cleanly, restoring stock behavior"
    /// needs nothing restored when there is nothing left.
    ///
    /// V26-W1: existence, not /Delete's stderr, decides idempotency. schtasks' error text is a
    /// per-language MUI resource and does not match on non-English Windows. When /Delete fails, a
    /// follow-up <c>/Query</c> call asks schtasks itself whether the task remains; if it does not,
    /// the failure is swallowed regardless of what /Delete's own text said. If it does, /Delete's
    /// original error is thrown unchanged.
    ///
    /// Tradeoff: if /Query itself fails for an unrelated reason (e.g. the Task Scheduler service is
    /// stopped), schtasks reports that with the same generic non-zero exit code as "not found", so
    /// this check treats it as absence too rather than re-adding a locale-dependent text match.
    /// </summary>
    public void Uninstall()
    {
        var result = _runner.Run(SchTasksExe, BuildUninstallArgs(_taskName));
        if (result.ExitCode == 0)
        {
            return;
        }

        var queryResult = _runner.Run(SchTasksExe, BuildQueryArgs(_taskName));
        if (queryResult.ExitCode == 0)
        {
            throw new InvalidOperationException(
                $"schtasks /Delete failed with exit code {result.ExitCode}: {result.StandardError}");
        }
    }
}
