using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CosmicWin.App.Alerts;
using CosmicWin.Interop.Win32;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// see-through-video-tint (S4): the host side of the letters-mask handshake. The WebView2 shell is a
/// hardware-only concern (see WebViewAlertLayerControllerTests), so the whole protocol -- per-show
/// sequence ids, PNG alpha decoding, SetTint/ClearTint, tint-ready -- lives in
/// <see cref="AlertTintCoordinator"/> and is driven here with a fake sink and a fake page transport.
/// </summary>
public sealed class AlertTintCoordinatorTests
{
    private sealed class FakeSink : IAlertTintSink
    {
        public readonly List<(byte[] Alpha, int Width, int Height, byte R, byte G, byte B)> Sets = [];
        public int Clears;
        private readonly object _gate = new();

        public event Action? TintRendered;

        public event Action? TintLost;

        public void RaiseRendered() => TintRendered?.Invoke();

        public void RaiseLost() => TintLost?.Invoke();

        public void SetTint(ReadOnlyMemory<byte> maskAlpha, int width, int height, byte r, byte g, byte b)
        {
            lock (_gate) Sets.Add((maskAlpha.ToArray(), width, height, r, g, b));
        }

        public void ClearTint() => Interlocked.Increment(ref Clears);
    }

    private sealed class Rig
    {
        public readonly FakeSink Sink = new();
        public readonly List<string> Posted = [];
        public readonly List<string> Traces = [];
        public readonly AlertTintCoordinator Coordinator;

        public Rig()
        {
            Coordinator = new AlertTintCoordinator(
                Sink, json => { lock (Posted) Posted.Add(json); }, line => { lock (Traces) Traces.Add(line); });
        }
    }

    /// <summary>A real PNG (WPF encoder): white letters with the given alpha per pixel, row-major.</summary>
    private static string PngDataUrl(int width, int height, byte[] alphas)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < alphas.Length; i++)
        {
            // Bgra32 is premultiplied-agnostic here: white at alpha a premultiplies to (a,a,a,a).
            pixels[i * 4] = alphas[i];
            pixels[i * 4 + 1] = alphas[i];
            pixels[i * 4 + 2] = alphas[i];
            pixels[i * 4 + 3] = alphas[i];
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private static string MaskJson(int seq, string kind, int width, int height, string png) =>
        JsonSerializer.Serialize(new { type = "mask", seq, kind, width, height, png });

    // ---- AlertMaskDecoder ------------------------------------------------------------------------

    [Fact]
    public void DecoderReturnsOnlyTheAlphaChannelRowMajorTopRowFirst()
    {
        byte[] alphas = [0, 255, 128, 64, 10, 20];
        var ok = AlertMaskDecoder.TryDecodeAlpha(PngDataUrl(3, 2, alphas), 3, 2, out var alpha);
        Assert.True(ok);
        Assert.Equal(alphas, alpha);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a data url")]
    [InlineData("data:image/png;base64,@@@@")]
    [InlineData("data:image/png;base64,TUFTSw==")] // valid base64, not a PNG
    [InlineData("data:image/jpeg;base64,TUFTSw==")]
    public void DecoderRejectsMalformedInputWithoutThrowing(string input)
    {
        Assert.False(AlertMaskDecoder.TryDecodeAlpha(input, 3, 2, out var alpha));
        Assert.Empty(alpha);
    }

    [Fact]
    public void DecoderRejectsAPngWhoseSizeDiffersFromTheDeclaredSize()
    {
        Assert.False(AlertMaskDecoder.TryDecodeAlpha(PngDataUrl(3, 2, new byte[6]), 4, 2, out _));
    }

    // ---- Message schema --------------------------------------------------------------------------

    [Fact]
    public void ShowTintedIsTheShowMessageWithTintAndSeq()
    {
        var request = new AlertShowRequest(["failed"], 1, 1, 8, 1000);
        var plain = AlertLayerMessages.Show(request);
        var tinted = AlertLayerMessages.ShowTinted(request, 7);
        Assert.Equal(plain[..^1] + ",\"tint\":true,\"seq\":7}", tinted);
        using var json = JsonDocument.Parse(tinted);
        Assert.True(json.RootElement.GetProperty("tint").GetBoolean());
        Assert.Equal(7, json.RootElement.GetProperty("seq").GetInt32());
        Assert.False(JsonDocument.Parse(plain).RootElement.TryGetProperty("tint", out _));
    }

    [Fact]
    public void TintReadyCarriesTheSeq()
    {
        Assert.Equal("{\"type\":\"tint-ready\",\"seq\":3}", AlertLayerMessages.TintReady(3));
    }

    // ---- Mask handling ---------------------------------------------------------------------------

    [Fact]
    public async Task FailedMaskSetsTheTintWithDecodedAlphaAndBlueThenPostsTintReady()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        byte[] alphas = [0, 255, 128, 64];

        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 2, 2, PngDataUrl(2, 2, alphas)));

        var set = Assert.Single(rig.Sink.Sets);
        Assert.Equal(alphas, set.Alpha);
        Assert.Equal((2, 2), (set.Width, set.Height));
        Assert.Equal((AlertTintColors.Failed.R, AlertTintColors.Failed.G, AlertTintColors.Failed.B), (set.R, set.G, set.B));
        Assert.Empty(rig.Posted); // requested is not rendered: the page keeps its letters

        rig.Sink.RaiseRendered();

        Assert.Equal([AlertLayerMessages.TintReady(seq)], rig.Posted);
    }

    [Fact]
    public void TintLostCarriesTheSeq()
    {
        Assert.Equal("{\"type\":\"tint-lost\",\"seq\":3}", AlertLayerMessages.TintLost(3));
    }

    [Fact]
    public void RenderedBeforeAnyMaskWasAppliedIsIgnored()
    {
        var rig = new Rig();
        rig.Coordinator.BeginShow();

        rig.Sink.RaiseRendered();

        Assert.Empty(rig.Posted);
    }

    [Fact]
    public async Task RenderedWithNoActiveShowIsIgnored()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255])));
        rig.Coordinator.Clear();

        rig.Sink.RaiseRendered();

        Assert.Empty(rig.Posted);
    }

    [Fact]
    public async Task RenderedTwiceAnnouncesOnce()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255])));

        rig.Sink.RaiseRendered();
        rig.Sink.RaiseRendered();

        Assert.Single(rig.Posted);
    }

    [Fact]
    public async Task LostAfterReadyPostsTintLost_AndALaterRenderedPostsTintReadyAgain()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255])));
        rig.Sink.RaiseRendered();

        rig.Sink.RaiseLost();
        rig.Sink.RaiseLost();
        rig.Sink.RaiseRendered();

        Assert.Equal(
            [AlertLayerMessages.TintReady(seq), AlertLayerMessages.TintLost(seq), AlertLayerMessages.TintReady(seq)],
            rig.Posted);
    }

    [Fact]
    public async Task LostBeforeTheTintWasEverAnnouncedPostsNothing()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255])));

        rig.Sink.RaiseLost();

        Assert.Empty(rig.Posted);
    }

    [Fact]
    public async Task ASeqZeroMaskWithNoShowActiveNeverTints()
    {
        var rig = new Rig();

        await rig.Coordinator.HandleMessageAsync(MaskJson(0, "failed", 1, 1, PngDataUrl(1, 1, [255])));
        var seq = rig.Coordinator.BeginShow();
        rig.Coordinator.Clear(); // the id is deactivated again
        await rig.Coordinator.HandleMessageAsync(MaskJson(0, "failed", 1, 1, PngDataUrl(1, 1, [255])));
        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255])));

        Assert.Empty(rig.Sink.Sets);
        Assert.Empty(rig.Posted);
        Assert.Contains(rig.Traces, line => line.Contains("no active show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMaskFailedMessageIsTracedAndNeverTints()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();

        await rig.Coordinator.HandleMessageAsync("{\"type\":\"mask-failed\",\"seq\":" + seq + "}");

        Assert.Empty(rig.Sink.Sets);
        Assert.Empty(rig.Posted);
        Assert.Contains(rig.Traces, line => line.Contains("mask-failed", StringComparison.Ordinal)
            && line.Contains($"seq={seq}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WarningMaskUsesTheVioletColor()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();

        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "warning", 1, 1, PngDataUrl(1, 1, [255])));

        var set = Assert.Single(rig.Sink.Sets);
        Assert.Equal((AlertTintColors.Warning.R, AlertTintColors.Warning.G, AlertTintColors.Warning.B), (set.R, set.G, set.B));
        Assert.NotEqual(AlertTintColors.Failed, AlertTintColors.Warning);
    }

    [Fact]
    public async Task AMaskFromAnEarlierShowIsIgnoredAfterANewShowBegan()
    {
        var rig = new Rig();
        var oldSeq = rig.Coordinator.BeginShow();
        var newSeq = rig.Coordinator.BeginShow();
        Assert.NotEqual(oldSeq, newSeq);

        await rig.Coordinator.HandleMessageAsync(MaskJson(oldSeq, "failed", 1, 1, PngDataUrl(1, 1, [255])));

        Assert.Empty(rig.Sink.Sets);
        Assert.Empty(rig.Posted);
    }

    [Fact]
    public async Task AMaskArrivingAfterClearIsIgnored()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        rig.Coordinator.Clear();

        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255])));

        Assert.Empty(rig.Sink.Sets);
        Assert.Empty(rig.Posted);
    }

    [Fact]
    public async Task OnlyTheFirstMaskOfAShowIsApplied()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        var json = MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255]));

        await rig.Coordinator.HandleMessageAsync(json);
        await rig.Coordinator.HandleMessageAsync(json);
        rig.Sink.RaiseRendered();

        Assert.Single(rig.Sink.Sets);
        Assert.Single(rig.Posted);
    }

    [Fact]
    public async Task ClearAfterAppliedMaskCallsClearTintAndBeginShowClearsToo()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        var afterBegin = rig.Sink.Clears;
        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255])));

        rig.Coordinator.Clear();
        Assert.Equal(afterBegin + 1, rig.Sink.Clears);

        rig.Coordinator.BeginShow();
        Assert.Equal(afterBegin + 2, rig.Sink.Clears);
    }

    [Fact]
    public async Task AClearedMaskCannotBeResurrectedByTheSameMessage()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        var json = MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255]));
        await rig.Coordinator.HandleMessageAsync(json);
        rig.Coordinator.Clear();

        await rig.Coordinator.HandleMessageAsync(json);

        Assert.Single(rig.Sink.Sets);
    }

    public static IEnumerable<object[]> MalformedMessages()
    {
        var png = PngDataUrl(1, 1, [255]);
        yield return ["not json"];
        yield return [""];
        yield return ["[1,2]"];
        yield return ["{\"type\":\"done\"}"];
        yield return ["{\"type\":\"mask\"}"];
        yield return [MaskJson(1, "bogus", 1, 1, png)];
        yield return [MaskJson(1, "failed", 0, 1, png)];
        yield return [MaskJson(1, "failed", 1, -1, png)];
        yield return [MaskJson(1, "failed", 100000, 100000, png)]; // oversized declared pixels
        yield return [MaskJson(1, "failed", 2, 1, png)]; // declared size differs from the PNG
        yield return [MaskJson(1, "failed", 1, 1, "data:image/png;base64,TUFTSw==")];
        yield return ["{\"type\":\"mask\",\"seq\":1,\"kind\":\"failed\",\"width\":1,\"height\":1,\"png\":42}"];
    }

    [Theory]
    [MemberData(nameof(MalformedMessages))]
    public async Task MalformedMasksAreTracedAndNeverTintOrThrow(string json)
    {
        var rig = new Rig();
        rig.Coordinator.BeginShow(); // seq 1 is the active one

        await rig.Coordinator.HandleMessageAsync(json);

        Assert.Empty(rig.Sink.Sets);
        Assert.Empty(rig.Posted);
    }

    [Fact]
    public async Task MalformedMaskOfTheActiveShowIsTraced()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();

        await rig.Coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, "data:image/png;base64,TUFTSw=="));

        Assert.Contains(rig.Traces, line => line.StartsWith("alert-layer tint rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOversizedMessageIsRejectedBeforeParsing()
    {
        var rig = new Rig();
        var seq = rig.Coordinator.BeginShow();
        var huge = MaskJson(seq, "failed", 1, 1, "data:image/png;base64," + new string('A', AlertTintCoordinator.MaxMessageChars));

        await rig.Coordinator.HandleMessageAsync(huge);

        Assert.Empty(rig.Sink.Sets);
        Assert.Contains(rig.Traces, line => line.Contains("too large", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASinkThatThrowsIsTracedAndDoesNotPostTintReady()
    {
        var posted = new List<string>();
        var traces = new List<string>();
        var coordinator = new AlertTintCoordinator(new ThrowingSink(), posted.Add, traces.Add);
        var seq = coordinator.BeginShow();

        await coordinator.HandleMessageAsync(MaskJson(seq, "failed", 1, 1, PngDataUrl(1, 1, [255])));

        Assert.Empty(posted);
        Assert.Contains(traces, line => line.StartsWith("alert-layer error tint", StringComparison.Ordinal));
    }

    private sealed class ThrowingSink : IAlertTintSink
    {
        public event Action? TintRendered
        {
            add { }
            remove { }
        }

        public event Action? TintLost
        {
            add { }
            remove { }
        }

        public void SetTint(ReadOnlyMemory<byte> maskAlpha, int width, int height, byte r, byte g, byte b) =>
            throw new InvalidOperationException("boom");

        public void ClearTint()
        {
        }
    }

    // ---- Controller wiring (host unattached: no WebView2 is ever created) -------------------------

    private static void OnStaWithDispatcher(Action<Win32VideoWallpaperHost> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                using var host = new Win32VideoWallpaperHost();
                body(host);
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
    }

    [Fact]
    public void ControllerClearsTheTintOnStartEndAndDispose()
    {
        var sink = new FakeSink();
        var snapshots = new List<int>();
        OnStaWithDispatcher(host =>
        {
            using (var layer = new WebViewAlertLayerController(host, tintSink: sink))
            {
                layer.Preload();
                snapshots.Add(sink.Clears);
                layer.Start(new AlertShowRequest(["failed"], 1, 1, 8, 1000));
                snapshots.Add(sink.Clears);
                layer.End();
                snapshots.Add(sink.Clears);
            }

            snapshots.Add(sink.Clears);
        });

        Assert.True(snapshots[1] > snapshots[0], "a new show clears the previous tint before its reveal");
        Assert.True(snapshots[2] > snapshots[1], "hide clears the tint");
        Assert.True(snapshots[3] > snapshots[2], "dispose clears the tint");
        Assert.Empty(sink.Sets);
    }

    [Fact]
    public void ControllerWithoutASinkNeverTouchesOne()
    {
        OnStaWithDispatcher(host =>
        {
            using var layer = new WebViewAlertLayerController(host);
            layer.Preload();
            layer.Start(new AlertShowRequest(["failed"], 1, 1, 8, 1000));
            layer.End();
        });
    }

    private static string ReadControllerSource([CallerFilePath] string testFilePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!,
            "..", "..", "CosmicWin.App", "Alerts", "WebViewAlertLayerController.cs")));

    [Fact]
    public void ControllerClearsTheTintWhenTheSceneReloads()
    {
        var source = ReadControllerSource();
        // SwitchScene (PageReloading) and TearDown (host change, process failure, dispose) both clear.
        foreach (var marker in new[] { "_state.PageReloading();", "private void TearDown(string reason" })
        {
            var start = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, marker);
            var window = source[start..Math.Min(source.Length, start + 700)];
            Assert.Contains("_tint?.Clear()", window);
        }
    }
}
