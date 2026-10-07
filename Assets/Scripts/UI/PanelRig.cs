// File: PanelRig.cs
// Holds the participant panels in one comfortable place relative to the workspace,
// instead of pinning them to fixed world coordinates.
//
// Two phases
// ----------
// BEFORE the workspace is placed there is no workspace to be relative to. The rig
// parks itself once, directly in front of the participant at reading distance, and
// stays there - world-locked, not head-locked - while they point at the desk. Only
// the main panel is shown then (AdaptivePanelController hides the rest).
//
// AFTER the workspace is placed the rig re-settles once relative to the workspace
// (the same marker-relative offsets as before), detaches from the anchor chain and
// stays put for the session. The panels never follow the head.
//
// Movement, when it is allowed at all, uses a dead zone rather than following every
// frame. Panels that chase the head are uncomfortable to read.

using UnityEngine;

namespace AdaptiveAR.UI
{
    public class PanelRig : MonoBehaviour
    {
        [Header("Anchoring")]
        [Tooltip("The workspace transform the panels are placed relative to. Normally MarkerAnchor.")]
        [SerializeField] private Transform markerAnchor;

        [Tooltip("Head transform. Falls back to Camera.main when empty.")]
        [SerializeField] private Transform head;

        [Header("Placement, metres")]
        [Tooltip("How far above the workspace plane the panels sit. Raise this if they cover the engine.")]
        [SerializeField] private float heightAboveMarker = 0.42f;

        [Tooltip("How far beyond the workspace, away from the viewer, the panels sit. " +
                 "Keeps them off the work area without pushing them out of easy reading range.")]
        [SerializeField] private float depthBeyondMarker = 0.14f;

        [Header("Before placement")]
        [Tooltip("The main panel. Parked in front of the head before the workspace exists. " +
                 "Found as the first child Canvas when empty.")]
        [SerializeField] private Transform mainPanel;

        [Tooltip("Reading distance from the head for the parked main panel, metres.")]
        [SerializeField] private float prePlacementDistance = 0.75f;

        [Tooltip("Vertical offset of the parked panel from eye height, metres. Slightly below is comfortable.")]
        [SerializeField] private float prePlacementHeightOffset = -0.08f;

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
        [Tooltip("Freeze the panels permanently once the workspace has been placed.")]
        [SerializeField] private bool lockWhenSequenceStarts = true;

        [Tooltip("Used only to detect that the sequence has begun.")]
        [SerializeField] private AdaptiveAR.Steps.StepRunner stepRunner;

        [Tooltip("Locks as soon as the workspace is placed, which is earlier than the " +
                 "sequence start and covers the whole onboarding.")]
        [SerializeField] private StepManager stepManager;

        [Tooltip("Metres. Below this the viewer is too close to the workspace for the " +
                 "look direction to be stable, so the last good direction is kept.")]
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

        /// <summary>True while parked in front of the head, before the workspace exists.</summary>
        public bool IsPrePlacement { get; private set; }

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

            if (mainPanel == null)
            {
                var canvas = GetComponentInChildren<Canvas>(true);
                if (canvas != null) mainPanel = canvas.transform;
            }

            bool awaitingPlacement = stepManager != null && stepManager.Placement != null && !stepManager.AnchorLocked;
            if (awaitingPlacement)
            {
                EnterPrePlacement();
                return;
            }

            if (snapOnFirstFrame && ComputeTarget(out Vector3 p, out Quaternion r))
            {
                _targetPosition = p;
                _targetRotation = r;
                _hasTarget = true;
                transform.SetPositionAndRotation(p, r);
            }
        }

        /// <summary>
        /// Parks the rig so the main panel sits in front of the head at reading distance,
        /// world-locked. The rig leaves the anchor chain now, so moving the workspace
        /// root during placement cannot drag the panel around.
        /// </summary>
        private void EnterPrePlacement()
        {
            IsPrePlacement = true;
            Detach(freezeMarker: false);
            ParkInFrontOfHead();
        }

        private void ParkInFrontOfHead()
        {
            if (head == null) return;

            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 centre = head.position + fwd * prePlacementDistance + Vector3.up * prePlacementHeightOffset;
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up);

            // The main panel is offset inside the rig; place the rig so the PANEL lands at
            // the centre of view, not the rig origin.
            Vector3 panelOffset = Vector3.zero;
            if (mainPanel != null)
            {
                var rt = mainPanel as RectTransform;
                panelOffset = rt != null
                    ? new Vector3(rt.anchoredPosition.x, rt.anchoredPosition.y, rt.localPosition.z)
                    : mainPanel.localPosition;
            }
            Vector3 pos = centre - rot * panelOffset;

            transform.SetPositionAndRotation(pos, rot);
            _targetPosition = pos;
            _targetRotation = rot;
            _hasTarget = true;
        }

        private void LateUpdate()
        {
            if (head == null)
            {
                if (Camera.main == null) return;
                head = Camera.main.transform;
            }

            bool recenter = OVRInput.GetDown(recenterButton) || Input.GetKeyDown(recenterKey);

            // --- parked in front of the head until the workspace exists ---
            if (IsPrePlacement)
            {
                if (stepManager != null && stepManager.AnchorLocked)
                {
                    // The workspace now exists: settle once relative to it and stay.
                    IsPrePlacement = false;
                    _hasStableDirection = false;
                    Recenter();
                    Detach(freezeMarker: true);
                    IsLocked = true;
                    return;
                }

                if (recenter) ParkInFrontOfHead();
                return;
            }

            if (recenter)
            {
                IsLocked = false;      // an explicit recentre always wins
                Recenter();
                if (lockWhenSequenceStarts && stepRunner != null && stepRunner.HasStarted)
                    IsLocked = true;
                return;
            }

            // Park the panels the moment the workspace is placed - before onboarding,
            // not after it - and leave them there.
            if (!IsLocked && lockWhenSequenceStarts && stepManager != null && stepManager.AnchorLocked)
            {
                Recenter();
                Detach(freezeMarker: true);
                IsLocked = true;
                return;
            }

            if (!IsLocked && lockWhenSequenceStarts && stepRunner != null && stepRunner.HasStarted)
            {
                Recenter();
                Detach(freezeMarker: true);
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
        /// Detaches the rig from the anchor hierarchy, keeping its world pose. A child
        /// inherits every move of its parent, so this is what actually stops the panels
        /// moving with the workspace root. Optionally caches the workspace position so
        /// later re-centres stay relative to where it was when the rig locked.
        /// </summary>
        private void Detach(bool freezeMarker)
        {
            if (freezeMarker && markerAnchor != null)
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

            // Horizontal direction from the viewer to the workspace. The panels go a little
            // further along it, so the engine stays nearer the viewer than the panels do.
            Vector3 away = markerPos - head.position;
            away.y = 0f;

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
