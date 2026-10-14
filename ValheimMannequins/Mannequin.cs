using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimMannequins
{
    /// <summary>
    /// Runtime behaviour of a placed mannequin.
    ///
    /// Persistence/sync: everything lives in the object's ZDO. A mannequin holds up to MaxSets gear sets of real
    /// items (stored per set+slot the way the vanilla armor stand stores items: prefab hash + ItemDrop.SaveToZDO
    /// blob). The ACTIVE set is mirrored into the ZDO keys VisEquipment reads, so every client renders it from
    /// replicated ZDO data alone.
    ///
    /// Item safety: equip/take/swap run only on the peer that has just claimed ZDO ownership, in one synchronous
    /// block together with the matching inventory change. Storage is only written after the inventory side
    /// succeeded, and a failed inventory change is rolled back before anything is stored.
    /// </summary>
    public class Mannequin : MonoBehaviour, Hoverable, Interactable, TextReceiver
    {
        public const int MaxNameLength = 40;
        private static readonly int[] NameKeys = new int[MaxSets];
        private const string RpcSetName = "PM_SetName";
        private const string RpcLabel = "PM_Label";
        private string _shown = "";
        private Nameplate _plate;

        public const int PoseCount = 15; // ArmorStand_Animator clip count (verified in probe round 2)
        public const int MaxSets = 8;    // limited by the 8 hotbar keys used for swapping
        private const int SwapSlots = 7; // slots 0..6 exist on a player (armor, utility, hands); back slots are display-only

        public sealed class SlotDef
        {
            public string Label;
            public ItemDrop.ItemData.ItemType[] Types;
            public int VisualKey;   // VisEquipment ZDO key (item hash)
            public int VariantKey;  // 0 = none
            public int QualityKey;  // 0 = none
        }

        private static ItemDrop.ItemData.ItemType[] T(params ItemDrop.ItemData.ItemType[] t) => t;
        private const ItemDrop.ItemData.ItemType OneH = ItemDrop.ItemData.ItemType.OneHandedWeapon;
        private const ItemDrop.ItemData.ItemType TwoH = ItemDrop.ItemData.ItemType.TwoHandedWeapon;
        private const ItemDrop.ItemData.ItemType Bow = ItemDrop.ItemData.ItemType.Bow;
        private const ItemDrop.ItemData.ItemType Tool = ItemDrop.ItemData.ItemType.Tool;
        private const ItemDrop.ItemData.ItemType Torch = ItemDrop.ItemData.ItemType.Torch;
        private const ItemDrop.ItemData.ItemType Shield = ItemDrop.ItemData.ItemType.Shield;

        // Slot/type rules mirror the vanilla armor stand (verified from its prefab in probe round 2).
        public static readonly SlotDef[] Slots =
        {
            Def("Helmet",          T(ItemDrop.ItemData.ItemType.Helmet),   ZDOVars.s_helmetItem),
            Def("Chest",           T(ItemDrop.ItemData.ItemType.Chest),    ZDOVars.s_chestItem),
            Def("Legs",            T(ItemDrop.ItemData.ItemType.Legs),     ZDOVars.s_legItem),
            Def("Cape",            T(ItemDrop.ItemData.ItemType.Shoulder), ZDOVars.s_shoulderItem, ZDOVars.s_shoulderItemVariant, ZDOVars.s_shoulderItemQuality),
            Def("Utility",         T(ItemDrop.ItemData.ItemType.Utility),  ZDOVars.s_utilityItem),
            Def("Right hand",      T(OneH, Bow, Tool, TwoH, Torch),        ZDOVars.s_rightItem, 0, ZDOVars.s_rightItemQuality),
            Def("Left hand",       T(OneH, Bow, Shield, TwoH, Torch, Tool), ZDOVars.s_leftItem, ZDOVars.s_leftItemVariant, ZDOVars.s_leftItemQuality),
            Def("Back (weapon)",   T(OneH, Bow, Tool, TwoH, Torch),        ZDOVars.s_leftBackItem, ZDOVars.s_leftBackItemVariant, ZDOVars.s_leftBackItemQuality),
            Def("Back (shield)",   T(Shield),                              ZDOVars.s_rightBackItem, 0, ZDOVars.s_rightBackItemQuality),
        };

        private static SlotDef Def(string label, ItemDrop.ItemData.ItemType[] types, int visual, int variant = 0, int quality = 0)
        {
            return new SlotDef { Label = label, Types = types, VisualKey = visual, VariantKey = variant, QualityKey = quality };
        }

        private const int SlotStride = 16; // blob index = set * SlotStride + slot (set 0 == the pre-gearset layout)
        private static readonly int PoseKey = "PM_pose".GetStableHashCode();
        private static readonly int ActiveSetKey = "PM_set".GetStableHashCode();
        private static readonly int[,] ItemKeys = new int[MaxSets, Slots.Length];
        private static readonly int[,] BlobKeys = new int[MaxSets, Slots.Length];
        private const string RpcSetPose = "PM_SetPose";

        private static readonly System.Reflection.FieldInfo RightItemField = AccessTools.Field(typeof(Humanoid), "m_rightItem");
        private static readonly System.Reflection.FieldInfo LeftItemField = AccessTools.Field(typeof(Humanoid), "m_leftItem");
        private static readonly System.Reflection.FieldInfo HiddenRightItemField = AccessTools.Field(typeof(Humanoid), "m_hiddenRightItem");
        private static readonly System.Reflection.FieldInfo HiddenLeftItemField = AccessTools.Field(typeof(Humanoid), "m_hiddenLeftItem");

        static Mannequin()
        {
            for (int s = 0; s < MaxSets; s++)
                NameKeys[s] = (s == 0 ? "PM_name" : "PM_name" + s).GetStableHashCode();
            for (int s = 0; s < MaxSets; s++)
                for (int i = 0; i < Slots.Length; i++)
                {
                    ItemKeys[s, i] = (s == 0 ? "PM_item" + i : "PM_item" + s + "_" + i).GetStableHashCode();
                    BlobKeys[s, i] = ((s * SlotStride + i) + "_itemData").GetStableHashCode(); // key used by ItemDrop.Save/LoadFromZDO(index)
                }
        }

        private ZNetView _nview;
        private Animator _anim;
        private VisEquipment _vis;
        private int _appliedModel = -1, _appliedEye = -1;
        private int _pose = -1;

        public int Pose => _pose;
        public ZDO Zdo => _nview != null ? _nview.GetZDO() : null;

        // ---- lifecycle -------------------------------------------------------------------------

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _anim = GetComponentInChildren<Animator>(true);
            _vis = GetComponent<VisEquipment>();

            var zdo = Zdo;
            if (zdo != null)
            {
                if (_vis != null) ByVis[_vis] = this;
                _nview.Register<int>(RpcSetPose, RPC_SetPose);
                _nview.Register<int, string>(RpcSetName, RPC_SetName);
                _nview.Register<string>(RpcLabel, RPC_Label);
                ShowLabel(Label);
                var wear = GetComponent<WearNTear>();
                if (wear != null) wear.m_onDestroyed = (Action)Delegate.Combine(wear.m_onDestroyed, new Action(OnDestroyed));
                ApplyPose(zdo.GetInt(PoseKey, Plugin.DefaultPose.Value));
            }
            else
            {
                ApplyPose(Plugin.DefaultPose.Value); // placement ghost
            }
        }

        private void OnDestroyed()
        {
            // Called by WearNTear on the ZDO owner only: return every stored item of every set as a world drop.
            if (!_nview.IsOwner()) return;
            for (int set = 0; set < MaxSets; set++)
                for (int i = 0; i < Slots.Length; i++)
                {
                    if (Zdo.GetInt(ItemKeys[set, i]) == 0) continue;
                    var item = LoadItem(set, i);
                    if (item == null)
                    {
                        Jotunn.Logger.LogWarning($"ValheimMannequins: could not return item in set {set + 1} slot '{Slots[i].Label}' (prefab no longer exists).");
                        continue;
                    }
                    var pos = transform.position + Vector3.up * 0.8f + UnityEngine.Random.insideUnitSphere * 0.3f;
                    ItemDrop.DropItem(item, 0, pos, Quaternion.identity);
                    ClearSlot(set, i);
                }
        }

        // ---- pose ------------------------------------------------------------------------------

        private void ApplyPose(int pose)
        {
            pose = Mathf.Clamp(pose, 0, PoseCount - 1);
            if (pose == _pose || _anim == null) return;
            _pose = pose;
            _anim.enabled = true;
            _anim.speed = 1f;
            _anim.SetInteger("Pose", pose);
            for (int i = 0; i < 30; i++) _anim.Update(0.05f); // run through the pose transition synchronously
            _anim.enabled = false;                          // bones keep the last evaluated pose
        }

        private void RPC_SetPose(long sender, int pose)
        {
            ApplyPose(pose);
            if (_nview.IsOwner()) _nview.GetZDO().Set(PoseKey, _pose);
        }

        public void RequestPoseStep(int delta)
        {
            if (!HasAccess(true)) return;
            _nview.InvokeRPC(ZNetView.Everybody, RpcSetPose, ((_pose + delta) % PoseCount + PoseCount) % PoseCount);
        }

        // ---- name ------------------------------------------------------------------------------

        private static string CleanName(string text)
        {
            text = (text ?? "").Replace("<", "").Replace(">", "").Trim(); // plain text only: no rich-text markup in hover or label
            return text.Length > MaxNameLength ? text.Substring(0, MaxNameLength) : text;
        }

        // Every gear set has its own name; the nameplate shows the name of the ACTIVE set.
        // Set 0 keeps the pre-gearset key "PM_name" so older mannequins keep their name.
        public string GetSetName(int set) => Zdo == null ? "" : Zdo.GetString(NameKeys[set], "");
        public string Label => GetSetName(ActiveSet);

        private void ShowLabel(string name)
        {
            name = CleanName(name);
            _shown = name;
            if (_plate == null)
            {
                if (name.Length == 0) return; // nothing to show, nothing to create
                _plate = Nameplate.Create(transform);
            }
            _plate.SetText(name);
        }

        // Owner -> everybody: the nameplate text changed (set switched or active set renamed).
        // The text travels in the RPC so clients do not depend on ZDO replication timing.
        private void BroadcastLabel()
        {
            _nview.InvokeRPC(ZNetView.Everybody, RpcLabel, Label);
        }

        private void RPC_Label(long sender, string name) => ShowLabel(name);

        private void RPC_SetName(long sender, int set, string name)
        {
            if (!_nview.IsOwner() || set < 0 || set >= MaxSets) return; // only the owner stores; length/markup are sanitized here
            name = CleanName(name);
            _nview.GetZDO().Set(NameKeys[set], name);
            if (set == ActiveSet) BroadcastLabel();
        }

        private int _renameSet;

        /// <summary>Opens the vanilla text dialog to rename the active set.</summary>
        public void RequestRename()
        {
            if (!HasAccess(true) || TextInput.instance == null) return;
            _renameSet = ActiveSet;
            TextInput.instance.RequestText(this, $"Name of set {_renameSet + 1} (empty = none)", MaxNameLength);
        }

        // TextReceiver (used by the vanilla text input dialog)
        public string GetText() => GetSetName(_renameSet);

        public void SetText(string text)
        {
            if (!HasAccess(true)) return;
            _nview.InvokeRPC(ZNetView.Everybody, RpcSetName, _renameSet, CleanName(text));
        }


        // ---- appearance (hair, beard, colours) -------------------------------------------------
        // Mannequin-wide, not per set. Written into the ZDO keys VisEquipment already reads for players
        // (HairItem, BeardItem, SkinColor, HairColor); owner-side writes, replicated by the ZDO.

        public struct Tone { public string Name; public Color Color; public Tone(string n, Color c) { Name = n; Color = c; } }

        // Presets (index 0 = game default). Tune freely; they are multiplied onto the textures by the player shader.
        public static readonly Tone[] SkinTones =
        {
            new Tone("Default", Color.white),
            new Tone("Fair",    new Color(1.00f, 0.93f, 0.86f)),
            new Tone("Light",   new Color(0.90f, 0.78f, 0.68f)),
            new Tone("Medium",  new Color(0.76f, 0.60f, 0.48f)),
            new Tone("Tan",     new Color(0.62f, 0.46f, 0.34f)),
            new Tone("Dark",    new Color(0.45f, 0.31f, 0.22f)),
            new Tone("Ashen",   new Color(0.78f, 0.84f, 0.90f)),
        };

        public static readonly Tone[] HairTones =
        {
            new Tone("Default",    Color.white),
            new Tone("Black",      new Color(0.12f, 0.10f, 0.09f)),
            new Tone("Dark brown", new Color(0.30f, 0.18f, 0.10f)),
            new Tone("Brown",      new Color(0.45f, 0.28f, 0.15f)),
            new Tone("Auburn",     new Color(0.55f, 0.25f, 0.12f)),
            new Tone("Red",        new Color(0.70f, 0.25f, 0.10f)),
            new Tone("Blonde",     new Color(0.85f, 0.70f, 0.35f)),
            new Tone("Platinum",   new Color(0.95f, 0.90f, 0.80f)),
            new Tone("Grey",       new Color(0.60f, 0.60f, 0.60f)),
        };

        private static readonly int SkinIdxKey = "PM_skinTone".GetStableHashCode();
        private static readonly int HairIdxKey = "PM_hairTone".GetStableHashCode();
        // Eye colour presets (index 0 = unchanged game texture). The colour is painted onto the eye patch of the
        // skin texture, see EyeTextures.
        public static readonly Tone[] EyeTones =
        {
            new Tone("Default",   new Color(0.44f, 0.73f, 1.00f)),
            new Tone("Deep blue", new Color(0.12f, 0.32f, 0.85f)),
            new Tone("Green",     new Color(0.30f, 0.78f, 0.35f)),
            new Tone("Hazel",     new Color(0.60f, 0.48f, 0.18f)),
            new Tone("Brown",     new Color(0.42f, 0.25f, 0.12f)),
            new Tone("Grey",      new Color(0.62f, 0.66f, 0.70f)),
            new Tone("Amber",     new Color(0.95f, 0.65f, 0.10f)),
            new Tone("Red",       new Color(0.90f, 0.12f, 0.12f)),
            new Tone("Violet",    new Color(0.60f, 0.28f, 0.88f)),
        };

        private static readonly int EyeKey = "PM_eyes".GetStableHashCode();
        public int EyeIndex => Zdo == null ? 0 : Mathf.Clamp(Zdo.GetInt(EyeKey, 0), 0, EyeTones.Length - 1);
        public void StepEyes(int delta)
        {
            if (!HasAccess(true) || !TakeOwnership()) return;
            Zdo.Set(EyeKey, ((EyeIndex + delta) % EyeTones.Length + EyeTones.Length) % EyeTones.Length);
        }

        // Registry so the VisEquipment hook below can find the owning mannequin without any GetComponent per frame.
        internal static readonly Dictionary<VisEquipment, Mannequin> ByVis = new Dictionary<VisEquipment, Mannequin>();

        private void OnDestroy()
        {
            if (_vis != null) ByVis.Remove(_vis);
        }

        /// <summary>
        /// Called right after VisEquipment evaluated the body model (it resets the skin texture whenever the model
        /// changes). Re-applies the eye texture only when the model or the chosen eye colour actually changed.
        /// </summary>
        internal void OnBodyUpdated()
        {
            var zdo = Zdo;
            if (zdo == null || _vis == null || _vis.m_bodyModel == null) return;
            int model = Mathf.Clamp(zdo.GetInt(ZDOVars.s_modelIndex), 0, _vis.m_models.Length - 1);
            int eye = EyeIndex;
            if (model == _appliedModel && eye == _appliedEye) return;
            _appliedModel = model;
            _appliedEye = eye;
            var baseTex = _vis.m_models[model].m_baseMaterial.GetTexture("_MainTex");
            var tex = eye == 0 ? baseTex : EyeTextures.Get(eye, model, baseTex);
            _vis.m_bodyModel.materials[0].SetTexture("_MainTex", tex);
        }

        private static List<string> _hairs, _beards;

        private static List<string> StyleNames(string prefix)
        {
            // same source as the vanilla character creation; "...None" entries are replaced by our own "None" option
            var names = new List<string>();
            if (ObjectDB.instance == null) return names;
            foreach (var drop in ObjectDB.instance.GetAllItems(ItemDrop.ItemData.ItemType.Customization, prefix))
                if (!drop.gameObject.name.EndsWith("None")) names.Add(drop.gameObject.name);
            names.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b));
            return names;
        }

        private static List<string> Hairs => _hairs != null && _hairs.Count > 0 ? _hairs : (_hairs = StyleNames("Hair"));
        private static List<string> Beards => _beards != null && _beards.Count > 0 ? _beards : (_beards = StyleNames("Beard"));

        private static string Pretty(string prefabName, string prefix) =>
            prefabName.StartsWith(prefix) ? "Style " + prefabName.Substring(prefix.Length) : prefabName;

        private int StyleIndex(int zdoKey, List<string> list)
        {
            int hash = Zdo != null ? Zdo.GetInt(zdoKey) : 0;
            if (hash == 0) return 0;
            int i = list.FindIndex(n => n.GetStableHashCode() == hash);
            return i < 0 ? 0 : i + 1; // 0 = none
        }

        public string HairName => StyleIndex(ZDOVars.s_hairItem, Hairs) == 0 ? "None" : Pretty(Hairs[StyleIndex(ZDOVars.s_hairItem, Hairs) - 1], "Hair");
        public string BeardName => StyleIndex(ZDOVars.s_beardItem, Beards) == 0 ? "None" : Pretty(Beards[StyleIndex(ZDOVars.s_beardItem, Beards) - 1], "Beard");
        public int SkinIndex => Zdo == null ? 0 : Mathf.Clamp(Zdo.GetInt(SkinIdxKey, 0), 0, SkinTones.Length - 1);
        public int HairToneIndex => Zdo == null ? 0 : Mathf.Clamp(Zdo.GetInt(HairIdxKey, 0), 0, HairTones.Length - 1);

        private void StepStyle(int zdoKey, List<string> list, int delta)
        {
            if (!HasAccess(true) || !TakeOwnership()) return;
            int count = list.Count + 1;
            int next = ((StyleIndex(zdoKey, list) + delta) % count + count) % count;
            Zdo.Set(zdoKey, next == 0 ? 0 : list[next - 1].GetStableHashCode());
        }

        public void StepHair(int delta) => StepStyle(ZDOVars.s_hairItem, Hairs, delta);
        public void StepBeard(int delta) => StepStyle(ZDOVars.s_beardItem, Beards, delta);

        private void StepTone(int idxKey, int colorKey, Tone[] tones, int current, int delta)
        {
            if (!HasAccess(true) || !TakeOwnership()) return;
            int next = ((current + delta) % tones.Length + tones.Length) % tones.Length;
            var c = tones[next].Color;
            Zdo.Set(idxKey, next);
            Zdo.Set(colorKey, new Vector3(c.r, c.g, c.b));
        }

        public void StepSkin(int delta) => StepTone(SkinIdxKey, ZDOVars.s_skinColor, SkinTones, SkinIndex, delta);
        public void StepHairTone(int delta) => StepTone(HairIdxKey, ZDOVars.s_hairColor, HairTones, HairToneIndex, delta);

        // ---- body ------------------------------------------------------------------------------

        public bool IsFemale => Zdo != null && Zdo.GetInt(ZDOVars.s_modelIndex) == 1;

        public void ToggleBody()
        {
            if (!HasAccess(true) || !TakeOwnership()) return;
            Zdo.Set(ZDOVars.s_modelIndex, IsFemale ? 0 : 1);
        }

        // ---- access / ownership ----------------------------------------------------------------

        public bool HasAccess(bool flash)
        {
            return _nview != null && _nview.IsValid() && PrivateArea.CheckAccess(transform.position, 0f, flash);
        }

        private bool TakeOwnership()
        {
            if (!_nview.IsValid()) return false;
            if (!_nview.IsOwner()) _nview.ClaimOwnership();
            return _nview.IsOwner();
        }

        // ---- gear sets / storage ---------------------------------------------------------------

        public int SetCount => Mathf.Clamp(Plugin.SetCount.Value, 1, MaxSets);

        public int ActiveSet
        {
            get
            {
                var zdo = Zdo;
                return zdo == null ? 0 : Mathf.Clamp(zdo.GetInt(ActiveSetKey, 0), 0, SetCount - 1);
            }
        }

        public bool IsEmpty(int slot) => IsEmpty(ActiveSet, slot);
        public bool IsEmpty(int set, int slot) => Zdo == null || Zdo.GetInt(ItemKeys[set, slot]) == 0;
        public ItemDrop.ItemData LoadItem(int slot) => LoadItem(ActiveSet, slot);

        public ItemDrop.ItemData LoadItem(int set, int slot)
        {
            var zdo = Zdo;
            if (zdo == null) return null;
            int hash = zdo.GetInt(ItemKeys[set, slot]);
            if (hash == 0) return null;
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return null;
            var data = drop.m_itemData.Clone();
            data.m_dropPrefab = prefab;
            ItemDrop.LoadFromZDO(data, zdo, set * SlotStride + slot);
            data.m_stack = 1;
            data.m_equipped = false;
            return data;
        }

        /// <summary>Switches which set is displayed (and used by hotbar-equip). Owner-side write.</summary>
        public void SelectSet(int set)
        {
            if (set < 0 || set >= SetCount || !HasAccess(true) || !TakeOwnership()) return;
            Zdo.Set(ActiveSetKey, set);
            RewriteVisuals();
            BroadcastLabel();
        }

        /// <summary>Switches to the next/previous set (wraps around).</summary>
        public void StepSet(int delta)
        {
            int n = SetCount;
            SelectSet(((ActiveSet + delta) % n + n) % n);
        }

        private void RewriteVisuals()
        {
            var zdo = Zdo;
            int set = ActiveSet;
            for (int i = 0; i < Slots.Length; i++)
            {
                var def = Slots[i];
                var item = zdo.GetInt(ItemKeys[set, i]) != 0 ? LoadItem(set, i) : null;
                zdo.Set(def.VisualKey, item != null ? item.m_dropPrefab.name.GetStableHashCode() : 0);
                if (def.VariantKey != 0) zdo.Set(def.VariantKey, item != null ? item.m_variant : 0);
                if (def.QualityKey != 0) zdo.Set(def.QualityKey, item != null ? item.m_quality : 0);
            }
        }

        private void WriteSlot(int set, int slot, ItemDrop.ItemData item)
        {
            var zdo = Zdo;
            var def = Slots[slot];
            var copy = item.Clone();
            copy.m_stack = 1;
            copy.m_equipped = false;
            int hash = copy.m_dropPrefab.name.GetStableHashCode();
            zdo.Set(ItemKeys[set, slot], hash);
            ItemDrop.SaveToZDO(copy, zdo, set * SlotStride + slot);
            if (set != ActiveSet) return;
            zdo.Set(def.VisualKey, hash);
            if (def.VariantKey != 0) zdo.Set(def.VariantKey, copy.m_variant);
            if (def.QualityKey != 0) zdo.Set(def.QualityKey, copy.m_quality);
        }

        private void ClearSlot(int set, int slot)
        {
            var zdo = Zdo;
            var def = Slots[slot];
            zdo.Set(ItemKeys[set, slot], 0);
            zdo.Set(BlobKeys[set, slot], new byte[0]);
            if (set != ActiveSet) return;
            zdo.Set(def.VisualKey, 0);
            if (def.VariantKey != 0) zdo.Set(def.VariantKey, 0);
            if (def.QualityKey != 0) zdo.Set(def.QualityKey, 0);
        }

        public bool CanAttach(int slot, ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null || item.m_dropPrefab == null) return false;
            var type = item.m_shared.m_attachOverride != ItemDrop.ItemData.ItemType.None ? item.m_shared.m_attachOverride : item.m_shared.m_itemType;
            if (Array.IndexOf(Slots[slot].Types, type) < 0) return false;
            if (type == ItemDrop.ItemData.ItemType.Chest || type == ItemDrop.ItemData.ItemType.Legs) return true;
            // same visibility rule as the vanilla armor stand: the item prefab needs an attach point to be shown
            var t = item.m_dropPrefab.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                string n = t.GetChild(i).gameObject.name;
                if (n == "attach" || n == "attach_skin") return true;
            }
            return false;
        }

        /// <summary>Moves one item from the player's inventory into an empty slot of the active set.</summary>
        public bool TryEquip(int slot, Player player, ItemDrop.ItemData item)
        {
            if (player == null || item == null || !HasAccess(true)) return false;
            if (!CanAttach(slot, item)) { player.Message(MessageHud.MessageType.Center, "That item does not fit this slot."); return false; }
            if (!TakeOwnership()) return false;
            int set = ActiveSet;
            if (!IsEmpty(set, slot)) { player.Message(MessageHud.MessageType.Center, "That slot is already occupied."); return false; }

            var inventory = player.GetInventory();
            if (!inventory.ContainsItem(item)) return false;

            bool wasEquipped = player.IsItemEquiped(item);
            try
            {
                WriteSlot(set, slot, item);
                if (wasEquipped) player.UnequipItem(item);
                if (!inventory.RemoveOneItem(item))
                {
                    ClearSlot(set, slot); // nothing was taken from the player, so nothing may stay on the mannequin
                    return false;
                }
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogError("ValheimMannequins: equip failed, rolling back: " + e);
                ClearSlot(set, slot);
                return false;
            }
            return true;
        }

        /// <summary>Moves the item in a slot of the active set into the player's inventory.</summary>
        public bool TryTake(int slot, Player player)
        {
            if (player == null || !HasAccess(true)) return false;
            if (!TakeOwnership()) return false;
            int set = ActiveSet;
            if (IsEmpty(set, slot)) return false;

            var item = LoadItem(set, slot);
            if (item == null) { player.Message(MessageHud.MessageType.Center, "This item can no longer be loaded (missing mod?)."); return false; }

            var inventory = player.GetInventory();
            if (!inventory.CanAddItem(item)) { player.Message(MessageHud.MessageType.Center, "$msg_inventoryfull"); return false; }
            if (!inventory.AddItem(item)) return false;

            ClearSlot(set, slot); // only after the player really holds the item
            return true;
        }

        // ---- gear swap -------------------------------------------------------------------------

        /// <summary>Fills worn[0..6] with the player's currently worn gear by mannequin slot.</summary>
        private static void CurrentGearSlots(Player player, ItemDrop.ItemData[] worn)
        {
            // Valheim holsters idle weapons: they stay equipped but move from m_rightItem/m_leftItem to the hidden fields.
            var right = (RightItemField?.GetValue(player) ?? HiddenRightItemField?.GetValue(player)) as ItemDrop.ItemData;
            var left = (LeftItemField?.GetValue(player) ?? HiddenLeftItemField?.GetValue(player)) as ItemDrop.ItemData;
            foreach (var item in player.GetInventory().GetEquippedItems())
            {
                int slot;
                switch (item.m_shared.m_itemType)
                {
                    case ItemDrop.ItemData.ItemType.Helmet: slot = 0; break;
                    case ItemDrop.ItemData.ItemType.Chest: slot = 1; break;
                    case ItemDrop.ItemData.ItemType.Legs: slot = 2; break;
                    case ItemDrop.ItemData.ItemType.Shoulder: slot = 3; break;
                    case ItemDrop.ItemData.ItemType.Utility: slot = 4; break;
                    default:
                        // anything else equipped is a hand item; shields go left, otherwise match the hand fields, else right then left
                        var type = item.m_shared.m_itemType;
                        bool handItem = type == Shield || Array.IndexOf(Slots[5].Types, type) >= 0;
                        slot = !handItem ? -1 : item == right ? 5 : item == left ? 6
                             : type == Shield ? 6
                             : worn[5] == null ? 5 : 6;
                        break;
                }
                if (slot >= 0 && worn[slot] == null) worn[slot] = item;
            }

        }

        // ---- back slots: items that travel with a set -------------------------------------------
        // A player has no "back slot", so a weapon/shield on a set's back slot is handed to the player's inventory
        // when the set is swapped in, and the set REMEMBERS it (a descriptor per set + back slot). When the set is
        // swapped again, the remembered item is looked up in the player's inventory and moved back onto the mannequin.
        // Nothing is ever copied: an item that was used up or dropped in the meantime simply stays gone.

        private const int BackFirst = 7, BackCount = 2;
        private static readonly int[,] CarryKeys = BuildCarryKeys();

        private static int[,] BuildCarryKeys()
        {
            var k = new int[MaxSets, BackCount];
            for (int s = 0; s < MaxSets; s++)
                for (int b = 0; b < BackCount; b++)
                    k[s, b] = ("PM_carry" + s + "_" + b).GetStableHashCode();
            return k;
        }

        private static int CarryIndex(int set, int back) => 200 + set * BackCount + back;

        private ItemDrop.ItemData LoadCarry(int set, int back)
        {
            var zdo = Zdo;
            int hash = zdo.GetInt(CarryKeys[set, back]);
            if (hash == 0) return null;
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) return null;
            var data = drop.m_itemData.Clone();
            data.m_dropPrefab = prefab;
            ItemDrop.LoadFromZDO(data, zdo, CarryIndex(set, back));
            return data;
        }

        private void WriteCarry(int set, int back, ItemDrop.ItemData item)
        {
            var copy = CleanCopy(item);
            Zdo.Set(CarryKeys[set, back], copy.m_dropPrefab.name.GetStableHashCode());
            ItemDrop.SaveToZDO(copy, Zdo, CarryIndex(set, back));
        }

        private void ClearCarry(int set, int back)
        {
            Zdo.Set(CarryKeys[set, back], 0);
            Zdo.Set((CarryIndex(set, back) + "_itemData").GetStableHashCode(), new byte[0]);
        }

        /// <summary>Finds the inventory item that best matches a remembered descriptor (same prefab, quality, variant, crafter; closest durability).</summary>
        private static ItemDrop.ItemData FindCarried(Inventory inventory, ItemDrop.ItemData desc, ICollection<ItemDrop.ItemData> exclude)
        {
            if (desc == null) return null;
            string prefabName = desc.m_dropPrefab.name;
            ItemDrop.ItemData best = null;
            float bestDiff = float.MaxValue;
            foreach (var it in inventory.GetAllItems())
            {
                if (exclude.Contains(it) || it.m_dropPrefab == null || it.m_dropPrefab.name != prefabName) continue;
                if (it.m_quality != desc.m_quality || it.m_variant != desc.m_variant || it.m_crafterID != desc.m_crafterID) continue;
                float diff = Mathf.Abs(it.m_durability - desc.m_durability);
                if (diff < bestDiff) { best = it; bestDiff = diff; }
            }
            return best;
        }

        private static ItemDrop.ItemData CleanCopy(ItemDrop.ItemData item)
        {
            var copy = item.Clone();
            copy.m_stack = 1;
            copy.m_equipped = false;
            return copy;
        }

        // ---- gear swap -------------------------------------------------------------------------

        /// <summary>
        /// Exchanges the worn gear of the player (armor, cape, belt, hands) with gear set <paramref name="set"/> of this
        /// mannequin, then displays that set. The set's back-slot weapons go to the player's inventory and the items
        /// remembered from the previous swap go back onto the mannequin. Everything is checked before anything changes.
        /// </summary>
        public bool SwapWithPlayer(Player player, int set)
        {
            if (player == null) return false;
            if (set < 0 || set >= SetCount) { player.Message(MessageHud.MessageType.Center, $"This mannequin has {SetCount} gear set(s)."); return false; }
            if (!HasAccess(true) || !TakeOwnership()) return false;

            var inventory = player.GetInventory();
            var mine = new ItemDrop.ItemData[SwapSlots];
            var theirs = new ItemDrop.ItemData[SwapSlots];
            CurrentGearSlots(player, mine);

            // worn slots (0..6): player <-> set
            for (int s = 0; s < SwapSlots; s++)
            {
                if (IsEmpty(set, s)) continue;
                theirs[s] = LoadItem(set, s);
                if (theirs[s] == null) { player.Message(MessageHud.MessageType.Center, "Gear set contains an item that cannot be loaded (missing mod?)."); return false; }
            }

            // back slots (7,8): content -> player (and remembered), remembered item <- player
            var backOut = new ItemDrop.ItemData[BackCount];   // currently on the mannequin, goes to the player
            var backIn = new ItemDrop.ItemData[BackCount];    // remembered earlier, found in the player's inventory
            var taken = new List<ItemDrop.ItemData>(mine);
            for (int b = 0; b < BackCount; b++)
            {
                if (!IsEmpty(set, BackFirst + b))
                {
                    backOut[b] = LoadItem(set, BackFirst + b);
                    if (backOut[b] == null) { player.Message(MessageHud.MessageType.Center, "Gear set contains an item that cannot be loaded (missing mod?)."); return false; }
                }
                backIn[b] = FindCarried(inventory, LoadCarry(set, b), taken);
                if (backIn[b] != null) taken.Add(backIn[b]);
            }

            int toAdd = 0, toRemove = 0;
            for (int s = 0; s < SwapSlots; s++) { if (theirs[s] != null) toAdd++; if (mine[s] != null) toRemove++; }
            for (int b = 0; b < BackCount; b++) { if (backOut[b] != null) toAdd++; if (backIn[b] != null) toRemove++; }
            bool hadRecord = false;
            for (int b = 0; b < BackCount; b++) hadRecord |= Zdo.GetInt(CarryKeys[set, b]) != 0;
            if (toAdd == 0 && toRemove == 0 && !hadRecord) { player.Message(MessageHud.MessageType.Center, "Nothing to swap."); return false; }
            if (inventory.GetEmptySlots() + toRemove < toAdd) { player.Message(MessageHud.MessageType.Center, "$msg_inventoryfull"); return false; }

            var removed = new List<ItemDrop.ItemData>();
            var added = new List<ItemDrop.ItemData>();
            var storedWorn = new ItemDrop.ItemData[SwapSlots];
            var storedBack = new ItemDrop.ItemData[BackCount];
            try
            {
                for (int s = 0; s < SwapSlots; s++)
                {
                    if (mine[s] == null) continue;
                    storedWorn[s] = CleanCopy(mine[s]);
                    player.UnequipItem(mine[s], false);
                    if (!inventory.RemoveItem(mine[s])) throw new Exception("could not remove worn item " + mine[s].m_shared.m_name);
                    removed.Add(mine[s]);
                }
                for (int b = 0; b < BackCount; b++)
                {
                    if (backIn[b] == null) continue;
                    storedBack[b] = CleanCopy(backIn[b]);
                    player.UnequipItem(backIn[b], false);
                    if (!inventory.RemoveItem(backIn[b])) throw new Exception("could not take back remembered item " + backIn[b].m_shared.m_name);
                    removed.Add(backIn[b]);
                }
                for (int s = 0; s < SwapSlots; s++)
                {
                    if (theirs[s] == null) continue;
                    if (!inventory.AddItem(theirs[s])) throw new Exception("could not add set item " + theirs[s].m_shared.m_name);
                    added.Add(theirs[s]);
                }
                for (int b = 0; b < BackCount; b++)
                {
                    if (backOut[b] == null) continue;
                    if (!inventory.AddItem(backOut[b])) throw new Exception("could not add back item " + backOut[b].m_shared.m_name);
                    added.Add(backOut[b]);
                }
            }
            catch (Exception e)
            {
                // roll the inventory back; nothing has been written to the mannequin yet
                Jotunn.Logger.LogError("ValheimMannequins: gear swap failed, rolling back: " + e.Message);
                foreach (var a in added) inventory.RemoveItem(a);
                foreach (var r in removed) { if (!inventory.ContainsItem(r)) inventory.AddItem(r); }
                player.Message(MessageHud.MessageType.Center, "Gear swap failed.");
                return false;
            }

            // inventory side succeeded -> the set now holds what the player wore plus the returned back items,
            // and remembers the back items it just handed out
            Zdo.Set(ActiveSetKey, set);
            for (int s = 0; s < SwapSlots; s++)
            {
                if (storedWorn[s] != null) WriteSlot(set, s, storedWorn[s]); else ClearSlot(set, s);
            }
            for (int b = 0; b < BackCount; b++)
            {
                if (storedBack[b] != null) WriteSlot(set, BackFirst + b, storedBack[b]); else ClearSlot(set, BackFirst + b);
                if (backOut[b] != null) WriteCarry(set, b, backOut[b]); else ClearCarry(set, b);
            }
            RewriteVisuals();
            BroadcastLabel();

            // wear the set's gear; items from the back slots stay in the inventory
            for (int s = 0; s < SwapSlots; s++)
                if (theirs[s] != null) player.EquipItem(theirs[s], true);
            return true;
        }

        // ---- Hoverable / Interactable ----------------------------------------------------------

        public string GetHoverName() => _shown.Length > 0 ? _shown : "Mannequin";

        public float GetHoverOffset() => 0f;

        public string GetHoverText()
        {
            if (!PrivateArea.CheckAccess(transform.position, 0f, false))
                return Localization.instance.Localize("Mannequin\n$piece_noaccess");
            string mod = Plugin.ModifierLabel();
            return Localization.instance.Localize(
                $"{GetHoverName()}  (set {ActiveSet + 1}/{SetCount})\n[<color=yellow><b>$KEY_Use</b></color>] Manage\n" +
                "[<color=yellow><b>$KEY_HotbarUse</b></color>] Equip hotbar item\n" +
                $"[<color=yellow><b>{mod}+1-{SetCount}</b></color>] Swap gear set with mine");
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;
            if (!HasAccess(true)) return true;
            MannequinPanel.Show(this);
            return true;
        }

        /// <summary>
        /// Vanilla armor-stand style: select an item on the hotbar while aiming at the mannequin and it is
        /// attached to the first free slot of the active set that fits. Humanoid.UseItem calls this for the hovered object.
        /// </summary>
        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            var player = user as Player;
            if (player == null || player != Player.m_localPlayer || item == null) return false;
            if (!HasAccess(true)) return true;

            bool fitsSomewhere = false;
            for (int i = 0; i < Slots.Length; i++)
            {
                if (!CanAttach(i, item)) continue;
                fitsSomewhere = true;
                if (IsEmpty(i)) { TryEquip(i, player, item); return true; }
            }
            if (fitsSomewhere) player.Message(MessageHud.MessageType.Center, "All matching slots are occupied.");
            return fitsSomewhere; // false lets the game show its normal "can't use on" message
        }
    }

    /// <summary>
    /// Alt+number (configurable modifier) while aiming at a mannequin swaps gear sets instead of using the hotbar item.
    /// Event-driven: hooks the game's own hotbar handler, no per-frame code of ours.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UseHotbarItem))]
    internal static class HotbarSwapPatch
    {
        private static bool Prefix(Player __instance, int index)
        {
            if (__instance != Player.m_localPlayer || !Plugin.ModifierHeld()) return true;
            var hover = __instance.GetHoverObject();
            var mannequin = hover != null ? hover.GetComponentInParent<Mannequin>() : null;
            if (mannequin == null) return true;
            mannequin.SwapWithPlayer(__instance, index - 1);
            return false; // swallow the hotbar use
        }
    }
}

namespace ValheimMannequins
{
    /// <summary>
    /// Mouse wheel while aiming at a mannequin switches its displayed gear set. The wheel is read raw (the game's own
    /// readers are zeroed for that frame so the camera does not zoom). Only active while scrolling; skipped in build
    /// mode and whenever a menu/dialog is open.
    /// </summary>
    internal static class MannequinScroll
    {
        public static Mannequin Hovered()
        {
            var player = Player.m_localPlayer;
            if (player == null || player.InPlaceMode() || player.IsDead()) return null;
            if (InventoryGui.IsVisible() || Menu.IsVisible() || TextInput.IsVisible() || Console.IsVisible() ||
                MannequinPanel.IsOpen || (Chat.instance != null && Chat.instance.HasFocus()))
                return null;
            var hover = player.GetHoverObject();
            return hover != null ? hover.GetComponentInParent<Mannequin>() : null;
        }
    }

    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
    internal static class ScrollZoomPatch
    {
        private static void Postfix(ref float __result)
        {
            if (__result == 0f || !Plugin.ScrollSwitchesSet.Value) return;
            if (MannequinScroll.Hovered() != null) __result = 0f;
        }
    }
}

namespace ValheimMannequins
{
    /// <summary>Runs after the game's own body-model update so a mannequin's eye texture survives model changes.</summary>
    [HarmonyPatch(typeof(VisEquipment), "UpdateBaseModel")]
    internal static class EyeTexturePatch
    {
        private static void Postfix(VisEquipment __instance)
        {
            if (Mannequin.ByVis.Count == 0) return;
            if (Mannequin.ByVis.TryGetValue(__instance, out var mannequin)) mannequin.OnBodyUpdated();
        }
    }
}
