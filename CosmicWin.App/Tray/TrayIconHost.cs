using System.Drawing;
using System.Windows.Forms;

namespace CosmicWin.App.Tray;

/// <summary>
/// Task 3.16 (WU11): thin WinForms <see cref="NotifyIcon"/>/<see cref="ContextMenuStrip"/> wrapper
/// -- the sole owner of the actual tray icon/menu (spec TC-1). Deliberately holds no behavior of
/// its own beyond forwarding clicks to <see cref="TrayMenuController"/> and mapping the pause state
/// to its label via <see cref="PauseLabel"/>; everything else here needs a live Win32
/// desktop/taskbar/notification area to exercise meaningfully and is covered only by the manual
/// verification checklist recorded in apply-progress, the same shape as task 2.15's manual HA-3.
/// Constructed on the WPF UI thread -- its message pump rides the existing WPF Dispatcher loop, no
/// second message loop or extra thread needed.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;

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

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CosmicWin",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    /// <summary>Task 3.16: the one sliver of tray label logic extracted as a pure function and unit-tested (spec TC-1/TC-2's exact "Pausar"/"Reanudar" strings).</summary>
    public static string PauseLabel(bool isPaused) => isPaused ? "Reanudar" : "Pausar";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
