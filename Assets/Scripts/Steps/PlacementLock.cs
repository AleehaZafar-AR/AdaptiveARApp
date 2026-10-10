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

            if (disableInteraction)
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

            bool fixedBody = false;
            if (freezePhysics && _body != null && !_body.isKinematic)
            {
                FreezeBody();
                fixedBody = true;
            }

            // A re-parent done by the assembly join is legitimate: adopt it.
            if (transform.parent != _lockedParent)
            {
                RefreshLockedPose();
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

            if (disableInteraction && (fixedBody || moved)) SetInteractionEnabled(false);
        }

        private void SetInteractionEnabled(bool enabled)
        {
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
        }
    }

    /// <summary>
    /// Diagnostic snapshot of a real part's visibility and hierarchy, written at every
    /// mutation point on the success path. Remove or silence once the Quest run is clean.
    /// </summary>
    public static class PartWatch
    {
        public static bool Enabled = true;

        public static void Log(string where, Transform t)
        {
            if (!Enabled || t == null) return;
            var r = t.GetComponentInChildren<Renderer>(true);
            var rb = t.GetComponent<Rigidbody>();
            Debug.Log($"[PartWatch] {where}: '{t.name}'#{t.GetInstanceID()} activeSelf={t.gameObject.activeSelf} " +
                      $"activeInHierarchy={t.gameObject.activeInHierarchy} renderer={(r != null ? r.enabled.ToString() : "none")} " +
                      $"parent={(t.parent != null ? t.parent.name : "<root>")} pos={t.position} rot={t.rotation.eulerAngles} " +
                      $"kinematic={(rb != null ? rb.isKinematic.ToString() : "n/a")}");
        }
    }
}
