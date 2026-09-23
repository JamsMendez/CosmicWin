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

    private const double FailureShakeMilliseconds = 230;
    private const double FailureRevealMilliseconds = 700;
    private const double FailureCounterStepMilliseconds = 100;

    private static readonly AlertVisualTheme WarningTheme = new(
        "WARNING",
        "01010111010000010101001001001110010010010100111001000111",
        new D2D1_COLOR_F { r = 1.0f, g = 0.784f, b = 0.078f, a = 0.58f },
        new D2D1_COLOR_F { r = 0.588f, g = 0.376f, b = 0.0f, a = 0.86f },
        new D2D1_COLOR_F { r = 0.345f, g = 0.157f, b = 0.769f, a = 0.95f },
        0,
        PixelateBackdrop: false);

    private static readonly AlertVisualTheme FailedTheme = new(
        "FAILED",
        "0100010101010010010100100100111101010010",
        new D2D1_COLOR_F { r = 0.769f, g = 0.047f, b = 0.118f, a = 0.52f },
        new D2D1_COLOR_F { r = 0.439f, g = 0.0f, b = 0.063f, a = 0.86f },
        new D2D1_COLOR_F { r = 0.0f, g = 0.627f, b = 0.769f, a = 0.95f },
        FailureShakeMilliseconds,
        PixelateBackdrop: true);

    private readonly object _tilesLock = new();
    private readonly TimeProvider _timeProvider;
    private FrameOverlayTile[] _tiles = [];

    private nint _deviceIdentity;
    private ID2D1Factory1? _factory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _deviceContext;
    private IDWriteFactory? _writeFactory;
    private ID2D1SolidColorBrush? _warningFillBrush;
    private ID2D1SolidColorBrush? _warningLetterBrush;
    private ID2D1SolidColorBrush? _warningIntersectionBrush;
    private ID2D1SolidColorBrush? _failedFillBrush;
    private ID2D1SolidColorBrush? _failedLetterBrush;
    private ID2D1SolidColorBrush? _failedIntersectionBrush;
    private ID2D1SolidColorBrush? _whiteBrush;
    private ID2D1SolidColorBrush? _moduleFillBrush;
    private ID2D1SolidColorBrush? _moduleStrokeBrush;
    private ID2D1SolidColorBrush? _moduleTextBrush;
    private IDWriteTextFormat? _titleTextFormat;
    private IDWriteTextFormat? _moduleTextFormat;

    private ID3D11Texture2D? _cachedBackBuffer;
    private ID2D1Bitmap1? _targetBitmap;
    private D2D1_ALPHA_MODE _workingAlphaMode;

    private string? _firstFailureText;
    private long _failureCount;
    private bool _disposed;

    public Direct2DAlertOverlay(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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
        D2D_RECT_F bounds = ClipToDestination(tile.Bounds, destination);
        if (bounds.right <= bounds.left || bounds.bottom <= bounds.top)
        {
            return;
        }

        AlertVisualState state = ComputeVisualState(tile, _timeProvider.GetUtcNow());
        if (state.IsShaking)
        {
            return;
        }

        AlertVisualTheme theme = ThemeFor(tile.Kind);
        ID2D1SolidColorBrush washBrush = tile.Kind == FrameOverlayTileKind.Failed
            ? _failedFillBrush!
            : _warningFillBrush!;
        ID2D1SolidColorBrush letterBrush = tile.Kind == FrameOverlayTileKind.Failed
            ? _failedLetterBrush!
            : _warningLetterBrush!;
        ID2D1SolidColorBrush intersectionBrush = tile.Kind == FrameOverlayTileKind.Failed
            ? _failedIntersectionBrush!
            : _warningIntersectionBrush!;

        float width = bounds.right - bounds.left;
        float height = bounds.bottom - bounds.top;
        float minD = MathF.Min(width, height);
        D2D_RECT_F frame = new()
        {
            left = bounds.left + width * 0.038f,
            top = bounds.top + height * 0.064f,
            right = bounds.right - width * 0.038f,
            bottom = bounds.bottom - height * 0.064f,
        };

        _deviceContext!.FillRectangle(&bounds, washBrush);
        DrawRails(bounds, frame, height, minD);
        DrawTitleFragments(bounds, frame, state.Title, letterBrush, intersectionBrush);
        DrawFrameAccents(bounds, frame, state.RevealProgress, intersectionBrush);
        _deviceContext.DrawRectangle(&frame, _whiteBrush!, MathF.Max(2.0f, minD * 0.004f), null);
        DrawModules(bounds, frame, state.Counter, state.Bits);
    }

    internal static AlertVisualState ComputeVisualStateForTests(FrameOverlayTile tile, DateTimeOffset now) =>
        ComputeVisualState(tile, now);

    private static AlertVisualState ComputeVisualState(FrameOverlayTile tile, DateTimeOffset now)
    {
        AlertVisualTheme theme = ThemeFor(tile.Kind);
        double elapsed = tile.StartedAt == default ? theme.ShakeMilliseconds + FailureRevealMilliseconds : (now - tile.StartedAt).TotalMilliseconds;
        elapsed = Math.Max(0, elapsed);

        double shakeElapsed = Math.Min(elapsed, theme.ShakeMilliseconds);
        bool isShaking = elapsed < theme.ShakeMilliseconds;
        double revealElapsed = isShaking ? 0 : Math.Max(0, elapsed - theme.ShakeMilliseconds);
        int counter = (int)(Math.Floor(revealElapsed / FailureCounterStepMilliseconds) % 100);
        double lifetimeProgress = tile.Duration > TimeSpan.Zero && tile.StartedAt != default
            ? Clamp01((now - tile.StartedAt).TotalMilliseconds / tile.Duration.TotalMilliseconds)
            : 1;

        return new AlertVisualState(
            theme.Title,
            theme.Bits,
            counter,
            isShaking,
            (int)Math.Round(shakeElapsed),
            (int)Math.Round(revealElapsed),
            Clamp01(revealElapsed / FailureRevealMilliseconds),
            lifetimeProgress,
            theme.PixelateBackdrop);
    }

    private static AlertVisualTheme ThemeFor(FrameOverlayTileKind kind) =>
        kind == FrameOverlayTileKind.Failed ? FailedTheme : WarningTheme;

    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

    private void DrawRails(D2D_RECT_F bounds, D2D_RECT_F frame, float height, float minD)
    {
        float railH = MathF.Max(6.0f, height * 0.012f);
        float topY = bounds.top + (frame.top - bounds.top) * 0.72f;
        float bottomY = frame.bottom + (bounds.bottom - frame.bottom) * 0.28f;
        foreach (float y in new[] { topY, bottomY })
        {
            foreach ((float from, float to) in new[] { (0.09f, 0.35f), (0.65f, 0.91f) })
            {
                D2D_RECT_F rail = new()
                {
                    left = bounds.left + (bounds.right - bounds.left) * from,
                    top = y - railH * 0.5f,
                    right = bounds.left + (bounds.right - bounds.left) * to,
                    bottom = y + railH * 0.5f,
                };
                _deviceContext!.FillRectangle(&rail, _whiteBrush!);
                _deviceContext.DrawRectangle(&rail, _moduleFillBrush!, MathF.Max(1.0f, minD * 0.0015f), null);
            }
        }
    }

    private void DrawTitleFragments(
        D2D_RECT_F bounds,
        D2D_RECT_F frame,
        string title,
        ID2D1SolidColorBrush letterBrush,
        ID2D1SolidColorBrush intersectionBrush)
    {
        float height = bounds.bottom - bounds.top;
        D2D_RECT_F upper = new()
        {
            left = frame.left,
            top = frame.top,
            right = frame.right,
            bottom = MathF.Min(frame.bottom, frame.top + height * 0.23f),
        };
        D2D_RECT_F lower = new()
        {
            left = frame.left,
            top = MathF.Max(frame.top, frame.bottom - height * 0.23f),
            right = frame.right,
            bottom = frame.bottom,
        };

        DrawText(title, _titleTextFormat!, &upper, letterBrush);
        DrawText(title, _titleTextFormat!, &lower, letterBrush);

        D2D_RECT_F upperIntersection = new()
        {
            left = upper.left,
            top = upper.top + (upper.bottom - upper.top) * 0.40f,
            right = upper.right,
            bottom = upper.top + (upper.bottom - upper.top) * 0.55f,
        };
        D2D_RECT_F lowerIntersection = new()
        {
            left = lower.left,
            top = lower.top + (lower.bottom - lower.top) * 0.45f,
            right = lower.right,
            bottom = lower.top + (lower.bottom - lower.top) * 0.60f,
        };
        _deviceContext!.FillRectangle(&upperIntersection, intersectionBrush);
        _deviceContext.FillRectangle(&lowerIntersection, intersectionBrush);
    }

    private void DrawFrameAccents(D2D_RECT_F bounds, D2D_RECT_F frame, double revealProgress, ID2D1SolidColorBrush intersectionBrush)
    {
        float width = bounds.right - bounds.left;
        float height = bounds.bottom - bounds.top;
        float bandH = MathF.Max(4.0f, height * 0.012f);
        float travel = (float)((revealProgress * 0.72 + 0.14) % 1.0);
        float x = bounds.left + width * travel;
        D2D_RECT_F verticalBand = new()
        {
            left = x - bandH,
            top = frame.top,
            right = x + bandH,
            bottom = frame.bottom,
        };
        D2D_RECT_F centerBand = new()
        {
            left = frame.left,
            top = bounds.top + height * 0.5f - bandH * 0.5f,
            right = frame.right,
            bottom = bounds.top + height * 0.5f + bandH * 0.5f,
        };
        _deviceContext!.FillRectangle(&verticalBand, intersectionBrush);
        _deviceContext.FillRectangle(&centerBand, intersectionBrush);
    }

    private void DrawModules(D2D_RECT_F bounds, D2D_RECT_F frame, int counter, string bits)
    {
        float width = bounds.right - bounds.left;
        float height = bounds.bottom - bounds.top;
        float minD = MathF.Min(width, height);
        float boxW = MathF.Max(16.0f, width * 0.018f);
        float gap = height * 0.012f;
        float boxH = MathF.Max(42.0f, (frame.bottom - frame.top) * 0.16f);
        float stackTop = bounds.top + height * 0.5f - (boxH * 4.0f + gap * 3.0f) * 0.5f;
        float[] centers =
        [
            bounds.left + (frame.left - bounds.left) * 0.62f,
            frame.right + (bounds.right - frame.right) * 0.38f,
        ];

        for (int column = 0; column < centers.Length; column++)
        {
            for (int box = 0; box < 4; box++)
            {
                D2D_RECT_F module = new()
                {
                    left = centers[column] - boxW * 0.5f,
                    top = stackTop + box * (boxH + gap),
                    right = centers[column] + boxW * 0.5f,
                    bottom = stackTop + box * (boxH + gap) + boxH,
                };
                _deviceContext!.FillRectangle(&module, _moduleFillBrush!);
                _deviceContext.DrawRectangle(&module, _moduleStrokeBrush!, MathF.Max(1.0f, minD * 0.0018f), null);

                int bitOffset = (column * 4 + box) * 8;
                string bitSlice = string.Create(8, (bits, bitOffset), static (chars, state) =>
                {
                    for (int index = 0; index < chars.Length; index++)
                    {
                        chars[index] = state.bits[(state.bitOffset + index) % state.bits.Length];
                    }
                });
                DrawText($"00:{counter:00} {bitSlice}", _moduleTextFormat!, &module, _moduleTextBrush!);
            }
        }
    }

    private void DrawText(string text, IDWriteTextFormat format, D2D_RECT_F* rect, ID2D1SolidColorBrush brush)
    {
        fixed (char* textPtr = text)
        {
            _deviceContext!.DrawText(
                textPtr,
                (uint)text.Length,
                format,
                rect,
                brush,
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
                    "Arial Black",
                    null,
                    DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BLACK,
                    DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                    120.0f,
                    "en-us",
                    out IDWriteTextFormat titleTextFormat);
                _titleTextFormat = titleTextFormat;
                _titleTextFormat.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
                _titleTextFormat.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

                _writeFactory.CreateTextFormat(
                    "Consolas",
                    null,
                    DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD,
                    DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                    12.0f,
                    "en-us",
                    out IDWriteTextFormat moduleTextFormat);
                _moduleTextFormat = moduleTextFormat;
                _moduleTextFormat.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
                _moduleTextFormat.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);

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
        D2D1_COLOR_F warningWash = WarningTheme.Wash;
        deviceContext.CreateSolidColorBrush(&warningWash, null, out _warningFillBrush);

        D2D1_COLOR_F warningLetters = WarningTheme.Letters;
        deviceContext.CreateSolidColorBrush(&warningLetters, null, out _warningLetterBrush);

        D2D1_COLOR_F warningIntersections = WarningTheme.Intersections;
        deviceContext.CreateSolidColorBrush(&warningIntersections, null, out _warningIntersectionBrush);

        D2D1_COLOR_F failedWash = FailedTheme.Wash;
        deviceContext.CreateSolidColorBrush(&failedWash, null, out _failedFillBrush);

        D2D1_COLOR_F failedLetters = FailedTheme.Letters;
        deviceContext.CreateSolidColorBrush(&failedLetters, null, out _failedLetterBrush);

        D2D1_COLOR_F failedIntersections = FailedTheme.Intersections;
        deviceContext.CreateSolidColorBrush(&failedIntersections, null, out _failedIntersectionBrush);

        D2D1_COLOR_F white = new() { r = 1.0f, g = 1.0f, b = 1.0f, a = 0.9f };
        deviceContext.CreateSolidColorBrush(&white, null, out _whiteBrush);

        D2D1_COLOR_F moduleFill = new() { r = 0.031f, g = 0.039f, b = 0.086f, a = 0.88f };
        deviceContext.CreateSolidColorBrush(&moduleFill, null, out _moduleFillBrush);

        D2D1_COLOR_F moduleStroke = new() { r = 0.667f, g = 0.745f, b = 0.922f, a = 0.85f };
        deviceContext.CreateSolidColorBrush(&moduleStroke, null, out _moduleStrokeBrush);

        D2D1_COLOR_F moduleText = new() { r = 0.824f, g = 0.863f, b = 1.0f, a = 1.0f };
        deviceContext.CreateSolidColorBrush(&moduleText, null, out _moduleTextBrush);
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
        ReleaseComObject(_warningLetterBrush);
        ReleaseComObject(_warningIntersectionBrush);
        ReleaseComObject(_failedFillBrush);
        ReleaseComObject(_failedLetterBrush);
        ReleaseComObject(_failedIntersectionBrush);
        ReleaseComObject(_whiteBrush);
        ReleaseComObject(_moduleFillBrush);
        ReleaseComObject(_moduleStrokeBrush);
        ReleaseComObject(_moduleTextBrush);
        ReleaseComObject(_titleTextFormat);
        ReleaseComObject(_moduleTextFormat);
        ReleaseComObject(_writeFactory);
        ReleaseComObject(_deviceContext);
        ReleaseComObject(_d2dDevice);
        ReleaseComObject(_factory);

        _warningFillBrush = null;
        _warningLetterBrush = null;
        _warningIntersectionBrush = null;
        _failedFillBrush = null;
        _failedLetterBrush = null;
        _failedIntersectionBrush = null;
        _whiteBrush = null;
        _moduleFillBrush = null;
        _moduleStrokeBrush = null;
        _moduleTextBrush = null;
        _titleTextFormat = null;
        _moduleTextFormat = null;
        _writeFactory = null;
        _deviceContext = null;
        _d2dDevice = null;
        _factory = null;
        _deviceIdentity = 0;
    }

    internal readonly record struct AlertVisualState(
        string Title,
        string Bits,
        int Counter,
        bool IsShaking,
        int ShakeElapsedMilliseconds,
        int RevealElapsedMilliseconds,
        double RevealProgress,
        double LifetimeProgress,
        bool PixelateBackdrop);

    private readonly record struct AlertVisualTheme(
        string Title,
        string Bits,
        D2D1_COLOR_F Wash,
        D2D1_COLOR_F Letters,
        D2D1_COLOR_F Intersections,
        double ShakeMilliseconds,
        bool PixelateBackdrop);

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
