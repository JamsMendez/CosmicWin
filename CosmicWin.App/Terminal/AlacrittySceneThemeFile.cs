using System.IO;
using System.Security;
using System.Text;

namespace CosmicWin.App.Terminal;

/// <summary>
/// Writes the per-scene Alacritty colours file (<see cref="AlacrittySceneTheme"/>) to the one path
/// the user opted into with <c>alacritty-theme-file</c>. CosmicWin owns that file outright.
/// </summary>
/// <remarks>
/// Never throws for an ordinary IO, permission or path problem: a terminal palette is a nicety and
/// must not break a scene switch. The write is temp-then-replace, the same shape
/// <c>AlertHttpTokenFile</c> uses, so Alacritty's live reload never reads a half-written file.
/// </remarks>
public static class AlacrittySceneThemeFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes the colours for <paramref name="scene"/> to <paramref name="path"/>. Returns true when
    /// the file holds that content afterwards (including when it already did, in which case it is not
    /// rewritten so Alacritty does not reload for nothing); false, after one
    /// <paramref name="onDiagnostic"/> call, when it could not.
    /// </summary>
    public static bool TryWrite(string path, WallpaperScene scene, Action<string>? onDiagnostic)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            onDiagnostic?.Invoke("alacritty-theme-failed: path is blank");
            return false;
        }

        try
        {
            var bytes = Utf8NoBom.GetBytes(AlacrittySceneTheme.Render(scene));
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                return true;
            }

            Write(path, bytes);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or SecurityException)
        {
            onDiagnostic?.Invoke($"alacritty-theme-failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            // Best-effort: on success the temp file is already renamed away; this only cleans up
            // after a failure between the write and the move/replace.
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Best-effort cleanup only; the real failure already propagates to the caller.
            }
        }
    }
}
