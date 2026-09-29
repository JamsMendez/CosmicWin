namespace CosmicWin.App.Terminal;

/// <summary>
/// One immutable Alacritty colour set: the primary pair plus the eight normal and eight bright ANSI
/// slots, each an <c>#RRGGBB</c> literal. The ANSI arrays are ordered black, red, green, yellow,
/// blue, magenta, cyan, white.
/// </summary>
public sealed record AlacrittyPalette(
    string Background,
    string Foreground,
    IReadOnlyList<string> Normal,
    IReadOnlyList<string> Bright);
