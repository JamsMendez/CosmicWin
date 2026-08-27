using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App;

/// <summary>The literal Interop-&gt;Layout adapter design's dependency graph requires (D1/D8) -- the only place an <see cref="IWindow"/> becomes a <see cref="WindowDescriptor"/>. Pure, stateless, no I/O.</summary>
public static class WindowDescriptorBuilder
{
    public static WindowDescriptor Build(IWindow window) =>
        new(window.ClassName, window.ProcessName, window.Title, window.ExStyle, window.Style,
            window.IsOwned, window.Bounds.Width, window.Bounds.Height);
}
