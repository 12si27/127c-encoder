using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using SkiaSharp;

namespace Encoder127c.Platform;

// Uses AppKit already loaded by Avalonia. Called only on macOS, on the UI thread.
internal sealed class MacDockProgress : IDisposable
{
    private IntPtr _dockTile;
    private IntPtr _originalContentView;
    private IntPtr _imageView;
    private SKBitmap? _icon;
    private int? _percentage;
    private bool _unavailable;

    public void Update(double? percentage)
    {
        if (_unavailable) return;
        var value = percentage is { } progress ? (int?)Math.Clamp(progress, 0, 100) : null;
        if (_percentage == value) return;
        try
        {
            if (value is null)
            {
                RestoreIcon();
                return;
            }
            EnsureDockTile();
            var png = Render(_icon!, value.Value);
            var data = SendBytes(Send(Class("NSData"), "alloc"), Selector("initWithBytes:length:"), png, (nuint)png.Length);
            var image = IntPtr.Zero;
            try
            {
                image = Send(Send(Class("NSImage"), "alloc"), "initWithData:", data);
                if (image == IntPtr.Zero) throw new InvalidOperationException("Cannot create Dock progress image.");
                // NSImageView retains the image; NSDockTile retains the content view.
                Send(_imageView, "setImage:", image);
                Send(_dockTile, "setContentView:", _imageView);
                Send(_dockTile, "display");
                _percentage = value;
            }
            finally
            {
                Send(image, "release");
                Send(data, "release");
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or InvalidOperationException or IOException or NotSupportedException)
        {
            Debug.WriteLine($"Dock progress unavailable: {exception.Message}");
            Dispose();
        }
    }

    private void EnsureDockTile()
    {
        if (_imageView != IntPtr.Zero) return;
        using var stream = AssetLoader.Open(new Uri("avares://127c-encoder/Assets/app-icon-mac.png"));
        _icon = SKBitmap.Decode(stream) ?? throw new InvalidOperationException("Cannot load Dock icon.");
        _dockTile = Send(Send(Class("NSApplication"), "sharedApplication"), "dockTile");
        if (_dockTile == IntPtr.Zero) throw new InvalidOperationException("No application Dock tile.");
        var size = SendSize(_dockTile, Selector("size"));
        if (size.Width <= 0 || size.Height <= 0)
            throw new InvalidOperationException("Invalid Dock tile size.");
        _originalContentView = Send(Send(_dockTile, "contentView"), "retain");
        _imageView = SendRect(Send(Class("NSImageView"), "alloc"), Selector("initWithFrame:"),
            new NativeRect { Width = size.Width, Height = size.Height });
        if (_imageView == IntPtr.Zero) throw new InvalidOperationException("Cannot create Dock content view.");
        // NSImageScaleProportionallyUpOrDown: fit the 256px image to the Dock's logical size.
        Send(_imageView, "setImageScaling:", new IntPtr(3));
    }

    // Redraw only when the whole percentage changes. PNG data is copied into native memory.
    internal static byte[] Render(SKBitmap icon, int percentage)
    {
        using var bitmap = new SKBitmap(256, 256);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(icon, new SKRect(0, 0, 256, 256));
        using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 220) };
        canvas.DrawRoundRect(new SKRect(44, 186, 212, 210), 12, 12, paint);
        paint.Color = SKColors.White;
        var width = 160 * Math.Clamp(percentage, 0, 100) / 100f;
        if (width > 0)
            canvas.DrawRoundRect(new SKRect(48, 190, 48 + width, 206), 8, 8, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private void RestoreIcon()
    {
        if (_percentage is null) return;
        Send(_dockTile, "setContentView:", _originalContentView);
        Send(_dockTile, "display");
        _percentage = null;
    }

    public void Dispose()
    {
        RestoreIcon();
        Send(_imageView, "release");
        Send(_originalContentView, "release");
        _imageView = _originalContentView = _dockTile = IntPtr.Zero;
        _icon?.Dispose();
        _icon = null;
        _unavailable = true;
    }

    private static IntPtr Send(IntPtr receiver, string selector) =>
        receiver == IntPtr.Zero ? IntPtr.Zero : SendMessage(receiver, Selector(selector));
    private static IntPtr Send(IntPtr receiver, string selector, IntPtr argument) =>
        SendPointer(receiver, Selector(selector), argument);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr Class([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector, IntPtr argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendBytes(IntPtr receiver, IntPtr selector, byte[] bytes, nuint length);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendRect(IntPtr receiver, IntPtr selector, NativeRect frame);
    // CGSize returns two doubles directly on Intel x64 and Apple Silicon (no objc_msgSend_stret).
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern NativeSize SendSize(IntPtr receiver, IntPtr selector);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public double X, Y, Width, Height; }
}
