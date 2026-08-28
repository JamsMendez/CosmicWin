namespace CosmicWin.Interop;

/// <summary>
/// Windows' own virtual desktops, addressed by POSITION.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not called a "workspace": <see cref="IWorkspace"/> already means "the set of
/// top-level windows on this system" in this codebase, and the two would be endlessly confused.
/// </para>
/// <para>
/// Positional, one-based, because that is what the shell actually models. <c>CreateDesktop</c>
/// appends at the END of the list and desktops have no durable name or number — so "desktop 3" can
/// only mean "the third one". The alternative, giving each desktop a name, needs
/// <c>SetDesktopName</c>, which takes an <c>HSTRING</c> this runtime cannot marshal without
/// hand-rolled interop. Positional also keeps <c>Win+Ctrl+Left/Right</c> agreeing with us.
/// </para>
/// </remarks>
public interface IVirtualDesktopService
{
    /// <summary>
    /// Whether this Windows build exposes the desktop layout CosmicWin expects. When
    /// <see langword="false"/>, every operation below is a no-op that returns <see langword="false"/>
    /// -- an unrecognised build degrades to "no virtual desktops", never to a wrong guess.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>How many desktops exist right now, or 0 when unsupported.</summary>
    int Count { get; }

    /// <summary>The one-based position of the desktop the user is on, or 0 when unsupported.</summary>
    int CurrentIndex { get; }

    /// <summary>
    /// The identity of the desktop being viewed, or <see cref="Guid.Empty"/> when unsupported.
    /// Position is what a chord names; identity is what a layout is filed under, because positions
    /// shift when a desktop is added or removed and a stored layout must not shift with them.
    /// </summary>
    Guid CurrentDesktopId { get; }

    /// <summary>
    /// Switches to the desktop at <paramref name="oneBasedIndex"/>, creating desktops until it
    /// exists. Returns <see langword="false"/> if unsupported, out of range, or the shell refused.
    /// </summary>
    bool TrySwitchTo(int oneBasedIndex);

    /// <summary>
    /// Sends <paramref name="windowHandle"/> to the desktop at <paramref name="oneBasedIndex"/>,
    /// creating desktops until it exists. Does NOT follow the window -- moving something away and
    /// being dragged along with it are separate intents.
    /// </summary>
    bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex);

    /// <summary>
    /// Closes the desktop the user is looking at, which is the only one that can be closed. Returns
    /// whether the shell was ASKED, not whether the desktop is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The current one because that is the whole vocabulary Windows offers: <c>Win+Ctrl+F4</c> takes
    /// no argument. The alternative would be the internal <c>RemoveDesktop</c> slot, which this
    /// codebase deliberately holds at a wrong signature -- deleting a desktop drags its surviving
    /// windows to a fallback, and that is not a decision to make through an interface Microsoft
    /// never promised.
    /// </para>
    /// <para>
    /// "Asked", not "done", and the distinction is not hedging. The shell is driven by synthetic
    /// input here and answers with an animation rather than a return value, so reading the desktop
    /// set back on the next line still names the desktop being closed. What the caller can rely on
    /// is that a <see langword="false"/> means nothing was asked at all -- an unsupported build, or
    /// the last remaining desktop, both refused here before any input is sent.
    /// </para>
    /// <para>
    /// Defaulted so the fakes that predate it need not answer a question they were never asked. A
    /// double that cannot close a desktop reporting that it did not is the truthful answer.
    /// </para>
    /// </remarks>
    bool TryCloseCurrentDesktop() => false;

    /// <summary>
    /// Why the last operation did not do what was asked, or <see langword="null"/> if it did.
    /// </summary>
    /// <remarks>
    /// Added after the first live run: every failure path here was swallowing its exception and
    /// returning a bare <c>false</c>, so "Alt+N does nothing" carried no information at all. A
    /// window manager that fails silently costs more to diagnose than it saves in noise.
    /// </remarks>
    string? LastError { get; }
}
