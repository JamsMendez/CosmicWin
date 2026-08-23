using System.IO;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>Owns the on-disk manual exception-list read. <see cref="ExceptionListLoader.Parse"/> takes raw text, not a path, keeping <c>CosmicWin.Layout</c> IO-free (D1/D8) -- this App-layer type owns the file.</summary>
public static class ExceptionListFile
{
    /// <summary>Default on-disk location, consistent with the design's <c>%LOCALAPPDATA%</c> use for the Scheduled Task XML.</summary>
    public static string ResolvePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CosmicWin", "exceptions.conf");

    /// <summary>Reads and parses the exception list at <see cref="ResolvePath"/>. Missing file -&gt; empty list.</summary>
    public static ExceptionList Load() => Load(ResolvePath());

    /// <summary>Reads and parses the exception list at <paramref name="path"/>. Missing file -&gt; empty list, not a throw (first run before the file exists must not block startup).</summary>
    public static ExceptionList Load(string path)
    {
        var content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        return ExceptionListLoader.Parse(content);
    }
}
