namespace CosmicWin.App.Tests;

/// <summary>
/// S6 (wallpaper-scene-http-endpoint, R3-persist-shared-stored-capture): <see
/// cref="SynchronizedSettingsStore"/> is the extracted, directly-testable piece of
/// <c>AppComposition.WireProduction</c>'s own persist closures -- <c>WireProduction</c> itself
/// constructs real Win32 collaborators and cannot run under this suite, so the lock discipline is
/// proven here instead, against the same <see cref="Settings"/> record production actually uses.
/// </summary>
public sealed class SynchronizedSettingsStoreTests
{
    /// <summary>
    /// Two concurrent updates to DIFFERENT fields must both survive. Each update's own `change`
    /// function sleeps before returning its new snapshot -- long enough that, without a lock held for
    /// the WHOLE read-modify-write, both threads read the SAME pre-update snapshot before either one
    /// writes, and whichever writes last clobbers the other's field. <see
    /// cref="System.Threading.ManualResetEventSlim"/> releases both waiting threads at effectively the
    /// same instant, so neither gets a head start reading before the other has even started.
    /// </summary>
    [Fact]
    public void Update_TwoConcurrentUpdatesToDifferentFields_BothSurvive()
    {
        var saved = new List<Settings>();
        var store = new SynchronizedSettingsStore(Settings.Default, saved.Add);
        var start = new ManualResetEventSlim(false);

        var focusBorderThread = new Thread(() =>
        {
            start.Wait();
            store.Update(s =>
            {
                Thread.Sleep(50);
                return s with { FocusBorder = false };
            });
        });
        var tilingThread = new Thread(() =>
        {
            start.Wait();
            store.Update(s =>
            {
                Thread.Sleep(50);
                return s with { Tiling = false };
            });
        });

        focusBorderThread.Start();
        tilingThread.Start();
        start.Set();

        Assert.True(focusBorderThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(tilingThread.Join(TimeSpan.FromSeconds(5)));

        Assert.False(store.Current.FocusBorder);
        Assert.False(store.Current.Tiling);
    }
}
