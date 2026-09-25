using System.IO;
using System.Security.Cryptography;

namespace CosmicWin.App.Alerts;

/// <summary>
/// Owns the on-disk bearer token for the HTTP alert endpoint (feature http-alert-endpoint, H3),
/// mirroring <see cref="SettingsFile"/> and <see cref="ExceptionListFile"/>: a plain App-layer file
/// beside <c>settings.conf</c> and <c>exceptions.conf</c> in <c>%LOCALAPPDATA%\CosmicWin</c>.
/// </summary>
/// <remarks>
/// <para>
/// Created once, on first use, and kept forever after: <see cref="LoadOrCreate(Action{string}?)"/>
/// reads an existing valid token back UNCHANGED, so every caller that was ever handed the token
/// keeps working across restarts. A missing, empty, or malformed file is replaced with a fresh
/// random one -- the same "degrade, do not block" posture <see cref="SettingsFile"/> takes on a
/// settings file it cannot parse, except here the created value is a secret rather than a default.
/// </para>
/// <para>
/// No custom ACL is written on the file. <c>%LOCALAPPDATA%</c> already carries a per-user ACL that
/// this file inherits by virtue of living inside it -- another user account on the machine cannot
/// read it without their own elevated access, which is the same protection <c>settings.conf</c> and
/// <c>exceptions.conf</c> already rely on implicitly. Writing a second, redundant ACL on top would
/// only be more code with the same effective permission.
/// </para>
/// <para>
/// The token is 32 random bytes (<see cref="RandomNumberGenerator.GetBytes(int)"/>) encoded as
/// base64url (RFC 4648 §5) without padding: <c>+</c>/<c>/</c> replaced with <c>-</c>/<c>_</c>, and
/// the trailing <c>=</c> dropped, so the value is exactly <see cref="TokenLength"/> (43) characters
/// and safe to put straight into an <c>Authorization: Bearer</c> header with no further escaping.
/// </para>
/// <para>
/// The write is create-directory-then-write-temp-then-move, exactly the shape a crash-safe write
/// needs: <see cref="File.Replace(string, string, string?)"/> (falling back to a plain
/// <see cref="File.Move(string, string, bool)"/> when the destination does not exist yet) is what
/// makes a crash mid-write leave either the OLD complete token or the NEW complete one on disk,
/// never a half-written fragment that would then read back as "malformed" and force every caller
/// who cached the old value to fail its next bearer check.
/// </para>
/// <para>
/// R3-token-create-race: the move/replace above is also where several concurrent FIRST calls to
/// <see cref="LoadOrCreate(string, Action{string}?)"/> for the same missing or malformed file could
/// otherwise race each other -- a plain check-then-act between "does the file exist" and the
/// destructive move/replace. Closed by re-reading whatever is at the path immediately before that
/// move/replace, and again if the move/replace itself still loses the race: either way the losing
/// caller adopts the token a concurrent caller already wrote rather than failing or overwriting it.
/// See <see cref="CommitTemporaryFile"/>.
/// </para>
/// <para>
/// This class never throws. A read or write failure -- e.g. the file is a directory, or the
/// directory cannot be created -- reports a diagnostic through <paramref name="onDiagnostic"/> and
/// returns <see langword="null"/>, exactly the convention <see
/// cref="CosmicWin.Interop.HttpAlertCommandServer"/>'s own <c>onDiagnostic</c> parameter uses, so the caller
/// can leave the HTTP endpoint off while the pipe keeps working. The token VALUE is never included
/// in a diagnostic message.
/// </para>
/// </remarks>
public static class AlertHttpTokenFile
{
    /// <summary>32 random bytes, the size of the secret before encoding.</summary>
    public const int TokenBytes = 32;

    /// <summary>
    /// The length of a valid token: 32 bytes of base64url without padding is
    /// <c>ceil(32 / 3) * 4 - 1</c> characters (11 groups of 4, the last one short one padding byte).
    /// </summary>
    public const int TokenLength = 43;

    /// <summary>Beside <c>settings.conf</c> and <c>exceptions.conf</c>, in <c>%LOCALAPPDATA%\CosmicWin</c>.</summary>
    public static string ResolvePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin",
            "alert-http.token");

    /// <summary>Loads or creates the token at <see cref="ResolvePath"/>.</summary>
    public static string? LoadOrCreate(Action<string>? onDiagnostic = null) =>
        LoadOrCreate(ResolvePath(), onDiagnostic);

    /// <summary>
    /// Loads or creates the token at <paramref name="path"/>. Returns the existing token unchanged
    /// when the file already holds a valid one; otherwise generates a fresh one, writes it, and
    /// returns that. <see langword="null"/>, plus one call to <paramref name="onDiagnostic"/>, only
    /// when the file could not be read or the fresh token could not be written -- never a throw.
    /// </summary>
    public static string? LoadOrCreate(string path, Action<string>? onDiagnostic = null)
    {
        var reportDiagnostic = onDiagnostic ?? (_ => { });

        string? existing;
        try
        {
            existing = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            reportDiagnostic($"alert-http token: failed to read {path}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }

        if (existing is not null && IsValidToken(existing))
        {
            return existing;
        }

        if (existing is not null)
        {
            // R3-vacuous-token-leak-test: the one path where a diagnostic is reported AND a token is
            // still returned to the caller, so a test can prove the token value never leaks into a
            // diagnostic without relying only on a failure path that never produces a token at all.
            reportDiagnostic($"alert-http token: {path} held an invalid token; replacing it with a freshly generated one.");
        }

        var token = GenerateToken();
        try
        {
            return Write(path, token, reportDiagnostic);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            reportDiagnostic($"alert-http token: failed to write {path}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Trimmed, base64url alphabet only, exactly <see cref="TokenLength"/> characters. Anything else
    /// -- empty, too short, too long, or containing a character outside <c>[A-Za-z0-9_-]</c> -- is
    /// treated as malformed and replaced, never guessed at.
    /// </summary>
    internal static bool IsValidToken(string value)
    {
        if (value.Length != TokenLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isBase64UrlCharacter =
                character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_';

            if (!isBase64UrlCharacter)
            {
                return false;
            }
        }

        return true;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Create-directory-then-write-temp-then-move/replace: a crash between these steps leaves either
    /// the OLD token file untouched or the NEW one complete, never a half-written one. Returns the
    /// token that ends up authoritative on disk, which is <paramref name="token"/> itself unless a
    /// concurrent caller won the create race (see <see cref="CommitTemporaryFile"/>).
    /// </summary>
    private static string Write(string path, string token, Action<string> reportDiagnostic)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, token);
            return CommitTemporaryFile(temporaryPath, path, token, reportDiagnostic);
        }
        finally
        {
            // Best-effort: on the success paths above the temp file is already gone (renamed away);
            // this only cleans up after a failure between the write and the move/replace.
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

    /// <summary>
    /// Moves/replaces <paramref name="temporaryPath"/> into <paramref name="path"/>, closing
    /// R3-token-create-race: the plain check-then-act between "does <paramref name="path"/> exist"
    /// and the destructive move/replace lets two concurrent first-run callers both decide to create
    /// the file, and lets a caller replacing a MALFORMED file race a concurrent caller that already
    /// fixed it. Two checks close both windows: (1) immediately before touching disk, re-read
    /// whatever is at <paramref name="path"/> right now, and adopt it if it is already a valid
    /// token -- never overwriting a token a concurrent caller just finished writing; (2) if the
    /// move/replace itself still loses a race in the instant after that check, the same re-read runs
    /// again as the exception filter, so the losing caller adopts the winner's token instead of
    /// failing. Either way this returns a token, never throws for an ordinary lost race, and reports
    /// one informational diagnostic (never the token value) when it hands back someone else's token
    /// instead of its own.
    /// </summary>
    private static string CommitTemporaryFile(string temporaryPath, string path, string token, Action<string> reportDiagnostic)
    {
        if (TryReadValidToken(path, out var alreadyThere))
        {
            reportDiagnostic($"alert-http token: another caller already created {path}; using its token instead of ours.");
            return alreadyThere!;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            return token;
        }
        catch (Exception exception) when (IsRecoverable(exception) && TryReadValidToken(path, out var winner))
        {
            reportDiagnostic($"alert-http token: lost a create race for {path}; using the other caller's token instead of ours.");
            return winner!;
        }
    }

    /// <summary>Reads <paramref name="path"/> and reports whether it currently holds a valid token, never throwing.</summary>
    private static bool TryReadValidToken(string path, out string? token)
    {
        token = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var content = File.ReadAllText(path).Trim();
            if (!IsValidToken(content))
            {
                return false;
            }

            token = content;
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// R3-never-throws-filter: this class promises to never throw for an ordinary IO/permission/path
    /// problem, but must not silently swallow a process-corrupting exception as if it were just a
    /// failed file read or write. Same corruption-class exclusion
    /// <c>AppComposition.IsRecoverableAlertLayerFailure</c> uses for the same reason.
    /// </summary>
    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
