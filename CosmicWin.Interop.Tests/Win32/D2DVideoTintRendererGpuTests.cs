using CosmicWin.Interop.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Skips (never fails) when no hardware D3D11 device with BGRA support can be created. Needs a GPU,
/// not a desktop session, so it is deliberately NOT <see cref="DesktopGate"/>.
/// </summary>
internal sealed class RequiresHardwareD3D11FactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Probe = new(() =>
    {
        try
        {
            using var gpu = OffscreenGpu.TryCreate();
            return gpu is null ? "No hardware D3D11 device with BGRA support on this machine." : null;
        }
        catch (Exception ex)
        {
            return "D3D11 probe threw: " + ex.GetType().Name;
        }
    });

    public RequiresHardwareD3D11FactAttribute()
    {
        if (Probe.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>A hardware D3D11 device plus helpers to upload to and read back offscreen BGRA textures.</summary>
internal sealed unsafe class OffscreenGpu : IDisposable
{
    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    private OffscreenGpu(ID3D11Device device, ID3D11DeviceContext context)
    {
        Device = device;
        Context = context;
    }

    public static OffscreenGpu? TryCreate()
    {
        HRESULT hr = PInvoke.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            null,
            0,
            PInvoke.D3D11_SDK_VERSION,
            out ID3D11Device? device,
            null,
            out ID3D11DeviceContext? context);
        return hr.Failed || device is null || context is null ? null : new OffscreenGpu(device, context);
    }

    /// <summary>Stands in for the swapchain back buffer (render-target capable, B8G8R8A8).</summary>
    public ID3D11Texture2D CreateBackBuffer(int width, int height)
    {
        D3D11_TEXTURE2D_DESC td = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        Device.CreateTexture2D(in td, null, out ID3D11Texture2D texture);
        return texture;
    }

    /// <summary>Writes tightly packed BGRA pixels (4 bytes each) into <paramref name="texture"/>.</summary>
    public void Upload(ID3D11Texture2D texture, byte[] bgra, int width)
    {
        fixed (byte* p = bgra)
        {
            Context.UpdateSubresource(texture, 0, null, p, (uint)(width * 4), 0);
        }
    }

    public byte[] ReadBack(ID3D11Texture2D texture, int width, int height)
    {
        D3D11_TEXTURE2D_DESC td = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        Device.CreateTexture2D(in td, null, out ID3D11Texture2D staging);
        Context.CopyResource(staging, texture);
        Context.Map(staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
        byte[] result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            new ReadOnlySpan<byte>((byte*)mapped.pData + y * mapped.RowPitch, width * 4)
                .CopyTo(result.AsSpan(y * width * 4, width * 4));
        }

        Context.Unmap(staging, 0);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(staging);
        return result;
    }

    public void Dispose()
    {
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(Context);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(Device);
    }
}

public sealed unsafe class D2DVideoTintRendererGpuTests
{
    private const int W = 64;
    private const int H = 32;

    // Source columns (each 16 px wide): white, mid grey, black, a colour.
    private static readonly (byte B, byte G, byte R)[] Columns =
    [
        (255, 255, 255),
        (128, 128, 128),
        (0, 0, 0),
        (50, 100, 200),
    ];

    private static byte[] SourceFrame()
    {
        byte[] px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                var c = Columns[x / 16];
                int i = (y * W + x) * 4;
                px[i] = c.B;
                px[i + 1] = c.G;
                px[i + 2] = c.R;
                px[i + 3] = 255;
            }
        }

        return px;
    }

    private static (byte R, byte G, byte B) Expected((byte B, byte G, byte R) src, (byte R, byte G, byte B) tint, double weight)
    {
        double l = 0.2126 * src.R + 0.7152 * src.G + 0.0722 * src.B;
        double lit = 0.25 * 255 + 0.75 * l; // 25% brightness floor (maintainer decision 2026-10-02)
        double Mix(double srcChannel, byte tintChannel) => srcChannel * (1 - weight) + lit * tintChannel / 255.0 * weight;
        return ((byte)Math.Round(Mix(src.R, tint.R)), (byte)Math.Round(Mix(src.G, tint.G)), (byte)Math.Round(Mix(src.B, tint.B)));
    }

    private static void AssertPixel(byte[] bgra, int x, int y, (byte R, byte G, byte B) expected)
    {
        int i = (y * W + x) * 4;
        Assert.InRange(bgra[i + 2], expected.R - 2, expected.R + 2);
        Assert.InRange(bgra[i + 1], expected.G - 2, expected.G + 2);
        Assert.InRange(bgra[i], expected.B - 2, expected.B + 2);
    }

    private static D3D11_TEXTURE2D_DESC DescOf(ID3D11Texture2D t)
    {
        t.GetDesc(out D3D11_TEXTURE2D_DESC d);
        return d;
    }

    private static byte[] LeftHalfMask(byte alpha, int w = W, int h = H)
    {
        byte[] m = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w / 2; x++)
            {
                m[y * w + x] = alpha;
            }
        }

        return m;
    }

    /// <summary>Runs one Prepare/upload/Render and returns the back buffer pixels.</summary>
    private static byte[] RunOnce(OffscreenGpu gpu, D2DVideoTintRenderer renderer, ID3D11Texture2D backBuffer, VideoTintRequest request)
    {
        Assert.True(renderer.Prepare(gpu.Device, backBuffer, DescOf(backBuffer), request));
        gpu.Upload(renderer.Intermediate!, SourceFrame(), W);
        Assert.Equal(VideoTintRenderResult.Ok, renderer.Render());
        return gpu.ReadBack(backBuffer, W, H);
    }

    [RequiresHardwareD3D11Fact]
    public void HardMask_TintsOnlyMaskedPixelsByLuminance_AndLeavesTheRestUntouched()
    {
        using var gpu = OffscreenGpu.TryCreate()!;
        using var renderer = new D2DVideoTintRenderer();
        var backBuffer = gpu.CreateBackBuffer(W, H);
        var tint = (R: (byte)51, G: (byte)115, B: (byte)255);
        var request = new VideoTintRequest(LeftHalfMask(255), W, H, tint.R, tint.G, tint.B);

        byte[] pixels = RunOnce(gpu, renderer, backBuffer, request);

        // Masked (left half): columns 0 (white) and 1 (grey) -> L * tint.
        AssertPixel(pixels, 5, 10, Expected(Columns[0], tint, 1));
        AssertPixel(pixels, 20, 10, Expected(Columns[1], tint, 1));
        // Unmasked (right half): untouched video.
        AssertPixel(pixels, 40, 10, (0, 0, 0));
        AssertPixel(pixels, 60, 10, (200, 100, 50));
    }

    [RequiresHardwareD3D11Fact]
    public void SoftMask_BlendsByMaskAlpha()
    {
        using var gpu = OffscreenGpu.TryCreate()!;
        using var renderer = new D2DVideoTintRenderer();
        var backBuffer = gpu.CreateBackBuffer(W, H);
        var tint = (R: (byte)51, G: (byte)115, B: (byte)255);
        byte[] mask = new byte[W * H];
        Array.Fill(mask, (byte)128, 0, W * H); // everything at ~50% coverage
        var request = new VideoTintRequest(mask, W, H, tint.R, tint.G, tint.B);

        byte[] pixels = RunOnce(gpu, renderer, backBuffer, request);

        double w = 128 / 255.0;
        AssertPixel(pixels, 5, 10, Expected(Columns[0], tint, w));
        AssertPixel(pixels, 60, 10, Expected(Columns[3], tint, w));
    }

    [RequiresHardwareD3D11Fact]
    public void MaskOfAnotherSize_IsScaledToTheBackBuffer()
    {
        using var gpu = OffscreenGpu.TryCreate()!;
        using var renderer = new D2DVideoTintRenderer();
        var backBuffer = gpu.CreateBackBuffer(W, H);
        var tint = (R: (byte)255, G: (byte)0, B: (byte)128);
        var request = new VideoTintRequest(LeftHalfMask(255, W / 2, H / 2), W / 2, H / 2, tint.R, tint.G, tint.B);

        byte[] pixels = RunOnce(gpu, renderer, backBuffer, request);

        AssertPixel(pixels, 20, 10, Expected(Columns[1], tint, 1));
        AssertPixel(pixels, 40, 10, (0, 0, 0));
        AssertPixel(pixels, 60, 10, (200, 100, 50));
    }

    [RequiresHardwareD3D11Fact]
    public void ANewRequest_ReplacesMaskAndColour_OnTheSameRenderer()
    {
        using var gpu = OffscreenGpu.TryCreate()!;
        using var renderer = new D2DVideoTintRenderer();
        var backBuffer = gpu.CreateBackBuffer(W, H);
        RunOnce(gpu, renderer, backBuffer, new VideoTintRequest(LeftHalfMask(255), W, H, 255, 0, 0));
        var tint = (R: (byte)0, G: (byte)255, B: (byte)0);

        byte[] pixels = RunOnce(gpu, renderer, backBuffer, new VideoTintRequest(LeftHalfMask(255), W, H, tint.R, tint.G, tint.B));

        AssertPixel(pixels, 5, 10, Expected(Columns[0], tint, 1));
    }

    [RequiresHardwareD3D11Fact]
    public void ADifferentBackBuffer_IsRebound_AndTheOldOneIsReleased()
    {
        using var gpu = OffscreenGpu.TryCreate()!;
        using var renderer = new D2DVideoTintRenderer();
        var first = gpu.CreateBackBuffer(W, H);
        uint baseline = WarmedRefCount(gpu, first);
        var request = new VideoTintRequest(LeftHalfMask(255), W, H, 51, 115, 255);
        RunOnce(gpu, renderer, first, request);
        Assert.True(RefCount(first) > baseline, "sanity: the renderer should pin the buffer while bound");

        var second = gpu.CreateBackBuffer(W, H);
        byte[] pixels = RunOnce(gpu, renderer, second, request);

        AssertPixel(pixels, 5, 10, Expected(Columns[0], (51, 115, 255), 1));
        gpu.Context.ClearState();
        gpu.Context.Flush();
        // Rebinding must drop the pin on the old buffer (a swapchain cannot resize while pinned).
        Assert.Equal(baseline, RefCount(first));
    }

    [RequiresHardwareD3D11Fact]
    public void Dispose_ReleasesTheBackBuffer()
    {
        using var gpu = OffscreenGpu.TryCreate()!;
        var renderer = new D2DVideoTintRenderer();
        var backBuffer = gpu.CreateBackBuffer(W, H);
        uint baseline = WarmedRefCount(gpu, backBuffer);
        RunOnce(gpu, renderer, backBuffer, new VideoTintRequest(LeftHalfMask(255), W, H, 1, 2, 3));

        renderer.Dispose();
        gpu.Context.ClearState();
        gpu.Context.Flush();

        Assert.Equal(baseline, RefCount(backBuffer));
    }

    /// <summary>
    /// The RCW caches every interface it was ever queried for, each holding a native reference, so
    /// the baseline must be taken after the same interfaces were touched once.
    /// </summary>
    private static uint WarmedRefCount(OffscreenGpu gpu, ID3D11Texture2D texture)
    {
        gpu.Upload(texture, SourceFrame(), W);
        gpu.ReadBack(texture, W, H);
        _ = (Windows.Win32.Graphics.Dxgi.IDXGISurface)texture;
        gpu.Context.ClearState();
        gpu.Context.Flush();
        return RefCount(texture);
    }

    /// <summary>
    /// Native reference count of the texture's COM object, excluding the probe's own reference. A
    /// D2D bitmap pinning the surface shows up as extra references.
    /// </summary>
    private static uint RefCount(ID3D11Texture2D texture)
    {
        nint unknown = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(texture);
        return (uint)System.Runtime.InteropServices.Marshal.Release(unknown);
    }
}
