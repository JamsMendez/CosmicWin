using System.Diagnostics;
using System.Threading.Channels;
using CosmicWin.App.Input;

namespace CosmicWin.App.Tests.Input;

[Collection(Desktop.RealDesktopCollection.Name)]
public sealed class LowLevelKeyboardHookDesktopTests
{
    [RequiresDesktopOptInFact]
    [Trait("Category", "RequiresDesktop")]
    public void StartAndDispose_WithRealNotepadWindow_InstallsAndUninstallsBoundedly()
    {
        var marker = $"cosmicwin-hook-{Guid.NewGuid():N}";
        var path = Path.Combine(Path.GetTempPath(), $"{marker}.txt");
        File.WriteAllText(path, string.Empty);
        Process? owner = null;
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            owner = FindWindowOwner(marker, TimeSpan.FromSeconds(10));
            var hook = new LowLevelKeyboardHook(Channel.CreateBounded<HotkeyAction>(32).Writer);

            hook.Start();
            hook.Dispose();

            Assert.True(hook.UnhookSucceeded);
        }
        finally
        {
            if (owner is not null)
            {
                owner.CloseMainWindow();
                owner.WaitForExit(5000);
                owner.Dispose();
            }
            File.Delete(path);
        }
    }

    private static Process FindWindowOwner(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var process in Process.GetProcessesByName("notepad"))
            {
                process.Refresh();
                if (process.MainWindowHandle != 0 &&
                    process.MainWindowTitle.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return process;
                process.Dispose();
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException("Timed out waiting for the Notepad window used by the hook test.");
    }
}
