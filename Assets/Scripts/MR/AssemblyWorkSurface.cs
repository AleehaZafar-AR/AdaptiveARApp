// File: AssemblyWorkSurface.cs
// The virtual work plane the pistons are built on.
//
// Lifecycle: it does not exist for the participant until the first piston stage
// begins; StepManager shows it then, poses the ghost kits on it, and hides it again
// once the last piston is installed. Position: directly above the engine block, a
// little towards the participant, a hand's height above the block's top - reachable,
// in the middle of the work, not off to the side. A scene object carrying this
// component may instead use its own authored transform (placementMode = Authored).
//
// The four ghost kits under Ghosties/PistonKits are posed on the surface in a
// canonical orientation (head up, rod hanging down, pin axis across the view), with
// their children at the ASSEMBLED relative poses copied from the ghost pistons in
// the bores. Every kit is posed at the same spot: the surface is reused per piston.

using System.Collections.Generic;
using AdaptiveAR.Steps;
using UnityEngine;

namespace AdaptiveAR.MR
{
    public class AssemblyWorkSurface : MonoBehaviour
    {
        public enum PlacementMode
        {
            /// <summary>Above the engine block, towards the participant.</summary>
            AboveEngine = 0,
            /// <summary>Use this object's own transform as authored in the scene.</summary>
            Authored = 1
        }

        [Header("Placement")]
        [SerializeField] private PlacementMode placementMode = PlacementMode.AboveEngine;

        [Tooltip("Side length of the square work surface, metres.")]
        [SerializeField] private float size = 0.32f;

        [Tooltip("Height of the surface above the TOP of the engine block, metres.")]
        [SerializeField] private float heightAboveEngine = 0.10f;

        [Tooltip("Shift from the block's centre towards the participant, metres.")]
        [SerializeField] private float towardsUser = 0.08f;

        [Tooltip("Clearance kept above the surface by the lowest point of a posed kit ghost.")]
        [SerializeField] private float kitClearance = 0.015f;

        [Header("Appearance")]
        [SerializeField] private Color gridColor = new Color(0.25f, 0.82f, 0.85f, 0.55f);
        [SerializeField] private Color fillColor = new Color(0.25f, 0.82f, 0.85f, 0.08f);
        [SerializeField] private int gridCells = 8;

        [Header("Collider")]
        [Tooltip("Thickness of the solid slab under the surface, so parts rest on it.")]
        [SerializeField] private float slabThickness = 0.02f;

        [Header("Debug")]
        [SerializeField] private bool logChanges = true;

        public Vector3 SurfaceCenter { get { return transform.position; } }
        public bool IsPlaced { get; private set; }
        public bool IsShown { get { return _visual != null && _visual.activeSelf; } }

        private GameObject _visual;
        private Material _material;
        private BoxCollider _collider;

        private static readonly string[] KitRoles =
        {
            "PistonHead", "ConnectingRod", "ConnectingPin", "PistonEnd",
            "pistonBolt", "PistonNut", "pistonBoltOther", "PistonNutOther"
        };

        // =====================================================================
        // Placement and visibility
        // =====================================================================

        /// <summary>Poses the surface for the current engine position. Does not show it.</summary>
        public void Place(Transform engineRoot, Transform head)
        {
            EnsureVisual();

            if (placementMode == PlacementMode.AboveEngine)
            {
                Transform block = engineRoot != null ? engineRoot.Find("Offset/Ghosties/oilPan") : null;
                Bounds engine = BoundsOf(block != null ? block : engineRoot, out bool hasEngine);

                Vector3 centre = hasEngine ? engine.center : (engineRoot != null ? engineRoot.position : Vector3.zero);
                float top = hasEngine ? engine.max.y : centre.y;

                Vector3 toUser = ViewDirectionToUser(centre, head);
                Vector3 pos = new Vector3(centre.x, top + heightAboveEngine, centre.z) + toUser * towardsUser;

                transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-toUser, Vector3.up));
            }

            IsPlaced = true;

            if (logChanges)
                Debug.Log($"[WorkSurface] Placed ({placementMode}) at {transform.position}.");
        }

        public void Show(bool shown)
        {
            EnsureVisual();
            if (_visual != null && _visual.activeSelf != shown) _visual.SetActive(shown);
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
                Debug.Log($"[WorkSurface] {arranged} ghost kit(s) arranged on the work surface.");
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

            Vector3 downLocal = (rodChild.localPosition - headChild.localPosition).normalized;
            Quaternion r0 = Quaternion.FromToRotation(downLocal, Vector3.down);

            if (pinChild != null)
            {
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
            _visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
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

            if (!gameObject.name.StartsWith("WorkSurface")) gameObject.name = "WorkSurface";

            _visual.SetActive(false);
            _collider.enabled = false;
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

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
