using System.Diagnostics;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Found in the wild, not by reading: a Notepad was still running after a suite had finished, its
/// title still carrying the <c>cosmicwin-&lt;guid&gt;</c> marker <see cref="SpawnedNotepadWindow.Spawn"/>
/// writes, alongside orphaned marker files in the temp directory.
/// </summary>
/// <remarks>
/// <para>
/// A stray top-level window is exactly what breaks a foreground assertion, so this leak was one of
/// the contributors to the desktop-test flakiness -- clearing the debris by hand moved a 0-of-2
/// stretch of solution runs to 1-of-2.
/// </para>
/// <para>
/// The leak is in the timeout path: it deleted the marker file and threw, and never touched the
/// Notepad it had already launched. Deleting the file there can throw too, which would then mask
/// the <see cref="TimeoutException"/> and hide the real diagnosis behind an IO error.
/// </para>
/// <para>
/// Survivors are identified by PROCESS ID, captured before the spawn -- never by the marker in the
/// window title. The first version of this fact used the title and passed while the defect was
/// present: on the timeout path the marker file is deleted before Notepad ever opens it, so the
/// leaked window is titled "Notepad" or nothing at all and carries no marker to match. Two orphaned
/// processes were sitting on the desktop while that version reported green.
/// </para>
/// </remarks>
[Trait("Category", "RequiresDesktop")]
[Collection(RealDesktopCollection.Name)]
public sealed class SpawnedNotepadWindowLeakTests
{
    /// <summary>
    /// Long enough for a launcher stub to hand off and the real window to appear, which is precisely
    /// the interval the leak lives in: the spawn has already given up, and Notepad has not yet shown
    /// the window that would have matched.
    /// </summary>
    private static readonly TimeSpan SettleAfterGivingUp = TimeSpan.FromSeconds(6);

    [RequiresDesktopSessionFact]
    public void Spawn_WhenItGivesUpWaitingForTheWindow_LeavesNoNotepadBehind()
    {
        var before = NotepadProcessIds();

        // One millisecond, so the deadline is already past on the first pass through the wait loop.
        // Notepad has been launched by then and its window has NOT appeared -- the exact ordering
        // the leak needs, made deterministic instead of waited for.
        Assert.Throws<TimeoutException>(() => SpawnedNotepadWindow.Spawn(TimeSpan.FromMilliseconds(1)));

        // The window arrives AFTER the throw. A spawn that only tidied up at the instant it gave up
        // would find nothing to tidy, which is why this waits before looking.
        Thread.Sleep(SettleAfterGivingUp);

        var leaked = NotepadProcessIds().Except(before).ToArray();
        try
        {
            Assert.Empty(leaked);
        }
        finally
        {
            // Never leave the machine dirtier than this fact found it, whatever the verdict. Only
            // ids this fact watched appear, so the maintainer's own Notepad is never touched.
            foreach (var id in leaked)
            {
                try
                {
                    using var stray = Process.GetProcessById(id);
                    stray.Kill();
                    stray.WaitForExit(5000);
                }
                catch
                {
                    // Best effort: the assertion above is what reports the defect.
                }
            }
        }
    }

    private static HashSet<int> NotepadProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var candidate in Process.GetProcessesByName("notepad"))
        {
            using (candidate)
            {
                ids.Add(candidate.Id);
            }
        }

        return ids;
    }
}
