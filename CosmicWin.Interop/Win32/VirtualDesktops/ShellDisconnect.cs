namespace CosmicWin.Interop.Win32.VirtualDesktops;

/// <summary>
/// Recognises the HRESULTs that mean "the shell process behind this COM proxy is gone".
/// </summary>
/// <remarks>
/// The shell's desktop managers are proxies into <c>explorer.exe</c>. When Explorer restarts, the
/// cached proxies are dead for good and every call through them fails with an RPC-class HRESULT --
/// which used to be swallowed as an ordinary interop failure, leaving CosmicWin seeing zero desktops
/// until it was restarted. One place decides what counts, so the two callers
/// (<see cref="Win32NativeVirtualDesktops"/> and <see cref="Win32VirtualDesktopQueries"/>) cannot
/// drift apart. The list is deliberately narrow: <c>E_ACCESSDENIED</c> or <c>E_NOINTERFACE</c> are
/// real answers from a LIVE shell, and re-resolving on them would only hide the answer.
/// </remarks>
internal static class ShellDisconnect
{
    /// <summary><c>RPC_S_SERVER_UNAVAILABLE</c>, the code seen on hardware after the restart.</summary>
    public const int ServerUnavailable = unchecked((int)0x800706BA);

    /// <summary><c>RPC_S_CALL_FAILED</c>.</summary>
    public const int CallFailed = unchecked((int)0x800706BE);

    /// <summary><c>RPC_E_DISCONNECTED</c>.</summary>
    public const int Disconnected = unchecked((int)0x80010108);

    /// <summary><c>RPC_E_SERVER_DIED_DNE</c>.</summary>
    public const int ServerDied = unchecked((int)0x80010012);

    /// <summary><c>CO_E_SERVER_STOPPING</c>.</summary>
    public const int ServerStopping = unchecked((int)0x80004018);

    /// <summary>
    /// Whether <paramref name="hresult"/> means the shell behind the proxy is gone. Works on both
    /// shapes a failure arrives in: a <c>COMException.HResult</c> and the <c>hr &lt; 0</c> a
    /// <c>PreserveSig</c> method returns.
    /// </summary>
    public static bool IsDisconnect(int hresult) =>
        hresult is ServerUnavailable or CallFailed or Disconnected or ServerDied or ServerStopping;
}
