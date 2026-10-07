// File: GuidanceArrow.cs
// A single arrow that tells the operator where to look next.
//
// Two jobs, in order:
//   1. Before the part is picked up, it hovers over the PART, so "which one do I
//      take?" is answered without reading anything.
//   2. Once the part is in hand, it moves to the TARGET, so "where does it go?" is
//      answered the same way.
//
// It follows whichever accepted part the operator actually picked up, which matters
// for interchangeable parts: the arrow tracks the one in their hand.
//
// Orientation
// -----------
// The arrow prefab is a flat shape. Its authored root rotation (set by hand in the
// prefab) points the tip straight down. The runtime keeps that rotation - it never
// re-derives the mesh axis - and only turns the arrow about the vertical so its flat
// face stays towards the viewer. Tip down, directly above the thing it indicates.
//
// Support level decides whether it appears at all - it is guidance, so it belongs
// to the levels that are meant to provide more of it.

using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using UnityEngine;

namespace AdaptiveAR.UI
{
    public class GuidanceArrow : MonoBehaviour
    {
        public enum OrientationMode
        {
            /// <summary>Keep the prefab's authored rotation (tip down); yaw to face the viewer.</summary>
            AuthoredTipDown = 0,

            /// <summary>Legacy: aim a named mesh axis along the arrow-to-target direction.</summary>
            ComputedFromMeshAxis = 1
        }

        [Header("Sources")]
        [SerializeField] private StepValidator validator;
        [SerializeField] private SupportLevelController supportLevel;

        [Tooltip("Supplies the active action. Without this the arrow only appears during a " +
                 "Place action, so it vanished on every Locate step.")]
        [SerializeField] private WorkflowState workflow;

        [Tooltip("Resolves the action part and target keys to scene objects.")]
        [SerializeField] private GuidanceRegistry guidanceRegistry;

        [Header("Arrow")]
        [Tooltip("Prefab instanced once and reused. Assets/Prefabs/arrow.prefab.")]
        [SerializeField] private GameObject arrowPrefab;

        [Tooltip("Metres above the thing being pointed at.")]
        [SerializeField] private float hoverHeight = 0.18f;

        [Tooltip("Size of the spawned arrow, in metres along its longest axis.")]
        [SerializeField] private float arrowSize = 0.12f;

        [Header("Motion")]
        [Tooltip("Bob distance in metres. Movement is what makes it read as a pointer.")]
        [SerializeField] private float bobAmplitude = 0.02f;

        [SerializeField] private float bobSpeed = 2.2f;

        [Tooltip("How quickly the arrow glides between part and target.")]
        [SerializeField] private float followSpeed = 6f;

        [Header("Orientation")]
        [Tooltip("AuthoredTipDown uses the prefab's own rotation, which was set by hand so the " +
                 "tip points down, and only yaws it to face the viewer.")]
        [SerializeField] private OrientationMode orientation = OrientationMode.AuthoredTipDown;

        [Tooltip("Turn the flat arrow about the vertical so its face is towards the head.")]
        [SerializeField] private bool faceViewer = true;

        [Tooltip("Legacy mode only: which local axis the arrow mesh points along.")]
        [SerializeField] private MeshForwardAxis meshForward = MeshForwardAxis.MinusY;

        [Tooltip("Small explicit nudge if a particular mesh needs it. Keep near zero.")]
        [SerializeField] private Vector3 extraRotationEuler = Vector3.zero;

        [Header("Colour")]
        [SerializeField] private bool tintArrow = true;

        [Header("Support levels")]
        [Tooltip("Arrows are assistance, so they appear only from this level upward. " +
                 "L1 is meant to be minimal.")]
        [SerializeField] private SupportLevel minimumLevel = SupportLevel.L2_Guided;

        /// <summary>The axis the arrow art points along in its own local space (legacy mode).</summary>
        public enum MeshForwardAxis
        {
            PlusZ = 0, MinusZ = 1, PlusY = 2, MinusY = 3, PlusX = 4, MinusX = 5
        }

        private GameObject _arrow;
        private Renderer[] _renderers;
        private Transform _meshChild;
        private Vector3 _meshThinAxisLocal = Vector3.forward;
        private Quaternion _authoredRotation = Quaternion.identity;
        private Vector3 _smoothedPos;
        private bool _hasPos;
        private Transform _head;

        private void Start()
        {
            EnsureArrow();
            SetVisible(false);
        }

        private void Update()
        {
            if (supportLevel != null && (int)supportLevel.CurrentLevel < (int)minimumLevel)
            {
                SetVisible(false);
                return;
            }

            // Held already? Point at the destination. Otherwise point at the part.
            bool handled = validator != null && validator.IsActive && validator.HasBeenHandled;
            Transform focus = ResolveFocus(handled);

            if (focus == null)
            {
                SetVisible(false);
                return;
            }

            EnsureArrow();
            if (_arrow == null) return;

            SetVisible(true);

            Vector3 want = focus.position + Vector3.up * hoverHeight;

            if (!_hasPos)
            {
                _smoothedPos = want;
                _hasPos = true;
            }
            else
            {
                _smoothedPos = Vector3.Lerp(_smoothedPos, want, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
            }

            float bob = Mathf.Sin(Time.time * bobSpeed) * bobAmplitude;
            _arrow.transform.position = _smoothedPos + Vector3.up * bob;

            Quaternion rot;
            if (orientation == OrientationMode.AuthoredTipDown)
            {
                // Straight down at what it indicates; the prefab rotation already does that.
                rot = _authoredRotation * Quaternion.Euler(extraRotationEuler);
                _arrow.transform.rotation = rot;

                if (faceViewer)
                    _arrow.transform.rotation = YawToFaceViewer(rot);
            }
            else
            {
                Vector3 direction = focus.position - _arrow.transform.position;
                if (direction.sqrMagnitude < 1e-6f)
                    direction = Vector3.down;   // hovering directly over the target

                if (!TryBuildRotation(direction.normalized, out rot))
                {
                    SetVisible(false);
                    return;
                }

                _arrow.transform.rotation = rot;
            }

            if (tintArrow)
                Tint(handled ? MrTheme.Success : MrTheme.Accent);
        }

        /// <summary>
        /// Rotates about the world vertical so the flat face (the mesh's thin axis) points
        /// at the viewer. Keeps the tip pointing down.
        /// </summary>
        private Quaternion YawToFaceViewer(Quaternion baseRotation)
        {
            if (_head == null && Camera.main != null) _head = Camera.main.transform;
            if (_head == null || _meshChild == null) return baseRotation;

            // Face normal in world, with the base rotation applied.
            Quaternion childWorld = baseRotation * _meshChild.localRotation;
            Vector3 normal = childWorld * _meshThinAxisLocal;
            normal.y = 0f;

            Vector3 toHead = _head.position - _arrow.transform.position;
            toHead.y = 0f;

            if (normal.sqrMagnitude < 1e-4f || toHead.sqrMagnitude < 1e-4f) return baseRotation;

            normal.Normalize();
            toHead.Normalize();

            // Either side of the flat face is fine; take the smaller turn.
            float a = Vector3.SignedAngle(normal, toHead, Vector3.up);
            float b = Vector3.SignedAngle(-normal, toHead, Vector3.up);
            float yaw = Mathf.Abs(a) <= Mathf.Abs(b) ? a : b;

            return Quaternion.AngleAxis(yaw, Vector3.up) * baseRotation;
        }

        /// <summary>
        /// Finds what the arrow should indicate right now.
        ///
        /// The validator is only armed during a Place action, so relying on it alone left
        /// the arrow missing during Locate. The workflow action is the authoritative source:
        /// it names the part on every action, and the target on the ones that have one.
        /// </summary>
        private Transform ResolveFocus(bool handled)
        {
            // Placement in progress: the validator knows which interchangeable part is
            // actually in the hand, which the action alone cannot.
            if (validator != null && validator.IsActive)
            {
                Transform fromValidator = handled ? validator.CurrentTarget : validator.CurrentPart;
                if (fromValidator != null) return fromValidator;
            }

            if (workflow == null || guidanceRegistry == null) return null;

            AssemblyAction action = workflow.CurrentAction;
            if (action == null || !action.enabled || !action.showArrow) return null;

            string key = handled && !string.IsNullOrEmpty(action.targetKey)
                ? action.targetKey
                : action.partKey;

            if (string.IsNullOrEmpty(key)) return null;

            return guidanceRegistry.TryResolveQuiet(key, out GameObject go) ? go.transform : null;
        }

        /// <summary>
        /// Legacy mode: aims the mesh's named axis along a world direction.
        /// </summary>
        private bool TryBuildRotation(Vector3 worldDirection, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (worldDirection.sqrMagnitude < 1e-6f) return false;

            Vector3 up = Mathf.Abs(Vector3.Dot(worldDirection, Vector3.up)) > 0.99f
                ? Vector3.forward
                : Vector3.up;

            Quaternion aimZ = Quaternion.LookRotation(worldDirection, up);

            Quaternion axisFix;
            switch (meshForward)
            {
                case MeshForwardAxis.MinusZ: axisFix = Quaternion.Euler(0f, 180f, 0f); break;
                case MeshForwardAxis.PlusY: axisFix = Quaternion.Euler(90f, 0f, 0f); break;
                case MeshForwardAxis.MinusY: axisFix = Quaternion.Euler(-90f, 0f, 0f); break;
                case MeshForwardAxis.PlusX: axisFix = Quaternion.Euler(0f, -90f, 0f); break;
                case MeshForwardAxis.MinusX: axisFix = Quaternion.Euler(0f, 90f, 0f); break;
                default: axisFix = Quaternion.identity; break;
            }

            rotation = aimZ * axisFix * Quaternion.Euler(extraRotationEuler);
            return true;
        }

        private void EnsureArrow()
        {
            if (_arrow != null || arrowPrefab == null) return;

            _arrow = Instantiate(arrowPrefab);
            _arrow.name = "GuidanceArrow (runtime)";

            // The prefab's root rotation is the authored "tip down" pose. Instantiate keeps
            // it; it is captured here before anything else touches the transform.
            _authoredRotation = _arrow.transform.rotation;

            // Normalise to a predictable size: the source prefab's scale is unknown.
            Bounds b = ComputeBounds(_arrow);
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (longest > 1e-4f)
                _arrow.transform.localScale *= arrowSize / longest;

            // Guidance must never obstruct the physical work.
            foreach (Collider col in _arrow.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            foreach (Rigidbody rb in _arrow.GetComponentsInChildren<Rigidbody>(true))
                rb.isKinematic = true;

            _renderers = _arrow.GetComponentsInChildren<Renderer>(true);

            // The flat face: the mesh's thinnest local axis, scale included.
            var mf = _arrow.GetComponentInChildren<MeshFilter>(true);
            if (mf != null && mf.sharedMesh != null)
            {
                _meshChild = mf.transform;
                Vector3 e = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.localScale);
                if (e.x <= e.y && e.x <= e.z) _meshThinAxisLocal = Vector3.right;
                else if (e.y <= e.x && e.y <= e.z) _meshThinAxisLocal = Vector3.up;
                else _meshThinAxisLocal = Vector3.forward;
            }
        }

        private static Bounds ComputeBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.one * 0.1f);

            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        private void Tint(Color c)
        {
            if (_renderers == null) return;

            foreach (Renderer r in _renderers)
            {
                if (r == null || r.material == null) continue;

                if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
                else if (r.material.HasProperty("_Color")) r.material.SetColor("_Color", c);
            }
        }

        private void SetVisible(bool visible)
        {
            if (_arrow != null && _arrow.activeSelf != visible)
                _arrow.SetActive(visible);

            if (!visible) _hasPos = false;
        }

        private void OnDestroy()
        {
            if (_arrow != null) Destroy(_arrow);
        }
    }
}
