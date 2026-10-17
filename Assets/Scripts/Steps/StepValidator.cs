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
            AxialHalfTurn = 3,
            /// <summary>Full orientation must match the target OR the target turned 180 degrees about the part's symmetry axis (rod end cap).</summary>
            HalfTurnAboutAxis = 4
        }

        /// <summary>Which mesh axis is the part's mechanical axis.</summary>
        public enum AxisSelect
        {
            /// <summary>Longest extent (shafts, pins, rods).</summary>
            Longest = 0,
            /// <summary>Shortest extent (a nut's insertion axis is its thickness, not its width).</summary>
            Shortest = 1,
            /// <summary>Direction from the part to the piston head in the assembled piston (rod end cap).</summary>
            TowardsHeadInAssembly = 2,
            /// <summary>Direction from the part to its paired part in the assembled piston (bolt -> its nut, nut -> its bolt): the insertion axis.</summary>
            TowardsPairedPart = 3
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
            [Tooltip("Which mesh axis is the mechanical axis the symmetry rule refers to.")]
            public AxisSelect axis = AxisSelect.Longest;
            [Tooltip("Insertion part: must approach along its axis (not across it) before it can be accepted.")]
            public bool insertionAxis;
        }

        /// <summary>Where the held part is relative to its target, for the participant feedback.</summary>
        public enum Proximity { None = 0, Far = 1, Near = 2, Valid = 3 }

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
        private const int CurrentRoleToleranceRevision = 6;

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
        public bool WrongPartHeld { get { return _wrongHeld != null || _wrongEpisodeObject != null; } }

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

        /// <summary>Far / Near / Valid for the held accepted part (None when nothing is held).</summary>
        public Proximity CurrentProximity { get; private set; }

        /// <summary>True while the part is in tolerance but came in across its insertion axis.</summary>
        public bool InsertionBlocked { get; private set; }

        /// <summary>Session-wide id of the current/last attempt; one counted error at most per attempt.</summary>
        public int AttemptId { get; private set; }

        /// <summary>True when the last failed attempt was a deliberate placement (release near the target) and counts as an error.</summary>
        public bool LastAttemptCounted { get; private set; }

        /// <summary>Raised when the held part's proximity state changes. (state, part)</summary>
        public event Action<Proximity, Transform> OnProximityChanged;

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
            public Vector3 centreLocal;          // mesh bounds centre: positions are compared here, not at the pivot
            public bool firedThisEpisode;        // eligibility/wrong-part dispatched for the current grab episode

            // grab event source
            public Action<Oculus.Interaction.PointerEvent> handler;
            public bool pendingGrab;
            public bool pendingRelease;
            public string grabSource;
            public string releaseSource;
            public readonly List<InteractableLink> links = new List<InteractableLink>();
        }

        /// <summary>A grab interactable (the thing the hand actually selects) that drives a watched part.</summary>
        private class InteractableLink
        {
            public MonoBehaviour component;
            public string kind;
            public Oculus.Interaction.IPointable pointable;
            public Oculus.Interaction.IInteractableView view;
            public Action<Oculus.Interaction.PointerEvent> handler;
        }

        private class TargetOption { public string key; public Transform tf; public float radius; }
        private readonly List<TargetOption> _targets = new List<TargetOption>();
        private readonly HashSet<Transform> _occupiedTargets = new HashSet<Transform>();

        /// <summary>Roles whose two instances are interchangeable, and whose two targets are too (either side).</summary>
        private static readonly string[][] EquivalentRoles =
        {
            new[] { "pistonBolt", "pistonBoltOther" },
            new[] { "PistonNut", "PistonNutOther" }
        };

        private static string[] GroupOf(string role)
        {
            if (string.IsNullOrEmpty(role)) return null;
            foreach (string[] g in EquivalentRoles) if (Array.IndexOf(g, role) >= 0) return g;
            return null;
        }

        /// <summary>Keys of every target the current action may be satisfied at (one, or both free equivalent targets).</summary>
        public IEnumerable<string> ActiveTargetKeys { get { foreach (TargetOption t in _targets) yield return t.key; } }

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
        private AxisSelect _axisSelect;
        private bool _insertion;
        private Vector3 _capAxisLocal = Vector3.up;
        private bool _hasCapAxis;
        private float _nextLiveLog;

        // Grab/repositioning of the kit handle while building the loose subassembly on the mat.
        private readonly HashSet<GameObject> _toleratedObjects = new HashSet<GameObject>();
        private Transform _followHandle;
        private Transform _followKitRoot;

        // insertion-path gate: recent positions of the active part
        private readonly List<KeyValuePair<float, Vector3>> _trail = new List<KeyValuePair<float, Vector3>>();
        private bool _approachOk;
        private bool _approachDecided;
        private float _lastApproachAngle = -1f;

        // interactor-side truth
        private bool _interactorsHooked;
        private readonly Dictionary<MonoBehaviour, MonoBehaviour> _interactorSelection = new Dictionary<MonoBehaviour, MonoBehaviour>();

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
                // Revision 4: same numbers as rev 3; symmetry rules made mechanical. Bolts/nuts
                // are free to roll about their INSERTION axis (a nut's is its shortest extent),
                // the rod end cap is accepted in either of its two equivalent orientations,
                // and insertion parts must approach along their axis.
                // Revision 6: POSITION tightened across the assembly (deliberate alignment with the
                // guide); every angle and every symmetry rule unchanged. Small hardware is tuned
                // on its own line. Holders get their own entry (any holder, slot in sequence).
                new RoleTolerance { role = "PistonHead",      positionMeters = 0.020f, rotationDegrees = 22f, symmetry = AxisSymmetry.None },
                new RoleTolerance { role = "ConnectingRod",   positionMeters = 0.020f, rotationDegrees = 22f, symmetry = AxisSymmetry.AxialHalfTurn },
                new RoleTolerance { role = "ConnectingPin",   positionMeters = 0.015f, rotationDegrees = 28f, symmetry = AxisSymmetry.AxialFreeFlip, insertionAxis = true },
                new RoleTolerance { role = "PistonEnd",       positionMeters = 0.020f, rotationDegrees = 25f, symmetry = AxisSymmetry.HalfTurnAboutAxis, axis = AxisSelect.TowardsHeadInAssembly },
                new RoleTolerance { role = "pistonBolt",      positionMeters = 0.020f, rotationDegrees = 28f, symmetry = AxisSymmetry.AxialFreeFlip, axis = AxisSelect.TowardsPairedPart, insertionAxis = true },
                new RoleTolerance { role = "pistonBoltOther", positionMeters = 0.020f, rotationDegrees = 28f, symmetry = AxisSymmetry.AxialFreeFlip, axis = AxisSelect.TowardsPairedPart, insertionAxis = true },
                new RoleTolerance { role = "PistonNut",       positionMeters = 0.024f, rotationDegrees = 32f, symmetry = AxisSymmetry.AxialFreeFlip, axis = AxisSelect.TowardsPairedPart, insertionAxis = true },
                new RoleTolerance { role = "PistonNutOther",  positionMeters = 0.024f, rotationDegrees = 32f, symmetry = AxisSymmetry.AxialFreeFlip, axis = AxisSelect.TowardsPairedPart, insertionAxis = true },
                new RoleTolerance { role = "crankshaft",      positionMeters = 0.022f, rotationDegrees = 20f, symmetry = AxisSymmetry.AxialFree },
                new RoleTolerance { role = "camshaft",        positionMeters = 0.022f, rotationDegrees = 20f, symmetry = AxisSymmetry.AxialFree, insertionAxis = true },
                new RoleTolerance { role = "engineBlockSep",  positionMeters = 0.020f, rotationDegrees = 25f, symmetry = AxisSymmetry.None },
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
            // Numbered family (engineBlockSep001..004) -> the family entry.
            string family = role.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (family != role)
                foreach (RoleTolerance r in roleTolerances)
                    if (r != null && r.role == family) return r;
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
            {
                if (c.pointable != null && c.handler != null) c.pointable.WhenPointerEventRaised -= c.handler;
                foreach (InteractableLink l in c.links)
                    if (l.pointable != null && l.handler != null) l.pointable.WhenPointerEventRaised -= l.handler;
            }
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
            _axisSelect = rt != null ? rt.axis : AxisSelect.Longest;
            _insertion = rt != null && rt.insertionAxis;
            _attemptZone = Mathf.Max(attemptZoneMinMeters, _posTol * attemptZoneMultiplier);
            _hasCapAxis = false;
            _approachOk = !_insertion;
            _approachDecided = !_insertion;
            _hasEntry = false;
            _trail.Clear();
            InsertionBlocked = false;
            CurrentProximity = Proximity.None;

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

            // Equivalent bolt/nut targets: both free positions are valid; the part goes to the
            // nearest free one (chosen live). Once a target is occupied it is no longer offered.
            _targets.Clear();
            // Either-side TARGETS exist only for the bolt/nut pairs. Holders are interchangeable
            // PARTS (any unused holder is accepted - expanded above) but their slots are guided
            // in sequence: this action's authored target only, one ghost.
            string[] group = GroupOf(RoleNameOf(action.partKey));
            bool equivalent = group != null;
            if (equivalent)
            {
                foreach (string tk in GuidanceAudit.ExpandEquivalent(action.targetKey))
                {
                    if (!guidanceRegistry.TryResolveQuiet(tk, out GameObject tg) || tg == null) continue;
                    if (_occupiedTargets.Contains(tg.transform)) continue;
                    _targets.Add(new TargetOption { key = tk, tf = tg.transform, radius = ShapeRadius(tg.transform) });
                }
                if (_targets.Count == 0)
                {
                    ArmFailureReason = "every equivalent target is already occupied";
                    Debug.LogError($"[StepValidator] Action '{action.Id}': every equivalent {group[0]} target is already occupied.", this);
                    return false;
                }
                TargetOption first = _targets.Find(t => t.key == action.targetKey) ?? _targets[0];
                CurrentTarget = first.tf;
                TargetKey = first.key;
                _targetRadius = first.radius;
                var keys = new List<string>(); foreach (TargetOption t in _targets) keys.Add(t.key);
                Debug.Log($"[StepValidator] '{action.Id}': either-side action for {group[0]}/{group[1]}: {_targets.Count} free target(s) [{string.Join(", ", keys)}]; " +
                          "any loose bolt/nut of either role fits the nearest free one.");
            }
            else _targets.Add(new TargetOption { key = TargetKey, tf = CurrentTarget, radius = _targetRadius });

            var accepted = new List<string>();
            foreach (string key in action.AcceptedPartKeys())
            {
                // A bolt/nut action accepts either of the two equivalent roles (either side).
                foreach (string eq in GuidanceAudit.ExpandEquivalent(key))
                {
                    List<string> expanded = interchangeableRoles ? guidanceRegistry.RoleCandidates(eq) : new List<string> { eq };
                    foreach (string k in expanded)
                        if (!accepted.Contains(k)) accepted.Add(k);
                }
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

            // Mechanical axes per candidate under the role's rule (longest / shortest / towards
            // the head in the assembled piston for the cap).
            if (_axisSelect == AxisSelect.TowardsHeadInAssembly || _symmetry == AxisSymmetry.HalfTurnAboutAxis) ResolveAssemblyAxis(action, "PistonHead");
            else if (_axisSelect == AxisSelect.TowardsPairedPart) ResolveAssemblyAxis(action, PairedRole(RoleNameOf(action.partKey)));
            foreach (Candidate c in _candidates) c.axisLocal = AxisFor(c.tf);

            // Building the loose subassembly on the mat: the kit handle (head) may be picked up
            // to turn the subassembly; it is tolerated (never a wrong part) and the kit's
            // ghosts/targets follow it.
            _toleratedObjects.Clear();
            _followHandle = null; _followKitRoot = null;
            string kitOfAction = GuidanceRegistry.KitOf(PartKey);
            if (kitOfAction != null && GuidanceRegistry.RoleOf(PartKey) != guidanceRegistry.KitHandleRole
                && guidanceRegistry.TryGetKitHandle(kitOfAction, out Transform kitHandle) && kitHandle != null)
            {
                _toleratedObjects.Add(kitHandle.gameObject);
                foreach (Transform joined in kitHandle.GetComponentsInChildren<Transform>(true))
                    if (joined != kitHandle && joined.GetComponent<Rigidbody>() != null) _toleratedObjects.Add(joined.gameObject);
                var hl = kitHandle.GetComponent<PlacementLock>();
                bool onMat = !string.IsNullOrEmpty(action.targetKey) && action.targetKey.StartsWith("ghost.PistonKit");
                if (hl != null && onMat) hl.Repositionable = true;
                if (onMat && CurrentTarget != null && CurrentTarget.parent != null && CurrentTarget.parent.Find("PistonHead") != null)
                {
                    _followHandle = kitHandle;
                    _followKitRoot = CurrentTarget.parent;
                }
                if (logEvaluations)
                    Debug.Log($"[StepValidator] '{action.Id}': kit handle '{kitHandle.name}' may be picked up to turn the subassembly (tolerated grab); ghosts follow it: {_followKitRoot != null}.");
            }

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

        /// <summary>The part's mechanical axis in its own local space, under the role's axis rule.</summary>
        private Vector3 AxisFor(Transform t)
        {
            if (t == null) return Vector3.forward;
            if (_hasCapAxis && (_symmetry == AxisSymmetry.HalfTurnAboutAxis || _axisSelect == AxisSelect.TowardsPairedPart || _axisSelect == AxisSelect.TowardsHeadInAssembly)) return _capAxisLocal;
            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return Vector3.forward;
            Vector3 s = Vector3.Scale(mf.sharedMesh.bounds.size, t.localScale);
            s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            if (_axisSelect == AxisSelect.Shortest)
            {
                if (s.x <= s.y && s.x <= s.z) return Vector3.right;
                if (s.y <= s.x && s.y <= s.z) return Vector3.up;
                return Vector3.forward;
            }
            if (s.x >= s.y && s.x >= s.z) return Vector3.right;
            if (s.y >= s.x && s.y >= s.z) return Vector3.up;
            return Vector3.forward;
        }

        /// <summary>
        /// Rod end cap: its two physically equivalent orientations differ by a half turn about
        /// the axis from the cap towards the piston head in the assembled piston. Read from the
        /// assembled ghost (ghost.piston00N) so no mechanical detail is invented.
        /// </summary>
        private static string PairedRole(string role)
        {
            switch (role)
            {
                case "pistonBolt": return "PistonNut";
                case "pistonBoltOther": return "PistonNutOther";
                case "PistonNut": return "pistonBolt";
                case "PistonNutOther": return "pistonBoltOther";
                default: return null;
            }
        }

        /// <summary>
        /// The part's mechanical axis read from the ASSEMBLED piston ghost (ghost.piston00N):
        /// the direction from this part's ghost to the named other part's ghost, in this
        /// part's local space. Cap -> head gives the cap's symmetry axis; bolt -> its nut (and
        /// nut -> its bolt) gives the insertion axis. Nothing mechanical is invented.
        /// </summary>
        private void ResolveAssemblyAxis(AssemblyAction action, string towardsRole)
        {
            _hasCapAxis = false;
            string role = RoleNameOf(action.partKey);
            string kit = GuidanceRegistry.KitOf(action.partKey);
            string n = kit != null ? kit.Substring(kit.Length - 1) : null;
            if (n != null && towardsRole != null && guidanceRegistry.TryResolveQuiet($"ghost.piston00{n}", out GameObject asm) && asm != null)
            {
                Transform self = asm.transform.Find(role);
                Transform other = asm.transform.Find(towardsRole);
                if (self != null && other != null)
                {
                    Vector3 d = other.position - self.position;
                    if (d.sqrMagnitude > 1e-8f)
                    {
                        _capAxisLocal = self.InverseTransformDirection(d.normalized).normalized;
                        _hasCapAxis = true;
                        if (logEvaluations) Debug.Log($"[StepValidator] '{role}' mechanical axis from the assembled piston: towards '{towardsRole}' = {_capAxisLocal} in part space.");
                        return;
                    }
                }
            }
            Debug.LogError($"[StepValidator] mechanical axis of '{role}' could not be read from the assembled piston ghost (towards '{towardsRole}'); falling back to the mesh extent rule.");
            _capAxisLocal = Vector3.up;
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
                axisLocal = LongAxisLocal(go.transform),
                centreLocal = CentreLocal(go.transform)
            };
        }

        private static Vector3 CentreLocal(Transform t)
        {
            var mf = t != null ? t.GetComponent<MeshFilter>() : null;
            return mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds.center : Vector3.zero;
        }

        /// <summary>World position of the part's geometric centre (same mesh on part and ghost, so it is comparable).</summary>
        private static Vector3 CentreWorld(Transform t, Vector3 centreLocal)
        {
            return t.TransformPoint(centreLocal);
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
            _targets.Clear();
            _toleratedObjects.Clear();
            _followHandle = null; _followKitRoot = null;
            _trail.Clear();
            InsertionBlocked = false;
            if (CurrentProximity != Proximity.None)
            {
                CurrentProximity = Proximity.None;
                try { OnProximityChanged?.Invoke(Proximity.None, _active != null ? _active.tf : null); } catch (Exception e) { Debug.LogError("[StepValidator] OnProximityChanged threw: " + e); }
            }
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

        private Transform EngineRoot()
        {
            if (guidanceRegistry != null && guidanceRegistry.TryResolveQuiet("part.crankshaft", out GameObject crank) && crank != null)
                return crank.transform.root;
            return null;
        }

        /// <summary>
        /// Watches every selectable part for the rest of the session, from three directions:
        /// the registry's parts (registered and convention-bound), the objects the Interaction
        /// SDK ACTUALLY drives (every HandGrab/Grab interactable under the engine root, keyed by
        /// the Rigidbody it grabs), and any other PointableElement with a collider. Each part
        /// is reported by its Grabbable's events, by its interactables' events, and by polling.
        /// Idempotent; returns the number of parts watched.
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
                Watch(key, go, false);
            }

            Transform root = EngineRoot();
            if (root != null)
            {
                // 2. what the hand actually selects
                foreach (var hgi in root.GetComponentsInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true))
                    WatchInteractable(hgi, hgi.Rigidbody, hgi.PointableElement, "HandGrabInteractable");
                foreach (var gi in root.GetComponentsInChildren<Oculus.Interaction.GrabInteractable>(true))
                    WatchInteractable(gi, gi.Rigidbody, gi.PointableElement, "GrabInteractable");

                // 3. anything else selectable that no key and no interactable named
                foreach (var pe in root.GetComponentsInChildren<Oculus.Interaction.PointableElement>(true))
                {
                    if (pe == null || pe is Oculus.Interaction.PointableCanvas) continue;
                    GameObject go = pe.gameObject;
                    if (_watched.ContainsKey(go)) continue;
                    if (go.GetComponent<Collider>() == null) continue;
                    Watch("unregistered:" + go.name, go, false);
                }
            }

            HookInteractors();

            if (_watchedList.Count != before)
                Debug.Log($"[GrabSource] watching {_watchedList.Count} selectable part(s) (+{_watchedList.Count - before}) " +
                          $"eventDriven={eventDrivenGrabs}");
            return _watchedList.Count;
        }

        // ---------------- interactor-side truth: what the hand ACTUALLY selected ----------------

        /// <summary>
        /// Subscribes to every grab interactor in the rig. On select, the interactable it
        /// chose is resolved to the Rigidbody/object it grabs and reported as
        /// [GrabRuntime] before any eligibility logic - independent of the registry and of
        /// any per-part subscription. A part grabbed this way that was not watched is
        /// watched from then on (and reported as an ERROR).
        /// </summary>
        private void HookInteractors()
        {
            if (_interactorsHooked) return;
            _interactorsHooked = true;
            int n = 0;

            foreach (var it in FindObjectsByType<Oculus.Interaction.HandGrab.HandGrabInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var cap = it; n++;
                cap.WhenStateChanged += a => OnInteractorState(cap, a, () => cap.SelectedInteractable);
            }
            foreach (var it in FindObjectsByType<Oculus.Interaction.HandGrab.DistanceHandGrabInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var cap = it; n++;
                cap.WhenStateChanged += a => OnInteractorState(cap, a, () => cap.SelectedInteractable);
            }
            foreach (var it in FindObjectsByType<Oculus.Interaction.GrabInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var cap = it; n++;
                cap.WhenStateChanged += a => OnInteractorState(cap, a, () => cap.SelectedInteractable);
            }
            foreach (var it in FindObjectsByType<Oculus.Interaction.DistanceGrabInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var cap = it; n++;
                cap.WhenStateChanged += a => OnInteractorState(cap, a, () => cap.SelectedInteractable);
            }
            _interactorCount = n;
            Debug.Log($"[GrabRuntime] hooked {n} grab interactor(s) in the rig (hand grab / distance hand grab / controller grab / distance grab); " +
                      (n > 0 ? "selection events are the wrong-component decision path." : "FALLBACK: polling decides."));
            if (n == 0) Debug.LogError("[GrabRuntime] NO grab interactor found in the rig: the runtime truth path has no source.");
        }

        private void OnInteractorState(MonoBehaviour interactor, Oculus.Interaction.InteractorStateChangeArgs a, Func<MonoBehaviour> selected)
        {
            try
            {
                if (a.NewState == Oculus.Interaction.InteractorState.Select)
                {
                    MonoBehaviour s = selected();
                    if (s == null) return;
                    _interactorSelection[interactor] = s;
                    OnRuntimeSelected(interactor, s);
                }
                else if (a.PreviousState == Oculus.Interaction.InteractorState.Select)
                {
                    if (_interactorSelection.TryGetValue(interactor, out MonoBehaviour s))
                    {
                        _interactorSelection.Remove(interactor);
                        OnRuntimeUnselected(interactor, s);
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[GrabRuntime] interactor handler threw: " + e); }
        }

        private static void ResolveInteractable(MonoBehaviour interactable, out Rigidbody body, out Component pointable)
        {
            body = null; pointable = null;
            switch (interactable)
            {
                case Oculus.Interaction.HandGrab.HandGrabInteractable h: body = h.Rigidbody; pointable = h.PointableElement as Component; break;
                case Oculus.Interaction.HandGrab.DistanceHandGrabInteractable dh: body = dh.Rigidbody; pointable = dh.PointableElement as Component; break;
                case Oculus.Interaction.GrabInteractable g: body = g.Rigidbody; pointable = g.PointableElement as Component; break;
                case Oculus.Interaction.DistanceGrabInteractable dg: body = dg.Rigidbody; pointable = dg.PointableElement as Component; break;
            }
        }

        // Grab episodes keyed by the selected object itself, independent of any watch list:
        // refcount per object (two hands = one episode), cleared when the last hand lets go.
        private readonly Dictionary<GameObject, int> _episodes = new Dictionary<GameObject, int>();
        private GameObject _wrongEpisodeObject;
        private int _interactorCount;

        /// <summary>The objects a grab is allowed on right now: accepted parts of the action, plus tolerated ones.</summary>
        private List<string> DescribeCurrentlyAccepted(out bool anyArmed)
        {
            bool physical = IsActive && !_completed;
            bool ack = _ackArmed;
            anyArmed = physical || ack;
            var names = new List<string>();
            if (physical) foreach (GameObject g in _acceptedObjects) { if (g != null) names.Add($"{g.name}#{g.GetInstanceID()}"); }
            else if (ack) foreach (GameObject g in _ackObjects) { if (g != null) names.Add($"{g.name}#{g.GetInstanceID()}"); }
            foreach (GameObject g in _toleratedObjects) if (g != null) names.Add($"{g.name}#{g.GetInstanceID()}(tolerated)");
            return names;
        }

        /// <summary>
        /// THE wrong-component decision. Called directly from the device's selection event with
        /// the assembly object that was grabbed:
        ///     grabbed in currentlyAcceptedParts (or tolerated)  -> fine
        ///     otherwise, while an action is armed                -> ONE wrong-component event
        /// One event per grab episode; the episode ends when the last hand lets go.
        /// </summary>
        private void EvaluateGrab(GameObject part, string key, string source)
        {
            if (part == null) return;
            if (_episodes.TryGetValue(part, out int refs) && refs > 0)
            {
                _episodes[part] = refs + 1;
                Debug.Log($"[EligibilityRuntime] selected={part.name}#{part.GetInstanceID()} already in an open grab episode (second hand) - no new decision.");
                return;
            }
            _episodes[part] = 1;

            bool physical = IsActive && !_completed;
            bool ack = _ackArmed;
            List<string> accepted = DescribeCurrentlyAccepted(out bool armed);
            bool tolerated = _toleratedObjects.Contains(part) || part.GetComponentInParent<AdaptiveAR.MR.EngineTurntable>() != null;
            bool eligible = tolerated || (physical && _acceptedObjects.Contains(part)) || (ack && _ackObjects.Contains(part));

            Debug.Log($"[EligibilityRuntime] selected={part.name}#{part.GetInstanceID()} acceptedObjects=[{string.Join(", ", accepted)}] eligible={eligible}" +
                      $"{(tolerated ? "(tolerated)" : "")} state={(physical ? "PHYSICAL" : (ack ? "READ" : "none"))} action={CurrentActionId()} source={source}");

            if (!armed) return;                                   // nothing to validate or acknowledge (AUTO / between actions)
            if (eligible)
            {
                if (ack && _ackObjects.Contains(part))
                {
                    _ackArmed = false;
                    if (logEvaluations) Debug.Log($"[StepValidator] instruction '{CurrentActionId()}' acknowledged by picking up '{part.name}'.");
                    try { OnAcknowledgedByGrab?.Invoke(key, part.transform); }
                    catch (Exception e) { Debug.LogError("[StepValidator] OnAcknowledgedByGrab handler threw: " + e); }
                }
                return;
            }
            if (!monitorWrongParts) return;

            _wrongEpisodeObject = part;
            if (_watched.TryGetValue(part, out Candidate wc)) _wrongHeld = wc;
            Debug.Log($"[WrongPart] FIRING object={part.name} action={CurrentActionId()} key={key}");
            try { OnWrongPartGrabbed?.Invoke(key, part.transform); }
            catch (Exception e) { Debug.LogError("[WrongPart] OnWrongPartGrabbed handler threw: " + e); }
        }

        private void EndGrabEpisode(GameObject part, string key, string source)
        {
            if (part == null || !_episodes.TryGetValue(part, out int refs)) return;
            if (refs > 1) { _episodes[part] = refs - 1; return; }
            _episodes.Remove(part);
            Debug.Log($"[GrabEvent] RELEASED object={part.name} id={part.GetInstanceID()} action={CurrentActionId()} source={source} key={key}");
            if (_wrongEpisodeObject == part)
            {
                _wrongEpisodeObject = null;
                _wrongHeld = null;
                Debug.Log($"[WrongPart] episode closed object={part.name} id={part.GetInstanceID()} (the next grab is a new episode)");
                try { OnWrongPartReleased?.Invoke(key, part.transform); }
                catch (Exception e) { Debug.LogError("[WrongPart] OnWrongPartReleased handler threw: " + e); }
            }
        }

        private void OnRuntimeSelected(MonoBehaviour interactor, MonoBehaviour interactable)
        {
            ResolveInteractable(interactable, out Rigidbody body, out Component pointable);
            GameObject part = body != null ? body.gameObject
                            : (pointable != null ? pointable.gameObject
                            : (interactable.transform.parent != null ? interactable.transform.parent.gameObject : interactable.gameObject));
            var grabbable = part.GetComponent<Oculus.Interaction.Grabbable>();

            Debug.Log($"[GrabRuntime] selectedObject={part.name}#{part.GetInstanceID()} path={PathOf(part.transform)} " +
                      $"rigidbody={(body != null ? body.name + "#" + body.GetInstanceID() : "NONE")} " +
                      $"pointable={(pointable != null ? PathOf(pointable.transform) + "#" + pointable.GetInstanceID() : "NONE")} " +
                      $"grabbable={(grabbable != null ? "own#" + grabbable.GetInstanceID() : "NONE")} " +
                      $"interactable={interactable.GetType().Name}@{PathOf(interactable.transform)} interactor={interactor.GetType().Name} currentAction={CurrentActionId()}");

            // Diagnostics only: the watch entry (grab-source coverage). The DECISION below does
            // not depend on it.
            string key = _watched.TryGetValue(part, out Candidate c) ? c.key : "unregistered:" + part.name;
            if (c == null && part.GetComponentInParent<AdaptiveAR.MR.EngineTurntable>() == null)
            {
                Debug.LogError($"[GrabRuntime] '{PathOf(part.transform)}' was selected on the device but was NOT in the watched set - watching it now.");
                Watch(key, part, true);
                if (_watched.TryGetValue(part, out c)) Link(c, interactable, interactable.GetType().Name);
            }
            if (c != null) { c.wasHeld = true; c.firedThisEpisode = true; c.pendingGrab = false; c.pendingRelease = false; c.grabSource = "interactor_event"; }

            EvaluateGrab(part, key, "interactor_event");
        }

        private void OnRuntimeUnselected(MonoBehaviour interactor, MonoBehaviour interactable)
        {
            ResolveInteractable(interactable, out Rigidbody body, out Component pointable);
            GameObject part = body != null ? body.gameObject : (pointable != null ? pointable.gameObject : null);
            if (part == null) return;
            string key = _watched.TryGetValue(part, out Candidate c) ? c.key : "unregistered:" + part.name;
            EndGrabEpisode(part, key, "interactor_event");
            if (c != null && !IsSelected(c)) { c.wasHeld = false; c.firedThisEpisode = false; c.pendingRelease = false; }
        }

        private void LogEligibilityRuntime(Candidate c)
        {
            bool physical = IsActive && !_completed;
            bool ack = _ackArmed;
            var accepted = new List<string>();
            if (physical) foreach (Candidate k in _candidates) { if (k.tf != null) accepted.Add($"{k.tf.name}#{k.tf.GetInstanceID()}"); }
            else if (ack) foreach (GameObject g in _ackObjects) { if (g != null) accepted.Add($"{g.name}#{g.GetInstanceID()}"); }
            foreach (GameObject g in _toleratedObjects) if (g != null) accepted.Add($"{g.name}#{g.GetInstanceID()}(tolerated)");
            GameObject go = c.tf.gameObject;
            bool eligible = (physical && _acceptedObjects.Contains(go)) || (ack && _ackObjects.Contains(go)) || _toleratedObjects.Contains(go);
            Debug.Log($"[EligibilityRuntime] selected={go.name}#{go.GetInstanceID()} acceptedObjects=[{string.Join(", ", accepted)}] eligible={eligible} " +
                      $"state={(physical ? "PHYSICAL" : (ack ? "READ" : "none"))} action={CurrentActionId()}");
        }

        private void WatchInteractable(MonoBehaviour interactable, Rigidbody body, Oculus.Interaction.IPointableElement element, string kind)
        {
            if (interactable == null) return;
            GameObject part = body != null ? body.gameObject
                            : (interactable.transform.parent != null ? interactable.transform.parent.gameObject : interactable.gameObject);

            var elementComp = element as Component;
            bool intentionalForward = elementComp != null && part.transform.IsChildOf(elementComp.transform);   // joined kit child -> its handle
            if (elementComp != null && elementComp.gameObject != part && !intentionalForward)
            {
                // Scene miswire: this interactable sits under one part but drives ANOTHER part's
                // Grabbable, so grabbing it selects/moves the other part. Repaired at runtime to
                // the part's own Grabbable; the scene reference still needs fixing by hand.
                var own = part.GetComponent<Oculus.Interaction.Grabbable>();
                Debug.LogError($"[GrabCoverage] MISWIRE: {kind} under '{PathOf(part.transform)}' drives the Grabbable of '{PathOf(elementComp.transform)}'" +
                               (own != null ? " - repaired at runtime to drive its own Grabbable. Fix the interactable's Pointable Element reference in the scene."
                                            : " and this part has no Grabbable of its own: NOT repaired."));
                if (own != null)
                {
                    if (interactable is Oculus.Interaction.HandGrab.HandGrabInteractable h) h.InjectOptionalPointableElement(own);
                    else if (interactable is Oculus.Interaction.GrabInteractable g) g.InjectOptionalPointableElement(own);
                }
            }

            string key = _watched.TryGetValue(part, out Candidate existing) ? existing.key : "unregistered:" + part.name;
            Watch(key, part, true);
            if (_watched.TryGetValue(part, out Candidate c)) Link(c, interactable, kind);
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

        private void Watch(string key, GameObject go, bool allowNoPointable)
        {
            if (go == null || _watched.ContainsKey(go)) return;
            var pointable = go.GetComponent<Oculus.Interaction.PointableElement>();
            if (pointable == null && !allowNoPointable) return;                   // not selectable
            if (go.GetComponent<Collider>() == null && !allowNoPointable) return;  // kit roots: not grabbable parts

            Candidate c = MakeCandidate(key, go);
            if (eventDrivenGrabs && pointable != null)
            {
                Candidate captured = c;
                c.handler = evt => HandlePointerEvent(captured, evt, "grabbable_event");
                pointable.WhenPointerEventRaised += c.handler;
            }
            _watched.Add(go, c);
            _watchedList.Add(c);
        }

        private void Link(Candidate c, MonoBehaviour interactable, string kind)
        {
            foreach (InteractableLink l in c.links) if (l.component == interactable) return;

            var link = new InteractableLink
            {
                component = interactable,
                kind = kind,
                pointable = interactable as Oculus.Interaction.IPointable,
                view = interactable as Oculus.Interaction.IInteractableView
            };
            if (eventDrivenGrabs && link.pointable != null)
            {
                Candidate captured = c;
                string source = kind + "_event";
                link.handler = evt => HandlePointerEvent(captured, evt, source);
                link.pointable.WhenPointerEventRaised += link.handler;
            }
            c.links.Add(link);
        }

        /// <summary>Selected right now, by the Grabbable's own points or by any interactable that drives it.</summary>
        private static bool IsSelected(Candidate c)
        {
            if (c.pointable != null && c.pointable.SelectingPointsCount > 0) return true;
            foreach (InteractableLink l in c.links)
                if (l.view != null && l.view.State == Oculus.Interaction.InteractableState.Select) return true;
            return false;
        }

        private static string DescribeSources(Candidate c)
        {
            var parts = new List<string>();
            parts.Add(c.pointable != null ? "grabbable_event+poll" : "poll");
            foreach (InteractableLink l in c.links) parts.Add(l.kind + "_event+state");
            return string.Join(",", parts);
        }

        /// <summary>
        /// Startup audit: every object that carries a grab interactable (what the participant
        /// can actually pick up) against what the wrong-component system monitors. ERROR for
        /// any interactable part that is not monitored, and for any registered assembly
        /// component that has no grab interactable at all.
        /// </summary>
        public void AuditGrabCoverage()
        {
            EnsureGrabSource();
            Transform root = EngineRoot();
            if (root == null) { Debug.LogError("[GrabCoverage] engine root not found (part.crankshaft unresolved)."); return; }

            var byPart = new Dictionary<GameObject, List<string>>();
            void Add(GameObject part, string kind)
            {
                if (!byPart.TryGetValue(part, out List<string> l)) byPart[part] = l = new List<string>();
                l.Add(kind);
            }
            foreach (var hgi in root.GetComponentsInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true))
                Add(hgi.Rigidbody != null ? hgi.Rigidbody.gameObject : (hgi.transform.parent != null ? hgi.transform.parent.gameObject : hgi.gameObject), "HandGrabInteractable");
            foreach (var gi in root.GetComponentsInChildren<Oculus.Interaction.GrabInteractable>(true))
                Add(gi.Rigidbody != null ? gi.Rigidbody.gameObject : (gi.transform.parent != null ? gi.transform.parent.gameObject : gi.gameObject), "GrabInteractable");

            int ok = 0, bad = 0;
            foreach (KeyValuePair<GameObject, List<string>> kv in byPart)
            {
                GameObject part = kv.Key;
                bool monitored = _watched.TryGetValue(part, out Candidate c);
                var pe = part.GetComponent<Oculus.Interaction.PointableElement>();
                Debug.Log($"[GrabCoverage] object={PathOf(part.transform)} id={part.GetInstanceID()} selectable=true active={part.activeInHierarchy} " +
                          $"monitored={monitored} key={(c != null ? c.key : "-")} interactables=[{string.Join(",", kv.Value)}] " +
                          $"sources={(monitored ? DescribeSources(c) : "none")} grabbable={(pe != null ? "own" : "NONE")}");
                if (monitored) ok++;
                else { bad++; Debug.LogError($"[GrabCoverage] ERROR: '{PathOf(part.transform)}' carries a grab interactable but is NOT monitored by the wrong-component system."); }
            }

            var seen = new HashSet<GameObject>();
            foreach (string key in guidanceRegistry.PartKeys())
            {
                if (GuidanceRegistry.IsKitHandleKey(key)) continue;
                if (!guidanceRegistry.TryResolveQuiet(key, out GameObject go) || go == null || !seen.Add(go)) continue;
                if (go.GetComponent<Collider>() == null) continue;              // kit roots
                if (!byPart.ContainsKey(go))
                    Debug.LogError($"[GrabCoverage] ERROR: assembly component '{key}' ({PathOf(go.transform)}) has NO grab interactable: it cannot be picked up on the headset.");
            }

            Debug.Log($"[GrabCoverage] {ok} interactable part(s) monitored, {bad} NOT monitored, {_watchedList.Count} watched in total.");
        }

        private void HandlePointerEvent(Candidate c, Oculus.Interaction.PointerEvent evt, string source)
        {
            if (c == null || c.tf == null) return;
            switch (evt.Type)
            {
                case Oculus.Interaction.PointerEventType.Select:
                    if (c.pendingGrab) break;                                  // the other source already reported it
                    if (c.wasHeld)
                    {
                        // A second hand joining the same grab is the same episode. A lone Select
                        // while we still think the part is held means a release was missed:
                        // close that episode and open a new one rather than staying mute.
                        if (c.pointable != null && c.pointable.SelectingPointsCount > 1) break;
                        Debug.Log($"[GrabEvent] Select on object={c.tf.name} id={c.tf.GetInstanceID()} while still marked held: closing the stale episode first.");
                        c.pendingRelease = true;
                        c.releaseSource = source + "(stale)";
                    }
                    c.pendingGrab = true;
                    c.grabSource = source;
                    Debug.Log($"[GrabEvent] RECEIVED object={c.tf.name} id={c.tf.GetInstanceID()} action={CurrentActionId()} " +
                              $"source={source} selecting={(c.pointable != null ? c.pointable.SelectingPointsCount : -1)} key={c.key}");
                    break;
                case Oculus.Interaction.PointerEventType.Unselect:
                case Oculus.Interaction.PointerEventType.Cancel:
                    if (!IsSelected(c))
                    {
                        c.pendingRelease = true;
                        c.releaseSource = source;
                        if (c.wasHeld)
                            Debug.Log($"[GrabEvent] RELEASED object={c.tf.name} id={c.tf.GetInstanceID()} action={CurrentActionId()} " +
                                      $"source={source}({evt.Type}) key={c.key}");
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
                try { ProcessCandidate(c); }
                catch (Exception e) { Debug.LogError($"[GrabSource] processing '{c.tf.name}' threw (the other parts are still processed): {e}"); }
            }
        }

        private void ProcessCandidate(Candidate c)
        {
            {
                bool held = IsSelected(c);
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
                    if (!releaseSource.EndsWith("_event") && !releaseSource.Contains("stale"))
                        Debug.Log($"[GrabEvent] RELEASED object={c.tf.name} id={c.tf.GetInstanceID()} action={CurrentActionId()} " +
                                  $"source=poll key={c.key}");
                    HandleRelease(c);
                }

                if (grab && !c.wasHeld)
                {
                    c.wasHeld = true;
                    if (!source.EndsWith("_event"))
                        Debug.Log($"[GrabEvent] RECEIVED object={c.tf.name} id={c.tf.GetInstanceID()} action={CurrentActionId()} " +
                                  $"source=poll key={c.key}");
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
            c.firedThisEpisode = true;

            // With interactors hooked, the selection event decides (EvaluateGrab); this path is
            // then diagnostics only. Without interactors it is the fallback decision path.
            if (_interactorCount > 0) { EvaluateGrab(go, c.key, source); return; }

            LogEligibilityRuntime(c);

            if (!physical && !ack)
            {
                Debug.Log($"[Eligibility] object={go.name} requested=<none> eligible=n/a reason=\"no action armed: nothing to validate or acknowledge\"");
                return;
            }

            if (_toleratedObjects.Contains(go))
            {
                Debug.Log($"[Eligibility] object={go.name} requested={RoleNameOf(physical ? PartKey : _ackPartKey) ?? "<none>"} eligible=tolerated " +
                          $"reason=\"kit handle: the loose subassembly may be picked up and turned; not a wrong part, not an error\" key={c.key} action={actionId} source={source}");
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
            c.firedThisEpisode = false;
            if (_interactorCount > 0) { EndGrabEpisode(c.tf != null ? c.tf.gameObject : null, c.key, "poll"); return; }
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

            FollowKitHandle();
            ChooseTarget();

            bool ok = WithinTolerance(_active, out float posErr, out float rotErr);
            bool inZone = posErr <= _attemptZone;

            UpdateInsertionGate(posErr, inZone, anyHeld);
            if (ok && _insertion && !InsertionApproachAllowed())
            {
                ok = false;
                InsertionBlocked = true;
            }
            else InsertionBlocked = false;

            UpdateProximity(anyHeld, inZone, ok);
            LiveLog(posErr, rotErr, anyHeld, inZone);

            // --- attempt lifecycle: enter zone -> success | leave zone | release inside ---
            if (inZone && !_inAttempt && (anyHeld || HasBeenHandled))
            {
                _inAttempt = true;
                AttemptId++;
                if (logEvaluations) Debug.Log($"[StepValidator] attempt {AttemptId} begins on '{_active.key}' (entered the {_attemptZone * 100f:F0} cm zone).");
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
                    // A deliberate placement: the part was let go near the target without fitting.
                    FailAttempt("released_in_zone", counted: true);
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
                // Exploring: carried out of the zone again. Diagnostic only, never an error.
                FailAttempt("left_zone", counted: false);
            }

            // --- live cue ---
            if (!inZone) CurrentCue = _stickyAlmostThere && !anyHeld ? Cue.AlmostThere : Cue.None;
            else if (posErr > _posTol) CurrentCue = Cue.MoveCloser;
            else CurrentCue = Cue.TurnToMatch;
        }

        /// <summary>Far / Near / Valid from the distance to the actual target while the part is held.</summary>
        private void UpdateProximity(bool anyHeld, bool inZone, bool valid)
        {
            Proximity p = !anyHeld ? Proximity.None : (valid ? Proximity.Valid : (inZone ? Proximity.Near : Proximity.Far));
            if (p == CurrentProximity) return;
            CurrentProximity = p;
            try { OnProximityChanged?.Invoke(p, _active != null ? _active.tf : null); }
            catch (Exception e) { Debug.LogError("[StepValidator] OnProximityChanged handler threw: " + e); }
        }

        /// <summary>
        /// Insertion parts (bolts, nuts, pin, camshaft) must come in along their axis. The
        /// approach direction is the part's travel over the last 0.3 s, judged while it is in
        /// the zone but not yet in position tolerance; across the axis = blocked until the
        /// part backs out and comes in again along it.
        /// </summary>
        private Vector3 _entryCentre;
        private bool _hasEntry;
        private float _nextInsertionLog;

        /// <summary>
        /// Final-approach check for insertion parts. The approach vector is from where the
        /// part's centre ENTERED the near zone to where it is now (falling back to its travel
        /// over the last 0.5 s when it entered at the target): if that vector is within 55 deg
        /// of the insertion axis (either direction) the part may be accepted; across the axis
        /// it is simply not accepted yet - no error, no spam - until it backs out of the zone
        /// and comes in along the axis. Evaluated whenever the pose is otherwise valid.
        /// </summary>
        private void UpdateInsertionGate(float posErr, bool inZone, bool anyHeld)
        {
            if (!_insertion || _active == null || _active.tf == null || CurrentTarget == null) return;

            float now = Time.time;
            Vector3 centre = CentreWorld(_active.tf, _active.centreLocal);
            _trail.Add(new KeyValuePair<float, Vector3>(now, centre));
            while (_trail.Count > 0 && now - _trail[0].Key > 0.4f) _trail.RemoveAt(0);

            if (!inZone)
            {
                _hasEntry = false;
                _approachOk = false;
                _approachDecided = false;
                return;
            }
            if (!_hasEntry) { _hasEntry = true; _entryCentre = centre; }
            JudgeRecentApproach();
        }

        /// <summary>
        /// Secondary to pose validation. While the part is in the near zone, every stretch of
        /// recent travel (>= 1.5 cm over the last 0.4 s) is judged against the insertion axis;
        /// one stretch within 55 deg of it (either way) latches "along" until the part leaves
        /// the zone. Position and axis must pass anyway; if the approach never ran along the
        /// axis the part is simply not snapped yet - no error, no flash.
        /// </summary>
        private void JudgeRecentApproach()
        {
            if (_approachOk || _trail.Count < 2 || _active == null || CurrentTarget == null) return;
            Vector3 travel = CentreWorld(_active.tf, _active.centreLocal) - _trail[0].Value;
            if (travel.magnitude < 0.015f) return;
            Vector3 axis = (CurrentTarget.rotation * _active.axisLocal).normalized;
            float angle = Vector3.Angle(travel.normalized, axis);
            angle = Mathf.Min(angle, 180f - angle);
            _lastApproachAngle = angle;
            _approachDecided = true;
            if (angle <= 55f)
            {
                _approachOk = true;
                Debug.Log($"[InsertionCheck] part={_active.tf.name}#{_active.tf.GetInstanceID()} approachAngle={angle:F0} allowed=true (travel {travel.magnitude * 100f:F1} cm along the axis; latched until the part leaves the zone)");
            }
        }

        private bool InsertionApproachAllowed()
        {
            if (!_insertion) return true;
            if (_active == null || _active.tf == null || CurrentTarget == null) return true;
            if (_approachOk) return true;
            if (Time.time >= _nextInsertionLog)
            {
                _nextInsertionLog = Time.time + 0.5f;
                Debug.Log($"[InsertionCheck] part={_active.tf.name}#{_active.tf.GetInstanceID()} approachAngle={(_approachDecided ? _lastApproachAngle.ToString("F0") : "n/a")} allowed=false " +
                          "(pose valid but the recent approach was not along the insertion axis; back out and come in along it)");
            }
            return false;
        }

        /// <summary>
        /// Loose subassembly on the mat: when the kit handle (head) is moved by the hand, the
        /// kit's ghosts and therefore the rod/pin targets move with it.
        /// </summary>
        private void FollowKitHandle()
        {
            if (_followHandle == null || _followKitRoot == null) return;
            Transform headGhost = _followKitRoot.Find("PistonHead");
            if (headGhost == null) return;
            if (Vector3.Distance(headGhost.position, _followHandle.position) < 0.0005f && Quaternion.Angle(headGhost.rotation, _followHandle.rotation) < 0.1f) return;
            _followKitRoot.rotation = _followHandle.rotation * Quaternion.Inverse(headGhost.localRotation);
            _followKitRoot.position += _followHandle.position - headGhost.position;
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

            bool rollIgnored = _symmetry == AxisSymmetry.AxialFree || _symmetry == AxisSymmetry.AxialFreeFlip;
            if (inZone)
                Debug.Log($"[ValidationPose] part={_active.tf.name}#{_active.tf.GetInstanceID()} positionError={posErr * 100f:F1}cm axisError={axisAngle:F1} ignoredRoll={(rollIgnored ? roll.ToString("F1") : "n/a")} " +
                          $"rotationError={rotErr:F1} tolPos={_posTol * 100f:F1}cm tolRot={_rotTol:F0} posOK={posErr <= _posTol} rotOK={rotErr <= _rotTol}");
            string variant = _symmetry == AxisSymmetry.HalfTurnAboutAxis
                ? (Quaternion.Angle(EffectiveTargetRotation(_active), CurrentTarget.rotation) < 0.5f ? "A(authored)" : "B(180deg)")
                : (_symmetry == AxisSymmetry.AxialFreeFlip && Vector3.Dot(partAxis, targetAxis) < 0f ? "flipped" : "authored");
            Debug.Log($"{Tag("Validation")} part={Describe(_active.tf)} target={Describe(CurrentTarget)} " +
                      $"positionError={posErr:F4} rotationError={rotErr:F1} posOK={posErr <= _posTol} rotOK={rotErr <= _rotTol} " +
                      $"axisError={axisAngle:F1} rollIgnored={rollIgnored} roll={roll:F1} symmetryVariant={variant} sym={_symmetry} axisLocal={_active.axisLocal} " +
                      $"armed={IsActive} held={anyHeld} inZone={inZone} tol={_posTol:F3}m/{_rotTol:F0}deg " +
                      $"proximity={CurrentProximity} insertion={(_insertion ? (_approachDecided ? (_approachOk ? "along" : "ACROSS") : "undecided") : "n/a")} " +
                      $"attempt={AttemptId} partCentre={CentreWorld(_active.tf, _active.centreLocal)} targetCentre={CentreWorld(CurrentTarget, _active.centreLocal)}");
        }

        /// <summary>Either-side actions: the active part is judged against the nearest FREE target.</summary>
        private void ChooseTarget()
        {
            if (_targets.Count < 2 || _active == null || _active.tf == null) return;
            TargetOption best = null;
            float bestD = float.MaxValue;
            foreach (TargetOption t in _targets)
            {
                if (t.tf == null || _occupiedTargets.Contains(t.tf)) continue;
                float d = Vector3.Distance(CentreWorld(_active.tf, _active.centreLocal), CentreWorld(t.tf, _active.centreLocal));
                if (d < bestD) { bestD = d; best = t; }
            }
            if (best == null || best.tf == CurrentTarget) return;

            CurrentTarget = best.tf;
            TargetKey = best.key;
            _targetRadius = best.radius;
            _inAttempt = false;
            if (logEvaluations) Debug.Log($"[StepValidator] nearest free target is now '{best.key}' ({bestD * 100f:F1} cm from '{_active.tf.name}').");
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
                float d = Vector3.Distance(CentreWorld(c.tf, c.centreLocal), CentreWorld(CurrentTarget, c.centreLocal));
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
            positionError = PositionError(c);
            rotationError = OrientationError(c);
            return positionError <= _posTol && rotationError <= _rotTol;
        }

        /// <summary>
        /// Distance between the part's geometric centre and where that centre is on the
        /// target. The pivot of a bolt, nut or cap is not on its symmetry axis, so comparing
        /// pivots made a free roll or an equivalent flip fail on POSITION; the centre does not move.
        /// </summary>
        private float PositionError(Candidate c)
        {
            return Vector3.Distance(CentreWorld(c.tf, c.centreLocal), CentreWorld(CurrentTarget, c.centreLocal));
        }

        /// <summary>
        /// Orientation error under the role's symmetry rule. Axis modes compare the part's
        /// long axis with the target's; roll about that axis is ignored or taken modulo a
        /// half turn as the rule says.
        /// </summary>
        /// <summary>The target rotation the part is actually judged against (the nearer equivalent orientation for the cap).</summary>
        private Quaternion EffectiveTargetRotation(Candidate c)
        {
            if (_symmetry != AxisSymmetry.HalfTurnAboutAxis || c == null) return CurrentTarget.rotation;
            Quaternion a = CurrentTarget.rotation;
            Quaternion b = CurrentTarget.rotation * Quaternion.AngleAxis(180f, c.axisLocal);
            return Quaternion.Angle(c.tf.rotation, a) <= Quaternion.Angle(c.tf.rotation, b) ? a : b;
        }

        private float OrientationError(Candidate c)
        {
            if (_symmetry == AxisSymmetry.None)
                return Quaternion.Angle(c.tf.rotation, CurrentTarget.rotation);

            if (_symmetry == AxisSymmetry.HalfTurnAboutAxis)
                return Quaternion.Angle(c.tf.rotation, EffectiveTargetRotation(c));

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

        private void FailAttempt(string trigger, bool counted)
        {
            if (!_inAttempt) return;
            _inAttempt = false;
            AttemptCount++;
            LastAttemptCounted = counted;

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
                Debug.Log($"[StepValidator] Attempt {AttemptId} {(counted ? "FAILED" : "abandoned")} ({trigger}) on '{_active.key}': {LastErrorType}; " +
                          $"closest {pos * 100f:F1} cm, orientation {rot:F0} deg (tol {_posTol * 100f:F1} cm / {_rotTol:F0} deg); error_counted={counted}.");

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
            LastAttemptCounted = false;
            if (AttemptId == 0) AttemptId++;
            Transform placedPart = _active.tf;
            if (_insertion && logEvaluations) Debug.Log($"[Insertion] '{_active.key}' accepted after an approach along its axis ({_lastApproachAngle:F0} deg off).");
            UpdateProximity(true, true, true);
            LastErrorType = null;
            LastAttemptPositionOk = true;
            LastAttemptOrientationOk = true;
            LastRejectReason = RejectReason.None;
            CurrentCue = Cue.None;
            _consumed.Add(_active.key);

            string tag = Tag("Success");
            string placedTag = PlacedTag();
            string parentBefore = _active.tf.parent != null ? $"{_active.tf.parent.name}#{_active.tf.parent.GetInstanceID()}" : "<root>";
            Vector3 snapPos = CurrentTarget.position;
            _occupiedTargets.Add(CurrentTarget);
            Debug.Log($"{tag} ENTER part={Describe(_active.tf)} target={Describe(CurrentTarget)} targetKey={TargetKey} parentBefore={parentBefore} " +
                      $"distance={posErr:F4} angle={rotErr:F1} trigger={trigger} tol={_posTol:F3}m/{_rotTol:F0}deg");
            if (placedTag != tag)
                Debug.Log($"{placedTag} real={Describe(_active.tf)} parentBefore={parentBefore} kit={GuidanceRegistry.KitOf(PartKey)} target={TargetKey}");

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
                snapped = PositionError(_active) < 0.002f
                          && Quaternion.Angle(_active.tf.rotation, EffectiveTargetRotation(_active)) < 1f;
            }
            catch (Exception e) { Debug.LogError($"{tag} snap threw: {e}"); }

            try
            {
                padlock = _active.tf.GetComponent<PlacementLock>();
                if (padlock == null) padlock = _active.tf.gameObject.AddComponent<PlacementLock>();
                // The head placed on the mat stays liftable (the subassembly may be turned); a
                // part installed on the engine is locked for good.
                bool headOnMat = GuidanceRegistry.RoleOf(PartKey) == guidanceRegistry.KitHandleRole
                                 && !string.IsNullOrEmpty(_action.targetKey) && _action.targetKey.StartsWith("ghost.PistonKit");
                padlock.Repositionable = headOnMat;
                padlock.LockAt(CurrentTarget);
                locked = padlock.IsLocked;
            }
            catch (Exception e) { Debug.LogError($"{tag} lock threw: {e}"); }
            PartWatch.Log("Succeed(after lock)", _active.tf);

            try
            {
                BindAndJoin(_active);
                // Locked onto the ENGINE (target under the turntable) and not joined to a kit
                // handle: the engine's turntable adopts it so it turns with the engine.
                var turntable = AdaptiveAR.MR.EngineTurntable.Current;
                if (turntable != null && CurrentTarget.IsChildOf(turntable.transform) && !_active.tf.IsChildOf(turntable.transform))
                    turntable.AdoptInstalled(_active.tf);
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

            // The placed part is watched for two seconds: it must stay visible, active, at its
            // snap pose and under its kit handle. Anything else is an error with the state.
            Transform handle = null;
            string kitName = GuidanceRegistry.KitOf(PartKey);
            if (kitName != null && guidanceRegistry != null) guidanceRegistry.TryGetKitHandle(kitName, out handle);
            StartCoroutine(WatchPlacedPart(placedPart, placedTag, parentBefore, snapPos, handle, kitName));
        }

        /// <summary>[Piston2RodSuccess] for kit 2's rod, [Piston1HeadSuccess] ..., else the role tag.</summary>
        private string PlacedTag()
        {
            string kit = GuidanceRegistry.KitOf(PartKey);
            if (kit == null) return Tag("Success");
            string role = RoleNameOf(PartKey) ?? "";
            string shortRole = role == "ConnectingRod" ? "Rod" : role == "ConnectingPin" ? "Pin" : role == "PistonHead" ? "Head"
                             : role == "PistonEnd" ? "Cap" : role.StartsWith("pistonBolt") ? "Bolt" : role.StartsWith("PistonNut") ? "Nut" : role;
            return "[Piston" + kit.Substring(kit.Length - 1) + shortRole + "Success]";
        }

        private System.Collections.IEnumerator WatchPlacedPart(Transform part, string tag, string parentBefore, Vector3 snapPos, Transform handle, string kit)
        {
            float[] delays = { 0f, 0.5f, 2f };
            foreach (float d in delays)
            {
                if (d > 0f) yield return new WaitForSeconds(d); else yield return null;
                if (part == null) { Debug.LogError($"{tag} the placed part was DESTROYED."); yield break; }

                var r = part.GetComponentInChildren<Renderer>(true);
                var padlock = part.GetComponent<PlacementLock>();
                var body = part.GetComponent<Rigidbody>();
                bool active = part.gameObject.activeInHierarchy;
                bool visible = active && r != null && r.enabled;
                float dist = Vector3.Distance(part.position, snapPos);
                bool underHandle = handle == null || part == handle || part.IsChildOf(handle);

                Debug.Log($"{tag} +{d:F1}s real={part.name}#{part.GetInstanceID()} visible={visible} active={active} " +
                          $"parent={(part.parent != null ? part.parent.name + "#" + part.parent.GetInstanceID() : "<root>")} (before: {parentBefore}) " +
                          $"position={part.position} distanceFromSnap={dist * 1000f:F0}mm locked={(padlock != null && padlock.IsLocked)} " +
                          $"kinematic={(body != null ? body.isKinematic.ToString() : "n/a")} kit={kit ?? "-"} handle={(handle != null ? handle.name + "#" + handle.GetInstanceID() : "-")} underKitHandle={underHandle}");

                if (!visible) Debug.LogError($"{tag} the placed part is NOT visible (active={active}, renderer={(r != null ? r.enabled.ToString() : "none")}).");
                if (dist > 0.05f) Debug.LogError($"{tag} the placed part is {dist * 100f:F1} cm away from its snap pose.");
                if (!underHandle) Debug.LogError($"{tag} the placed part is not under its kit handle '{handle.name}' (parent is '{(part.parent != null ? part.parent.name : "<root>")}').");
            }
        }

        private void SnapToTarget(Candidate c)
        {
            if (c.body != null)
            {
                c.body.linearVelocity = Vector3.zero;
                c.body.angularVelocity = Vector3.zero;
                c.body.isKinematic = true;
            }

            c.tf.rotation = EffectiveTargetRotation(c);
            // Rotation first, then align the geometric centres (identical to the pivot snap when
            // the rotations match; keeps the equivalent flip/roll in place when they do not).
            c.tf.position += CentreWorld(CurrentTarget, c.centreLocal) - CentreWorld(c.tf, c.centreLocal);
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
            ForwardGrabsToHandle(c.tf, handle);
            if (!c.tf.gameObject.activeSelf)
            {
                c.tf.gameObject.SetActive(true);
                Debug.LogError($"[StepValidator] '{c.key}' was INACTIVE at join time; re-activated.");
            }
            if (!handle.gameObject.activeInHierarchy)
                Debug.LogError($"[StepValidator] kit handle '{handle.name}' of {kit} is not active in the hierarchy: the joined part would be invisible.");
            if (logEvaluations) Debug.Log($"[StepValidator] '{c.key}' joined {kit} under '{handle.name}'#{handle.GetInstanceID()}.");
        }

        /// <summary>
        /// A part joined to the loose subassembly stays grabbable, but grabbing it moves the
        /// WHOLE subassembly: its grab interactables are pointed at the kit handle's
        /// Grabbable. Its own lock keeps it rigid relative to the handle. The handle's final
        /// lock (installation) disables everything underneath it again.
        /// </summary>
        private void ForwardGrabsToHandle(Transform child, Transform handle)
        {
            var handleGrabbable = handle != null ? handle.GetComponent<Oculus.Interaction.Grabbable>() : null;
            if (handleGrabbable == null) return;

            // Only while the subassembly is LOOSE (handle unlocked or repositionable on the mat).
            // A cap, bolt or nut joined after the piston is installed must stay fully locked:
            // forwarding then would let a bolt move the installed piston.
            var handleLock = handle.GetComponent<PlacementLock>();
            if (handleLock != null && handleLock.IsLocked && !handleLock.Repositionable)
            {
                if (logEvaluations) Debug.Log($"[StepValidator] '{child.name}' joined an INSTALLED piston: stays locked (no subassembly grab).");
                return;
            }

            int n = 0;
            foreach (var hgi in child.GetComponentsInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true))
            { hgi.InjectOptionalPointableElement(handleGrabbable); hgi.enabled = true; n++; }
            foreach (var gi in child.GetComponentsInChildren<Oculus.Interaction.GrabInteractable>(true))
            { gi.InjectOptionalPointableElement(handleGrabbable); gi.enabled = true; n++; }

            var padlock = child.GetComponent<PlacementLock>();
            if (padlock != null) padlock.ForwardedToHandle = true;
            if (logEvaluations) Debug.Log($"[StepValidator] '{child.name}' joined: {n} grab interactable(s) now move the subassembly via '{handle.name}'.");
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

            positionError = PositionError(_active);
            rotationError = OrientationError(_active);
        }
    }
}
