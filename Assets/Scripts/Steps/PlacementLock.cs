// File: PlacementLock.cs
// Locks a component once it has been correctly placed.
//
// One reusable mechanism rather than a special case per part. Anything that gets
// placed - crankshaft, piston, camshaft, cap, bolt - is locked the same way, so a
// participant working on step 5 cannot pull the crankshaft back out of the block.
//
// Why the lock must be enforced continuously
// -------------------------------------------
// A part can lock the moment it is aligned, while still in the hand. Disabling its
// Interaction SDK components makes the interactor let go, and the SDK's
// RigidbodyKinematicLocker then restores the Rigidbody's PRE-GRAB state (dynamic)
// and applies a throw velocity - after this lock ran, possibly several frames later.
// A part that silently became dynamic was what made rods vanish (see DropIntoTray).
// So while locked, this component re-asserts "kinematic, zero velocity, locked pose"
// every frame (LateUpdate, before physics) and after every physics step, and logs
// any correction it had to make. It never disables the GameObject or a renderer.

using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class PlacementLock : MonoBehaviour
    {
        [Header("State")]
        [SerializeField] private bool isLocked;

        [Header("What locking does")]
        [Tooltip("Turn off the Interaction SDK grab components so the part cannot be picked up again.")]
        [SerializeField] private bool disableInteraction = true;

        [Tooltip("Make the Rigidbody kinematic so physics cannot nudge it out of place.")]
        [SerializeField] private bool freezePhysics = true;

        [Tooltip("Turn colliders into triggers so a hand passing through does not shove it.")]
        [SerializeField] private bool collidersToTriggers = false;

        [Tooltip("Log every correction the lock has to make (diagnostic).")]
        [SerializeField] private bool logCorrections = true;

        public bool IsLocked { get { return isLocked; } }

        /// <summary>
        /// While true the locked part may still be picked up (the loose piston subassembly on
        /// the mat): the lock yields while a hand holds it and re-freezes it wherever it was
        /// let go. Off once the part is installed on the engine.
        /// </summary>
        public bool Repositionable
        {
            get { return _repositionable; }
            set
            {
                if (_repositionable == value) return;
                _repositionable = value;
                if (isLocked && disableInteraction) SetInteractionEnabled(!value ? false : true);
                if (!value && isLocked) { RefreshLockedPose(); FreezeBody(); }
                PartWatch.Log(value ? "PlacementLock.Repositionable=ON" : "PlacementLock.Repositionable=OFF", transform);
            }
        }
        private bool _repositionable;
        private bool _wasHeld;

        /// <summary>
        /// Set on a part joined to a loose subassembly: its grab interactables drive the kit
        /// handle, so this lock must not switch them off. The handle's own lock does that
        /// for the whole subassembly once it is installed.
        /// </summary>
        public bool ForwardedToHandle { get; set; }

        /// <summary>
        /// While true a repositionable lock does NOT yield to the hand: set for the moment
        /// after a child part snapped onto the subassembly while the hand that placed it is
        /// still closed, so the hand cannot carry the subassembly away. Cleared once every
        /// hand has opened.
        /// </summary>
        public bool RepositionSuspended { get; set; }

        private bool IsHeldNow()
        {
            var pe = GetComponent<Oculus.Interaction.PointableElement>();
            return pe != null && pe.SelectingPointsCount > 0;
        }

        private Rigidbody _body;
        private MonoBehaviour[] _interactionComponents;
        private Vector3 _lockedLocalPosition;
        private Quaternion _lockedLocalRotation;
        private Transform _lockedParent;
        private int _corrections;

        public void LockAt(Transform target)
        {
            if (target != null)
                transform.SetPositionAndRotation(target.position, target.rotation);

            Lock();
        }

        public void Lock()
        {
            if (isLocked) return;
            isLocked = true;
            _corrections = 0;

            RefreshLockedPose();

            _body = GetComponent<Rigidbody>();
            FreezeBody();

            if (collidersToTriggers)
            {
                foreach (Collider c in GetComponentsInChildren<Collider>(true))
                    c.isTrigger = true;
            }

            if (disableInteraction && !_repositionable && !ForwardedToHandle)
                SetInteractionEnabled(false);

            PartWatch.Log("PlacementLock.Lock", transform);
        }

        public void Unlock()
        {
            if (!isLocked) return;
            isLocked = false;

            if (collidersToTriggers)
            {
                foreach (Collider c in GetComponentsInChildren<Collider>(true))
                    c.isTrigger = false;
            }

            SetInteractionEnabled(true);
            PartWatch.Log("PlacementLock.Unlock", transform);
        }

        /// <summary>Re-reads the pose the lock holds, in LOCAL space of the current parent.</summary>
        public void RefreshLockedPose()
        {
            _lockedParent = transform.parent;
            _lockedLocalPosition = transform.localPosition;
            _lockedLocalRotation = transform.localRotation;
        }

        private void FreezeBody()
        {
            if (!freezePhysics || _body == null) return;
            if (!_body.isKinematic) _body.isKinematic = true;
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
        }

        private void LateUpdate()
        {
            if (isLocked) Enforce("LateUpdate");
        }

        private void FixedUpdate()
        {
            if (isLocked) Enforce("FixedUpdate");
        }

        /// <summary>
        /// Holds the locked pose in the parent's local space, so a part joined to a moving
        /// assembly rides with it, and keeps the body kinematic whatever the SDK restored.
        /// </summary>
        private void Enforce(string phase)
        {
            if (_body == null) _body = GetComponent<Rigidbody>();

            // Repositionable: the hand owns the pose while holding; on release the pose it was
            // left at becomes the locked pose and the body is frozen again.
            if (_repositionable && !RepositionSuspended)
            {
                bool held = IsHeldNow();
                if (held) { _wasHeld = true; return; }
                if (_wasHeld)
                {
                    _wasHeld = false;
                    RefreshLockedPose();
                    FreezeBody();
                    if (logCorrections) Debug.Log($"[PlacementLock] '{name}' released by the hand: re-locked at {transform.position}.");
                    return;
                }
            }

            bool fixedBody = false;
            if (freezePhysics && _body != null && !_body.isKinematic)
            {
                FreezeBody();
                fixedBody = true;
            }

            // A re-parent done by the assembly join is legitimate: adopt it (and record it:
            // a stray SetParent from anywhere becomes permanent here).
            if (transform.parent != _lockedParent)
            {
                string was = _lockedParent != null ? _lockedParent.name + "#" + _lockedParent.GetInstanceID() : "<root>";
                RefreshLockedPose();
                PartWatch.Log("PlacementLock.Enforce(adopted new parent)", transform, "was " + was);
                return;
            }

            bool moved = (transform.localPosition - _lockedLocalPosition).sqrMagnitude > 1e-10f
                         || Quaternion.Angle(transform.localRotation, _lockedLocalRotation) > 0.01f;
            if (moved)
            {
                transform.localPosition = _lockedLocalPosition;
                transform.localRotation = _lockedLocalRotation;
            }

            if ((fixedBody || moved) && logCorrections && _corrections < 20)
            {
                _corrections++;
                Debug.Log($"[PlacementLock] '{name}' corrected in {phase}: {(fixedBody ? "body had become dynamic; " : "")}" +
                          $"{(moved ? "pose had moved; " : "")}restored. (#{_corrections})");
            }

            if (disableInteraction && (fixedBody || moved) && !_repositionable && !ForwardedToHandle) SetInteractionEnabled(false);
        }

        private void SetInteractionEnabled(bool enabled)
        {
            // Always re-scan: parts join (and leave) this hierarchy during the session, and a
            // cached list from the first lock would miss the children joined since.
            _interactionComponents = null;
            if (_interactionComponents == null)
            {
                var found = new System.Collections.Generic.List<MonoBehaviour>();

                foreach (MonoBehaviour mb in GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || mb == this) continue;
                    string ns = mb.GetType().Namespace;
                    if (!string.IsNullOrEmpty(ns) && ns.StartsWith("Oculus.Interaction"))
                        found.Add(mb);
                }

                _interactionComponents = found.ToArray();
            }

            foreach (MonoBehaviour mb in _interactionComponents)
                if (mb != null && mb.enabled != enabled) mb.enabled = enabled;

            // The SDK's trigger broadcaster is disabled on unregister and NOT re-enabled on
            // re-register: after any re-enable the part would be invisible to that interactor
            // type (hand or controller). Repair it here, every time.
            if (enabled) GrabParity.ReassertBroadcasters(gameObject, "lock interaction re-enabled", false);
        }
    }

    /// <summary>
    /// Diagnostic snapshot of a real part's visibility and hierarchy, written at every
    /// mutation point on the success path. Remove or silence once the Quest run is clean.
    /// </summary>
    /// <summary>
    /// Snapshot of a part at every snap / lock / join / hand-over / guard event, to logcat
    /// ([PartWatch]) and, when a session logger is attached, to the session JSONL as
    /// part_lifecycle lines. One device reproduction of a "vanished" part then shows the
    /// last event at which it was still active, rendered, correctly parented and in place.
    /// </summary>
    public static class PartWatch
    {
        public static bool Enabled = true;

        /// <summary>Set by AssemblySessionController; null = logcat only.</summary>
        public static AdaptiveAR.Logging.SessionLogger Logger;

        public static void Log(string where, Transform t) { Log(where, t, null); }

        public static void Log(string where, Transform t, string note)
        {
            if (!Enabled) return;
            if (t == null) { Gone(where, "<null>", note); return; }
            Emit(where, t, ParentName(t.parent), note);
        }

        /// <summary>Re-parents and records parent-before / parent-after in one line.</summary>
        public static void Reparent(string where, Transform t, Transform newParent, bool worldPositionStays, string note)
        {
            if (t == null) return;
            string before = ParentName(t.parent);
            t.SetParent(newParent, worldPositionStays);
            if (!Enabled) return;
            Emit(where, t, before, note);
        }

        /// <summary>The part no longer exists (destroyed) or was never found.</summary>
        public static void Gone(string where, string name, string note)
        {
            if (!Enabled) return;
            Debug.LogError($"[PartWatch] {where}: '{name}' GONE (destroyed or null) {note}");
            var l = Logger;
            if (l != null) l.LogPartLifecycle(where, name, 0, false, false, 0, 0, null, null, null, Vector3.zero, null, "gone: " + (note ?? ""));
        }

        private static string ParentName(Transform p) { return p != null ? p.name + "#" + p.GetInstanceID() : "<root>"; }

        private static void Emit(string where, Transform t, string parentBefore, string note)
        {
            int on = 0, total = 0;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true)) { total++; if (r != null && r.enabled) on++; }
            var rb = t.GetComponent<Rigidbody>();
            string parentNow = ParentName(t.parent);
            string kin = rb != null ? rb.isKinematic.ToString() : "n/a";
            Debug.Log($"[PartWatch] {where}: '{t.name}'#{t.GetInstanceID()} activeSelf={t.gameObject.activeSelf} " +
                      $"activeInHierarchy={t.gameObject.activeInHierarchy} renderers={on}/{total} " +
                      $"parent={parentNow}{(parentBefore != parentNow ? " (was " + parentBefore + ")" : "")} pos={t.position} rot={t.rotation.eulerAngles} " +
                      $"scale={t.lossyScale.x:F3} kinematic={kin}{(note != null ? " note=" + note : "")}");
            var l = Logger;
            if (l != null)
                l.LogPartLifecycle(where, t.name, t.GetInstanceID(), t.gameObject.activeSelf, t.gameObject.activeInHierarchy, on, total,
                                   parentNow, parentBefore != parentNow ? parentBefore : null, t.parent != null ? t.parent.name : null, t.position, kin, note);
        }
    }
}
