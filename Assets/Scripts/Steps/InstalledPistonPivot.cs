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

            var rb = go.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false;
            pivot._grabbable = go.AddComponent<Oculus.Interaction.Grabbable>();
            var rot = go.AddComponent<Oculus.Interaction.OneGrabRotateTransformer>();
            rot.InjectOptionalPivotTransform(go.transform);
            rot.InjectOptionalRotationAxis(Oculus.Interaction.OneGrabRotateTransformer.Axis.Up);
            pivot._grabbable.InjectOptionalOneGrabTransformer(rot);

            head.SetParent(go.transform, true);
            var headLock = head.GetComponent<PlacementLock>();
            if (headLock != null) { headLock.RefreshLockedPose(); headLock.ForwardedToHandle = true; }
            if (ghostGroup != null) ghostGroup.SetParent(go.transform, true);

            pivot.ForwardSubassemblyGrabs(head);
            _all.Add(pivot);
            Debug.Log($"[PistonPivot] {kit}: created at {pivotPoint} axis={up} ({pivot._forwarded.Count} grab interactable(s) turn the installed piston about the journal axis; targets {(ghostGroup != null ? ghostGroup.name : "-")} follow).");
            return pivot;
        }

        /// <summary>Every grab on the subassembly (head, rod, pin - and parts joined later) turns the pivot.</summary>
        public void ForwardSubassemblyGrabs(Transform root)
        {
            if (Locked || _grabbable == null || root == null) return;
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

        public void LockPermanently(string reason)
        {
            if (Locked) return;
            Locked = true;
            foreach (MonoBehaviour mb in _forwarded) if (mb != null) mb.enabled = false;
            if (_grabbable != null) _grabbable.enabled = false;
            Debug.Log($"[PistonPivot] {Kit}: locked permanently ({reason}).");
        }

        public static void LockAll(string reason)
        {
            foreach (InstalledPistonPivot p in _all) if (p != null) p.LockPermanently(reason);
        }

        private void OnDestroy()
        {
            _all.Remove(this);
        }
    }
}
