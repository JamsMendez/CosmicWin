using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace CosmicWin.Interop.Win32;

/// <summary>
/// Direct2D implementation of the see-through tint pass (proven by the S1 spike at ~0.35 ms CPU per
/// frame at 3440x1440).
/// </summary>
/// <remarks>
/// <para>
/// Why an intermediate texture: a D2D effect cannot read the surface it renders to, so the video
/// frame is transferred into <see cref="Intermediate"/> (B8G8R8A8, render target + shader resource),
/// then D2D (1) copies it into the back buffer with SOURCE_COPY and (2) draws
/// <c>AlphaMask(ColorMatrix(intermediate), mask)</c> over it with SOURCE_OVER.
/// </para>
/// <para>
/// Why <c>CLSID_D2D1AlphaMask</c> rather than FillOpacityMask or a clip layer: it composes in the same
/// effect graph as the ColorMatrix, takes the mask as an ordinary bitmap input (an 8-bit A8 surface,
/// a quarter of the memory of BGRA), gives soft edges for free (the mask alpha is the blend weight),
/// and needs no layer/geometry objects per frame. Premultiplied SOURCE_OVER then yields
/// <c>tint * a + frame * (1 - a)</c>, so only masked pixels change.
/// </para>
/// <para>
/// Pinning: the target bitmap holds a reference to the swapchain buffer, which would block a
/// ResizeBuffers or keep a replaced swapchain alive. <see cref="Prepare"/> therefore drops every
/// back-buffer-bound object the moment it is handed a different back buffer, and the owning
/// <see cref="VideoTintDriver"/> disposes the whole renderer as soon as the tint is cleared, so
/// nothing pins the swapchain outside an alert. All members run on the player's worker thread.
/// </para>
/// </remarks>
internal sealed unsafe class D2DVideoTintRenderer : IVideoTintRenderer
{
    private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    private static readonly Guid Iid_ID2D1Factory1 = new(0xbb12d362, 0xdaee, 0x4b9a, 0xaa, 0x1d, 0x14, 0xba, 0x40, 0x1c, 0xfa, 0x1f);

    private ID2D1Factory1? _factory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _dc;
    private ID2D1Effect? _colorMatrix;
    private ID2D1Effect? _alphaMask;

    // Back-buffer-bound.
    private ID3D11Texture2D? _intermediate;
    private ID3D11Texture2D? _boundBackBuffer;
    private ID2D1Bitmap1? _targetBitmap;
    private ID2D1Bitmap1? _sourceBitmap;
    private ID2D1Image? _colorMatrixOutput;
    private ID2D1Image? _alphaMaskOutput;
    private uint _width;
    private uint _height;

    // Request-bound.
    private ID2D1Bitmap1? _maskBitmap;
    private VideoTintRequest? _appliedRequest;

    public ID3D11Texture2D? Intermediate => _intermediate;

    public bool Prepare(ID3D11Device device, ID3D11Texture2D backBuffer, in D3D11_TEXTURE2D_DESC desc, VideoTintRequest request)
    {
        if (_dc is null)
        {
            CreateDeviceObjects(device);
        }

        if (_intermediate is null || !ReferenceEquals(_boundBackBuffer, backBuffer)
            || _width != desc.Width || _height != desc.Height)
        {
            BindBackBuffer(device, backBuffer, desc);
        }

        if (!ReferenceEquals(_appliedRequest, request))
        {
            ApplyRequest(request);
        }

        return true;
    }

    public VideoTintRenderResult Render()
    {
        ID2D1DeviceContext? dc = _dc;
        if (dc is null || _targetBitmap is null || _sourceBitmap is null || _alphaMaskOutput is null)
        {
            return VideoTintRenderResult.Failed;
        }

        bool drawing = false;
        try
        {
            dc.SetTarget(_targetBitmap);
            dc.BeginDraw();
            drawing = true;

            dc.DrawImage(_sourceBitmap, null, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_COPY);
            dc.DrawImage(_alphaMaskOutput, null, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);

            drawing = false;
            HRESULT hr = dc.EndDraw(null, null);
            dc.SetTarget(null);
            if (hr.Succeeded)
            {
                return VideoTintRenderResult.Ok;
            }

            return hr.Value == D2DERR_RECREATE_TARGET ? VideoTintRenderResult.RecreateTarget : VideoTintRenderResult.Failed;
        }
        catch (Exception ex)
        {
            if (drawing)
            {
                try { dc.EndDraw(null, null); } catch { }
            }

            return ex.HResult == D2DERR_RECREATE_TARGET ? VideoTintRenderResult.RecreateTarget : VideoTintRenderResult.Failed;
        }
    }

    public void Dispose()
    {
        ReleaseMask();
        ReleaseBackBufferBound();
        Release(_alphaMask);
        Release(_colorMatrix);
        Release(_dc);
        Release(_d2dDevice);
        Release(_factory);
        _alphaMask = null;
        _alphaMaskOutput = null;
        _colorMatrix = null;
        _colorMatrixOutput = null;
        _dc = null;
        _d2dDevice = null;
        _factory = null;
    }

    private void CreateDeviceObjects(ID3D11Device device)
    {
        var options = new D2D1_FACTORY_OPTIONS { debugLevel = D2D1_DEBUG_LEVEL.D2D1_DEBUG_LEVEL_NONE };
        HRESULT hr = PInvoke.D2D1CreateFactory(
            D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, Iid_ID2D1Factory1, options, out object factoryObj);
        Marshal.ThrowExceptionForHR(hr.Value);
        _factory = (ID2D1Factory1)factoryObj;

        _factory.CreateDevice((IDXGIDevice)device, out _d2dDevice);
        _d2dDevice.CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, out _dc);

        Guid colorMatrixClsid = PInvoke.CLSID_D2D1ColorMatrix;
        _dc.CreateEffect(&colorMatrixClsid, out _colorMatrix);
        Guid alphaMaskClsid = PInvoke.CLSID_D2D1AlphaMask;
        _dc.CreateEffect(&alphaMaskClsid, out _alphaMask);

        // An effect's output image IS the effect object (same COM identity, hence the same RCW), so
        // these two are fetched once and never released on their own: releasing one would kill the
        // effect it came from.
        _colorMatrix.GetOutput(out _colorMatrixOutput);
        _alphaMask.GetOutput(out _alphaMaskOutput);
    }

    private void BindBackBuffer(ID3D11Device device, ID3D11Texture2D backBuffer, in D3D11_TEXTURE2D_DESC desc)
    {
        // Release the old buffer's pin FIRST; the mask is sized to the back buffer too.
        ReleaseBackBufferBound();
        ReleaseMask();

        D3D11_TEXTURE2D_DESC td = new()
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        device.CreateTexture2D(in td, null, out ID3D11Texture2D created);
        _intermediate = created;

        var pixelFormat = new D2D1_PIXEL_FORMAT
        {
            format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE,
        };

        var targetProps = new D2D1_BITMAP_PROPERTIES1_unmanaged
        {
            pixelFormat = pixelFormat,
            dpiX = 96f,
            dpiY = 96f,
            bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        };
        _dc!.CreateBitmapFromDxgiSurface((IDXGISurface)backBuffer, &targetProps, out _targetBitmap);

        var sourceProps = new D2D1_BITMAP_PROPERTIES1_unmanaged { pixelFormat = pixelFormat, dpiX = 96f, dpiY = 96f };
        _dc.CreateBitmapFromDxgiSurface((IDXGISurface)_intermediate, &sourceProps, out _sourceBitmap);

        _colorMatrix!.SetInput(0, _sourceBitmap, true);
        _alphaMask!.SetInput(0, _colorMatrixOutput, true);

        _width = desc.Width;
        _height = desc.Height;
        _boundBackBuffer = backBuffer;
        _appliedRequest = null; // the mask bitmap must be rebuilt for the new size
    }

    private void ApplyRequest(VideoTintRequest request)
    {
        byte[] matrix = VideoTintMatrix.ToBytes(VideoTintMatrix.Create(request.R, request.G, request.B));
        _colorMatrix!.SetValue(
            (uint)D2D1_COLORMATRIX_PROP.D2D1_COLORMATRIX_PROP_COLOR_MATRIX,
            D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_UNKNOWN,
            matrix,
            (uint)matrix.Length);

        byte[] mask = VideoTintMaskResampler.Resample(
            request.MaskAlpha, request.Width, request.Height, (int)_width, (int)_height);
        ReleaseMask();
        var props = new D2D1_BITMAP_PROPERTIES1
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = 96f,
            dpiY = 96f,
        };
        fixed (byte* pixels = mask)
        {
            _dc!.CreateBitmap(new D2D_SIZE_U { width = _width, height = _height }, pixels, _width, in props, out _maskBitmap);
        }

        _alphaMask!.SetInput(1, _maskBitmap, true);
        _appliedRequest = request;
    }

    private void ReleaseMask()
    {
        try { _alphaMask?.SetInput(1, null, true); } catch { }
        Release(_maskBitmap);
        _maskBitmap = null;
        _appliedRequest = null;
    }

    private void ReleaseBackBufferBound()
    {
        // Detach the effect inputs first so no effect keeps a bitmap (and through it the buffer) alive.
        try { _dc?.SetTarget(null); } catch { }
        try { _colorMatrix?.SetInput(0, null, true); } catch { }
        try { _alphaMask?.SetInput(0, null, true); } catch { }
        Release(_sourceBitmap);
        Release(_targetBitmap);
        Release(_intermediate);
        _sourceBitmap = null;
        _targetBitmap = null;
        _intermediate = null;
        _boundBackBuffer = null;
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null)
        {
            try { Marshal.FinalReleaseComObject(comObject); } catch { }
        }
    }
}
