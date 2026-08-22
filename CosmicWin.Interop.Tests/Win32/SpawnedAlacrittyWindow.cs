using System.Diagnostics;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// WU29: spawns a real, external Alacritty window for real-desktop integration tests that need
/// SEVERAL independently existing top-level windows -- as opposed to <see cref="SpawnedNotepadWindow"/>,
/// whose host (<c>notepad.exe</c>, on this project's Windows build) is the packaged, tabbed Store
/// app: a second launch opens a new TAB inside the first process's window instead of a second
/// top-level window, so it cannot back a multi-window test (see Engram #94; measured directly:
/// two <c>notepad.exe</c> launches produced <c>MainWindowHandle</c> values <c>0</c> and a single
/// real handle, i.e. 2 processes but only 1 top-level window). Alacritty, measured on the same
/// machine, gave 2 processes and 2 distinct top-level window handles for 2 launches.
/// </summary>
/// <remarks>
/// <para>
/// The executable path is never hardcoded: it is read from the <see cref="ExecutablePathEnvVar"/>
/// environment variable and this class fails fast, naming the variable and giving a worked example,
/// when it is unset or does not point at an existing file. A committed test must not embed one
/// developer's absolute machine path.
/// </para>
/// <para>
/// On this configuration Alacritty launches WSL as a child process, so <see cref="Dispose"/> always
/// escalates to <see cref="Process.Kill(bool)"/> with <c>entireProcessTree: true</c> once the
/// bounded graceful <see cref="Process.CloseMainWindow"/> wait elapses (mirroring the same
/// bounded-close-then-escalate hardening as <see cref="SpawnedNotepadWindow.Dispose"/>, V11-W4) --
/// killing only the parent process would leave the WSL child running.
/// </para>
/// </remarks>
public sealed class SpawnedAlacrittyWindow : IDisposable
{
    /// <summary>
    /// Names the environment variable a developer or CI runner must set to the full path of the
    /// Alacritty executable used for real-desktop tests -- deliberately not a hardcoded path,
    /// since the measured binary on the original development machine has a versioned filename
    /// (<c>Alacritty-v0.17.0.exe</c>, not <c>Alacritty.exe</c>) that will differ elsewhere.
    /// </summary>
    public const string ExecutablePathEnvVar = "COSMICWIN_DESKTOP_TEST_TERMINAL";

    private readonly Process _windowProcess;
    private bool _disposed;

    private SpawnedAlacrittyWindow(Process windowProcess, nint handle)
    {
        _windowProcess = windowProcess;
        Handle = handle;
    }

    /// <summary>The real native handle of the spawned Alacritty window.</summary>
    public nint Handle { get; }

    /// <summary>The real OS process id that owns <see cref="Handle"/> (lets a caller verify this spawned window's PID against a protected-ancestry blacklist before ever touching it).</summary>
    public int ProcessId => _windowProcess.Id;

    /// <summary>Spawns Alacritty and blocks (bounded) until its real main window handle is known.</summary>
    public static SpawnedAlacrittyWindow Spawn(TimeSpan? timeout = null)
    {
        var exePath = ResolveExecutablePath();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var marker = $"cosmicwin-{Guid.NewGuid():N}";

        var startInfo = new ProcessStartInfo(exePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add(marker);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{exePath}'.");

        while (true)
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle != 0 && process.MainWindowTitle.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return new SpawnedAlacrittyWindow(process, handle);
            }

            if (DateTime.UtcNow >= deadline)
            {
                TryKillEntireTree(process);
                process.Dispose();
                throw new TimeoutException(
                    "Timed out waiting for the Alacritty window associated with this test launch.");
            }

            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (!_windowProcess.HasExited)
            {
                _windowProcess.CloseMainWindow();
                _windowProcess.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort: a failed graceful-close attempt still gets the guaranteed tree-kill below.
        }

        // Always ensure the WHOLE process tree is gone before returning, not only on a timeout
        // escalation path: Alacritty launches WSL as a child on this configuration, and a graceful
        // CloseMainWindow alone does not reliably reap it. Killing only the parent leaks wsl.exe.
        TryKillEntireTree(_windowProcess);
        _windowProcess.Dispose();
    }

    private static void TryKillEntireTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort cleanup -- never let teardown failure mask a test's real failure.
        }
    }

    private static string ResolveExecutablePath()
    {
        var path = Environment.GetEnvironmentVariable(ExecutablePathEnvVar);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Set the {ExecutablePathEnvVar} environment variable to the full path of the " +
                "Alacritty executable used for real-desktop tests, e.g. " +
                "C:\\Users\\jamsm\\Documents\\Bin\\Alacritty-v0.17.0.exe (a committed test must not " +
                "hardcode one developer's absolute path). Notepad cannot be used for this: on this " +
                "project's Windows build it is a tabbed packaged app and does not yield one " +
                "top-level window per process (see Engram #94).");
        }

        return path;
    }
}
