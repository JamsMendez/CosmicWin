using System.Text.Json;

namespace CosmicWin.Interop;

/// <summary>The category a <see cref="VideoWallpaperHttpProtocol.TryValidate"/> call resolves to.</summary>
public enum VideoWallpaperRequestOutcome
{
    /// <summary>The path is valid, absolute, local, an existing <c>.mp4</c> file. Maps to 202.</summary>
    Accepted,

    /// <summary>The body or the path shape is wrong. Maps to 400.</summary>
    BadRequest,

    /// <summary>The path is well-formed but nothing exists there (or it is a directory). Maps to 404.</summary>
    NotFound,

    /// <summary>The path exists but does not end in <c>.mp4</c>. Maps to 415.</summary>
    UnsupportedMediaType,
}

/// <summary>
/// The pure rules of the localhost HTTP video-wallpaper endpoint (feature
/// video-wallpaper-http-endpoint): the route, the body ceiling, how a JSON body becomes a validated
/// absolute path, and how that validation outcome becomes an HTTP status code.
/// </summary>
/// <remarks>
/// <para>
/// This is a separate class from <see cref="AlertHttpProtocol"/> on purpose. That class only knows
/// how to turn alert counters into pipe command text, and its status mapping is keyed off the
/// shared alert command handler's reply strings -- both alert-specific. This route validates a
/// filesystem path instead, so it gets its own vocabulary, its own body ceiling (4096 bytes, against
/// the alert route's 1024), and its own outcome-to-status mapping. Nothing here is reused from, or
/// by, the alert route except the general shape of the pattern (constants, a parse method that
/// never throws, a separate status mapping, an error string that never echoes the input).
/// </para>
/// <para>
/// The body is <c>{ "path": "C:\\videos\\x.mp4" }</c>. <see cref="TryValidate"/> runs every check
/// cheapest first, all pure string checks before any filesystem I/O:
/// </para>
/// <list type="number">
/// <item>shape -- valid JSON, an object, exactly one field named <c>path</c>, a non-empty string;</item>
/// <item>
/// path syntax -- no invalid path characters, not a URI, absolute and drive-rooted (<c>C:\...</c>).
/// Relative, drive-relative (<c>C:foo</c>), rooted-without-a-drive (<c>\foo</c>), UNC
/// (<c>\\server\share</c>) and device (<c>\\?\</c>, <c>\\.\</c>) paths are all rejected here, and so
/// is a forward-slash drive path (<c>C:/x/y.mp4</c>) -- see the remark below on that choice;
/// </item>
/// <item>extension -- must end with <c>.mp4</c>, case-insensitively;</item>
/// <item>drive type -- must not be a mapped network drive (one filesystem probe);</item>
/// <item>existence -- the file must exist and not be a directory (a second filesystem probe).</item>
/// </list>
/// <para>
/// Extension is checked before existence on purpose: a wrong-extension request against a MISSING
/// file still answers 415, never 404. Both codes mean something else elsewhere on this server (an
/// unknown route also answers 404; a wrong <c>Content-Type</c> also answers 415), so a caller must
/// be able to tell "this isn't a video" apart from "this file isn't there" without probing further,
/// and that answer must not depend on whether the file happens to exist.
/// </para>
/// <para>
/// Forward-slash drive paths (<c>C:/x/y.mp4</c>) are REJECTED, not normalised to backslashes.
/// Decision 4 spells the accepted shape as <c>C:\...</c>; accepting the forward-slash variant too
/// would mean this endpoint has to also decide, for every future syntax check, whether a
/// forward-slash rewrite of that check still holds -- exactly the kind of path-shape ambiguity this
/// endpoint exists to avoid (nothing stops a caller from writing <c>C://evil</c>, which reads like
/// both a drive path and a URI). Failing closed on the literally decided shape is the safer default;
/// loosening it later, if a real caller needs it, is easy. This is flagged for the maintainer in the
/// task report.
/// </para>
/// <para>
/// Unknown JSON fields are rejected with 400, mirroring <see cref="AlertHttpProtocol"/>'s
/// strictness. A duplicate <c>path</c> key is not rejected -- the last one written wins, the same
/// "last write wins" semantics most JSON readers apply to duplicate keys -- since, unlike the alert
/// route's counters, there is no downstream parser that benefits from seeing every duplicate.
/// </para>
/// </remarks>
public static class VideoWallpaperHttpProtocol
{
    /// <summary>The only route this class validates bodies for.</summary>
    public const string VideoPath = "/v1/wallpaper/video";

    /// <summary>
    /// Request bodies past this many bytes are rejected unread. Wider than the alert route's 1024
    /// bytes because a Windows path can be long; still nowhere near enough to stream anything.
    /// </summary>
    public const int MaxBodyBytes = 4096;

    /// <summary>The status code used when the video wallpaper feature is not wired into this composition.</summary>
    public const int NotAvailableStatusCode = 503;

    /// <summary>The reply body used alongside <see cref="NotAvailableStatusCode"/>.</summary>
    public const string NotAvailableError = "video wallpaper is not available on this composition";

    private const string InvalidJson = "body is not valid JSON";
    private const string NotAnObject = "body must be a JSON object";
    private const string PathMissing = "field 'path' is required";
    private const string PathNotString = "field 'path' must be a string";
    private const string PathEmpty = "field 'path' must not be empty";
    private const string PathHasInvalidChars = "path contains invalid characters";
    private const string PathIsUri = "path must be a local file path, not a URI";
    private const string PathNotDriveRooted = "path must be an absolute, drive-rooted local path (for example C:\\videos\\x.mp4)";
    private const string PathIsNetworkDrive = "path must not be on a network drive";
    private const string FileMissing = "file does not exist";
    private const string WrongExtension = "file must have the .mp4 extension";

    private static readonly char[] InvalidPathChars = BuildInvalidPathChars();

    /// <summary>
    /// Validates <paramref name="body"/> against every rule in the class remarks, cheapest first.
    /// Never throws: a malformed body or an unreachable drive is an everyday event from outside the
    /// process, not a bug.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="path">
    /// The validated, absolute, drive-rooted path when the outcome is
    /// <see cref="VideoWallpaperRequestOutcome.Accepted"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="error">
    /// A short reason, never containing the candidate path (it lives under the user's profile), when
    /// the outcome is not <see cref="VideoWallpaperRequestOutcome.Accepted"/>; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <param name="probes">
    /// The filesystem probes to use; defaults to <see cref="VideoWallpaperFileProbes.Default"/>.
    /// Overridable so tests can be deterministic without touching disk.
    /// </param>
    public static VideoWallpaperRequestOutcome TryValidate(
        string? body,
        out string? path,
        out string? error,
        VideoWallpaperFileProbes? probes = null)
    {
        path = null;
        probes ??= VideoWallpaperFileProbes.Default;

        if (!TryReadPathField(body, out var candidate, out error))
        {
            return VideoWallpaperRequestOutcome.BadRequest;
        }

        if (!TryValidateSyntax(candidate!, out error))
        {
            return VideoWallpaperRequestOutcome.BadRequest;
        }

        if (!candidate!.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            error = WrongExtension;
            return VideoWallpaperRequestOutcome.UnsupportedMediaType;
        }

        var driveRoot = candidate[..3];
        if (probes.IsNetworkDrive(driveRoot))
        {
            error = PathIsNetworkDrive;
            return VideoWallpaperRequestOutcome.BadRequest;
        }

        if (probes.DirectoryExists(candidate) || !probes.FileExists(candidate))
        {
            error = FileMissing;
            return VideoWallpaperRequestOutcome.NotFound;
        }

        path = candidate;
        error = null;
        return VideoWallpaperRequestOutcome.Accepted;
    }

    /// <summary>The status code for a validation outcome from <see cref="TryValidate"/>.</summary>
    public static int StatusCodeFor(VideoWallpaperRequestOutcome outcome) => outcome switch
    {
        VideoWallpaperRequestOutcome.Accepted => 202,
        VideoWallpaperRequestOutcome.BadRequest => 400,
        VideoWallpaperRequestOutcome.NotFound => 404,
        VideoWallpaperRequestOutcome.UnsupportedMediaType => 415,
        _ => 500,
    };

    private static bool TryReadPathField(string? body, out string? candidate, out string? error)
    {
        candidate = null;

        if (body is null)
        {
            error = InvalidJson;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = NotAnObject;
                return false;
            }

            string? found = null;
            var seen = false;
            foreach (var field in document.RootElement.EnumerateObject())
            {
                if (field.Name != "path")
                {
                    error = $"unknown field '{field.Name}'";
                    return false;
                }

                if (field.Value.ValueKind != JsonValueKind.String)
                {
                    error = PathNotString;
                    return false;
                }

                // Last write wins on a duplicate "path" key -- see the class remarks.
                found = field.Value.GetString();
                seen = true;
            }

            if (!seen)
            {
                error = PathMissing;
                return false;
            }

            if (string.IsNullOrEmpty(found))
            {
                error = PathEmpty;
                return false;
            }

            candidate = found;
            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = InvalidJson;
            return false;
        }
    }

    private static bool TryValidateSyntax(string candidate, out string? error)
    {
        if (candidate.IndexOfAny(InvalidPathChars) >= 0)
        {
            error = PathHasInvalidChars;
            return false;
        }

        if (candidate.Contains("://", StringComparison.Ordinal))
        {
            error = PathIsUri;
            return false;
        }

        if (!IsDriveRooted(candidate))
        {
            error = PathNotDriveRooted;
            return false;
        }

        // Checked only once the path is known to be drive-rooted, so a URI or a device path keeps
        // its more specific reason above: a colon past the drive letter's own names an NTFS
        // alternate data stream (C:\x.txt:hidden.mp4), never a plain file.
        if (candidate.IndexOf(':', 2) >= 0)
        {
            error = PathHasInvalidChars;
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsDriveRooted(string candidate) =>
        candidate.Length >= 3 &&
        char.IsAsciiLetter(candidate[0]) &&
        candidate[1] == ':' &&
        candidate[2] == '\\';

    private static char[] BuildInvalidPathChars()
    {
        var chars = new HashSet<char>(Path.GetInvalidPathChars());
        foreach (var extra in new[] { '"', '<', '>', '|', '*', '?' })
        {
            chars.Add(extra);
        }

        return chars.ToArray();
    }
}
