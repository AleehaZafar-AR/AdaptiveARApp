// File: WorkspaceViewRotation.cs
// Viewing convenience: rotates the ENTIRE placed workspace (CalibrationOffset, which
// carries EngineAnchor with the engine, trays, ghosts, targets, mat and every
// installed component) about the vertical axis through the engine's centre, in
// steps, from a small floating panel: [ Rotate left ] [ Reset View ] [ Rotate right ].
// Children move with their parent, so every validation transform, ghost and installed
// part stays exactly where it belongs relative to the engine. No vertical translation
// and no scale can change through this control, and it never counts as an assembly
// action or error - it only writes a note to the log.
//
// The panel is a stripped clone of the existing pointable OverviewCanvas (same
// Interaction SDK ray/pointable wiring the other panels use), parented under the main
// instruction card so it follows it. Nothing in the authored UI layout is modified.

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.MR
{
    public class WorkspaceViewRotation : MonoBehaviour
    {
        [Tooltip("Degrees per press.")]
        [SerializeField] private float stepDegrees = 15f;

        [Tooltip("Position of the floating panel in the main card's canvas units (0.001 m), below the card.")]
        [SerializeField] private Vector3 panelLocalOffset = new Vector3(0f, -330f, 0f);

        [Tooltip("Panel size, canvas units.")]
        [SerializeField] private Vector2 panelSize = new Vector2(560f, 96f);

        [SerializeField] private bool logChanges = true;

        private Transform _root;                 // CalibrationOffset
        private Transform _engine;               // EngineAnchor (pivot source)
        private Vector3 _authoredLocalPosition;
        private Quaternion _authoredLocalRotation;
        private Vector3 _authoredLocalScale;
        private bool _captured;
        private float _yaw;
        private GameObject _panel;
        private System.Action<string> _note;

        public float CurrentYaw { get { return _yaw; } }

        /// <summary>
        /// Creates (once) the rotation controller for the workspace root and its floating panel.
        /// </summary>
        public static WorkspaceViewRotation Ensure(Transform workspaceRoot, Transform engineAnchor, GameObject pointableCanvasTemplate,
                                                   Transform mainCard, GameObject buttonTemplate, System.Action<string> note)
        {
            if (workspaceRoot == null) return null;
            var existing = workspaceRoot.GetComponent<WorkspaceViewRotation>();
            if (existing != null) return existing;

            var vr = workspaceRoot.gameObject.AddComponent<WorkspaceViewRotation>();
            vr._root = workspaceRoot;
            vr._engine = engineAnchor;
            vr._note = note;
            vr.CaptureAuthored();
            vr.BuildPanel(pointableCanvasTemplate, mainCard, buttonTemplate);
            return vr;
        }

        private void CaptureAuthored()
        {
            if (_captured || _root == null) return;
            _authoredLocalPosition = _root.localPosition;
            _authoredLocalRotation = _root.localRotation;
            _authoredLocalScale = _root.localScale;
            _captured = true;
        }

        private Vector3 Pivot()
        {
            Transform block = _engine != null ? _engine.Find("Offset/Ghosties/oilPan") : null;
            Transform src = block != null ? block : (_engine != null ? _engine : _root);
            bool any = false; var b = new Bounds();
            foreach (Renderer r in src.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b.center : src.position;
        }

        public void Rotate(float degrees)
        {
            if (_root == null) return;
            CaptureAuthored();
            Vector3 pivot = Pivot();
            float before = _root.position.y;
            _root.RotateAround(pivot, Vector3.up, degrees);
            // Yaw only: height and scale are never touched by this control.
            Vector3 p = _root.position; p.y = before; _root.position = p;
            _root.localScale = _authoredLocalScale;
            _yaw += degrees;
            if (logChanges) Debug.Log($"[ViewRotation] yaw={_yaw:F0} deg (step {degrees:F0}) about {pivot}; viewing only, not an assembly action.");
            _note?.Invoke($"view_rotated:{_yaw:F0}");
        }

        public void ResetView()
        {
            if (_root == null || !_captured) return;
            _root.localPosition = _authoredLocalPosition;
            _root.localRotation = _authoredLocalRotation;
            _root.localScale = _authoredLocalScale;
            _yaw = 0f;
            if (logChanges) Debug.Log("[ViewRotation] reset to the authored workspace rotation.");
            _note?.Invoke("view_reset");
        }

        // ---------------- floating panel ----------------

        private void BuildPanel(GameObject template, Transform mainCard, GameObject buttonTemplate)
        {
            if (template == null || mainCard == null)
            {
                Debug.LogError("[ViewRotation] no pointable canvas template or main card: the floating View panel was not created.");
                return;
            }

            _panel = Instantiate(template, mainCard);
            _panel.name = "ViewRotationCanvas";
            _panel.transform.localPosition = panelLocalOffset;
            _panel.transform.localRotation = Quaternion.identity;
            _panel.transform.localScale = Vector3.one;      // the card's 0.001 scale applies

            // Strip everything the template carried except its Interaction SDK wiring and a
            // background: rows, HUD scripts, extra panels.
            foreach (MonoBehaviour mb in _panel.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                string ns = mb.GetType().Namespace ?? "";
                if (ns.StartsWith("AdaptiveAR")) Destroy(mb);
            }
            var rect = _panel.GetComponent<RectTransform>();
            if (rect != null) rect.sizeDelta = panelSize;

            Transform background = null;
            var toDestroy = new List<GameObject>();
            foreach (Transform child in _panel.transform)
            {
                string n = child.name;
                bool isdk = n.StartsWith("Surface") || n.StartsWith("ISDK");
                if (isdk) continue;
                if (background == null && child.GetComponent<Image>() != null && child.gameObject.activeSelf) { background = child; continue; }
                toDestroy.Add(child.gameObject);
            }
            foreach (GameObject g in toDestroy) Destroy(g);
            if (background != null)
            {
                foreach (Transform grand in background)
                    if (grand.GetComponent<Image>() == null || grand.name != "Border") Destroy(grand.gameObject);
                var brt = background as RectTransform;
                if (brt != null) { brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero; }
            }

            MakeButton(buttonTemplate, "RotateLeft", "↺  Rotate", new Vector2(-190f, 0f), new Vector2(160f, 60f), () => Rotate(-stepDegrees));
            MakeButton(buttonTemplate, "ResetView", "Reset View", new Vector2(0f, 0f), new Vector2(170f, 60f), ResetView);
            MakeButton(buttonTemplate, "RotateRight", "Rotate  ↻", new Vector2(190f, 0f), new Vector2(160f, 60f), () => Rotate(stepDegrees));

            if (logChanges) Debug.Log($"[ViewRotation] floating panel created under '{mainCard.name}' at {panelLocalOffset} (canvas units).");
        }

        private void MakeButton(GameObject template, string name, string label, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go;
            if (template != null)
            {
                go = Instantiate(template, _panel.transform);
                foreach (Transform extra in go.transform)
                    if (extra.GetComponent<TextMeshProUGUI>() == null) Destroy(extra.gameObject);
            }
            else
            {
                go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                go.transform.SetParent(_panel.transform, false);
                var l = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                l.transform.SetParent(go.transform, false);
                var lrt = l.GetComponent<RectTransform>(); lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            }
            go.name = name;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one;

            var text = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                text.text = label;
                text.enableAutoSizing = false;
                text.fontSize = 20f;
                text.alignment = TextAlignmentOptions.Center;
            }

            var button = go.GetComponent<Button>();
            if (button == null) button = go.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(onClick);
        }
    }
}
