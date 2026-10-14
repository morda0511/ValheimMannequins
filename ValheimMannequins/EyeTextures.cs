using System.Collections.Generic;
using UnityEngine;

namespace ValheimMannequins
{
    /// <summary>
    /// Eye colour. The player body has no eye-colour property: the eyes sample a small blue patch of the skin
    /// texture (identical position in the male and female main texture, found with the texture dump:
    /// 40 pixels at x 118..127, y 20..24 from the top-left). Variants are generated once, in memory, by recolouring
    /// only that patch; everything else in the texture stays untouched.
    /// </summary>
    internal static class EyeTextures
    {
        // search window around the patch, in top-left pixel coordinates of the 256x256 texture
        private const int X0 = 112, X1 = 128, Y0 = 16, Y1 = 28;

        private static readonly Dictionary<int, Texture2D> Cache = new Dictionary<int, Texture2D>();

        public static Texture Get(int eye, int model, Texture source)
        {
            int key = model * 100 + eye;
            if (Cache.TryGetValue(key, out var cached) && cached != null) return cached;
            var tex = Build(source, Mannequin.EyeTones[eye].Color);
            if (tex == null) return source;
            Cache[key] = tex;
            return tex;
        }

        private static Texture2D Build(Texture source, Color target)
        {
            if (source == null) return null;
            int w = source.width, h = source.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prev = RenderTexture.active;
            Color32[] px;
            try
            {
                Graphics.Blit(source, rt); // works for compressed / non-readable textures
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                px = copy.GetPixels32();
                Object.Destroy(copy);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }

            // scale the window if the texture is not 256 wide (keeps the logic valid for modded bodies)
            float sx = w / 256f, sy = h / 256f;
            int x0 = Mathf.FloorToInt(X0 * sx), x1 = Mathf.CeilToInt(X1 * sx), y0 = Mathf.FloorToInt(Y0 * sy), y1 = Mathf.CeilToInt(Y1 * sy);
            Color32 t = target;
            for (int py = y0; py < y1; py++)
            {
                int uy = h - 1 - py; // pixel arrays are bottom-left based
                for (int x = x0; x < x1; x++)
                {
                    var c = px[uy * w + x];
                    // blue-dominant pixels belong to the patch; partially blended edge pixels get a partial weight
                    if (c.b <= c.r + 10) continue;
                    float weight = Mathf.Clamp01((c.b - c.r) / 142f);
                    float shade = c.b / 255f;
                    px[uy * w + x] = new Color32(
                        (byte)Mathf.Lerp(c.r, t.r * shade, weight),
                        (byte)Mathf.Lerp(c.g, t.g * shade, weight),
                        (byte)Mathf.Lerp(c.b, t.b * shade, weight),
                        c.a);
                }
            }

            var result = new Texture2D(w, h, TextureFormat.RGBA32, true);
            result.SetPixels32(px);
            result.wrapMode = source.wrapMode;
            result.filterMode = source.filterMode;
            result.anisoLevel = source.anisoLevel;
            result.Apply(true, true); // keep it GPU-only after upload
            result.name = "PM_Eyes";
            return result;
        }
    }
}
