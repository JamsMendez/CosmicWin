namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The collection fixture that holds <see cref="RealDesktopLock"/> for as long as one assembly's
/// desktop collection is running, so the other assembly's waits rather than races it.
/// </summary>
/// <remarks>
/// <para>
/// One fixture instance per collection, constructed before its first fact and disposed after its
/// last, which is exactly the window the lock has to cover. Both
/// <c>CosmicWin.Interop.Tests</c> and <c>CosmicWin.App.Tests</c> declare the SAME fixture type --
/// App.Tests already references Interop.Tests as a project -- so there is one lock and one place
/// it is taken, rather than two copies that could drift apart.
/// </para>
/// <para>
/// Deliberately does nothing else. It is not a place to put desktop setup: every fact in these
/// collections already arranges its own world, and a fixture that also mutated the desktop would
/// reintroduce cross-class coupling inside the assembly that <c>DisableParallelization</c> exists
/// to remove.
/// </para>
/// </remarks>
public sealed class RealDesktopSession : IDisposable
{
    private readonly RealDesktopLock _lock;

    public RealDesktopSession() => _lock = RealDesktopLock.Acquire(RealDesktopLock.DefaultTimeout);

    public void Dispose() => _lock.Dispose();
}
