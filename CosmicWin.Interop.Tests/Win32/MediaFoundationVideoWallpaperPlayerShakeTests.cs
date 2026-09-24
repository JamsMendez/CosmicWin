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

        // Production wiring (AppComposition.UpdateAlertOverlay) always passes exactly
        // VideoShakeMath.DurationMilliseconds -- use the same duration here so the restart is
        // exercised inside the one window production can actually reach.
        var duration = TimeSpan.FromMilliseconds(CosmicWin.Interop.Win32.VideoShakeMath.DurationMilliseconds);

        player.Shake(duration);
        time.Advance(TimeSpan.FromMilliseconds(80));

        // A second failed alert arrives before the first shake finished -- it must restart, not be
        // ignored, and not throw from being called while already active.
        player.Shake(duration);
        time.Advance(TimeSpan.FromMilliseconds(50));
        player.ApplyShakeForTests(host);

        Assert.True(player.IsShakingForTests);

        // 50 ms since the restart is inside VideoShakeMath's decay window (< DurationMilliseconds),
        // so this is not the trivial zero the identity transform would produce. It also differs from
        // what 130 ms since the FIRST start would give -- past DurationMilliseconds, i.e. identity --
        // which is what an unrestarted clock (or an ignored second Shake() call) would show. Matching
        // the former and not the latter is what actually proves the restart reset the start time.
        var expectedSinceRestart = CosmicWin.Interop.Win32.VideoShakeMath.Compute(50, 1920, 1080);
        var expectedIfNotRestarted = CosmicWin.Interop.Win32.VideoShakeMath.Compute(130, 1920, 1080);
        Assert.Equal(CosmicWin.Interop.Win32.VideoShakeTransform.Identity.Dx, expectedIfNotRestarted.Dx);
        Assert.NotEqual(expectedIfNotRestarted.Dx, expectedSinceRestart.Dx);

        var actual = host.LastVideoTransform!.Value;
        Assert.Equal((float)expectedSinceRestart.Dx, actual.OffsetX, precision: 3);
    }

    [Theory]
    [InlineData(119)]
    [InlineData(120)]
    public void Shake_AtProductionDurationBoundary_NonIdentityJustBeforeClearsAtOrAfter(int elapsedMilliseconds)
    {
        var time = new ManualTimeProvider(Epoch);
        using var player = new CosmicWin.Interop.Win32.MediaFoundationVideoWallpaperPlayer(timeProvider: time);
        var host = new FakeVideoWallpaperHost { BackBufferSize = (1920, 1080) };

        // The production-duration expiry boundary: AppComposition.UpdateAlertOverlay always shakes
        // for exactly VideoShakeMath.DurationMilliseconds, so this is the one boundary production can
        // actually reach (a longer test duration, e.g. 230 ms, never exercises it).
        var duration = TimeSpan.FromMilliseconds(CosmicWin.Interop.Win32.VideoShakeMath.DurationMilliseconds);
        player.Shake(duration);
        time.Advance(TimeSpan.FromMilliseconds(elapsedMilliseconds));
        player.ApplyShakeForTests(host);

        if (elapsedMilliseconds < CosmicWin.Interop.Win32.VideoShakeMath.DurationMilliseconds)
        {
            var expected = CosmicWin.Interop.Win32.VideoShakeMath.Compute(elapsedMilliseconds, 1920, 1080);
            Assert.True(player.IsShakingForTests);
            Assert.Equal(1, host.SetVideoTransformCallCount);
            Assert.Equal(0, host.ClearVideoTransformCallCount);
            Assert.Equal((float)expected.Dx, host.LastVideoTransform!.Value.OffsetX, precision: 3);
        }
        else
        {
            Assert.False(player.IsShakingForTests);
            Assert.Equal(0, host.SetVideoTransformCallCount);
            Assert.Equal(1, host.ClearVideoTransformCallCount);
        }
    }
}
