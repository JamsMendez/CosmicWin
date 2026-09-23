using System.Security.Principal;

namespace CosmicWin.Interop;

/// <summary>
/// Computes the per-user alert pipe name both ends of the T4 channel connect to.
/// </summary>
/// <remarks>
/// <para>
/// Decided 2026-09-23 (plan &#167;4, task file T4 entry): <c>CosmicWin.App.exe</c> runs elevated
/// (<c>requireAdministrator</c>), <c>CosmicWinAlert.exe</c> runs <c>asInvoker</c>, from the SAME
/// Windows user account -- elevation changes the token's integrity level and admin-group state, not
/// the account SID, so both processes resolve the identical name from <see
/// cref="WindowsIdentity.GetCurrent"/> with no coordination needed beyond calling this method.
/// </para>
/// <para>
/// Kept in exactly one place, in the root (Win32-free) <c>CosmicWin.Interop</c> namespace beside
/// <see cref="AlertPipeProtocol"/>, rather than duplicated in <c>CosmicWinAlert</c>: a name that
/// drifted between the two ends would fail silently (each side would simply never find the other,
/// with nothing to log), and <c>CosmicWin.Interop</c> is already the one project both the elevated
/// server and the unelevated client reference -- the server for <see cref="Win32.NamedPipeAlertCommandServer"/>,
/// the client for this method alone, which costs it no WPF or elevation dependency (see that
/// project's own remarks).
/// </para>
/// </remarks>
public static class AlertPipeName
{
    private const string Prefix = "CosmicWin.Alerts.";

    /// <summary>
    /// The pipe name for the current user, e.g. <c>CosmicWin.Alerts.S-1-5-21-...</c> -- passed to
    /// <c>NamedPipeServerStream</c>/<c>NamedPipeClientStream</c> as-is; neither type wants the
    /// leading <c>\\.\pipe\</c>.
    /// </summary>
    public static string Resolve() => Resolve(WindowsIdentity.GetCurrent());

    /// <summary>
    /// Testable overload: builds the name from an already-resolved identity rather than the live
    /// one, so a unit test can assert the exact string without depending on which account is
    /// running the test.
    /// </summary>
    public static string Resolve(WindowsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var sid = identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        return Prefix + sid.Value;
    }
}
