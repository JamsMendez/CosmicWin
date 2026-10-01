using System.Runtime.InteropServices;
using CosmicWin.Interop.Win32.VirtualDesktops;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// After an Explorer restart the static <c>IVirtualDesktopManager</c> is a dead proxy. A
/// disconnect-class failure must drop it, create a fresh one and retry once; any other failure must
/// not. The factory seam stands a fake in for the shell, so no desktop is needed.
/// </summary>
/// <remarks>
/// Serialised in one collection because the seam is static state -- the same state production
/// caches the manager in.
/// </remarks>
[Collection("VirtualDesktopQueriesStatic")]
public sealed class Win32VirtualDesktopQueriesReconnectTests : IDisposable
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private static readonly Guid Desktop = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private int _created;

    public void Dispose() => Win32VirtualDesktopQueries.UseFactoryForTests(null);

    private void Use(params FakeManager[] managers)
    {
        var queue = new Queue<FakeManager>(managers);
        Win32VirtualDesktopQueries.UseFactoryForTests(() =>
        {
            _created++;
            return queue.Count > 0 ? queue.Dequeue() : null;
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryGetWindowDesktopId_reconnects_once_and_answers(bool thrown)
    {
        Use(new FakeManager { DeadHr = ShellDisconnect.ServerUnavailable, DeadThrows = thrown }, new FakeManager());

        var ok = Win32VirtualDesktopQueries.TryGetWindowDesktopId(0x50A22, out var id, out var error);

        Assert.True(ok);
        Assert.Equal(Desktop, id);
        Assert.Null(error);
        Assert.Equal(2, _created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryIsWindowOnCurrentDesktop_reconnects_once_and_answers(bool thrown)
    {
        Use(new FakeManager { DeadHr = ShellDisconnect.Disconnected, DeadThrows = thrown }, new FakeManager());

        var ok = Win32VirtualDesktopQueries.TryIsWindowOnCurrentDesktop(0x50A22, out var onCurrent, out var error);

        Assert.True(ok);
        Assert.True(onCurrent);
        Assert.Null(error);
        Assert.Equal(2, _created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_non_disconnect_failure_does_not_reconnect(bool thrown)
    {
        Use(new FakeManager { DeadHr = E_ACCESSDENIED, DeadThrows = thrown }, new FakeManager());

        var ok = Win32VirtualDesktopQueries.TryGetWindowDesktopId(0x50A22, out _, out var error);

        Assert.False(ok);
        Assert.Equal(1, _created);
        Assert.Contains("0x80070005", error);
    }

    [Fact]
    public void A_shell_that_is_still_dead_reconnects_once_and_says_so()
    {
        Use(
            new FakeManager { DeadHr = ShellDisconnect.ServerUnavailable },
            new FakeManager { DeadHr = ShellDisconnect.ServerUnavailable },
            new FakeManager());

        var ok = Win32VirtualDesktopQueries.TryGetWindowDesktopId(0x50A22, out _, out var error);

        Assert.False(ok);
        Assert.Equal(2, _created);
        Assert.Contains("after reconnect", error);
        Assert.Contains("0x800706BA", error);
    }

    [Fact]
    public void A_reconnect_that_cannot_create_a_manager_reports_why()
    {
        Use(new FakeManager { DeadHr = ShellDisconnect.ServerUnavailable });

        var ok = Win32VirtualDesktopQueries.TryIsWindowOnCurrentDesktop(0x50A22, out _, out var error);

        Assert.False(ok);
        Assert.Equal(2, _created);
        Assert.Contains("could not be created", error);
    }

    [Fact]
    public void The_recreated_manager_is_kept_for_the_next_call()
    {
        Use(new FakeManager { DeadHr = ShellDisconnect.ServerUnavailable }, new FakeManager());

        _ = Win32VirtualDesktopQueries.TryGetWindowDesktopId(0x50A22, out _, out _);
        _ = Win32VirtualDesktopQueries.TryGetWindowDesktopId(0x50A22, out _, out _);

        Assert.Equal(2, _created);
    }

    private sealed class FakeManager : IVirtualDesktopManager
    {
        public int? DeadHr { get; init; }

        public bool DeadThrows { get; init; } = true;

        private int Fail() => DeadThrows ? throw new COMException("dead", DeadHr!.Value) : DeadHr!.Value;

        public int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop)
        {
            onCurrentDesktop = 0;
            if (DeadHr is not null)
            {
                return Fail();
            }

            onCurrentDesktop = 1;
            return 0;
        }

        public int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId)
        {
            desktopId = Guid.Empty;
            if (DeadHr is not null)
            {
                return Fail();
            }

            desktopId = Desktop;
            return 0;
        }

        public int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId) => throw new NotSupportedException();
    }
}
