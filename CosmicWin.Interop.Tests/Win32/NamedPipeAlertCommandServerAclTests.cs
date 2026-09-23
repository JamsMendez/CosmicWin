using System.Security.Principal;
using System.Text.RegularExpressions;
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
    /// <summary>
    /// Finding R3-acl-test-owner-masks-dacl: <c>Assert.Contains(userSid, sddl)</c> alone would still
    /// pass if the current user's SID showed up ONLY as the security descriptor's OWNER
    /// (<c>O:&lt;sid&gt;</c>) and the DACL granted access to nobody, or to a different SID -- the
    /// owner field says who owns the object, not who may open it. This test instead parses the
    /// <c>D:</c> section out of the SDDL and asserts the exact ACE list it contains: exactly one
    /// entry, an ALLOW ACE, GENERIC_ALL rights, for the current user's SID and no one else.
    /// </summary>
    [Fact]
    public void CreateSecuredHandle_DaclGrantsExactlyOneAllowGenericAllAceForTheCurrentUser()
    {
        var pipeName = $"CosmicWin.Alerts.Tests.Acl.{Guid.NewGuid():N}";
        var userSid = WindowsIdentity.GetCurrent().User!.Value;

        using var handle = NamedPipeAlertCommandServer.CreateSecuredHandle(pipeName);
        var sddl = NamedPipeAlertCommandServer.DescribeSecurity(handle);

        var aces = ParseDaclAces(sddl);
        var ace = Assert.Single(aces);
        Assert.Equal("A", ace.AceType);
        Assert.Equal("", ace.Flags);
        // CreateSecuredHandle grants "GA" (GENERIC_ALL) in the SDDL it builds, but the kernel
        // resolves a generic right to its object-specific equivalent before storing the ACE, so a
        // round trip through GetSecurityInfo reports the resolved mask -- "FA" (all File/pipe
        // access), not the generic alias that went in. Observed directly against a live handle
        // rather than assumed.
        Assert.Equal("FA", ace.Rights);
        Assert.Equal(userSid, ace.TrusteeSid);
    }

    /// <summary>
    /// Parses the ACE list out of the SDDL's own <c>D:</c> (DACL) component, stopping before the
    /// next top-level component (<c>O:</c>/<c>G:</c>/<c>S:</c>) rather than scanning the whole
    /// string, so an ACE-shaped SID elsewhere (e.g. the owner) can never be mistaken for a DACL
    /// grant. Each ACE is <c>(AceType;AceFlags;Rights;ObjectGuid;InheritObjectGuid;TrusteeSid)</c>
    /// (<see href="https://learn.microsoft.com/windows/win32/secauthz/ace-strings"/>); this server
    /// only ever builds the simple, non-object form, so every field but type/rights/trustee is
    /// expected empty.
    /// </summary>
    private static IReadOnlyList<(string AceType, string Flags, string Rights, string TrusteeSid)> ParseDaclAces(string sddl)
    {
        var daclMatch = Regex.Match(sddl, @"D:(?:[A-Z]+)?((?:\([^)]*\))*)");
        Assert.True(daclMatch.Success, $"No D: (DACL) component found in SDDL: {sddl}");

        var aces = new List<(string, string, string, string)>();
        foreach (Match aceMatch in Regex.Matches(daclMatch.Groups[1].Value, @"\(([^)]*)\)"))
        {
            var fields = aceMatch.Groups[1].Value.Split(';');
            Assert.Equal(6, fields.Length);
            aces.Add((fields[0], fields[1], fields[2], fields[5]));
        }

        return aces;
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
