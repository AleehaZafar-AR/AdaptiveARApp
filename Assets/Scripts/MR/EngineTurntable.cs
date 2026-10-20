// File: EngineTurntable.cs
// A subtle turntable under the engine: grab its rim and the ENGINE turns about the
// world vertical axis - oil pan, every installed component and every engine-side
// target/ghost with it. Trays, loose parts, the piston mat and the UI do not move.
// Viewing convenience only: yaw, no translation, no tilt, no scale, never an assembly
// action or an error (a log note on release).
//
// How the hierarchy makes that true
// ---------------------------------
//   EngineAnchor/Offset/EngineTurntable        <- this object, pivot under the oil pan
//        Ghosties (oil pan + every engine target ghost)   moved here at setup
//        <installed parts>                     adopted here when they lock onto the engine
//   EngineAnchor/Offset/PistonKits (mat ghosts) moved OUT of Ghosties: they belong to the mat
//   EngineAnchor/Offset/tray, tray (1), Worksurface, Components (loose parts)   untouched
//
// Grabbing uses the same Interaction SDK wiring as every part: a Grabbable driven by a
// OneGrabRotateTransformer (pivot = this transform, axis = its up = world up) and a copy
// of the authored ISDK_HandGrabInteraction object re-pointed at this body.

using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.MR
{
    public class EngineTurntable : MonoBehaviour
    {
        public static EngineTurntable Current { get; private set; }

        [SerializeField] private float rimMargin = 0.05f;

        [Tooltip("Grab collider = the rim band only (not the whole disc). A controller's grab scoring prefers " +
                 "any collider its grab point is INSIDE, so a full disc under the engine out-competes small parts " +
                 "lying on or beside it; hands are gated by finger-volume overlap and do not see that.")]
        [SerializeField] private bool rimOnlyCollider = true;
        [SerializeField] private int rimSegments = 16;
        [SerializeField] private Color ringColor = new Color(0.55f, 0.58f, 0.62f, 0.35f);
        [SerializeField] private bool logChanges = true;

        private Quaternion _authoredLocalRotation;
        private GameObject _visual;
        private Material _material;
        private Collider _collider;
        private GameObject _colliderRoot;
        private Oculus.Interaction.Grabbable _grabbable;
        private readonly List<MonoBehaviour> _interactables = new List<MonoBehaviour>();
        private System.Action<string> _note;
        private bool _wasGrabbed;
        private float _lastLoggedYaw;

        /// <summary>Yaw away from the authored orientation, degrees.</summary>
        public float Yaw
        {
            get
            {
                Quaternion delta = Quaternion.Inverse(_authoredLocalRotation) * transform.localRotation;
                delta.ToAngleAxis(out float angle, out Vector3 axis);
                return Mathf.DeltaAngle(0f, angle * Mathf.Sign(Vector3.Dot(axis, transform.parent != null ? transform.parent.InverseTransformDirection(Vector3.up) : Vector3.up)));
            }
        }

        public bool IsRotated { get { return Quaternion.Angle(transform.localRotation, _authoredLocalRotation) > 2f; } }

        public static EngineTurntable Ensure(Transform engineAnchor, GameObject interactionTemplate, System.Action<string> note)
        {
            if (Current != null) return Current;
            if (engineAnchor == null) { Debug.LogError("[Turntable] no EngineAnchor."); return null; }
            Transform offset = engineAnchor.Find("Offset");
            if (offset == null) { Debug.LogError("[Turntable] EngineAnchor/Offset not found."); return null; }

            Transform existing = offset.Find("EngineTurntable");
            if (existing != null) { Current = existing.GetComponent<EngineTurntable>(); if (Current != null) return Current; }

            Transform ghosties = offset.Find("Ghosties");
            if (ghosties == null) { Debug.LogError("[Turntable] Offset/Ghosties not found."); return null; }

            // Pivot and radius from the oil pan (the fixed engine base).
            Transform oilPan = ghosties.Find("oilPan");
            Bounds b = RendererBounds(oilPan != null ? oilPan : ghosties, out bool any);
            if (!any) b = new Bounds(ghosties.position, Vector3.one * 0.3f);
            Vector3 pivot = new Vector3(b.center.x, b.min.y, b.center.z);
            float radius = Mathf.Max(b.extents.x, b.extents.z);

            var go = new GameObject("EngineTurntable");
            go.transform.SetParent(offset, false);
            go.transform.SetPositionAndRotation(pivot, Quaternion.identity);   // up = world up: yaw only
            var tt = go.AddComponent<EngineTurntable>();
            tt._note = note;
            tt._authoredLocalRotation = go.transform.localRotation;
            tt.Build(radius + tt.rimMargin, interactionTemplate);

            // Mat ghosts stay with the mat; everything else under Ghosties turns with the engine.
            Transform kits = ghosties.Find("PistonKits");
            if (kits != null) kits.SetParent(offset, true);
            ghosties.SetParent(go.transform, true);

            Current = tt;
            Debug.Log($"[Turntable] created under '{offset.name}' at {pivot} radius {radius + tt.rimMargin:F2} m; Ghosties adopted, PistonKits ghosts left with the mat.");
            return tt;
        }

        private static Bounds RendererBounds(Transform root, out bool any)
        {
            any = false; var b = new Bounds();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        private void Build(float radius, GameObject interactionTemplate)
        {
            // Subtle flat ring on the desk, not an engine component.
            _visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _visual.name = "TurntableRing";
            _visual.transform.SetParent(transform, false);
            _visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _visual.transform.localPosition = new Vector3(0f, 0.003f, 0f);
            _visual.transform.localScale = new Vector3(radius * 2f, radius * 2f, 1f);
            var qc = _visual.GetComponent<Collider>(); if (qc != null) Destroy(qc);
            Shader shader = Shader.Find("Sprites/Default"); if (shader == null) shader = Shader.Find("Unlit/Transparent");
            _material = new Material(shader) { mainTexture = BuildRingTexture(256), color = ringColor };
            var mr = _visual.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // Body + grab volume (a thin slab the size of the ring; the rim is what the hand reaches).
            var rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true; rb.useGravity = false;
            if (rimOnlyCollider)
            {
                // The ring texture is the outer ~20% of the radius: that band is the grab surface.
                float rimWidth = Mathf.Max(0.03f, radius * 0.2f);
                float midRadius = radius - rimWidth * 0.5f;
                int n = Mathf.Max(8, rimSegments);
                float chord = 2f * Mathf.PI * midRadius / n * 1.08f;
                var rim = new GameObject("RimCollider");
                rim.transform.SetParent(transform, false);
                for (int i = 0; i < n; i++)
                {
                    float a = i * 360f / n;
                    var seg = new GameObject("Seg" + i);
                    seg.transform.SetParent(rim.transform, false);
                    seg.transform.localRotation = Quaternion.Euler(0f, a, 0f);
                    seg.transform.localPosition = seg.transform.localRotation * new Vector3(0f, 0.015f, midRadius);
                    var b = seg.AddComponent<BoxCollider>();
                    b.size = new Vector3(chord, 0.03f, rimWidth);
                }
                _collider = rim.GetComponentInChildren<BoxCollider>();
                _colliderRoot = rim;
                if (logChanges) Debug.Log($"[Turntable] grab collider = rim band only ({n} segments, width {rimWidth:F3} m); the disc under the engine is not a grab target.");
            }
            else
            {
                var box = gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(radius * 2f, 0.03f, radius * 2f);
                box.center = new Vector3(0f, 0.015f, 0f);
                _collider = box;
            }

            _grabbable = gameObject.AddComponent<Oculus.Interaction.Grabbable>();
            var rot = gameObject.AddComponent<Oculus.Interaction.OneGrabRotateTransformer>();
            rot.InjectOptionalPivotTransform(transform);
            rot.InjectOptionalRotationAxis(Oculus.Interaction.OneGrabRotateTransformer.Axis.Up);
            _grabbable.InjectOptionalOneGrabTransformer(rot);
            _grabbable.WhenPointerEventRaised += OnPointerEvent;

            if (interactionTemplate != null)
            {
                bool wasActive = interactionTemplate.activeSelf;
                interactionTemplate.SetActive(false);                 // clone asleep, inject, then wake
                GameObject clone = Instantiate(interactionTemplate, transform);
                clone.name = "ISDK_TurntableInteraction";
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                foreach (var h in clone.GetComponentsInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true))
                { h.InjectRigidbody(rb); h.InjectOptionalPointableElement(_grabbable); _interactables.Add(h); }
                foreach (var g in clone.GetComponentsInChildren<Oculus.Interaction.GrabInteractable>(true))
                { g.InjectRigidbody(rb); g.InjectOptionalPointableElement(_grabbable); _interactables.Add(g); }
                interactionTemplate.SetActive(wasActive);
                clone.SetActive(true);
                // The toggle unregistered/re-registered the template's interactables: repair their broadcasters.
                if (interactionTemplate.transform.parent != null)
                    AdaptiveAR.Steps.GrabParity.ReassertBroadcasters(interactionTemplate.transform.parent.gameObject, "turntable template toggled", false);
            }
            else Debug.LogError("[Turntable] no ISDK interaction template: the turntable cannot be grabbed.");
        }

        private void OnPointerEvent(Oculus.Interaction.PointerEvent evt)
        {
            if (evt.Type == Oculus.Interaction.PointerEventType.Select) _wasGrabbed = true;
            else if ((evt.Type == Oculus.Interaction.PointerEventType.Unselect || evt.Type == Oculus.Interaction.PointerEventType.Cancel)
                     && _grabbable != null && _grabbable.SelectingPointsCount == 0 && _wasGrabbed)
            {
                _wasGrabbed = false;
                float yaw = Yaw;
                if (Mathf.Abs(yaw - _lastLoggedYaw) > 0.5f)
                {
                    _lastLoggedYaw = yaw;
                    if (logChanges) Debug.Log($"[Turntable] engine turned to yaw={yaw:F0} deg (viewing only, not an assembly action).");
                    _note?.Invoke($"engine_turned:{yaw:F0}");
                }
            }
        }

        private bool _inspection;

        /// <summary>
        /// Completion: the finished engine becomes ONE inspection object. The ring goes, the
        /// yaw-only rule is lifted, one hand rotates it freely in place (position locked), two
        /// hands scale it within [minScale, maxScale] of its authored size, and every installed
        /// part's grab interactable is pointed at this body so grabbing any part moves the whole
        /// assembly - nothing can separate.
        /// </summary>
        public void EnterInspectionMode(float minScale, float maxScale)
        {
            if (_inspection) return;
            _inspection = true;
            if (_visual != null) _visual.SetActive(false);

            var free = gameObject.AddComponent<Oculus.Interaction.GrabFreeTransformer>();
            var lockPos = new Oculus.Interaction.TransformerUtils.PositionConstraints
            {
                ConstraintsAreRelative = true,
                XAxis = new Oculus.Interaction.TransformerUtils.ConstrainedAxis { ConstrainAxis = true, AxisRange = new Oculus.Interaction.TransformerUtils.FloatRange { Min = 0f, Max = 0f } },
                YAxis = new Oculus.Interaction.TransformerUtils.ConstrainedAxis { ConstrainAxis = true, AxisRange = new Oculus.Interaction.TransformerUtils.FloatRange { Min = 0f, Max = 0f } },
                ZAxis = new Oculus.Interaction.TransformerUtils.ConstrainedAxis { ConstrainAxis = true, AxisRange = new Oculus.Interaction.TransformerUtils.FloatRange { Min = 0f, Max = 0f } }
            };
            var scale = new Oculus.Interaction.TransformerUtils.ScaleConstraints
            {
                ConstraintsAreRelative = true,
                XAxis = new Oculus.Interaction.TransformerUtils.ConstrainedAxis { ConstrainAxis = true, AxisRange = new Oculus.Interaction.TransformerUtils.FloatRange { Min = minScale, Max = maxScale } },
                YAxis = new Oculus.Interaction.TransformerUtils.ConstrainedAxis { ConstrainAxis = true, AxisRange = new Oculus.Interaction.TransformerUtils.FloatRange { Min = minScale, Max = maxScale } },
                ZAxis = new Oculus.Interaction.TransformerUtils.ConstrainedAxis { ConstrainAxis = true, AxisRange = new Oculus.Interaction.TransformerUtils.FloatRange { Min = minScale, Max = maxScale } }
            };
            free.InjectOptionalPositionConstraints(lockPos);
            free.InjectOptionalScaleConstraints(scale);
            free.Initialize(_grabbable);
            _grabbable.InjectOptionalOneGrabTransformer(free);
            _grabbable.InjectOptionalTwoGrabTransformer(free);

            int n = 0;
            foreach (var h in GetComponentsInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true))
            { if (h.transform.IsChildOf(transform)) { h.InjectOptionalPointableElement(_grabbable); h.enabled = true; n++; } }
            foreach (var g in GetComponentsInChildren<Oculus.Interaction.GrabInteractable>(true))
            { if (g.transform.IsChildOf(transform)) { g.InjectOptionalPointableElement(_grabbable); g.enabled = true; n++; } }
            foreach (var padlock in GetComponentsInChildren<AdaptiveAR.Steps.PlacementLock>(true)) padlock.ForwardedToHandle = true;
            foreach (var rb in GetComponentsInChildren<Rigidbody>(true))
                AdaptiveAR.Steps.GrabParity.ReassertBroadcasters(rb.gameObject, "inspection mode", false);

            if (logChanges) Debug.Log($"[Turntable] inspection mode: ring hidden, free rotation in place, two-hand scale {minScale:F1}-{maxScale:F1}x, {n} part grab(s) forwarded to the whole engine.");
        }

        private void LateUpdate()
        {
            if (_inspection) return;
            // Yaw only, whatever the SDK did: keep the authored height/position and no tilt.
            Vector3 lp = transform.localPosition;
            Vector3 euler = transform.rotation.eulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(euler.x, 0f)) > 0.01f || Mathf.Abs(Mathf.DeltaAngle(euler.z, 0f)) > 0.01f)
                transform.rotation = Quaternion.Euler(0f, euler.y, 0f);
            if ((transform.localPosition - lp).sqrMagnitude > 0f) transform.localPosition = lp;
        }

        /// <summary>A part locked onto the engine turns with it from now on (its lock adopts the new parent).</summary>
        public void AdoptInstalled(Transform part)
        {
            if (part == null || part.IsChildOf(transform)) return;
            part.SetParent(transform, true);
            if (logChanges) Debug.Log($"[Turntable] '{part.name}' adopted: it turns with the engine.");
        }

        public void ResetView()
        {
            transform.localRotation = _authoredLocalRotation;
            if (logChanges) Debug.Log("[Turntable] reset to the authored engine orientation.");
            _note?.Invoke("engine_view_reset");
        }

        /// <summary>Completion: the ring and the grab go away; the engine stays where it is.</summary>
        public void SetInteractive(bool on)
        {
            if (_visual != null) _visual.SetActive(on);
            if (_colliderRoot != null) foreach (Collider c in _colliderRoot.GetComponentsInChildren<Collider>(true)) c.enabled = on;
            else if (_collider != null) _collider.enabled = on;
            foreach (MonoBehaviour mb in _interactables) if (mb != null) mb.enabled = on;
        }

        private static Texture2D BuildRingTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = (size - 1) * 0.5f;
            float outer = size * 0.49f, inner = size * 0.40f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c));
                float a = d <= outer && d >= inner ? 1f : (d < inner ? 0.06f : 0f);
                if (a >= 1f) a *= Mathf.Clamp01(Mathf.Min(Mathf.Abs(d - outer), Mathf.Abs(d - inner)) / 1.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();
            return tex;
        }

        private void OnDestroy()
        {
            if (_grabbable != null) _grabbable.WhenPointerEventRaised -= OnPointerEvent;
            if (_material != null) Destroy(_material);
            if (Current == this) Current = null;
        }
    }
}
