// File: AssemblyWorkSurface.cs
// The assembly mat the pistons are built on.
//
// Lifecycle: it does not exist for the participant until a piston stage begins;
// StepManager shows it then, poses the ghost kit on it, and hides it as soon as the
// partial piston (head + rod + pin) has been installed on the crankshaft. Position:
// in FRONT of the engine block, towards the participant, just above the desk - a small
// workbench between them and the engine.
//
// The ghost kit is posed with the piston head lying flat on the mat (crown down, skirt
// up), the rod target above it and the pin target across the view, at the ASSEMBLED
// relative poses copied from the ghost piston in the bore. A subtle ring on the mat
// marks where the head goes; it is always there (it defines the task area) so that L1,
// with no ghost, still knows where the system expects the head.

using System.Collections.Generic;
using AdaptiveAR.Steps;
using UnityEngine;

namespace AdaptiveAR.MR
{
    public class AssemblyWorkSurface : MonoBehaviour
    {
        public enum PlacementMode
        {
            /// <summary>In front of the engine block, towards the participant, just above the desk.</summary>
            InFrontOfEngine = 0,
            /// <summary>Use this object's own transform as authored in the scene.</summary>
            Authored = 1
        }

        [Header("Placement")]
        [SerializeField] private PlacementMode placementMode = PlacementMode.InFrontOfEngine;

        // Field names carry a "mat" prefix on purpose: the scene object you authored still
        // serialises the earlier names (heightAboveDesk 0.1 etc.), and those must not win.
        [Tooltip("Side length of the square mat, metres.")]
        [SerializeField] private float matSize = 0.28f;

        [Tooltip("Height of the mat above the detected desk plane, metres. Millimetres: it only avoids z-fighting.")]
        [SerializeField] private float matHeightAboveDesk = 0.004f;

        [Tooltip("Gap between the engine block's footprint and the near edge of the mat, metres.")]
        [SerializeField] private float matGapFromEngine = 0.02f;

        [Tooltip("Clearance between the mat and the lowest point of the posed kit ghost (the head's crown).")]
        [SerializeField] private float kitClearance = 0.005f;

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

        private GameObject _visual;
        private GameObject _marking;
        private Material _material;
        private Material _markingMaterial;
        private BoxCollider _collider;

        private static readonly string[] KitRoles =
        {
            "PistonHead", "ConnectingRod", "ConnectingPin", "PistonEnd",
            "pistonBolt", "PistonNut", "pistonBoltOther", "PistonNutOther"
        };

        // =====================================================================
        // Placement and visibility
        // =====================================================================

        /// <summary>Poses the mat for the current engine position and desk height. Does not show it.</summary>
        public void Place(Transform engineRoot, float deskY, Transform head)
        {
            EnsureVisual();

            if (placementMode == PlacementMode.InFrontOfEngine)
            {
                Transform block = engineRoot != null ? engineRoot.Find("Offset/Ghosties/oilPan") : null;
                Bounds engine = BoundsOf(block != null ? block : engineRoot, out bool hasEngine);

                Vector3 centre = hasEngine ? engine.center : (engineRoot != null ? engineRoot.position : Vector3.zero);
                Vector3 toUser = ViewDirectionToUser(centre, head);

                // Footprint of the block along the line to the participant.
                float along = hasEngine
                    ? Mathf.Abs(engine.extents.x * toUser.x) + Mathf.Abs(engine.extents.z * toUser.z)
                    : 0.3f;

                Vector3 pos = new Vector3(centre.x, deskY + matHeightAboveDesk, centre.z)
                              + toUser * (along + matGapFromEngine + matSize * 0.5f);

                transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-toUser, Vector3.up));
            }

            IsPlaced = true;

            if (logChanges)
                Debug.Log($"[WorkSurface] Placed ({placementMode}) at {transform.position}.");
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
                      $"pos={transform.position} scale={transform.lossyScale} chain={chain}");
            if (_visual != null && !_visual.activeInHierarchy)
                Debug.LogError("[WorkSurface] visual is NOT active in hierarchy after SHOW - see chain above.");
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
        // Kit ghosts
        // =====================================================================

        public int ArrangeKitGhosts(GuidanceRegistry registry, Transform head)
        {
            if (registry == null || !IsPlaced) return 0;

            int arranged = 0;
            for (int n = 1; n <= 4; n++)
            {
                string kit = $"PistonKit00{n}";
                if (!registry.TryResolveQuiet($"ghost.{kit}.PistonHead", out GameObject headGhost) || headGhost == null) continue;
                Transform kitRoot = headGhost.transform.parent;
                if (kitRoot == null) continue;

                registry.TryResolveQuiet($"ghost.piston00{n}", out GameObject assembled);
                if (assembled == null) continue;

                if (ArrangeKit(kitRoot, assembled.transform)) arranged++;
            }

            if (logChanges)
                Debug.Log($"[WorkSurface] {arranged} ghost kit(s) arranged on the mat.");
            return arranged;
        }

        private bool ArrangeKit(Transform kitRoot, Transform assembled)
        {
            Transform headChild = null, rodChild = null, pinChild = null;
            foreach (string role in KitRoles)
            {
                Transform src = assembled.Find(role);
                Transform dst = kitRoot.Find(role);
                if (src == null || dst == null) continue;

                dst.localPosition = src.localPosition;
                dst.localRotation = src.localRotation;
                dst.localScale = src.localScale;

                if (role == "PistonHead") headChild = dst;
                else if (role == "ConnectingRod") rodChild = dst;
                else if (role == "ConnectingPin") pinChild = dst;
            }
            if (headChild == null || rodChild == null) return false;

            // Head flat on the mat, crown down: the rod is ABOVE the head, attached from above.
            Vector3 towardsRodLocal = (rodChild.localPosition - headChild.localPosition).normalized;
            Quaternion r0 = Quaternion.FromToRotation(towardsRodLocal, Vector3.up);

            if (pinChild != null)
            {
                // The pin goes in from the side: its axis runs across the participant's view.
                Vector3 pinAxisLocal = pinChild.localRotation * Vector3.forward;
                Vector3 pinWorld = r0 * pinAxisLocal;
                pinWorld.y = 0f;

                Vector3 across = transform.right;
                if (pinWorld.sqrMagnitude > 1e-4f)
                {
                    float yaw = Vector3.SignedAngle(pinWorld.normalized, across, Vector3.up);
                    r0 = Quaternion.AngleAxis(yaw, Vector3.up) * r0;
                }
            }

            kitRoot.rotation = r0;
            kitRoot.position = SurfaceCenter;

            // Lift so the HEAD's lowest point (its crown) rests on the mat - the head is what
            // must seat flat; everything else attaches above it - and centre it over the mat.
            float minY = float.MaxValue;
            {
                var mf = headChild.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    Bounds mb = mf.sharedMesh.bounds;
                    Matrix4x4 m = headChild.localToWorldMatrix;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 c = new Vector3((i & 1) == 0 ? mb.min.x : mb.max.x,
                                                (i & 2) == 0 ? mb.min.y : mb.max.y,
                                                (i & 4) == 0 ? mb.min.z : mb.max.z);
                        minY = Mathf.Min(minY, m.MultiplyPoint3x4(c).y);
                    }
                }
            }
            if (minY == float.MaxValue) minY = kitRoot.position.y;

            Vector3 headWorld = headChild.position;
            Vector3 shift = new Vector3(SurfaceCenter.x - headWorld.x,
                                        SurfaceCenter.y + kitClearance - minY,
                                        SurfaceCenter.z - headWorld.z);
            kitRoot.position += shift;

            if (logChanges)
                Debug.Log($"[WorkSurface] kit '{kitRoot.name}' posed: head target {headChild.position}, mat centre {SurfaceCenter}, head crown {minY + shift.y:F3} vs mat {SurfaceCenter.y:F3}.");

            // Marked head area on the mat, sized to the head.
            float headRadius = 0.04f;
            var hmf = headChild.GetComponent<MeshFilter>();
            if (hmf != null && hmf.sharedMesh != null)
            {
                Vector3 e = Vector3.Scale(hmf.sharedMesh.bounds.extents, headChild.lossyScale);
                headRadius = Mathf.Max(Mathf.Abs(e.x), Mathf.Abs(e.z), Mathf.Abs(e.y)) ;
            }
            PlaceMarking(headChild.position, headRadius * 2.6f);
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
            _visual.transform.localScale = new Vector3(matSize, matSize, 1f);
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
            _marking.transform.localScale = new Vector3(0.1f, 0.1f, 1f);

            _collider = gameObject.GetComponent<BoxCollider>();
            if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();
            _collider.size = new Vector3(matSize, slabThickness, matSize);
            _collider.center = new Vector3(0f, -slabThickness * 0.5f, 0f);

            if (!gameObject.name.StartsWith("WorkSurface")) gameObject.name = "WorkSurface";

            _visual.SetActive(false);
            _marking.SetActive(false);
            _collider.enabled = false;
        }

        private void PlaceMarking(Vector3 headWorld, float diameter)
        {
            if (_marking == null) return;
            Vector3 local = transform.InverseTransformPoint(new Vector3(headWorld.x, SurfaceCenter.y + 0.002f, headWorld.z));
            _marking.transform.localPosition = local;
            _marking.transform.localScale = new Vector3(diameter, diameter, 1f);
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
