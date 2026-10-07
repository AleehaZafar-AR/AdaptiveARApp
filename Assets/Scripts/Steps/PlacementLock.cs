// File: PlacementLock.cs
// Locks a component once it has been correctly placed.
//
// One reusable mechanism rather than a special case per part. Anything that gets
// placed - crankshaft, piston, camshaft, and later a cap or a bolt - is locked the
// same way, so a participant working on step 5 cannot pull the crankshaft back out
// of the block.
//
// Locking while held
// ------------------
// A part can now lock the moment it is aligned, while still in the hand. Disabling
// its Interaction SDK components makes the interactor let go, and the SDK then
// restores the Rigidbody's pre-grab state (dynamic) and applies a throw velocity -
// AFTER this lock ran. So the lock re-asserts the pose and the kinematic state for a
// few frames; by then the SDK has finished and the part stays exactly where it snapped.

using System.Collections;
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

        [Tooltip("Turn colliders into triggers so a hand passing through does not shove it. " +
                 "Off keeps them solid, which is better if parts must rest on each other.")]
        [SerializeField] private bool collidersToTriggers = false;

        [Tooltip("Frames over which the locked pose is re-asserted after locking, so a grab " +
                 "that is released by the lock cannot move or unfreeze the part.")]
        [SerializeField] private int reassertFrames = 6;

        public bool IsLocked { get { return isLocked; } }

        private Rigidbody _body;
        private MonoBehaviour[] _interactionComponents;
        private Vector3 _lockedPosition;
        private Quaternion _lockedRotation;
        private Transform _lockedParent;
        private Coroutine _reassert;

        /// <summary>
        /// Snaps to the authoritative pose and prevents further manipulation.
        /// Parenting is untouched, so the part keeps moving with the assembly.
        /// </summary>
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

            _lockedPosition = transform.position;
            _lockedRotation = transform.rotation;
            _lockedParent = transform.parent;

            _body = GetComponent<Rigidbody>();
            FreezeBody();

            if (collidersToTriggers)
            {
                foreach (Collider c in GetComponentsInChildren<Collider>(true))
                    c.isTrigger = true;
            }

            if (disableInteraction)
                SetInteractionEnabled(false);

            if (_reassert != null) StopCoroutine(_reassert);
            if (isActiveAndEnabled) _reassert = StartCoroutine(Reassert());
        }

        /// <summary>Researcher control: lets a locked part be moved again.</summary>
        public void Unlock()
        {
            if (!isLocked) return;
            isLocked = false;

            if (_reassert != null) { StopCoroutine(_reassert); _reassert = null; }

            if (collidersToTriggers)
            {
                foreach (Collider c in GetComponentsInChildren<Collider>(true))
                    c.isTrigger = false;
            }

            SetInteractionEnabled(true);
        }

        /// <summary>Updates the pose the lock holds, e.g. after the part was joined to a moving assembly.</summary>
        public void RefreshLockedPose()
        {
            _lockedPosition = transform.position;
            _lockedRotation = transform.rotation;
            _lockedParent = transform.parent;
        }

        private void FreezeBody()
        {
            if (!freezePhysics || _body == null) return;
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            _body.isKinematic = true;
        }

        private IEnumerator Reassert()
        {
            for (int i = 0; i < reassertFrames; i++)
            {
                yield return null;
                if (!isLocked) yield break;

                // The pose is held in world space unless the part was re-parented to a
                // moving assembly meanwhile; then its local pose is what matters.
                if (transform.parent == _lockedParent)
                    transform.SetPositionAndRotation(_lockedPosition, _lockedRotation);

                FreezeBody();
                if (disableInteraction) SetInteractionEnabled(false);
            }
            _reassert = null;
        }

        /// <summary>
        /// Enables or disables the Interaction SDK components on this part.
        ///
        /// They are found by namespace rather than by concrete type: referencing the ISDK
        /// types directly would tie this script to a specific SDK version, and the grab
        /// components differ between the crankshaft and the pistons anyway.
        /// </summary>
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
}
