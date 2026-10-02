using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CosmicWin.App.Alerts;

/// <summary>
/// see-through-video-tint (S4): decodes the PNG data URL the alert page exports (opaque letters on a
/// transparent canvas) into a bare 8-bit alpha mask -- row-major, tightly packed, top row first, the
/// layout <see cref="IAlertTintSink"/> expects. Pure and thread-agnostic: nothing here touches a
/// dispatcher, so the coordinator runs it off the UI thread.
/// </summary>
internal static class AlertMaskDecoder
{
    private const string Prefix = "data:image/png;base64,";

    /// <summary>Largest mask the host accepts, in pixels (a 6880x2880 canvas is ~20M).</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>
    /// <see langword="false"/> (and an empty <paramref name="alpha"/>) for anything that is not a PNG
    /// data URL of exactly <paramref name="expectedWidth"/> x <paramref name="expectedHeight"/>; never throws.
    /// </summary>
    public static bool TryDecodeAlpha(string dataUrl, int expectedWidth, int expectedHeight, out byte[] alpha)
    {
        alpha = [];
        try
        {
            if (expectedWidth <= 0 || expectedHeight <= 0 || (long)expectedWidth * expectedHeight > MaxPixels) return false;
            if (dataUrl is null || !dataUrl.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            var bytes = Convert.FromBase64String(dataUrl[Prefix.Length..]);
            using var stream = new MemoryStream(bytes);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.Default);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != expectedWidth || frame.PixelHeight != expectedHeight) return false;

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = expectedWidth * 4;
            var pixels = new byte[stride * expectedHeight];
            converted.CopyPixels(pixels, stride, 0);
            var result = new byte[expectedWidth * expectedHeight];
            for (var i = 0; i < result.Length; i++) result[i] = pixels[i * 4 + 3];
            alpha = result;
            return true;
        }
        catch (Exception)
        {
            // FormatException (bad base64), NotSupportedException / IOException (not a PNG), ...: all
            // just "not a usable mask".
            alpha = [];
            return false;
        }
    }
}
