using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.Layout.Tests.Filters;

/// <summary>
/// WE-3: manual exception list changes take effect without a full WM restart. Covers spec
/// scenario "Removing an exception restores tiling" — a live <see cref="ExceptionListStore"/>
/// callers keep one reference to, calling <see cref="ExceptionListStore.Reload"/> when the
/// on-disk file changes (tray "Reload" or file-watch, per WE-3 — the actual file I/O and
/// trigger remain an App-layer concern outside this batch's scope).
/// </summary>
public class ExceptionListStoreTests
{
    private static WindowDescriptor SpotifyWindow() => new(
        ClassName: "Chrome_WidgetWin_1",
        ProcessName: "Spotify.exe",
        Title: "Spotify Premium",
        ExStyle: 0,
        Style: WindowStyleFlags.SystemMenu | WindowStyleFlags.MaximizeBox | WindowStyleFlags.MinimizeBox,
        IsOwned: false,
        Width: 800,
        Height: 600);

    [Fact]
    public void Reload_RemovingEntry_MatchesReturnsFalseAfterward()
    {
        // Spec scenario: "Removing an exception restores tiling" — GIVEN an entry is removed and
        // the list is reloaded, WHEN a matching window is created afterward, THEN it tiles normally.
        var store = new ExceptionListStore(ExceptionListLoader.Parse("process:Spotify.exe"));
        Assert.True(store.Current.Matches(SpotifyWindow()));

        store.Reload(string.Empty);

        Assert.False(store.Current.Matches(SpotifyWindow()));
    }

    [Fact]
    public void Reload_AddingEntry_MatchesReturnsTrueAfterward()
    {
        // Triangulation: the mirror direction — reload can also newly EXCLUDE a window that was
        // previously tileable, proving Reload genuinely replaces state rather than only clearing it.
        var store = new ExceptionListStore(ExceptionList.Empty);
        Assert.False(store.Current.Matches(SpotifyWindow()));

        store.Reload("process:Spotify.exe");

        Assert.True(store.Current.Matches(SpotifyWindow()));
    }

    [Fact]
    public void Reload_ReturnsSameStoreInstance_CallersHoldingTheStoreSeeTheUpdate()
    {
        // Proves WE-3's "no restart" requirement structurally: a caller that keeps only the
        // ExceptionListStore reference (not Current) still observes the reload without re-wiring.
        var store = new ExceptionListStore(ExceptionList.Empty);

        store.Reload("process:Spotify.exe");
        var afterFirstReload = store.Current;

        store.Reload(string.Empty);

        Assert.NotSame(afterFirstReload, store.Current);
        Assert.False(store.Current.Matches(SpotifyWindow()));
    }

    /// <summary>The tray's Reload wiring supplies an already-parsed <see cref="ExceptionList"/> (e.g. From a file loader) rather than raw text -- an overload accepting one directly.</summary>
    [Fact]
    public void Reload_ExceptionListOverload_ReplacesCurrent_WithTheGivenInstance()
    {
        var store = new ExceptionListStore(ExceptionList.Empty);
        var replacement = ExceptionListLoader.Parse("process:Spotify.exe");

        store.Reload(replacement);

        Assert.Same(replacement, store.Current);
        Assert.True(store.Current.Matches(SpotifyWindow()));
    }
}
