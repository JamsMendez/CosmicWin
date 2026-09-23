using System.Security.Principal;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Proves <see cref="NamedPipeAlertCommandServer.CreateSecuredHandle"/> actually attaches the
/// security descriptor the T4 task file demands (current-user DACL, medium mandatory label with
/// no-write-up) to the pipe it creates -- read back from the live handle via <c>GetSecurityInfo</c>
/// + <c>ConvertSecurityDescriptorToStringSecurityDescriptor</c>
/// (<see cref="NamedPipeAlertCommandServer.DescribeSecurity"/>), never merely asserted against the
/// SDDL string that went in.
/// </summary>
/// <remarks>
/// Needs no desktop, no elevation and no <see cref="DesktopGate"/>: creating and inspecting a
/// named pipe is available to any process running as a normal user, exactly like the rest of this
/// assembly's headless facts.
/// </remarks>
public sealed class NamedPipeAlertCommandServerAclTests
{
    [Fact]
    public void CreateSecuredHandle_GrantsTheCurrentUser()
    {
        var pipeName = $"CosmicWin.Alerts.Tests.Acl.{Guid.NewGuid():N}";
        var userSid = WindowsIdentity.GetCurrent().User!.Value;

        using var handle = NamedPipeAlertCommandServer.CreateSecuredHandle(pipeName);
        var sddl = NamedPipeAlertCommandServer.DescribeSecurity(handle);

        Assert.Contains(userSid, sddl);
    }

    [Fact]
    public void CreateSecuredHandle_CarriesAMediumMandatoryLabelWithNoWriteUp()
    {
        var pipeName = $"CosmicWin.Alerts.Tests.Acl.{Guid.NewGuid():N}";

        using var handle = NamedPipeAlertCommandServer.CreateSecuredHandle(pipeName);
        var sddl = NamedPipeAlertCommandServer.DescribeSecurity(handle);

        // The exact ACE the task file specifies: mandatory label (ML), no-write-up (NW), Medium (ME).
        Assert.Contains("(ML;;NW;;;ME)", sddl);
    }

    [Fact]
    public void CreateSecuredHandle_TwoCallsWithDifferentNames_BothSucceed()
    {
        // Not a shared-state fact -- guards against a static/leaked buffer in CreateSecuredHandle
        // (e.g. forgetting to free or double-freeing the security descriptor across calls).
        using var first = NamedPipeAlertCommandServer.CreateSecuredHandle($"CosmicWin.Alerts.Tests.Acl.{Guid.NewGuid():N}");
        using var second = NamedPipeAlertCommandServer.CreateSecuredHandle($"CosmicWin.Alerts.Tests.Acl.{Guid.NewGuid():N}");

        Assert.False(first.IsInvalid);
        Assert.False(second.IsInvalid);
    }
}
