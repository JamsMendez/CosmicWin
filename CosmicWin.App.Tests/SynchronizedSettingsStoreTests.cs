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

        Assert.True(focusBorderThread.Join(TimeSpan.FromMinutes(1))); // was 5s: hang guard sized for the 2-core GitHub Actions runner
        Assert.True(tilingThread.Join(TimeSpan.FromMinutes(1))); // was 5s: hang guard sized for the 2-core GitHub Actions runner

        Assert.False(store.Current.FocusBorder);
        Assert.False(store.Current.Tiling);

        // R3-settings-store-test-asserts-unused-snapshot: production never reads Current -- what
        // reaches disk is the argument handed to save, so the LAST saved snapshot must carry both
        // fields too, or a regression that saves a stale value would bring the lost update back.
        Assert.Equal(2, saved.Count);
        Assert.False(saved[^1].FocusBorder);
        Assert.False(saved[^1].Tiling);
    }

    /// <summary>
    /// S10 (wallpaper-scene-http-endpoint, R3-settings-store-save-failure-semantics-unproved):
    /// characterizes what <see cref="SynchronizedSettingsStore.Update"/> actually does when the save
    /// delegate throws -- an open follow-up from the S6/S7 review, accepted then as "eventually
    /// convergent" but never pinned. Production's own <c>save</c> delegate is
    /// <c>SettingsFile.Save</c>, which already swallows <see cref="IOException"/>/
    /// <see cref="UnauthorizedAccessException"/> internally (S10's other change, R3-first-run-write-
    /// failure-silent) and therefore never actually throws either of those two -- so this is a
    /// characterization of <see cref="SynchronizedSettingsStore"/>'s OWN contract in the general
    /// case, not a production reachability claim: <c>_current</c> is assigned BEFORE <c>save</c> is
    /// called, so a throwing save (for any reason at all) still leaves memory holding the new value;
    /// <see cref="SynchronizedSettingsStore.Update"/> does not catch that throw itself, so it
    /// propagates to the caller, which must guard its own persist call if it cannot tolerate a
    /// throw. The NEXT successful <see cref="SynchronizedSettingsStore.Update"/>
    /// starts its own <c>change</c> from that same ahead-of-disk <c>_current</c>, so its saved
    /// snapshot ends up carrying BOTH the failed update's field and its own -- one save behind,
    /// never lost.
    /// </summary>
    [Fact]
    public void Update_WhenSaveThrows_MemoryStaysAheadAndTheNextSuccessfulUpdateSavesBothChanges()
    {
        var saved = new List<Settings>();
        var saveCallCount = 0;
        void Save(Settings settings)
        {
            saveCallCount++;
            if (saveCallCount == 1)
            {
                throw new IOException("disk full");
            }

            saved.Add(settings);
        }

        var store = new SynchronizedSettingsStore(Settings.Default, Save);

        var thrown = Assert.Throws<IOException>(() => store.Update(s => s with { FocusBorder = false }));
        Assert.Equal("disk full", thrown.Message);

        // Memory is already ahead of disk: _current was assigned before the throwing save ran.
        Assert.False(store.Current.FocusBorder);

        // The next successful Update reads that same ahead-of-disk _current, so BOTH fields land in
        // the one snapshot it actually manages to save -- the failed update is not lost, only delayed.
        store.Update(s => s with { Tiling = false });

        var onlySavedSnapshot = Assert.Single(saved);
        Assert.False(onlySavedSnapshot.FocusBorder);
        Assert.False(onlySavedSnapshot.Tiling);
    }
}
