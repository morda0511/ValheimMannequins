using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimMannequins
{
    /// <summary>
    /// Single management window: left = what the mannequin wears, right = your items that fit the selected slot.
    /// Built lazily on first use, rebuilt only on user actions or when the mannequin's ZDO changes while open.
    /// </summary>
    internal class MannequinPanel : MonoBehaviour
    {
        private static MannequinPanel _instance;

        private Mannequin _target;
        private GameObject _root;
        private RectTransform _leftRows, _content;
        private Text _rightHeader, _poseLabel;
        private Text _title, _lookLabel;
        private bool _lookMode;
        private GameObject _scrollGo;
        private readonly List<GameObject> _dynamic = new List<GameObject>();
        private int _selected;
        private uint _lastRevision;
        private float _nextPoll;

        private const float MaxDistance = 6f;

        public static bool IsOpen => _instance != null && _instance._root != null && _instance._root.activeSelf;

        public static void Show(Mannequin target)
        {
            if (GUIManager.Instance == null || GUIManager.CustomGUIFront == null) return;
            if (_instance == null)
            {
                var go = new GameObject("PM_Panel");
                go.transform.SetParent(GUIManager.CustomGUIFront.transform, false);
                _instance = go.AddComponent<MannequinPanel>();
                _instance.Build(go.transform);
            }
            _instance.Open(target);
        }

        // ---- construction --------------------------------------------------------------------

        private void Build(Transform parent)
        {
            var gm = GUIManager.Instance;
            _root = gm.CreateWoodpanel(parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, 780f, 620f, true);
            var t = _root.transform;

            _title = Label(t, "Mannequin", new Vector2(0f, 285f), 28, 600f, 40f).GetComponent<Text>();
            Label(t, "Tip: aim at it and press an item's hotbar key", new Vector2(-190f, 203f), 15, 360f, 24f);

            var rows = new GameObject("Rows", typeof(RectTransform));
            _leftRows = rows.GetComponent<RectTransform>();
            _leftRows.SetParent(t, false);
            _leftRows.anchorMin = _leftRows.anchorMax = new Vector2(0.5f, 0.5f);
            _leftRows.anchoredPosition = Vector2.zero;
            _leftRows.sizeDelta = Vector2.zero;

            _rightHeader = Label(t, "", new Vector2(190f, 203f), 15, 340f, 24f).GetComponent<Text>();

            var scroll = gm.CreateScrollView(t, false, true, 8f, 4f, gm.ValheimScrollbarHandleColorBlock,
                new Color(0f, 0f, 0f, 0.35f), 340f, 380f);
            var rect = scroll.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(190f, 0f);
            _scrollGo = scroll;
            var scrollRect = scroll.GetComponentInChildren<ScrollRect>(true);
            _content = scrollRect.content;
            var vl = _content.gameObject.GetComponent<VerticalLayoutGroup>() ?? _content.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.childControlWidth = true; vl.childControlHeight = true;
            vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            vl.spacing = 4f; vl.padding = new RectOffset(4, 12, 4, 4);
            var fitter = _content.gameObject.GetComponent<ContentSizeFitter>() ?? _content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // bottom bar: pose, body, close
            Button(t, "<", new Vector2(-340f, -265f), 40f, 34f, () => _target?.RequestPoseStep(-1));
            _poseLabel = Label(t, "", new Vector2(-265f, -265f), 16, 110f, 30f).GetComponent<Text>();
            Button(t, ">", new Vector2(-190f, -265f), 40f, 34f, () => _target?.RequestPoseStep(1));
            var look = Button(t, "Look", new Vector2(-70f, -265f), 150f, 34f, () => { _lookMode = !_lookMode; Refresh(); });
            _lookLabel = look.GetComponentInChildren<Text>();
            Button(t, "Rename", new Vector2(110f, -265f), 150f, 34f, Rename);
            Button(t, "Close", new Vector2(290f, -265f), 140f, 34f, Close);

            _root.SetActive(false);
        }

        private static GameObject Label(Transform parent, string text, Vector2 pos, int size, float w, float h)
        {
            var gm = GUIManager.Instance;
            return gm.CreateText(text, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, gm.AveriaSerifBold, size,
                gm.ValheimBeige, true, Color.black, w, h, false);
        }

        private static GameObject Button(Transform parent, string text, Vector2 pos, float w, float h, UnityEngine.Events.UnityAction onClick)
        {
            var go = GUIManager.Instance.CreateButton(text, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, w, h);
            go.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(onClick);
            return go;
        }

        // ---- open / close / refresh ----------------------------------------------------------

        private void Open(Mannequin target)
        {
            _target = target;
            _selected = 0;
            _lookMode = false;
            _root.transform.localScale = Vector3.one * Plugin.PanelScale.Value;
            _root.SetActive(true);
            GUIManager.BlockInput(true);
            Refresh();
        }

        private void Rename()
        {
            var target = _target;
            if (target == null) return;
            Close(); // release the input block before the vanilla text dialog takes over
            target.RequestRename();
        }

        private void Close()
        {
            if (_root != null && _root.activeSelf)
            {
                _root.SetActive(false);
                GUIManager.BlockInput(false);
            }
            _target = null;
        }

        private void OnDisable()
        {
            if (_root != null && _root.activeSelf) GUIManager.BlockInput(false);
        }

        private void Update()
        {
            if (_root == null || !_root.activeSelf) return;
            var player = Player.m_localPlayer;
            if (_target == null || player == null || player.IsDead() ||
                Input.GetKeyDown(KeyCode.Escape) ||
                Vector3.Distance(player.transform.position, _target.transform.position) > MaxDistance)
            {
                Close();
                return;
            }
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.4f;
            var zdo = _target.Zdo;
            if (zdo == null) { Close(); return; }
            if (zdo.DataRevision != _lastRevision) Refresh();
        }

        private void Clear()
        {
            foreach (var go in _dynamic) if (go) Destroy(go);
            _dynamic.Clear();
        }

        private void Refresh()
        {
            if (_target == null || _target.Zdo == null) return;
            _lastRevision = _target.Zdo.DataRevision;
            Clear();
            var gm = GUIManager.Instance;

            // gear set tabs
            int sets = _target.SetCount;
            int active = _target.ActiveSet;
            for (int s = 0; s < sets; s++)
            {
                int set = s;
                float width = Mathf.Min(140f, 700f / sets - 6f);
                float x = (s - (sets - 1) / 2f) * (width + 6f);
                string setName = _target.GetSetName(set);
                string label = setName.Length == 0 ? "Set " + (set + 1) : (setName.Length > 11 ? setName.Substring(0, 10) + "…" : setName);
                var tab = Button(_leftRows, set == active ? "> " + label + " <" : label, new Vector2(x, 247f), width, 32f,
                    () => { _target.SelectSet(set); _selected = 0; Refresh(); });
                tab.GetComponentInChildren<Text>().fontSize = 15;
                _dynamic.Add(tab);
            }

            // left: slot list
            for (int i = 0; i < Mannequin.Slots.Length; i++)
            {
                int slot = i;
                var item = _target.LoadItem(i);
                bool filled = !_target.IsEmpty(i);
                string name = item != null ? Localization.instance.Localize(item.m_shared.m_name) : (filled ? "(unavailable item)" : "- empty -");
                string text = (slot == _selected ? "> " : "") + Mannequin.Slots[i].Label + ":  " + name;
                float y = 165f - i * 42f;
                var b = Button(_leftRows, text, new Vector2(-215f, y), 270f, 36f, () => { _selected = slot; Refresh(); });
                b.GetComponentInChildren<Text>().fontSize = 15;
                _dynamic.Add(b);
                if (filled)
                {
                    var take = Button(_leftRows, "Take", new Vector2(-30f, y), 70f, 36f, () => { _target.TryTake(slot, Player.m_localPlayer); Refresh(); });
                    take.GetComponentInChildren<Text>().fontSize = 15;
                    _dynamic.Add(take);
                }
            }

            _scrollGo.SetActive(!_lookMode);
            if (_lookMode) BuildLook();
            else
            {
            // right: fitting items from the player's inventory
            _rightHeader.text = "Your items for:  " + Mannequin.Slots[_selected].Label;
            foreach (Transform c in _content) Destroy(c.gameObject);
            var player = Player.m_localPlayer;
            if (player != null && _target.IsEmpty(_selected))
            {
                foreach (var item in player.GetInventory().GetAllItems())
                {
                    if (!_target.CanAttach(_selected, item)) continue;
                    var captured = item;
                    var row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
                    row.transform.SetParent(_content, false);
                    var hl = row.GetComponent<HorizontalLayoutGroup>();
                    hl.childControlWidth = hl.childControlHeight = true;
                    hl.childForceExpandWidth = false; hl.childForceExpandHeight = false;
                    hl.spacing = 6f;
                    row.GetComponent<LayoutElement>().minHeight = 36f;

                    var icon = new GameObject("Icon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                    icon.transform.SetParent(row.transform, false);
                    icon.GetComponent<Image>().sprite = item.GetIcon();
                    var le = icon.GetComponent<LayoutElement>(); le.minWidth = le.preferredWidth = 34f; le.minHeight = le.preferredHeight = 34f;

                    string label = Localization.instance.Localize(item.m_shared.m_name) +
                                   (item.m_quality > 1 ? $" (Q{item.m_quality})" : "") +
                                   (player.IsItemEquiped(item) ? " [worn]" : "");
                    var btn = gm.CreateButton(label, row.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, 0f, 0f);
                    var bl = btn.AddComponent<LayoutElement>(); bl.flexibleWidth = 1f; bl.minHeight = 34f; bl.preferredHeight = 34f;
                    btn.GetComponentInChildren<Text>().fontSize = 14;
                    btn.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() =>
                    {
                        _target.TryEquip(_selected, Player.m_localPlayer, captured);
                        Refresh();
                    });
                }
            }
            else if (!_target.IsEmpty(_selected))
            {
                AddNote("This slot is occupied. Take the item first.");
            }
            }

            _title.text = _target.Label.Length > 0 ? "Mannequin - " + _target.Label : "Mannequin";
            _poseLabel.text = $"Pose {_target.Pose + 1}/{Mannequin.PoseCount}";
            _lookLabel.text = _lookMode ? "Back to items" : "Look";
        }

        // ---- appearance pane -------------------------------------------------------------------

        private void BuildLook()
        {
            _rightHeader.text = "Appearance";
            var m = _target;
            LookRow("Body", m.IsFemale ? "Female" : "Male", null, 150f, d => m.ToggleBody());
            LookRow("Hair style", m.HairName, null, 85f, d => m.StepHair(d));
            LookRow("Hair colour", Mannequin.HairTones[m.HairToneIndex].Name, Mannequin.HairTones[m.HairToneIndex].Color, 20f, d => m.StepHairTone(d));
            LookRow("Beard", m.BeardName, null, -45f, d => m.StepBeard(d));
            LookRow("Skin tone", Mannequin.SkinTones[m.SkinIndex].Name, Mannequin.SkinTones[m.SkinIndex].Color, -110f, d => m.StepSkin(d));
            LookRow("Eye colour", Mannequin.EyeTones[m.EyeIndex].Name, Mannequin.EyeTones[m.EyeIndex].Color, -175f, d => m.StepEyes(d));
        }

        private void LookRow(string title, string value, Color? swatch, float y, System.Action<int> step)
        {
            const float cx = 190f;
            _dynamic.Add(Label(_leftRows, title, new Vector2(cx, y + 24f), 15, 300f, 24f));
            var left = Button(_leftRows, "<", new Vector2(cx - 145f, y - 8f), 44f, 34f, () => { step(-1); Refresh(); });
            var right = Button(_leftRows, ">", new Vector2(cx + 145f, y - 8f), 44f, 34f, () => { step(1); Refresh(); });
            _dynamic.Add(left); _dynamic.Add(right);
            _dynamic.Add(Label(_leftRows, value, new Vector2(swatch.HasValue ? cx - 22f : cx, y - 8f), 17, 150f, 30f));
            if (swatch.HasValue)
            {
                var sw = new GameObject("Swatch", typeof(RectTransform), typeof(Image));
                var rt = sw.GetComponent<RectTransform>();
                rt.SetParent(_leftRows, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(cx + 88f, y - 8f);
                rt.sizeDelta = new Vector2(26f, 26f);
                sw.GetComponent<Image>().color = swatch.Value;
                _dynamic.Add(sw);
            }
        }

        private void AddNote(string msg)
        {
            var go = GUIManager.Instance.CreateText(msg, _content, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                GUIManager.Instance.AveriaSerifBold, 15, GUIManager.Instance.ValheimBeige, false, Color.black, 300f, 30f, false);
            go.AddComponent<LayoutElement>().minHeight = 30f;
        }
    }
}
