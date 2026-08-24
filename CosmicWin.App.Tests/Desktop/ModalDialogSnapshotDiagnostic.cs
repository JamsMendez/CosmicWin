using System.Diagnostics;
using System.Runtime.InteropServices;
using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

using CosmicWin.Interop.Tests.Win32;
using Xunit.Abstractions;

namespace CosmicWin.App.Tests.Desktop;

/// <summary>
/// Diagnostic, not a behavioural test. Answers one question with measured data: what IS the window
/// the user calls a modal, and which of CosmicWin's predicates actually match it?
/// </summary>
/// <remarks>
/// <para>
/// Written after two live reports that contradict each other under any single explanation. The move
/// chord MOVES the dialog, which normally means it is a leaf of the tree -- but the windows behind
/// it never reflow, and a tree that gained a leaf would have re-divided the space among all three.
/// So it is moved by something, and it is not in the tree. Both facts cannot be guessed at further;
/// this measures them.
/// </para>
/// <para>
/// Watches rather than snapshots, because the interesting window does not exist yet when the run
/// starts: it reports every visible titled window that APPEARS while it watches, which is the
/// dialog at the moment of birth.
/// </para>
/// <para>
/// Read-only throughout -- it enumerates and reads, and never moves, activates, spawns or closes
/// anything. That is why it does NOT use <c>RequiresDesktopFact</c>, whose second gate refuses to
/// run while CosmicWin.App is live: that gate exists for facts that SPAWN windows a live window
/// manager would tile out from under them. This one has nothing to protect, and the app being live
/// is the very condition being diagnosed.
/// </para>
/// </remarks>
public sealed class ModalDialogSnapshotDiagnostic(ITestOutputHelper output)
{
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    /// <summary>
    /// How long to keep watching after the snapshot, from
    /// <c>COSMICWIN_DIAG_WATCH_SECONDS</c>. ZERO by default, which makes this a plain snapshot.
    /// </summary>
    /// <remarks>
    /// Watching was the original design and it failed twice for the same reason: it puts the person
    /// running it under a stopwatch, and reading the instruction took longer than the window. A
    /// snapshot has no such race -- open the dialog first, take the picture second. Watching is kept
    /// only for the question a snapshot genuinely cannot answer: what MOVES when a chord fires.
    /// </remarks>
    private static TimeSpan WatchFor =>
        int.TryParse(Environment.GetEnvironmentVariable("COSMICWIN_DIAG_WATCH_SECONDS"), out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    [Fact]
    public void ReportEveryWindowThatAppearsWhileWatching()
    {
        // Gated in the body rather than by an attribute so this stays a plain [Fact] with no
        // running-app check. A silent pass would be worse than useless for a diagnostic, so the
        // reason is written where its output is read.
        if (DesktopGate.OptInSkipReason() is { } notRun)
        {
            output.WriteLine($"NOT RUN. {notRun}");
            return;
        }

        var source = new Win32NativeWindowSource();

        output.WriteLine("owner  = has a Win32 owner (GW_OWNER). Decides everything below it.");
        output.WriteLine("track  = IsTrackable    -> reaches the tiling pipeline, can become a tree leaf");
        output.WriteLine("autoex = IsAutoExcluded -> refused a tile even when tracked");
        output.WriteLine("modal  = IsModalDialog  -> would be centred by the dialog path");
        output.WriteLine(string.Empty);

        var snapshot = VisibleTitledWindows(source);
        var seen = new HashSet<nint>(snapshot.Select(entry => entry.Handle));
        output.WriteLine($"SNAPSHOT: {snapshot.Count} visible titled window(s).");
        foreach (var entry in snapshot)
        {
            output.WriteLine("  " + Describe(entry));
        }

        if (WatchFor <= TimeSpan.Zero)
        {
            output.WriteLine(string.Empty);
            output.WriteLine("Snapshot only. Set COSMICWIN_DIAG_WATCH_SECONDS to also watch for changes.");
            return;
        }

        output.WriteLine(string.Empty);
        output.WriteLine($"WATCHING for {WatchFor.TotalSeconds:0} more second(s).");
        output.WriteLine(string.Empty);

        // Rectangles as well as membership, because the open question is not only WHAT the dialog
        // is but what moves it. If a chord moves the dialog and the windows behind it never change,
        // it is not a leaf of the tree -- a tree that gained a leaf re-divides the space among all
        // of them, and every sibling's rectangle would move in the same tick.
        var lastBounds = new Dictionary<nint, Rectangle>();
        foreach (var entry in VisibleTitledWindows(source))
        {
            lastBounds[entry.Handle] = entry.Info.Bounds;
        }

        var appeared = 0;
        var moves = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < WatchFor)
        {
            foreach (var entry in VisibleTitledWindows(source))
            {
                if (seen.Add(entry.Handle))
                {
                    appeared++;
                    output.WriteLine($"APPEARED (+{clock.Elapsed.TotalSeconds:00.0}s) " + Describe(entry));
                }
                else if (lastBounds.TryGetValue(entry.Handle, out var before) && before != entry.Info.Bounds)
                {
                    moves++;
                    output.WriteLine(
                        $"MOVED    (+{clock.Elapsed.TotalSeconds:00.0}s) 0x{entry.Handle:X8} " +
                        $"[L={before.Left} T={before.Top} W={before.Width} H={before.Height}] -> " +
                        $"[L={entry.Info.Bounds.Left} T={entry.Info.Bounds.Top} " +
                        $"W={entry.Info.Bounds.Width} H={entry.Info.Bounds.Height}] " +
                        $"proc={entry.Info.ProcessName} title={entry.Info.Title}");
                }

                lastBounds[entry.Handle] = entry.Info.Bounds;
            }

            Thread.Sleep(200);
        }

        output.WriteLine(string.Empty);
        output.WriteLine($"{appeared} window(s) appeared and {moves} move(s) observed while watching.");
    }

    private readonly record struct Entry(nint Handle, NativeWindowInfo Info);

    /// <summary>
    /// Enumerates RAW, not through <c>EnumerateTopLevelWindows</c>: that one already applies
    /// <c>IsTrackable</c>, which would hide the very windows in question. Titleless windows are
    /// shell scaffolding and would drown every interesting line.
    /// </summary>
    private static List<Entry> VisibleTitledWindows(Win32NativeWindowSource source)
    {
        var handles = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                handles.Add(hwnd);
            }

            return true;
        }, 0);

        var entries = new List<Entry>();
        foreach (var hwnd in handles)
        {
            if (source.TryGetWindowInfo(hwnd, out var info) && !string.IsNullOrWhiteSpace(info.Title))
            {
                entries.Add(new Entry(hwnd, info));
            }
        }

        return entries;
    }

    private static string Describe(Entry entry)
    {
        var info = entry.Info;
        var descriptor = new WindowDescriptor(
            info.ClassName, info.ProcessName, info.Title, info.ExStyle, info.Style, info.IsOwned);

        return
            $"0x{entry.Handle:X8} owner={Yes(info.IsOwned)} " +
            $"track={Yes(Win32NativeWindowSource.IsTrackable(info.IsOwned, isCloaked: false))} " +
            $"autoex={Yes(WindowFilters.IsAutoExcluded(descriptor))} " +
            $"modal={Yes(WindowFilters.IsModalDialog(descriptor))} " +
            $"| {Styles(info.Style, info.ExStyle)} " +
            $"| proc={info.ProcessName} class={info.ClassName} " +
            $"rect=[L={info.Bounds.Left} T={info.Bounds.Top} W={info.Bounds.Width} H={info.Bounds.Height}] " +
            $"title={info.Title}";
    }

    private static string Yes(bool value) => value ? "YES" : "no ";

    /// <summary>Only the bits a predicate here reads. A raw hex mask answers nothing on its own.</summary>
    private static string Styles(uint style, uint exStyle)
    {
        var bits = new List<string>();
        if ((style & WindowStyleFlags.SystemMenu) != 0) bits.Add("SYSMENU");
        if ((style & WindowStyleFlags.MaximizeBox) != 0) bits.Add("MAXBOX");
        if ((style & WindowStyleFlags.MinimizeBox) != 0) bits.Add("MINBOX");
        if ((style & WindowStyleFlags.Minimized) != 0) bits.Add("MINIMIZED");
        if ((exStyle & WindowStyleFlags.ExToolWindow) != 0) bits.Add("TOOLWINDOW");

        return bits.Count == 0 ? "(none)" : string.Join("+", bits);
    }
}
