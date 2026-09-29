// File: StepValidator.cs
// Checks whether the operator placed the current step's part correctly.
//
// Replaces the old SnapToPosition, which was defective (a fresh coroutine every
// frame, a position threshold of 10.0 that was always true, Euler-angle distance
// that wraps at 0/360, and a UnityEditor import that breaks Android builds).
//
// Interchangeable parts
// ---------------------
// A step can accept several parts. The four pistons are identical, so picking up
// any unplaced one is correct - insisting on "piston 2" when the operator reached
// for an identical piston would record an error that is not one. Whichever
// accepted part the operator actually moves becomes the one being judged, and a
// part that has been placed is not offered again on a later step.
//
// What counts as an attempt
// -------------------------
// For a part WITH a Rigidbody the Interaction SDK holds it kinematically, so a
// release shows up as isKinematic going true -> false, judged after a short settle.
//
// Several parts have no Rigidbody at all: the pistons are Grabbable transforms
// whose meshes sit on child objects. For those there is no kinematic flag, so
// handling is detected from movement and speed is estimated from the transform.

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

        [Tooltip("Metres a part must move from where it started to count as handled. Needed " +
                 "because parts without a Rigidbody have no kinematic flag to watch.")]
        [SerializeField] private float handledMoveThreshold = 0.05f;

        [Header("Debug")]
        [SerializeField] private bool logEvaluations = true;

        /// <summary>Fired for every judged placement. (success, positionError, rotationError, trigger)</summary>
        public event Action<bool, float, float, string> OnAttemptEvaluated;

        /// <summary>Fired once when the current step's placement is accepted.</summary>
        public event Action OnStepValidated;

        public bool IsActive { get; private set; }
        public int AttemptCount { get; private set; }
        public string PartKey { get; private set; }
        public string TargetKey { get; private set; }

        /// <summary>The part currently being judged. Null when nothing is armed.</summary>
        public Transform CurrentPart { get; private set; }

        /// <summary>The pose the part must reach.</summary>
        public Transform CurrentTarget { get; private set; }

        /// <summary>True once the operator has actually picked the part up.</summary>
        public bool HasBeenHandled { get; private set; }

        private class Candidate
        {
            public string key;
            public Transform tf;
            public Rigidbody body;
            public Vector3 startPos;
            public Vector3 prevPos;
            public bool wasKinematic;
            public bool moved;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>();

        // Parts already assembled. Survives across steps so an identical part is not
        // offered twice; cleared when the session restarts.
        private readonly HashSet<string> _consumed = new HashSet<string>();

        private Candidate _active;
        private StepData _step;
        private bool _awaitingSettle;
        private float _settleTimer;
        private float _inToleranceTimer;
        private bool _completed;

        // =====================================================================

        /// <summary>Forgets which parts have been assembled. Call when a session restarts.</summary>
        public void ResetConsumedParts()
        {
            _consumed.Clear();
        }

        /// <summary>
        /// Arms validation for a step. Returns false when the step does not use validation
        /// or nothing could be resolved - the caller then relies on manual advance.
        /// </summary>
        public bool BeginStep(StepData step)
        {
            Clear();
            _step = step;

            if (step == null || !step.requiresValidation)
                return false;

            PartKey = step.validationPartKey;
            TargetKey = step.validationTargetKey;

            if (guidanceRegistry == null)
            {
                Debug.LogWarning("[StepValidator] No GuidanceRegistry assigned; validation disabled.", this);
                return false;
            }

            if (string.IsNullOrEmpty(TargetKey) ||
                !guidanceRegistry.TryResolve(TargetKey, out GameObject targetGo))
            {
                Debug.LogWarning($"[StepValidator] Step '{step.StepIdentifier}': target '{TargetKey}' " +
                                 "could not be resolved; advance manually.", this);
                return false;
            }

            CurrentTarget = targetGo.transform;

            foreach (string key in step.AllAcceptedPartKeys())
            {
                if (_consumed.Contains(key)) continue;                     // already assembled
                if (!guidanceRegistry.TryResolveQuiet(key, out GameObject go)) continue;

                _candidates.Add(new Candidate
                {
                    key = key,
                    tf = go.transform,
                    body = go.GetComponent<Rigidbody>(),
                    startPos = go.transform.position,
                    prevPos = go.transform.position,
                    wasKinematic = go.GetComponent<Rigidbody>() != null && go.GetComponent<Rigidbody>().isKinematic
                });
            }

            if (_candidates.Count == 0)
            {
                Debug.LogWarning($"[StepValidator] Step '{step.StepIdentifier}': no accepted part could be " +
                                 "resolved; advance manually.", this);
                return false;
            }

            _active = _candidates[0];
            CurrentPart = _active.tf;
            IsActive = true;
            _completed = false;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Watching {_candidates.Count} accepted part(s) against " +
                          $"'{TargetKey}' (tolerance {step.positionToleranceMeters * 100f:F1} cm / " +
                          $"{step.rotationToleranceDegrees:F0} deg).");

            return true;
        }

        public void Clear()
        {
            IsActive = false;
            _completed = false;
            _candidates.Clear();
            _active = null;
            _step = null;
            CurrentPart = null;
            CurrentTarget = null;
            HasBeenHandled = false;
            AttemptCount = 0;
            _awaitingSettle = false;
            _settleTimer = 0f;
            _inToleranceTimer = 0f;
            PartKey = null;
            TargetKey = null;
        }

        private void Update()
        {
            if (!IsActive || _completed || CurrentTarget == null) return;

            float dt = Mathf.Max(Time.deltaTime, 1e-5f);
            bool anyHeld = false;
            bool releasedThisFrame = false;

            foreach (Candidate c in _candidates)
            {
                if (c.tf == null) continue;

                bool held = c.body != null && c.body.isKinematic;
                if (c.body != null && c.wasKinematic && !held) releasedThisFrame = true;
                c.wasKinematic = held;
                if (held) anyHeld = true;

                if (Vector3.Distance(c.tf.position, c.startPos) > handledMoveThreshold)
                    c.moved = true;

                c.prevPos = c.tf.position;
            }

            // Whichever accepted part the operator is actually working with is the one judged.
            Candidate chosen = PickActive();
            if (chosen != null && chosen != _active)
            {
                _active = chosen;
                CurrentPart = chosen.tf;
                _inToleranceTimer = 0f;
            }

            HasBeenHandled = _active != null && _active.moved;

            if (releasedThisFrame)
            {
                _awaitingSettle = true;
                _settleTimer = 0f;
            }

            if (anyHeld)
            {
                _inToleranceTimer = 0f;
                return;
            }

            if (_awaitingSettle)
            {
                _settleTimer += dt;
                if (_settleTimer >= settleSeconds)
                {
                    _awaitingSettle = false;
                    Evaluate("release");
                }
                return;
            }

            if (!acceptOnRest) return;
            if (requireHandledBeforeRest && !HasBeenHandled) return;
            if (_active == null || _active.tf == null) return;

            float speed = _active.body != null
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

            return positionError <= _step.positionToleranceMeters
                && rotationError <= _step.rotationToleranceDegrees;
        }

        private void Evaluate(string trigger)
        {
            if (_completed || _active == null || _active.tf == null) return;

            bool ok = WithinTolerance(_active, out float posErr, out float rotErr);
            AttemptCount++;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Attempt {AttemptCount} ({trigger}) on '{_active.key}': " +
                          $"{(ok ? "ACCEPTED" : "rejected")}  off by {posErr * 100f:F1} cm / {rotErr:F0} deg");

            OnAttemptEvaluated?.Invoke(ok, posErr, rotErr, trigger);

            if (!ok)
            {
                _inToleranceTimer = 0f;
                return;
            }

            _completed = true;
            IsActive = false;
            _consumed.Add(_active.key);

            if (_step.snapOnSuccess)
                SnapToTarget(_active);

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
