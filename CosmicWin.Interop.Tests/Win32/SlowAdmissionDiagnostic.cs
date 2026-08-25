using System.Diagnostics;
using CosmicWin.Interop.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// TEMPORARY diagnostic, not a behavioural test. Reported: Brave and the Windows Settings app take
/// roughly 1 to 1.5 seconds to join the tree, where an ordinary window joins at once.
/// </summary>
/// <remarks>
/// <para>
/// Measures the gap between the moment Windows CREATES such a window and the moment
/// <c>Win32NativeWindowSource</c> is willing to admit it, and prints every window event that
/// arrived in between -- including the ones OUTSIDE the range the production hook subscribes
/// (0x8000..0x800B), which is where the suspicion lies: a window that is born CLOAKED is refused by
/// <c>IsTrackable</c>, and <c>EVENT_OBJECT_UNCLOAKED</c> (0x8018) is past the end of that range, so
/// nothing looks again until the 2-second reconciliation tick.
/// </para>
/// <para>
/// The admission oracle is <c>EnumerateTopLevelWindows</c> rather than a hand-rolled copy of the
/// gate, so what is measured is the real decision the production path makes, not a restatement of
/// it. It CLOSES Settings twice -- once to force a cold launch, once to put the desktop back --
/// and touches no other window.
/// </para>
/// <para>
/// In the serialised desktop collection like every other fact that touches the real desktop.
/// A class with NO <c>[Collection]</c> gets its own implicit one, which xunit runs in PARALLEL
/// with <c>RealDesktop</c> -- so this raced the very facts that collection exists to serialise.
/// Read-only is not an exemption: a reader that runs while another fact is moving windows or
/// switching desktops reports a desktop nobody ever had.
/// </para>
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class SlowAdmissionDiagnostic(ITestOutputHelper output)
{
    /// <summary>
    /// Its own opt-in, on top of the shared desktop one, because this CLOSES the user's Settings
    /// window (see <see cref="CloseSettings"/>) rather than only reading the desktop.
    /// </summary>
    /// <remarks>
    /// The same lesson <c>WindowBorderColourSpike</c> taught the hard way in this session: a fact
    /// that mutates the machine must not ride the gate meant for facts that observe it, or an
    /// ordinary <c>dotnet test</c> starts doing things nobody asked for.
    /// <para>
    /// Declared by <see cref="MeasuresAdmissionFactAttribute"/>, which is what actually skips on it.
    /// Named here so the empty-run message and the gate cannot drift apart.
    /// </para>
    /// </remarks>
    private const string RunVariable = MeasuresAdmissionFactAttribute.Variable;

    /// <summary>How long the launch is watched. Named once so the wait and the empty-run message cannot drift.</summary>
    private static readonly TimeSpan PumpWindow = TimeSpan.FromSeconds(8);

    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectUncloaked = 0x8018;

    private static readonly Dictionary<uint, string> EventNames = new()
    {
        [0x8000] = "CREATE",
        [0x8001] = "DESTROY",
        [0x8002] = "SHOW",
        [0x8003] = "HIDE",
        [0x8004] = "REORDER",
        [0x8005] = "FOCUS",
        [0x8006] = "SELECTION",
        [0x800B] = "LOCATIONCHANGE",
        [0x800C] = "NAMECHANGE",
        [0x8017] = "CLOAKED",
        [0x8018] = "UNCLOAKED",
    };

    [MeasuresAdmissionFact]
    public void ReportHowLongAUwpWindowTakesToBecomeTrackable()
    {
        // Settings must be CLOSED first or there is nothing to measure: the protocol handler simply
        // activates an existing window, which was already in the enumeration before the clock
        // started. Measured the hard way -- the first run of this reported no new window at all.
        CloseSettings();

        var source = new Win32NativeWindowSource();
        // dwmsEventTime, NOT the time the callback ran. An out-of-context hook only delivers while
        // this thread retrieves messages, and enumerating every top-level window between pumps
        // starves that badly enough that a whole launch arrives in one batch wearing one timestamp
        // -- measured on the first version of this, which reported nine events in the same
        // millisecond. dwmsEventTime is stamped by the OS when the event happened and shares its
        // clock with Environment.TickCount, so the two below are directly comparable.
        var events = new List<(uint At, uint Kind, nint Hwnd)>();
        // Each admitted window PAIRED with the info read at the moment it was admitted. Not a bare
        // set of handles: the report runs after the finally has closed Settings, so the handle
        // alone is not enough to say what the window WAS.
        var admitted = new List<(nint Hwnd, NativeWindowInfo Info)>();
        var created = new Dictionary<nint, uint>();
        var known = new HashSet<nint>(source.EnumerateTopLevelWindows());

        // The PRODUCTION hook, running beside the wide one. This is the decisive line: whether the
        // arm that actually feeds the tree ever reports this window at all, and when.
        using var production = source.SubscribeWindowEvents((kind, hwnd) =>
        {
            if (kind == NativeWindowEventKind.Created)
            {
                created.TryAdd(hwnd, (uint)Environment.TickCount);
            }
        });

        // The WHOLE object range, deliberately wider than production's. The point is to see what
        // arrives outside the subscribed window, which a hook shaped like production's cannot show.
        var thunk = new WINEVENTPROC((_, eventType, hwnd, idObject, idChild, _, eventTime) =>
        {
            if (idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW && idChild == 0 && hwnd != HWND.Null)
            {
                events.Add((eventTime, eventType, hwnd));
            }
        });

        var hook = PInvoke.SetWinEventHook(
            EventObjectCreate, EventObjectUncloaked, HMODULE.Null, thunk,
            idProcess: 0, idThread: 0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        Assert.False(hook.IsNull, "SetWinEventHook failed.");

        // `thunk` is a LOCAL, and its last syntactic use is the call above -- so from the JIT's point
        // of view it is collectable from that instant, while native code holds a function pointer to
        // it and will keep calling it for the next eight seconds. Exactly the hazard
        // WinEventHookSubscription is documented to guard against by holding its own delegate in a
        // field; a diagnostic does not get to ignore the rule the production class spells out.
        // GC.KeepAlive below, after the hook is torn down, is what makes that guarantee here.

        try
        {
            // A protocol handler is dispatched BY THE SHELL, so there is no process to hand back:
            // null is the normal answer here, not a failure.
            Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true })?.Dispose();

            // Pumped TIGHTLY, with no enumeration in the loop. Enumerating every top-level window
            // costs an EnumWindows plus a DWM read per window, and doing that between drains
            // starved the queue badly enough that the production callback's delivery time measured
            // the harness rather than the system -- it read ~390ms whether or not the uncloak arm
            // existed. Admission is collected once, afterwards.
            MessagePump.For(PumpWindow);

            foreach (var hwnd in source.EnumerateTopLevelWindows())
            {
                if (known.Add(hwnd))
                {
                    // Read the window HERE, while it is still alive. The finally below closes
                    // Settings and the report loop runs after it, so reading there answers about a
                    // dead window: TryGetWindowInfo returns false and every identifying field prints
                    // empty. The timeline survived that because it is built from captured events,
                    // which is exactly why the breakage looked cosmetic instead of total -- a
                    // timeline for a bare hwnd, with no idea which application it belongs to.
                    source.TryGetWindowInfo(hwnd, out var info);
                    admitted.Add((hwnd, info));
                }
            }
        }
        finally
        {
            PInvoke.UnhookWinEvent(hook);

            // The delegate must outlive the hook, so this is the earliest safe point.
            GC.KeepAlive(thunk);

            // Clear up after the launch. Deliberately NOT called a restore: a Settings window the
            // user had open was killed at the start and is not reopened, so this only guarantees the
            // harness does not LEAVE one behind. That is the most a cold-launch measurement can
            // offer, and leaving a fresh window open would be the same mutate-without-restore shape
            // this batch gated WindowBorderColourSpike for.
            //
            // Suppressed HERE and nowhere else: an exception thrown from a finally REPLACES the one
            // already in flight, so a failure to tidy up would erase the failure worth reading.
            try
            {
                CloseSettings();
            }
            catch
            {
                // Tidying up is never worth losing the real exception over.
            }
        }

        // Only the windows that actually arrived during the run: the desktop is full of traffic that
        // has nothing to do with the launch, and reporting all of it would bury the answer.
        foreach (var (hwnd, info) in admitted)
        {
            var mine = events.Where(e => e.Hwnd == hwnd).OrderBy(e => e.At).ToList();
            if (mine.Count == 0)
            {
                continue;
            }

            var born = mine[0].At;

            output.WriteLine(string.Empty);
            output.WriteLine($"hwnd=0x{hwnd:X} class={info.ClassName} proc={info.ProcessName} title={info.Title}");
            foreach (var (at, kind, _) in mine)
            {
                var name = EventNames.TryGetValue(kind, out var known2) ? known2 : $"0x{kind:X}";
                var subscribed = kind is >= 0x8000 and <= 0x800B ? "subscribed" : "OUTSIDE RANGE";
                output.WriteLine($"    +{at - born,5}ms  {name,-15} {subscribed}");
            }

            output.WriteLine(
                created.TryGetValue(hwnd, out var reportedAt)
                    ? $"    +{reportedAt - born,5}ms  PRODUCTION HOOK reported Created (delivery time)"
                    : "    ------  PRODUCTION HOOK NEVER reported Created");
        }

        // Deliberately NOT an assertion. This measures how long admission takes, so "slower than the
        // pump window" is the very observation it exists to make -- failing there would delete the
        // finding and report a broken harness instead. An empty run says so and stays green.
        if (admitted.Count == 0)
        {
            output.WriteLine(
                $"No new window became trackable within {PumpWindow.TotalSeconds:F0}s. That is a " +
                "RESULT, not a harness failure: raise the window, or admission is genuinely slower.");
        }
    }

    /// <remarks>
    /// Throws what it cannot handle. The only exception this swallows is a per-process one, because
    /// a single stubborn instance must not stop the rest from closing -- everything else, notably a
    /// failing <c>GetProcessesByName</c>, propagates so the caller fails loudly.
    /// <para>
    /// The finally-block caller is the one that cannot afford a throw, and it suppresses at ITS OWN
    /// call site rather than here. Suppressing inside this method would have covered the
    /// top-of-method call too, which is not in a finally and has nothing to be protected from --
    /// silently widening an error swallow past the hazard that justified it.
    /// </para>
    /// </remarks>
    private static void CloseSettings()
    {
        foreach (var running in Process.GetProcessesByName("SystemSettings"))
        {
            using (running)
            {
                try
                {
                    running.Kill();
                    running.WaitForExit(5000);
                }
                catch
                {
                    // Per process, so one stubborn instance does not stop the rest from closing. An
                    // unkillable Settings only means this run measures nothing, which the empty-run
                    // message at the end of the method reports.
                }
            }
        }

        Thread.Sleep(1500);
    }
}
