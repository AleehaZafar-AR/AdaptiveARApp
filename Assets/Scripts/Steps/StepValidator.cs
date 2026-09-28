// File: StepValidator.cs
// Checks whether the operator placed the current step's part correctly.
//
// Replaces the old SnapToPosition, which was defective (a fresh coroutine every
// frame, a position threshold of 10.0 that was always true, Euler-angle distance
// that wraps at 0/360, and a UnityEditor import that breaks Android builds).
//
// Both the part and the target pose are resolved through GuidanceRegistry, because
// StepData is a ScriptableObject and cannot hold scene references.
//
// What counts as an attempt
// -------------------------
// The Interaction SDK drives a held part kinematically, so a release shows up as
// isKinematic going true -> false. That transition, after a short settle, is one
// attempt. A part can also be accepted by simply coming to rest in tolerance, which
// covers placements that never involved a grab.

using System;
using AdaptiveAR.Logging;
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

        [Tooltip("Seconds a part must sit in tolerance, untouched, to be accepted without a release.")]
        [SerializeField] private float restAcceptSeconds = 0.8f;

        [Header("Behaviour")]
        [Tooltip("Accept a placement when the part comes to rest in tolerance, even with no release event.")]
        [SerializeField] private bool acceptOnRest = true;

        [Tooltip("Require the part to have been picked up at least once before a resting " +
                 "placement counts. Stops a step auto-completing because the part happened " +
                 "to spawn close to its target.")]
        [SerializeField] private bool requireHandledBeforeRest = true;

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

        private Transform _part;
        private Transform _target;
        private Rigidbody _partBody;
        private StepData _step;

        private bool _wasKinematic;
        private bool _hasBeenHandled;
        private bool _awaitingSettle;
        private float _settleTimer;
        private float _inToleranceTimer;
        private bool _completed;

        // =====================================================================
        // Lifecycle
        // =====================================================================

        /// <summary>
        /// Arms validation for a step. Returns false when the step does not use validation
        /// or its keys cannot be resolved - the caller should then rely on manual advance.
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

            if (string.IsNullOrEmpty(PartKey) || string.IsNullOrEmpty(TargetKey))
            {
                Debug.LogWarning($"[StepValidator] Step '{step.StepIdentifier}' requires validation but its " +
                                 "part/target keys are not set; advance manually.", this);
                return false;
            }

            if (!guidanceRegistry.TryResolve(PartKey, out GameObject partGo) ||
                !guidanceRegistry.TryResolve(TargetKey, out GameObject targetGo))
            {
                Debug.LogWarning($"[StepValidator] Step '{step.StepIdentifier}': could not resolve " +
                                 $"'{PartKey}' and/or '{TargetKey}'; advance manually.", this);
                return false;
            }

            _part = partGo.transform;
            _target = targetGo.transform;
            _partBody = partGo.GetComponent<Rigidbody>();
            _wasKinematic = _partBody != null && _partBody.isKinematic;
            _hasBeenHandled = false;

            IsActive = true;
            _completed = false;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Watching '{PartKey}' against '{TargetKey}' " +
                          $"(tolerance {step.positionToleranceMeters * 100f:F1} cm / " +
                          $"{step.rotationToleranceDegrees:F0} deg).");

            return true;
        }

        /// <summary>Disarms validation and clears all per-step state.</summary>
        public void Clear()
        {
            IsActive = false;
            _completed = false;
            _part = null;
            _target = null;
            _partBody = null;
            _step = null;
            AttemptCount = 0;
            _awaitingSettle = false;
            _hasBeenHandled = false;
            _settleTimer = 0f;
            _inToleranceTimer = 0f;
            PartKey = null;
            TargetKey = null;
        }

        private void Update()
        {
            if (!IsActive || _completed || _part == null || _target == null)
                return;

            bool held = _partBody != null && _partBody.isKinematic;

            // --- release detected: kinematic (held) -> non-kinematic (let go) ---
            if (_partBody != null && _wasKinematic && !held)
            {
                _awaitingSettle = true;
                _settleTimer = 0f;
            }
            _wasKinematic = held;

            if (held)
            {
                // Being handled: no judgement, and the rest timer restarts.
                _hasBeenHandled = true;
                _inToleranceTimer = 0f;
                return;
            }

            // --- judge after a release, once the part has settled ---
            if (_awaitingSettle)
            {
                _settleTimer += Time.deltaTime;
                if (_settleTimer >= settleSeconds)
                {
                    _awaitingSettle = false;
                    Evaluate("release");
                }
                return;
            }

            // --- accept a part that simply comes to rest in tolerance ---
            if (!acceptOnRest)
                return;

            if (requireHandledBeforeRest && !_hasBeenHandled)
                return;

            bool slow = _partBody == null || _partBody.linearVelocity.magnitude <= restSpeed;
            if (slow && WithinTolerance(out _, out _))
            {
                _inToleranceTimer += Time.deltaTime;
                if (_inToleranceTimer >= restAcceptSeconds)
                    Evaluate("rest");
            }
            else
            {
                _inToleranceTimer = 0f;
            }
        }

        // =====================================================================
        // Evaluation
        // =====================================================================

        private bool WithinTolerance(out float positionError, out float rotationError)
        {
            positionError = Vector3.Distance(_part.position, _target.position);

            // Quaternion.Angle is the true shortest angular distance, so it does not
            // break near 0/360 the way an Euler comparison does.
            rotationError = Quaternion.Angle(_part.rotation, _target.rotation);

            return positionError <= _step.positionToleranceMeters
                && rotationError <= _step.rotationToleranceDegrees;
        }

        private void Evaluate(string trigger)
        {
            if (_completed) return;

            bool ok = WithinTolerance(out float posErr, out float rotErr);
            AttemptCount++;

            if (logEvaluations)
                Debug.Log($"[StepValidator] Attempt {AttemptCount} ({trigger}): " +
                          $"{(ok ? "ACCEPTED" : "rejected")}  " +
                          $"off by {posErr * 100f:F1} cm / {rotErr:F0} deg");

            OnAttemptEvaluated?.Invoke(ok, posErr, rotErr, trigger);

            if (!ok)
            {
                _inToleranceTimer = 0f;
                return;
            }

            _completed = true;
            IsActive = false;

            if (_step.snapOnSuccess)
                SnapPartToTarget();

            OnStepValidated?.Invoke();
        }

        private void SnapPartToTarget()
        {
            if (_partBody != null)
            {
                _partBody.linearVelocity = Vector3.zero;
                _partBody.angularVelocity = Vector3.zero;
                _partBody.isKinematic = true; // stays put once assembled
            }

            _part.SetPositionAndRotation(_target.position, _target.rotation);
        }

        /// <summary>Current placement error, for a live on-device readout. Zero when inactive.</summary>
        public void GetCurrentError(out float positionError, out float rotationError)
        {
            positionError = 0f;
            rotationError = 0f;

            if (!IsActive || _part == null || _target == null) return;

            positionError = Vector3.Distance(_part.position, _target.position);
            rotationError = Quaternion.Angle(_part.rotation, _target.rotation);
        }
    }
}
