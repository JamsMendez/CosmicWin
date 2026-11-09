using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
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

    /// <summary>The exact, fixed argv that asks schtasks for the registered task's own XML, read by <see cref="IsEnabled"/>.</summary>
    public static IReadOnlyList<string> BuildQueryXmlArgs(string taskName) =>
        new[] { "/Query", "/TN", taskName, "/XML" };

    /// <summary>
    /// The exact, fixed argv <c>schtasks /Change /DISABLE</c> receives. Deliberately NOT
    /// <see cref="BuildUninstallArgs"/>: turning start-up off from the tray keeps the task
    /// registered and only stops it from firing, so turning it back on needs nothing re-typed.
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
    /// Whether the task is registered and not switched off as a whole -- what <see cref="Install"/>
    /// turns on and <see cref="Disable"/> turns off. A trigger disabled on its own, by hand in Task
    /// Scheduler, is not looked at.
    /// </summary>
    /// <remarks>
    /// Read from the task's own XML, never from <c>/Query</c>'s table text, whose column names and
    /// "Disabled"/"Ready" words are per-language MUI resources. A task that is switched off carries
    /// <c>&lt;Settings&gt;&lt;Enabled&gt;false&lt;/Enabled&gt;</c>; a task that is on simply omits
    /// it, so only an explicit <c>false</c> means off. Any failure to query or parse answers NO: a
    /// tick that promises a start-up nobody can confirm is the one this item must not show.
    /// </remarks>
    public bool IsEnabled()
    {
        var result = _runner.Run(SchTasksExe, BuildQueryXmlArgs(_taskName));
        if (result.ExitCode != 0)
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(result.StandardOutput);
            var settingsEnabled = document.Root?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Settings")?
                .Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled");
            return !string.Equals(settingsEnabled?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stops the logon trigger from firing again, without unregistering the task.
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
