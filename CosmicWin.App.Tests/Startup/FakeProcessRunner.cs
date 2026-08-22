using CosmicWin.Interop;

namespace CosmicWin.App.Tests.Startup;

/// <summary>Shared <see cref="IProcessRunner"/> test double: captures the last invocation, returns a fixed exit code.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public string? LastFileName;
    public IReadOnlyList<string>? LastArguments;
    public int ExitCodeToReturn;

    public ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        LastFileName = fileName;
        LastArguments = arguments;
        return new ProcessRunResult(ExitCodeToReturn, string.Empty, string.Empty);
    }
}
