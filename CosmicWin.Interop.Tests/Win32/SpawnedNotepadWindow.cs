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
/// <see cref="IDisposable"/> requests a normal close and only escalates to <see
/// cref="Process.Kill"/> if that graceful close does not complete within its own bound (:
/// under back-to-back suite runs, <c>CloseMainWindow</c>'s WM_CLOSE can take longer than 5 seconds
/// to be processed under load, leaving a process that exits "shortly after" instead of within the
/// bound the original best-effort wait relied on -- observed directly during
/// revision 11's audit). Escalating guarantees the process has actually exited by the time <see
/// cref="Dispose"/> returns, instead of merely having requested that it exit, so a later test's own
/// close-detection poll only has to wait out the OS's own handle-teardown lag rather than an
/// unbounded amount of the target process's own message-pump latency.
/// </para>
/// </remarks>
/// <summary>
/// Made <c>public</c> (was <c>internal</c>) so <c>CosmicWin.App.Tests</c> can reuse this
/// exact hardened spawn/dispose harness for a real-desktop tiling integration test, via a new
/// <c>CosmicWin.App.Tests</c>-&gt;<c>CosmicWin.Interop.Tests</c> <c>ProjectReference</c>, rather
/// than re-implementing the same close/kill-on-timeout logic a second time.
/// </summary>
public sealed class SpawnedNotepadWindow : IDisposable
{
    /// <summary>How long the failure path keeps sweeping for a Notepad that has not appeared yet.</summary>
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(8);

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

    /// <summary>The real OS process id that owns <see cref="Handle"/>.</summary>
    public int ProcessId => _windowProcess.Id;

    /// <summary>Spawns Notepad and blocks (bounded) until its real main window handle is known.</summary>
    public static SpawnedNotepadWindow Spawn(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var marker = $"cosmicwin-{Guid.NewGuid():N}";
        var filePath = Path.Combine(Path.GetTempPath(), $"{marker}.txt");
        File.WriteAllText(filePath, string.Empty);

        // Every Notepad alive BEFORE the launch, so anything new can be recognised as ours.
        // Deliberately by process id and not by the marker in the window title: on the failure path
        // the marker FILE is gone before Notepad ever opens it, so the leaked window is titled
        // "Notepad" or nothing at all and matches no marker. A leak detector keyed on the title
        // reports green while two orphans sit on the desktop -- measured, not supposed.
        var preExisting = NotepadProcessIds();

        try
        {
            using (var launcher = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{filePath}\"") { UseShellExecute = true }))
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
                    throw new TimeoutException(
                        "Timed out waiting for the Notepad window associated with this test launch.");
                }

                Thread.Sleep(50);
            }
        }
        catch
        {
            // Giving up is not a reason to leave a window on the desktop. The old code deleted the
            // marker file, threw, and never touched the Notepad it had already launched -- so a
            // spawn that timed out leaked a real top-level window for the rest of the session, which
            // is exactly what a later foreground assertion trips over.
            KillNotepadsStartedSince(preExisting);
            TryDelete(filePath);
            throw;
        }
    }

    /// <summary>
    /// Kills every Notepad that appeared after <paramref name="preExisting"/> was captured, sweeping
    /// repeatedly for a short while rather than once.
    /// </summary>
    /// <remarks>
    /// The sweep has to outlive the giving-up moment, because that moment is precisely when the
    /// window does not exist yet: <c>notepad.exe</c> is a launcher stub that hands off to a separate
    /// window-owning process, and a spawn times out exactly when that hand-off is slow. Killing once
    /// at the instant of failure finds nothing to kill and leaves the leak intact.
    /// </remarks>
    private static void KillNotepadsStartedSince(HashSet<int> preExisting)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            foreach (var id in NotepadProcessIds())
            {
                if (preExisting.Contains(id))
                {
                    continue;
                }

                try
                {
                    using var stray = Process.GetProcessById(id);
                    stray.Kill();
                    stray.WaitForExit(5000);
                }
                catch
                {
                    // Per process: one that will not die must not stop the others being cleaned up,
                    // and none of this may mask the failure being propagated.
                }
            }

            Thread.Sleep(100);
        }
        while (clock.Elapsed < CleanupGrace);
    }

    private static HashSet<int> NotepadProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var candidate in Process.GetProcessesByName("notepad"))
        {
            using (candidate)
            {
                ids.Add(candidate.Id);
            }
        }

        return ids;
    }

    /// <summary>Deleting the marker file must never replace the exception explaining why we are here.</summary>
    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // A leftover temp file is a far smaller problem than an IOException masking a timeout.
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
                if (!_windowProcess.WaitForExit(5000))
                {
                    // The graceful close did not finish within the bound -- escalate so
                    // Dispose() never returns while the process is still alive, guaranteeing the
                    // caller's own close-detection poll only has to wait out OS handle teardown.
                    _windowProcess.Kill();
                    _windowProcess.WaitForExit(5000);
                }
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
