namespace CosmicWin.Interop.Win32;

/// <summary>
/// WT-2: enumerates attached monitors and exposes each one's real (PerMonitorV2) DPI scaling
/// factor, bounds, and work area.
/// </summary>
/// <remarks>
/// Ports the enumerate + <c>GetDpiForMonitor</c> pattern from fancywm's
/// <c>WinMan.Windows.Win32DisplayManager</c> (algorithm only — original code), trimmed to what
/// WT-2 and this work unit need: a point-in-time snapshot taken at construction. Hotplug
/// tracking (MM-1/MM-2, Phase 3, WU8) is out of this work unit's scope.
/// </remarks>
public sealed class Win32DisplayManager : IDisplayManager
{
    private readonly List<Win32Display> _displays;

    public Win32DisplayManager()
        : this(new Win32NativeDisplaySource())
    {
    }

    internal Win32DisplayManager(INativeDisplaySource nativeSource)
    {
        var infos = nativeSource.EnumerateDisplays();
        if (infos.Count == 0)
        {
            throw new InvalidOperationException("No displays were found.");
        }

        _displays = infos.Select(info => new Win32Display(info)).ToList();
    }

    public IReadOnlyList<IDisplay> Displays => _displays;

    public IDisplay Primary => _displays.FirstOrDefault(d => d.IsPrimary) ?? _displays[0];
}
