// File: PanelRig.cs
// Holds the three participant panels in one comfortable place relative to the
// ArUco marker, instead of pinning them to fixed world coordinates.
//
// Why this exists
// ---------------
// The panels used to sit at hard-coded world positions set once at build time. If
// the marker went down somewhere else, or the operator shifted in their seat, the
// panels ended up in the wrong place or out of view entirely.
//
// Now they are anchored to the marker: the engine sits at the marker, and the
// panels float a little behind it and a little higher, always turned towards
// whoever is looking. The work stays in the middle of the view, the panels frame
// it, and leaning back moves nothing out of sight.
//
// Movement uses a dead zone rather than following every frame. Panels that chase
// the head continuously are uncomfortable to read; panels that only re-settle once
// you have genuinely moved are not.

using UnityEngine;

namespace AdaptiveAR.UI
{
    public class PanelRig : MonoBehaviour
    {
        [Header("Anchoring")]
        [Tooltip("The marker-driven transform the panels follow. Normally MarkerAnchor.")]
        [SerializeField] private Transform markerAnchor;

        [Tooltip("Head transform. Falls back to Camera.main when empty.")]
        [SerializeField] private Transform head;

        [Header("Placement, metres")]
        [Tooltip("How far above the marker plane the panels sit. Raise this if they cover the engine.")]
        [SerializeField] private float heightAboveMarker = 0.42f;

        [Tooltip("How far beyond the marker, away from the viewer, the panels sit. " +
                 "Keeps them off the work area without pushing them out of easy reading range.")]
        [SerializeField] private float depthBeyondMarker = 0.14f;

        [Header("Comfort")]
        [Tooltip("Degrees of head turn tolerated before the panels re-aim. Stops them chasing you.")]
        [SerializeField] private float yawDeadZoneDegrees = 14f;

        [Tooltip("Metres of head movement tolerated before the panels re-settle.")]
        [SerializeField] private float positionDeadZone = 0.12f;

        [Tooltip("Higher is snappier. Low values drift gently into place.")]
        [SerializeField] private float smoothing = 3.5f;

        [Tooltip("Keep the panels upright. Off lets them tilt with the head, which reads badly.")]
        [SerializeField] private bool keepUpright = true;

        [Header("Locking")]
        [Tooltip("Freeze the panels permanently once the marker has been found. The marker is " +
                 "taped to the bench and does not move, so a fixed panel position is calmer to " +
                 "read than one that keeps re-settling.")]
        [SerializeField] private bool lockWhenSequenceStarts = true;

        [Tooltip("Used only to detect that the marker has been found and the sequence has begun.")]
        [SerializeField] private AdaptiveAR.Steps.StepRunner stepRunner;

        [Tooltip("Locks as soon as the ArUco anchor is found, which is earlier than the " +
                 "sequence start and covers the whole onboarding.")]
        [SerializeField] private StepManager stepManager;

        [Tooltip("Metres. Below this the viewer is too close to the marker for the " +
                 "look direction to be stable, so the last good direction is kept. " +
                 "Without this the panels swing wildly when leaning over the bench.")]
        [SerializeField] private float minStableDistance = 0.35f;

        [Header("Behaviour")]
        [Tooltip("Snap straight to the target on the first frame instead of gliding in from the origin.")]
        [SerializeField] private bool snapOnFirstFrame = true;

        [Header("Debug")]
        [Tooltip("Re-centre the panels immediately. Handy when a participant has shifted seat.")]
        [SerializeField] private OVRInput.RawButton recenterButton = OVRInput.RawButton.LThumbstick;

        [SerializeField] private KeyCode recenterKey = KeyCode.R;

        /// <summary>True once the panels have been parked for good.</summary>
        public bool IsLocked { get; private set; }

        private Vector3 _frozenMarkerPos;
        private bool _hasFrozenMarkerPos;
        private Vector3 _stableDirection = Vector3.forward;
        private bool _hasStableDirection;
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private bool _hasTarget;

        private void Start()
        {
            if (head == null && Camera.main != null)
                head = Camera.main.transform;

            if (snapOnFirstFrame && ComputeTarget(out Vector3 p, out Quaternion r))
            {
                _targetPosition = p;
                _targetRotation = r;
                _hasTarget = true;
                transform.SetPositionAndRotation(p, r);
            }
        }

        private void LateUpdate()
        {
            if (head == null)
            {
                if (Camera.main == null) return;
                head = Camera.main.transform;
            }

            if (OVRInput.GetDown(recenterButton) || Input.GetKeyDown(recenterKey))
            {
                IsLocked = false;      // an explicit recentre always wins
                Recenter();
                if (lockWhenSequenceStarts && stepRunner != null && stepRunner.HasStarted)
                    IsLocked = true;
                return;
            }

            // Park the panels the moment the ArUco anchor is found - before onboarding,
            // not after it - and leave them there.
            if (!IsLocked && lockWhenSequenceStarts && stepManager != null && stepManager.AnchorLocked)
            {
                Recenter();
                DetachFromMarkerChain();
                IsLocked = true;
                return;
            }

            if (!IsLocked && lockWhenSequenceStarts && stepRunner != null && stepRunner.HasStarted)
            {
                Recenter();
                DetachFromMarkerChain();
                IsLocked = true;
                return;
            }

            if (IsLocked) return;

            if (!ComputeTarget(out Vector3 wantPos, out Quaternion wantRot))
                return;

            // Dead zone: only adopt a new target once the viewer has actually moved.
            if (!_hasTarget
                || Vector3.Distance(wantPos, _targetPosition) > positionDeadZone
                || Quaternion.Angle(wantRot, _targetRotation) > yawDeadZoneDegrees)
            {
                _targetPosition = wantPos;
                _targetRotation = wantRot;
                _hasTarget = true;
            }

            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, _targetPosition, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, t);
        }

        /// <summary>
        /// Detaches the rig from the marker-driven hierarchy, keeping its world pose.
        ///
        /// THIS is what actually stops the jitter. The rig was parented to MarkerAnchor,
        /// which ArUco rewrites every frame. Skipping the rig's own follow logic changed
        /// nothing, because a child inherits its parent's transform regardless: every
        /// pose correction and every bit of tracking noise still reached the panels.
        /// Once detached, nothing downstream of the marker can move them.
        ///
        /// EngineAnchor stays under the marker chain, so the assembly itself keeps its
        /// registration - only the participant UI is decoupled.
        /// </summary>
        private void DetachFromMarkerChain()
        {
            if (markerAnchor != null)
            {
                _frozenMarkerPos = markerAnchor.position;
                _hasFrozenMarkerPos = true;
            }

            if (transform.parent == null) return;

            Vector3 p = transform.position;
            Quaternion r = transform.rotation;

            transform.SetParent(null, true);
            transform.SetPositionAndRotation(p, r);
        }

        /// <summary>Drops the dead zone for one frame and re-aims at the current head pose.</summary>
        public void Recenter()
        {
            if (!ComputeTarget(out Vector3 p, out Quaternion r)) return;

            _targetPosition = p;
            _targetRotation = r;
            _hasTarget = true;
            transform.SetPositionAndRotation(p, r);
        }

        private bool ComputeTarget(out Vector3 position, out Quaternion rotation)
        {
            position = transform.position;
            rotation = transform.rotation;

            if (markerAnchor == null || head == null)
                return false;

            // Cached once the rig detaches, so the reference survives unparenting.
            Vector3 markerPos = _hasFrozenMarkerPos ? _frozenMarkerPos : markerAnchor.position;

            // Horizontal direction from the viewer to the marker. The panels go a little
            // further along it, so the marker - and the engine on it - stays nearer the
            // viewer than the panels do.
            Vector3 away = markerPos - head.position;
            away.y = 0f;

            // Close to the marker the horizontal direction becomes unstable and tiny head
            // movements swing it through large angles - that is the jitter. Hold the last
            // good direction instead of recomputing from a degenerate vector.
            if (away.magnitude < minStableDistance)
            {
                if (_hasStableDirection) away = _stableDirection;
                else away = Vector3.forward;
            }
            else
            {
                away.Normalize();
                _stableDirection = away;
                _hasStableDirection = true;
            }

            position = markerPos + Vector3.up * heightAboveMarker + away * depthBeyondMarker;

            // Face the viewer. Canvas forward points away from its readable side, so the
            // panels look along the same direction the viewer is looking.
            Vector3 toViewer = position - head.position;
            if (keepUpright) toViewer.y = 0f;

            if (toViewer.sqrMagnitude < 1e-4f)
                return false;

            rotation = Quaternion.LookRotation(toViewer.normalized, Vector3.up);
            return true;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (markerAnchor == null) return;

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(markerAnchor.position, transform.position);
            Gizmos.DrawWireSphere(markerAnchor.position, 0.03f);
        }
#endif
    }
}
