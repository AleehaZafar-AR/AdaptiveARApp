// File: AssemblyWorkSurface.cs
// The assembly mat the pistons are built on.
//
// Lifecycle: it does not exist for the participant until a piston stage begins;
// StepManager shows it then, poses the ghost kit on it, and hides it as soon as the
// partial piston (head + rod + pin) has been installed on the crankshaft. Position:
// right in front of the oil pan, towards the participant, on the desk plane - a small
// working gap, not a separate workstation.
//
// ONE head target
// ---------------
// `HeadTarget` (child "PistonHeadMatTarget") is the single authoritative pose for the
// piston head on the mat: centred on the marked ring, head lying flat (crown down, as
// in the assembled piston with the rod above it), its actual lowest point a few
// millimetres above the mat. It is computed from the HEAD mesh alone, never from kit
// bounds, and it is the same transform for every piston. The ghost kits are posed so
// that each kit's head ghost coincides with it exactly (residual logged), the validator
// measures against it and snaps to it, so: ghost pose == validation pose == snap pose.
// A subtle ring on the mat marks it; the ring is always there (it defines the task
// area) so that L1, with no ghost, still knows where the system expects the head.

using System.Collections.Generic;
using AdaptiveAR.Steps;
using UnityEngine;

namespace AdaptiveAR.MR
{
    public class AssemblyWorkSurface : MonoBehaviour
    {
        public enum PlacementMode
        {
            /// <summary>In front of the engine block, towards the participant, on the desk plane.</summary>
            InFrontOfEngine = 0,
            /// <summary>Use this object's own transform as authored in the scene.</summary>
            Authored = 1
        }

        [Header("Placement")]
        [SerializeField] private PlacementMode placementMode = PlacementMode.InFrontOfEngine;

        // Field names carry a "mat" prefix on purpose: the scene object you authored still
        // serialises the earlier names (heightAboveDesk 0.1 etc.), and those must not win.
        [Tooltip("Side length of the square mat, metres (true metres: parent scale is compensated).")]
        [SerializeField] private float matSize = 0.20f;

        [Tooltip("Height of the mat above the detected desk plane, metres. Millimetres: it only avoids z-fighting.")]
        [SerializeField] private float matHeightAboveDesk = 0.004f;

        [Tooltip("Gap between the oil pan's footprint and the near edge of the mat, metres.")]
        [SerializeField] private float matGapFromEngine = 0.01f;

        [Tooltip("Distance from the mat's engine-side edge to the centre of the head ring, metres.")]
        [SerializeField] private float headRingFromEngineEdge = 0.07f;

        [Tooltip("Clearance between the mat surface and the head's actual lowest point, metres.")]
        [SerializeField] private float kitClearance = 0.003f;

        [Tooltip("Revision of the layout defaults above. When the code's defaults are newer than the " +
                 "values serialized in the scene, the layout fields are reset to the defaults at start.")]
        [SerializeField] private int layoutRevision = 0;
        private const int CurrentLayoutRevision = 1;

        [Header("Appearance")]
        [SerializeField] private Color gridColor = new Color(0.25f, 0.82f, 0.85f, 0.50f);
        [SerializeField] private Color fillColor = new Color(0.25f, 0.82f, 0.85f, 0.07f);
        [SerializeField] private Color markingColor = new Color(0.25f, 0.82f, 0.85f, 0.75f);
        [SerializeField] private int gridCells = 6;

        [Header("Collider")]
        [SerializeField] private float slabThickness = 0.02f;

        [Header("Debug")]
        [SerializeField] private bool logChanges = true;

        public Vector3 SurfaceCenter { get { return transform.position; } }
        public bool IsPlaced { get; private set; }
        public bool IsShown { get { return _visual != null && _visual.activeSelf; } }

        /// <summary>The one authoritative pose for a piston head on the mat. Null until arranged.</summary>
        public Transform HeadTarget { get { return _headTarget; } }
        public bool HeadTargetValid { get; private set; }

        private GameObject _visual;
        private GameObject _marking;
        private Transform _headTarget;
        private Material _material;
        private Material _markingMaterial;
        private BoxCollider _collider;
        private Vector3 _ringWorld;
        private Vector3 _placedForEngine;
        private bool _placedForEngineKnown;

        private static readonly string[] KitRoles =
        {
            "PistonHead", "ConnectingRod", "ConnectingPin", "PistonEnd",
            "pistonBolt", "PistonNut", "pistonBoltOther", "PistonNutOther"
        };

        private void Awake()
        {
            ApplyLayoutRevision();
        }

        private void ApplyLayoutRevision()
        {
            if (layoutRevision >= CurrentLayoutRevision) return;
            matSize = 0.20f;
            matHeightAboveDesk = 0.004f;
            matGapFromEngine = 0.01f;
            headRingFromEngineEdge = 0.07f;
            kitClearance = 0.003f;
            layoutRevision = CurrentLayoutRevision;
            Debug.Log($"[WorkSurface] Layout fields reset to revision {CurrentLayoutRevision} defaults " +
                      $"(size {matSize} m, gap {matGapFromEngine} m, ring {headRingFromEngineEdge} m from the engine edge, clearance {kitClearance} m).");
        }

        // =====================================================================
        // Placement and visibility
        // =====================================================================

        /// <summary>
        /// Poses the mat for the current engine position and desk height. Does not show it.
        /// Once posed, it stays where it is for the session (same mat, same ring, same head
        /// target for every piston) unless the engine itself has moved.
        /// </summary>
        public void Place(Transform engineRoot, float deskY, Transform head)
        {
            ApplyLayoutRevision();
            EnsureVisual();

            Vector3 engineNow = engineRoot != null ? engineRoot.position : Vector3.zero;
            if (IsPlaced && _placedForEngineKnown && (engineNow - _placedForEngine).sqrMagnitude < 1e-6f)
            {
                if (logChanges) Debug.Log($"[WorkSurface] Place: engine unmoved, mat kept at {transform.position}.");
                return;
            }

            if (placementMode == PlacementMode.InFrontOfEngine)
            {
                Transform block = engineRoot != null ? engineRoot.Find("Offset/Ghosties/oilPan") : null;
                Bounds engine = BoundsOf(block != null ? block : engineRoot, out bool hasEngine);

                Vector3 centre = hasEngine ? engine.center : (engineRoot != null ? engineRoot.position : Vector3.zero);
                Vector3 toUser = ViewDirectionToUser(centre, head);

                // Footprint of the oil pan along the line to the participant.
                float along = hasEngine
                    ? Mathf.Abs(engine.extents.x * toUser.x) + Mathf.Abs(engine.extents.z * toUser.z)
                    : 0.3f;

                Vector3 edge = new Vector3(centre.x, deskY + matHeightAboveDesk, centre.z) + toUser * (along + matGapFromEngine);
                Vector3 pos = edge + toUser * (matSize * 0.5f);
                _ringWorld = edge + toUser * headRingFromEngineEdge;

                transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-toUser, Vector3.up));

                if (logChanges)
                    Debug.Log($"[WorkSurface] Placed ({placementMode}) at {transform.position}: oil pan {(block != null ? "bounds" : "ROOT bounds (oilPan ghost not found)")} " +
                              $"centre={centre} halfDepth={along:F3} gap={matGapFromEngine:F3} size={matSize:F2}; ring {headRingFromEngineEdge:F3} m from the engine edge at {_ringWorld}.");
            }
            else
            {
                _ringWorld = transform.position + transform.forward * (matSize * 0.5f - headRingFromEngineEdge);
                if (logChanges) Debug.Log($"[WorkSurface] Placed ({placementMode}) at {transform.position}.");
            }

            ApplyVisualScale();
            IsPlaced = true;
            _placedForEngine = engineNow;
            _placedForEngineKnown = true;
            HeadTargetValid = false;   // the target is re-derived for the new pose by ArrangeKitGhosts
        }

        /// <summary>Diagnostic: hierarchy and renderer state, so a silent SHOW is explainable.</summary>
        public void ReportState(string where)
        {
            var r = _visual != null ? _visual.GetComponent<MeshRenderer>() : null;
            string chain = "";
            for (Transform t = transform; t != null; t = t.parent)
                chain = t.name + (t.gameObject.activeSelf ? "" : "[INACTIVE]") + "/" + chain;
            Debug.Log($"[WorkSurface] {where}: activeInHierarchy={gameObject.activeInHierarchy} visual={(_visual != null ? _visual.activeSelf.ToString() : "none")} " +
                      $"renderer={(r != null ? r.enabled.ToString() : "none")} collider={(_collider != null ? _collider.enabled.ToString() : "none")} " +
                      $"pos={transform.position} lossyScale={transform.lossyScale} headTarget={(HeadTargetValid && _headTarget != null ? _headTarget.position.ToString() : "INVALID")} chain={chain}");
            if (_visual != null && !_visual.activeInHierarchy)
                Debug.LogError("[WorkSurface] visual is NOT active in hierarchy after SHOW - see chain above.");
            if (!HeadTargetValid)
                Debug.LogError("[WorkSurface] head target is not valid after SHOW: ArrangeKitGhosts did not derive it (see errors above).");
        }

        public void Show(bool shown)
        {
            if (shown && !gameObject.activeSelf) gameObject.SetActive(true);
            EnsureVisual();
            if (_visual != null && _visual.activeSelf != shown) _visual.SetActive(shown);
            if (_marking != null && _marking.activeSelf != shown) _marking.SetActive(shown);
            if (_collider != null) _collider.enabled = shown;

            if (logChanges) Debug.Log("[WorkSurface] " + (shown ? "shown" : "hidden"));
        }

        private static Vector3 ViewDirectionToUser(Vector3 from, Transform head)
        {
            Vector3 toUser = Vector3.back;
            if (head != null)
            {
                toUser = head.position - from;
                toUser.y = 0f;
                if (toUser.sqrMagnitude < 1e-4f) toUser = Vector3.ProjectOnPlane(-head.forward, Vector3.up);
            }
            if (toUser.sqrMagnitude < 1e-4f) toUser = Vector3.back;
            return toUser.normalized;
        }

        private static Bounds BoundsOf(Transform root, out bool any)
        {
            any = false;
            var b = new Bounds();
            if (root == null) return b;

            bool rootIsBlock = root.name == "oilPan";
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null) continue;
                if (!rootIsBlock && IsUnder(r.transform, "Ghosties")) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        private static bool IsUnder(Transform t, string ancestorName)
        {
            while (t != null)
            {
                if (t.name == ancestorName) return true;
                t = t.parent;
            }
            return false;
        }

        // =====================================================================
        // Head target and kit ghosts
        // =====================================================================

        /// <summary>
        /// Derives the ONE head target from the head mesh and the assembled piston's
        /// head/rod/pin relation, then poses every ghost kit so its head ghost coincides
        /// with that target. Returns the number of kits arranged.
        /// </summary>
        public int ArrangeKitGhosts(GuidanceRegistry registry, Transform head)
        {
            if (registry == null || !IsPlaced) return 0;

            // The relation head->rod->pin is the same in every assembled piston; use the
            // first one that resolves to derive the flat orientation.
            Transform refKitRoot = null, refAssembled = null;
            for (int n = 1; n <= 4 && refKitRoot == null; n++)
            {
                if (!registry.TryResolveQuiet($"ghost.PistonKit00{n}.PistonHead", out GameObject hg) || hg == null) continue;
                if (!registry.TryResolveQuiet($"ghost.piston00{n}", out GameObject asm) || asm == null) continue;
                refKitRoot = hg.transform.parent;
                refAssembled = asm.transform;
            }

            if (refKitRoot == null || !DeriveHeadTarget(refKitRoot, refAssembled))
            {
                Debug.LogError("[WorkSurface] head target could NOT be derived: no ghost kit / assembled piston ghost resolved.");
                return 0;
            }

            int arranged = 0;
            for (int n = 1; n <= 4; n++)
            {
                if (!registry.TryResolveQuiet($"ghost.PistonKit00{n}.PistonHead", out GameObject headGhost) || headGhost == null) continue;
                Transform kitRoot = headGhost.transform.parent;
                if (kitRoot == null) continue;

                registry.TryResolveQuiet($"ghost.piston00{n}", out GameObject assembled);
                if (assembled == null) continue;

                if (ArrangeKit(kitRoot, assembled.transform, n)) arranged++;
            }

            if (logChanges)
                Debug.Log($"[WorkSurface] {arranged} ghost kit(s) arranged on the head target.");
            return arranged;
        }

        /// <summary>
        /// Head flat on the mat, crown down, centred on the ring, actual lowest point
        /// kitClearance above the mat. Orientation: in the assembled piston the rod hangs
        /// below the head, so "rod direction up" lays the head crown-down; the pin axis is
        /// then turned to run across the participant's view.
        /// </summary>
        private bool DeriveHeadTarget(Transform kitRoot, Transform assembled)
        {
            Transform headSrc = assembled != null ? assembled.Find("PistonHead") : null;
            Transform rodSrc = assembled != null ? assembled.Find("ConnectingRod") : null;
            Transform pinSrc = assembled != null ? assembled.Find("ConnectingPin") : null;
            Transform headGhost = kitRoot.Find("PistonHead");
            if (headSrc == null || rodSrc == null || headGhost == null)
            {
                Debug.LogError($"[WorkSurface] head target: assembled piston ghost lacks PistonHead/ConnectingRod children ({(assembled != null ? assembled.name : "<null>")}).");
                return false;
            }

            // Kit-root orientation that lays the head flat (as before), expressed in world space.
            Vector3 towardsRodLocal = (rodSrc.localPosition - headSrc.localPosition).normalized;
            Quaternion r0 = Quaternion.FromToRotation(towardsRodLocal, Vector3.up);
            if (pinSrc != null)
            {
                Vector3 pinAxisLocal = pinSrc.localRotation * Vector3.forward;
                Vector3 pinWorld = r0 * pinAxisLocal;
                pinWorld.y = 0f;
                if (pinWorld.sqrMagnitude > 1e-4f)
                {
                    float yaw = Vector3.SignedAngle(pinWorld.normalized, transform.right, Vector3.up);
                    r0 = Quaternion.AngleAxis(yaw, Vector3.up) * r0;
                }
            }

            // The head's own world rotation at that kit orientation, and its world scale.
            Quaternion headRot = r0 * headSrc.localRotation;
            Vector3 headScale = Vector3.Scale(headGhost.localScale, kitRoot.lossyScale);

            var mf = headGhost.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null)
            {
                Debug.LogError("[WorkSurface] head target: the head ghost has no mesh.");
                return false;
            }

            // Lowest point and footprint centre of the HEAD mesh alone, at the target rotation,
            // from real vertices when the mesh is readable (bounds corners otherwise).
            Matrix4x4 m = Matrix4x4.TRS(Vector3.zero, headRot, headScale);
            float minY = float.MaxValue;
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            string source;
            Vector3[] verts = mesh.isReadable ? mesh.vertices : null;
            if (verts != null && verts.Length > 0)
            {
                source = $"{verts.Length} vertices";
                foreach (Vector3 v in verts)
                {
                    Vector3 w = m.MultiplyPoint3x4(v);
                    minY = Mathf.Min(minY, w.y);
                    lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w);
                }
            }
            else
            {
                source = "bounds corners (mesh not readable)";
                Bounds mb = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = new Vector3((i & 1) == 0 ? mb.min.x : mb.max.x,
                                            (i & 2) == 0 ? mb.min.y : mb.max.y,
                                            (i & 4) == 0 ? mb.min.z : mb.max.z);
                    Vector3 w = m.MultiplyPoint3x4(c);
                    minY = Mathf.Min(minY, w.y);
                    lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w);
                }
            }

            // Pivot placement: geometry centre over the ring, lowest point on the mat + clearance.
            Vector3 centreOffset = (lo + hi) * 0.5f;                   // pivot -> geometry centre, world axes
            Vector3 pivot = new Vector3(_ringWorld.x - centreOffset.x,
                                        SurfaceCenter.y + kitClearance - minY,
                                        _ringWorld.z - centreOffset.z);

            EnsureHeadTarget();
            _headTarget.SetPositionAndRotation(pivot, headRot);
            HeadTargetValid = true;

            float footprint = Mathf.Max(hi.x - lo.x, hi.z - lo.z);
            PlaceMarking(_ringWorld, footprint * 1.3f);

            if (logChanges)
                Debug.Log($"[WorkSurface] head target derived from {source}: pos={pivot} rot={headRot.eulerAngles} " +
                          $"lowest point {SurfaceCenter.y + kitClearance:F4} vs mat {SurfaceCenter.y:F4} (clearance {kitClearance * 1000f:F0} mm), " +
                          $"footprint {footprint * 100f:F1} cm, ring at {_ringWorld}, headScale={headScale}.");
            return true;
        }

        private void EnsureHeadTarget()
        {
            if (_headTarget != null) return;
            Transform existing = transform.Find("PistonHeadMatTarget");
            if (existing == null)
            {
                var go = new GameObject("PistonHeadMatTarget");
                go.transform.SetParent(transform, false);
                existing = go.transform;
            }
            _headTarget = existing;
        }

        /// <summary>
        /// Copies the assembled relative poses into the kit, then poses the kit root so its
        /// HEAD child coincides exactly with the head target (rod and pin follow at their
        /// assembled offsets). The residual is logged; anything over a millimetre / a degree
        /// is an error.
        /// </summary>
        private bool ArrangeKit(Transform kitRoot, Transform assembled, int kitNumber)
        {
            Transform headChild = null;
            foreach (string role in KitRoles)
            {
                Transform src = assembled.Find(role);
                Transform dst = kitRoot.Find(role);
                if (src == null || dst == null) continue;

                dst.localPosition = src.localPosition;
                dst.localRotation = src.localRotation;
                dst.localScale = src.localScale;

                if (role == "PistonHead") headChild = dst;
            }
            if (headChild == null || !HeadTargetValid) return false;

            // Direct child: world = root * local.
            kitRoot.rotation = _headTarget.rotation * Quaternion.Inverse(headChild.localRotation);
            kitRoot.position += _headTarget.position - headChild.position;

            float dPos = Vector3.Distance(headChild.position, _headTarget.position);
            float dRot = Quaternion.Angle(headChild.rotation, _headTarget.rotation);
            if (logChanges)
                Debug.Log($"[WorkSurface] kit {kitNumber} '{kitRoot.name}' posed on the head target: head ghost {headChild.name}#{headChild.GetInstanceID()} " +
                          $"pos={headChild.position} vs target {_headTarget.position} residual {dPos * 1000f:F2} mm / {dRot:F2} deg.");
            if (dPos > 0.001f || dRot > 1f)
                Debug.LogError($"[WorkSurface] kit {kitNumber}: head ghost does NOT coincide with the head target (residual {dPos * 1000f:F1} mm / {dRot:F1} deg).");
            return true;
        }

        // =====================================================================
        // Visual, marking, collider
        // =====================================================================

        private void EnsureVisual()
        {
            if (_visual != null) return;

            _visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _visual.name = "WorkSurfaceVisual";
            _visual.transform.SetParent(transform, false);
            _visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Collider quadCol = _visual.GetComponent<Collider>();
            if (quadCol != null) Destroy(quadCol);

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            _material = new Material(shader);
            _material.mainTexture = BuildGridTexture(256, gridCells);
            _material.color = Color.white;

            var r = _visual.GetComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            _marking = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _marking.name = "HeadMarking";
            _marking.transform.SetParent(transform, false);
            _marking.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Collider mc = _marking.GetComponent<Collider>();
            if (mc != null) Destroy(mc);
            _markingMaterial = new Material(shader);
            _markingMaterial.mainTexture = BuildRingTexture(128);
            _markingMaterial.color = markingColor;
            var mr = _marking.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _markingMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            _collider = gameObject.GetComponent<BoxCollider>();
            if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();

            ApplyVisualScale();
            EnsureHeadTarget();

            if (!gameObject.name.StartsWith("WorkSurface")) gameObject.name = "WorkSurface";

            _visual.SetActive(false);
            _marking.SetActive(false);
            _collider.enabled = false;
        }

        /// <summary>Scale factor that turns metres into this object's local units (it may sit under a scaled anchor).</summary>
        private float LocalPerMetre()
        {
            float s = transform.lossyScale.x;
            return s > 1e-5f ? 1f / s : 1f;
        }

        private void ApplyVisualScale()
        {
            float k = LocalPerMetre();
            if (_visual != null) _visual.transform.localScale = new Vector3(matSize * k, matSize * k, 1f);
            if (_collider != null)
            {
                _collider.size = new Vector3(matSize * k, slabThickness * k, matSize * k);
                _collider.center = new Vector3(0f, -slabThickness * 0.5f * k, 0f);
            }
        }

        private void PlaceMarking(Vector3 ringWorld, float diameterMetres)
        {
            if (_marking == null) return;
            float k = LocalPerMetre();
            Vector3 local = transform.InverseTransformPoint(new Vector3(ringWorld.x, SurfaceCenter.y + 0.002f, ringWorld.z));
            _marking.transform.localPosition = local;
            _marking.transform.localScale = new Vector3(diameterMetres * k, diameterMetres * k, 1f);
        }

        private Texture2D BuildGridTexture(int px, int cells)
        {
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            float cell = (float)px / Mathf.Max(1, cells);
            float line = Mathf.Max(1.5f, px / 128f);
            float edge = px * 0.5f;

            for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                float dx = Mathf.Min(x % cell, cell - x % cell);
                float dy = Mathf.Min(y % cell, cell - y % cell);
                bool onLine = dx < line || dy < line || x < line || y < line || x > px - 1 - line || y > px - 1 - line;

                float fx = 1f - Mathf.Abs(x - edge) / edge;
                float fy = 1f - Mathf.Abs(y - edge) / edge;
                float fade = Mathf.Clamp01(Mathf.Min(fx, fy) * 3f);

                Color c = onLine ? gridColor : fillColor;
                c.a *= Mathf.Lerp(0.5f, 1f, fade);
                tex.SetPixel(x, y, c);
            }

            tex.Apply();
            return tex;
        }

        private static Texture2D BuildRingTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float c = (size - 1) * 0.5f;
            float outer = size * 0.48f, inner = size * 0.42f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c));
                float a = d <= outer && d >= inner ? 1f : (d < inner ? 0.12f : 0f);
                if (a >= 1f)
                {
                    float edge = Mathf.Min(Mathf.Abs(d - outer), Mathf.Abs(d - inner));
                    a *= Mathf.Clamp01(edge / 1.5f);
                }
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }

            tex.Apply();
            return tex;
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_markingMaterial != null) Destroy(_markingMaterial);
        }
    }
}
