using System.IO;

namespace CosmicWin.App.Tests;

public sealed class WorkstationLockGuardTests : IDisposable
{
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("cosmicwin-lock-");

    private string MarkerPath => Path.Combine(_scratch.FullName, "lock-workstation-owned");

    public void Dispose() => _scratch.Delete(recursive: true);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public void DisableThenRestore_PutsBackExactlyWhatWasThere(int? before)
    {
        var store = new FakeLockPolicyStore { Value = before };
        var guard = new WorkstationLockGuard(store, MarkerPath);

        Assert.True(guard.TryDisable());
        Assert.Equal(1, store.Value);
        Assert.True(File.Exists(MarkerPath));

        Assert.True(guard.TryRestore());
        Assert.Equal(before, store.Value);
        Assert.False(File.Exists(MarkerPath));
    }

    /// <summary>A start after a crash disables again; the value to go back to is still the user's own.</summary>
    [Fact]
    public void DisablingTwice_KeepsTheValueFromBeforeTheFirst()
    {
        var store = new FakeLockPolicyStore { Value = null };
        var guard = new WorkstationLockGuard(store, MarkerPath);

        guard.TryDisable();
        guard.TryDisable();
        guard.TryRestore();

        Assert.Null(store.Value);
    }

    /// <summary>Without the marker the value is not CosmicWin's, so it is not CosmicWin's to change.</summary>
    [Fact]
    public void RestoreWithoutHavingDisabled_LeavesTheValueAlone()
    {
        var store = new FakeLockPolicyStore { Value = 1 };
        var guard = new WorkstationLockGuard(store, MarkerPath);

        Assert.True(guard.TryRestore());
        Assert.Equal(1, store.Value);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void AFailedDisable_ChangesNothing_AndLeavesNoMarker()
    {
        var store = new FakeLockPolicyStore { Value = null, FailWrites = true };
        var guard = new WorkstationLockGuard(store, MarkerPath);

        Assert.False(guard.TryDisable());
        Assert.Null(store.Value);
        Assert.False(File.Exists(MarkerPath));
    }

    /// <summary>A restore that could not write keeps the marker, so the next start tries again.</summary>
    [Fact]
    public void AFailedRestore_KeepsTheMarker()
    {
        var store = new FakeLockPolicyStore { Value = null };
        var guard = new WorkstationLockGuard(store, MarkerPath);
        guard.TryDisable();
        store.FailWrites = true;

        Assert.False(guard.TryRestore());
        Assert.True(File.Exists(MarkerPath));
    }

    private sealed class FakeLockPolicyStore : ILockPolicyStore
    {
        public int? Value { get; set; }
        public bool FailWrites { get; set; }
        public int Writes { get; private set; }

        public int? Read() => Value;

        public void Write(int? value)
        {
            if (FailWrites)
            {
                throw new UnauthorizedAccessException();
            }

            Writes++;
            Value = value;
        }
    }
}
