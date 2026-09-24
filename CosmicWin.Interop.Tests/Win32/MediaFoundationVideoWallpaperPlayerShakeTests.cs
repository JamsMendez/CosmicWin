namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// T4 (webview-alert-layer): <see cref="MediaFoundationVideoWallpaperPlayer.Shake"/> and the
/// per-tick transform it drives on the host's composition seam
/// (<see cref="IVideoWallpaperHost.SetVideoTransform"/>/<see cref="IVideoWallpaperHost.ClearVideoTransform"/>).
/// Exercised through <see cref="MediaFoundationVideoWallpaperPlayer.ApplyShakeForTests"/> -- a
/// test-only entry point into the same per-tick logic <c>Tick</c> calls, with no real
/// <c>IMFMediaEngine</c> anywhere near it (mirrors how the existing frame-overlay tests exercise
/// <c>TickForTests</c> instead of a real engine) -- and a manual <see cref="TimeProvider"/> so the
/// 230 ms decay window is deterministic rather than raced against a real clock.
/// </summary>
public sealed class MediaFoundationVideoWallpaperPlayerShakeTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    [Fact]
    public void NoShakeTriggered_ApplyDoesNothingToTheHost()
    {
        var time = new ManualTimeProvider(Epoch);
        using var player = new CosmicWin.Interop.Win32.MediaFoundationVideoWallpaperPlayer(timeProvider: time);
        var host = new FakeVideoWallpaperHost { BackBufferSize = (1920, 1080) };

        player.ApplyShakeForTests(host);

        Assert.Equal(0, host.SetVideoTransformCallCount);
        Assert.Equal(0, host.ClearVideoTransformCallCount);
    }

    [Fact]
    public void Shake_AtStart_SetsTransformMatchingVideoShakeMath()
    {
        var time = new ManualTimeProvider(Epoch);
        using var player = new CosmicWin.Interop.Win32.MediaFoundationVideoWallpaperPlayer(timeProvider: time);
        var host = new FakeVideoWallpaperHost { BackBufferSize = (1920, 1080) };

        player.Shake(TimeSpan.FromMilliseconds(230));
        player.ApplyShakeForTests(host);

        Assert.Equal(1, host.SetVideoTransformCallCount);
        Assert.Equal(0, host.ClearVideoTransformCallCount);

        var expected = CosmicWin.Interop.Win32.VideoShakeMath.Compute(0, 1920, 1080);
        var actual = host.LastVideoTransform!.Value;
        Assert.Equal(960, actual.CenterX, precision: 3);
        Assert.Equal(540, actual.CenterY, precision: 3);
        Assert.Equal((float)expected.Dx, actual.OffsetX, precision: 3);
        Assert.Equal((float)expected.Dy, actual.OffsetY, precision: 3);
        Assert.Equal((float)expected.AngleDegrees, actual.AngleDegrees, precision: 3);
        Assert.Equal((float)expected.Scale, actual.Scale, precision: 3);
    }

    [Fact]
    public void Shake_PartwayThrough_IsStillActiveAndDoesNotClear()
    {
        var time = new ManualTimeProvider(Epoch);
        using var player = new CosmicWin.Interop.Win32.MediaFoundationVideoWallpaperPlayer(timeProvider: time);
        var host = new FakeVideoWallpaperHost { BackBufferSize = (1920, 1080) };

        player.Shake(TimeSpan.FromMilliseconds(230));
        time.Advance(TimeSpan.FromMilliseconds(100));
        player.ApplyShakeForTests(host);

        Assert.True(player.IsShakingForTests);
        Assert.Equal(1, host.SetVideoTransformCallCount);
        Assert.Equal(0, host.ClearVideoTransformCallCount);
    }

    [Fact]
    public void Shake_OnceDurationElapses_ClearsExactlyOnceAndStops()
    {
        var time = new ManualTimeProvider(Epoch);
        using var player = new CosmicWin.Interop.Win32.MediaFoundationVideoWallpaperPlayer(timeProvider: time);
        var host = new FakeVideoWallpaperHost { BackBufferSize = (1920, 1080) };

        player.Shake(TimeSpan.FromMilliseconds(230));
        time.Advance(TimeSpan.FromMilliseconds(230));
        player.ApplyShakeForTests(host);

        Assert.False(player.IsShakingForTests);
        Assert.Equal(0, host.SetVideoTransformCallCount);
        Assert.Equal(1, host.ClearVideoTransformCallCount);

        // Zero extra cost once it has ended: a later tick touches the host at all.
        player.ApplyShakeForTests(host);
        Assert.Equal(0, host.SetVideoTransformCallCount);
        Assert.Equal(1, host.ClearVideoTransformCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveShake_StopOrDispose_ClearsWithoutAnotherTick(bool dispose)
    {
        var time = new ManualTimeProvider(Epoch);
        using var player = new CosmicWin.Interop.Win32.MediaFoundationVideoWallpaperPlayer(timeProvider: time);
        var host = new FakeVideoWallpaperHost { BackBufferSize = (1920, 1080) };

        player.Shake(TimeSpan.FromMilliseconds(230));
        player.ApplyShakeForTests(host);
        Assert.NotNull(host.LastVideoTransform);

        if (dispose) player.Dispose();
        else player.Stop();

        Assert.False(player.IsShakingForTests);
        Assert.Equal(1, host.ClearVideoTransformCallCount);
    }

    [Fact]
    public void Shake_CalledAgainWhileActive_RestartsTheElapsedClock()
    {
        var time = new ManualTimeProvider(Epoch);
        using var player = new CosmicWin.Interop.Win32.MediaFoundationVideoWallpaperPlayer(timeProvider: time);
        var host = new FakeVideoWallpaperHost { BackBufferSize = (1920, 1080) };

        player.Shake(TimeSpan.FromMilliseconds(230));
        time.Advance(TimeSpan.FromMilliseconds(200));

        // A second failed alert arrives before the first shake finished -- it must restart, not be
        // ignored, and not throw from being called while already active.
        player.Shake(TimeSpan.FromMilliseconds(230));
        time.Advance(TimeSpan.FromMilliseconds(200));
        player.ApplyShakeForTests(host);

        Assert.True(player.IsShakingForTests);

        var expected = CosmicWin.Interop.Win32.VideoShakeMath.Compute(200, 1920, 1080);
        var actual = host.LastVideoTransform!.Value;
        Assert.Equal((float)expected.Dx, actual.OffsetX, precision: 3);
    }
}
