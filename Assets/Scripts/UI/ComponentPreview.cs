// File: ComponentPreview.cs
// A small, slowly rotating 3-D copy of the component the current action is about,
// shown inside the main instruction panel. It answers "WHAT object" - the thing to
// find in the tray - which text alone does badly and which matters most in the READ
// state, when the real parts are faded.
//
// It is a renderer-only clone of the actual part mesh: no collider, no Rigidbody, no
// Interaction SDK, not registered anywhere, so it cannot be grabbed, validated,
// consumed or counted. It lives in front of the panel in world space (robust in
// passthrough, no render texture or extra camera) and is destroyed when nothing is
// relevant.

using AdaptiveAR.Steps;
using UnityEngine;

namespace AdaptiveAR.UI
{
    public class ComponentPreview : MonoBehaviour
    {
        [Header("Placement inside the panel (canvas units, scale 0.001 = mm)")]
        [Tooltip("Offset from the panel centre. Right half of the card, mid height, in front of the glass.")]
        [SerializeField] private Vector3 localOffset = new Vector3(230f, 10f, -60f);

        [Tooltip("Longest extent of the preview, metres.")]
        [SerializeField] private float size = 0.10f;

        [Tooltip("Roles shown larger and embedded halfway through the panel plane.")]
        [SerializeField] private string[] largeRoles = { "crankshaft", "camshaft" };
        [SerializeField] private float largeSize = 0.14f;

        [Tooltip("Depth of the preview centre relative to the panel plane, canvas units (negative = towards the viewer).")]
        [SerializeField] private float defaultDepth = -20f;
        [SerializeField] private float largeDepth = 0f;

        private float _sizeInUse;
        private float _depthInUse;

        [Header("Motion")]
        [SerializeField] private float degreesPerSecond = 28f;
        [SerializeField] private float tiltDegrees = 18f;

        private Transform _panel;
        private GameObject _preview;
        private GameObject _source;
        private float _spin;

        /// <summary>Attaches the preview to a panel (the main canvas).</summary>
        public void Configure(Transform panel)
        {
            _panel = panel;
        }

        /// <summary>Shows a copy of the given part, or hides the preview when null.</summary>
        public void Show(GameObject source)
        {
            Show(source, null);
        }

        public void Show(GameObject source, string role)
        {
            if (source == _source && (_preview != null || source == null)) return;

            bool large = role != null && System.Array.IndexOf(largeRoles, role) >= 0;
            _sizeInUse = large ? largeSize : size;
            _depthInUse = large ? largeDepth : defaultDepth;

            Clear();
            _source = source;
            if (source == null || _panel == null) return;

            var mf = source.GetComponentInChildren<MeshFilter>(true);
            var mr = source.GetComponentInChildren<MeshRenderer>(true);
            if (mf == null || mf.sharedMesh == null || mr == null) return;

            _preview = new GameObject("ComponentPreview (" + source.name + ")");
            var pf = _preview.AddComponent<MeshFilter>();
            var pr = _preview.AddComponent<MeshRenderer>();
            pf.sharedMesh = mf.sharedMesh;
            pr.sharedMaterials = mr.sharedMaterials;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pr.receiveShadows = false;

            // The mesh's own local scale carries the part's real proportions; normalise the
            // longest extent to the preview size.
            Vector3 meshScale = mf.transform.lossyScale;
            Vector3 ext = Vector3.Scale(mf.sharedMesh.bounds.size, meshScale);
            float longest = Mathf.Max(Mathf.Abs(ext.x), Mathf.Abs(ext.y), Mathf.Abs(ext.z));
            float k = longest > 1e-5f ? _sizeInUse / longest : 1f;
            _preview.transform.localScale = new Vector3(Mathf.Abs(meshScale.x), Mathf.Abs(meshScale.y), Mathf.Abs(meshScale.z)) * k;

            _spin = 0f;
            Place();
        }

        public void Clear()
        {
            if (_preview != null) Destroy(_preview);
            _preview = null;
            _source = null;
        }

        private void LateUpdate()
        {
            if (_preview == null || _panel == null) return;
            _spin += degreesPerSecond * Time.deltaTime;
            Place();
        }

        private void Place()
        {
            // Panel-relative so the preview follows the card wherever the rig parks it.
            Vector3 world = _panel.TransformPoint(new Vector3(localOffset.x, localOffset.y, _depthInUse));
            Quaternion facing = _panel.rotation;
            Quaternion spin = Quaternion.AngleAxis(_spin, Vector3.up) * Quaternion.AngleAxis(tiltDegrees, Vector3.right);

            // Centre the mesh on its bounds centre, not its pivot.
            var mf = _preview.GetComponent<MeshFilter>();
            Vector3 centreOffset = mf != null && mf.sharedMesh != null
                ? spin * Vector3.Scale(mf.sharedMesh.bounds.center, _preview.transform.localScale)
                : Vector3.zero;

            _preview.transform.SetPositionAndRotation(world - centreOffset, spin);
            _ = facing;
        }

        private void OnDestroy()
        {
            Clear();
        }
    }
}
