using System.Diagnostics;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Spawns a real, lightweight top-level window (Notepad) for integration tests that exercise the
/// production, CsWin32-backed <c>Win32NativeWindowSource</c> against an actual desktop session and
/// a genuinely external process — as opposed to <see cref="FakeNativeWindowSource"/>, which only
/// exercises <c>Win32Workspace</c>'s tracking algorithm in-memory.
/// </summary>
/// <remarks>
/// <para>
/// Task 0.10 tradeoff: spawning a real external process instead of creating a minimal native
/// window in-process via CsWin32's <c>CreateWindowEx</c> was chosen because it exercises the full
/// real path end-to-end against a window owned by a genuinely different process — the same shape
/// of window <c>Win32Workspace</c> tracks in production, and what the design's threat-matrix row
/// "Cross-process window manipulation" is actually about. The cost is an external process
/// dependency and non-deterministic startup timing (bounded-retry wait below) versus a
/// same-process <c>CreateWindowEx</c> window, which would be faster and fully deterministic but
/// would never exercise true cross-process semantics.
/// </para>
/// <para>
/// Confirmed via manual probe on this environment's Windows build: <c>notepad.exe</c> is a
/// launcher stub — the <see cref="Process"/> returned by <see cref="Process.Start(ProcessStartInfo)"/>
/// exits almost immediately, and the real, window-owning "Notepad" process may appear under a
/// <b>different</b> process id shortly after. Waiting on the launcher's own
/// <see cref="Process.MainWindowHandle"/> never succeeds on such builds. This class instead
/// opens a uniquely named temporary file and only accepts a Notepad window whose title contains
/// that unguessable marker, which correlates the window to this launch across launcher indirection.
/// </para>
/// <para>
/// <see cref="IDisposable"/> requests a normal close and never forcibly terminates the process.
/// </para>
/// </remarks>
internal sealed class SpawnedNotepadWindow : IDisposable
{
    private readonly Process _windowProcess;
    private readonly string _filePath;
    private bool _disposed;

    private SpawnedNotepadWindow(Process windowProcess, nint handle, string filePath)
    {
        _windowProcess = windowProcess;
        _filePath = filePath;
        Handle = handle;
    }

    /// <summary>The real native handle of the spawned Notepad window.</summary>
    public nint Handle { get; }

    /// <summary>Spawns Notepad and blocks (bounded) until its real main window handle is known.</summary>
    public static SpawnedNotepadWindow Spawn(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var marker = $"cosmicwin-{Guid.NewGuid():N}";
        var filePath = Path.Combine(Path.GetTempPath(), $"{marker}.txt");
        File.WriteAllText(filePath, string.Empty);

        using (var launcher = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{filePath}\"") { UseShellExecute = true }))
        {
            if (launcher is null)
            {
                File.Delete(filePath);
                throw new InvalidOperationException("Failed to start notepad.exe.");
            }
        }

        while (true)
        {
            foreach (var candidate in Process.GetProcessesByName("notepad"))
            {
                candidate.Refresh();
                var handle = candidate.MainWindowHandle;
                if (handle != 0 && candidate.MainWindowTitle.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return new SpawnedNotepadWindow(candidate, handle, filePath);
                }

                candidate.Dispose();
            }

            if (DateTime.UtcNow >= deadline)
            {
                File.Delete(filePath);
                throw new TimeoutException(
                    "Timed out waiting for the Notepad window associated with this test launch.");
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
            // Best-effort cleanup — never let teardown failure mask a test's real failure.
        }
        finally
        {
            _windowProcess.Dispose();
            File.Delete(_filePath);
        }
    }
}
