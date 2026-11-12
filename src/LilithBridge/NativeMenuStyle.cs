using UnityEngine;
using UnityEngine.UI;
namespace LilithBridge;
internal static class NativeMenuStyle
{
    private static Sprite? _rounded;
    internal static void Paint(Image image, Color color)
    {
        if (_rounded == null)
        {
            const int size = 32; const float radius = 8;
            byte[] pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float dx = Math.Max(0, Math.Max(radius - x - .5f, x + .5f - (size - radius)));
                float dy = Math.Max(0, Math.Max(radius - y - .5f, y + .5f - (size - radius)));
                int p = (y * size + x) * 4;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = 255;
                pixels[p + 3] = (byte)(255 * Math.Clamp(radius + .5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1));
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.LoadRawTextureData(pixels); texture.Apply();
            _rounded = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100, 0,
                SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            _rounded.hideFlags = HideFlags.HideAndDontSave;
        }
        image.sprite = _rounded; image.type = Image.Type.Sliced; image.color = color;
    }
}
