// File: PlacementLock.cs
// Locks a component once it has been correctly placed.
//
// One reusable mechanism rather than a special case per part. Anything that gets
// placed - crankshaft, piston, camshaft, and later a cap or a bolt - is locked the
// same way, so a participant working on step 5 cannot pull the crankshaft back out
// of the block.
//
// The part keeps its pose RELATIVE to EngineAnchor, so the whole assembly still
// travels with the marker afterwards.

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

        public bool IsLocked { get { return isLocked; } }

        private Rigidbody _body;
        private MonoBehaviour[] _interactionComponents;

        /// <summary>
        /// Snaps to the authoritative pose and prevents further manipulation.
        /// Parenting is untouched, so the part keeps moving with EngineAnchor.
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

            _body = GetComponent<Rigidbody>();
            if (freezePhysics && _body != null)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                _body.isKinematic = true;
            }

            if (collidersToTriggers)
            {
                foreach (Collider c in GetComponentsInChildren<Collider>(true))
                    c.isTrigger = true;
            }

            if (disableInteraction)
                SetInteractionEnabled(false);
        }

        /// <summary>Researcher control: lets a locked part be moved again.</summary>
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
                if (mb != null) mb.enabled = enabled;
        }
    }
}
