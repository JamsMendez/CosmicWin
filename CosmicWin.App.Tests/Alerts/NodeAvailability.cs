using System.Diagnostics;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// Decides whether the Node vm-sandbox harness (T9, alert-tile-mosaic) can run at all, mirroring
/// <c>DesktopGate</c>'s "gate, never fake" shape (<c>CosmicWin.Interop.Tests.Win32.DesktopGate</c>):
/// a pure function of a probe delegate, so the decision can be pinned in a test without actually
/// shelling out.
/// </summary>
/// <remarks>
/// Unlike <c>DesktopGate</c>'s terminal check, there is no environment-variable opt-in to read here
/// -- <c>node</c> is either reachable on PATH or it is not, and the only reliable way to answer that
/// is to try running it. <c>node --version</c> is the cheapest possible call that proves both "the
/// executable exists" and "it actually runs" in one step.
/// </remarks>
internal static class NodeAvailability
{
    public static string? SkipReasonIfMissing() => SkipReasonIfMissing(TryRunNodeVersion);

    /// <inheritdoc cref="SkipReasonIfMissing()"/>
    public static string? SkipReasonIfMissing(Func<bool> nodeRuns) =>
        nodeRuns()
            ? null
            : "Requires `node` on PATH to run the alert-layer.js vm-sandbox harness (T9, alert-tile-mosaic).";

    /// <summary>
    /// T14 (alert-tile-mosaic, review R3/R4-node-harness-no-timeout): the probe's OWN bound,
    /// independent of <c>AlertLayerLayoutNodeTests.HarnessTimeout</c> -- nothing derives one from the
    /// other, so changing the harness timeout leaves this one alone. Short because
    /// <c>node --version</c> should answer in milliseconds; a hang here is not something a
    /// well-behaved install ever does.
    /// </summary>
    private const int ProbeTimeoutMilliseconds = 5000;

    private static bool TryRunNodeVersion() =>
        TryRunProbe("node", ["--version"], TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds));

    /// <summary>
    /// R3-probe-timeout-path-unproved: the probe's command, arguments and bound as an internal seam, so
    /// a test can aim it at a process that never exits and watch the timeout branch (kill the tree,
    /// report unavailable) run for real instead of waiting <see cref="ProbeTimeoutMilliseconds"/> on a
    /// genuinely hung `node --version`. <see cref="TryRunNodeVersion"/> is the only production caller
    /// and passes the same command and bound the probe always used. <paramref name="readyFile"/> and
    /// <paramref name="readinessBound"/> are test-only: they start <paramref name="timeout"/> only once the
    /// script reported ready, waiting at most <paramref name="readinessBound"/> (default
    /// <see cref="ReadinessBound"/>) -- see <see cref="WaitUntilReady"/>.
    /// </summary>
    internal static bool TryRunProbe(
        string fileName, IEnumerable<string> arguments, TimeSpan timeout, string? readyFile = null,
        TimeSpan? readinessBound = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Read concurrently, not that a single short version line could ever fill a pipe buffer,
            // but so this probe never becomes a second place that reintroduces the sequential-read
            // deadlock risk the harness runner (AlertLayerLayoutNodeTests.RunNode) was fixed for.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            WaitUntilReady(process, readyFile, readinessBound);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                // R3-node-probe-leaks-on-timeout: the old code called WaitForExit and then read
                // ExitCode regardless of whether it actually returned true, so a hung `node --version`
                // was left running in the background AND (see the comment below) only reported
                // "unavailable" by accident, via the exception ExitCode throws on a still-running
                // process. Both are fixed together: kill the tree explicitly, and branch on
                // WaitForExit's own result instead of relying on that exception.
                process.Kill(entireProcessTree: true);
                return false;
            }

            _ = stdoutTask.Result;
            _ = stderrTask.Result;
            return process.ExitCode == 0;
        }
        // R2-node-probe-catch-comment-misleading: this used to say only "node missing from PATH
        // surfaces as Win32Exception on some machines, and a process that never starts still leaves
        // WaitForExit valid to call" -- true, but it hid the ACTUAL reason InvalidOperationException
        // used to reach here on every timeout: Process.ExitCode throws that exact type when read
        // before the process has exited, which is exactly what an unchecked WaitForExit result let
        // happen above. Now that the timeout branch checks WaitForExit's result explicitly and
        // returns before ever touching ExitCode, this catch guards only genuine start failures
        // (Win32Exception) and the unlikely case of the process handle becoming invalid between the
        // checks above (InvalidOperationException) -- a gate deciding whether to SKIP must still never
        // crash test discovery over either one.
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    internal static readonly TimeSpan ReadinessBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// R3-pid-handshake-shares-timeout-budget: a hang bound that starts when the process starts also pays
    /// for Node's cold start, so a slow start could kill a test script before it wrote its pid and fail the
    /// test for no real reason. A caller that names a ready file gets its bound started only once that file
    /// holds a pid, or the process exits on its own. A script still running but not ready when
    /// <see cref="ReadinessBound"/> runs out is killed (whole tree) and reported with a
    /// <see cref="TimeoutException"/> (R2-ready-wait-silent-on-readiness-bound): letting it fall through
    /// into the hang bound would report "never got ready" as an ordinary timeout. TimeoutException on
    /// purpose -- <see cref="TryRunProbe"/>'s catch must not turn it into a quiet "unavailable".
    /// </summary>
    internal static void WaitUntilReady(Process process, string? readyFile, TimeSpan? readinessBound = null)
    {
        if (readyFile is null)
        {
            return;
        }

        // Stopwatch, not DateTime.UtcNow: a wall-clock adjustment mid-wait must not stretch or cut the
        // bound (R3-readiness-deadline-wall-clock).
        var bound = readinessBound ?? ReadinessBound;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < bound)
        {
            if (process.HasExited || IsReady(readyFile))
            {
                return;
            }

            Thread.Sleep(25);
        }

        // One last look after the bound (R2-ready-wait-post-loop-recheck-unexplained): the loop can
        // run out while sleeping, so a script that got ready inside that final 25 ms slice would
        // otherwise be killed and reported as never ready.
        if (process.HasExited || IsReady(readyFile))
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        throw new TimeoutException(
            $"The script never reported ready in {readyFile} within {bound.TotalSeconds:0.###} s; it was killed.");
    }

    private static bool IsReady(string readyFile) => TryReadPid(readyFile, out _);

    /// <summary>
    /// Reads a pid a test script wrote as <c>String(pid) + '\n'</c>. A reader can land between create
    /// and write (empty file) or in the middle of the write ("123" of "12345", still a valid int but the
    /// WRONG pid -- R3-isready-accepts-partial-pid-prefix), so a pid only counts once its newline
    /// terminator is on disk. Zero and negatives are rejected too: no real process has one.
    /// </summary>
    internal static bool TryReadPid(string pidFile, out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(pidFile))
            {
                return false;
            }

            var text = File.ReadAllText(pidFile);
            if (text.EndsWith('\n') && int.TryParse(text.AsSpan().TrimEnd(), out var parsed) && parsed > 0)
            {
                pid = parsed;
                return true;
            }

            return false;
        }
        // UnauthorizedAccessException too: a file still being created can deny the read for a moment
        // (R3-isready-catches-only-ioexception); either way it is simply "not ready yet".
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The opt-in and nothing more, wired the same way <c>DesktopFactAttributes</c> wires its own gates: a constructor-time <see cref="Skip"/> so xunit reports WHY, not just that a fact did not run.</summary>
internal sealed class RequiresNodeFactAttribute : FactAttribute
{
    public RequiresNodeFactAttribute()
    {
        if (NodeAvailability.SkipReasonIfMissing() is { } reason)
        {
            Skip = reason;
        }
    }
}
