// File: StepValidator.cs
// Judges whether the active component is correctly placed, and makes the physical
// mating possible in the first place.
//
// Immediate snap
// --------------
// The participant does not release anything. The moment the correct part is inside
// position AND orientation tolerance - in the hand or not - it snaps to the exact
// target, the grab is ended, the part locks and joins its assembly, and success is
// raised once. Completion is guarded by a flag, so staying inside the zone cannot
// fire it twice.
//
// Per-role tolerance and symmetry
// -------------------------------
// Small parts are judged by their mechanically meaningful axes, not by raw quaternion
// distance. A pin or bolt is round: rotation about its own axis is ignored, and the
// axis may point either way. A rod or a cap is judged on its long axis plus roll
// modulo a half turn. The crank and cam rotate in their bearings, so roll about
// their axis is free but the axis direction must match. Anything without an entry
// uses the action's own tolerance with full orientation matching.
//
// Attempts and errors
// -------------------
// A placement ATTEMPT begins when the correct part enters the attempt zone around the
// target. It ends in success, or in exactly one failure: the part leaves the zone or
// is released inside it without reaching tolerance. The failure is classified from
// the closest the part got: never inside position tolerance -> incorrect_position;
// position reached but orientation never -> incorrect_orientation. Releasing the part
// far from the target is a drop, reported but not an error. Picking up a part that
// cannot satisfy the action is a wrong-component error, once per grab.
//
// Mating assist
// -------------
// Near the target (shape-to-shape) collisions between the moving part (with whatever
// is joined to it) and the receiving assembly are ignored so geometry can overlap.
// Bench furniture stays solid.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class StepValidator : MonoBehaviour
    {
        /// <summary>How a part's orientation is compared with its target.</summary>
        public enum AxisSymmetry
        {
            /// <summary>Full orientation must match (Quaternion angle).</summary>
            None = 0,
            /// <summary>Long axis must point the same way; rotation about it is free.</summary>
            AxialFree = 1,
            /// <summary>Long axis may point either way; rotation about it is free (pins, bolts).</summary>
            AxialFreeFlip = 2,
            /// <summary>Long axis must point the same way; roll about it matches modulo 180 degrees.</summary>
            AxialHalfTurn = 3
        }

        [Serializable]
        public class RoleTolerance
        {
            [Tooltip("Role name: the last segment of the part key (PistonHead, ConnectingPin, crankshaft ...).")]
            public string role;
            [Tooltip("Position tolerance, metres.")]
            public float positionMeters = 0.03f;
            [Tooltip("Orientation tolerance, degrees, under the symmetry rule.")]
            public float rotationDegrees = 30f;
            public AxisSymmetry symmetry = AxisSymmetry.None;
        }

        /// <summary>What the participant should do right now, derived from live validation state.</summary>
        public enum Cue { None = 0, MoveCloser = 1, TurnToMatch = 2, AlmostThere = 3 }

        [Header("References")]
        [SerializeField] private GuidanceRegistry guidanceRegistry;

        [Header("Behaviour")]
        [Tooltip("Snap, lock and succeed the moment the part is inside tolerance, without a release.")]
        [SerializeField] private bool snapImmediately = true;

        [Tooltip("Accept any unconsumed part of the same role from any kit, not only the " +
                 "instance the action names.")]
        [SerializeField] private bool interchangeableRoles = true;

        [Tooltip("Watch every other registered part so picking up a wrong one gives feedback.")]
        [SerializeField] private bool monitorWrongParts = true;

        [Tooltip("Metres a part must move from where it started to count as handled, for " +
                 "parts with no Interaction SDK grab state to read.")]
        [SerializeField] private float handledMoveThreshold = 0.05f;

        [Header("Tolerances by role (override the action's values)")]
        [SerializeField] private List<RoleTolerance> roleTolerances = DefaultRoleTolerances();

        [Tooltip("Revision of the default table above. When the code's defaults are newer than " +
                 "the values serialized in the scene, the table is reset to the defaults at start.")]
        [SerializeField] private int roleToleranceRevision = 0;
        private const int CurrentRoleToleranceRevision = 2;

        [Tooltip("Attempt zone radius as a multiple of the position tolerance.")]
        [SerializeField] private float attemptZoneMultiplier = 3f;

        [Tooltip("Smallest attempt zone radius, metres.")]
        [SerializeField] private float attemptZoneMinMeters = 0.10f;

        [Header("Guided snap assembly")]
        [Tooltip("Gap between the moving part's shape and the target's shape below which the " +
                 "mating assist is active. Shape-to-shape, so a long rod engages as its end " +
                 "reaches the head, not when its pivot does.")]
        [SerializeField] private float assistGapMeters = 0.06f;

        [Tooltip("Ignore collisions between the moving part and the receiving assembly while " +
                 "inside the assist gap, so mating geometry can overlap.")]
        [SerializeField] private bool suppressCollisionsNearTarget = true;

        [Tooltip("Objects whose name starts with any of these are never part of the receiving " +
                 "assembly: the moving part must still collide with them.")]
        [SerializeField] private string[] alwaysSolidNamePrefixes = { "tray", "Plane", "TabletopSupport", "WorkSurface" };

        [Header("Debug")]
        [SerializeField] private bool logEvaluations = true;

        // ---------------- events ----------------

        /// <summary>One judged attempt. (success, positionError, rotationError, trigger)</summary>
        public event Action<bool, float, float, string> OnAttemptEvaluated;

        /// <summary>Fired once when the current action's placement is accepted.</summary>
        public event Action OnStepValidated;

        /// <summary>An accepted part was picked up / let go. (instance key, part)</summary>
        public event Action<string, Transform> OnPartGrabbed;
        public event Action<string, Transform> OnPartReleased;

        /// <summary>An accepted part was released far from the target. Not an error.</summary>
        public event Action<string, Transform> OnComponentDropped;

        /// <summary>A part that does NOT satisfy the current action was picked up / let go.</summary>
        public event Action<string, Transform> OnWrongPartGrabbed;
        public event Action<string, Transform> OnWrongPartReleased;

        // ---------------- state ----------------

        public bool IsActive { get; private set; }
        public int AttemptCount { get; private set; }
        public string PartKey { get; private set; }
        public string TargetKey { get; private set; }
        public string ActiveInstanceKey { get { return _active != null ? _active.key : null; } }
        public AssemblyAction CurrentAction { get { return _action; } }
        public Transform CurrentPart { get; private set; }
        public Transform CurrentTarget { get; private set; }
        public bool HasBeenHandled { get; private set; }
        public bool IsAssisting { get; private set; }
        public bool WrongPartHeld { get { return _wrongHeld != null; } }

        /// <summary>
        /// Why the last attempt failed: "incorrect_position", "incorrect_orientation" or
        /// "incorrect_position_and_orientation". Null after a success.
        /// </summary>
        public string LastErrorType { get; private set; }

        /// <summary>Per-dimension validity of the last judged attempt.</summary>
        public bool LastAttemptPositionOk { get; private set; }
        public bool LastAttemptOrientationOk { get; private set; }

        /// <summary>Legacy reject reason, derived from LastErrorType.</summary>
        public RejectReason LastRejectReason { get; private set; }

        /// <summary>Live participant cue derived from the current state.</summary>
        public Cue CurrentCue { get; private set; }

        /// <summary>Effective tolerances for the current action.</summary>
        public float PositionTolerance { get { return _posTol; } }
        public float RotationTolerance { get { return _rotTol; } }
        public AxisSymmetry CurrentSymmetry { get { return _symmetry; } }

        private class Candidate
        {
            public string key;
            public Transform tf;
            public Rigidbody body;
            public Oculus.Interaction.PointableElement pointable;
            public Vector3 startPos;
            public bool wasKinematic;
            public bool wasHeld;
            public bool moved;
            public float radius;
            public Vector3 axisLocal;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly List<Candidate> _others = new List<Candidate>();
        private readonly HashSet<string> _consumed = new HashSet<string>();

        private Candidate _active;
        private Candidate _wrongHeld;
        private AssemblyAction _action;
        private bool _completed;
        private float _targetRadius;
        private float _posTol, _rotTol, _attemptZone;
        private AxisSymmetry _symmetry;

        // attempt tracking
        private bool _inAttempt;
        private float _attemptBestPos;
        private float _attemptRotAtBestPos;
        private bool _attemptReachedPosition;
        private float _attemptBestRotWhenPositioned;
        private bool _stickyAlmostThere;

        // assist state
        private readonly List<Collider> _receiving = new List<Collider>();
        private readonly List<Collider> _activeColliders = new List<Collider>();
        private Candidate _assistFor;

        // =====================================================================
        // Defaults
        // =====================================================================

        private static List<RoleTolerance> DefaultRoleTolerances()
        {
            return new List<RoleTolerance>
            {
                // Revision 2: middle ground between the first (too strict) and second (too
                // forgiving) builds. Starting calibration values, tune on the bench.
                new RoleTolerance { role = "PistonHead",      positionMeters = 0.025f, rotationDegrees = 22f, symmetry = AxisSymmetry.None },
                new RoleTolerance { role = "ConnectingRod",   positionMeters = 0.025f, rotationDegrees = 22f, symmetry = AxisSymmetry.AxialHalfTurn },
                new RoleTolerance { role = "ConnectingPin",   positionMeters = 0.020f, rotationDegrees = 28f, symmetry = AxisSymmetry.AxialFreeFlip },
                new RoleTolerance { role = "PistonEnd",       positionMeters = 0.025f, rotationDegrees = 25f, symmetry = AxisSymmetry.AxialHalfTurn },
                new RoleTolerance { role = "pistonBolt",      positionMeters = 0.020f, rotationDegrees = 28f, symmetry = AxisSymmetry.AxialFreeFlip },
                new RoleTolerance { role = "pistonBoltOther", positionMeters = 0.020f, rotationDegrees = 28f, symmetry = AxisSymmetry.AxialFreeFlip },
                new RoleTolerance { role = "PistonNut",       positionMeters = 0.020f, rotationDegrees = 28f, symmetry = AxisSymmetry.AxialFreeFlip },
                new RoleTolerance { role = "PistonNutOther",  positionMeters = 0.020f, rotationDegrees = 28f, symmetry = AxisSymmetry.AxialFreeFlip },
                new RoleTolerance { role = "crankshaft",      positionMeters = 0.030f, rotationDegrees = 20f, symmetry = AxisSymmetry.AxialFree },
                new RoleTolerance { role = "camshaft",        positionMeters = 0.030f, rotationDegrees = 20f, symmetry = AxisSymmetry.AxialFree },
            };
        }

        /// <summary>The role name used for tolerance lookup: the last segment of the key.</summary>
        public static string RoleNameOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string role = GuidanceRegistry.RoleOf(key);
            if (role != null) return role;
            int i = key.LastIndexOf('.');
            return i >= 0 ? key.Substring(i + 1) : key;
        }

        private RoleTolerance FindRole(string key)
        {
            string role = RoleNameOf(key);
            if (role == null || roleTolerances == null) return null;
            foreach (RoleTolerance r in roleTolerances)
                if (r != null && r.role == role) return r;
            return null;
        }

        // =====================================================================
        // Arming
        // =====================================================================

        public void ResetConsumedParts()
        {
            _consumed.Clear();
        }

        private void Awake()
        {
            if (roleToleranceRevision < CurrentRoleToleranceRevision)
            {
                roleTolerances = DefaultRoleTolerances();
                roleToleranceRevision = CurrentRoleToleranceRevision;
                Debug.Log($"[StepValidator] Role tolerance table reset to revision {CurrentRoleToleranceRevision} defaults.");
            }
        }

        public bool BeginAction(AssemblyAction action)
        {
            Clear();
            _action = action;

            if (action == null || !action.RequiresPhysicalValidation)
                return false;

            PartKey = action.partKey;
            TargetKey = action.targetKey;

            // Tolerance: role entry wins, action values are the fallback.
            RoleTolerance rt = FindRole(action.partKey);
            _posTol = rt != null ? rt.positionMeters : action.positionToleranceMeters;
            _rotTol = rt != null ? rt.rotationDegrees : action.rotationToleranceDegrees;
            _symmetry = rt != null ? rt.symmetry : AxisSymmetry.None;
            _attemptZone = Mathf.Max(attemptZoneMinMeters, _posTol * attemptZoneMultiplier);

            if (guidanceRegistry == null)
            {
                Debug.LogWarning("[StepValidator] No GuidanceRegistry assigned; validation disabled.", this);
                return false;
            }

            if (string.IsNullOrEmpty(TargetKey) ||
                !guidanceRegistry.TryResolve(TargetKey, out GameObject targetGo))
            {
                Debug.LogWarning($"[StepValidator] Action '{action.Id}': target '{TargetKey}' " +
                                 "could not be resolved; this action cannot be validated.", this);
                return false;
            }

            CurrentTarget = targetGo.transform;
            _targetRadius = ShapeRadius(CurrentTarget);

            var accepted = new List<string>();
            foreach (string key in action.AcceptedPartKeys())
            {
                List<string> expanded = interchangeableRoles ? guidanceRegistry.RoleCandidates(key) : new List<string> { key };
                foreach (string k in expanded)
                    if (!accepted.Contains(k)) accepted.Add(k);
            }

            var acceptedSet = new HashSet<string>();
            foreach (string key in accepted)
            {
                if (_consumed.Contains(key)) continue;
                if (!guidanceRegistry.TryResolveQuiet(key, out GameObject go)) continue;

                // Installing a finished kit: its handle was locked when it was built; this is
                // the action that moves it, so it becomes grabbable again here.
                if (GuidanceRegistry.IsKitHandleKey(key))
                {
                    var padlock = go.GetComponent<PlacementLock>();
                    if (padlock != null && padlock.IsLocked)
                    {
                        padlock.Unlock();
                        if (logEvaluations)
                            Debug.Log($"[StepValidator] Kit handle '{key}' ({go.name}) unlocked for installation.");
                    }
                }

                _candidates.Add(MakeCandidate(key, go));
                acceptedSet.Add(key);
            }

            if (_candidates.Count == 0)
            {
                Debug.LogWarning($"[StepValidator] Action '{action.Id}': no accepted part could be " +
                                 "resolved; this action cannot be validated.", this);
                return false;
            }

            if (monitorWrongParts)
            {
                foreach (string key in guidanceRegistry.PartKeys())
                {
                    if (acceptedSet.Contains(key)) continue;
                    if (!guidanceRegistry.TryResolveQuiet(key, out GameObject go) || go == null) continue;
                    if (go.GetComponent<Oculus.Interaction.PointableElement>() == null) continue;

                    var padlock = go.GetComponent<PlacementLock>();
                    if (padlock != null && padlock.IsLocked) continue;

                    _others.Add(MakeCandidate(key, go));
                }
            }

            _active = _candidates[0];
            CurrentPart = _active.tf;
            IsActive = true;
            _completed = false;
            CurrentCue = Cue.None;
            LastErrorType = null;

            // Feedback reflects what is in the hand NOW, including across an action change.
            foreach (Candidate c in _others)
            {
                if (!c.wasHeld) continue;
                _wrongHeld = c;
                OnWrongPartGrabbed?.Invoke(c.key, c.tf);
                break;
            }

            if (logEvaluations)
                Debug.Log($"[StepValidator] '{action.Id}': {_candidates.Count} accepted instance(s) for '{PartKey}' " +
                          $"vs '{TargetKey}'; tolerance {_posTol * 100f:F1} cm / {_rotTol:F0} deg ({_symmetry}); " +
                          $"attempt zone {_attemptZone * 100f:F0} cm; assist gap {assistGapMeters * 100f:F0} cm; " +
                          $"{_others.Count} other part(s) watched.");

            return true;
        }

        private static Candidate MakeCandidate(string key, GameObject go)
        {
            Rigidbody body = go.GetComponent<Rigidbody>();
            var pointable = go.GetComponent<Oculus.Interaction.PointableElement>();
            return new Candidate
            {
                key = key,
                tf = go.transform,
                body = body,
                pointable = pointable,
                startPos = go.transform.position,
                wasKinematic = body != null && body.isKinematic,
                wasHeld = pointable != null && pointable.SelectingPointsCount > 0,
                radius = ShapeRadius(go.transform),
                axisLocal = LongAxisLocal(go.transform)
            };
        }

        private static float ShapeRadius(Transform t)
        {
            if (t == null) return 0f;
            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return 0f;
            Vector3 e = Vector3.Scale(mf.sharedMesh.bounds.extents, t.lossyScale);
            return new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z)).magnitude;
        }

        /// <summary>The part's longest extent, in its own local space: its mechanical axis.</summary>
        private static Vector3 LongAxisLocal(Transform t)
        {
            if (t == null) return Vector3.forward;
            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return Vector3.forward;
            Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, t.localScale);
            s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            if (s.x >= s.y && s.x >= s.z) return Vector3.right;
            if (s.y >= s.x && s.y >= s.z) return Vector3.up;
            return Vector3.forward;
        }

        public void Clear()
        {
            DisengageAssist();

            if (_wrongHeld != null)
            {
                Candidate w = _wrongHeld;
                _wrongHeld = null;
                OnWrongPartReleased?.Invoke(w.key, w.tf);
            }

            IsActive = false;
            _completed = false;
            _candidates.Clear();
            _others.Clear();
            _active = null;
            _action = null;
            CurrentPart = null;
            CurrentTarget = null;
            HasBeenHandled = false;
            AttemptCount = 0;
            PartKey = null;
            TargetKey = null;
            CurrentCue = Cue.None;
            _inAttempt = false;
            _stickyAlmostThere = false;
            _receiving.Clear();
        }

        // =====================================================================
        // Per frame
        // =====================================================================

        private void Update()
        {
            if (!IsActive || _completed || CurrentTarget == null) return;

            bool anyHeld = false;
            Candidate releasedNow = null;
            Candidate grabbedNow = null;

            foreach (Candidate c in _candidates)
            {
                if (c.tf == null) continue;

                bool held = IsHeld(c);
                if (c.wasHeld && !held) releasedNow = c;
                if (!c.wasHeld && held) grabbedNow = c;
                c.wasHeld = held;
                if (held) anyHeld = true;

                if (held) c.moved = true;
                else if (Vector3.Distance(c.tf.position, c.startPos) > handledMoveThreshold) c.moved = true;
            }

            MonitorWrongParts();

            Candidate chosen = PickActive();
            if (chosen != null && chosen != _active)
            {
                _active = chosen;
                CurrentPart = chosen.tf;
                DisengageAssist();
                _inAttempt = false;
            }

            HasBeenHandled = _active != null && _active.moved;

            if (grabbedNow != null)
            {
                _stickyAlmostThere = false;
                OnPartGrabbed?.Invoke(grabbedNow.key, grabbedNow.tf);
                // Picking the part up again inside the zone starts a fresh attempt.
                if (grabbedNow == _active) _inAttempt = false;
            }

            UpdateAssist();

            if (_active == null || _active.tf == null) return;

            bool ok = WithinTolerance(_active, out float posErr, out float rotErr);
            bool inZone = posErr <= _attemptZone;

            // --- attempt lifecycle: enter zone -> success | leave zone | release inside ---
            if (inZone && !_inAttempt && (anyHeld || HasBeenHandled))
            {
                _inAttempt = true;
                _attemptBestPos = float.MaxValue;
                _attemptRotAtBestPos = float.MaxValue;
                _attemptReachedPosition = false;
                _attemptBestRotWhenPositioned = float.MaxValue;
            }

            if (_inAttempt)
            {
                if (posErr < _attemptBestPos) { _attemptBestPos = posErr; _attemptRotAtBestPos = rotErr; }
                if (posErr <= _posTol)
                {
                    _attemptReachedPosition = true;
                    _attemptBestRotWhenPositioned = Mathf.Min(_attemptBestRotWhenPositioned, rotErr);
                }
            }

            if (ok && (snapImmediately || !anyHeld))
            {
                Succeed(anyHeld ? "aligned_in_hand" : "aligned", posErr, rotErr);
                return;
            }

            if (releasedNow == _active)
            {
                if (_inAttempt && inZone)
                {
                    FailAttempt("released_in_zone");
                    _stickyAlmostThere = true;
                    DisengageAssist();
                }
                else if (!inZone)
                {
                    OnComponentDropped?.Invoke(_active.key, _active.tf);
                    if (logEvaluations) Debug.Log($"[StepValidator] '{_active.key}' dropped away from the target (not an error).");
                }
            }
            else if (_inAttempt && !inZone)
            {
                FailAttempt("left_zone");
            }

            // --- live cue ---
            if (!inZone) CurrentCue = _stickyAlmostThere && !anyHeld ? Cue.AlmostThere : Cue.None;
            else if (posErr > _posTol) CurrentCue = Cue.MoveCloser;
            else CurrentCue = Cue.TurnToMatch;
        }

        private void MonitorWrongParts()
        {
            if (_others.Count == 0) return;

            foreach (Candidate c in _others)
            {
                if (c.tf == null) continue;
                bool held = IsHeld(c);

                if (held && !c.wasHeld)
                {
                    _wrongHeld = c;
                    OnWrongPartGrabbed?.Invoke(c.key, c.tf);
                }
                else if (!held && c.wasHeld && _wrongHeld == c)
                {
                    _wrongHeld = null;
                    OnWrongPartReleased?.Invoke(c.key, c.tf);
                }

                c.wasHeld = held;
            }
        }

        private static bool IsHeld(Candidate c)
        {
            if (c.pointable != null)
                return c.pointable.SelectingPointsCount > 0;

            return c.body != null && c.body.isKinematic && !c.wasKinematic;
        }

        private Candidate PickActive()
        {
            Candidate best = null;
            float bestScore = float.MaxValue;

            foreach (Candidate c in _candidates)
            {
                if (c.tf == null) continue;
                float d = Vector3.Distance(c.tf.position, CurrentTarget.position);
                float score = c.moved ? d : d + 1000f;
                if (IsHeld(c)) score -= 2000f;            // the one in the hand always wins
                if (score < bestScore) { bestScore = score; best = c; }
            }

            return best;
        }

        // =====================================================================
        // Tolerance and symmetry
        // =====================================================================

        private bool WithinTolerance(Candidate c, out float positionError, out float rotationError)
        {
            positionError = Vector3.Distance(c.tf.position, CurrentTarget.position);
            rotationError = OrientationError(c);
            return positionError <= _posTol && rotationError <= _rotTol;
        }

        /// <summary>
        /// Orientation error under the role's symmetry rule. Axis modes compare the part's
        /// long axis with the target's; roll about that axis is ignored or taken modulo a
        /// half turn as the rule says.
        /// </summary>
        private float OrientationError(Candidate c)
        {
            if (_symmetry == AxisSymmetry.None)
                return Quaternion.Angle(c.tf.rotation, CurrentTarget.rotation);

            Vector3 partAxis = c.tf.rotation * c.axisLocal;
            Vector3 targetAxis = CurrentTarget.rotation * c.axisLocal;   // same mesh, same local axis

            float axisAngle = Vector3.Angle(partAxis, targetAxis);
            if (_symmetry == AxisSymmetry.AxialFreeFlip)
                axisAngle = Mathf.Min(axisAngle, 180f - axisAngle);

            if (_symmetry != AxisSymmetry.AxialHalfTurn)
                return axisAngle;

            // Roll: what is left after the axes are aligned, measured about that axis.
            Quaternion alignAxes = Quaternion.FromToRotation(partAxis, targetAxis);
            Quaternion residual = Quaternion.Inverse(CurrentTarget.rotation) * (alignAxes * c.tf.rotation);
            float roll = Quaternion.Angle(residual, Quaternion.identity);   // 0..180
            float rollHalf = Mathf.Min(roll, 180f - roll);                  // half-turn symmetric
            return Mathf.Max(axisAngle, rollHalf);
        }

        private bool IsNearTarget(Candidate c)
        {
            if (c == null || c.tf == null || CurrentTarget == null) return false;
            float pivotDistance = Vector3.Distance(c.tf.position, CurrentTarget.position);
            float gap = pivotDistance - c.radius - _targetRadius;
            float threshold = Mathf.Max(assistGapMeters, _posTol * 1.5f);
            return gap <= threshold;
        }

        // =====================================================================
        // Outcomes
        // =====================================================================

        private void FailAttempt(string trigger)
        {
            if (!_inAttempt) return;
            _inAttempt = false;
            AttemptCount++;

            float pos = _attemptBestPos == float.MaxValue ? 0f : _attemptBestPos;
            float rot = _attemptReachedPosition ? _attemptBestRotWhenPositioned : _attemptRotAtBestPos;
            if (rot == float.MaxValue) rot = 0f;

            // Both dimensions, judged at the closest approach.
            LastAttemptPositionOk = _attemptReachedPosition;
            LastAttemptOrientationOk = rot <= _rotTol;
            if (!LastAttemptPositionOk && !LastAttemptOrientationOk) LastErrorType = "incorrect_position_and_orientation";
            else if (!LastAttemptPositionOk) LastErrorType = "incorrect_position";
            else LastErrorType = "incorrect_orientation";
            LastRejectReason = LastAttemptPositionOk ? RejectReason.WrongRotation : RejectReason.TooFar;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Attempt {AttemptCount} FAILED ({trigger}) on '{_active.key}': {LastErrorType}; " +
                          $"closest {pos * 100f:F1} cm, orientation {rot:F0} deg (tol {_posTol * 100f:F1} cm / {_rotTol:F0} deg).");

            OnAttemptEvaluated?.Invoke(false, pos, rot, trigger);
        }

        private void Succeed(string trigger, float posErr, float rotErr)
        {
            if (_completed || _active == null || _active.tf == null) return;

            _completed = true;
            _inAttempt = false;
            IsActive = false;
            AttemptCount++;
            LastErrorType = null;
            LastAttemptPositionOk = true;
            LastAttemptOrientationOk = true;
            LastRejectReason = RejectReason.None;
            CurrentCue = Cue.None;
            _consumed.Add(_active.key);

            if (logEvaluations)
                Debug.Log($"[StepValidator] Attempt {AttemptCount} ACCEPTED ({trigger}) on '{_active.key}': " +
                          $"off by {posErr * 100f:F1} cm / {rotErr:F0} deg.");

            // 1. exact pose, 2. grab ended + locked (PlacementLock disables the SDK components
            //    and re-asserts the pose for a few frames), 3. joined to its assembly.
            SnapToTarget(_active);

            var padlock = _active.tf.GetComponent<PlacementLock>();
            if (padlock == null) padlock = _active.tf.gameObject.AddComponent<PlacementLock>();
            padlock.LockAt(CurrentTarget);

            BindAndJoin(_active);
            padlock.RefreshLockedPose();

            // Collisions with the assembly stay ignored for this part: it lives inside it now.
            _assistFor = null;
            IsAssisting = false;

            OnAttemptEvaluated?.Invoke(true, posErr, rotErr, trigger);
            OnStepValidated?.Invoke();
        }

        private void SnapToTarget(Candidate c)
        {
            if (c.body != null)
            {
                c.body.linearVelocity = Vector3.zero;
                c.body.angularVelocity = Vector3.zero;
                c.body.isKinematic = true;
            }

            c.tf.SetPositionAndRotation(CurrentTarget.position, CurrentTarget.rotation);
        }

        private void BindAndJoin(Candidate c)
        {
            if (guidanceRegistry == null || c == null || c.tf == null) return;

            string kit = GuidanceRegistry.KitOf(PartKey);
            string role = GuidanceRegistry.RoleOf(c.key);
            if (kit == null || role == null) return;

            if (role == guidanceRegistry.KitHandleRole)
            {
                guidanceRegistry.BindKitHandle(kit, c.tf);
                if (logEvaluations) Debug.Log($"[StepValidator] '{c.key}' is now the handle of {kit}.");
                return;
            }

            if (!guidanceRegistry.TryGetKitHandle(kit, out Transform handle) || handle == null) return;
            if (handle == c.tf || c.tf.IsChildOf(handle)) return;

            c.tf.SetParent(handle, true);
            if (logEvaluations) Debug.Log($"[StepValidator] '{c.key}' joined {kit} under '{handle.name}'.");
        }

        // =====================================================================
        // Mating assist
        // =====================================================================

        private void UpdateAssist()
        {
            if (!suppressCollisionsNearTarget || _active == null || _active.tf == null)
            {
                if (IsAssisting) DisengageAssist();
                return;
            }

            bool near = IsNearTarget(_active);

            if (near && (!IsAssisting || _assistFor != _active))
                EngageAssist(_active);
            else if (!near && IsAssisting)
                DisengageAssist();
        }

        private void EngageAssist(Candidate c)
        {
            DisengageAssist();
            BuildReceivingSet(c);

            _activeColliders.Clear();
            foreach (Collider col in c.tf.GetComponentsInChildren<Collider>(true))
                if (col != null && !col.isTrigger) _activeColliders.Add(col);

            foreach (Collider a in _activeColliders)
                foreach (Collider r in _receiving)
                    if (a != null && r != null) Physics.IgnoreCollision(a, r, true);

            _assistFor = c;
            IsAssisting = true;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Mating assist ON for '{c.key}': ignoring {_receiving.Count} receiving collider(s).");
        }

        private void DisengageAssist()
        {
            if (!IsAssisting) return;

            foreach (Collider a in _activeColliders)
                foreach (Collider r in _receiving)
                    if (a != null && r != null) Physics.IgnoreCollision(a, r, false);

            if (logEvaluations && _assistFor != null)
                Debug.Log($"[StepValidator] Mating assist OFF for '{_assistFor.key}'.");

            _activeColliders.Clear();
            _assistFor = null;
            IsAssisting = false;
        }

        private void BuildReceivingSet(Candidate c)
        {
            _receiving.Clear();
            if (CurrentTarget == null) return;

            Transform root = CurrentTarget.root;
            foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || col.isTrigger) continue;
                if (col.transform == c.tf || col.transform.IsChildOf(c.tf)) continue;
                if (IsAlwaysSolid(col.transform)) continue;
                _receiving.Add(col);
            }
        }

        private bool IsAlwaysSolid(Transform t)
        {
            if (t != null && t.GetComponentInParent<AdaptiveAR.MR.AssemblyWorkSurface>() != null) return true;

            if (alwaysSolidNamePrefixes == null) return false;
            while (t != null)
            {
                foreach (string prefix in alwaysSolidNamePrefixes)
                    if (!string.IsNullOrEmpty(prefix) && t.name.StartsWith(prefix)) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>Current placement error under the role's rule, for the researcher readout.</summary>
        public void GetCurrentError(out float positionError, out float rotationError)
        {
            positionError = 0f;
            rotationError = 0f;

            if (!IsActive || _active == null || _active.tf == null || CurrentTarget == null) return;

            positionError = Vector3.Distance(_active.tf.position, CurrentTarget.position);
            rotationError = OrientationError(_active);
        }
    }
}
