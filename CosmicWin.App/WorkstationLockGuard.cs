using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace CosmicWin.App;

/// <summary>The <c>DisableLockWorkstation</c> policy value: <see langword="null"/> when absent.</summary>
public interface ILockPolicyStore
{
    int? Read();

    /// <summary>Writes <paramref name="value"/>, or deletes the value for <see langword="null"/>.</summary>
    void Write(int? value);
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
/// A marker file beside <c>settings.conf</c> records the value from before the first disable, and
/// its presence is what makes the value CosmicWin's to change: a restore without it does nothing,
/// so a user who set the policy themselves keeps it. Written BEFORE the registry, so a crash between
/// the two leaves at worst a marker holding the true previous value.
/// </para>
/// </remarks>
public sealed class WorkstationLockGuard(ILockPolicyStore store, string markerPath)
{
    /// <summary>Beside <c>settings.conf</c>, in CosmicWin's own folder.</summary>
    public static string ResolveMarkerPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CosmicWin",
            "lock-workstation-owned");

    /// <summary>Disables the manual lock; <see langword="false"/> when it could not, with nothing changed.</summary>
    public bool TryDisable()
    {
        var createdMarker = false;
        try
        {
            if (!File.Exists(markerPath))
            {
                var before = store.Read();
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
                File.WriteAllText(markerPath, before?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                createdMarker = true;
            }

            store.Write(1);
            return true;
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            if (createdMarker)
            {
                TryDeleteMarker();
            }

            return false;
        }
    }

    /// <summary>
    /// Puts back the value from before <see cref="TryDisable"/>, if CosmicWin changed it at all.
    /// <see langword="false"/> when the write failed; the marker then stays for the next attempt.
    /// </summary>
    public bool TryRestore()
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return true;
            }

            var text = File.ReadAllText(markerPath).Trim();
            int? before = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
            store.Write(before);
            File.Delete(markerPath);
            return true;
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return false;
        }
    }

    private void TryDeleteMarker()
    {
        try
        {
            File.Delete(markerPath);
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
        }
    }

    private static bool IsAccessFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;
}

/// <summary>The real policy value, in the current user's hive: no elevation needed.</summary>
public sealed class RegistryLockPolicyStore : ILockPolicyStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

    private const string ValueName = "DisableLockWorkstation";

    public int? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is int value ? value : null;
    }

    public void Write(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (value is { } number)
        {
            key.SetValue(ValueName, number, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
