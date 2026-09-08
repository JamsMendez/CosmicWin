namespace CosmicWin.Interop.Win32;

/// <summary>
/// The thin PUBLIC seam onto <see cref="Win32NativeWindowSource.EnumerateTopLevelWindows"/>, for a
/// composition site outside this assembly that needs the desktop's real Z-ORDER.
/// </summary>
/// <remarks>
/// <para>
/// Deviation from the untiled-focus design, worth recording here: that design asked for
/// <c>AppComposition.WireProduction</c> to hoist and call a <see cref="Win32NativeWindowSource"/>
/// instance directly. It cannot -- the class is deliberately <c>internal</c>
/// (<c>InternalsVisibleTo</c> reaches only the two test projects), and two of its OTHER public
/// members return/accept <c>NativeWindowInfo</c>/<c>NativeWindowEventCallback</c>, which are
/// themselves internal. Widening the class to reach <c>CosmicWin.App</c> would force widening
/// those two along with it, for a change that needs exactly ONE of its members. This wrapper hoists
/// the same one instance the design asked for and exposes only the enumeration, which is all the
/// untiled focus wiring actually reads.
/// </para>
/// <para>
/// The pattern already used for every other Win32 collaborator <c>AppComposition</c> composes
/// against -- <see cref="Win32Workspace"/>, <see cref="Win32ForegroundWindowSource"/>, <see
/// cref="Win32DisplayManager"/> -- is a small public class over the internal machinery; this is
/// that same shape, sized to one method instead of a whole interface because nothing else here is
/// needed yet.
/// </para>
/// </remarks>
public sealed class Win32ZOrderSource
{
    private readonly Win32NativeWindowSource _native = new();

    /// <summary>
    /// Every top-level window worth tracking, in Z-ORDER, TOPMOST FIRST -- see
    /// <see cref="Win32NativeWindowSource.EnumerateTopLevelWindows"/>, backed by
    /// <c>EnumWindows</c>, which Windows documents as returning handles in that order.
    /// </summary>
    public IReadOnlyList<nint> EnumerateTopLevelWindows() => _native.EnumerateTopLevelWindows();
}
