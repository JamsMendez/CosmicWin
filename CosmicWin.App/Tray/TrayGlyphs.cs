using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CosmicWin.App.Tray;

/// <summary>
/// The icons the tray menu wears, and how they are drawn.
/// </summary>
/// <remarks>
/// <para>
/// .NET ships no icon set that covers these. <see cref="SystemIcons"/> stops at Error, Warning,
/// Shield and friends -- there is no pause, no refresh, no exit. Windows itself has them, as a
/// FONT: <c>Segoe Fluent Icons</c> on Windows 11, <c>Segoe MDL2 Assets</c> on 10 and as the
/// fallback here.
/// </para>
/// <para>
/// A font is the right shape for this rather than a bitmap resource. It is already installed, it is
/// what the shell's own menus draw from, and it renders at whatever size the notification area asks
/// for at this display's DPI -- where a single embedded bitmap would be smeared to fit, exactly the
/// problem the tray icon itself already had to solve.
/// </para>
/// <para>
/// Every draw is best-effort. A missing font, a locked-down machine, any GDI+ failure: the item
/// simply keeps its text and no icon. A decoration is never worth failing startup over.
/// </para>
/// </remarks>
public static class TrayGlyphs
{
    /// <summary>Segoe icon-font code points. All in the Private Use Area, where both fonts keep theirs.</summary>
    public const string Pause = "\uE769";

    /// <inheritdoc cref="Pause"/>
    public const string Play = "\uE768";

    /// <inheritdoc cref="Pause"/>
    public const string Refresh = "\uE72C";

    /// <inheritdoc cref="Pause"/>
    public const string Exit = "\uE7E8";

    /// <summary>
    /// The glyph for the pause command in a given state. Says what the command DOES, never what the
    /// state IS -- so a paused CosmicWin offers Play, exactly as its label offers "Reanudar".
    /// </summary>
    public static string ForPause(bool isPaused) => isPaused ? Play : Pause;

    /// <summary>
    /// Draws one glyph at the menu's own icon size and text colour, or <see langword="null"/> if it
    /// cannot be drawn.
    /// </summary>
    /// <remarks>
    /// <see cref="SystemColors.MenuText"/> rather than a fixed colour, so the icons follow the
    /// user's light or dark theme the same way the labels beside them already do. Drawing them
    /// black would leave four invisible squares on a dark menu.
    /// </remarks>
    public static Image? Render(string glyph)
    {
        try
        {
            var size = SystemInformation.SmallIconSize;
            var bitmap = new Bitmap(size.Width, size.Height);

            using var family = ResolveIconFont();
            if (family is null)
            {
                bitmap.Dispose();
                return null;
            }

            // Short of the full box: the glyphs are designed on a square em, and drawing at the
            // exact icon height leaves them touching the edges of a menu row.
            using var font = new Font(family, size.Height * 0.66f, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(SystemColors.MenuText);
            using var graphics = Graphics.FromImage(bitmap);
            using var centred = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size.Width, size.Height), centred);

            return bitmap;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows 11's icon font, then Windows 10's, then nothing. Resolved by NAME through the
    /// installed families rather than by constructing a <see cref="Font"/> and hoping: GDI+ silently
    /// substitutes a default face for an unknown family, which would draw the code point as a
    /// missing-glyph box instead of failing where it can be caught.
    /// </summary>
    private static FontFamily? ResolveIconFont()
    {
        foreach (var name in (string[])["Segoe Fluent Icons", "Segoe MDL2 Assets"])
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
