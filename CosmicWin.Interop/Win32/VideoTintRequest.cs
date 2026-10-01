namespace CosmicWin.Interop.Win32;

/// <summary>
/// One immutable "tint these pixels" request: an 8-bit coverage mask plus the tint colour. Instances
/// are published to the player's worker thread by reference swap, so a request is never mutated
/// after construction and "latest wins" is just a reference read.
/// </summary>
internal sealed class VideoTintRequest
{
    /// <summary>Takes a private copy of <paramref name="maskAlpha"/>.</summary>
    /// <exception cref="ArgumentException">Size is not positive or does not match the pixel count.</exception>
    public VideoTintRequest(ReadOnlySpan<byte> maskAlpha, int width, int height, byte r, byte g, byte b)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Mask size must be positive.");
        }

        if (maskAlpha.Length != checked(width * height))
        {
            throw new ArgumentException("Mask length must equal width * height (8 bits per pixel).");
        }

        MaskAlpha = maskAlpha.ToArray();
        Width = width;
        Height = height;
        R = r;
        G = g;
        B = b;
    }

    /// <summary>Coverage per pixel, row-major, tightly packed (pitch == <see cref="Width"/>): 0 = untouched, 255 = fully tinted.</summary>
    public byte[] MaskAlpha { get; }

    public int Width { get; }

    public int Height { get; }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }
}
