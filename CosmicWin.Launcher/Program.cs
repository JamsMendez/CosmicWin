using CosmicWin.Interop;

namespace CosmicWin.Launcher;

/// <summary>
/// Unelevated asInvoker shim, no UAC prompt on double-click. Asks the
/// ALREADY-registered elevated Scheduled Task (ES-2) to run via <c>schtasks /Run</c>,
/// which elevates silently via its own RunLevel HighestAvailable.
/// </summary>
public static class Program
{
    public const string TaskName = "CosmicWin";

    public static int Main() => Run(new Win32ProcessRunner());

    public static int Run(IProcessRunner runner)
    {
        var result = runner.Run("schtasks.exe", new[] { "/Run", "/TN", TaskName });
        return result.ExitCode;
    }
}
