using CosmicWin.Interop;

namespace CosmicWin.App.Tests.Startup;

/// <summary>Shared <see cref="IProcessRunner"/> test double: captures the last invocation, returns a fixed exit code.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public string? LastFileName;
    public IReadOnlyList<string>? LastArguments;
    public int ExitCodeToReturn;
    public string StandardErrorToReturn = string.Empty;

    // Lets a test give schtasks' two verbs independent responses -- e.g. /Delete fails while
    // a follow-up /Query reports the task gone (or still present) -- so the locale-independent
    // existence check can be exercised without ever inspecting stderr text.
    public Dictionary<string, ProcessRunResult>? ResultByVerb;

    public ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        LastFileName = fileName;
        LastArguments = arguments;
        if (ResultByVerb is { } byVerb && arguments.Count > 0 && byVerb.TryGetValue(arguments[0], out var mapped))
        {
            return mapped;
        }

        return new ProcessRunResult(ExitCodeToReturn, string.Empty, StandardErrorToReturn);
    }
}
