using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace CosmicWin.Interop.Win32;

/// <summary>Draws the current alert tile snapshot over a D3D11 back buffer using Direct2D.</summary>
public sealed unsafe class Direct2DAlertOverlay : IFrameOverlay, IDisposable
{
    private static readonly Lazy<string> s_logPath = new(() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CosmicWin",
        "alert-overlay.log"));

    private readonly object _tilesLock = new();
    private FrameOverlayTile[] _tiles = [];

    private nint _deviceIdentity;
    private ID2D1Factory1? _factory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _deviceContext;
    private IDWriteFactory? _writeFactory;
    private IDWriteTextFormat? _textFormat;
    private ID2D1SolidColorBrush? _warningFillBrush;
    private ID2D1SolidColorBrush? _warningBorderBrush;
    private ID2D1SolidColorBrush? _failedFillBrush;
    private ID2D1SolidColorBrush? _failedBorderBrush;
    private ID2D1SolidColorBrush? _textBrush;

    private ID3D11Texture2D? _cachedBackBuffer;
    private ID2D1Bitmap1? _targetBitmap;
    private D2D1_ALPHA_MODE _workingAlphaMode;

    private string? _firstFailureText;
    private long _failureCount;
    private bool _disposed;

    public int TileCount => Volatile.Read(ref _tiles).Length;

    public void SetTiles(IEnumerable<FrameOverlayTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        FrameOverlayTile[] snapshot = tiles
            .Where(static tile => tile.Bounds.Width > 0 && tile.Bounds.Height > 0)
            .ToArray();

        lock (_tilesLock)
        {
            _tiles = snapshot;
        }
    }

    public void Clear()
    {
        lock (_tilesLock)
        {
            _tiles = [];
        }
    }

    void IFrameOverlay.Draw(ID3D11Texture2D backBuffer, RECT destination)
    {
        FrameOverlayTile[] tiles = Volatile.Read(ref _tiles);
        if (tiles.Length == 0 || _disposed)
        {
            return;
        }

        try
        {
            EnsureDeviceResources(backBuffer);
            EnsureTargetBitmap(backBuffer);

            _deviceContext!.SetTarget((ID2D1Image)_targetBitmap!);
            _deviceContext.BeginDraw();
            try
            {
                foreach (FrameOverlayTile tile in tiles)
                {
                    DrawTile(tile, destination);
                }
            }
            finally
            {
                _deviceContext.EndDraw((ulong*)null, (ulong*)null).ThrowOnFailure();
            }
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            ReleaseAllResources();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseAllResources();
    }

    private void DrawTile(FrameOverlayTile tile, RECT destination)
    {
        D2D_RECT_F rect = ClipToDestination(tile.Bounds, destination);
        if (rect.right <= rect.left || rect.bottom <= rect.top)
        {
            return;
        }

        ID2D1SolidColorBrush fillBrush = tile.Kind == FrameOverlayTileKind.Failed
            ? _failedFillBrush!
            : _warningFillBrush!;
        ID2D1SolidColorBrush borderBrush = tile.Kind == FrameOverlayTileKind.Failed
            ? _failedBorderBrush!
            : _warningBorderBrush!;

        _deviceContext!.FillRectangle(&rect, fillBrush);
        _deviceContext.DrawRectangle(&rect, borderBrush, 2.0f, null);

        string text = string.IsNullOrWhiteSpace(tile.Label) ? DefaultLabel(tile.Kind) : tile.Label!;
        fixed (char* textPtr = text)
        {
            _deviceContext.DrawText(
                textPtr,
                (uint)text.Length,
                _textFormat,
                &rect,
                _textBrush,
                D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
        }
    }

    private static D2D_RECT_F ClipToDestination(Rectangle bounds, RECT destination)
    {
        float left = Math.Max(bounds.Left, destination.left);
        float top = Math.Max(bounds.Top, destination.top);
        float right = Math.Min(bounds.Right, destination.right);
        float bottom = Math.Min(bounds.Bottom, destination.bottom);

        return new D2D_RECT_F
        {
            left = left,
            top = top,
            right = right,
            bottom = bottom,
        };
    }

    private static string DefaultLabel(FrameOverlayTileKind kind) =>
        kind == FrameOverlayTileKind.Failed ? "FAILED" : "WARNING";

    private void EnsureDeviceResources(ID3D11Texture2D backBuffer)
    {
        nint currentDeviceIdentity = GetDeviceIdentity(backBuffer);
        if (_deviceContext is not null && currentDeviceIdentity == _deviceIdentity)
        {
            return;
        }

        ReleaseDeviceResources();

        var deviceChild = (ID3D11DeviceChild)backBuffer;
        deviceChild.GetDevice(out ID3D11Device d3dDevice);
        try
        {
            var dxgiDevice = (IDXGIDevice)d3dDevice;
            try
            {
                Guid factoryIid = typeof(ID2D1Factory1).GUID;
                PInvoke.D2D1CreateFactory(
                    D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED,
                    factoryIid,
                    null,
                    out object factoryObj);
                _factory = (ID2D1Factory1)factoryObj;

                _factory.CreateDevice(dxgiDevice, out ID2D1Device d2dDevice);
                _d2dDevice = d2dDevice;

                _d2dDevice.CreateDeviceContext(
                    D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
                    out ID2D1DeviceContext deviceContext);
                _deviceContext = deviceContext;

                PInvoke.DWriteCreateFactory(
                    DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED,
                    out IDWriteFactory writeFactory);
                _writeFactory = writeFactory;

                _writeFactory.CreateTextFormat(
                    "Segoe UI",
                    null,
                    DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD,
                    DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                    22.0f,
                    "en-us",
                    out IDWriteTextFormat textFormat);
                _textFormat = textFormat;
                _textFormat.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
                _textFormat.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

                CreateBrushes(_deviceContext);
                _deviceIdentity = currentDeviceIdentity;

                ReleaseComObject(_targetBitmap);
                _targetBitmap = null;
                _cachedBackBuffer = null;
            }
            finally
            {
                ReleaseComObject(dxgiDevice);
            }
        }
        finally
        {
            ReleaseComObject(d3dDevice);
        }
    }

    private void CreateBrushes(ID2D1DeviceContext deviceContext)
    {
        D2D1_COLOR_F warningFill = new() { r = 1.0f, g = 0.78f, b = 0.08f, a = 0.32f };
        deviceContext.CreateSolidColorBrush(&warningFill, null, out _warningFillBrush);

        D2D1_COLOR_F warningBorder = new() { r = 1.0f, g = 0.86f, b = 0.24f, a = 0.95f };
        deviceContext.CreateSolidColorBrush(&warningBorder, null, out _warningBorderBrush);

        D2D1_COLOR_F failedFill = new() { r = 1.0f, g = 0.0f, b = 0.0f, a = 0.35f };
        deviceContext.CreateSolidColorBrush(&failedFill, null, out _failedFillBrush);

        D2D1_COLOR_F failedBorder = new() { r = 1.0f, g = 0.18f, b = 0.14f, a = 0.95f };
        deviceContext.CreateSolidColorBrush(&failedBorder, null, out _failedBorderBrush);

        D2D1_COLOR_F text = new() { r = 1.0f, g = 1.0f, b = 1.0f, a = 1.0f };
        deviceContext.CreateSolidColorBrush(&text, null, out _textBrush);
    }

    private void EnsureTargetBitmap(ID3D11Texture2D backBuffer)
    {
        if (_targetBitmap is not null && ReferenceEquals(backBuffer, _cachedBackBuffer))
        {
            return;
        }

        ReleaseComObject(_targetBitmap);
        _targetBitmap = null;

        var surface = (IDXGISurface)backBuffer;
        try
        {
            _targetBitmap = CreateTargetBitmap(surface, D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED);
            _workingAlphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED;
        }
        catch (COMException)
        {
            _targetBitmap = CreateTargetBitmap(surface, D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE);
            _workingAlphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE;
        }
        finally
        {
            ReleaseComObject(surface);
        }

        _cachedBackBuffer = backBuffer;
    }

    private ID2D1Bitmap1 CreateTargetBitmap(IDXGISurface surface, D2D1_ALPHA_MODE alphaMode)
    {
        D2D1_BITMAP_PROPERTIES1_unmanaged props = new()
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = alphaMode,
            },
            bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET
                | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        };

        _deviceContext!.CreateBitmapFromDxgiSurface(surface, &props, out ID2D1Bitmap1 bitmap);
        return bitmap;
    }

    private static nint GetDeviceIdentity(ID3D11Texture2D backBuffer)
    {
        var deviceChild = (ID3D11DeviceChild)backBuffer;
        deviceChild.GetDevice(out ID3D11Device device);
        try
        {
            nint unknown = Marshal.GetIUnknownForObject(device);
            Marshal.Release(unknown);
            return unknown;
        }
        finally
        {
            ReleaseComObject(device);
        }
    }

    private void ReleaseAllResources()
    {
        ReleaseComObject(_targetBitmap);
        _targetBitmap = null;
        _cachedBackBuffer = null;

        ReleaseDeviceResources();
    }

    private void ReleaseDeviceResources()
    {
        ReleaseComObject(_warningFillBrush);
        ReleaseComObject(_warningBorderBrush);
        ReleaseComObject(_failedFillBrush);
        ReleaseComObject(_failedBorderBrush);
        ReleaseComObject(_textBrush);
        ReleaseComObject(_textFormat);
        ReleaseComObject(_writeFactory);
        ReleaseComObject(_deviceContext);
        ReleaseComObject(_d2dDevice);
        ReleaseComObject(_factory);

        _warningFillBrush = null;
        _warningBorderBrush = null;
        _failedFillBrush = null;
        _failedBorderBrush = null;
        _textBrush = null;
        _textFormat = null;
        _writeFactory = null;
        _deviceContext = null;
        _d2dDevice = null;
        _factory = null;
        _deviceIdentity = 0;
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void LogFailure(Exception ex)
    {
        _failureCount++;
        _firstFailureText ??= $"{ex.GetType().Name}: {ex.Message}";

        bool isPowerOfTen = _failureCount == 1;
        long n = _failureCount;
        while (!isPowerOfTen && n >= 10 && n % 10 == 0)
        {
            n /= 10;
            isPowerOfTen = n == 1;
        }

        if (!isPowerOfTen)
        {
            return;
        }

        WriteLogFile($"{DateTime.UtcNow:O} T6 alert overlay failure (first: {_firstFailureText}; alpha: {_workingAlphaMode}) -- total failures: {_failureCount}");
    }

    private static void WriteLogFile(string line)
    {
        try
        {
            string path = s_logPath.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Overlay logging must never affect video playback.
        }
    }
}
