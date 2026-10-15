// File: StepValidator.cs
// Judges whether the active component is correctly placed, and makes the physical
// mating possible in the first place.
//
// Grab event source (one for the whole session)
// ---------------------------------------------
// Every selectable part - registered or not - is watched from the moment the first
// action is armed: its Interaction SDK PointableElement raises a pointer event on
// Select, and SelectingPointsCount is polled every frame as a fallback. Either way a
// grab is reported ONCE per episode and logged before any eligibility logic:
//   [GrabEvent] RECEIVED object= id= action= source=isdk_event|poll
//   [Eligibility] object= requested= eligible= reason=
//   [WrongPart] FIRING object= action=
// Eligibility is judged against whatever is armed right now: the accepted instances
// of a PHYSICAL action, or the named part of a READ instruction ("pick it up"). So a
// wrong part picked up while READING an instruction is reported exactly like one
// picked up during a placement. With nothing armed the grab is still logged.
//
// Immediate snap
// --------------
// The participant does not release anything. The moment the correct part is inside
// position AND orientation tolerance - in the hand or not - it snaps to the exact
// target, the grab is ended, the part locks and joins its assembly, and success is
// raised once. Completion is guarded by a flag, so staying inside the zone cannot
// fire it twice. Every stage of that path is logged and isolated: a failure to snap
// or lock is an error, never a silently dead action.
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

        [Tooltip("Watch every selectable part so picking up a wrong one gives feedback.")]
        [SerializeField] private bool monitorWrongParts = true;

        [Tooltip("Metres a part must move from where it started to count as handled, for " +
                 "parts with no Interaction SDK grab state to read.")]
        [SerializeField] private float handledMoveThreshold = 0.05f;

        [Header("Grab event source")]
        [Tooltip("Subscribe to each selectable part's Interaction SDK pointer events. Polling of " +
                 "SelectingPointsCount stays on as a fallback; a grab is reported once either way.")]
        [SerializeField] private bool eventDrivenGrabs = true;

        [Tooltip("Seconds between live validation log lines while the part is handled or near " +
                 "the target (about 4 Hz). Elsewhere one line per second proves the loop runs.")]
        [SerializeField] private float liveLogInterval = 0.25f;

        [Header("Tolerances by role (override the action's values)")]
        [SerializeField] private List<RoleTolerance> roleTolerances = DefaultRoleTolerances();

        [Tooltip("Revision of the default table above. When the code's defaults are newer than " +
                 "the values serialized in the scene, the table is reset to the defaults at start.")]
        [SerializeField] private int roleToleranceRevision = 0;
        private const int CurrentRoleToleranceRevision = 3;

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

        /// <summary>During an instruction (READ) the named part was picked up: the instruction is done.</summary>
        public event Action<string, Transform> OnAcknowledgedByGrab;

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

        /// <summary>True when the last failed attempt never came close (clearly wrong pose, red cue).</summary>
        public bool LastAttemptFar { get; private set; }

        /// <summary>Why the last BeginAction could not arm, for the action log and the self-check.</summary>
        public string ArmFailureReason { get; private set; }

        /// <summary>What the last Succeed() actually achieved, for the success log line.</summary>
        public bool LastSuccessSnapped { get; private set; }
        public bool LastSuccessLocked { get; private set; }

        /// <summary>Number of selectable parts watched by the grab event source (session-wide).</summary>
        public int GrabSourceCount { get { return _watchedList.Count; } }

        /// <summary>Selectable parts watched that are NOT accepted by the current action.</summary>
        public int WatchedOtherCount
        {
            get
            {
                int n = 0;
                foreach (Candidate c in _watchedList)
                    if (c.tf != null && !_acceptedObjects.Contains(c.tf.gameObject)) n++;
                return n;
            }
        }

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

            // grab event source
            public Action<Oculus.Interaction.PointerEvent> handler;
            public bool pendingGrab;
            public bool pendingRelease;
            public string grabSource;
            public string releaseSource;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly HashSet<GameObject> _acceptedObjects = new HashSet<GameObject>();
        private readonly HashSet<string> _consumed = new HashSet<string>();

        // READ-state pick-up monitor (instruction completes when the named part is grabbed)
        private readonly HashSet<GameObject> _ackObjects = new HashSet<GameObject>();
        private AssemblyAction _ackAction;
        private string _ackPartKey;
        private bool _ackArmed;

        // Session-wide grab event source: every selectable part, registered or not.
        private readonly Dictionary<GameObject, Candidate> _watched = new Dictionary<GameObject, Candidate>();
        private readonly List<Candidate> _watchedList = new List<Candidate>();

        private Candidate _active;
        private Candidate _wrongHeld;
        private AssemblyAction _action;
        private bool _completed;
        private float _targetRadius;
        private float _posTol, _rotTol, _attemptZone;
        private AxisSymmetry _symmetry;
        private float _nextLiveLog;

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
                // Revision 3: nuts loosened specifically (tiny objects); everything else as rev 2.
                new RoleTolerance { role = "PistonNut",       positionMeters = 0.028f, rotationDegrees = 32f, symmetry = AxisSymmetry.AxialFreeFlip },
                new RoleTolerance { role = "PistonNutOther",  positionMeters = 0.028f, rotationDegrees = 32f, symmetry = AxisSymmetry.AxialFreeFlip },
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

        /// <summary>
        /// Role prefix for log tags: "Crankshaft", "PistonHead", "ConnectingRod" ... so the
        /// crankshaft lines read [CrankshaftValidation] and the head lines [PistonHeadValidation].
        /// </summary>
        public static string RoleTag(string partKey)
        {
            string role = RoleNameOf(partKey);
            if (string.IsNullOrEmpty(role)) return "Part";
            return char.ToUpperInvariant(role[0]) + role.Substring(1);
        }

        private string Tag(string what)
        {
            return "[" + RoleTag(PartKey) + what + "]";
        }

        private AdaptiveAR.MR.AssemblyWorkSurface _workSurface;

        /// <summary>
        /// Head-on-the-mat actions validate against the mat's ONE head target, which is also
        /// where the ghost is posed and where the part snaps. Null for every other action.
        /// </summary>
        private Transform MatHeadTargetFor(AssemblyAction action)
        {
            if (action == null || string.IsNullOrEmpty(action.targetKey) || !action.targetKey.StartsWith("ghost.PistonKit")) return null;
            if (RoleNameOf(action.partKey) != "PistonHead") return null;
            if (_workSurface == null) _workSurface = FindAnyObjectByType<AdaptiveAR.MR.AssemblyWorkSurface>(FindObjectsInactive.Include);
            if (_workSurface == null || !_workSurface.HeadTargetValid || _workSurface.HeadTarget == null) return null;
            return _workSurface.HeadTarget;
        }

        private static string Describe(Transform t)
        {
            if (t == null) return "<null>";
            return $"{t.name}#{t.GetInstanceID()}";
        }

        private static string PathOf(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new System.Text.StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        // =====================================================================
        // Arming
        // =====================================================================

        public void ResetConsumedParts()
        {
            _consumed.Clear();
        }

        /// <summary>Marks an instance as used (the auto-complete shortcut uses this).</summary>
        public void MarkConsumed(string instanceKey)
        {
            if (!string.IsNullOrEmpty(instanceKey)) _consumed.Add(instanceKey);
        }

        public bool IsConsumed(string instanceKey)
        {
            return !string.IsNullOrEmpty(instanceKey) && _consumed.Contains(instanceKey);
        }

        /// <summary>True when the grab event source watches this object.</summary>
        public bool IsWatched(GameObject go)
        {
            return go != null && _watched.ContainsKey(go);
        }

        /// <summary>
        /// Arms a light pick-up monitor for an instruction: when any unconsumed instance of
        /// the named role is grabbed, OnAcknowledgedByGrab fires once. Any OTHER selectable
        /// part grabbed meanwhile is a wrong component, reported like during a placement.
        /// </summary>
        public bool BeginAcknowledge(AssemblyAction action)
        {
            _ackObjects.Clear();
            _ackArmed = false;
            _ackAction = action;
            _ackPartKey = action != null ? action.partKey : null;
            if (action == null || string.IsNullOrEmpty(action.partKey) || guidanceRegistry == null) return false;

            foreach (string key in guidanceRegistry.RoleCandidates(action.partKey))
            {
                if (_consumed.Contains(key)) continue;
                if (!guidanceRegistry.TryResolveQuiet(key, out GameObject go) || go == null) continue;
                var padlock = go.GetComponent<PlacementLock>();
                if (padlock != null && padlock.IsLocked) continue;
                _ackObjects.Add(go);
            }

            _ackArmed = _ackObjects.Count > 0;
            int watched = EnsureGrabSource();
            if (logEvaluations)
                Debug.Log($"[StepValidator] '{action.Id}': pick-up monitor armed for '{action.partKey}' " +
                          $"({_ackObjects.Count} instance(s)); grab source watches {watched} selectable part(s).");
            return _ackArmed;
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

        private void OnDestroy()
        {
            foreach (Candidate c in _watchedList)
                if (c.pointable != null && c.handler != null) c.pointable.WhenPointerEventRaised -= c.handler;
            _watchedList.Clear();
            _watched.Clear();
        }

        public bool BeginAction(AssemblyAction action)
        {
            Clear();
            _action = action;

            ArmFailureReason = null;
            if (action == null || !action.RequiresPhysicalValidation)
            {
                ArmFailureReason = "not a physical action";
                return false;
            }

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
                ArmFailureReason = "no GuidanceRegistry";
                Debug.LogError("[StepValidator] No GuidanceRegistry assigned; validation disabled.", this);
                return false;
            }

            if (string.IsNullOrEmpty(TargetKey) ||
                !guidanceRegistry.TryResolve(TargetKey, out GameObject targetGo))
            {
                ArmFailureReason = $"target '{TargetKey}' unresolved";
                Debug.LogError($"[StepValidator] Action '{action.Id}': target '{TargetKey}' " +
                               "could not be resolved; this action cannot be validated.", this);
                return false;
            }

            CurrentTarget = targetGo.transform;
            _targetRadius = ShapeRadius(CurrentTarget);

            Transform matTarget = MatHeadTargetFor(action);
            if (matTarget != null)
            {
                float dPos = Vector3.Distance(targetGo.transform.position, matTarget.position);
                float dRot = Quaternion.Angle(targetGo.transform.rotation, matTarget.rotation);
                Debug.Log($"[PistonHeadAction] validation target = mat head target {Describe(matTarget)} ({PathOf(matTarget)}); " +
                          $"ghost {Describe(targetGo.transform)} residual {dPos * 1000f:F2} mm / {dRot:F2} deg; snap destination = the same transform.");
                if (dPos > 0.002f || dRot > 1f)
                    Debug.LogError($"[PistonHeadAction] ghost '{targetGo.name}' is NOT at the head target (residual {dPos * 1000f:F1} mm / {dRot:F1} deg): the visible ghost and the measured target differ.");
                CurrentTarget = matTarget;
            }
            else if (RoleNameOf(action.partKey) == "PistonHead" && !string.IsNullOrEmpty(action.targetKey) && action.targetKey.StartsWith("ghost.PistonKit"))
                Debug.LogError($"[PistonHeadAction] no valid mat head target for '{action.Id}': measuring against the ghost '{targetGo.name}' instead (mat not shown or target not derived).");

            var accepted = new List<string>();
            foreach (string key in action.AcceptedPartKeys())
            {
                List<string> expanded = interchangeableRoles ? guidanceRegistry.RoleCandidates(key) : new List<string> { key };
                foreach (string k in expanded)
                    if (!accepted.Contains(k)) accepted.Add(k);
            }

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

                if (!go.activeInHierarchy)
                {
                    if (logEvaluations) Debug.Log($"[StepValidator] '{key}' ({go.name}) is inactive in the scene: not a candidate.");
                    continue;
                }

                _candidates.Add(MakeCandidate(key, go));
                _acceptedObjects.Add(go);
            }

            if (_candidates.Count == 0)
            {
                ArmFailureReason = $"no accepted instance for '{PartKey}' (expanded: {string.Join(", ", accepted)})";
                Debug.LogError($"[StepValidator] Action '{action.Id}': no accepted part could be " +
                               $"resolved for '{PartKey}'; this action cannot be validated.", this);
                return false;
            }

            if (CurrentTarget.GetComponent<Oculus.Interaction.PointableElement>() != null)
                Debug.LogError($"[StepValidator] Action '{action.Id}': target '{TargetKey}' resolves to a GRABBABLE object " +
                               $"({PathOf(CurrentTarget)}); the target must be the ghost, not a real part.", this);
            foreach (Candidate c in _candidates)
                if (c.tf == CurrentTarget)
                    Debug.LogError($"[StepValidator] Action '{action.Id}': part '{c.key}' and target '{TargetKey}' are the SAME object.", this);

            _active = _candidates[0];
            CurrentPart = _active.tf;
            IsActive = true;
            _completed = false;
            CurrentCue = Cue.None;
            LastErrorType = null;
            LastSuccessSnapped = false;
            LastSuccessLocked = false;
            _nextLiveLog = 0f;

            int watched = EnsureGrabSource();

            // Feedback reflects what is in the hand NOW, including across an action change.
            if (monitorWrongParts)
            {
                foreach (Candidate c in _watchedList)
                {
                    if (c.tf == null || !c.wasHeld || _acceptedObjects.Contains(c.tf.gameObject)) continue;
                    _wrongHeld = c;
                    Debug.Log($"[WrongPart] FIRING object={c.tf.name} action={action.Id} key={c.key} (already held when the action was armed)");
                    try { OnWrongPartGrabbed?.Invoke(c.key, c.tf); }
                    catch (Exception e) { Debug.LogError("[WrongPart] OnWrongPartGrabbed handler threw: " + e); }
                    break;
                }
            }

            if (logEvaluations)
            {
                var mfP = CurrentPart.GetComponent<MeshFilter>();
                var mfT = CurrentTarget.GetComponent<MeshFilter>();
                Debug.Log($"[StepValidator] '{action.Id}': {_candidates.Count} accepted instance(s) for '{PartKey}' " +
                          $"vs '{TargetKey}'; tolerance {_posTol * 100f:F1} cm / {_rotTol:F0} deg ({_symmetry}); " +
                          $"attempt zone {_attemptZone * 100f:F0} cm; assist gap {assistGapMeters * 100f:F0} cm; " +
                          $"{watched} selectable part(s) watched.\n" +
                          $"    realPart={Describe(CurrentPart)} path={PathOf(CurrentPart)} mesh={(mfP != null && mfP.sharedMesh != null ? mfP.sharedMesh.name : "-")} " +
                          $"lossyScale={CurrentPart.lossyScale} pos={CurrentPart.position}\n" +
                          $"    target={Describe(CurrentTarget)} path={PathOf(CurrentTarget)} mesh={(mfT != null && mfT.sharedMesh != null ? mfT.sharedMesh.name : "-")} " +
                          $"lossyScale={CurrentTarget.lossyScale} pos={CurrentTarget.position} activeInHierarchy={CurrentTarget.gameObject.activeInHierarchy}\n" +
                          $"    distanceNow={Vector3.Distance(CurrentPart.position, CurrentTarget.position):F3} m angleNow={OrientationError(_active):F1} deg");
            }

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
            _ackObjects.Clear();
            _ackArmed = false;
            _ackAction = null;
            _ackPartKey = null;
            DisengageAssist();

            if (_wrongHeld != null)
            {
                Candidate w = _wrongHeld;
                _wrongHeld = null;
                try { OnWrongPartReleased?.Invoke(w.key, w.tf); }
                catch (Exception e) { Debug.LogError("[WrongPart] OnWrongPartReleased handler threw: " + e); }
            }

            IsActive = false;
            _completed = false;
            _candidates.Clear();
            _acceptedObjects.Clear();
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
        // Grab event source
        // =====================================================================

        /// <summary>
        /// Watches every selectable part for the rest of the session: the registry's parts
        /// (including the convention-bound kit roles and holders) plus any other object
        /// under the engine root that carries a PointableElement and a Collider. Idempotent;
        /// returns the number of parts watched.
        /// </summary>
        public int EnsureGrabSource()
        {
            if (guidanceRegistry == null) return _watchedList.Count;

            int before = _watchedList.Count;
            var seen = new HashSet<GameObject>();

            // 1. registered and convention-bound keys
            ProbeConventionKeys();
            foreach (string key in guidanceRegistry.PartKeys())
            {
                if (GuidanceRegistry.IsKitHandleKey(key)) continue;              // alias of a head
                if (!guidanceRegistry.TryResolveQuiet(key, out GameObject go) || go == null) continue;
                if (!seen.Add(go)) continue;
                Watch(key, go);
            }

            // 2. anything selectable under the same root as the crankshaft that no key names
            if (guidanceRegistry.TryResolveQuiet("part.crankshaft", out GameObject crank) && crank != null)
            {
                foreach (var pe in crank.transform.root.GetComponentsInChildren<Oculus.Interaction.PointableElement>(true))
                {
                    if (pe == null || pe is Oculus.Interaction.PointableCanvas) continue;
                    GameObject go = pe.gameObject;
                    if (seen.Contains(go) || _watched.ContainsKey(go)) continue;
                    if (go.GetComponent<Collider>() == null) continue;
                    Watch("unregistered:" + go.name, go);
                }
            }

            if (_watchedList.Count != before)
                Debug.Log($"[GrabSource] watching {_watchedList.Count} selectable part(s) (+{_watchedList.Count - before}) " +
                          $"eventDriven={eventDrivenGrabs}");
            return _watchedList.Count;
        }

        /// <summary>Binds the convention keys so the kit roles and holders exist before anything asks for them.</summary>
        private void ProbeConventionKeys()
        {
            string[] roles = { "PistonHead", "ConnectingRod", "ConnectingPin", "PistonEnd", "pistonBolt", "pistonBoltOther", "PistonNut", "PistonNutOther" };
            for (int k = 1; k <= 4; k++)
            {
                foreach (string role in roles) guidanceRegistry.TryResolveQuiet($"part.PistonKit00{k}.{role}", out _);
                guidanceRegistry.TryResolveQuiet($"part.engineBlockSep00{k}", out _);
            }
            guidanceRegistry.TryResolveQuiet("part.camshaft", out _);
        }

        private void Watch(string key, GameObject go)
        {
            if (go == null || _watched.ContainsKey(go)) return;
            var pointable = go.GetComponent<Oculus.Interaction.PointableElement>();
            if (pointable == null) return;                                        // not selectable
            if (go.GetComponent<Collider>() == null) return;                      // kit roots: not grabbable parts

            Candidate c = MakeCandidate(key, go);
            if (eventDrivenGrabs)
            {
                Candidate captured = c;
                c.handler = evt => HandlePointerEvent(captured, evt);
                pointable.WhenPointerEventRaised += c.handler;
            }
            _watched.Add(go, c);
            _watchedList.Add(c);
        }

        private void HandlePointerEvent(Candidate c, Oculus.Interaction.PointerEvent evt)
        {
            if (c == null || c.tf == null) return;
            switch (evt.Type)
            {
                case Oculus.Interaction.PointerEventType.Select:
                    if (c.pendingGrab) break;
                    if (c.wasHeld)
                    {
                        // A Select while we still think the part is held means a release was
                        // missed. Close that episode and open a new one rather than staying mute.
                        Debug.Log($"[GrabEvent] Select on object={c.tf.name} id={c.tf.GetInstanceID()} while still marked held: closing the stale episode first.");
                        c.pendingRelease = true;
                    }
                    c.pendingGrab = true;
                    c.grabSource = "isdk_event";
                    Debug.Log($"[GrabEvent] RECEIVED object={c.tf.name} id={c.tf.GetInstanceID()} action={CurrentActionId()} " +
                              $"source=isdk_event selecting={(c.pointable != null ? c.pointable.SelectingPointsCount : -1)} key={c.key}");
                    break;
                case Oculus.Interaction.PointerEventType.Unselect:
                case Oculus.Interaction.PointerEventType.Cancel:
                    if (c.pointable == null || c.pointable.SelectingPointsCount == 0)
                    {
                        c.pendingRelease = true;
                        c.releaseSource = "isdk_event";
                        if (c.wasHeld)
                            Debug.Log($"[GrabEvent] RELEASED object={c.tf.name} id={c.tf.GetInstanceID()} action={CurrentActionId()} " +
                                      $"source=isdk_event({evt.Type}) selecting=0 key={c.key}");
                    }
                    break;
            }
        }

        private string CurrentActionId()
        {
            if (_action != null) return _action.Id;
            if (_ackArmed && _ackAction != null) return _ackAction.Id;
            return "<none>";
        }

        /// <summary>Runs every frame, armed or not: one grab / one release per episode, whichever source saw it first.</summary>
        private void ProcessGrabSource()
        {
            for (int i = 0; i < _watchedList.Count; i++)
            {
                Candidate c = _watchedList[i];
                if (c.tf == null) continue;

                bool held = c.pointable != null && c.pointable.SelectingPointsCount > 0;
                bool grab = c.pendingGrab || (held && !c.wasHeld);
                bool release = c.pendingRelease || (!held && c.wasHeld);
                string source = c.pendingGrab ? c.grabSource : "poll";
                string releaseSource = c.pendingRelease ? c.releaseSource : "poll";
                c.pendingGrab = false;
                c.pendingRelease = false;

                // Release FIRST: an episode is closed the moment the part is no longer selected
                // (event or poll), so the next grab - same object or another - can warn again.
                if (release && c.wasHeld)
                {
                    c.wasHeld = false;
                    if (releaseSource != "isdk_event")
                        Debug.Log($"[GrabEvent] RELEASED object={c.tf.name} id={c.tf.GetInstanceID()} action={CurrentActionId()} " +
                                  $"source=poll selecting={(c.pointable != null ? c.pointable.SelectingPointsCount : -1)} key={c.key}");
                    HandleRelease(c);
                }

                if (grab && !c.wasHeld)
                {
                    c.wasHeld = true;
                    if (source != "isdk_event")
                        Debug.Log($"[GrabEvent] RECEIVED object={c.tf.name} id={c.tf.GetInstanceID()} action={CurrentActionId()} " +
                                  $"source=poll selecting={(c.pointable != null ? c.pointable.SelectingPointsCount : -1)} key={c.key}");
                    HandleGrab(c, source);
                }
            }
        }

        private void HandleGrab(Candidate c, string source)
        {
            GameObject go = c.tf.gameObject;
            bool physical = IsActive && !_completed;
            bool ack = _ackArmed;
            string actionId = CurrentActionId();

            if (!physical && !ack)
            {
                Debug.Log($"[Eligibility] object={go.name} requested=<none> eligible=n/a reason=\"no action armed: nothing to validate or acknowledge\"");
                return;
            }

            string requested = physical ? PartKey : _ackPartKey;
            bool eligible = physical ? _acceptedObjects.Contains(go) : _ackObjects.Contains(go);
            string reason;
            if (eligible)
                reason = physical ? "accepted instance of the requested role for this placement"
                                  : "the part this instruction asks for (pick-up acknowledges it)";
            else if (physical)
                reason = $"selectable part but not an accepted instance of '{requested}' (accepted: {DescribeAccepted()})";
            else
                reason = $"selectable part but not the part this instruction asks for ('{requested}')";

            Debug.Log($"[Eligibility] object={go.name} requested={RoleNameOf(requested) ?? "<none>"} eligible={eligible} " +
                      $"reason=\"{reason}\" key={c.key} action={actionId} source={source}");

            if (eligible)
            {
                if (ack)
                {
                    _ackArmed = false;
                    if (logEvaluations) Debug.Log($"[StepValidator] instruction '{actionId}' acknowledged by picking up '{c.key}' ({go.name}).");
                    try { OnAcknowledgedByGrab?.Invoke(c.key, c.tf); }
                    catch (Exception e) { Debug.LogError("[StepValidator] OnAcknowledgedByGrab handler threw: " + e); }
                }
                // PHYSICAL: OnPartGrabbed is raised by the attempt lifecycle below (same frame).
                return;
            }

            if (!monitorWrongParts) return;

            _wrongHeld = c;
            Debug.Log($"[WrongPart] FIRING object={go.name} action={actionId} key={c.key}");
            try { OnWrongPartGrabbed?.Invoke(c.key, c.tf); }
            catch (Exception e) { Debug.LogError("[WrongPart] OnWrongPartGrabbed handler threw: " + e); }
        }

        private void HandleRelease(Candidate c)
        {
            if (_wrongHeld == c)
            {
                _wrongHeld = null;
                Debug.Log($"[WrongPart] episode closed object={c.tf.name} id={c.tf.GetInstanceID()} (warned state cleared; the next grab is a new episode)");
                try { OnWrongPartReleased?.Invoke(c.key, c.tf); }
                catch (Exception e) { Debug.LogError("[WrongPart] OnWrongPartReleased handler threw: " + e); }
                return;
            }

            if (IsActive && c.tf != null && _acceptedObjects.Contains(c.tf.gameObject))
            {
                try { OnPartReleased?.Invoke(c.key, c.tf); }
                catch (Exception e) { Debug.LogError("[StepValidator] OnPartReleased handler threw: " + e); }
            }
        }

        private string DescribeAccepted()
        {
            var names = new List<string>();
            foreach (Candidate k in _candidates) if (k.tf != null) names.Add($"{k.tf.name}#{k.tf.GetInstanceID()}");
            return string.Join(", ", names);
        }

        // =====================================================================
        // Per frame
        // =====================================================================

        private void Update()
        {
            ProcessGrabSource();

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
                try { OnPartGrabbed?.Invoke(grabbedNow.key, grabbedNow.tf); }
                catch (Exception e) { Debug.LogError("[StepValidator] OnPartGrabbed handler threw: " + e); }
                // Picking the part up again inside the zone starts a fresh attempt.
                if (grabbedNow == _active) _inAttempt = false;
            }

            UpdateAssist();

            if (_active == null || _active.tf == null) return;

            bool ok = WithinTolerance(_active, out float posErr, out float rotErr);
            bool inZone = posErr <= _attemptZone;

            LiveLog(posErr, rotErr, anyHeld, inZone);

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

        /// <summary>
        /// Throttled live readout of the REAL part against the REAL target: about 4 Hz while
        /// the part is handled or near the target, 1 Hz otherwise so a silent validator is
        /// visible as such.
        /// </summary>
        private void LiveLog(float posErr, float rotErr, bool anyHeld, bool inZone)
        {
            if (!logEvaluations || Time.time < _nextLiveLog) return;
            _nextLiveLog = Time.time + ((anyHeld || inZone) ? liveLogInterval : 1f);

            // Breakdown under the mechanical axes regardless of the rule in force: how far the
            // long axis is off, and how much roll about it remains.
            Vector3 partAxis = _active.tf.rotation * _active.axisLocal;
            Vector3 targetAxis = CurrentTarget.rotation * _active.axisLocal;
            float axisAngle = Vector3.Angle(partAxis, targetAxis);
            Quaternion alignAxes = Quaternion.FromToRotation(partAxis, targetAxis);
            float roll = Quaternion.Angle(Quaternion.Inverse(CurrentTarget.rotation) * (alignAxes * _active.tf.rotation), Quaternion.identity);

            Debug.Log($"{Tag("Validation")} part={Describe(_active.tf)} target={Describe(CurrentTarget)} " +
                      $"distance={posErr:F4} angle={rotErr:F1} posOK={posErr <= _posTol} rotOK={rotErr <= _rotTol} " +
                      $"armed={IsActive} held={anyHeld} inZone={inZone} tol={_posTol:F3}m/{_rotTol:F0}deg sym={_symmetry} " +
                      $"axisOff={axisAngle:F1} roll={roll:F1} partPos={_active.tf.position} targetPos={CurrentTarget.position}");
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
            LastAttemptFar = pos > _posTol * 2.5f;
            if (!LastAttemptPositionOk && !LastAttemptOrientationOk) LastErrorType = "incorrect_position_and_orientation";
            else if (!LastAttemptPositionOk) LastErrorType = "incorrect_position";
            else LastErrorType = "incorrect_orientation";
            LastRejectReason = LastAttemptPositionOk ? RejectReason.WrongRotation : RejectReason.TooFar;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Attempt {AttemptCount} FAILED ({trigger}) on '{_active.key}': {LastErrorType}; " +
                          $"closest {pos * 100f:F1} cm, orientation {rot:F0} deg (tol {_posTol * 100f:F1} cm / {_rotTol:F0} deg).");

            try { OnAttemptEvaluated?.Invoke(false, pos, rot, trigger); }
            catch (Exception e) { Debug.LogError("[StepValidator] OnAttemptEvaluated handler threw: " + e); }
        }

        /// <summary>
        /// Exactly once per action: snap, lock, join, then raise success. Each stage is
        /// isolated and reported; the success events are raised even if a stage failed, so
        /// a snap problem is an error in the log rather than a dead action.
        /// </summary>
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

            string tag = Tag("Success");
            Debug.Log($"{tag} ENTER part={Describe(_active.tf)} target={Describe(CurrentTarget)} " +
                      $"distance={posErr:F4} angle={rotErr:F1} trigger={trigger} tol={_posTol:F3}m/{_rotTol:F0}deg");

            if (logEvaluations)
                Debug.Log($"[StepValidator] Attempt {AttemptCount} ACCEPTED ({trigger}) on '{_active.key}': " +
                          $"off by {posErr * 100f:F1} cm / {rotErr:F0} deg.");

            // 1. exact pose, 2. grab ended + locked (PlacementLock disables the SDK components
            //    and enforces pose + kinematic state continuously), 3. joined to its assembly.
            bool snapped = false, locked = false;
            PlacementLock padlock = null;

            PartWatch.Log("Succeed(before snap)", _active.tf);
            try
            {
                SnapToTarget(_active);
                snapped = Vector3.Distance(_active.tf.position, CurrentTarget.position) < 0.002f
                          && Quaternion.Angle(_active.tf.rotation, CurrentTarget.rotation) < 1f;
            }
            catch (Exception e) { Debug.LogError($"{tag} snap threw: {e}"); }

            try
            {
                padlock = _active.tf.GetComponent<PlacementLock>();
                if (padlock == null) padlock = _active.tf.gameObject.AddComponent<PlacementLock>();
                padlock.LockAt(CurrentTarget);
                locked = padlock.IsLocked;
            }
            catch (Exception e) { Debug.LogError($"{tag} lock threw: {e}"); }
            PartWatch.Log("Succeed(after lock)", _active.tf);

            try
            {
                BindAndJoin(_active);
                if (padlock != null) padlock.RefreshLockedPose();
            }
            catch (Exception e) { Debug.LogError($"{tag} join threw: {e}"); }
            PartWatch.Log("Succeed(after join)", _active.tf);

            // Collisions with the assembly stay ignored for this part: it lives inside it now.
            _assistFor = null;
            IsAssisting = false;

            LastSuccessSnapped = snapped;
            LastSuccessLocked = locked;

            var renderer = _active.tf.GetComponent<Renderer>();
            Debug.Log($"{tag} snapped={snapped} locked={locked} partVisible={(renderer != null && renderer.enabled && _active.tf.gameObject.activeInHierarchy)} " +
                      $"-> raising OnAttemptEvaluated + OnStepValidated");
            if (!snapped || !locked)
                Debug.LogError($"{tag} incomplete: snapped={snapped} locked={locked} (see exceptions above). Progression continues on the authoritative gate.");

            try { OnAttemptEvaluated?.Invoke(true, posErr, rotErr, trigger); }
            catch (Exception e) { Debug.LogError($"{tag} OnAttemptEvaluated handler threw: {e}"); }

            try { OnStepValidated?.Invoke(); }
            catch (Exception e) { Debug.LogError($"{tag} OnStepValidated handler threw: {e}"); }
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
