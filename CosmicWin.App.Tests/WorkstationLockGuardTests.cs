using Microsoft.Win32;

namespace CosmicWin.App.Tests;

public sealed class WorkstationLockGuardTests
{
    private const string Policy = WorkstationLockGuard.PolicyValueName;

    public static TheoryData<object?, RegistryValueKind> PriorValues => new()
    {
        { null, RegistryValueKind.Unknown },
        { 0, RegistryValueKind.DWord },
        { 1, RegistryValueKind.DWord },
        // Not what Windows writes, but what a hand edit or a script can leave: put back as it was,
        // never deleted for being the wrong type.
        { "1", RegistryValueKind.String },
        { 1L, RegistryValueKind.QWord },
    };

    [Theory]
    [MemberData(nameof(PriorValues))]
    public void DisableThenRestore_PutsBackExactlyWhatWasThere(object? before, RegistryValueKind kind)
    {
        var store = new FakePolicyKey();
        if (before is not null)
        {
            store.Values[Policy] = new PolicyValue(before, kind);
        }

        var guard = new WorkstationLockGuard(store);

        Assert.True(guard.TryDisable());
        Assert.Equal(new PolicyValue(1, RegistryValueKind.DWord), store.Values[Policy]);

        Assert.True(guard.TryRestore());
        if (before is null)
        {
            Assert.False(store.Values.ContainsKey(Policy));
        }
        else
        {
            Assert.Equal(new PolicyValue(before, kind), store.Values[Policy]);
        }

        // Nothing of CosmicWin's is left in the key.
        Assert.Equal(before is null ? 0 : 1, store.Values.Count);
    }

    /// <summary>A start after a crash disables again; the value to go back to is still the user's own.</summary>
    [Fact]
    public void DisablingTwice_KeepsTheValueFromBeforeTheFirst()
    {
        var store = new FakePolicyKey();
        var guard = new WorkstationLockGuard(store);

        guard.TryDisable();
        guard.TryDisable();
        guard.TryRestore();

        Assert.Empty(store.Values);
    }

    /// <summary>Without the ownership value the policy is not CosmicWin's, so it is not CosmicWin's to change.</summary>
    [Fact]
    public void RestoreWithoutHavingDisabled_LeavesTheValueAlone()
    {
        var store = new FakePolicyKey();
        store.Values[Policy] = new PolicyValue(1, RegistryValueKind.DWord);
        var guard = new WorkstationLockGuard(store);

        Assert.True(guard.TryRestore());
        Assert.Equal(new PolicyValue(1, RegistryValueKind.DWord), store.Values[Policy]);
        Assert.Equal(0, store.Writes);
    }

    /// <summary>
    /// A backup with no ownership value is not CosmicWin's record and must not be written back
    /// into the policy -- the shape a planted record would have.
    /// </summary>
    [Fact]
    public void AnUnownedBackup_IsIgnoredByRestore()
    {
        var store = new FakePolicyKey();
        store.Values[WorkstationLockGuard.BackupValueName] = new PolicyValue(1, RegistryValueKind.DWord);
        var guard = new WorkstationLockGuard(store);

        Assert.True(guard.TryRestore());
        Assert.False(store.Values.ContainsKey(Policy));
    }

    [Fact]
    public void AFailedDisable_ChangesNothing()
    {
        var store = new FakePolicyKey { FailWrites = true };
        var guard = new WorkstationLockGuard(store);

        Assert.False(guard.TryDisable());
        Assert.Empty(store.Values);
    }

    /// <summary>A restore that could not write keeps the ownership record, so the next start tries again.</summary>
    [Fact]
    public void AFailedRestore_KeepsTheOwnership()
    {
        var store = new FakePolicyKey();
        var guard = new WorkstationLockGuard(store);
        guard.TryDisable();
        store.FailWrites = true;

        Assert.False(guard.TryRestore());
        Assert.True(store.Values.ContainsKey(WorkstationLockGuard.OwnedValueName));

        store.FailWrites = false;
        Assert.True(guard.TryRestore());
        Assert.Empty(store.Values);
    }

    private sealed class FakePolicyKey : IPolicyKey
    {
        public Dictionary<string, PolicyValue> Values { get; } = [];
        public bool FailWrites { get; set; }
        public int Writes { get; private set; }

        public PolicyValue? Read(string name) => Values.TryGetValue(name, out var value) ? value : null;

        public void Write(string name, PolicyValue value)
        {
            Fail();
            Values[name] = value;
        }

        public void Delete(string name)
        {
            Fail();
            Values.Remove(name);
        }

        private void Fail()
        {
            if (FailWrites)
            {
                throw new UnauthorizedAccessException();
            }

            Writes++;
        }
    }
}
