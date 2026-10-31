using System.IO;
using System.Security;
using Microsoft.Win32;

namespace CosmicWin.App;

/// <summary>A registry value as found: its data and its kind, so it can be put back unchanged.</summary>
public sealed record PolicyValue(object Data, RegistryValueKind Kind);

/// <summary>The values of the one policy key the guard works in.</summary>
public interface IPolicyKey
{
    /// <summary>The value, or <see langword="null"/> when it is absent.</summary>
    PolicyValue? Read(string name);

    void Write(string name, PolicyValue value);

    /// <summary>Removes the value; an absent one is not an error.</summary>
    void Delete(string name);
}

/// <summary>
/// Turns Windows' manual lock off while Win is the chord modifier, so <c>Win+L</c> reaches the hook
/// instead of locking the session, and puts back exactly what was there before.
/// </summary>
/// <remarks>
/// <para>
/// The policy disables EVERY manual lock -- <c>Win+L</c>, the Lock entries of Start and
/// <c>Ctrl+Alt+Del</c>, and <c>LockWorkStation()</c> -- not only the chord, which is why the tray
/// asks before turning it on. Sign-in after sleep is a separate setting and is not touched.
/// </para>
/// <para>
/// The record of what was there before lives IN the policy key, beside the value it protects, and
/// nowhere else. That key is writable only elevated. A record kept in a file the user's own
/// unelevated programs can write would let any of them hand the elevated CosmicWin a "previous
/// value" of 1, and have the lock switched off for them on the next restore -- exactly the change
/// the key's permissions exist to refuse.
/// </para>
/// <para>
/// <see cref="OwnedValueName"/> is what makes the policy CosmicWin's to change: a restore without it
/// does nothing, so a policy the user set themselves stays theirs. The backup is written before the
/// ownership value and the ownership value before the policy, so a crash at any step leaves either
/// nothing claimed or a claim whose backup is the true previous value.
/// </para>
/// </remarks>
public sealed class WorkstationLockGuard(IPolicyKey key)
{
    public const string PolicyValueName = "DisableLockWorkstation";

    /// <summary>Present exactly while CosmicWin has the policy changed.</summary>
    public const string OwnedValueName = "CosmicWinOwnsDisableLockWorkstation";

    /// <summary>The policy value from before CosmicWin changed it, kind and all; absent when there was none.</summary>
    public const string BackupValueName = "CosmicWinPriorDisableLockWorkstation";

    private static readonly PolicyValue Disabled = new(1, RegistryValueKind.DWord);

    /// <summary>Disables the manual lock; <see langword="false"/> when it could not.</summary>
    public bool TryDisable()
    {
        try
        {
            if (key.Read(OwnedValueName) is null)
            {
                if (key.Read(PolicyValueName) is { } before)
                {
                    key.Write(BackupValueName, before);
                }
                else
                {
                    key.Delete(BackupValueName);
                }

                key.Write(OwnedValueName, Disabled);
            }

            key.Write(PolicyValueName, Disabled);
            return true;
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Puts back the value from before <see cref="TryDisable"/>, if CosmicWin changed it at all.
    /// <see langword="false"/> when a write failed; the ownership then stays for the next attempt.
    /// </summary>
    public bool TryRestore()
    {
        try
        {
            if (key.Read(OwnedValueName) is null)
            {
                return true;
            }

            if (key.Read(BackupValueName) is { } before)
            {
                key.Write(PolicyValueName, before);
                key.Delete(BackupValueName);
            }
            else
            {
                key.Delete(PolicyValueName);
            }

            // Last, so a failure above leaves the claim standing and the next start retries it.
            key.Delete(OwnedValueName);
            return true;
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return false;
        }
    }

    private static bool IsAccessFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;
}

/// <summary>The real policy key, in the current user's hive.</summary>
/// <remarks>
/// Writable only elevated: the user's own token holds just ReadKey on <c>...\Policies</c>, which is
/// what keeps an ordinary program from switching policies off -- and what keeps it from forging the
/// guard's record too. CosmicWin always runs elevated (its manifest requires it), and the elevated
/// token is the same user, so this is the same hive.
/// </remarks>
public sealed class RegistryPolicyKey : IPolicyKey
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

    public PolicyValue? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } data
            ? new PolicyValue(data, key.GetValueKind(name))
            : null;
    }

    public void Write(string name, PolicyValue value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(name, value.Data, value.Kind);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
