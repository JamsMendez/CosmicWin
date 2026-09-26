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

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        // Same "a gate deciding whether to SKIP must never crash test discovery" reasoning as
        // DesktopGate.IsRaisedDesktopLayout: node missing from PATH surfaces as Win32Exception on
        // some machines, and a process that never starts still leaves WaitForExit valid to call.
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
