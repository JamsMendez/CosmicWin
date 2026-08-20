using CosmicWin.Interop;

namespace CosmicWin.Interop.Tests.TestDoubles;

/// <summary>
/// Hand-rolled in-memory <see cref="IDisplayManager"/> used to pin down the contract before
/// the Win32-backed implementation exists.
/// </summary>
internal sealed class FakeDisplayManager : IDisplayManager
{
    private readonly List<FakeDisplay> _displays;

    public FakeDisplayManager(params FakeDisplay[] displays)
    {
        if (displays.Length == 0)
        {
            throw new ArgumentException("At least one display is required.", nameof(displays));
        }

        _displays = displays.ToList();
    }

    public IReadOnlyList<IDisplay> Displays => _displays;

    public IDisplay Primary => _displays.First(d => d.IsPrimary);
}
