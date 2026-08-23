using System.Diagnostics;

namespace CosmicWin.Interop;

/// <summary>
/// Injectable process-execution seam. Design threat matrix, "Subprocess: schtasks"
/// row: callers always pass a FIXED argv array, never a composed command-line string. Lives here,
/// not <c>CosmicWin.App</c>, so <c>CosmicWin.Launcher</c>'s unelevated shim can reuse it without
/// referencing the whole App assembly.
/// </summary>
public interface IProcessRunner
{
    ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments);
}

/// <summary>Exit code and captured output of one <see cref="IProcessRunner.Run"/> call.</summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Real <see cref="IProcessRunner"/> backed by <see cref="Process"/> and <see cref="ProcessStartInfo.ArgumentList"/> (never a composed command-line string).</summary>
public sealed class Win32ProcessRunner : IProcessRunner
{
    public ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessRunResult(process.ExitCode, stdout, stderr);
    }
}
