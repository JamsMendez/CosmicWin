using System.IO;
using System.Security.Cryptography;
using System.Threading;

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
/// R3-replace-branch-race-still-open: an EARLIER version of this class tried to close the race
/// between two concurrent <see cref="LoadOrCreate(string, Action{string}?)"/> calls for the same
/// path with a plain re-read-then-act check immediately before the move/replace. That narrowed the
/// window but did not close it: <see cref="File.Replace(string, string, string?)"/> does not throw
/// just because the destination already exists -- existing is the whole point of calling it -- so
/// two callers racing to replace the SAME malformed file could both pass the re-check and then both
/// call <c>Replace</c>, with the later one silently discarding the earlier one's already-committed,
/// already-returned token. No exception, no diagnostic, just a lost update. The fix here is a real
/// mutual-exclusion lock, not a better-aimed check-then-act: the entire read-validate-create
/// sequence in <see cref="LoadOrCreate(string, Action{string}?)"/> runs inside one named,
/// cross-process <see cref="Mutex"/> (<see cref="LockName"/>), so at most one caller on this machine
/// is ever inside it at a time, and the existence/validity re-read that used to be a best-effort
/// race mitigation is now simply the ordinary read of the current, unambiguous state.
/// </para>
/// <para>
/// This class never throws. A read or write failure -- e.g. the file is a directory, or the
/// directory cannot be created -- reports a diagnostic through <paramref name="onDiagnostic"/> and
/// returns <see langword="null"/>, exactly the convention <see
/// cref="CosmicWin.Interop.HttpAlertCommandServer"/>'s own <c>onDiagnostic</c> parameter uses, so the caller
/// can leave the HTTP endpoint off while the pipe keeps working. The token VALUE is never included
/// in a diagnostic message. A timed-out wait for the lock (see <see cref="LockTimeout"/>) is treated
/// the same way: a diagnostic and <see langword="null"/>, never a throw and never an indefinite hang.
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

    /// <summary>
    /// Named, session-local (<c>Local\</c>, not <c>Global\</c> -- this token is per-user, like the
    /// file itself) mutex serialising every <see cref="LoadOrCreate(string, Action{string}?)"/> call
    /// on this machine against every other one, closing R3-replace-branch-race-still-open for real:
    /// see the class remarks.
    /// </summary>
    private const string LockName = @"Local\CosmicWin.AlertHttpToken";

    /// <summary>
    /// Bounds the wait for <see cref="LockName"/> so a stuck or crashed holder degrades this call to
    /// a diagnostic and <see langword="null"/> rather than hanging the caller forever.
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

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
    /// when the lock could not be acquired in time, or the file could not be read or the fresh token
    /// could not be written -- never a throw.
    /// </summary>
    public static string? LoadOrCreate(string path, Action<string>? onDiagnostic = null) =>
        LoadOrCreate(path, onDiagnostic, LockName, LockTimeout);

    /// <summary>
    /// <see cref="LoadOrCreate(string, Action{string}?)"/> with the lock name and timeout exposed, so
    /// tests can hold a private lock past a short timeout instead of the real one for 5 seconds.
    /// </summary>
    internal static string? LoadOrCreate(string path, Action<string>? onDiagnostic, string lockName, TimeSpan lockTimeout)
    {
        var reportDiagnostic = onDiagnostic ?? (_ => { });

        // Finding R3-mutex-ctor-outside-never-throws: creating the named mutex can itself throw (a
        // same-named object of another type, or one with a restrictive ACL), so it is inside the same
        // never-throw contract as the file access below.
        Mutex gate;
        try
        {
            gate = new Mutex(initiallyOwned: false, lockName);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            reportDiagnostic($"alert-http token: could not open the token lock: {exception.GetType().Name}: {exception.Message}");
            return null;
        }

        using var ownedGate = gate;
        bool acquired;
        try
        {
            acquired = gate.WaitOne(lockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder terminated (e.g. crashed) without releasing the lock. The .NET
            // documentation is explicit that ownership is still granted to THIS caller when this is
            // thrown -- the file on disk cannot have been left mid-write either way, since the only
            // write below goes through a temp-file-then-move/replace that is itself crash-safe.
            acquired = true;
        }

        if (!acquired)
        {
            reportDiagnostic($"alert-http token: timed out waiting for the token lock for {path}.");
            return null;
        }

        try
        {
            return LoadOrCreateLocked(path, reportDiagnostic);
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    /// <summary>
    /// The actual read-validate-create sequence, run with <see cref="LockName"/> held: at most one
    /// caller on this machine is ever in here at a time, so the read below is simply the current,
    /// unambiguous state of <paramref name="path"/> -- not a best-effort re-check racing another
    /// writer.
    /// </summary>
    private static string? LoadOrCreateLocked(string path, Action<string> reportDiagnostic)
    {
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
            Write(path, token);
            return token;
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
    /// the OLD token file untouched or the NEW one complete, never a half-written one. Safe to do as
    /// a plain check-then-act -- <c>File.Exists</c> then <c>Move</c> or <c>Replace</c> -- because the
    /// caller already holds <see cref="LockName"/> for the whole read-validate-create sequence; see
    /// the class remarks for why an EARLIER version needed (and still lost the race with) a re-check
    /// around exactly this step.
    /// </summary>
    private static void Write(string path, string token)
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
    /// R3-never-throws-filter: this class promises to never throw for an ordinary IO/permission/path
    /// problem, but must not silently swallow a process-corrupting exception as if it were just a
    /// failed file read or write. Same corruption-class exclusion
    /// <c>AppComposition.IsRecoverableAlertLayerFailure</c> uses for the same reason.
    /// </summary>
    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
