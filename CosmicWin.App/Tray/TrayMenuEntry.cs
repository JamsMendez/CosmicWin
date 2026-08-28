namespace CosmicWin.App.Tray;

/// <summary>
/// The items the tray menu offers, named so their ORDER can be stated once and tested.
/// </summary>
/// <remarks>
/// An enum rather than a list of labels: the order is a decision about the menu, and tying it to
/// display strings would make every wording change look like a reordering. <see cref="TrayIconHost"/>
/// builds its menu by walking <see cref="TrayIconHost.MenuOrder"/>, so this is the real source of
/// truth rather than a description of one.
/// </remarks>
public enum TrayMenuEntry
{
    /// <summary>Draws CosmicWin's own thicker focus border, or leaves only the one DWM draws.</summary>
    FocusBorder,

    /// <summary>Picks the colour that border is drawn in, or hands it back to Windows' accent.</summary>
    BorderColor,

    /// <summary>Stops and resumes the keyboard hook, and with it every chord.</summary>
    Pause,

    /// <summary>Re-reads the exception list from disk.</summary>
    Reload,

    /// <summary>Stops the logon trigger and takes the process down.</summary>
    Exit,
}
