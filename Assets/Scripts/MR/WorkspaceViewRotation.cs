// File: WorkspaceViewRotation.cs
// Viewing convenience: rotates the ENTIRE placed workspace (CalibrationOffset, which
// carries EngineAnchor with the engine, trays, ghosts, targets, mat and every
// installed component) about the vertical axis through the engine's centre, in
// steps: [ Rotate Left ] [ Reset View ] [ Rotate Right ]. Children move with their
// parent, so every validation transform, ghost and installed part stays exactly where
// it belongs relative to the engine. No vertical translation and no scale can change
// through this control, and it never counts as an assembly action or error - it only
// writes a note to the log.
//
// The three buttons live INSIDE the main instruction card (bottom centre, between
// Back and Continue), i.e. on the one world-space canvas whose Interaction SDK ray /
// pointable wiring is proven on the device. A separate floating canvas was tried
// first and received no input: parented under the card it became a nested canvas,
// and the card's clipped interaction surface does not extend below the card.

using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.MR
{
    public class WorkspaceViewRotation : MonoBehaviour
    {
        [Tooltip("Degrees per press.")]
        [SerializeField] private float stepDegrees = 15f;

        [Tooltip("Bottom-centre positions of the three buttons inside the card, canvas units.")]
        [SerializeField] private float buttonSpacing = 118f;
        [SerializeField] private Vector2 buttonSize = new Vector2(112f, 44f);
        [SerializeField] private float buttonBottom = 24f;

        [SerializeField] private bool logChanges = true;

        private Transform _root;                 // CalibrationOffset
        private Transform _engine;               // EngineAnchor (pivot source)
        private Vector3 _authoredLocalPosition;
        private Quaternion _authoredLocalRotation;
        private Vector3 _authoredLocalScale;
        private bool _captured;
        private float _yaw;
        private System.Action<string> _note;

        public float CurrentYaw { get { return _yaw; } }

        /// <summary>
        /// Creates (once) the rotation controller for the workspace root and its buttons
        /// on the given card panel.
        /// </summary>
        public static WorkspaceViewRotation Ensure(Transform workspaceRoot, Transform engineAnchor, RectTransform cardPanel,
                                                   GameObject buttonTemplate, System.Action<string> note)
        {
            if (workspaceRoot == null) return null;
            var existing = workspaceRoot.GetComponent<WorkspaceViewRotation>();
            if (existing != null) return existing;

            var vr = workspaceRoot.gameObject.AddComponent<WorkspaceViewRotation>();
            vr._root = workspaceRoot;
            vr._engine = engineAnchor;
            vr._note = note;
            vr.CaptureAuthored();
            vr.BuildButtons(cardPanel, buttonTemplate);
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

        public void RotateLeft() { Rotate(-stepDegrees); }
        public void RotateRight() { Rotate(stepDegrees); }

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

        // ---------------- buttons on the card ----------------

        private void BuildButtons(RectTransform cardPanel, GameObject template)
        {
            if (cardPanel == null)
            {
                Debug.LogError("[ViewRotation] no card panel: the Rotate / Reset View buttons were not created.");
                return;
            }
            if (cardPanel.Find("ViewRotateLeft") != null) return;      // never duplicated

            MakeButton(cardPanel, template, "ViewRotateLeft", "↺ Rotate", -buttonSpacing, RotateLeft);
            MakeButton(cardPanel, template, "ViewReset", "Reset View", 0f, ResetView);
            MakeButton(cardPanel, template, "ViewRotateRight", "Rotate ↻", buttonSpacing, RotateRight);

            if (logChanges) Debug.Log($"[ViewRotation] Rotate Left / Reset View / Rotate Right created inside '{cardPanel.name}' (bottom centre).");
        }

        private void MakeButton(RectTransform parent, GameObject template, string name, string label, float x, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go;
            if (template != null)
            {
                go = Instantiate(template, parent);
                // The template's serialized listeners must not come along: a fresh Button.
                var old = go.GetComponent<Button>();
                if (old != null) DestroyImmediate(old);
                foreach (Transform extra in go.transform)
                    if (extra.GetComponent<TextMeshProUGUI>() == null) Destroy(extra.gameObject);
                foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
                    if (mb != null && !(mb is Image) && !(mb is Button)) Destroy(mb);
            }
            else
            {
                go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(parent, false);
                var l = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                l.transform.SetParent(go.transform, false);
                var lrt = l.GetComponent<RectTransform>(); lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            }
            go.name = name;
            go.SetActive(true);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(x, buttonBottom);
            rt.sizeDelta = buttonSize;
            rt.localScale = Vector3.one;

            var text = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                text.text = label;
                text.enableAutoSizing = false;
                text.fontSize = 16f;
                text.fontStyle = FontStyles.Normal;
                text.alignment = TextAlignmentOptions.Center;
                var trt = text.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            }

            var button = go.AddComponent<Button>();
            button.onClick.AddListener(onClick);
        }
    }
}
