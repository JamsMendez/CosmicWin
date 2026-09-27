namespace CosmicWin.App;

/// <summary>
/// S6 (wallpaper-scene-http-endpoint, R3-persist-shared-stored-capture): the ONE place every
/// <c>persistXyz</c> closure <c>AppComposition.WireProduction</c> hands out reads and rewrites the
/// captured settings snapshot -- serialized, so two of those closures firing from different threads
/// (the UI STA thread for focus-border/border-colour/tiling/scene, the video-wallpaper MTA thread for
/// the video path) can never interleave their own read-modify-write of the SAME snapshot.
/// </summary>
/// <remarks>
/// Before this class, <c>WireProduction</c> captured one mutable <c>stored</c> local and every
/// closure did <c>SettingsFile.Save(stored = stored with { Field = value })</c> directly -- a classic
/// lost update: two concurrent closures both read the same pre-update <c>stored</c>, each compute
/// their own "with" from it, and whichever assigns last wins, silently discarding the other's field.
/// Wrapping the ENTIRE read-modify-write-and-save in one lock closes that window: a concurrent
/// <see cref="Update"/> from another thread runs either entirely before or entirely after this one,
/// never interleaved with it, so both edits always survive regardless of which arrives first.
/// </remarks>
/// <remarks>
/// Deliberately minimal (the task's own instruction): no queueing, no async, no batching. Every
/// caller is a fire-and-forget settings toggle (a checkbox flip, an imported video path, a scene
/// switch) -- nothing waits on <see cref="Update"/>'s result, so holding a lock for the sub-millisecond
/// duration of an in-memory record `with` plus one small file write costs no more than what each
/// individual unsynchronized closure already paid on its own.
/// </remarks>
internal sealed class SynchronizedSettingsStore(Settings initial, Action<Settings> save)
{
    private readonly object _gate = new();
    private Settings _current = initial;

    /// <summary>
    /// The current in-memory snapshot, read under the same lock <see cref="Update"/> writes under.
    /// Production never reads this directly (every persisted field flows back out only through
    /// <paramref name="save"/>'s own side effect) -- it exists so a test can observe the end state of
    /// concurrent updates without needing its own separate synchronization.
    /// </summary>
    public Settings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the CURRENT snapshot and saves the result, all under one
    /// lock. <paramref name="change"/> receives whatever the most recently applied update actually
    /// landed -- never a value read before this call was made -- exactly the same "read at the last
    /// possible moment" rule <c>SwitchVideoWallpaper</c>'s own posted work item already follows for
    /// <c>currentVideoWallpaperPath</c>.
    /// </summary>
    public void Update(Func<Settings, Settings> change)
    {
        lock (_gate)
        {
            _current = change(_current);
            save(_current);
        }
    }
}
