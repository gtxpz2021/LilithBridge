using System;

namespace LilithBridge;

/// <summary>
/// Row-order helper for the screen-capture path.
///
/// Why this exists: <see cref="VisionCapture"/> reads the screen through a
/// top-down DIB (<c>BitmapInfoHeader.Height = -height</c>), so the first row in
/// memory is the top of the screen. Unity textures store the first row at the
/// bottom, so the buffer has to be flipped once before
/// <c>LoadRawTextureData</c>; otherwise the JPEG that reaches the model is
/// vertically mirrored (top and bottom swapped, left and right unchanged).
/// That mirroring is the defect confirmed on 2026-09-18 by comparing the
/// frames on disk with the images embedded in the prompt dumps.
/// </summary>
public static class VisionRows
{
    /// <summary>
    /// Flips a BGRA32 buffer top-to-bottom in place. A no-op when the buffer is
    /// too small for the declared size, so a bad capture can never crash here.
    /// </summary>
    public static void FlipVerticallyInPlace(byte[] bgra, int width, int height)
    {
        if (bgra == null)
            return;
        long stride = (long)width * 4;
        if (stride <= 0 || height <= 1 || bgra.LongLength < stride * height)
            return;

        int rowBytes = (int)stride;
        var scratch = new byte[rowBytes];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * rowBytes;
            int bottom = (height - 1 - y) * rowBytes;
            Buffer.BlockCopy(bgra, top, scratch, 0, rowBytes);
            Buffer.BlockCopy(bgra, bottom, bgra, top, rowBytes);
            Buffer.BlockCopy(scratch, 0, bgra, bottom, rowBytes);
        }
    }
}
