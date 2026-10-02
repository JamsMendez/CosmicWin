using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// The wire format the alert layer page understands. Moved here from the removed mini scene window
/// tests, which used to be the only place it was asserted behaviorally.
/// </summary>
public sealed class AlertLayerMessagesTests
{
    [Fact]
    public void AlertMessagesMatchTheExistingLayerWireFormat()
    {
        var request = new AlertShowRequest(["failed", "warning"], 2, 1, 8, 3000, 1, 2, 3, 4);

        Assert.Equal(
            "{\"type\":\"show\",\"tiles\":[\"failed\",\"warning\"],\"columns\":2,\"rows\":1,\"gap\":8,"
            + "\"workArea\":{\"left\":1,\"top\":2,\"width\":3,\"height\":4},\"duration\":3000}",
            AlertLayerMessages.Show(request));
        Assert.Equal("{\"type\":\"hide\"}", AlertLayerMessages.Hide);
    }

    [Fact]
    public void ShowMessageClampsNegativeWorkAreaToZero()
    {
        var request = new AlertShowRequest(["warning"], 1, 1, 0, 1000, -1, -2, -3, -4);

        Assert.Contains("\"workArea\":{\"left\":0,\"top\":0,\"width\":0,\"height\":0}", AlertLayerMessages.Show(request));
    }
}
