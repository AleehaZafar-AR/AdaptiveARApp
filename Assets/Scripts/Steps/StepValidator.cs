// File: StepValidator.cs
// Checks whether the operator placed the current action's part correctly, and makes
// the physical mating possible in the first place.
//
// Grab state
// ----------
// "Held" and "released" come straight from the Interaction SDK (the part's
// PointableElement reports how many pointers are selecting it). Parts with no
// Interaction SDK component fall back to a kinematic heuristic plus movement.
//
// Interchangeable parts
// ---------------------
// Validation is by ROLE, not by instance. An action that asks for
// part.PistonKit001.PistonHead accepts any unconsumed PistonHead from any kit. The
// instance that is actually locked becomes bound to the kit named by the action, so
// the rest of that kit's actions join their parts to THAT head, and a consumed
// instance can never satisfy a second assembly. The instance key is reported for
// logging so the session can be reconstructed.
//
// Wrong parts
// -----------
// Every other registered part is watched too. Picking up a part that does not match
// the required role raises OnWrongPartGrabbed; letting it go raises
// OnWrongPartReleased. Feedback therefore reflects what is in the hand now.
//
// Guided snap assembly
// --------------------
// Mechanical mating cannot happen with solid colliders: the pieces repel. While the
// active part is near its target - measured between the two shapes, not their
// pivots - collisions between it (and anything joined to it) and the receiving
// assembly are ignored; a release is frozen and judged where the hand let go; a valid
// release snaps, locks and joins; an invalid one restores collisions. Bench
// furniture (trays, work surface, tabletop) always stays solid.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class StepValidator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GuidanceRegistry guidanceRegistry;

        [Header("Timing")]
        [Tooltip("Seconds to wait after a release before judging the placement, so the part settles.")]
        [SerializeField] private float settleSeconds = 0.45f;

        [Tooltip("Speed below which a part counts as at rest, in metres per second.")]
        [SerializeField] private float restSpeed = 0.03f;

        [Tooltip("Seconds a part must sit in tolerance, untouched, to be accepted.")]
        [SerializeField] private float restAcceptSeconds = 0.8f;

        [Header("Behaviour")]
        [Tooltip("Accept a placement when the part comes to rest in tolerance, even with no release event.")]
        [SerializeField] private bool acceptOnRest = true;

        [Tooltip("Require the part to have been picked up before a resting placement counts.")]
        [SerializeField] private bool requireHandledBeforeRest = true;

        [Tooltip("Metres a part must move from where it started to count as handled, for " +
                 "parts with no Interaction SDK grab state to read.")]
        [SerializeField] private float handledMoveThreshold = 0.05f;

        [Tooltip("Accept any unconsumed part of the same role from any kit, not only the " +
                 "instance the action names.")]
        [SerializeField] private bool interchangeableRoles = true;

        [Tooltip("Watch every other registered part so picking up a wrong one gives feedback.")]
        [SerializeField] private bool monitorWrongParts = true;

        [Header("Guided snap assembly")]
        [Tooltip("Gap between the moving part's shape and the target's shape below which the " +
                 "mating assist is active. Shape-to-shape, so a long rod engages as its end " +
                 "reaches the head, not when its pivot does. Provisional; tune on the bench.")]
        [SerializeField] private float assistGapMeters = 0.06f;

        [Tooltip("Ignore collisions between the moving part and the receiving assembly while " +
                 "inside the assist gap, so mating geometry can overlap.")]
        [SerializeField] private bool suppressCollisionsNearTarget = true;

        [Tooltip("Freeze a part released inside the assist gap and judge it where the hand " +
                 "let go, instead of letting gravity move it during the settle.")]
        [SerializeField] private bool freezeOnReleaseNearTarget = true;

        [Tooltip("Seconds between a near-target release and its judgement.")]
        [SerializeField] private float nearReleaseSettleSeconds = 0.12f;

        [Tooltip("Objects whose name starts with any of these are never part of the receiving " +
                 "assembly: the moving part must still collide with them.")]
        [SerializeField] private string[] alwaysSolidNamePrefixes = { "tray", "Plane", "TabletopSupport", "WorkSurface" };

        [Header("Debug")]
        [SerializeField] private bool logEvaluations = true;

        /// <summary>Fired for every judged placement. (success, positionError, rotationError, trigger)</summary>
        public event Action<bool, float, float, string> OnAttemptEvaluated;

        /// <summary>Why the last placement was rejected, so feedback can be specific.</summary>
        public RejectReason LastRejectReason { get; private set; }

        /// <summary>Fired once when the current action's placement is accepted.</summary>
        public event Action OnStepValidated;

        /// <summary>An accepted part was picked up / let go. (instance key, part)</summary>
        public event Action<string, Transform> OnPartGrabbed;
        public event Action<string, Transform> OnPartReleased;

        /// <summary>A part that does NOT satisfy the current action was picked up / let go.</summary>
        public event Action<string, Transform> OnWrongPartGrabbed;
        public event Action<string, Transform> OnWrongPartReleased;

        public bool IsActive { get; private set; }
        public int AttemptCount { get; private set; }

        /// <summary>The key the action asked for (a role, for kit parts).</summary>
        public string PartKey { get; private set; }
        public string TargetKey { get; private set; }

        /// <summary>The instance currently being judged, e.g. part.PistonKit003.PistonHead.</summary>
        public string ActiveInstanceKey { get { return _active != null ? _active.key : null; } }

        /// <summary>The action being validated. Null when nothing is armed.</summary>
        public AssemblyAction CurrentAction { get { return _action; } }

        public Transform CurrentPart { get; private set; }
        public Transform CurrentTarget { get; private set; }

        /// <summary>True once the operator has actually picked an accepted part up.</summary>
        public bool HasBeenHandled { get; private set; }

        /// <summary>True while the mating assist is engaged for the active part.</summary>
        public bool IsAssisting { get; private set; }

        /// <summary>True while a wrong part is in the hand.</summary>
        public bool WrongPartHeld { get { return _wrongHeld != null; } }

        private class Candidate
        {
            public string key;
            public Transform tf;
            public Rigidbody body;
            public Oculus.Interaction.PointableElement pointable;
            public Vector3 startPos;
            public Vector3 prevPos;
            public bool wasKinematic;
            public bool wasHeld;
            public bool moved;
            public float radius;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly List<Candidate> _others = new List<Candidate>();

        // Instances already assembled. Survives across steps; cleared when the session restarts.
        private readonly HashSet<string> _consumed = new HashSet<string>();

        private Candidate _active;
        private Candidate _wrongHeld;
        private AssemblyAction _action;
        private bool _awaitingSettle;
        private float _settleTimer;
        private float _settleTarget;
        private string _settleTrigger;
        private float _inToleranceTimer;
        private bool _completed;
        private float _targetRadius;

        // assist state
        private readonly List<Collider> _receiving = new List<Collider>();
        private readonly List<Collider> _activeColliders = new List<Collider>();
        private Candidate _assistFor;
        private bool _frozenForJudgement;
        private bool _kinematicBeforeFreeze;

        // =====================================================================

        /// <summary>Forgets which instances have been assembled. Call when a session restarts.</summary>
        public void ResetConsumedParts()
        {
            _consumed.Clear();
        }

        /// <summary>
        /// Arms validation for an action. Returns false when the action does not use
        /// validation or nothing could be resolved - the caller then relies on manual advance.
        /// </summary>
        public bool BeginAction(AssemblyAction action)
        {
            Clear();
            _action = action;

            if (action == null || !action.RequiresPhysicalValidation)
                return false;

            PartKey = action.partKey;
            TargetKey = action.targetKey;
            settleSeconds = Mathf.Max(0.05f, action.settleSeconds);

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

            // Accepted instances: the action's keys, widened to every unconsumed instance of
            // the same role when roles are interchangeable.
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
                if (_consumed.Contains(key)) continue;                     // already assembled
                if (!guidanceRegistry.TryResolveQuiet(key, out GameObject go)) continue;

                // Installing a finished kit: the handle was locked when it was built, and this
                // is the action that moves it, so it becomes grabbable again here.
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

            // Everything else that can be picked up, for wrong-part feedback.
            if (monitorWrongParts)
            {
                foreach (string key in guidanceRegistry.PartKeys())
                {
                    if (acceptedSet.Contains(key)) continue;
                    if (!guidanceRegistry.TryResolveQuiet(key, out GameObject go) || go == null) continue;
                    if (go.GetComponent<Oculus.Interaction.PointableElement>() == null) continue;

                    // A joined kit component is part of an assembly now, not a loose part.
                    var padlock = go.GetComponent<PlacementLock>();
                    if (padlock != null && padlock.IsLocked) continue;

                    _others.Add(MakeCandidate(key, go));
                }
            }

            _active = _candidates[0];
            CurrentPart = _active.tf;
            IsActive = true;
            _completed = false;

            // Feedback reflects what is in the hand NOW, including across an action change.
            foreach (Candidate c in _others)
            {
                if (!c.wasHeld) continue;
                _wrongHeld = c;
                OnWrongPartGrabbed?.Invoke(c.key, c.tf);
                break;
            }

            if (logEvaluations)
                Debug.Log($"[StepValidator] Watching {_candidates.Count} accepted instance(s) for '{PartKey}' against " +
                          $"'{TargetKey}' (tolerance {action.positionToleranceMeters * 100f:F1} cm / " +
                          $"{action.rotationToleranceDegrees:F0} deg, assist gap {assistGapMeters * 100f:F0} cm); " +
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
                prevPos = go.transform.position,
                wasKinematic = body != null && body.isKinematic,
                wasHeld = pointable != null && pointable.SelectingPointsCount > 0,
                radius = ShapeRadius(go.transform)
            };
        }

        /// <summary>Bounding-sphere radius of a part's own mesh, metres. Works on inactive objects.</summary>
        private static float ShapeRadius(Transform t)
        {
            if (t == null) return 0f;
            var mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return 0f;
            Vector3 e = Vector3.Scale(mf.sharedMesh.bounds.extents, t.lossyScale);
            return new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z)).magnitude;
        }

        public void Clear()
        {
            DisengageAssist();
            UnfreezeIfFrozen();

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
            _awaitingSettle = false;
            _settleTimer = 0f;
            _inToleranceTimer = 0f;
            PartKey = null;
            TargetKey = null;
            _receiving.Clear();
        }

        // =====================================================================
        // Per frame
        // =====================================================================

        private void Update()
        {
            if (!IsActive || _completed || CurrentTarget == null) return;

            float dt = Mathf.Max(Time.deltaTime, 1e-5f);
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

                c.prevPos = c.tf.position;
            }

            MonitorWrongParts();

            // Whichever accepted part the operator is actually working with is the one judged.
            Candidate chosen = PickActive();
            if (chosen != null && chosen != _active)
            {
                _active = chosen;
                CurrentPart = chosen.tf;
                _inToleranceTimer = 0f;
                DisengageAssist();
            }

            HasBeenHandled = _active != null && _active.moved;

            if (grabbedNow != null)
            {
                UnfreezeIfFrozen();
                OnPartGrabbed?.Invoke(grabbedNow.key, grabbedNow.tf);
            }

            UpdateAssist();

            if (releasedNow != null)
            {
                OnPartReleased?.Invoke(releasedNow.key, releasedNow.tf);

                bool near = releasedNow == _active && IsNearTarget(releasedNow);

                if (near && freezeOnReleaseNearTarget)
                {
                    FreezeForJudgement(releasedNow);
                    BeginSettle(nearReleaseSettleSeconds, "release_near_target");
                }
                else
                {
                    BeginSettle(settleSeconds, "release");
                }
            }

            if (anyHeld)
            {
                _inToleranceTimer = 0f;
                return;
            }

            if (_awaitingSettle)
            {
                _settleTimer += dt;
                if (_settleTimer >= _settleTarget)
                {
                    _awaitingSettle = false;
                    Evaluate(_settleTrigger);
                }
                return;
            }

            if (!acceptOnRest) return;
            if (requireHandledBeforeRest && !HasBeenHandled) return;
            if (_active == null || _active.tf == null) return;

            float speed = _active.body != null && !_active.body.isKinematic
                ? _active.body.linearVelocity.magnitude
                : Vector3.Distance(_active.tf.position, _active.prevPos) / dt;

            if (speed <= restSpeed && WithinTolerance(_active, out _, out _))
            {
                _inToleranceTimer += dt;
                if (_inToleranceTimer >= restAcceptSeconds)
                    Evaluate("rest");
            }
            else
            {
                _inToleranceTimer = 0f;
            }
        }

        /// <summary>Raises grab/release events for parts that cannot satisfy this action.</summary>
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

        private void BeginSettle(float seconds, string trigger)
        {
            _awaitingSettle = true;
            _settleTimer = 0f;
            _settleTarget = Mathf.Max(0.02f, seconds);
            _settleTrigger = trigger;
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
                float score = c.moved ? d : d + 1000f;   // moved parts always win

                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            return best;
        }

        private bool WithinTolerance(Candidate c, out float positionError, out float rotationError)
        {
            positionError = Vector3.Distance(c.tf.position, CurrentTarget.position);
            rotationError = Quaternion.Angle(c.tf.rotation, CurrentTarget.rotation);

            return positionError <= _action.positionToleranceMeters
                && rotationError <= _action.rotationToleranceDegrees;
        }

        /// <summary>
        /// Shape-to-shape proximity: the pivot distance minus both bounding radii. A rod
        /// whose end is touching the head is "near" even though its pivot is far away.
        /// </summary>
        private bool IsNearTarget(Candidate c)
        {
            if (c == null || c.tf == null || CurrentTarget == null) return false;
            float pivotDistance = Vector3.Distance(c.tf.position, CurrentTarget.position);
            float gap = pivotDistance - c.radius - _targetRadius;
            float threshold = Mathf.Max(assistGapMeters, _action != null ? _action.positionToleranceMeters * 1.5f : 0f);
            return gap <= threshold;
        }

        // =====================================================================
        // Judgement
        // =====================================================================

        private void Evaluate(string trigger)
        {
            if (_completed || _active == null || _active.tf == null) return;

            bool ok = WithinTolerance(_active, out float posErr, out float rotErr);
            AttemptCount++;

            if (ok) LastRejectReason = RejectReason.None;
            else if (posErr > _action.positionToleranceMeters * 3f) LastRejectReason = RejectReason.TooFar;
            else if (posErr > _action.positionToleranceMeters) LastRejectReason = RejectReason.TooFar;
            else LastRejectReason = RejectReason.WrongRotation;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Attempt {AttemptCount} ({trigger}) on '{_active.key}': " +
                          $"{(ok ? "ACCEPTED" : "rejected")}  off by {posErr * 100f:F1} cm / {rotErr:F0} deg");

            OnAttemptEvaluated?.Invoke(ok, posErr, rotErr, trigger);

            if (!ok)
            {
                _inToleranceTimer = 0f;
                UnfreezeIfFrozen();
                DisengageAssist();
                return;
            }

            _completed = true;
            IsActive = false;
            _consumed.Add(_active.key);

            SnapToTarget(_active);

            var padlock = _active.tf.GetComponent<PlacementLock>();
            if (padlock == null) padlock = _active.tf.gameObject.AddComponent<PlacementLock>();
            padlock.LockAt(CurrentTarget);

            BindAndJoin(_active);

            _frozenForJudgement = false;
            _assistFor = null;
            IsAssisting = false;

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

        /// <summary>
        /// The instance that just locked is now part of the kit the ACTION named (not the
        /// kit its own key came from). The handle role binds the kit; any other role joins
        /// the bound handle as a child so the finished assembly moves as one.
        /// </summary>
        private void BindAndJoin(Candidate c)
        {
            if (guidanceRegistry == null || c == null || c.tf == null) return;

            string kit = GuidanceRegistry.KitOf(PartKey);     // the action's kit
            string role = GuidanceRegistry.RoleOf(c.key);     // the instance's role
            if (kit == null || role == null) return;

            if (role == guidanceRegistry.KitHandleRole)
            {
                guidanceRegistry.BindKitHandle(kit, c.tf);
                if (logEvaluations)
                    Debug.Log($"[StepValidator] '{c.key}' is now the handle of {kit}.");
                return;
            }

            if (!guidanceRegistry.TryGetKitHandle(kit, out Transform handle) || handle == null) return;
            if (handle == c.tf || c.tf.IsChildOf(handle)) return;

            c.tf.SetParent(handle, true);

            if (logEvaluations)
                Debug.Log($"[StepValidator] '{c.key}' joined {kit} under '{handle.name}'.");
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
            else if (!near && IsAssisting && !_frozenForJudgement)
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

        /// <summary>
        /// Everything the moving part may overlap: the colliders under the anchored
        /// workspace, minus the part itself (and whatever is joined to it), minus the
        /// bench furniture it must keep resting on.
        /// </summary>
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
            // The work surface is bench furniture by type, not by name: the serialized
            // prefix list in the scene predates it and would not include it.
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

        private void FreezeForJudgement(Candidate c)
        {
            if (c == null || c.body == null || _frozenForJudgement) return;

            _kinematicBeforeFreeze = c.body.isKinematic;
            c.body.linearVelocity = Vector3.zero;
            c.body.angularVelocity = Vector3.zero;
            c.body.isKinematic = true;
            _frozenForJudgement = true;
        }

        private void UnfreezeIfFrozen()
        {
            if (!_frozenForJudgement) return;
            _frozenForJudgement = false;

            if (_active != null && _active.body != null && !_completed)
                _active.body.isKinematic = _kinematicBeforeFreeze;
        }

        /// <summary>Current placement error, for a live on-device readout. Zero when inactive.</summary>
        public void GetCurrentError(out float positionError, out float rotationError)
        {
            positionError = 0f;
            rotationError = 0f;

            if (!IsActive || _active == null || _active.tf == null || CurrentTarget == null) return;

            positionError = Vector3.Distance(_active.tf.position, CurrentTarget.position);
            rotationError = Quaternion.Angle(_active.tf.rotation, CurrentTarget.rotation);
        }
    }
}
