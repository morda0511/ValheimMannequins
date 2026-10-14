using System.IO;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace ValheimMannequins
{
    /// <summary>
    /// Build-menu icon. If BepInEx/config/ValheimMannequins_icon.png exists it is used as is (edit or replace it freely;
    /// delete it to regenerate). Otherwise the icon is rendered once from the real mannequin model with Jotunn's
    /// RenderManager and saved to that file. Nothing happens on a headless (dedicated) server.
    /// </summary>
    internal static class MannequinIcon
    {
        private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "ValheimMannequins_icon.png");

        public static void Apply(GameObject prefab)
        {
            var piece = prefab.GetComponent<Piece>();
            if (piece == null || GUIManager.IsHeadless()) return;

            if (File.Exists(FilePath))
            {
                var custom = AssetUtils.LoadSpriteFromFile(FilePath);
                if (custom != null) { piece.m_icon = custom; return; }
                Jotunn.Logger.LogWarning("ValheimMannequins: could not load " + FilePath + ", rendering a new icon.");
            }

            // Pre-posed instance without networking (same technique the game uses for placement ghosts);
            // Jotunn clones it, strips everything but the visuals and keeps the evaluated pose.
            GameObject temp = null;
            ZNetView.m_forceDisableInit = true;
            try { temp = Object.Instantiate(prefab, new Vector3(0f, -5000f, 0f), Quaternion.identity); }
            finally { ZNetView.m_forceDisableInit = false; }

            try
            {
                var request = new RenderManager.RenderRequest(temp)
                {
                    Width = 256,
                    Height = 256,
                    Rotation = Quaternion.Euler(0f, 25f, 0f), // three-quarter view; the model faces +Z, the render camera looks from +Z
                    DistanceMultiplier = 1.05f,
                    UseCache = false,
                };
                var sprite = RenderManager.Instance.Render(request);
                if (sprite == null) { Jotunn.Logger.LogWarning("ValheimMannequins: icon render returned nothing, keeping the placeholder."); return; }
                piece.m_icon = sprite;
                File.WriteAllBytes(FilePath, sprite.texture.EncodeToPNG());
                Jotunn.Logger.LogInfo("ValheimMannequins: icon rendered and saved to " + FilePath);
            }
            catch (System.Exception e)
            {
                Jotunn.Logger.LogWarning("ValheimMannequins: icon render failed, keeping the placeholder: " + e.Message);
            }
            finally
            {
                if (temp != null) Object.Destroy(temp);
            }
        }
    }
}
