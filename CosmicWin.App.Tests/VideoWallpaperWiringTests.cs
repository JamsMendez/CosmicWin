using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// T5's own scope: the tray hands the raw picked path to the injected import delegate, and the
/// delegate's RETURN VALUE (the copied, on-disk path) is what gets persisted -- never the raw one.
/// Starting or restarting real playback is T6, deliberately not exercised here.
/// </summary>
public sealed class VideoWallpaperWiringTests
{
    private sealed class NoForeground : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed record Harness(AppComposition Composition, TrayMenuController Tray);

    private static Harness Wire(
        Func<string, string>? importVideoWallpaper = null, Action<string>? persistVideoWallpaperPath = null)
    {
        var workspace = new FakeWorkspace();
        var primary = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var registry = new WindowRegistry();
        var treeManager = new TreeManager([primary], primary, registry);
        var foreground = new NoForeground();
        TrayMenuController? tray = null;

        var composition = AppComposition.Wire(
            workspace, treeManager, registry, foreground, new ExceptionListStore(ExceptionList.Empty),
            focusTrace: new RecordingFocusTrace(),
            disableTaskTrigger: () => { },
            scheduleReconcile: (_, _) => new NullDisposable(),
            hookFactory: writer => new LowLevelKeyboardHook(
                writer, new FakeKeyboardHookPlatform(), TimeSpan.FromSeconds(5), () => 0),
            loadExceptions: () => ExceptionList.Empty,
            shutdown: () => { },
            buildTray: controller =>
            {
                tray = controller;
                return new NullDisposable();
            },
            importVideoWallpaper: importVideoWallpaper ?? (path => path),
            persistVideoWallpaperPath: persistVideoWallpaperPath);

        return new Harness(composition, tray!);
    }

    [Fact]
    public void PickingAVideo_HandsTheRawPickedPath_ToTheImportDelegate()
    {
        var imported = new List<string>();
        var harness = Wire(importVideoWallpaper: path =>
        {
            imported.Add(path);
            return path;
        });
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");

            Assert.Equal([@"C:\Users\me\Videos\clip.mp4"], imported);
        }
    }

    /// <summary>
    /// The IMPORTED path is persisted, not the raw one the user picked -- the file on disk moved,
    /// and the settings file has to name where it actually is now.
    /// </summary>
    [Fact]
    public void PickingAVideo_PersistsTheImportedPath_NotTheRawOne()
    {
        var persisted = new List<string>();
        var harness = Wire(
            importVideoWallpaper: _ => @"C:\LOCALAPPDATA\CosmicWin\video-wallpaper.mp4",
            persistVideoWallpaperPath: persisted.Add);
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");

            Assert.Equal([@"C:\LOCALAPPDATA\CosmicWin\video-wallpaper.mp4"], persisted);
        }
    }

    /// <summary>Unwired -- as a composition built before this setting existed is -- a menu click must never throw.</summary>
    [Fact]
    public void WithNoPersistDelegateWired_PickingAVideoDoesNotThrow()
    {
        var harness = Wire();
        using (harness.Composition)
        {
            harness.Tray.SetVideoWallpaperPath(@"C:\Users\me\Videos\clip.mp4");
        }
    }
}
