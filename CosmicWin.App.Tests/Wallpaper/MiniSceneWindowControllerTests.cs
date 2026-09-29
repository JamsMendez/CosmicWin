using CosmicWin.App.Alerts;
using CosmicWin.App.Wallpaper;
using CosmicWin.Interop;
using CosmicWin.Layout;
using DrawingRectangle = System.Drawing.Rectangle;
using InteropRectangle = CosmicWin.Interop.Rectangle;

namespace CosmicWin.App.Tests.Wallpaper;

public sealed class MiniSceneWindowControllerTests
{
    private static readonly Rect Corner = new(2272, 0, 288, 288);

    private sealed class FakeSurface : IMiniSceneSurface
    {
        public bool CreateResult = true;
        public InteropRectangle? Created;
        public List<InteropRectangle> Placed { get; } = [];
        public int DisposeCount;
        public nint Hwnd => 42;
        public bool IsCompositionReady => true;
        public int CompositionGeneration => 1;

        public bool TryCreate(InteropRectangle bounds)
        {
            Created = bounds;
            return CreateResult;
        }

        public bool Place(InteropRectangle bounds)
        {
            Placed.Add(bounds);
            return true;
        }

        public object? AddCompositionOverlayVisual() => new object();
        public void RemoveCompositionOverlayVisual() { }
        public void CommitComposition() { }
        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeBrowser : IMiniSceneBrowser
    {
        public TaskCompletionSource<bool> AttachResult { get; } = new();
        public DrawingRectangle? AttachedViewport;
        public List<string> Navigations { get; } = [];
        public List<DrawingRectangle> Resizes { get; } = [];
        public List<string> Messages { get; } = [];
        public int DisposeCount;
        public event Action? Ready;

        public Task<bool> AttachAsync(ICompositionOverlaySurface surface, DrawingRectangle viewport)
        {
            AttachedViewport = viewport;
            return AttachResult.Task;
        }

        public void Navigate(string url) => Navigations.Add(url);
        public void Resize(DrawingRectangle viewport) => Resizes.Add(viewport);
        public void PostMessage(string json) => Messages.Add(json);
        public void Dispose() => DisposeCount++;
        public void RaiseReady() => Ready?.Invoke();
    }

    private static (MiniSceneWindowController Controller, FakeSurface Surface, FakeBrowser Browser) Create()
    {
        var surface = new FakeSurface();
        var browser = new FakeBrowser();
        return (new MiniSceneWindowController(() => surface, browser), surface, browser);
    }

    private static AlertShowRequest Alert() =>
        new(["failed"], 1, 1, 8, 3000, WorkAreaLeft: 100, WorkAreaTop: 50, WorkAreaWidth: 900, WorkAreaHeight: 700);

    [Fact]
    public void SceneUrlCarriesTheMiniVariantAndKeepsTheFullUrlUnchanged()
    {
        Assert.Equal(
            "https://cosmicwin-scene.example/processing/index.html?fps=30&variant=mini",
            WebViewAlertLayerController.SceneUrl(WallpaperScene.Processing, 30, "mini"));
        Assert.Equal(
            "https://cosmicwin-scene.example/raphael/index.html?fps=60",
            WebViewAlertLayerController.SceneUrl(WallpaperScene.Raphael, 60));
    }

    [Fact]
    public void ShowCreatesTheSurfaceAtTheBoundsAttachesASquareViewportThenNavigatesToTheMiniPage()
    {
        var (controller, surface, browser) = Create();

        Assert.True(controller.Show(WallpaperScene.Processing, 30, Corner));

        Assert.Equal(InteropRectangle.FromSize(2272, 0, 288, 288), surface.Created);
        Assert.Equal(new DrawingRectangle(0, 0, 288, 288), browser.AttachedViewport);
        Assert.Empty(browser.Navigations);

        browser.AttachResult.SetResult(true);

        Assert.Equal(
            ["https://cosmicwin-scene.example/processing/index.html?fps=30&variant=mini"], browser.Navigations);
    }

    [Fact]
    public void ShowMakesTheCreatedWindowVisibleThroughPlace()
    {
        var (controller, surface, _) = Create();

        controller.Show(WallpaperScene.Processing, 30, Corner);

        Assert.Equal([InteropRectangle.FromSize(2272, 0, 288, 288)], surface.Placed);
    }

    [Fact]
    public void ShowReportsFailureAndNeverAttachesWhenTheSurfaceCannotBeCreated()
    {
        var (controller, surface, browser) = Create();
        surface.CreateResult = false;

        Assert.False(controller.Show(WallpaperScene.Idle, 60, Corner));

        Assert.Null(browser.AttachedViewport);
        Assert.Equal(1, surface.DisposeCount);
    }

    [Fact]
    public void FailedBrowserAttachNeverNavigates()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Idle, 60, Corner);

        browser.AttachResult.SetResult(false);

        Assert.Empty(browser.Navigations);
    }

    [Fact]
    public void SwitchingToTheCurrentSceneIsAnAcceptedNoOp()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);
        browser.AttachResult.SetResult(true);

        Assert.True(controller.SwitchScene(WallpaperScene.Processing));

        Assert.Single(browser.Navigations);
    }

    [Fact]
    public void SwitchingToAnotherSceneNavigatesToItsMiniPage()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);
        browser.AttachResult.SetResult(true);

        Assert.True(controller.SwitchScene(WallpaperScene.Explorer));

        Assert.Equal(
            "https://cosmicwin-scene.example/explorer/index.html?fps=30&variant=mini", browser.Navigations[^1]);
    }

    [Fact]
    public void SwitchingBeforeTheBrowserIsAttachedOnlyRecordsTheSceneForTheFirstNavigation()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);

        Assert.True(controller.SwitchScene(WallpaperScene.Raphael));
        Assert.Empty(browser.Navigations);

        browser.AttachResult.SetResult(true);

        Assert.Equal(
            ["https://cosmicwin-scene.example/raphael/index.html?fps=30&variant=mini"], browser.Navigations);
    }

    [Fact]
    public void MoveToPlacesTheSurfaceAndOnlyResizesTheBrowserWhenTheSizeChanged()
    {
        var (controller, surface, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);
        browser.AttachResult.SetResult(true);
        surface.Placed.Clear();

        controller.MoveTo(new Rect(0, 40, 288, 288));
        controller.MoveTo(new Rect(0, 40, 200, 200));

        Assert.Equal(
            [InteropRectangle.FromSize(0, 40, 288, 288), InteropRectangle.FromSize(0, 40, 200, 200)],
            surface.Placed);
        Assert.Equal([new DrawingRectangle(0, 0, 200, 200)], browser.Resizes);
    }

    [Fact]
    public void ViewportIsAlwaysSquareAndMatchesThePhysicalWindowSide()
    {
        Assert.Equal(new DrawingRectangle(0, 0, 288, 288), MiniSceneWindowController.ViewportFor(Corner));
        Assert.Equal(
            new DrawingRectangle(0, 0, 200, 200), MiniSceneWindowController.ViewportFor(new Rect(0, 0, 300, 200)));
        Assert.Equal(new DrawingRectangle(0, 0, 0, 0), MiniSceneWindowController.ViewportFor(new Rect(0, 0, -5, 10)));
    }

    [Fact]
    public void DisposeIsIdempotentAndReleasesTheBrowserAndSurfaceOnce()
    {
        var (controller, surface, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);

        controller.Dispose();
        controller.Dispose();

        Assert.Equal(1, browser.DisposeCount);
        Assert.Equal(1, surface.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => controller.MoveTo(Corner));
    }

    [Fact]
    public void AttachCompletingAfterDisposeNeverNavigates()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);
        controller.Dispose();

        browser.AttachResult.SetResult(true);

        Assert.Empty(browser.Navigations);
    }

    [Fact]
    public void AlertShownBeforeThePageIsReadyIsPostedOnReadyWithTheWholeCanvasAsWorkArea()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);
        browser.AttachResult.SetResult(true);

        controller.ShowAlert(Alert());
        Assert.Empty(browser.Messages);

        browser.RaiseReady();

        var message = Assert.Single(browser.Messages);
        Assert.Contains("\"type\":\"show\"", message);
        Assert.Contains("\"workArea\":{\"left\":0,\"top\":0,\"width\":0,\"height\":0}", message);
    }

    [Fact]
    public void AlertShownOnceReadyIsPostedImmediatelyAndHideClearsAPendingShow()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);
        browser.AttachResult.SetResult(true);
        browser.RaiseReady();

        controller.ShowAlert(Alert());
        controller.HideAlert();

        Assert.Equal(2, browser.Messages.Count);
        Assert.Equal("{\"type\":\"hide\"}", browser.Messages[1]);
    }

    [Fact]
    public void HideBeforeReadyDropsThePendingShow()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);
        browser.AttachResult.SetResult(true);

        controller.ShowAlert(Alert());
        controller.HideAlert();
        browser.RaiseReady();

        Assert.DoesNotContain(browser.Messages, m => m.Contains("\"type\":\"show\""));
    }

    [Fact]
    public void SceneSwitchMakesThePageNotReadyUntilItReportsReadyAgain()
    {
        var (controller, _, browser) = Create();
        controller.Show(WallpaperScene.Processing, 30, Corner);
        browser.AttachResult.SetResult(true);
        browser.RaiseReady();

        controller.SwitchScene(WallpaperScene.Idle);
        controller.ShowAlert(Alert());
        Assert.Empty(browser.Messages);

        browser.RaiseReady();
        Assert.Single(browser.Messages);
    }

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
