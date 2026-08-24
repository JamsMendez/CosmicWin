using CosmicWin.Interop.Win32;
using CosmicWin.Layout.Filters;
using Xunit.Abstractions;

namespace CosmicWin.App.Tests.Desktop;

/// <summary>
/// TEMPORARY diagnostic, not a behavioural test. Read-only: enumerates the real desktop's
/// top-level windows and reports which ones CosmicWin's own filter chain would admit into the
/// tree, so a "the tiling only used half the screen" report can be answered with the actual
/// admitted set instead of a guess. Never moves, activates or closes anything.
/// </summary>
public sealed class DesktopSnapshotDiagnostic(ITestOutputHelper output)
{
    [RequiresDesktopSessionFact]
    public void ReportWindowsTheFilterChainAdmits()
    {
        var source = new Win32NativeWindowSource();
        var handles = source.EnumerateTopLevelWindows();
        output.WriteLine($"EnumerateTopLevelWindows returned {handles.Count} handle(s).");

        var admitted = 0;
        foreach (var hwnd in handles)
        {
            if (!source.TryGetWindowInfo(hwnd, out var info))
            {
                output.WriteLine($"  0x{hwnd:X} UNREADABLE");
                continue;
            }

            var window = new Win32Window(
                hwnd, info.Title, info.Bounds, source,
                info.ClassName, info.ProcessName, info.Style, info.ExStyle, info.IsOwned);

            var excluded = WorkspaceSessionAdapter.IsExcluded(window, ExceptionList.Empty);
            if (!excluded)
            {
                admitted++;
            }

            output.WriteLine(
                $"  {(excluded ? "skip   " : "ADMIT  ")} 0x{hwnd:X} " +
                $"proc={info.ProcessName,-24} class={info.ClassName,-28} " +
                $"rect=[L={info.Bounds.Left} T={info.Bounds.Top} W={info.Bounds.Width} H={info.Bounds.Height}] " +
                $"title={info.Title}");
        }

        output.WriteLine($"ADMITTED TOTAL: {admitted}");
    }
}
