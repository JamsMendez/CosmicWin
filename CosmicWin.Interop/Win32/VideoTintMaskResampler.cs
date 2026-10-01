namespace CosmicWin.Interop.Win32;

/// <summary>Scales an 8-bit coverage mask to another size (nearest neighbour). Pure, no GPU.</summary>
internal static class VideoTintMaskResampler
{
    /// <summary>Returns <paramref name="source"/> itself when the sizes already match.</summary>
    public static byte[] Resample(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
        {
            return source;
        }

        byte[] target = new byte[checked(targetWidth * targetHeight)];

        // Source column per target column, computed once instead of per pixel.
        int[] columns = new int[targetWidth];
        for (int x = 0; x < targetWidth; x++)
        {
            columns[x] = (int)((long)x * sourceWidth / targetWidth);
        }

        for (int y = 0; y < targetHeight; y++)
        {
            int sourceRow = (int)((long)y * sourceHeight / targetHeight) * sourceWidth;
            int targetRow = y * targetWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                target[targetRow + x] = source[sourceRow + columns[x]];
            }
        }

        return target;
    }
}
