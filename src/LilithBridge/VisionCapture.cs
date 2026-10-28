using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LilithBridge;

internal static class VisionCapture
{
    // Called on the Unity main thread after the input-hide frame boundary.
    public static VisionImage Capture(BridgeConfig config)
    {
        if (!config.VisionEnabled) return Failed("vision_disabled");
        var foreground = AppAwareness.GetCurrent();
        if (foreground != null && PrivacySafety.IsSensitiveWindow(
            foreground.ProcessName, foreground.WindowTitle, foreground.WindowClass))
            return Failed("sensitive_foreground");
        IntPtr screen = IntPtr.Zero, bitmap = IntPtr.Zero, memory = IntPtr.Zero, old = IntPtr.Zero;
        Texture2D? texture = null, scaled = null;
        try
        {
            int x = GetSystemMetrics(76), y = GetSystemMetrics(77);
            int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                return Failed("invalid_desktop_size");
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return Failed("screen_dc_failed");
            bitmap = CreateCompatibleBitmap(screen, width, height);
            memory = CreateCompatibleDC(screen);
            if (bitmap == IntPtr.Zero || memory == IntPtr.Zero) return Failed("capture_allocation_failed");
            old = SelectObject(memory, bitmap);
            if (old == IntPtr.Zero || old == new IntPtr(-1)) return Failed("capture_selection_failed");
            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020))
                return Failed("desktop_copy_failed");
            SelectObject(memory, old); old = IntPtr.Zero;
            byte[] pixels = ReadPixels(bitmap, width, height);
            if (pixels.Length != width * height * 4) return Failed("pixel_read_failed");
            texture = new Texture2D(width, height, TextureFormat.BGRA32, false)
            { hideFlags = HideFlags.DontSave };
            texture.LoadRawTextureData(pixels);
            texture.Apply(false);
            int targetWidth = Math.Min(Math.Clamp(config.VisionMaxWidth, 320, 4096), width);
            int targetHeight = Math.Max(1, (int)Math.Round(height * (double)targetWidth / width));
            if (targetWidth < width)
            {
                RenderTexture? temporary = null;
                RenderTexture? previous = RenderTexture.active;
                try
                {
                    temporary = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(texture, temporary);
                    RenderTexture.active = temporary;
                    scaled = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false)
                    { hideFlags = HideFlags.DontSave };
                    scaled.ReadPixels(new Rect(0f, 0f, targetWidth, targetHeight), 0, 0, false);
                    scaled.Apply(false);
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
                }
            }
            byte[] jpeg = ImageConversion.EncodeToJPG(scaled ?? texture, Math.Clamp(config.VisionJpegQuality, 20, 95));
            if (jpeg.Length == 0) return Failed("jpeg_encode_failed");
            if (config.SaveFrames)
            {
                try
                {
                    string dir = Path.Combine(DataPaths.Root(), "telemetry", "frames");
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"desktop_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg"), jpeg);
                }
                catch { /* Optional local copy; successful capture remains usable. */ }
            }
            return new VisionImage
            {
                Kind = "desktop", MimeType = "image/jpeg",
                DataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg), Detail = config.VisionDetail,
                CaptureOutcome = "captured", CapturedAt = capturedAt,
                X = x, Y = y, SourceWidth = width, SourceHeight = height,
                Width = targetWidth, Height = targetHeight,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(jpeg))
            };
        }
        catch { return Failed("capture_failed"); }
        finally
        {
            if (scaled != null) UnityEngine.Object.Destroy(scaled);
            if (texture != null) UnityEngine.Object.Destroy(texture);
            if (old != IntPtr.Zero && old != new IntPtr(-1) && memory != IntPtr.Zero) SelectObject(memory, old);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static VisionImage Failed(string outcome) => new() { CaptureOutcome = outcome };

    private static byte[] ReadPixels(IntPtr bitmap, int width, int height)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                SizeImage = width * height * 4
            }
        };
        IntPtr bits = Marshal.AllocHGlobal(width * height * 4);
        try
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return Array.Empty<byte>();
            int lines;
            try { lines = GetDIBits(screen, bitmap, 0, height, bits, ref info, 0); }
            finally { ReleaseDC(IntPtr.Zero, screen); }
            if (lines != height) return Array.Empty<byte>();
            var result = new byte[width * height * 4];
            Marshal.Copy(bits, result, 0, result.Length);
            // The DIB above is top-down (Height = -height): its first row is the
            // top of the screen. Unity textures expect the first row at the
            // bottom, so flip once here — without this the screenshot reaches
            // the model vertically mirrored (top/bottom swapped).
            VisionRows.FlipVerticallyInPlace(result, width, height);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(bits);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, int flags);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, int start, int lines, IntPtr bits, ref BitmapInfo info, int usage);
}
