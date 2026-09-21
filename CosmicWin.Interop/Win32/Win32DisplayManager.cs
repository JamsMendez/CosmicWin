namespace CosmicWin.Interop.Win32;

/// <summary>
/// WT-2: enumerates attached monitors and exposes each one's real (PerMonitorV2) DPI scaling
/// factor, bounds, and work area.
/// </summary>
/// <remarks>
/// Enumerates once and reads each monitor's real DPI through <c>GetDpiForMonitor</c>, trimmed to what
/// WT-2 and this work unit need: a point-in-time snapshot taken at construction. Hotplug
/// tracking (MM-1/MM-2, Phase 3,) is out of this work unit's scope.
/// </remarks>
public sealed class Win32DisplayManager : IDisplayManager
{
    private readonly List<Win32Display> _displays;
    private readonly INativeDisplaySource _nativeSource;

    public Win32DisplayManager()
        : this(new Win32NativeDisplaySource())
    {
    }

    internal Win32DisplayManager(INativeDisplaySource nativeSource)
    {
        _nativeSource = nativeSource;
        var infos = nativeSource.EnumerateDisplays();
        if (infos.Count == 0)
        {
            throw new InvalidOperationException("No displays were found.");
        }

        _displays = infos.Select(info => new Win32Display(info)).ToList();
    }

    public IReadOnlyList<IDisplay> Displays => _displays;

    /// <summary>
    /// Re-reads every known monitor from the OS and returns the ones whose bounds, work area, scaling
    /// or primary flag differ from what they last reported. The <see cref="IDisplay"/> objects are the
    /// same ones <see cref="Displays"/> already holds, updated in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a taskbar that hides, moves to another edge or is resized reaches the layout: the
    /// work area is the one thing that changes while every window and monitor stays put, and Windows
    /// raises nothing this process subscribes to for it.
    /// </para>
    /// <para>
    /// A monitor that is no longer enumerated is left as last known and not reported. Hot-plug is a
    /// different event with its own handling; treating a short enumeration as "the work area shrank
    /// to nothing" would tear the layout down on every unplug.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IDisplay> Refresh()
    {
        var current = _nativeSource.EnumerateDisplays();
        var changed = new List<IDisplay>();

        foreach (var display in _displays)
        {
            foreach (var info in current)
            {
                if (info.Handle == display.Handle)
                {
                    if (display.Refresh(info))
                    {
                        changed.Add(display);
                    }

                    break;
                }
            }
        }

        return changed;
    }

    public IDisplay Primary => _displays.FirstOrDefault(d => d.IsPrimary) ?? _displays[0];
}
