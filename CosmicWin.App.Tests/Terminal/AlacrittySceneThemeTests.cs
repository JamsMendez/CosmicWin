using System.Globalization;
using System.Text.RegularExpressions;
using CosmicWin.App.Terminal;

namespace CosmicWin.App.Tests.Terminal;

/// <summary>
/// The per-scene Alacritty palettes: the TOML shape, and the legibility guarantee that is the whole
/// reason they exist.
/// </summary>
public sealed class AlacrittySceneThemeTests
{
    private static readonly string[] AnsiNames =
        ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"];

    // Alacritty's opacity only affects the BACKGROUND, so a translucent terminal shows
    // 0.85 * themeBg + 0.15 * scenePixel behind the text. That 15% bleeds the scene's brightest
    // highlights through, and legibility is decided by luminance, not hue -- so every scene is
    // checked against its own brightest and darkest colours rather than one generic backdrop.
    private const double WorstCaseOpacity = 0.85;

    private static readonly Dictionary<WallpaperScene, (int R, int G, int B)[]> Backdrops = new()
    {
        [WallpaperScene.Processing] =
            [(1, 4, 10), (255, 255, 255), (70, 210, 255), (255, 70, 170), (255, 238, 60), (80, 255, 170)],
        [WallpaperScene.Raphael] =
            [(1, 4, 10), (255, 255, 255), (70, 210, 255), (255, 70, 170), (255, 238, 60), (255, 201, 74), (255, 243, 196)],
        [WallpaperScene.Idle] =
            [(1, 4, 10), (255, 255, 255), (235, 235, 235), (255, 250, 235)],
        [WallpaperScene.Explorer] =
            [(1, 4, 10), (255, 255, 255), (255, 250, 235), (40, 120, 200), (25, 120, 175)],
    };

    public static TheoryData<WallpaperScene> AllScenes => new(Enum.GetValues<WallpaperScene>());

    [Theory]
    [MemberData(nameof(AllScenes))]
    public void Render_EmitsEveryColourKeyQuotedAsHex(WallpaperScene scene)
    {
        var colours = Parse(AlacrittySceneTheme.Render(scene));

        Assert.Equal(2 + 8 + 8, colours.Count);
        Assert.Contains("primary.background", colours.Keys);
        Assert.Contains("primary.foreground", colours.Keys);
        foreach (var section in new[] { "normal", "bright" })
        {
            foreach (var name in AnsiNames)
            {
                Assert.Contains($"{section}.{name}", colours.Keys);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllScenes))]
    public void Render_StartsWithAnOwnershipHeaderNamingTheScene(WallpaperScene scene)
    {
        var text = AlacrittySceneTheme.Render(scene);

        Assert.StartsWith("# Written by CosmicWin for scene " + scene.ToString().ToLowerInvariant(), text);
        Assert.Contains("do not edit", text);
    }

    [Fact]
    public void Render_UsesTheAgreedPalette()
    {
        var colours = Parse(AlacrittySceneTheme.Render(WallpaperScene.Raphael));

        Assert.Equal("#0A0C12", colours["primary.background"]);
        Assert.Equal("#9AA0AC", colours["bright.black"]);
        Assert.Equal("#6FD6CC", colours["normal.cyan"]);
    }

    [Theory]
    [MemberData(nameof(AllScenes))]
    public void Render_KeepsTextLegibleOverTheSceneWorstCase(WallpaperScene scene)
    {
        var colours = Parse(AlacrittySceneTheme.Render(scene));
        var paletteBg = ToRgb(colours["primary.background"]);

        foreach (var backdrop in Backdrops[scene])
        {
            var composite = (
                R: WorstCaseOpacity * paletteBg.R + (1 - WorstCaseOpacity) * backdrop.R,
                G: WorstCaseOpacity * paletteBg.G + (1 - WorstCaseOpacity) * backdrop.G,
                B: WorstCaseOpacity * paletteBg.B + (1 - WorstCaseOpacity) * backdrop.B);

            foreach (var (key, hex) in colours)
            {
                if (key.StartsWith("primary.background", StringComparison.Ordinal) || key == "normal.black")
                {
                    continue; // black is the ANSI slot for dark-on-light UIs, not body text
                }

                var required = key == "primary.foreground" ? 7.0 : 4.5;
                var contrast = Contrast(ToRgb(hex), composite);
                Assert.True(contrast >= required,
                    $"{scene} {key} {hex} over backdrop {backdrop}: {contrast:F2} < {required}");
            }
        }
    }

    private static Dictionary<string, string> Parse(string toml)
    {
        var result = new Dictionary<string, string>();
        var section = string.Empty;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            var header = Regex.Match(line, @"^\[colors\.(\w+)\]$");
            if (header.Success)
            {
                section = header.Groups[1].Value;
                continue;
            }

            var entry = Regex.Match(line, @"^(\w+)\s*=\s*'(#[0-9A-Fa-f]{6})'$");
            if (entry.Success)
            {
                result[$"{section}.{entry.Groups[1].Value}"] = entry.Groups[2].Value;
            }
        }

        return result;
    }

    private static (double R, double G, double B) ToRgb(string hex) => (
        int.Parse(hex[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static double Luminance((double R, double G, double B) c)
    {
        static double Linear(double channel)
        {
            var s = channel / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
