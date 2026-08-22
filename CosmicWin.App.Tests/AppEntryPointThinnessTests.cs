using System.Runtime.CompilerServices;

namespace CosmicWin.App.Tests;

/// <summary>
/// Replaces the deleted <c>CompositionSiteArchitectureTests</c> (verify-report #21 revision 15
/// WARNING V15-W1: that test's source-text wiring-correctness assertions were falsified by probes
/// P1/P2/P6, each editing only <c>App.xaml.cs</c>). Wiring CORRECTNESS is now proven by real
/// behavioral facts in <c>AppCompositionTests</c>, which exercise <see
/// cref="AppComposition.Wire"/> directly. This file keeps only what a source-text check can
/// HONESTLY claim: that <c>App.xaml.cs</c> stays a thin WPF entry point and does not grow new
/// composition logic of its own -- a structural regression guard, not a wiring guarantee. Reaching
/// any assertion here still needs a maintainer directly editing <c>App.xaml.cs</c>; no user of
/// CosmicWin can hit it. This test executes no production code -- it is a source-text check by
/// design, same acknowledged limitation as its predecessor (declared, not hidden).
/// </summary>
public sealed class AppEntryPointThinnessTests
{
    private static string ReadAppXamlCsSource([CallerFilePath] string testFilePath = "")
    {
        var testProjectDir = Path.GetDirectoryName(testFilePath)!;
        var appXamlCsPath = Path.GetFullPath(Path.Combine(testProjectDir, "..", "CosmicWin.App", "App.xaml.cs"));
        return File.ReadAllText(appXamlCsPath);
    }

    [Fact]
    public void AppXamlCs_OnStartup_DelegatesToAppCompositionWireProduction_ExactlyOnce()
    {
        var source = ReadAppXamlCsSource();

        var wireProductionCalls = System.Text.RegularExpressions.Regex.Matches(
            source, @"AppComposition\.WireProduction\(");
        Assert.True(
            wireProductionCalls.Count == 1,
            $"Expected exactly one AppComposition.WireProduction(...) call in App.xaml.cs, found {wireProductionCalls.Count}.");
    }

    [Fact]
    public void AppXamlCs_OnStartup_DelegatesToTryHandleTaskCommand_ExactlyOnce()
    {
        // Task 3.20: pins the single delegating call for --install-task/--uninstall-task.
        var source = ReadAppXamlCsSource();

        var taskCommandCalls = System.Text.RegularExpressions.Regex.Matches(
            source, @"AppComposition\.TryHandleTaskCommand\(");
        Assert.True(
            taskCommandCalls.Count == 1,
            $"Expected exactly one AppComposition.TryHandleTaskCommand(...) call in App.xaml.cs, found {taskCommandCalls.Count}.");
    }

    [Fact]
    public void AppXamlCs_DoesNotCallCompositionRootDirectly()
    {
        var source = ReadAppXamlCsSource();

        Assert.DoesNotContain("CompositionRoot.", source);
    }

    [Fact]
    public void AppXamlCs_DoesNotConstructCompositionCollaboratorsDirectly()
    {
        var source = ReadAppXamlCsSource();

        Assert.DoesNotContain("new LowLevelKeyboardHook(", source);
        Assert.DoesNotContain("new WorkspaceSessionAdapter(", source);
        Assert.DoesNotContain("new Win32Workspace(", source);
        Assert.DoesNotContain("new LayoutTree(", source);
    }

    [Fact]
    public void AppXamlCs_HasNoMutableCompositionFields()
    {
        var source = ReadAppXamlCsSource();

        // Structurally, nothing left in App.xaml.cs should be reassignable across two statements
        // the way the old `_hook` field was (verify-report #21 probe P1's exact shape) -- the only
        // field this class should own now is the single opaque, already-disposed-as-a-unit result
        // of AppComposition.WireProduction.
        Assert.DoesNotContain("_hook", source);
        Assert.DoesNotContain("_sessionAdapter", source);
        Assert.DoesNotContain("_workspace", source);
        Assert.DoesNotContain("_exceptionStore", source);
        Assert.DoesNotContain("_dispatcher", source);
    }
}
