// File: InstalledPistonPivot.cs
// Once a piston (head + rod + pin) is installed on the crankshaft, the participant can
// turn it about its crank-journal axis - ONE degree of freedom - to reach the end cap,
// bolts and nuts. Nothing translates, tilts or scales, and no joined part is draggable
// on its own.
//
//   Turntable
//     PistonPivot_<kit>    <- this object: pivot at the journal (the cap's seat), up = crank axis
//        <head>            <- the installed subassembly, locked relative to the pivot
//        ghost.piston00N   <- the cap / bolt / nut targets, so validation turns with it
//
// Grabbing: the head's and its joined children's grab interactables are pointed at this
// pivot's Grabbable, driven by a OneGrabRotateTransformer about the pivot's up axis.
// LockPermanently() (when the piston's stage ends, and at completion) switches all of
// that off again.

using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class InstalledPistonPivot : MonoBehaviour
    {
        private static readonly List<InstalledPistonPivot> _all = new List<InstalledPistonPivot>();

        public string Kit { get; private set; }
        public bool Locked { get; private set; }
        public Transform GhostGroup { get; private set; }

        [Tooltip("Seconds for the smooth return to the authored crankshaft orientation once the piston is complete.")]
        public float returnSeconds = 0.4f;

        [Tooltip("Largest swing allowed either way from the authored orientation, degrees (upper bound; the oil pan may reduce it).")]
        public float maxSwingDegrees = 75f;
        [Tooltip("Smallest swing always allowed either way, degrees, so the cap / bolts / nuts stay reachable.")]
        public float minSwingDegrees = 25f;

        public float LimitNegative { get; private set; }
        public float LimitPositive { get; private set; }

        private Quaternion _authoredLocalRotation;
        private bool _returning;

        private Oculus.Interaction.Grabbable _grabbable;
        private readonly List<MonoBehaviour> _forwarded = new List<MonoBehaviour>();

        public static InstalledPistonPivot Create(string kit, Transform head, Transform ghostGroup, Vector3 pivotPoint, Vector3 axisWorld)
        {
            if (head == null) return null;
            foreach (InstalledPistonPivot p in _all) if (p != null && p.Kit == kit) return p;

            Transform parent = head.parent;                       // the turntable (or wherever the head was adopted)
            var go = new GameObject("PistonPivot_" + kit);
            go.transform.SetParent(parent, false);
            Vector3 up = axisWorld.sqrMagnitude > 1e-6f ? axisWorld.normalized : Vector3.right;
            Vector3 fwd = Vector3.Cross(up, Vector3.up); if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.Cross(up, Vector3.forward);
            go.transform.SetPositionAndRotation(pivotPoint, Quaternion.LookRotation(fwd.normalized, up));

            var pivot = go.AddComponent<InstalledPistonPivot>();
            pivot.Kit = kit;
            pivot.GhostGroup = ghostGroup;
            pivot._authoredLocalRotation = go.transform.localRotation;     // the authored crankshaft orientation

            var rb = go.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            pivot._grabbable = go.AddComponent<Oculus.Interaction.Grabbable>();
            var rot = go.AddComponent<Oculus.Interaction.OneGrabRotateTransformer>();
            rot.InjectOptionalPivotTransform(go.transform);
            rot.InjectOptionalRotationAxis(Oculus.Interaction.OneGrabRotateTransformer.Axis.Up);
            pivot.ComputeLimits(head, pivotPoint, up);
            rot.Constraints = new Oculus.Interaction.OneGrabRotateTransformer.OneGrabRotateConstraints
            {
                MinAngle = new Oculus.Interaction.FloatConstraint { Constrain = true, Value = -pivot.LimitNegative },
                MaxAngle = new Oculus.Interaction.FloatConstraint { Constrain = true, Value = pivot.LimitPositive }
            };
            pivot._grabbable.InjectOptionalOneGrabTransformer(rot);

            PartWatch.Reparent("PistonPivot.Create(head)", head, go.transform, true, kit);
            var headLock = head.GetComponent<PlacementLock>();
            if (headLock != null) { headLock.RefreshLockedPose(); headLock.ForwardedToHandle = true; }
            if (ghostGroup != null) ghostGroup.SetParent(go.transform, true);

            pivot.ForwardSubassemblyGrabs(head);
            _all.Add(pivot);
            Debug.Log($"[PistonPivot] {kit}: created at {pivotPoint} axis={up} ({pivot._forwarded.Count} grab interactable(s) turn the installed piston about the journal axis; targets {(ghostGroup != null ? ghostGroup.name : "-")} follow).");
            return pivot;
        }

        /// <summary>
        /// Swing limits: 0 = authored orientation. Each way, the piston (its renderers' bounds
        /// corners, rotated about the crank axis through the pivot) must stay above the oil pan's
        /// top plane; the first angle that dips below it ends the range. Clamped to
        /// [minSwingDegrees, maxSwingDegrees]; symmetric default when no oil pan is found.
        /// </summary>
        private void ComputeLimits(Transform head, Vector3 pivotPoint, Vector3 axisWorld)
        {
            LimitNegative = LimitPositive = Mathf.Clamp(60f, minSwingDegrees, maxSwingDegrees);
            Renderer pan = null;
            foreach (Transform t in head.root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.IndexOf("oilpan", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.GetComponentInParent<InstalledPistonPivot>() != null) continue;
                var r = t.GetComponentInChildren<Renderer>(true);
                if (r != null && r.gameObject.activeInHierarchy) { pan = r; break; }
            }
            if (pan == null)
            {
                Debug.LogWarning($"[PistonPivot] {Kit}: no oil pan renderer found - symmetric swing limit ±{LimitPositive:F0} deg.");
                return;
            }
            float panTop = pan.bounds.max.y;
            var corners = new List<Vector3>();
            foreach (Renderer r in head.GetComponentsInChildren<Renderer>(true))
            {
                Bounds b = r.bounds;
                for (int i = 0; i < 8; i++)
                    corners.Add(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
            }
            float Limit(float sign)
            {
                float ok = 0f;
                for (float a = 5f; a <= maxSwingDegrees; a += 5f)
                {
                    Quaternion q = Quaternion.AngleAxis(sign * a, axisWorld);
                    float minY = float.MaxValue;
                    foreach (Vector3 c in corners) minY = Mathf.Min(minY, (q * (c - pivotPoint) + pivotPoint).y);
                    if (minY < panTop - 0.005f) break;
                    ok = a;
                }
                return Mathf.Clamp(ok, minSwingDegrees, maxSwingDegrees);
            }
            LimitPositive = Limit(+1f);
            LimitNegative = Limit(-1f);
            Debug.Log($"[PistonPivot] {Kit}: swing limits min=-{LimitNegative:F0} max=+{LimitPositive:F0} deg about the crank axis (0 = authored; oil pan top y={panTop:F3}, {corners.Count / 8} renderer(s) checked).");
        }

        /// <summary>Every grab on the subassembly (head, rod, pin - and parts joined later) turns the pivot.</summary>
        public void ForwardSubassemblyGrabs(Transform root)
        {
            if (Locked || _grabbable == null || root == null) return;
            PartWatch.Log("PistonPivot.ForwardSubassemblyGrabs", root, Kit);
            foreach (var h in root.GetComponentsInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true))
            { h.InjectOptionalPointableElement(_grabbable); h.enabled = true; if (!_forwarded.Contains(h)) _forwarded.Add(h); }
            foreach (var g in root.GetComponentsInChildren<Oculus.Interaction.GrabInteractable>(true))
            { g.InjectOptionalPointableElement(_grabbable); g.enabled = true; if (!_forwarded.Contains(g)) _forwarded.Add(g); }
            foreach (var padlock in root.GetComponentsInChildren<PlacementLock>(true)) padlock.ForwardedToHandle = true;
            GrabParity.ReassertBroadcasters(root.gameObject, "piston pivot forwarding", false);
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && t.GetComponent<Rigidbody>() != null) GrabParity.ReassertBroadcasters(t.gameObject, "piston pivot forwarding", false);
        }

        public static InstalledPistonPivot For(string kit)
        {
            foreach (InstalledPistonPivot p in _all) if (p != null && p.Kit == kit) return p;
            return null;
        }

        /// <summary>True once the cap, both bolts and both nuts of this piston occupy their targets.</summary>
        public bool AllTargetsOccupied(HashSet<Transform> occupied)
        {
            if (GhostGroup == null || occupied == null) return false;
            foreach (string role in new[] { "PistonEnd", "pistonBolt", "pistonBoltOther", "PistonNut", "PistonNutOther" })
            {
                Transform t = FindDeep(GhostGroup, role);
                if (t == null || !occupied.Contains(t)) return false;
            }
            return true;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            Transform direct = root.Find(name);
            if (direct != null) return direct;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) if (t != root && t.name == name) return t;
            return null;
        }

        /// <summary>
        /// The piston is complete: its grabs are switched off at once, it turns smoothly back
        /// to the authored crankshaft orientation about the same axis (no translation, no
        /// teleport), then it is locked for good. Idempotent.
        /// </summary>
        public void ReturnAndLock(string reason)
        {
            if (Locked || _returning) return;
            _returning = true;
            foreach (MonoBehaviour mb in _forwarded) if (mb != null) mb.enabled = false;
            if (_grabbable != null) _grabbable.enabled = false;
            float delta = Quaternion.Angle(transform.localRotation, _authoredLocalRotation);
            Debug.Log($"[PistonPivot] {Kit}: complete ({reason}) - rotation interaction off, returning {delta:F1} deg to the authored orientation over {returnSeconds:F2} s.");
            StartCoroutine(ReturnRoutine(reason));
        }

        private System.Collections.IEnumerator ReturnRoutine(string reason)
        {
            yield return null;                                        // let the SDK release this frame
            Quaternion from = transform.localRotation;
            Vector3 heldPosition = transform.localPosition;
            float t = 0f;
            float duration = Mathf.Max(0.05f, returnSeconds);
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
                transform.localRotation = Quaternion.Slerp(from, _authoredLocalRotation, k);
                transform.localPosition = heldPosition;
                yield return null;
            }
            transform.localRotation = _authoredLocalRotation;
            transform.localPosition = heldPosition;
            LockPermanently(reason);
        }

        public void LockPermanently(string reason)
        {
            if (Locked) return;
            Locked = true;
            foreach (MonoBehaviour mb in _forwarded) if (mb != null) mb.enabled = false;
            if (_grabbable != null) _grabbable.enabled = false;
            Debug.Log($"[PistonPivot] {Kit}: locked permanently ({reason}); orientation = authored (yaw error {Quaternion.Angle(transform.localRotation, _authoredLocalRotation):F2} deg).");
        }

        /// <summary>Every pivot not yet locked returns smoothly and locks (stage change / completion).</summary>
        public static void LockAll(string reason)
        {
            foreach (InstalledPistonPivot p in _all) if (p != null && !p.Locked) p.ReturnAndLock(reason);
        }

        private void OnDestroy()
        {
            _all.Remove(this);
        }
    }
}
