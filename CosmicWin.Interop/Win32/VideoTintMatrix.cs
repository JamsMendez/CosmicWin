namespace CosmicWin.Interop.Win32;

/// <summary>
/// Builds the Direct2D ColorMatrix (<c>CLSID_D2D1ColorMatrix</c>) that maps a video pixel to its
/// luminance multiplied by a tint colour. Pure math, no GPU.
/// </summary>
internal static class VideoTintMatrix
{
    /// <summary>Number of floats in a D2D 5x4 matrix.</summary>
    public const int FloatCount = 20;

    /// <summary>Rec. 709 luma weights, the same ones the page-side luminance maths would use.</summary>
    public const float LumaR = 0.2126f;
    public const float LumaG = 0.7152f;
    public const float LumaB = 0.0722f;

    /// <summary>
    /// Returns the matrix as 20 floats, row-major, in Direct2D's row-vector convention:
    /// <c>[R' G' B' A'] = [R G B A 1] * M</c> where M has 5 rows (input R, G, B, A, constant 1) and
    /// 4 columns (output R, G, B, A). So <c>M[input * 4 + output]</c>.
    /// Each of the R, G and B input rows carries its luma weight times the tint channel in every
    /// output colour column, the alpha input row is zero, and the constant row forces output alpha
    /// to 1 (the video is opaque; the mask supplies the coverage later).
    /// </summary>
    public static float[] Create(float tintR, float tintG, float tintB) =>
    [
        LumaR * tintR, LumaR * tintG, LumaR * tintB, 0f,
        LumaG * tintR, LumaG * tintG, LumaG * tintB, 0f,
        LumaB * tintR, LumaB * tintG, LumaB * tintB, 0f,
        0f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f,
    ];

    /// <summary>Same as <see cref="Create(float, float, float)"/> with 0..255 channels.</summary>
    public static float[] Create(byte tintR, byte tintG, byte tintB) =>
        Create(tintR / 255f, tintG / 255f, tintB / 255f);

    /// <summary>The matrix as raw bytes, the form <c>ID2D1Effect.SetValue</c> takes (CsWin32 has no D2D1_MATRIX_5X4_F).</summary>
    public static byte[] ToBytes(float[] matrix)
    {
        byte[] bytes = new byte[matrix.Length * sizeof(float)];
        Buffer.BlockCopy(matrix, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
