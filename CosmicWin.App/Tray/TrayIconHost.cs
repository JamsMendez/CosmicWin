using System.Drawing;
using System.Windows.Forms;

namespace CosmicWin.App.Tray;

/// <summary>
/// Thin WinForms <see cref="NotifyIcon"/>/<see cref="ContextMenuStrip"/> wrapper
/// the sole owner of the actual tray icon/menu. Deliberately holds no behavior of
/// its own beyond forwarding clicks to <see cref="TrayMenuController"/> and mapping the pause state
/// to its label via <see cref="PauseLabel"/>; everything else here needs a live Win32
/// desktop/taskbar/notification area to exercise meaningfully, so it is verified by hand rather
/// than pretended to be covered.
/// Constructed on the WPF UI thread -- its message pump rides the existing WPF Dispatcher loop, no
/// second message loop or extra thread needed.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Icon? _ownedIcon;
    private readonly ContextMenuStrip _menu;

    public TrayIconHost(TrayMenuController controller)
    {
        _pauseItem = new ToolStripMenuItem(PauseLabel(controller.IsPaused))
        {
            Image = TrayGlyphs.Render(TrayGlyphs.ForPause(controller.IsPaused)),
        };
        _pauseItem.Click += (_, _) =>
        {
            var isPaused = controller.TogglePause();
            _pauseItem.Text = PauseLabel(isPaused);

            // The icon flips WITH the label, never after it. They are two renderings of one
            // decision, and "Reanudar" beside a pause icon reads worse than no icon at all.
            // ToolStripMenuItem does not own its Image, so the outgoing one is released here.
            var previous = _pauseItem.Image;
            _pauseItem.Image = TrayGlyphs.Render(TrayGlyphs.ForPause(isPaused));
            previous?.Dispose();
        };

        // A TICK rather than a glyph, and deliberately so: a ToolStripMenuItem that carries an
        // Image renders it INSTEAD of its check mark, so a state this item exists to show would
        // have been hidden by decorating it. CheckOnClick is left off -- the controller owns the
        // state, and letting WinForms flip the tick on its own would give it a second owner.
        var borderItem = new ToolStripMenuItem("Borde de foco")
        {
            Checked = controller.IsFocusBorderEnabled,
        };
        borderItem.Click += (_, _) => borderItem.Checked = controller.ToggleFocusBorder();

        // A SUBMENU rather than a bare item, so the accent has a way back. A colour dialog cannot
        // express "no colour of my own" -- it always answers with one -- and a picker that could
        // only ever move away from the system accent would be a one-way door out of the default.
        var colorItem = new ToolStripMenuItem("Color del borde");
        var pickColorItem = new ToolStripMenuItem("Elegir...");
        pickColorItem.Click += (_, _) => PickBorderColor(controller);
        var accentItem = new ToolStripMenuItem("Acento del sistema");
        accentItem.Click += (_, _) => controller.SetBorderColor(null);
        colorItem.DropDownItems.Add(pickColorItem);
        colorItem.DropDownItems.Add(accentItem);

        var reloadItem = new ToolStripMenuItem("Reload")
        {
            Image = TrayGlyphs.Render(TrayGlyphs.Refresh),
        };
        reloadItem.Click += (_, _) => controller.Reload();

        var exitItem = new ToolStripMenuItem("Salir")
        {
            Image = TrayGlyphs.Render(TrayGlyphs.Exit),
        };
        exitItem.Click += (_, _) => controller.Exit();

        // Kept so Dispose can release the images: ToolStripMenuItem never owns the Image handed
        // to it, and the menu outlives every local here.
        _menu = new ContextMenuStrip();
        var menu = _menu;

        // Added by WALKING MenuOrder rather than in source order, so that list is the real decision
        // about how this menu reads and not a comment describing one. Reordering the menu is a change
        // to MenuOrder, which a fact already pins.
        var items = new Dictionary<TrayMenuEntry, ToolStripMenuItem>
        {
            [TrayMenuEntry.FocusBorder] = borderItem,
            [TrayMenuEntry.BorderColor] = colorItem,
            [TrayMenuEntry.Pause] = _pauseItem,
            [TrayMenuEntry.Reload] = reloadItem,
            [TrayMenuEntry.Exit] = exitItem,
        };

        foreach (var entry in MenuOrder)
        {
            menu.Items.Add(items[entry]);
        }

        _ownedIcon = LoadTrayIcon();
        _icon = new NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "CosmicWin",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    /// <summary>
    /// The application icon at the notification area's own size.
    /// </summary>
    /// <remarks>
    /// Asking the embedded multi-size <c>.ico</c> for <see cref="SystemInformation.SmallIconSize"/>
    /// lets Windows pick the frame drawn at this display's DPI, instead of scaling one size to fit
    /// and smearing a deliberately pixelated image. Returns <see langword="null"/> rather than
    /// throwing if the resource is missing: a tray icon is not worth failing startup over, and the
    /// caller falls back to the system icon.
    /// </remarks>
    private static Icon? LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(TrayIconHost).Assembly
                .GetManifestResourceStream("CosmicWin.App.cosmicwin.ico");
            return stream is null ? null : new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>The one sliver of tray label logic extracted as a pure function and unit-tested.</summary>
    public static string PauseLabel(bool isPaused) => isPaused ? "Reanudar" : "Pausar";

    /// <summary>
    /// The order the items appear in: the border and its colour first, then the pause, then the two
    /// that end something.
    /// </summary>
    /// <remarks>
    /// The constructor ADDS its items by walking this list, which is what makes it the decision
    /// rather than a description of one. Kept public so a fact can assert the real order instead of
    /// its own copy of it -- everything else in this class needs a live notification area and is
    /// verified by hand.
    /// </remarks>
    public static IReadOnlyList<TrayMenuEntry> MenuOrder { get; } =
    [
        TrayMenuEntry.FocusBorder,
        TrayMenuEntry.BorderColor,
        TrayMenuEntry.Pause,
        TrayMenuEntry.Reload,
        TrayMenuEntry.Exit,
    ];

    /// <summary>
    /// Opens Windows' own colour dialog, seeded with the colour the border is wearing right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancelled, it changes NOTHING -- not even back to the accent. A dialog dismissed is a user
    /// who decided not to decide, and reading it as a choice is the classic way a picker eats a
    /// setting somebody liked.
    /// </para>
    /// <para>
    /// Seeded with the system highlight when the border is following the accent, because the dialog
    /// has to open on some colour and the one on screen is the least surprising one. The alpha byte
    /// is dropped on the way out: the border is opaque, and a stored alpha would be a value nothing
    /// reads and everything has to keep carrying.
    /// </para>
    /// </remarks>
    private static void PickBorderColor(TrayMenuController controller)
    {
        using var dialog = new ColorDialog
        {
            Color = controller.BorderColor is { } rgb
                ? Color.FromArgb((int)(0xFF000000u | rgb))
                : SystemColors.Highlight,

            // The full picker straight away. Collapsed, it offers 48 basic colours and hides the
            // custom one behind a button, which is the half of the dialog this menu item exists for.
            FullOpen = true,
            AnyColor = true,
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            controller.SetBorderColor((uint)(dialog.Color.ToArgb() & 0x00FFFFFF));
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();

        // NotifyIcon does not own the Icon it was handed, so the one loaded here has to be released
        // here. SystemIcons.Application is a shared system handle and must NOT be disposed, which is
        // why only the icon this class created is tracked.
        _ownedIcon?.Dispose();

        // Same rule one level down: a menu item does not own its Image either.
        foreach (var item in _menu.Items.OfType<ToolStripMenuItem>())
        {
            item.Image?.Dispose();
        }

        _menu.Dispose();
    }
}
