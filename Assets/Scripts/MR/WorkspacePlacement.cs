// File: WorkspacePlacement.cs
// Lets the participant choose where the virtual workspace goes, instead of the
// ArUco marker deciding it.
//
// Why this exists
// ---------------
// The study now uses fully virtual components in passthrough. There is no physical
// engine to register against, so continuous marker tracking only added a scanning
// step and a camera permission. This component replaces that startup:
//
//     passthrough -> point at the desk -> reticle -> "Place Workspace"
//              -> workspace root posed on the surface -> frozen
//
// It is a swap of the registration PROVIDER, not of the architecture. It writes the
// same transform ArUco used to write (MarkerAnchor, the top of the anchor chain), so
// CalibrationOffset, EngineAnchor, the trays, ghosts, TabletopSupport and the
// PanelRig all keep working exactly as they did under the marker. Nothing downstream
// knows the provider changed.
//
// Surface source
// --------------
// Meta's EnvironmentRaycastManager (Depth API, MR Utility Kit) gives a real hit
// against the physical desk with no room setup. When it is not supported - the
// Editor, or depth not yet available - a horizontal plane at a configurable table
// height is used instead, so the app is still usable and the fallback is reported
// rather than silent.
//
// Axis convention
// ---------------
// MarkerAnchor's local +Z is the marker normal ("up, out of the table"); that is what
// AnchorCalibration and TabletopSupport assume. This component poses MarkerAnchor so
// that +Z is world up and +Y points away from the viewer, with a configurable yaw.

using System;
using Meta.XR;
using UnityEngine;

namespace AdaptiveAR.MR
{
    public class WorkspacePlacement : MonoBehaviour
    {
        public enum Provider { None = 0, EnvironmentDepth = 1, FallbackPlane = 2 }

        public enum State { Idle = 0, Placing = 1, Placed = 2 }

        [Header("Targets")]
        [Tooltip("The transform the ArUco marker used to drive: MarkerAnchor, the top of the " +
                 "anchor chain. Everything in the workspace hangs under it.")]
        [SerializeField] private Transform workspaceRoot;

        [Tooltip("Head transform. Camera.main when empty.")]
        [SerializeField] private Transform head;

        [Header("Placement, metres")]
        [Tooltip("Extra lift above the detected surface, so no part spawns inside the desk. " +
                 "The model is already grounded by CalibrationOffset; this is a safety margin.")]
        [SerializeField] private float surfaceUpOffset = 0.01f;

        [Tooltip("Rotation of the workspace about the surface normal, in degrees. Tune on the " +
                 "bench if the engine should face a different way relative to the participant.")]
        [SerializeField] private float yawOffsetDegrees = 0f;

        [Tooltip("Surfaces tilted more than this from horizontal are rejected as a workspace.")]
        [SerializeField] private float maxSurfaceTiltDegrees = 35f;

        [Tooltip("Furthest distance a surface may be placed at.")]
        [SerializeField] private float maxRayDistance = 4f;

        [Tooltip("How quickly the reticle glides to the latest hit. Depth hits jitter a little.")]
        [SerializeField] private float reticleSmoothing = 12f;

        [Header("Fallback plane")]
        [Tooltip("Use a horizontal plane when environment raycasting is unsupported or has " +
                 "produced no hit for a while.")]
        [SerializeField] private bool allowPlaneFallback = true;

        [Tooltip("Height of the fallback plane above the floor, metres. Tracking origin is " +
                 "floor level, so this is the real table height.")]
        [SerializeField] private float fallbackTableHeight = 0.72f;

        [Tooltip("Seconds without a depth hit before the plane is used, while depth is supported.")]
        [SerializeField] private float fallbackAfterSeconds = 4f;

        [Header("Input")]
        [Tooltip("Index trigger on either controller confirms a valid candidate.")]
        [SerializeField] private bool confirmWithTrigger = true;

        [Tooltip("Index pinch on the pointing hand confirms a valid candidate.")]
        [SerializeField] private bool confirmWithPinch = true;

        [Tooltip("Researcher control: starts a reposition of an already placed workspace.")]
        [SerializeField] private OVRInput.RawButton repositionButton = OVRInput.RawButton.RThumbstick;

        [SerializeField] private KeyCode editorRepositionKey = KeyCode.P;

        [Tooltip("Editor only: Space confirms, for play-mode testing without a headset.")]
        [SerializeField] private KeyCode editorConfirmKey = KeyCode.Space;

        [Header("Reticle")]
        [SerializeField] private float reticleDiameter = 0.16f;
        [SerializeField] private Color reticleColor = new Color(0.25f, 0.82f, 0.85f, 0.9f);
        [SerializeField] private Color reticleInvalidColor = new Color(0.95f, 0.65f, 0.2f, 0.9f);

        [Header("Behaviour")]
        [Tooltip("While placing, move the workspace root with the candidate so marker-relative " +
                 "UI floats over the reticle. The engine stays hidden until the first placement.")]
        [SerializeField] private bool previewMovesRoot = true;

        [Tooltip("Where the root sits while no surface has been found yet: in front of and " +
                 "below the head, so the placement panel is readable from the start.")]
        [SerializeField] private Vector3 idleOffsetFromHead = new Vector3(0f, -0.45f, 0.8f);

        [Header("Debug")]
        [SerializeField] private bool logChanges = true;

        // ---------------- public state ----------------

        public State Current { get; private set; } = State.Idle;
        public bool IsPlaced { get { return _hasPlacedOnce; } }
        public bool IsPlacing { get { return Current == State.Placing; } }

        /// <summary>Provider that produced the current candidate (or the last placement).</summary>
        public Provider ActiveProvider { get; private set; } = Provider.None;

        /// <summary>True when the current candidate can be confirmed.</summary>
        public bool HasValidCandidate { get; private set; }

        /// <summary>One-line, participant-safe status for the placement screen.</summary>
        public string StatusText { get; private set; } = "";

        /// <summary>Raised after a placement is applied. The bool is true for a reposition.</summary>
        public event Action<bool> OnPlaced;

        /// <summary>Raised when a reposition starts, so UI can explain what to do.</summary>
        public event Action OnRepositionStarted;

        public Vector3 LastPlacedPosition { get; private set; }
        public Vector3 LastSurfaceNormal { get; private set; } = Vector3.up;
        public float LastNormalConfidence { get; private set; }

        // ---------------- private ----------------

        private EnvironmentRaycastManager _envRaycast;
        private Meta.XR.EnvironmentDepth.EnvironmentDepthManager _depthManager;
        private bool _envSupported;
        private float _lastDepthHitTime = -999f;

        private Vector3 _candidatePoint;
        private Vector3 _candidateNormal = Vector3.up;
        private float _candidateConfidence;
        private bool _hasCandidate;

        private GameObject _reticle;
        private Material _reticleMaterial;
        private Vector3 _reticleSmoothed;
        private bool _reticleHasPos;

        private bool _hasPlacedOnce;
        private OVRCameraRig _rig;
        private OVRHand[] _hands;
        private bool _pinchWasDown;

        // =====================================================================
        // Setup
        // =====================================================================

        /// <summary>Used by StepManager when it creates this component at runtime.</summary>
        public void Configure(Transform root, Transform headTransform)
        {
            workspaceRoot = root;
            if (headTransform != null) head = headTransform;
        }

        private void Awake()
        {
            _rig = FindAnyObjectByType<OVRCameraRig>();
            _hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        private void Start()
        {
            if (head == null && Camera.main != null)
                head = Camera.main.transform;

            SetupEnvironmentRaycast();
            EnsureReticle();
            SetReticleVisible(false);
        }

        private void SetupEnvironmentRaycast()
        {
            _envSupported = false;
            try
            {
                _envSupported = EnvironmentRaycastManager.IsSupported;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WorkspacePlacement] Environment raycast support check threw: " + e.Message, this);
            }

            if (!_envSupported)
            {
                if (logChanges)
                    Debug.Log("[WorkspacePlacement] Environment depth raycast not supported here; " +
                              (allowPlaneFallback ? "using the fallback plane." : "placement will not find surfaces."));
                return;
            }

            _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);
            if (_envRaycast == null)
            {
                // The depth manager is added first so the raycast manager does not have to
                // add one itself and warn about it. Occlusion shading is left off: the scene's
                // materials do not implement the occlusion keyword, and placement only needs
                // the depth data for raycasting.
                var go = new GameObject("EnvironmentRaycast (runtime)");
                _depthManager = go.AddComponent<Meta.XR.EnvironmentDepth.EnvironmentDepthManager>();
                _depthManager.OcclusionShadersMode = Meta.XR.EnvironmentDepth.OcclusionShadersMode.None;
                _envRaycast = go.AddComponent<EnvironmentRaycastManager>();
            }
            else
            {
                _depthManager = FindAnyObjectByType<Meta.XR.EnvironmentDepth.EnvironmentDepthManager>(FindObjectsInactive.Include);
            }

            // Depth sensing costs GPU time; it runs only while a placement is in progress.
            SetDepthActive(Current == State.Placing);

            if (logChanges)
                Debug.Log("[WorkspacePlacement] Environment depth raycast available.");
        }

        private void SetDepthActive(bool active)
        {
            if (_envRaycast != null && _envRaycast.enabled != active) _envRaycast.enabled = active;
            if (_depthManager != null && _depthManager.enabled != active) _depthManager.enabled = active;
        }

        // =====================================================================
        // Flow
        // =====================================================================

        /// <summary>Enters placement mode. Safe to call repeatedly.</summary>
        public void BeginPlacement()
        {
            if (Current == State.Placing) return;

            Current = State.Placing;
            _hasCandidate = false;
            HasValidCandidate = false;
            _reticleHasPos = false;
            SetReticleVisible(true);
            SetDepthActive(true);

            if (_hasPlacedOnce) OnRepositionStarted?.Invoke();

            if (logChanges)
                Debug.Log("[WorkspacePlacement] " + (_hasPlacedOnce ? "Repositioning" : "Placing") + " the workspace.");
        }

        /// <summary>Researcher control: move an already placed workspace.</summary>
        public void BeginReposition()
        {
            if (!_hasPlacedOnce) { BeginPlacement(); return; }
            BeginPlacement();
        }

        /// <summary>
        /// Confirms the current candidate. Returns false, with StatusText explaining why,
        /// when there is nothing valid to confirm.
        /// </summary>
        public bool TryConfirm()
        {
            if (Current != State.Placing)
            {
                if (!_hasPlacedOnce) BeginPlacement();
                else return false;
            }

            if (!HasValidCandidate)
            {
                StatusText = _hasCandidate
                    ? "That surface is too steep. Point at a flat table."
                    : "No surface found yet. Point at the table.";
                return false;
            }

            bool reposition = _hasPlacedOnce;
            ApplyPose(_candidatePoint, _candidateNormal);

            LastPlacedPosition = _candidatePoint;
            LastSurfaceNormal = _candidateNormal;
            LastNormalConfidence = _candidateConfidence;

            Current = State.Placed;
            _hasPlacedOnce = true;
            SetReticleVisible(false);
            SetDepthActive(false);
            StatusText = "Workspace placed.";

            if (logChanges)
                Debug.Log($"[WorkspacePlacement] Workspace {(reposition ? "repositioned" : "placed")} at " +
                          $"{_candidatePoint} via {ActiveProvider} (normal confidence {_candidateConfidence:F2}).");

            OnPlaced?.Invoke(reposition);
            return true;
        }

        /// <summary>Leaves placement mode without applying anything.</summary>
        public void CancelPlacement()
        {
            if (Current != State.Placing) return;
            Current = _hasPlacedOnce ? State.Placed : State.Idle;
            SetReticleVisible(false);
            SetDepthActive(false);
        }

        private void Update()
        {
            // Reposition request, only once a workspace exists and the app is not placing.
            if (_hasPlacedOnce && Current == State.Placed
                && (OVRInput.GetDown(repositionButton) || Input.GetKeyDown(editorRepositionKey)))
            {
                BeginReposition();
                return;
            }

            if (Current != State.Placing) return;

            UpdateCandidate();
            UpdateReticle();
            UpdatePreviewRoot();
            UpdateStatus();
            HandleConfirmInput();
        }

        // =====================================================================
        // Candidate
        // =====================================================================

        private void UpdateCandidate()
        {
            if (!TryGetPointingRay(out Ray ray))
            {
                _hasCandidate = false;
                HasValidCandidate = false;
                return;
            }

            bool got = false;
            Vector3 p = Vector3.zero, n = Vector3.up;
            float conf = 0f;

            if (_envSupported && _envRaycast != null && _envRaycast.isActiveAndEnabled)
            {
                try
                {
                    if (_envRaycast.Raycast(ray, out EnvironmentRaycastHit hit, maxRayDistance)
                        && hit.status == EnvironmentRaycastHitStatus.Hit)
                    {
                        got = true;
                        p = hit.point;
                        n = hit.normalConfidence > 0.2f && hit.normal.sqrMagnitude > 0.5f ? hit.normal.normalized : Vector3.up;
                        conf = hit.normalConfidence;
                        _lastDepthHitTime = Time.time;
                        ActiveProvider = Provider.EnvironmentDepth;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[WorkspacePlacement] Environment raycast threw: " + e.Message, this);
                }
            }

            bool depthStale = !_envSupported || Time.time - _lastDepthHitTime > fallbackAfterSeconds;

            if (!got && allowPlaneFallback && depthStale)
            {
                // Intersect the ray with the horizontal plane at table height.
                float planeY = fallbackTableHeight;
                float dy = ray.direction.y;
                if (Mathf.Abs(dy) > 1e-4f)
                {
                    float t = (planeY - ray.origin.y) / dy;
                    if (t > 0.05f && t <= maxRayDistance)
                    {
                        got = true;
                        p = ray.origin + ray.direction * t;
                        n = Vector3.up;
                        conf = 0f;
                        ActiveProvider = Provider.FallbackPlane;
                    }
                }
            }

            _hasCandidate = got;
            if (got)
            {
                _candidatePoint = p;
                _candidateNormal = n;
                _candidateConfidence = conf;
            }

            HasValidCandidate = got && Vector3.Angle(n, Vector3.up) <= maxSurfaceTiltDegrees;
        }

        /// <summary>
        /// Pointing ray: a tracked controller first, then a hand with a valid pointer pose,
        /// then head gaze. All in world space.
        /// </summary>
        private bool TryGetPointingRay(out Ray ray)
        {
            Transform space = _rig != null ? _rig.trackingSpace : null;

            foreach (OVRInput.Controller c in new[] { OVRInput.Controller.RTouch, OVRInput.Controller.LTouch })
            {
                if (!OVRInput.IsControllerConnected(c) || !OVRInput.GetControllerPositionTracked(c)) continue;

                Vector3 lp = OVRInput.GetLocalControllerPosition(c);
                Quaternion lr = OVRInput.GetLocalControllerRotation(c);

                Vector3 wp = space != null ? space.TransformPoint(lp) : lp;
                Quaternion wr = space != null ? space.rotation * lr : lr;

                ray = new Ray(wp, wr * Vector3.forward);
                return true;
            }

            if (_hands != null)
            {
                foreach (OVRHand h in _hands)
                {
                    if (h == null || !h.isActiveAndEnabled || !h.IsTracked || !h.IsPointerPoseValid || h.PointerPose == null) continue;
                    ray = new Ray(h.PointerPose.position, h.PointerPose.forward);
                    return true;
                }
            }

            if (head != null)
            {
                ray = new Ray(head.position, head.forward);
                return true;
            }

            ray = default;
            return false;
        }

        private OVRHand PointingHand()
        {
            if (_hands == null) return null;
            foreach (OVRHand h in _hands)
                if (h != null && h.isActiveAndEnabled && h.IsTracked && h.IsPointerPoseValid) return h;
            return null;
        }

        private void HandleConfirmInput()
        {
            bool confirm = false;

            if (confirmWithTrigger && OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
                confirm = true;

            if (confirmWithPinch)
            {
                OVRHand h = PointingHand();
                bool pinch = h != null && h.GetFingerIsPinching(OVRHand.HandFinger.Index);
                if (pinch && !_pinchWasDown) confirm = true;
                _pinchWasDown = pinch;
            }

#if UNITY_EDITOR
            if (Input.GetKeyDown(editorConfirmKey)) confirm = true;
#endif

            if (confirm) TryConfirm();
        }

        // =====================================================================
        // Applying the pose
        // =====================================================================

        /// <summary>
        /// Poses the workspace root on the surface. MarkerAnchor convention: local +Z is the
        /// surface normal, local +Y points away from the viewer, yaw about the normal.
        /// </summary>
        private void ApplyPose(Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            if (workspaceRoot == null) return;

            Vector3 up = Vector3.up;   // a table is horizontal; a slightly noisy normal must not tilt the engine
            Vector3 away = Vector3.forward;

            if (head != null)
            {
                away = surfacePoint - head.position;
                away.y = 0f;
                if (away.sqrMagnitude < 1e-4f) away = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                if (away.sqrMagnitude < 1e-4f) away = Vector3.forward;
                away.Normalize();
            }

            Quaternion rot = Quaternion.LookRotation(up, away) * Quaternion.Euler(0f, 0f, yawOffsetDegrees);
            Vector3 pos = surfacePoint + Vector3.up * surfaceUpOffset;

            workspaceRoot.SetPositionAndRotation(pos, rot);
        }

        private void UpdatePreviewRoot()
        {
            if (!previewMovesRoot || workspaceRoot == null) return;

            // Once an engine is on the table, preview by moving it with the candidate so the
            // researcher sees where it will land. Before the first placement nothing is
            // visible under the root except the UI, which simply floats over the reticle.
            if (HasValidCandidate)
            {
                ApplyPose(_reticleHasPos ? _reticleSmoothed : _candidatePoint, _candidateNormal);
            }
            else if (!_hasPlacedOnce && head != null)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, fwd);

                Vector3 p = head.position + right * idleOffsetFromHead.x + Vector3.up * idleOffsetFromHead.y + fwd * idleOffsetFromHead.z;
                Quaternion rot = Quaternion.LookRotation(Vector3.up, fwd);
                workspaceRoot.SetPositionAndRotation(p, rot);
            }
        }

        private void UpdateStatus()
        {
            if (!_hasCandidate)
                StatusText = "Point at the table where you want to place the assembly workspace.";
            else if (!HasValidCandidate)
                StatusText = "That surface is too steep. Point at a flat table.";
            else if (ActiveProvider == Provider.FallbackPlane)
                StatusText = "Press Place Workspace when the ring is where you want the engine.\n" +
                             "(Surface sensing unavailable: using an estimated table height.)";
            else
                StatusText = "Press Place Workspace when the ring is where you want the engine.";
        }

        // =====================================================================
        // Reticle
        // =====================================================================

        private void EnsureReticle()
        {
            if (_reticle != null) return;

            _reticle = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _reticle.name = "WorkspaceReticle (runtime)";

            Collider col = _reticle.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Sprites/Default is in the always-included shader list, so it exists in the APK.
            // It is alpha blended and double sided, which is all a flat ring needs.
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            _reticleMaterial = new Material(shader);
            _reticleMaterial.mainTexture = BuildRingTexture(128);
            _reticleMaterial.color = reticleColor;

            var r = _reticle.GetComponent<MeshRenderer>();
            r.sharedMaterial = _reticleMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            _reticle.transform.localScale = Vector3.one * reticleDiameter;
        }

        private static Texture2D BuildRingTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float c = (size - 1) * 0.5f;
            float outer = size * 0.48f, inner = size * 0.36f, dot = size * 0.06f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c));
                float a = 0f;
                if (d <= outer && d >= inner) a = 1f;
                else if (d <= dot) a = 1f;

                // soft edges
                if (a > 0f)
                {
                    float edge = Mathf.Min(Mathf.Abs(d - outer), Mathf.Abs(d - inner));
                    if (d <= dot) edge = dot - d;
                    a *= Mathf.Clamp01(edge / 1.5f);
                }
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }

            tex.Apply();
            return tex;
        }

        private void UpdateReticle()
        {
            if (_reticle == null) return;

            if (!_hasCandidate)
            {
                _reticle.SetActive(false);
                return;
            }

            if (!_reticleHasPos)
            {
                _reticleSmoothed = _candidatePoint;
                _reticleHasPos = true;
            }
            else
            {
                _reticleSmoothed = Vector3.Lerp(_reticleSmoothed, _candidatePoint,
                                                1f - Mathf.Exp(-reticleSmoothing * Time.deltaTime));
            }

            _reticle.SetActive(true);
            _reticle.transform.position = _reticleSmoothed + _candidateNormal * 0.004f;
            _reticle.transform.rotation = Quaternion.FromToRotation(Vector3.forward, -_candidateNormal);

            if (_reticleMaterial != null)
                _reticleMaterial.color = HasValidCandidate ? reticleColor : reticleInvalidColor;
        }

        private void SetReticleVisible(bool visible)
        {
            if (_reticle != null) _reticle.SetActive(visible && _hasCandidate);
        }

        private void OnDestroy()
        {
            if (_reticle != null) Destroy(_reticle);
            if (_reticleMaterial != null) Destroy(_reticleMaterial);
        }
    }
}
