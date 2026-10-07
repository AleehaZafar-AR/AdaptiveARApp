// File: AssemblyWorkSurface.cs
// A virtual work plane for building pistons, created once the physical desk is known.
//
// Why
// ---
// Building a piston in free space was awkward: the first ghost floated somewhere
// behind the engine and nothing said "assemble here". This surface is that place:
// a faded grid a hand-height above the desk, in front of the participant and beside
// the engine, with a thin solid collider so a dropped part rests on it.
//
// It also owns the geometry of the piston build: the four ghost kits under
// Ghosties/PistonKits are re-posed onto this surface in a canonical orientation
// (head up, rod hanging down, pin axis across the participant's view), with their
// children at the ASSEMBLED relative poses copied from the ghost pistons in the bores.
// Every kit is posed at the same spot: the surface is reused piston after piston.
//
// Nothing here is created in the Editor; it is built at runtime by StepManager after
// the workspace is placed, and re-posed if the workspace is repositioned.

using System.Collections.Generic;
using AdaptiveAR.Steps;
using UnityEngine;

namespace AdaptiveAR.MR
{
    public class AssemblyWorkSurface : MonoBehaviour
    {
        [Header("Size and placement, metres")]
        [Tooltip("Side length of the square work surface.")]
        [SerializeField] private float size = 0.32f;

        [Tooltip("Height of the surface above the physical desk. About a hand height.")]
        [SerializeField] private float heightAboveDesk = 0.10f;

        [Tooltip("Gap kept between the surface and the engine's footprint.")]
        [SerializeField] private float clearanceFromEngine = 0.10f;

        [Tooltip("Clearance kept above the surface by the lowest point of a posed kit ghost.")]
        [SerializeField] private float kitClearance = 0.015f;

        [Header("Appearance")]
        [SerializeField] private Color gridColor = new Color(0.25f, 0.82f, 0.85f, 0.55f);
        [SerializeField] private Color fillColor = new Color(0.25f, 0.82f, 0.85f, 0.08f);
        [Tooltip("Grid cells per side.")]
        [SerializeField] private int gridCells = 8;

        [Header("Collider")]
        [Tooltip("Thickness of the solid slab under the surface, so parts rest on it.")]
        [SerializeField] private float slabThickness = 0.02f;

        [Header("Debug")]
        [SerializeField] private bool logChanges = true;

        /// <summary>Centre of the top face, world space.</summary>
        public Vector3 SurfaceCenter { get { return transform.position; } }

        public bool IsPlaced { get; private set; }

        private GameObject _visual;
        private Material _material;
        private BoxCollider _collider;

        // Roles in the order their ghosts are copied; only names that exist are used.
        private static readonly string[] KitRoles =
        {
            "PistonHead", "ConnectingRod", "ConnectingPin", "PistonEnd",
            "pistonBolt", "PistonNut", "pistonBoltOther", "PistonNutOther"
        };

        // =====================================================================
        // Placement
        // =====================================================================

        /// <summary>
        /// Puts the surface beside the engine, in front of the participant, a hand height
        /// above the desk. Avoids overlapping the engine and the trays in plan view.
        /// </summary>
        public void Place(Transform engineRoot, float deskY, Transform head)
        {
            EnsureVisual();

            // "Near the engine" means the block, not the trays a metre away from it.
            Transform block = engineRoot != null ? engineRoot.Find("Offset/Ghosties/oilPan") : null;
            Bounds engine = BoundsOf(block != null ? block : engineRoot, includeInactive: false, out bool hasEngine);
            Vector3 engineCentre = hasEngine ? engine.center : (engineRoot != null ? engineRoot.position : Vector3.zero);
            float engineRadius = hasEngine ? Mathf.Max(engine.extents.x, engine.extents.z) : 0.3f;

            Vector3 toUser = Vector3.forward;
            if (head != null)
            {
                toUser = head.position - engineCentre;
                toUser.y = 0f;
                if (toUser.sqrMagnitude < 1e-4f) toUser = Vector3.ProjectOnPlane(-head.forward, Vector3.up);
                if (toUser.sqrMagnitude < 1e-4f) toUser = Vector3.forward;
                toUser.Normalize();
            }
            Vector3 right = Vector3.Cross(Vector3.up, toUser);

            float reach = engineRadius + clearanceFromEngine + size * 0.5f;

            // Candidates: towards the user, then user-right, then user-left, then further out.
            var candidates = new List<Vector3>
            {
                engineCentre + toUser * reach,
                engineCentre + (toUser * 0.6f + right * 0.8f).normalized * reach,
                engineCentre + (toUser * 0.6f - right * 0.8f).normalized * reach,
                engineCentre + right * reach,
                engineCentre - right * reach,
                engineCentre + toUser * (reach + 0.2f)
            };

            List<Bounds> obstacles = ObstacleBounds(engineRoot);
            Vector3 chosen = candidates[0];
            foreach (Vector3 c in candidates)
            {
                if (!OverlapsAny(c, obstacles)) { chosen = c; break; }
            }

            chosen.y = deskY + heightAboveDesk;

            Quaternion yaw = Quaternion.LookRotation(-toUser, Vector3.up);
            transform.SetPositionAndRotation(chosen, yaw);

            IsPlaced = true;

            if (logChanges)
                Debug.Log($"[WorkSurface] Placed at {chosen} ({size * 100f:F0} cm, {heightAboveDesk * 100f:F0} cm above the desk).");
        }

        private bool OverlapsAny(Vector3 centre, List<Bounds> obstacles)
        {
            float half = size * 0.5f + 0.03f;
            foreach (Bounds b in obstacles)
            {
                bool x = centre.x + half > b.min.x && centre.x - half < b.max.x;
                bool z = centre.z + half > b.min.z && centre.z - half < b.max.z;
                if (x && z) return true;
            }
            return false;
        }

        private static List<Bounds> ObstacleBounds(Transform engineRoot)
        {
            var list = new List<Bounds>();
            if (engineRoot == null) return list;

            foreach (Renderer r in engineRoot.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled) continue;
                // Ghost targets are not obstacles; the trays, the parts and the engine block are.
                bool ghost = IsUnder(r.transform, "Ghosties") && !IsUnder(r.transform, "oilPan");
                if (ghost) continue;
                list.Add(r.bounds);
            }
            return list;
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

        private static Bounds BoundsOf(Transform root, bool includeInactive, out bool any)
        {
            any = false;
            var b = new Bounds();
            if (root == null) return b;

            bool rootIsGhostBlock = root.name == "oilPan";
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(includeInactive))
            {
                if (r == null) continue;
                if (!rootIsGhostBlock && IsUnder(r.transform, "Ghosties")) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        // =====================================================================
        // Kit ghosts
        // =====================================================================

        /// <summary>
        /// Poses every ghost kit (Ghosties/PistonKits/PistonKit00N) on this surface with its
        /// children at assembled relative poses, so the head target sits on the plane and
        /// each later target is relative to the locked head.
        /// </summary>
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

                if (ArrangeKit(kitRoot, assembled.transform, head)) arranged++;
            }

            if (logChanges)
                Debug.Log($"[WorkSurface] {arranged} ghost kit(s) arranged on the work surface.");
            return arranged;
        }

        private bool ArrangeKit(Transform kitRoot, Transform assembled, Transform head)
        {
            // 1. Children take the assembled relative poses of the bore ghost.
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

            // 2. Orientation: rod hangs below the head; pin axis runs across the view.
            Vector3 downLocal = (rodChild.localPosition - headChild.localPosition).normalized;
            Quaternion r0 = Quaternion.FromToRotation(downLocal, Vector3.down);

            if (pinChild != null)
            {
                Vector3 pinAxisLocal = pinChild.localRotation * Vector3.forward;   // the pin mesh is long along its local Z
                Vector3 pinWorld = r0 * pinAxisLocal;
                pinWorld.y = 0f;

                Vector3 across = transform.right;   // surface faces the user: right is across the view
                if (pinWorld.sqrMagnitude > 1e-4f)
                {
                    float yaw = Vector3.SignedAngle(pinWorld.normalized, across, Vector3.up);
                    r0 = Quaternion.AngleAxis(yaw, Vector3.up) * r0;
                }
            }

            // Scale lives on the Ghosties ancestor; only rotation and position are set here.
            kitRoot.rotation = r0;
            kitRoot.position = SurfaceCenter;

            // 3. Lift so the lowest point of the assembly clears the surface, and centre the
            //    head over the plane.
            float minY = float.MaxValue;
            foreach (Transform child in kitRoot)
            {
                var mf = child.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Bounds mb = mf.sharedMesh.bounds;
                Matrix4x4 m = child.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = new Vector3((i & 1) == 0 ? mb.min.x : mb.max.x,
                                            (i & 2) == 0 ? mb.min.y : mb.max.y,
                                            (i & 4) == 0 ? mb.min.z : mb.max.z);
                    minY = Mathf.Min(minY, m.MultiplyPoint3x4(c).y);
                }
            }
            if (minY == float.MaxValue) minY = kitRoot.position.y;

            Vector3 headWorld = headChild.position;
            Vector3 shift = new Vector3(SurfaceCenter.x - headWorld.x,
                                        SurfaceCenter.y + kitClearance - minY,
                                        SurfaceCenter.z - headWorld.z);
            kitRoot.position += shift;
            return true;
        }

        // =====================================================================
        // Visual + collider
        // =====================================================================

        private void EnsureVisual()
        {
            if (_visual != null) return;

            _visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _visual.name = "WorkSurfaceVisual";
            _visual.transform.SetParent(transform, false);
            _visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // lie flat, face up
            _visual.transform.localScale = new Vector3(size, size, 1f);

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

            _collider = gameObject.GetComponent<BoxCollider>();
            if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();
            _collider.size = new Vector3(size, slabThickness, size);
            _collider.center = new Vector3(0f, -slabThickness * 0.5f, 0f);

            gameObject.name = "WorkSurface";
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

                // Fade towards the edges so the plane reads as a soft zone, not a slab.
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

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
