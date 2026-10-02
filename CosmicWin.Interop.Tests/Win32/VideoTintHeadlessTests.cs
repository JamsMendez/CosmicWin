using CosmicWin.Interop.Win32;
using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.Interop.Tests.Win32;

public sealed class VideoTintMatrixTests
{
    [Fact]
    public void Create_PlacesLumaTimesTintInEveryColourColumn_AndForcesAlphaOne()
    {
        float[] m = VideoTintMatrix.Create(0.2f, 0.5f, 1.0f);

        float[] expected =
        [
            0.2126f * 0.2f, 0.2126f * 0.5f, 0.2126f * 1.0f, 0f,
            0.7152f * 0.2f, 0.7152f * 0.5f, 0.7152f * 1.0f, 0f,
            0.0722f * 0.2f, 0.0722f * 0.5f, 0.0722f * 1.0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 1f,
        ];
        Assert.Equal(expected, m);
    }

    [Fact]
    public void Create_ByteOverload_ScalesTo0To1()
    {
        float[] m = VideoTintMatrix.Create((byte)255, (byte)0, (byte)51);

        Assert.Equal(VideoTintMatrix.Create(1f, 0f, 0.2f), m);
    }

    [Fact]
    public void Create_WhiteInput_YieldsTheTintColour()
    {
        float[] m = VideoTintMatrix.Create(0.2f, 0.45f, 1f);
        float[] tint = [0.2f, 0.45f, 1f];

        // [1 1 1 1 1] * M, column by column.
        for (int col = 0; col < 3; col++)
        {
            float sum = m[col] + m[4 + col] + m[8 + col] + m[12 + col] + m[16 + col];
            Assert.Equal(tint[col], sum, precision: 5);
        }

        Assert.Equal(1f, m[3] + m[7] + m[11] + m[15] + m[19]);
    }

    [Fact]
    public void ToBytes_IsTheRawFloats()
    {
        byte[] bytes = VideoTintMatrix.ToBytes(VideoTintMatrix.Create(1f, 1f, 1f));

        Assert.Equal(80, bytes.Length);
        Assert.Equal(0.2126f, BitConverter.ToSingle(bytes, 0));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 76));
    }
}

public sealed class VideoTintMaskResamplerTests
{
    [Fact]
    public void SameSize_ReturnsTheSameArray()
    {
        byte[] src = [1, 2, 3, 4];

        Assert.Same(src, VideoTintMaskResampler.Resample(src, 2, 2, 2, 2));
    }

    [Fact]
    public void Upscale_RepeatsNearestPixels()
    {
        byte[] src = [10, 20, 30, 40]; // 2x2

        byte[] up = VideoTintMaskResampler.Resample(src, 2, 2, 4, 4);

        Assert.Equal(
            new byte[] { 10, 10, 20, 20, 10, 10, 20, 20, 30, 30, 40, 40, 30, 30, 40, 40 }, up);
    }

    [Fact]
    public void Downscale_PicksNearestPixels()
    {
        byte[] src = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]; // 4x4

        byte[] down = VideoTintMaskResampler.Resample(src, 4, 4, 2, 2);

        Assert.Equal(new byte[] { 1, 3, 9, 11 }, down);
    }

    [Fact]
    public void NonUniformScale_MapsRowsAndColumnsIndependently()
    {
        byte[] src = [1, 2]; // 2x1

        byte[] r = VideoTintMaskResampler.Resample(src, 2, 1, 4, 2);

        Assert.Equal(new byte[] { 1, 1, 2, 2, 1, 1, 2, 2 }, r);
    }
}

public sealed class VideoTintRequestTests
{
    [Fact]
    public void Constructor_CopiesTheMask()
    {
        byte[] mask = [255, 0];
        var request = new VideoTintRequest(mask, 2, 1, 1, 2, 3);
        mask[0] = 7;

        Assert.Equal(new byte[] { 255, 0 }, request.MaskAlpha);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(2, 2, 3)]
    public void Constructor_RejectsBadSizes(int w, int h, int length)
    {
        Assert.Throws<ArgumentException>(() => new VideoTintRequest(new byte[length], w, h, 0, 0, 0));
    }
}

public sealed class VideoTintDriverTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeRenderer : IVideoTintRenderer
    {
        public bool PrepareResult = true;
        public bool PrepareThrows;
        public VideoTintRenderResult RenderResult = VideoTintRenderResult.Ok;
        public bool RenderThrows;
        public VideoTintRequest? LastRequest;
        public int PrepareCalls;
        public int RenderCalls;
        public bool Disposed;

        public ID3D11Texture2D? Intermediate => null;

        public bool Prepare(ID3D11Device device, ID3D11Texture2D backBuffer, in D3D11_TEXTURE2D_DESC desc, VideoTintRequest request)
        {
            PrepareCalls++;
            LastRequest = request;
            return PrepareThrows ? throw new InvalidOperationException() : PrepareResult;
        }

        public VideoTintRenderResult Render()
        {
            RenderCalls++;
            return RenderThrows ? throw new InvalidOperationException() : RenderResult;
        }

        public void Dispose() => Disposed = true;
    }

    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(5);

    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeRenderer> _renderers = [];
    private readonly List<string> _diagnostics = [];
    private Action<string>? _diagnosticSink;
    private Func<FakeRenderer>? _customFactory;
    private int _factoryCalls;

    private VideoTintDriver NewDriver() =>
        new(_time, () =>
        {
            _factoryCalls++;
            FakeRenderer r = _customFactory?.Invoke() ?? new FakeRenderer();
            _renderers.Add(r);
            return r;
        }, Backoff, _diagnosticSink ?? _diagnostics.Add);

    private static VideoTintRequest Request(byte seed = 255) => new([seed], 1, 1, 1, 2, 3);

    private static bool Begin(VideoTintDriver d) =>
        d.TryBegin(null!, null!, default(D3D11_TEXTURE2D_DESC), out _);

    [Fact]
    public void NeverSet_IsNotEngaged_AndCreatesNoRenderer()
    {
        using var d = NewDriver();

        Assert.False(d.IsEngaged);
        Assert.False(Begin(d));
        Assert.Equal(0, _factoryCalls);
    }

    [Fact]
    public void Set_EngagesAndRendersThroughTheRenderer()
    {
        using var d = NewDriver();

        d.Set(Request());

        Assert.True(d.IsEngaged);
        Assert.True(Begin(d));
        Assert.True(d.Complete());
        Assert.Equal(1, _renderers[0].RenderCalls);
    }

    [Fact]
    public void Clear_DisposesTheRenderer_AndDisengages()
    {
        using var d = NewDriver();
        d.Set(Request());
        Begin(d);

        d.Clear();

        Assert.False(Begin(d));
        Assert.True(_renderers[0].Disposed);
        Assert.False(d.IsEngaged);
    }

    [Fact]
    public void LatestRequestWins()
    {
        using var d = NewDriver();
        VideoTintRequest first = Request(1), second = Request(2);

        d.Set(first);
        d.Set(second);
        Begin(d);

        Assert.Same(second, _renderers[0].LastRequest);
    }

    [Fact]
    public void RendererIsReusedAcrossTicks()
    {
        using var d = NewDriver();
        d.Set(Request());

        Begin(d);
        Begin(d);

        Assert.Equal(1, _factoryCalls);
        Assert.Equal(2, _renderers[0].PrepareCalls);
    }

    [Fact]
    public void FactoryFailure_FallsBack_AndBacksOffUntilTheDelayElapses()
    {
        using var d = NewDriver();
        d.Set(Request());
        var failing = true;
        _customFactory = () => failing ? throw new InvalidOperationException() : new FakeRenderer();

        Assert.False(Begin(d));
        Assert.Equal(1, _factoryCalls);

        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(Begin(d));
        Assert.Equal(1, _factoryCalls);

        _time.Advance(TimeSpan.FromSeconds(1));
        failing = false;
        Assert.True(Begin(d));
        Assert.Equal(2, _factoryCalls);
    }

    [Fact]
    public void PrepareFailure_DisposesRenderer_AndBacksOff()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { PrepareResult = false };

        Assert.False(Begin(d));
        Assert.True(_renderers[0].Disposed);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(Begin(d));
        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public void PrepareThrowing_NeverEscapes()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { PrepareThrows = true };

        Assert.False(Begin(d));
        Assert.True(_renderers[0].Disposed);
    }

    [Fact]
    public void RenderFailure_ReturnsFalse_DisposesRenderer_AndBacksOff()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderResult = VideoTintRenderResult.Failed };
        Begin(d);

        Assert.False(d.Complete());
        Assert.True(_renderers[0].Disposed);
        Assert.False(Begin(d));
        Assert.Equal(1, _factoryCalls);
    }

    [Fact]
    public void RenderThrowing_NeverEscapes()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderThrows = true };
        Begin(d);

        Assert.False(d.Complete());
        Assert.True(_renderers[0].Disposed);
    }

    [Fact]
    public void RecreateTarget_TearsDown_AndRebuildsOnTheNextTickWithoutBackoff()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderResult = VideoTintRenderResult.RecreateTarget };
        Begin(d);

        Assert.False(d.Complete());
        Assert.True(_renderers[0].Disposed);

        Assert.True(Begin(d));
        Assert.Equal(2, _factoryCalls);
    }

    [Fact]
    public void RecreateTarget_RepeatingWithinTheBackoffWindow_BacksOffInsteadOfRebuildingEveryTick()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderResult = VideoTintRenderResult.RecreateTarget };
        Begin(d);
        Assert.False(d.Complete()); // first loss: one immediate rebuild is allowed
        Assert.True(Begin(d));
        Assert.Equal(2, _factoryCalls);

        Assert.False(d.Complete()); // lost again right away: same back-off as any other failure

        Assert.True(_renderers[1].Disposed);
        Assert.False(Begin(d));
        Assert.Equal(2, _factoryCalls);
        _time.Advance(Backoff);
        Assert.True(Begin(d));
        Assert.Equal(3, _factoryCalls);
    }

    [Fact]
    public void RecreateTarget_AfterTheBackoffWindowElapsed_AllowsAnotherImmediateRebuild()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderResult = VideoTintRenderResult.RecreateTarget };
        Begin(d);
        Assert.False(d.Complete());
        _time.Advance(Backoff);

        Assert.True(Begin(d));
        Assert.False(d.Complete());

        Assert.True(Begin(d)); // an isolated device loss never costs the back-off
        Assert.Equal(3, _factoryCalls);
    }

    private readonly List<string> _events = [];

    private VideoTintDriver NewObservedDriver()
    {
        VideoTintDriver d = NewDriver();
        d.Rendered += () => _events.Add("rendered");
        d.Lost += () => _events.Add("lost");
        return d;
    }

    /// <summary>
    /// R3-releasegpu-drops-announced-without-lost / R4-releasegpu-swallows-lost-signal: playback
    /// stopping (worker exit, e.g. a video re-pick) while a tint was ANNOUNCED takes the tint off the
    /// screen, so the page must hear Lost and paint its letters again -- otherwise the alert shows no
    /// letters at all.
    /// </summary>
    [Fact]
    public void ReleaseGpu_AfterTheTintWasAnnounced_RaisesLost()
    {
        using var d = NewObservedDriver();
        d.Set(Request());
        Begin(d);
        d.Complete();

        d.ReleaseGpu();

        Assert.Equal(["rendered", "lost"], _events);
    }

    [Fact]
    public void ReleaseGpu_BeforeAnyAnnouncement_RaisesNothing()
    {
        using var d = NewObservedDriver();
        d.Set(Request());

        d.ReleaseGpu();

        Assert.Empty(_events);
    }

    [Fact]
    public void Rendered_IsRaisedOnceOnTheFirstSuccessfulFrame()
    {
        using var d = NewObservedDriver();
        d.Set(Request());

        Begin(d);
        d.Complete();
        Begin(d);
        d.Complete();

        Assert.Equal(["rendered"], _events);
    }

    [Fact]
    public void Rendered_IsNotRaisedWhenTheFrameFails()
    {
        using var d = NewObservedDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderResult = VideoTintRenderResult.Failed };

        Begin(d);
        d.Complete();

        Assert.Empty(_events);
    }

    [Fact]
    public void Lost_IsRaisedWhenARenderingTintBacksOff_AndRenderedAgainOnRecovery()
    {
        using var d = NewObservedDriver();
        d.Set(Request());
        Begin(d);
        d.Complete();
        _renderers[0].RenderResult = VideoTintRenderResult.Failed;
        Begin(d);
        d.Complete();
        Begin(d); // inside the window: nothing new
        _time.Advance(Backoff);
        Begin(d);
        d.Complete();

        Assert.Equal(["rendered", "lost", "rendered"], _events);
    }

    [Fact]
    public void Lost_IsNotRaised_WhenTheTintNeverRendered()
    {
        using var d = NewObservedDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { PrepareResult = false };

        Begin(d);

        Assert.Empty(_events);
    }

    [Fact]
    public void Lost_IsNotRaised_ForTheSingleImmediateRecreateTarget()
    {
        using var d = NewObservedDriver();
        d.Set(Request());
        Begin(d);
        d.Complete();
        _renderers[0].RenderResult = VideoTintRenderResult.RecreateTarget;
        Begin(d);
        d.Complete();
        Begin(d);
        d.Complete();

        Assert.Equal(["rendered"], _events);
    }

    [Fact]
    public void Rendered_IsRaisedAgainForANewRequest()
    {
        using var d = NewObservedDriver();
        d.Set(Request());
        Begin(d);
        d.Complete();

        d.Set(Request(1));
        Begin(d);
        d.Complete();

        Assert.Equal(["rendered", "rendered"], _events);
    }

    [Fact]
    public void Rendered_IsNotRaised_ForARequestThatWasClearedOrReplacedMeanwhile()
    {
        using var d = NewObservedDriver();
        d.Set(Request());
        Begin(d);
        d.Clear();
        d.Complete();
        d.Set(Request());
        Begin(d);
        d.Set(Request(1)); // replaced between the transfer and the compose
        d.Complete();

        Assert.Empty(_events);
    }

    [Fact]
    public void Events_AHandlerThatThrows_NeverEscapes()
    {
        using var d = NewDriver();
        d.Rendered += () => throw new InvalidOperationException();
        d.Set(Request());
        Begin(d);

        Assert.True(d.Complete());
    }

    [Fact]
    public void Diagnostics_NeverReported_WhenNothingFails()
    {
        using var d = NewDriver();
        d.Set(Request());

        Begin(d);
        d.Complete();

        Assert.Empty(_diagnostics);
    }

    [Fact]
    public void Diagnostics_ReportTheFirstFailureOnce_WithHResultAndExceptionTypeOnly()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderThrows = true };
        Begin(d);

        d.Complete();
        Begin(d); // inside the window: skipped, silent
        Begin(d);

        string line = Assert.Single(_diagnostics);
        Assert.Equal("video-tint failed hr=0x80131509 type=InvalidOperationException backoff=5s", line);
    }

    [Fact]
    public void Diagnostics_ResultFailures_NameTheResult()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderResult = VideoTintRenderResult.Failed };
        Begin(d);

        d.Complete();

        Assert.Equal("video-tint failed result=Failed backoff=5s", Assert.Single(_diagnostics));
    }

    [Fact]
    public void Diagnostics_AbortWithAnException_ReportsIt()
    {
        using var d = NewDriver();
        d.Set(Request());
        Begin(d);

        d.Abort(new InvalidOperationException("secret detail"));

        string line = Assert.Single(_diagnostics);
        Assert.Equal("video-tint failed hr=0x80131509 type=InvalidOperationException backoff=5s", line);
        Assert.DoesNotContain("secret", line);
    }

    [Fact]
    public void Diagnostics_RetryAfterTheWindow_ThenRecovered()
    {
        using var d = NewDriver();
        d.Set(Request());
        int calls = 0;
        _customFactory = () => new FakeRenderer { PrepareResult = ++calls > 1 };
        Assert.False(Begin(d));
        _time.Advance(Backoff);

        Assert.True(Begin(d));
        Assert.True(d.Complete());
        Assert.True(Begin(d));
        Assert.True(d.Complete()); // a second success is not news

        Assert.Equal(
            ["video-tint failed result=PrepareFalse backoff=5s", "video-tint retry", "video-tint recovered"],
            _diagnostics);
    }

    [Fact]
    public void Diagnostics_ASecondWindowReportsAgain()
    {
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { PrepareResult = false };
        Begin(d);
        _time.Advance(Backoff);

        Begin(d);

        Assert.Equal(
            ["video-tint failed result=PrepareFalse backoff=5s", "video-tint retry", "video-tint failed result=PrepareFalse backoff=5s"],
            _diagnostics);
    }

    [Fact]
    public void Diagnostics_ASinkThatThrows_NeverEscapes()
    {
        _diagnosticSink = _ => throw new InvalidOperationException();
        using var d = NewDriver();
        d.Set(Request());
        _customFactory = () => new FakeRenderer { RenderThrows = true };
        Begin(d);

        Assert.False(d.Complete());
        Assert.False(Begin(d));
    }

    [Fact]
    public void Abort_DisposesTheRenderer_AndBacksOff()
    {
        using var d = NewDriver();
        d.Set(Request());
        Begin(d);

        d.Abort();

        Assert.True(_renderers[0].Disposed);
        Assert.False(Begin(d));
        Assert.Equal(1, _factoryCalls);
        _time.Advance(Backoff);
        Assert.True(Begin(d));
        Assert.Equal(2, _factoryCalls);
    }

    [Fact]
    public void ReleaseGpu_DropsTheRenderer_ButKeepsTheRequest()
    {
        using var d = NewDriver();
        var request = Request();
        d.Set(request);
        Begin(d);

        d.ReleaseGpu();

        Assert.True(_renderers[0].Disposed);
        Assert.True(d.IsEngaged);
        Assert.True(Begin(d));
        Assert.Same(request, _renderers[1].LastRequest);
    }

    [Fact]
    public void Dispose_ReleasesTheRenderer()
    {
        var d = NewDriver();
        d.Set(Request());
        Begin(d);

        d.Dispose();

        Assert.True(_renderers[0].Disposed);
    }
}

public sealed class MediaFoundationVideoWallpaperPlayerTintTests
{
    [Fact]
    public void NewPlayer_HasNoTint()
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();

        Assert.False(player.IsTintRequestedForTests);
    }

    [Fact]
    public void SetTint_Requests_AndClearTint_Removes()
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();

        player.SetTint(new byte[] { 255, 0, 0, 255 }, 2, 2, 51, 115, 255);
        Assert.True(player.IsTintRequestedForTests);

        player.ClearTint();
        Assert.False(player.IsTintRequestedForTests);
    }

    [Fact]
    public void SetTint_RejectsAMaskThatDoesNotMatchItsSize()
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();

        Assert.Throws<ArgumentException>(() => player.SetTint(new byte[3], 2, 2, 0, 0, 0));
        Assert.False(player.IsTintRequestedForTests);
    }

    [Fact]
    public void SetTint_FromManyThreads_NeverThrows_AndEndsRequested()
    {
        using var player = new MediaFoundationVideoWallpaperPlayer();

        Parallel.For(0, 200, i =>
        {
            if (i % 3 == 0)
            {
                player.ClearTint();
            }
            else
            {
                player.SetTint(new byte[] { 1 }, 1, 1, (byte)i, 0, 0);
            }
        });
        player.SetTint(new byte[] { 1 }, 1, 1, 0, 0, 0);

        Assert.True(player.IsTintRequestedForTests);
    }
}

public sealed class TintedFrameTransferTests
{
    private readonly List<string> _calls = [];

    private void Run(bool begin, bool beginThrows = false, bool transferThrows = false, bool complete = true)
    {
        TintedFrameTransfer.Run(
            tryBegin: () => { _calls.Add("begin"); return beginThrows ? throw new InvalidOperationException() : begin; },
            transferIntoTarget: () => { _calls.Add("target"); if (transferThrows) throw new InvalidOperationException(); },
            complete: () => { _calls.Add("complete"); return complete; },
            abort: _ => _calls.Add("abort"),
            transferPlain: () => _calls.Add("plain"));
    }

    [Fact]
    public void TryBeginFalse_TransfersPlainOnce()
    {
        Run(begin: false);

        Assert.Equal(["begin", "plain"], _calls);
    }

    [Fact]
    public void TransferIntoTargetThrowing_AbortsThenTransfersPlain()
    {
        Run(begin: true, transferThrows: true);

        Assert.Equal(["begin", "target", "abort", "plain"], _calls);
    }

    [Fact]
    public void TryBeginThrowing_AbortsThenTransfersPlain()
    {
        Run(begin: true, beginThrows: true);

        Assert.Equal(["begin", "abort", "plain"], _calls);
    }

    [Fact]
    public void CompleteFalse_TransfersPlainOnce_WithoutAborting()
    {
        Run(begin: true, complete: false);

        Assert.Equal(["begin", "target", "complete", "plain"], _calls);
    }

    [Fact]
    public void Success_NeverTransfersPlain()
    {
        Run(begin: true);

        Assert.Equal(["begin", "target", "complete"], _calls);
    }
}
