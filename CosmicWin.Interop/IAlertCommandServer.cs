namespace CosmicWin.Interop;

/// <summary>
/// Listens for <c>--alert</c> commands arriving over the T4 named pipe and hands each one's raw
/// text to a caller-supplied handler.
/// </summary>
/// <remarks>
/// <see cref="Win32.NamedPipeAlertCommandServer"/> is the real implementation. This T4 task does
/// NOT wire it into <c>AppComposition</c> -- that is T8; this interface exists now only so T8 can
/// depend on an abstraction rather than the concrete Win32 type, the same shape as <see
/// cref="IVideoWallpaperHost"/>/<see cref="Win32.Win32VideoWallpaperHost"/>.
/// </remarks>
public interface IAlertCommandServer : IDisposable
{
    /// <summary>
    /// Starts listening on a background thread. Idempotent: a second call is a no-op. Never
    /// throws; a failure to start is reported the same way a failure on any later connection is --
    /// through the constructor's diagnostic callback.
    /// </summary>
    void Start();
}
