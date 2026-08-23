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

    public TrayIconHost(TrayMenuController controller)
    {
        _pauseItem = new ToolStripMenuItem(PauseLabel(controller.IsPaused));
        _pauseItem.Click += (_, _) => _pauseItem.Text = PauseLabel(controller.TogglePause());

        var reloadItem = new ToolStripMenuItem("Reload");
        reloadItem.Click += (_, _) => controller.Reload();

        var exitItem = new ToolStripMenuItem("Salir");
        exitItem.Click += (_, _) => controller.Exit();

        var menu = new ContextMenuStrip();
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
    }
}
