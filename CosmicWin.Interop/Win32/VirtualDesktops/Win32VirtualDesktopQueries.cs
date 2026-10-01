namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>
/// Read-only questions about where a window lives, answered through the DOCUMENTED
/// <see cref="IVirtualDesktopManager"/>.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="Win32VirtualDesktopService"/> because it answers a different kind of
/// question and carries a different risk. Reading a window's desktop needs no undocumented surface
/// at all — but "documented" was already shown not to mean "works cross-process" on this very
/// interface, since <c>MoveWindowToDesktop</c> refuses windows the caller does not own. So this is
/// measured before anything is built on it, not assumed.
/// <para>
/// This is only the process-wide entry point: the logic and the cached manager live in
/// <see cref="VirtualDesktopQueryClient"/>. There is deliberately no mutable static seam here --
/// tests build their own client, so a fake can never leak into another test class running in
/// parallel.
/// </para>
/// </remarks>
internal static class Win32VirtualDesktopQueries
{
    private static readonly VirtualDesktopQueryClient Shared = new(VirtualDesktopQueryClient.CreateManager);

    /// <inheritdoc cref="VirtualDesktopQueryClient.TryGetWindowDesktopId"/>
    public static bool TryGetWindowDesktopId(nint windowHandle, out Guid desktopId, out string? error) =>
        Shared.TryGetWindowDesktopId(windowHandle, out desktopId, out error);

    /// <inheritdoc cref="VirtualDesktopQueryClient.TryIsWindowOnCurrentDesktop"/>
    public static bool TryIsWindowOnCurrentDesktop(nint windowHandle, out bool onCurrentDesktop, out string? error) =>
        Shared.TryIsWindowOnCurrentDesktop(windowHandle, out onCurrentDesktop, out error);

    /// <summary>The desktop the user is currently looking at, or <see cref="Guid.Empty"/> if unknown.</summary>
    public static Guid GetCurrentDesktopId() =>
        new Win32NativeVirtualDesktops() is { IsAvailable: true } native ? native.GetCurrentDesktopId() : Guid.Empty;
}
