using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CosmicWin.App.Tests;

/// <summary>
/// Closes verify-report #21 revision 14 WARNING V14-W1: <see cref="CompositionRoot.BuildSessionAdapter"/>
/// is <c>public</c> and reachable from the SAME assembly as the untestable WPF <see cref="App"/>
/// composition site, so no C# access modifier (not even <c>internal</c> -- <c>App.xaml.cs</c> lives
/// in the same <c>CosmicWin.App</c> assembly as <see cref="CompositionRoot"/>, so <c>internal</c>
/// changes nothing about this specific reachability) can stop a same-assembly edit at
/// <c>App.xaml.cs</c> from swapping <see cref="CompositionRoot.BuildPauseGatedSession"/> for <see
/// cref="CompositionRoot.BuildSessionAdapter"/> with a constant <c>() =&gt; false</c> gate --
/// verify-report #21 probe P2 did exactly this, built with 0 Warning(s), and left App
/// 131 passed / 0 failed. Since the vulnerable seam cannot be closed by the type system without
/// either merging the two factories or moving <c>App.xaml.cs</c> out of this assembly (both larger
/// changes than a warning-closure batch), this test instead makes the seam OBSERVABLE: it reads
/// <c>App.xaml.cs</c>'s own source text and asserts the wiring line is the exact sanctioned call,
/// so P2 (or a variant of it) fails this test instead of compiling clean and shipping silently.
/// </summary>
/// <remarks>
/// Honest scope: this is a source-text check, not a type-system guarantee. A maintainer willing to
/// edit both <c>App.xaml.cs</c> AND this test in the same change could still defeat it -- that
/// residual risk is named, not hidden. What this test DOES end is the property every prior probe in
/// this chain exploited: a mis-wiring at this exact composition site building with 0 Warning(s) and
/// leaving the WHOLE suite green with no test anywhere noticing. After this fix, it does not.
/// </remarks>
public sealed class CompositionSiteArchitectureTests
{
    private static readonly Regex SanctionedCall = new(
        @"CompositionRoot\.BuildPauseGatedSession\(\s*_workspace,\s*tree,\s*registry,\s*executor,\s*_exceptionStore,\s*_hook\s*\)",
        RegexOptions.Singleline);

    private static readonly Regex AnyAdapterFactoryCall = new(
        @"CompositionRoot\.(BuildPauseGatedSession|BuildSessionAdapter)\(",
        RegexOptions.Singleline);

    private static string ReadAppXamlCsSource([CallerFilePath] string testFilePath = "")
    {
        var testProjectDir = Path.GetDirectoryName(testFilePath)!;
        var appXamlCsPath = Path.GetFullPath(Path.Combine(testProjectDir, "..", "CosmicWin.App", "App.xaml.cs"));
        return File.ReadAllText(appXamlCsPath);
    }

    /// <summary>
    /// Kills verify-report #21 probe P2 verbatim: replacing the sanctioned
    /// <c>BuildPauseGatedSession(_workspace, tree, registry, executor, _exceptionStore, _hook)</c>
    /// call with <c>BuildSessionAdapter(_workspace, tree, registry, executor, _exceptionStore, ()
    /// =&gt; false)</c> (or any other factory-swap / wrong-argument variant) changes the source text
    /// this test reads and fails it -- it no longer builds with 0 Warning(s) and a silently green
    /// suite.
    /// </summary>
    [Fact]
    public void OnStartup_WiresSessionAdapter_ThroughBuildPauseGatedSession_WithTheLiveHook_ExactlyOnce()
    {
        var source = ReadAppXamlCsSource();

        var adapterFactoryCalls = AnyAdapterFactoryCall.Matches(source);
        Assert.True(
            adapterFactoryCalls.Count == 1,
            $"Expected exactly one adapter-construction call in App.xaml.cs, found {adapterFactoryCalls.Count}.");
        Assert.Matches(SanctionedCall, source);
        Assert.DoesNotContain("new WorkspaceSessionAdapter(", source);
    }
}
