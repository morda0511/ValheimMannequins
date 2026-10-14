using TMPro;
using UnityEngine;

namespace ValheimMannequins
{
    /// <summary>
    /// Name above a mannequin's head, drawn exactly like another player's name: a clone of the vanilla player-HUD name
    /// text (same font, size, colour, outline) placed in the game's own HUD root and projected to the screen the way
    /// EnemyHud does it. Only the name text is cloned - no health bar or other player UI.
    ///
    /// This component sits on a small anchor object above the head. It is created when the first name is set and only
    /// runs (LateUpdate) while a name exists; text changes only on name events.
    /// </summary>
    internal class Nameplate : MonoBehaviour
    {
        private const float HeadHeight = 2.1f; // vanilla uses GetHeadPoint() + 0.3 m

        private GameObject _container;
        private TextMeshProUGUI _text;
        private bool _hasText;

        public static Nameplate Create(Transform parent)
        {
            var go = new GameObject("NameplateAnchor");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, HeadHeight, 0f);
            var plate = go.AddComponent<Nameplate>();
            plate.enabled = false;
            return plate;
        }

        private bool EnsureWidget()
        {
            if (_container != null) return true;
            var hud = EnemyHud.instance;
            if (hud == null || hud.m_baseHudPlayer == null || hud.m_hudRoot == null) return false;
            var template = hud.m_baseHudPlayer.transform.Find("Name");
            if (template == null) { Jotunn.Logger.LogWarning("ValheimMannequins: vanilla player name widget not found; no nameplate."); return false; }

            _container = new GameObject("PM_Nameplate", typeof(RectTransform));
            var crect = _container.GetComponent<RectTransform>();
            crect.SetParent(hud.m_hudRoot.transform, false);
            crect.anchorMin = crect.anchorMax = new Vector2(0.5f, 0.5f);
            crect.sizeDelta = Vector2.zero;

            var clone = Instantiate(template.gameObject, crect, false);
            clone.SetActive(true);
            var rect = clone.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            _text = clone.GetComponent<TextMeshProUGUI>();
            _text.richText = false; // names are plain text, never markup
            _container.SetActive(false);
            return true;
        }

        private string _wanted = "";

        public void SetText(string text)
        {
            _wanted = text ?? "";
            _hasText = _wanted.Length > 0;
            if (_hasText) { if (EnsureWidget()) _text.text = _wanted; }
            else if (_container != null) _container.SetActive(false);
            enabled = _hasText; // if the HUD is not ready yet, LateUpdate retries
        }

        private void LateUpdate()
        {
            if (_container == null)
            {
                if (!EnsureWidget()) return;
                _text.text = _wanted;
            }
            var cam = Utils.GetMainCamera();
            if (cam == null) return;

            float range = Plugin.NameDistance.Value;
            if ((cam.transform.position - transform.position).sqrMagnitude > range * range) { Hide(); return; }

            Vector3 screen = cam.WorldToScreenPointScaled(transform.position);
            // same visibility test as EnemyHud: hide when behind the camera
            if (screen.z <= 0f) { Hide(); return; }
            if (!_container.activeSelf) _container.SetActive(true);
            _container.transform.position = screen;
        }

        private void Hide()
        {
            if (_container.activeSelf) _container.SetActive(false);
        }

        private void OnDestroy()
        {
            if (_container != null) Destroy(_container);
        }
    }
}
