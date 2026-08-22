using CosmicWin.Interop.Win32.VirtualDesktops;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Spike diagnostic: reports whether THIS machine's Windows build exposes the virtual-desktop
/// vtable that <see cref="IVirtualDesktopManagerInternal"/> declares. Read-only — it never creates,
/// removes, switches or moves a desktop, so it is safe to run on a live session.
/// <para>
/// It asserts only that the probe reached a verdict, not which verdict. An unsupported build is a
/// legitimate answer here: the whole point of the probe is to say so out loud instead of calling
/// through a mismatched vtable.
/// </para>
/// </summary>
public sealed class VirtualDesktopProbeDiagnostic(ITestOutputHelper output)
{
    [RequiresDesktopFact]
    public void ReportWhetherThisBuildExposesTheDeclaredVirtualDesktopVTable()
    {
        output.WriteLine($"OS               : {Environment.OSVersion.Version}");

        var result = VirtualDesktopProbe.Run();

        output.WriteLine($"supported        : {result.Supported}");
        output.WriteLine($"GetCount         : {result.Count}");
        output.WriteLine($"current desktop  : {result.CurrentDesktopId}");
        output.WriteLine($"enumerated ({result.EnumeratedIds.Count,2})   : {string.Join(", ", result.EnumeratedIds)}");
        output.WriteLine($"failure          : {result.Failure ?? "(none)"}");

        Assert.NotNull(result);
    }
}
