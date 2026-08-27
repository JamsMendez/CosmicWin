using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.Layout.Tests.Filters;

/// <summary>
/// What counts as a modal dialog — the window that gets centred and left floating instead of tiled.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. The same <c>SetWinEventHook</c> range that delivers a dialog also delivers
/// every tooltip, dropdown, context menu and IME candidate list on the desktop, and this predicate
/// is the only thing standing between them and a window being moved under the user's cursor.
/// </para>
/// <para>
/// The shape is the OWNED branch of <see cref="WindowFilters.IsAutoExcluded"/> read forwards
/// instead of backwards: that branch already decided such a window must never be tiled. Saying so
/// twice would let the two drift, so the last fact here pins them together — anything centred is
/// excluded from tiling by construction, never by coincidence.
/// </para>
/// </remarks>
public sealed class ModalDialogTests
{
    private const uint SysMenu = WindowStyleFlags.SystemMenu;

    private static WindowDescriptor Descriptor(
        uint style = SysMenu, uint exStyle = 0u, bool isOwned = true) =>
        new("#32770", "app", "Save changes?", exStyle, style, isOwned, 400, 200);

    [Fact]
    public void AnOwnedWindowWithACloseButtonAndNoMaximiseOrMinimise_IsAModalDialog()
    {
        Assert.True(WindowFilters.IsModalDialog(Descriptor()));
    }

    /// <summary>An unowned window is an ordinary application window, whatever else it looks like.</summary>
    [Fact]
    public void AnUnownedWindow_IsNotAModalDialog()
    {
        Assert.False(WindowFilters.IsModalDialog(Descriptor(isOwned: false)));
    }

    /// <summary>
    /// The one that would hurt most: a tooltip or dropdown is owned and has no maximise button
    /// either, and centring one would yank it out from under the pointer that summoned it.
    /// </summary>
    [Fact]
    public void AToolWindow_IsNotAModalDialog()
    {
        Assert.False(WindowFilters.IsModalDialog(Descriptor(exStyle: WindowStyleFlags.ExToolWindow)));
    }

    /// <summary>
    /// No system menu means no close button, which is what separates a dialog the user must answer
    /// from a menu, a tooltip or a candidate list that vanishes on its own.
    /// </summary>
    [Fact]
    public void AWindowWithNoSystemMenu_IsNotAModalDialog()
    {
        Assert.False(WindowFilters.IsModalDialog(Descriptor(style: 0u)));
    }

    [Theory]
    [InlineData(WindowStyleFlags.MaximizeBox)]
    [InlineData(WindowStyleFlags.MinimizeBox)]
    public void AnOwnedWindowThatCanBeMaximisedOrMinimised_IsATiledWindow_NotADialog(uint extra)
    {
        Assert.False(WindowFilters.IsModalDialog(Descriptor(style: SysMenu | extra)));
    }

    /// <summary>A minimized window is drawn nowhere; centring it would place a rectangle nobody sees.</summary>
    [Fact]
    public void AMinimizedWindow_IsNotAModalDialog()
    {
        Assert.False(WindowFilters.IsModalDialog(Descriptor(style: SysMenu | WindowStyleFlags.Minimized)));
    }

    /// <summary>
    /// The invariant that keeps the two predicates from drifting: a window this centres is one the
    /// tiling filter already refuses. A dialog that entered the tree would be given a share of the
    /// work area and then fought over by both paths.
    /// </summary>
    [Fact]
    public void EveryModalDialog_IsAlreadyExcludedFromTiling()
    {
        var dialog = Descriptor();

        Assert.True(WindowFilters.IsModalDialog(dialog));
        Assert.True(WindowFilters.IsAutoExcluded(dialog));
    }
}
