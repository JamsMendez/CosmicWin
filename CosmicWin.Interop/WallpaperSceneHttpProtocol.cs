using System.Text.Json;

namespace CosmicWin.Interop;

/// <summary>The category a <see cref="WallpaperSceneHttpProtocol.TryValidate"/> call resolves to.</summary>
public enum WallpaperSceneRequestOutcome
{
    /// <summary>The body names one of the four fixed scenes. Maps to 202.</summary>
    Accepted,

    /// <summary>The body or the scene name is wrong. Maps to 400.</summary>
    BadRequest,
}

/// <summary>
/// The pure rules of the localhost HTTP wallpaper-scene endpoint (feature
/// wallpaper-scene-http-endpoint, S1): the route, the body ceiling, how a JSON body becomes a
/// validated scene name, and how that validation outcome becomes an HTTP status code.
/// </summary>
/// <remarks>
/// <para>
/// This is a separate class from <see cref="VideoWallpaperHttpProtocol"/> and <see
/// cref="AlertHttpProtocol"/> on purpose, the same way those two are separate from each other: this
/// route validates neither a filesystem path nor an alert counter, only a name against a CLOSED,
/// compile-time-fixed allow-list, so it needs no filesystem probe at all and gets the smallest body
/// ceiling of the three (256 bytes -- wider than the shortest possible valid body
/// <c>{"scene":"idle"}</c> by a comfortable margin, nowhere near enough to carry anything else).
/// </para>
/// <para>
/// The body is <c>{ "scene": "&lt;name&gt;" }</c>. <see cref="TryValidate"/> runs the same shape of
/// checks <see cref="VideoWallpaperHttpProtocol.TryValidate"/> does, cheapest first:
/// </para>
/// <list type="number">
/// <item>shape -- valid JSON, an object, exactly one field named <c>scene</c>, a non-empty string;</item>
/// <item>
/// membership -- the (lower-cased) value must be one of <c>processing</c>, <c>explorer</c>,
/// <c>idle</c>, <c>raphael</c> -- the exact same four spellings <c>CosmicWin.App.Settings.Parse</c>
/// accepts for the <c>wallpaper-scene</c> settings key, and matched the SAME way: case-insensitively.
/// This class lives in <c>CosmicWin.Interop</c>, which has no reference to <c>CosmicWin.App</c>'s
/// <c>WallpaperScene</c> enum, so it hands back the validated name as a plain lowercase string rather
/// than that enum -- the caller (<c>AppComposition</c>) is the one place that maps a
/// settings/protocol string to the enum, exactly as <c>Settings.Parse</c> already does for the
/// settings file.
/// </item>
/// </list>
/// <para>
/// Unlike the video route, there is no third, filesystem-dependent outcome (no 404, no 415): a scene
/// name is either one of the four fixed strings or it is not, so <see
/// cref="WallpaperSceneRequestOutcome"/> only ever needs <see
/// cref="WallpaperSceneRequestOutcome.Accepted"/> and <see
/// cref="WallpaperSceneRequestOutcome.BadRequest"/>.
/// </para>
/// <para>
/// Unknown JSON fields are rejected with 400, mirroring both sibling routes' strictness. A duplicate
/// <c>scene</c> key is not rejected -- the last one written wins, the same "last write wins"
/// semantics <see cref="VideoWallpaperHttpProtocol"/> applies to a duplicate <c>path</c> key.
/// </para>
/// </remarks>
public static class WallpaperSceneHttpProtocol
{
    /// <summary>The only route this class validates bodies for.</summary>
    public const string ScenePath = "/v1/wallpaper/scene";

    /// <summary>
    /// Request bodies past this many bytes are rejected unread. The smallest cap of the three HTTP
    /// routes on this server (alerts: 1024, video: 4096): a scene name is one of four short fixed
    /// words, never a filesystem path or a counter payload.
    /// </summary>
    public const int MaxBodyBytes = 256;

    /// <summary>The status code used when the wallpaper-scene feature is not wired into this composition.</summary>
    public const int NotAvailableStatusCode = 503;

    /// <summary>The reply body used alongside <see cref="NotAvailableStatusCode"/>.</summary>
    public const string NotAvailableError = "wallpaper scene switching is not available on this composition";

    private const string InvalidJson = "body is not valid JSON";
    private const string NotAnObject = "body must be a JSON object";
    private const string SceneMissing = "field 'scene' is required";
    private const string SceneNotString = "field 'scene' must be a string";
    private const string SceneEmpty = "field 'scene' must not be empty";
    private const string SceneUnknown = "field 'scene' must be one of: processing, explorer, idle, raphael";

    /// <summary>
    /// The closed allow-list, lower-case -- the same four spellings <c>CosmicWin.App.Settings.Parse</c>
    /// accepts for the <c>wallpaper-scene</c> settings key.
    /// </summary>
    private static readonly string[] AllowedScenes = ["processing", "explorer", "idle", "raphael"];

    /// <summary>
    /// Validates <paramref name="body"/> against every rule in the class remarks, cheapest first.
    /// Never throws: a malformed body is an everyday event from outside the process, not a bug.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="scene">
    /// The validated scene name, lower-cased to one of the four fixed spellings, when the outcome is
    /// <see cref="WallpaperSceneRequestOutcome.Accepted"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="error">
    /// A short reason when the outcome is not <see cref="WallpaperSceneRequestOutcome.Accepted"/>;
    /// otherwise <see langword="null"/>.
    /// </param>
    public static WallpaperSceneRequestOutcome TryValidate(string? body, out string? scene, out string? error)
    {
        scene = null;

        if (!TryReadSceneField(body, out var candidate, out error))
        {
            return WallpaperSceneRequestOutcome.BadRequest;
        }

        // Case-insensitive, mirroring CosmicWin.App.Settings.TryReadWallpaperScene -- the same text
        // a person types in settings.conf is accepted here too, normalised to its canonical
        // lowercase spelling.
        var normalized = candidate!.ToLowerInvariant();
        if (Array.IndexOf(AllowedScenes, normalized) < 0)
        {
            error = SceneUnknown;
            return WallpaperSceneRequestOutcome.BadRequest;
        }

        scene = normalized;
        error = null;
        return WallpaperSceneRequestOutcome.Accepted;
    }

    /// <summary>The status code for a validation outcome from <see cref="TryValidate"/>.</summary>
    public static int StatusCodeFor(WallpaperSceneRequestOutcome outcome) => outcome switch
    {
        WallpaperSceneRequestOutcome.Accepted => 202,
        WallpaperSceneRequestOutcome.BadRequest => 400,
        _ => 500,
    };

    private static bool TryReadSceneField(string? body, out string? candidate, out string? error)
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
                if (field.Name != "scene")
                {
                    error = $"unknown field '{field.Name}'";
                    return false;
                }

                if (field.Value.ValueKind != JsonValueKind.String)
                {
                    error = SceneNotString;
                    return false;
                }

                // Last write wins on a duplicate "scene" key -- see the class remarks.
                found = field.Value.GetString();
                seen = true;
            }

            if (!seen)
            {
                error = SceneMissing;
                return false;
            }

            if (string.IsNullOrEmpty(found))
            {
                error = SceneEmpty;
                return false;
            }

            candidate = found;
            error = null;
            return true;
        }
        // InvalidOperationException: the body is valid JSON, but a lone escaped surrogate
        // ("\uD800") in a key or in the scene name cannot be turned into a .NET string -- still a
        // malformed body, and TryValidate must never throw.
        catch (Exception parseFailure) when (parseFailure is JsonException or InvalidOperationException)
        {
            error = InvalidJson;
            return false;
        }
    }
}
