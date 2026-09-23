using System.Security.Principal;
using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests;

/// <summary>
/// <see cref="AlertPipeName"/> is the one place T4's per-user pipe name is computed, shared by the
/// elevated server (<c>NamedPipeAlertCommandServer</c>) and the unelevated <c>CosmicWinAlert</c>
/// client -- see that class's remarks for why both resolve the same name despite running at
/// different integrity levels.
/// </summary>
/// <remarks>
/// <see cref="WindowsIdentity"/> has no supported way to fabricate an identity carrying an
/// arbitrary SID chosen by a test -- every public constructor either logs on or wraps a real
/// token. These facts use the two real identities every Windows process can always construct
/// without a live logon: the running process's own identity, and the well-known anonymous one --
/// which, measured here, reports a <see langword="null"/> <see cref="WindowsIdentity.User"/>
/// rather than the <c>S-1-5-7</c> SID its documentation might suggest, making it the fact that
/// proves <see cref="AlertPipeName.Resolve(WindowsIdentity)"/> refuses a SID-less identity with a
/// clear message instead of a raw <see cref="NullReferenceException"/>.
/// </remarks>
public sealed class AlertPipeNameTests
{
    [Fact]
    public void Resolve_TheLiveIdentity_IsPrefixedWithItsOwnSid()
    {
        var identity = WindowsIdentity.GetCurrent();

        var name = AlertPipeName.Resolve(identity);

        Assert.Equal("CosmicWin.Alerts." + identity.User!.Value, name);
    }

    [Fact]
    public void Resolve_TheLiveIdentity_MatchesTheOverloadTakingItExplicitly()
    {
        // Proves the parameterless overload is not a second implementation that could drift from
        // the testable one -- exactly the drift this class's own remarks say the shared location
        // exists to prevent.
        Assert.Equal(AlertPipeName.Resolve(WindowsIdentity.GetCurrent()), AlertPipeName.Resolve());
    }

    [Fact]
    public void Resolve_AnIdentityWithNoUserSid_ThrowsAClearError()
    {
        // WindowsIdentity.GetAnonymous() on this platform carries a null User -- verified by
        // MeasuredAnonymousIdentityHasNoUserSid below, which is what makes it a real, reachable
        // case for this fact rather than an assumption about a SID nobody constructed here.
        var identity = WindowsIdentity.GetAnonymous();

        var error = Assert.Throws<InvalidOperationException>(() => AlertPipeName.Resolve(identity));
        Assert.Contains("no user SID", error.Message);
    }

    [Fact]
    public void MeasuredAnonymousIdentityHasNoUserSid() =>
        Assert.Null(WindowsIdentity.GetAnonymous().User);

    [Fact]
    public void Resolve_NullIdentity_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AlertPipeName.Resolve(null!));
}
