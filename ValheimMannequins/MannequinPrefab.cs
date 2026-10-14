using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimMannequins
{
    /// <summary>
    /// Builds the mannequin prefab at startup from the vanilla Player prefab's visual subtree:
    /// strips all gameplay/network-sync components and swaps the animator controller for the armor stand's
    /// single-frame pose controller. Verified against the Stage 0 probe reports (Valheim 1.0.17).
    /// </summary>
    internal static class MannequinPrefab
    {
        public const string PrefabName = "PM_Mannequin";

        public static GameObject Build()
        {
            var stand = PrefabManager.Instance.GetPrefab("ArmorStand_Male");
            var standAnimator = stand != null ? stand.GetComponentInChildren<Animator>(true) : null;
            if (standAnimator == null || standAnimator.runtimeAnimatorController == null)
            {
                Jotunn.Logger.LogError("ValheimMannequins: ArmorStand_Male animator not found, cannot build the mannequin.");
                return null;
            }

            var go = PrefabManager.Instance.CreateClonedPrefab(PrefabName, "Player");
            if (go == null)
            {
                Jotunn.Logger.LogError("ValheimMannequins: failed to clone the Player prefab.");
                return null;
            }

            // 1) Break the references that would dangle once Player is gone.
            var vis = go.GetComponent<VisEquipment>();
            vis.m_playerComponent = null;
            vis.m_nViewOverride = null;
            vis.m_isPlayer = true;
            vis.m_isArmorStand = false;

            // 2) Remove character logic, sync and physics. Several passes because of RequireComponent chains.
            var visual = go.transform.Find("Visual");
            Remove<CharacterAnimEvent>(visual.gameObject);
            Remove<PlayerController>(go);
            Remove<FootStep>(go);
            Remove<Talker>(go);
            Remove<Skills>(go);
            Remove<ZSyncAnimation>(go);
            Remove<ZSyncTransform>(go);
            Remove<Player>(go);
            Remove<Rigidbody>(go);
            Remove<CapsuleCollider>(go);

            // 3) Remove effects that only make sense on a live player.
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true).ToList())
                UnityEngine.Object.DestroyImmediate(ps.gameObject);
            var dev = visual.Find("DevEffects");
            if (dev != null) UnityEngine.Object.DestroyImmediate(dev.gameObject);

            // 4) Pose controller from the armor stand (same humanoid avatar, one int parameter "Pose").
            var anim = visual.GetComponent<Animator>();
            anim.runtimeAnimatorController = standAnimator.runtimeAnimatorController;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; // Mannequin freezes + disables it after posing

            // 5) Networking: persistent world object.
            var nview = go.GetComponent<ZNetView>();
            nview.m_persistent = true;
            nview.m_distant = false;

            // 6) Collision / hover target on the build-piece layer.
            var hitbox = new GameObject("Hitbox");
            hitbox.transform.SetParent(go.transform, false);
            hitbox.layer = LayerMask.NameToLayer("piece");
            var cap = hitbox.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0f, 0.9f, 0f);
            cap.height = 1.8f;
            cap.radius = 0.35f;

            // 7) Build piece + destructible, configured like the vanilla armor stand.
            var piece = go.AddComponent<Piece>();
            CopyPublicFields(stand.GetComponent<Piece>(), piece, stand.transform);
            piece.m_name = "Mannequin";
            piece.m_description = "A player-shaped display figure.";

            var wear = go.AddComponent<WearNTear>();
            CopyPublicFields(stand.GetComponent<WearNTear>(), wear, stand.transform);
            wear.m_supports = false;
            wear.m_autoCreateFragments = false;
            wear.m_new = wear.m_worn = wear.m_broken = wear.m_wet = null;

            go.AddComponent<Mannequin>();
            go.layer = LayerMask.NameToLayer("piece");
            return go;
        }

        private static void Remove<T>(GameObject root) where T : Component
        {
            for (int pass = 0; pass < 3; pass++)
            {
                var found = root.GetComponentsInChildren<T>(true);
                if (found.Length == 0) return;
                foreach (var c in found)
                {
                    try { UnityEngine.Object.DestroyImmediate(c); }
                    catch (Exception e) { Jotunn.Logger.LogDebug($"ValheimMannequins: could not remove {typeof(T).Name} yet: {e.Message}"); }
                }
            }
            if (root.GetComponentsInChildren<T>(true).Length > 0)
                Jotunn.Logger.LogWarning($"ValheimMannequins: {typeof(T).Name} could not be removed from the mannequin prefab.");
        }

        /// <summary>Copies public instance fields; drops references into the source prefab's own hierarchy and delegates.</summary>
        private static void CopyPublicFields(Component src, Component dst, Transform srcRoot)
        {
            foreach (var f in src.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                var v = f.GetValue(src);
                if (v is Component c && c != null && c.transform.root == srcRoot) v = null;
                else if (v is GameObject g && g != null && g.transform.root == srcRoot) v = null;
                else if (v is IList list && f.FieldType.IsArray && v is UnityEngine.Object[])
                    v = Array.CreateInstance(f.FieldType.GetElementType(), 0);
                else if (v is IList l2 && !f.FieldType.IsArray && f.FieldType.IsGenericType &&
                         typeof(Component).IsAssignableFrom(f.FieldType.GetGenericArguments()[0]))
                    v = Activator.CreateInstance(f.FieldType);
                f.SetValue(dst, v);
            }
        }
    }
}
