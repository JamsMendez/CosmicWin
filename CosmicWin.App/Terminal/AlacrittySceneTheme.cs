using System.Text;

namespace CosmicWin.App.Terminal;

/// <summary>
/// Renders the Alacritty <c>[colors.*]</c> TOML for a wallpaper scene. Pure: no disk, no clock.
/// </summary>
/// <remarks>
/// Alacritty's opacity only affects the background, so a translucent terminal shows
/// <c>opacity * themeBg + (1 - opacity) * scenePixel</c> behind the text and a bright scene highlight
/// bleeds through. Legibility is a luminance problem, not a hue one, so every palette keeps a dark
/// background, a light foreground and a raised <c>bright.black</c> (comments, autosuggestions), and
/// only the accent hues are tuned per scene. The tests assert the contrast floor against each
/// scene's worst-case backdrop.
/// </remarks>
public static class AlacrittySceneTheme
{
    private static readonly string[] AnsiNames =
        ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"];

    private static readonly string[] BrightCool =
        ["#9AA0AC", "#FFB8BE", "#BDEB9C", "#FFDA7A", "#A6DBFF", "#F5C6FF", "#94EDE4", "#F4F6FA"];

    private static readonly AlacrittyPalette Processing = new(
        "#0A0C10", "#E3E6ED",
        ["#0B0D10", "#FF9AA2", "#A8DC86", "#F5CF72", "#7FC4FF", "#EDB6F7", "#8FE3DA", "#C9CDD6"],
        BrightCool);

    private static readonly AlacrittyPalette Raphael = new(
        "#0A0C12", "#E3E6ED",
        ["#0B0D10", "#FFA3AB", "#9FD67A", "#F7D98A", "#7FC4FF", "#E6A8F5", "#6FD6CC", "#C9CDD6"],
        ["#9AA0AC", "#FFB8BE", "#BDEB9C", "#FFE6A8", "#A6DBFF", "#F5C6FF", "#94EDE4", "#F4F6FA"]);

    private static readonly AlacrittyPalette Idle = new(
        "#0B0D10", "#E3E6ED",
        ["#0B0D10", "#FF9AA2", "#9FD67A", "#F2C45A", "#7FC4FF", "#E6A8F5", "#6FD6CC", "#C9CDD6"],
        BrightCool);

    private static readonly AlacrittyPalette Explorer = new(
        "#0E0D0C", "#E3E6ED",
        ["#0B0D10", "#FF9AA2", "#9FD67A", "#F2C45A", "#9CD2FF", "#E6A8F5", "#7EDCD2", "#C9CDD6"],
        ["#9AA0AC", "#FFB8BE", "#BDEB9C", "#FFDA7A", "#BCE3FF", "#F5C6FF", "#94EDE4", "#F4F6FA"]);

    /// <summary>The palette a scene uses.</summary>
    public static AlacrittyPalette PaletteFor(WallpaperScene scene) => scene switch
    {
        WallpaperScene.Explorer => Explorer,
        WallpaperScene.Idle => Idle,
        WallpaperScene.Raphael => Raphael,
        _ => Processing,
    };

    /// <summary>The complete colours file for <paramref name="scene"/>, LF line endings.</summary>
    public static string Render(WallpaperScene scene)
    {
        var palette = PaletteFor(scene);
        var text = new StringBuilder();
        text.Append("# Written by CosmicWin for scene ").Append(scene.ToString().ToLowerInvariant())
            .Append(". Overwritten on every scene change; do not edit.\n\n");

        text.Append("[colors.primary]\n");
        text.Append("background = '").Append(palette.Background).Append("'\n");
        text.Append("foreground = '").Append(palette.Foreground).Append("'\n\n");

        AppendAnsi(text, "normal", palette.Normal);
        text.Append('\n');
        AppendAnsi(text, "bright", palette.Bright);
        return text.ToString();
    }

    private static void AppendAnsi(StringBuilder text, string section, IReadOnlyList<string> colours)
    {
        text.Append("[colors.").Append(section).Append("]\n");
        for (var i = 0; i < AnsiNames.Length; i++)
        {
            text.Append(AnsiNames[i]).Append(" = '").Append(colours[i]).Append("'\n");
        }
    }
}
