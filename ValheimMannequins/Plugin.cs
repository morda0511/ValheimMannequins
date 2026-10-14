using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace ValheimMannequins
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "morda.ValheimMannequins";
        public const string ModName = "Valheim Mannequins";
        public const string ModVersion = "0.3.0";

        internal static ConfigEntry<string> Recipe;
        internal static ConfigEntry<int> DefaultPose;
        internal static ConfigEntry<int> SetCount;
        internal static ConfigEntry<KeyCode> SwapModifier;
        internal static ConfigEntry<float> PanelScale;
        internal static ConfigEntry<float> NameDistance;
        internal static ConfigEntry<bool> ScrollSwitchesSet;
        private float _nextScroll;

        private void Awake()
        {
            Recipe = Config.Bind("Recipe", "Materials", "Wood:20,Iron:1",
                new ConfigDescription("Build cost as Item:Amount pairs separated by commas (prefab names, e.g. Wood:20,Iron:1). Read at game start.",
                    null, new ConfigurationManagerAttributes { IsAdminOnly = true }));
            DefaultPose = Config.Bind("Mannequin", "DefaultPose", 0,
                new ConfigDescription("Pose index (0-14) a newly placed mannequin starts with.", new AcceptableValueRange<int>(0, Mannequin.PoseCount - 1)));
            SetCount = Config.Bind("Gear sets", "SetCount", 5,
                new ConfigDescription("Number of gear sets per mannequin (1-8). Lowering it never deletes stored items; hidden sets are returned when the mannequin is removed.",
                    new AcceptableValueRange<int>(1, Mannequin.MaxSets)));
            SwapModifier = Config.Bind("Gear sets", "SwapModifier", KeyCode.LeftAlt,
                "Hold this key + a number key while aiming at a mannequin to swap your worn gear with that gear set. (Left/right Alt, Ctrl and Shift are treated as the same key.) Change it if another mod already uses Alt+number.");
            PanelScale = Config.Bind("UI", "PanelScale", 1.6f,
                new ConfigDescription("Size of the mannequin window.", new AcceptableValueRange<float>(0.8f, 3f)));

            NameDistance = Config.Bind("UI", "NameDistance", 30f,
                new ConfigDescription("Distance in meters up to which a mannequin's name is shown.", new AcceptableValueRange<float>(5f, 100f)));

            ScrollSwitchesSet = Config.Bind("Gear sets", "ScrollSwitchesSet", true,
                "Scrolling the mouse wheel while aiming at a mannequin switches its displayed gear set (the camera does not zoom while doing so).");

            new Harmony(ModGuid).PatchAll();
            PrefabManager.OnVanillaPrefabsAvailable += RegisterMannequin;
        }

        private void Update()
        {
            float wheel = Input.mouseScrollDelta.y; // single property read per frame; everything else only while scrolling
            if (wheel == 0f || !ScrollSwitchesSet.Value || Time.unscaledTime < _nextScroll) return;
            var mannequin = MannequinScroll.Hovered();
            if (mannequin == null) return;
            _nextScroll = Time.unscaledTime + 0.15f;
            mannequin.StepSet(wheel > 0f ? 1 : -1);
        }

        internal static bool ModifierHeld()
        {
            switch (SwapModifier.Value)
            {
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                case KeyCode.LeftControl: case KeyCode.RightControl: return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                case KeyCode.LeftShift: case KeyCode.RightShift: return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                case KeyCode.None: return false;
                default: return Input.GetKey(SwapModifier.Value);
            }
        }

        internal static string ModifierLabel()
        {
            switch (SwapModifier.Value)
            {
                case KeyCode.LeftAlt: case KeyCode.RightAlt: return "Alt";
                case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
                default: return SwapModifier.Value.ToString();
            }
        }

        private static void RegisterMannequin()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= RegisterMannequin;
            var prefab = MannequinPrefab.Build();
            if (prefab == null) return;

            var stand = PrefabManager.Instance.GetPrefab("ArmorStand_Male");
            var reqs = new List<RequirementConfig>();
            foreach (var part in Recipe.Value.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && int.TryParse(kv[1].Trim(), out int n) && n > 0 && !string.IsNullOrWhiteSpace(kv[0]))
                    reqs.Add(new RequirementConfig(kv[0].Trim(), n, 0, true));
                else
                    Jotunn.Logger.LogWarning($"{ModName}: ignoring malformed recipe entry '{part}'");
            }

            var cfg = new PieceConfig
            {
                Name = "Mannequin",
                Description = "A player-shaped display figure.",
                PieceTable = "Hammer",
                Category = "Furniture",
                Requirements = reqs.ToArray(),
                // Placeholder until a rendered icon exists: reuse the vanilla armor stand icon.
                Icon = stand != null ? stand.GetComponent<Piece>().m_icon : null,
            };
            PieceManager.Instance.AddPiece(new CustomPiece(prefab, false, cfg));
            MannequinIcon.Apply(prefab);
            Jotunn.Logger.LogInfo($"{ModName}: mannequin piece registered.");
        }
    }

    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdminOnly;
    }
}
