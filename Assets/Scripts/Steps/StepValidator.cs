// File: StepValidator.cs
// Checks whether the operator placed the current action's part correctly, and makes
// the physical mating possible in the first place.
//
// Replaces the old SnapToPosition, which was defective (a fresh coroutine every
// frame, a position threshold of 10.0 that was always true, Euler-angle distance
// that wraps at 0/360, and a UnityEditor import that breaks Android builds).
//
// Grab state
// ----------
// "Held" and "released" come straight from the Interaction SDK (the part's
// PointableElement reports how many pointers are selecting it). The old heuristic
// read Rigidbody.isKinematic, which the SDK toggles while holding - but a part that
// is kinematic by design read as "held forever" and was never judged. Parts with no
// Interaction SDK component fall back to that heuristic plus movement.
//
// Guided snap assembly
// --------------------
// Mechanical mating (a rod entering a piston head, a pin through both) cannot
// happen with solid colliders: the pieces repel. So, while the active part is within
// a configurable radius of its target:
//   - collisions between it (and anything joined to it) and the receiving assembly
//     are ignored, so the geometry may overlap;
//   - a release freezes the part where the hand let go and judges it at once;
//   - a valid release snaps, locks, joins the part to its kit and clears the assist;
//   - an invalid release restores collisions and lets the part go again.
// Away from the target the part behaves normally: free, solid, under gravity.
//
// Kits
// ----
// When a component of a kit (part.PistonKit001.ConnectingRod) locks, it is parented
// under the kit's handle (part.PistonKit001 -> the piston head). The handle is then
// the single thing the participant moves to install the finished assembly, and the
// joined components ride with it as plain transform children. When the install
// action begins, the handle's lock is lifted so it can be grabbed again.

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

        [Tooltip("Require the part to have been picked up before a resting placement counts. " +
                 "Stops a step auto-completing because a part spawned close to its target.")]
        [SerializeField] private bool requireHandledBeforeRest = true;

        [Tooltip("Metres a part must move from where it started to count as handled, for " +
                 "parts with no Interaction SDK grab state to read.")]
        [SerializeField] private float handledMoveThreshold = 0.05f;

        [Header("Guided snap assembly")]
        [Tooltip("Within this distance of the target the mating assist is active: collisions " +
                 "with the receiving assembly are ignored and a release is judged in place. " +
                 "Provisional; tune on the bench.")]
        [SerializeField] private float assistRadiusMeters = 0.08f;

        [Tooltip("Ignore collisions between the moving part and the receiving assembly while " +
                 "inside the assist radius, so mating geometry can overlap.")]
        [SerializeField] private bool suppressCollisionsNearTarget = true;

        [Tooltip("Freeze a part released inside the assist radius and judge it where the hand " +
                 "let go, instead of letting gravity move it during the settle.")]
        [SerializeField] private bool freezeOnReleaseNearTarget = true;

        [Tooltip("Seconds between a near-target release and its judgement. Short: just enough " +
                 "for the SDK's release to finish.")]
        [SerializeField] private float nearReleaseSettleSeconds = 0.12f;

        [Tooltip("Objects whose name starts with any of these are never part of the receiving " +
                 "assembly: the moving part must still collide with them.")]
        [SerializeField] private string[] alwaysSolidNamePrefixes = { "tray", "Plane", "TabletopSupport" };

        [Header("Debug")]
        [SerializeField] private bool logEvaluations = true;

        /// <summary>Fired for every judged placement. (success, positionError, rotationError, trigger)</summary>
        public event Action<bool, float, float, string> OnAttemptEvaluated;

        /// <summary>Why the last placement was rejected, so feedback can be specific.</summary>
        public RejectReason LastRejectReason { get; private set; }

        /// <summary>Fired once when the current action's placement is accepted.</summary>
        public event Action OnStepValidated;

        /// <summary>Fired when the judged part is picked up / let go. Argument: part key.</summary>
        public event Action<string> OnPartGrabbed;
        public event Action<string> OnPartReleased;

        public bool IsActive { get; private set; }
        public int AttemptCount { get; private set; }
        public string PartKey { get; private set; }
        public string TargetKey { get; private set; }

        /// <summary>The action being validated. Null when nothing is armed.</summary>
        public AssemblyAction CurrentAction { get { return _action; } }

        /// <summary>The part currently being judged. Null when nothing is armed.</summary>
        public Transform CurrentPart { get; private set; }

        /// <summary>The pose the part must reach.</summary>
        public Transform CurrentTarget { get; private set; }

        /// <summary>True once the operator has actually picked the part up.</summary>
        public bool HasBeenHandled { get; private set; }

        /// <summary>True while the mating assist is engaged for the active part.</summary>
        public bool IsAssisting { get; private set; }

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
        }

        private readonly List<Candidate> _candidates = new List<Candidate>();

        // Parts already assembled. Survives across steps so an identical part is not
        // offered twice; cleared when the session restarts.
        private readonly HashSet<string> _consumed = new HashSet<string>();

        private Candidate _active;
        private AssemblyAction _action;
        private bool _awaitingSettle;
        private float _settleTimer;
        private float _settleTarget;
        private string _settleTrigger;
        private float _inToleranceTimer;
        private bool _completed;

        // assist state
        private readonly List<Collider> _receiving = new List<Collider>();
        private readonly List<Collider> _activeColliders = new List<Collider>();
        private Candidate _assistFor;
        private bool _frozenForJudgement;
        private bool _kinematicBeforeFreeze;

        // =====================================================================

        /// <summary>Forgets which parts have been assembled. Call when a session restarts.</summary>
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

            foreach (string key in action.AcceptedPartKeys())
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
                            Debug.Log($"[StepValidator] Kit handle '{key}' unlocked for installation.");
                    }
                }

                Rigidbody body = go.GetComponent<Rigidbody>();
                var pointable = go.GetComponent<Oculus.Interaction.PointableElement>();

                _candidates.Add(new Candidate
                {
                    key = key,
                    tf = go.transform,
                    body = body,
                    pointable = pointable,
                    startPos = go.transform.position,
                    prevPos = go.transform.position,
                    wasKinematic = body != null && body.isKinematic,
                    wasHeld = pointable != null && pointable.SelectingPointsCount > 0
                });
            }

            if (_candidates.Count == 0)
            {
                Debug.LogWarning($"[StepValidator] Action '{action.Id}': no accepted part could be " +
                                 "resolved; this action cannot be validated.", this);
                return false;
            }

            _active = _candidates[0];
            CurrentPart = _active.tf;
            IsActive = true;
            _completed = false;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Watching {_candidates.Count} accepted part(s) against " +
                          $"'{TargetKey}' (tolerance {action.positionToleranceMeters * 100f:F1} cm / " +
                          $"{action.rotationToleranceDegrees:F0} deg, assist radius {assistRadiusMeters * 100f:F0} cm).");

            return true;
        }

        public void Clear()
        {
            DisengageAssist();
            UnfreezeIfFrozen();

            IsActive = false;
            _completed = false;
            _candidates.Clear();
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
                OnPartGrabbed?.Invoke(grabbedNow.key);
            }

            // --- mating assist follows the active part in and out of the radius ---
            UpdateAssist();

            if (releasedNow != null)
            {
                OnPartReleased?.Invoke(releasedNow.key);

                bool near = releasedNow == _active && IsNearTarget(releasedNow);

                if (near && freezeOnReleaseNearTarget)
                {
                    // Judge where the hand let go. Without this the part would drop or be
                    // shoved during the settle and a correct release could fail.
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

        private void BeginSettle(float seconds, string trigger)
        {
            _awaitingSettle = true;
            _settleTimer = 0f;
            _settleTarget = Mathf.Max(0.02f, seconds);
            _settleTrigger = trigger;
        }

        /// <summary>
        /// Grab state from the Interaction SDK when the part has one; otherwise the
        /// kinematic heuristic (the SDK makes a held body kinematic).
        /// </summary>
        private static bool IsHeld(Candidate c)
        {
            if (c.pointable != null)
                return c.pointable.SelectingPointsCount > 0;

            return c.body != null && c.body.isKinematic && !c.wasKinematic;
        }

        /// <summary>
        /// Prefers a part the operator has moved; otherwise the one nearest the target.
        /// </summary>
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

            // Quaternion.Angle is the true shortest angular distance, so it does not break
            // near 0/360 the way an Euler comparison does.
            rotationError = Quaternion.Angle(c.tf.rotation, CurrentTarget.rotation);

            return positionError <= _action.positionToleranceMeters
                && rotationError <= _action.rotationToleranceDegrees;
        }

        private bool IsNearTarget(Candidate c)
        {
            if (c == null || c.tf == null || CurrentTarget == null) return false;
            float radius = Mathf.Max(assistRadiusMeters, _action != null ? _action.positionToleranceMeters * 1.5f : 0f);
            return Vector3.Distance(c.tf.position, CurrentTarget.position) <= radius;
        }

        // =====================================================================
        // Judgement
        // =====================================================================

        private void Evaluate(string trigger)
        {
            if (_completed || _active == null || _active.tf == null) return;

            bool ok = WithinTolerance(_active, out float posErr, out float rotErr);
            AttemptCount++;

            // Specific enough to act on. "Alignment needed" tells the participant nothing.
            if (ok) LastRejectReason = RejectReason.None;
            else if (posErr > _action.positionToleranceMeters * 3f) LastRejectReason = RejectReason.WrongComponent;
            else if (posErr > _action.positionToleranceMeters) LastRejectReason = RejectReason.TooFar;
            else LastRejectReason = RejectReason.WrongRotation;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Attempt {AttemptCount} ({trigger}) on '{_active.key}': " +
                          $"{(ok ? "ACCEPTED" : "rejected")}  off by {posErr * 100f:F1} cm / {rotErr:F0} deg");

            OnAttemptEvaluated?.Invoke(ok, posErr, rotErr, trigger);

            if (!ok)
            {
                _inToleranceTimer = 0f;

                // Let the part go again, solid, so another attempt can be made. Overlapping
                // geometry is pushed apart by the physics engine, which is the correct
                // "this is not seated" cue.
                UnfreezeIfFrozen();
                DisengageAssist();
                return;
            }

            _completed = true;
            IsActive = false;
            _consumed.Add(_active.key);

            // Collisions stay ignored for this part: it now lives inside the assembly.
            SnapToTarget(_active);

            // Reusable locking, so an installed part cannot be pulled back out later.
            var padlock = _active.tf.GetComponent<PlacementLock>();
            if (padlock == null) padlock = _active.tf.gameObject.AddComponent<PlacementLock>();
            padlock.LockAt(CurrentTarget);

            JoinKitIfComponent(_active);

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
                c.body.isKinematic = true;   // stays put once assembled
            }

            c.tf.SetPositionAndRotation(CurrentTarget.position, CurrentTarget.rotation);
        }

        /// <summary>
        /// A locked kit component rides with the kit's handle from now on, so the finished
        /// assembly moves as one object when the handle is picked up.
        /// </summary>
        private void JoinKitIfComponent(Candidate c)
        {
            if (guidanceRegistry == null || c == null || c.tf == null) return;
            if (!guidanceRegistry.TryResolveKitHandle(c.key, out Transform handle)) return;
            if (handle == null || handle == c.tf || c.tf.IsChildOf(handle)) return;

            c.tf.SetParent(handle, true);

            if (logEvaluations)
                Debug.Log($"[StepValidator] '{c.key}' joined its kit under '{handle.name}'.");
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
