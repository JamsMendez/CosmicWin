namespace CosmicWin.Interop.Win32;

/// <summary>Info snapshot for one native window, as read by <see cref="INativeWindowSource"/>. Task 3.28's 5 new fields are trailing/default-valued so pre-existing positional call sites keep compiling.</summary>
internal readonly record struct NativeWindowInfo(
    string Title,
    Rectangle Bounds,
    string ClassName = "",
    string ProcessName = "",
    uint Style = 0u,
    uint ExStyle = 0u,
    bool IsOwned = false,

    /// <summary>
    /// Whether the window is SHOWN (<c>WS_VISIBLE</c>), as opposed to merely existing. The one
    /// signal that separates a window hidden into the notification area -- which
    /// <c>ShowWindow(SW_HIDE)</c> clears -- from one DWM has cloaked for being on another virtual
    /// desktop, which keeps it set. Both vanish from the enumeration; only the first is gone.
    /// </summary>
    bool IsVisible = true);

/// <summary>
/// The kind of change delivered by <see cref="INativeWindowSource.SubscribeWindowEvents"/>.
/// </summary>
internal enum NativeWindowEventKind
{
    Created,
    Destroyed,
    BoundsChanged,

    /// <summary>
    /// The user grabbed the window's title bar or a resize border (<c>EVENT_SYSTEM_MOVESIZESTART</c>).
    /// Every <see cref="BoundsChanged"/> between this and <see cref="MoveSizeEnded"/> is an
    /// intermediate frame of one gesture, not a settled position.
    /// </summary>
    MoveSizeStarted,

    /// <summary>The user let go (<c>EVENT_SYSTEM_MOVESIZEEND</c>).</summary>
    MoveSizeEnded,

    /// <summary>
    /// The window was hidden without being destroyed (<c>EVENT_OBJECT_HIDE</c>) -- what an
    /// application that lives in the notification area does when the user closes it.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT folded into <see cref="Destroyed"/> at the source. The two are different
    /// facts about the window -- one still has a live HWND and can come back through
    /// <see cref="Created"/>, the other cannot -- and a source that lied about which one happened
    /// would leave every consumer unable to tell them apart. They happen to have the same
    /// consequence for tracking today; that is the consumer's decision to make, not this enum's.
    /// </remarks>
    Hidden,
}

/// <summary>
/// Callback invoked by a native window event source. Delivered on whatever thread the
/// underlying hook uses — <see cref="Win32Workspace"/> does not assume a specific thread.
/// </summary>
internal delegate void NativeWindowEventCallback(NativeWindowEventKind kind, nint hwnd);

/// <summary>
/// Abstracts the native Win32 primitives <see cref="Win32Workspace"/> depends on for WT-1
/// (enumerate + <c>SetWinEventHook</c> tracking + polling fallback), so the tracking algorithm
/// — including the "hook misses an event, polling catches it" scenario — can be exercised
/// without a real desktop session or window. <see cref="Win32NativeWindowSource"/> is the real,
/// CsWin32-backed implementation; tests substitute a fake.
/// </summary>
internal interface INativeWindowSource
{
    /// <summary>Enumerates the currently open top-level windows worth tracking.</summary>
    IReadOnlyList<nint> EnumerateTopLevelWindows();

    /// <summary>
    /// Reads the current title/bounds for <paramref name="hwnd"/>. Returns <c>false</c> if the
    /// window no longer exists or is no longer trackable.
    /// </summary>
    bool TryGetWindowInfo(nint hwnd, out NativeWindowInfo info);

    /// <summary>
    /// Repositions/resizes the given window. Returns <c>false</c> (rather than throwing) if the
    /// native call fails — e.g. the target belongs to a higher-integrity/protected process.
    /// </summary>
    bool SetWindowPosition(nint hwnd, Rectangle bounds);

    /// <summary>
    /// Subscribes to create/destroy/move/resize notifications. Disposing the returned handle
    /// unsubscribes.
    /// </summary>
    IDisposable SubscribeWindowEvents(NativeWindowEventCallback callback);

    /// <summary>
    /// Brings the given window to the foreground, reporting WHICH rung of the escalation answered.
    /// Never throws: a refused activation is <see cref="ActivationOutcome.Failed"/>, an expired
    /// budget is <see cref="ActivationOutcome.TimedOut"/>.
    /// </summary>
    /// <remarks>
    /// Carries the outcome rather than a boolean because this is where the six endings are known.
    /// A boolean here could not be un-flattened by anything above it, however well instrumented.
    /// </remarks>
    ActivationOutcome Activate(nint hwnd);

    /// <summary>
    /// ASKS the given window to close, reporting only whether the ask was delivered. Never waits
    /// for an answer, and never forces one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WM_CLOSE</c> is a request an application is entitled to refuse -- an unsaved document
    /// puts up its own dialog and stays exactly where it is. So <c>true</c> here means the message
    /// was posted, never that a window went away. Nothing above this may treat it as a removal;
    /// the destroy/hide event path reports that when and if it actually happens.
    /// </para>
    /// <para>
    /// POSTED rather than sent. <c>SendMessage</c> blocks the calling thread until the target
    /// finishes handling it, and the target's answer may be a modal "save changes?" dialog that
    /// waits on a human -- an unbounded freeze on whichever thread asked, which for a chord is the
    /// one that also owns the focus border.
    /// </para>
    /// </remarks>
    bool TryClose(nint hwnd);

    /// <summary>
    /// Subscribes to every top-level window being SHOWN, with NO trackability filtering. Disposing
    /// the returned handle unsubscribes.
    /// </summary>
    /// <remarks>
    /// A separate registration from <see cref="SubscribeWindowEvents"/>, not a widening of it. That
    /// one gates its create arm on trackability — <c>!hasOwner &amp;&amp; !isCloaked</c> — which is
    /// the single function keeping tooltips, dropdowns, context menus and IME candidate lists out
    /// of the tiling pipeline, and a modal dialog is dropped by the same rule for having an owner.
    /// Loosening it to reach dialogs would let all of that through with them; a second, narrower
    /// hook leaves the tiling path byte-for-byte as it was.
    /// </remarks>
    IDisposable SubscribeShownWindows(Action<nint> callback);
}
