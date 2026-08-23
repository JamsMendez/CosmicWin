using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CosmicWin.Interop;

namespace CosmicWin.App.Startup;

/// <summary>
/// Registers/removes the elevated logon Scheduled Task via
/// <c>schtasks.exe</c>. Threat matrix: task name is allow-list validated, every call passes a
/// FIXED argv array, and a non-zero exit always throws, never swallowed.
/// </summary>
public sealed class TaskInstaller
{
    private static readonly Regex ValidTaskName = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private const string SchTasksExe = "schtasks.exe";

    // TaskXmlBuilder declares "encoding=UTF-16" (TaskXmlBuilder.cs:15) -- MSXML6, the parser
    // Task Scheduler itself uses, requires the on-disk bytes to actually be UTF-16LE with a leading
    // byte order mark, or it rejects the file with "Switch from current encoding to specified
    // encoding not supported." UTF-16+BOM is chosen over re-declaring utf-8 because it is the
    // Verified-working route: confirmed against a real MSXML6 parse.
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

    /// <summary>The exact, fixed argv <c>schtasks /Create</c> receives -- 's RED "fixed argv array" proof target.</summary>
    public static IReadOnlyList<string> BuildInstallArgs(string taskName, string xmlPath) =>
        new[] { "/Create", "/TN", taskName, "/XML", xmlPath, "/F" };

    /// <summary>The exact, fixed argv <c>schtasks /Delete</c> receives (ES-4).</summary>
    public static IReadOnlyList<string> BuildUninstallArgs(string taskName) =>
        new[] { "/Delete", "/TN", taskName, "/F" };

    /// <summary>The exact, fixed argv <c>schtasks /Query</c> receives -- 's locale-independent existence check.</summary>
    public static IReadOnlyList<string> BuildQueryArgs(string taskName) =>
        new[] { "/Query", "/TN", taskName };

    /// <summary>
    /// The exact, fixed argv <c>schtasks /Change /DISABLE</c> receives (TC-3). Deliberately NOT
    /// <see cref="BuildUninstallArgs"/>: TC-3 says "disable the Scheduled Task trigger", where ES-4
    /// says "remove the Scheduled Task ... restoring stock behavior". Quitting from the tray must
    /// not throw the user's installation away -- the task stays registered, only its trigger stops.
    /// </summary>
    public static IReadOnlyList<string> BuildDisableArgs(string taskName) =>
        new[] { "/Change", "/TN", taskName, "/DISABLE" };

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
    /// Existence, not /Delete's stderr, decides idempotency. Schtasks' error text is a
    /// per-language MUI resource and does not match on non-English Windows. When /Delete fails, a
    /// follow-up <c>/Query</c> call asks schtasks itself whether the task remains; if it does not,
    /// the failure is swallowed regardless of what /Delete's own text said. If it does, /Delete's
    /// original error is thrown unchanged.
    ///
    /// Tradeoff: if /Query itself fails for an unrelated reason (e.g. the Task Scheduler service is
    /// stopped), schtasks reports that with the same generic non-zero exit code as "not found", so
    /// this check treats it as absence too rather than re-adding a locale-dependent text match.
    /// </summary>
    public void Uninstall() => RunUnlessTheTaskIsAlreadyGone("/Delete", BuildUninstallArgs(_taskName));

    /// <summary>
    /// TC-3 (Salir): stops the logon trigger from firing again, without unregistering the task.
    /// Shares <see cref="RunUnlessTheTaskIsAlreadyGone"/> with <see cref="Uninstall"/> so the
    /// locale-independent idempotency rule established lives at ONE choke point -- a second
    /// copy is a second place for it to drift back to reading stderr text.
    /// </summary>
    public void Disable() => RunUnlessTheTaskIsAlreadyGone("/Change /DISABLE", BuildDisableArgs(_taskName));

    /// <summary>
    /// Runs <paramref name="arguments"/> and, on failure, asks schtasks itself whether the task
    /// still exists (: never its stderr, which is a per-language MUI resource). Gone means
    /// there was nothing to do and the failure is swallowed; still present means the failure was
    /// genuine and <paramref name="verb"/>'s original exit code is thrown unchanged.
    /// </summary>
    private void RunUnlessTheTaskIsAlreadyGone(string verb, IReadOnlyList<string> arguments)
    {
        var result = _runner.Run(SchTasksExe, arguments);
        if (result.ExitCode == 0)
        {
            return;
        }

        var queryResult = _runner.Run(SchTasksExe, BuildQueryArgs(_taskName));
        if (queryResult.ExitCode == 0)
        {
            throw new InvalidOperationException(
                $"schtasks {verb} failed with exit code {result.ExitCode}: {result.StandardError}");
        }
    }
}
