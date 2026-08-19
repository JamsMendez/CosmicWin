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
/// exits almost immediately, and the real, window-owning "Notepad" process appears under a
/// <b>different</b> process id shortly after. Waiting on the launcher's own
/// <see cref="Process.MainWindowHandle"/> never succeeds on such builds. This class instead
/// snapshots existing "notepad"-named process ids before spawning, then polls for a NEW
/// "notepad"-named process (any id not in that snapshot) that has produced a main window handle —
/// this works whether or not the current Windows build uses the launcher-stub indirection.
/// </para>
/// <para>
/// <see cref="IDisposable"/> guarantees the spawned window process is always killed, even if a
/// test assertion fails — callers MUST use a <c>using</c> declaration/block (or `finally`) so a
/// failing test never leaks a live Notepad process.
/// </para>
/// </remarks>
internal sealed class SpawnedNotepadWindow : IDisposable
{
    private readonly Process _windowProcess;
    private bool _disposed;

    private SpawnedNotepadWindow(Process windowProcess, nint handle)
    {
        _windowProcess = windowProcess;
        Handle = handle;
    }

    /// <summary>The real native handle of the spawned Notepad window.</summary>
    public nint Handle { get; }

    /// <summary>Spawns Notepad and blocks (bounded) until its real main window handle is known.</summary>
    public static SpawnedNotepadWindow Spawn(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var preExisting = new HashSet<int>(Process.GetProcessesByName("notepad").Select(p => p.Id));

        using (var launcher = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true }))
        {
            if (launcher is null)
            {
                throw new InvalidOperationException("Failed to start notepad.exe.");
            }
        }

        while (true)
        {
            foreach (var candidate in Process.GetProcessesByName("notepad"))
            {
                if (preExisting.Contains(candidate.Id))
                {
                    candidate.Dispose();
                    continue;
                }

                candidate.Refresh();
                var handle = candidate.MainWindowHandle;
                if (handle != 0)
                {
                    return new SpawnedNotepadWindow(candidate, handle);
                }

                candidate.Dispose();
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Timed out waiting for a new Notepad window to appear. If notepad.exe's " +
                    "process/launch model changed on this Windows build, SpawnedNotepadWindow's " +
                    "new-process-id matching strategy may need to be revisited.");
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
                _windowProcess.Kill();
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
        }
    }
}
