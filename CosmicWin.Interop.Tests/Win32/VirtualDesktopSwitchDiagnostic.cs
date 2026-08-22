using CosmicWin.Interop.Win32.VirtualDesktops;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Diagnostic for the live report that <c>Alt+N</c> creates desktops but never switches to them.
/// Creation and switching sit in the same interface, so "one works and the other does not" narrows
/// the cause sharply: it exercises the switch directly here, on a plain test thread, and then again
/// from a thread-pool thread — which is where <c>ActionDispatcher</c> actually runs the executor.
/// <para>
/// That distinction has bitten this project before: the first AttachThreadInput fix looked correct
/// and never worked, because it ran on a thread-pool thread that has no input queue to attach to.
/// </para>
/// </summary>
[Collection(RealDesktopCollection.Name)]
public sealed class VirtualDesktopSwitchDiagnostic(ITestOutputHelper output)
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    [RequiresDesktopFact]
    public void ReportWhetherSwitchingActuallyMovesTheUser()
    {
        var service = new Win32VirtualDesktopService();
        output.WriteLine($"supported : {service.IsSupported}");
        output.WriteLine($"count     : {service.Count}");
        output.WriteLine($"current   : {service.CurrentIndex}");

        if (!service.IsSupported)
        {
            output.WriteLine("Unsupported build -- nothing further to measure.");
            return;
        }

        var startedOn = service.CurrentIndex;
        var startedWith = service.Count;

        try
        {
            // 1) The caller's own thread.
            var direct = service.TrySwitchTo(2);
            Thread.Sleep(Settle);
            output.WriteLine($"direct    : returned={direct} landedOn={service.CurrentIndex} (wanted 2)");

            service.TrySwitchTo(startedOn);
            Thread.Sleep(Settle);

            // 2) A thread-pool thread, the way ActionDispatcher reaches the executor in production.
            var fromPool = Task.Run(() =>
            {
                var ok = new Win32VirtualDesktopService().TrySwitchTo(2);
                Thread.Sleep(Settle);
                return (ok, new Win32VirtualDesktopService().CurrentIndex);
            }).GetAwaiter().GetResult();

            output.WriteLine($"pool      : returned={fromPool.ok} landedOn={fromPool.Item2} (wanted 2)");
        }
        finally
        {
            service.TrySwitchTo(startedOn);
            Thread.Sleep(Settle);
            output.WriteLine($"restored  : current={service.CurrentIndex} count={service.Count} (started on {startedOn} of {startedWith})");
        }
    }
}
