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
        menu.Items.Add(_pauseItem);
        menu.Items.Add(reloadItem);
        menu.Items.Add(exitItem);

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
