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

    private static bool TryRunNodeVersion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            // Read concurrently, not that a single short version line could ever fill a pipe buffer,
            // but so this probe never becomes a second place that reintroduces the sequential-read
            // deadlock risk the harness runner (AlertLayerLayoutNodeTests.RunNode) was fixed for.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ProbeTimeoutMilliseconds))
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
