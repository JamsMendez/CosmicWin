using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// T4 (webview-alert-layer): pure port of the alert page's own <c>applyFailureShake</c> (dropped
/// from the T1 page -- see its remarks -- because T4 shakes the VIDEO natively instead of the
/// page's canvas). No DirectComposition, no host, no player: a function of elapsed time and the
/// back buffer's own size, so this is testable without a desktop.
/// </summary>
public sealed class VideoShakeMathTests
{
    [Fact]
    public void AtZeroElapsed_DecayIsFullAndScaleIsMax()
    {
        var transform = VideoShakeMath.Compute(0, 1920, 1080);

        // decay = 1 - 0/230 = 1 -> amplitude = min(1920,1080) * 1.0 = 1080
        // t = 0 -> sin(0)=0, sin(1.3)=0.963558..., sin(2.1)=0.863209..., sin(0.4)=0.389418...
        double amplitude = 1080;
        double expectedDx = amplitude * 0.1 * (Math.Sin(0) * 0.65 + Math.Sin(1.3) * 0.35);
        double expectedDy = amplitude * 0.04 * (Math.Sin(2.1) * 0.6 + Math.Sin(0.4) * 0.4);
        double expectedAngle = 3.5 * 1.0 * Math.Sin(0.7);

        Assert.Equal(expectedDx, transform.Dx, precision: 6);
        Assert.Equal(expectedDy, transform.Dy, precision: 6);
        Assert.Equal(expectedAngle, transform.AngleDegrees, precision: 6);
        Assert.Equal(1.18, transform.Scale, precision: 6);
    }

    [Fact]
    public void HalfwayThroughTheShake_DecayIsHalf()
    {
        var transform = VideoShakeMath.Compute(115, 1000, 500);

        double decay = 1 - 115.0 / 230.0;
        double amplitude = Math.Min(1000, 500) * (0.5 + 0.5 * decay);
        double t = 115.0 / 1000.0;
        double expectedDx = amplitude * 0.1 * (Math.Sin(t * 131) * 0.65 + Math.Sin(t * 211 + 1.3) * 0.35);
        double expectedDy = amplitude * 0.04 * (Math.Sin(t * 109 + 2.1) * 0.6 + Math.Sin(t * 197 + 0.4) * 0.4);
        double expectedAngle = 3.5 * decay * Math.Sin(t * 89 + 0.7);

        Assert.Equal(expectedDx, transform.Dx, precision: 6);
        Assert.Equal(expectedDy, transform.Dy, precision: 6);
        Assert.Equal(expectedAngle, transform.AngleDegrees, precision: 6);
        Assert.Equal(1.18, transform.Scale, precision: 6);
    }

    [Theory]
    [InlineData(230)]
    [InlineData(231)]
    [InlineData(1000)]
    public void AtOrPastTheShakeDuration_IsIdentity(double elapsedMilliseconds)
    {
        var transform = VideoShakeMath.Compute(elapsedMilliseconds, 1920, 1080);

        Assert.Equal(0, transform.Dx);
        Assert.Equal(0, transform.Dy);
        Assert.Equal(0, transform.AngleDegrees);
        Assert.Equal(1.0, transform.Scale);
    }

    [Fact]
    public void NegativeElapsed_IsAlsoIdentity()
    {
        var transform = VideoShakeMath.Compute(-1, 1920, 1080);

        Assert.Equal(0, transform.Dx);
        Assert.Equal(0, transform.Dy);
        Assert.Equal(0, transform.AngleDegrees);
        Assert.Equal(1.0, transform.Scale);
    }

    [Fact]
    public void DurationMillisecondsConstant_MatchesThePageAndTheDirect2DTheme()
    {
        Assert.Equal(230, VideoShakeMath.DurationMilliseconds);
    }
}
