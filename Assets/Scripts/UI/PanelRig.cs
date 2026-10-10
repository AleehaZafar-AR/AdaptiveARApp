// File: PanelRig.cs
// Holds the participant panels in one comfortable place relative to the workspace,
// instead of pinning them to fixed world coordinates.
//
// Two phases
// ----------
// BEFORE the workspace is placed there is no workspace to be relative to. The rig
// parks itself once, directly in front of the participant at reading distance, and
// stays there - world-locked, not head-locked - while they point at the desk.
//
// AFTER the workspace is placed the rig settles once so that the MAIN panel sits
// directly above and a little beyond the engine, centred on the viewing axis the
// workspace was placed with, then detaches from the anchor chain and stays put.
// The side panels are children of the rig at offsets tuned by hand relative to the
// main panel; the rig moves as one unit, so those offsets are never touched.

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
        [Tooltip("How far above the workspace plane the MAIN panel's centre sits.")]
        [SerializeField] private float heightAboveMarker = 0.42f;

        [Tooltip("How far beyond the engine, away from the viewer, the main panel's centre sits.")]
        [SerializeField] private float depthBeyondMarker = 0.14f;

        [Header("Main panel")]
        [Tooltip("The main panel. Centred on the viewing axis after placement; parked in front " +
                 "of the head before it. Found as the first child Canvas when empty.")]
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

        [Tooltip("Locks as soon as the workspace is placed.")]
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

        public bool IsLocked { get; private set; }
        public bool IsPrePlacement { get; private set; }

        /// <summary>
        /// Authored local pose of each child canvas, captured once the rig locks and
        /// enforced afterwards. The side panels' Inspector values (Pos X/Y = anchored
        /// position, Pos Z = local z) are authoritative; nothing may move them later.
        /// </summary>
        private class Pin { public RectTransform rt; public Vector3 pos; public Quaternion rot; public Vector3 scale; public float lastLogAt; }
        private readonly System.Collections.Generic.List<Pin> _pins = new System.Collections.Generic.List<Pin>();

        private Vector3 _frozenMarkerPos;
        private bool _hasFrozenMarkerPos;
        private Vector3 _frozenViewAxis;
        private bool _hasFrozenViewAxis;
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

        private void EnterPrePlacement()
        {
            IsPrePlacement = true;
            Detach(freezeMarker: false);
            ParkInFrontOfHead();
        }

        /// <summary>Offset of the main panel inside the rig (the rig has unit scale).</summary>
        private Vector3 MainPanelOffset()
        {
            if (mainPanel == null) return Vector3.zero;
            var rt = mainPanel as RectTransform;
            return rt != null
                ? new Vector3(rt.anchoredPosition.x, rt.anchoredPosition.y, rt.localPosition.z)
                : mainPanel.localPosition;
        }

        private void ParkInFrontOfHead()
        {
            if (head == null) return;

            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 centre = head.position + fwd * prePlacementDistance + Vector3.up * prePlacementHeightOffset;
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up);
            Vector3 pos = centre - rot * MainPanelOffset();

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

            if (IsPrePlacement)
            {
                if (stepManager != null && stepManager.AnchorLocked)
                {
                    IsPrePlacement = false;
                    _hasStableDirection = false;
                    FreezeWorkspaceFrame();
                    Recenter();
                    Detach(freezeMarker: true);
                    IsLocked = true;
                    PinChildren();
                    return;
                }

                if (recenter) ParkInFrontOfHead();
                return;
            }

            if (recenter)
            {
                IsLocked = false;
                Recenter();
                if (lockWhenSequenceStarts && stepRunner != null && stepRunner.HasStarted)
                    IsLocked = true;
                return;
            }

            if (!IsLocked && lockWhenSequenceStarts && stepManager != null && stepManager.AnchorLocked)
            {
                FreezeWorkspaceFrame();
                Recenter();
                Detach(freezeMarker: true);
                IsLocked = true;
                PinChildren();
                return;
            }

            if (!IsLocked && lockWhenSequenceStarts && stepRunner != null && stepRunner.HasStarted)
            {
                Recenter();
                Detach(freezeMarker: true);
                IsLocked = true;
                PinChildren();
                return;
            }

            if (IsLocked) { EnforcePins(); return; }

            if (!ComputeTarget(out Vector3 wantPos, out Quaternion wantRot))
                return;

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
        /// Records every child canvas's authored local pose. A RectTransform under a plain
        /// Transform keeps its authored X/Y in anchoredPosition and its Z in localPosition;
        /// Unity re-derives one from the other on enable/layout, and the scene serialises
        /// them out of step. The Inspector values are taken as truth here, applied once,
        /// and then held.
        /// </summary>
        private void PinChildren()
        {
            _pins.Clear();
            foreach (Transform child in transform)
            {
                var rt = child as RectTransform;
                if (rt == null) continue;

                Vector3 pos = new Vector3(rt.anchoredPosition.x, rt.anchoredPosition.y, rt.localPosition.z);
                rt.localPosition = pos;
                _pins.Add(new Pin { rt = rt, pos = pos, rot = rt.localRotation, scale = rt.localScale, lastLogAt = -999f });
            }
            Debug.Log($"[PanelRig] {_pins.Count} panel(s) pinned to their authored local poses.");
        }

        /// <summary>Restores any pinned canvas that something moved, and says so.</summary>
        private void EnforcePins()
        {
            foreach (Pin p in _pins)
            {
                if (p.rt == null) continue;
                bool moved = (p.rt.localPosition - p.pos).sqrMagnitude > 1e-8f
                             || Quaternion.Angle(p.rt.localRotation, p.rot) > 0.01f
                             || (p.rt.localScale - p.scale).sqrMagnitude > 1e-10f;
                if (!moved) continue;

                if (Time.time - p.lastLogAt > 1f)
                {
                    p.lastLogAt = Time.time;
                    Debug.LogWarning($"[PanelRig] '{p.rt.name}' moved to {p.rt.localPosition} (authored {p.pos}); restored. " +
                                     "Something re-laid the canvas out this frame.");
                }
                p.rt.localPosition = p.pos;
                p.rt.localRotation = p.rot;
                p.rt.localScale = p.scale;
            }
        }

        /// <summary>
        /// Captures the workspace's viewing axis at placement: MarkerAnchor's +Y is the
        /// horizontal "away from the participant" direction the workspace was placed with.
        /// </summary>
        private void FreezeWorkspaceFrame()
        {
            if (markerAnchor == null) return;
            Vector3 axis = Vector3.ProjectOnPlane(markerAnchor.up, Vector3.up);
            if (axis.sqrMagnitude > 1e-4f)
            {
                _frozenViewAxis = axis.normalized;
                _hasFrozenViewAxis = true;
            }
        }

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

        public void Recenter()
        {
            if (!ComputeTarget(out Vector3 p, out Quaternion r)) return;

            _targetPosition = p;
            _targetRotation = r;
            _hasTarget = true;
            transform.SetPositionAndRotation(p, r);
        }

        /// <summary>Centre of the engine block in plan, if it can be found under the anchor; else the anchor point.</summary>
        private Vector3 EngineReferencePoint(Vector3 markerPos)
        {
            if (markerAnchor == null) return markerPos;

            foreach (Transform t in markerAnchor.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "oilPan") continue;
                var rs = t.GetComponentsInChildren<Renderer>(false);
                if (rs.Length == 0) break;
                Bounds b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                return new Vector3(b.center.x, markerPos.y, b.center.z);
            }
            return markerPos;
        }

        /// <summary>
        /// Rig pose such that the MAIN panel's centre is above and beyond the engine on the
        /// viewing axis, facing back along it. Side panels keep their rig-relative offsets.
        /// </summary>
        private bool ComputeTarget(out Vector3 position, out Quaternion rotation)
        {
            position = transform.position;
            rotation = transform.rotation;

            if (markerAnchor == null || head == null)
                return false;

            Vector3 markerPos = _hasFrozenMarkerPos ? _frozenMarkerPos : markerAnchor.position;
            Vector3 reference = EngineReferencePoint(markerPos);

            Vector3 away;
            if (_hasFrozenViewAxis)
            {
                away = _frozenViewAxis;
            }
            else
            {
                away = reference - head.position;
                away.y = 0f;

                if (away.magnitude < minStableDistance)
                {
                    away = _hasStableDirection ? _stableDirection : Vector3.forward;
                }
                else
                {
                    away.Normalize();
                    _stableDirection = away;
                    _hasStableDirection = true;
                }
            }

            Vector3 panelCentre = reference + Vector3.up * heightAboveMarker + away * depthBeyondMarker;

            // Face back along the viewing axis. Canvas forward points away from its readable
            // side, so the rotation looks along "away".
            rotation = keepUpright
                ? Quaternion.LookRotation(away, Vector3.up)
                : Quaternion.LookRotation(panelCentre - head.position, Vector3.up);

            position = panelCentre - rotation * MainPanelOffset();
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
