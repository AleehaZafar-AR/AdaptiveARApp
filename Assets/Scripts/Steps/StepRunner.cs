// File: StepRunner.cs
// Single owner of the current assembly step index.
//
// This component is the ONLY writer of the step index, and SupportLevelController
// is the only writer of the support level. Neither holds a reference to the
// other's state, so a support-level change cannot advance the assembly step.
//
// Phase 1 ships a single step. Advance()/GoToStep() exist as the progression seam
// but the full assembly sequence is not authored yet.

using System;
using System.Collections.Generic;
using AdaptiveAR.Support;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class StepRunner : MonoBehaviour
    {
        [Header("Sequence")]
        [Tooltip("Ordered assembly steps. Phase 1 uses a single entry (the crankshaft step).")]
        [SerializeField] private List<StepData> steps = new List<StepData>();

        [Header("References")]
        [Tooltip("Support level is set to each step's defaultSupportLevel on entry.")]
        [SerializeField] private SupportLevelController supportLevel;

        [Header("Debug")]
        [SerializeField] private bool logChanges = true;

        /// <summary>Index of the active step, or -1 before the sequence begins.</summary>
        public int CurrentStepIndex { get; private set; } = -1;

        /// <summary>True once BeginSequence has run.</summary>
        public bool HasStarted { get { return CurrentStepIndex >= 0; } }

        /// <summary>Number of authored steps.</summary>
        public int StepCount { get { return steps != null ? steps.Count : 0; } }

        /// <summary>The active step, or null before the sequence begins.</summary>
        public StepData CurrentStep
        {
            get
            {
                if (steps == null || CurrentStepIndex < 0 || CurrentStepIndex >= steps.Count)
                    return null;
                return steps[CurrentStepIndex];
            }
        }

        /// <summary>
        /// Raised after the active step changes. Arguments: step, index, reason.
        /// Carries a reason string so the future session logger can subscribe here
        /// without further refactoring.
        /// </summary>
        public event Action<StepData, int, string> OnStepChanged;

        /// <summary>
        /// Raised when Advance is called on the final step. The runner stays on that step;
        /// the session controller decides what finishing means.
        /// </summary>
        public event Action OnSequenceComplete;

        /// <summary>True when the active step is the last one in the sequence.</summary>
        public bool IsOnLastStep
        {
            get { return HasStarted && CurrentStepIndex == StepCount - 1; }
        }

        /// <summary>Read-only access to any authored step, for progress displays.</summary>
        public StepData GetStepAt(int index)
        {
            if (steps == null || index < 0 || index >= steps.Count)
                return null;
            return steps[index];
        }

        /// <summary>
        /// Enters the first step. Called by StepManager once the ArUco anchor is
        /// locked and the engine model is visible.
        /// </summary>
        public bool BeginSequence()
        {
            if (StepCount == 0)
            {
                Debug.LogWarning("[StepRunner] BeginSequence called but no steps are assigned.", this);
                return false;
            }

            return GoToStep(0, "sequence_start");
        }

        /// <summary>
        /// Moves to the next step. Returns false at the end of the sequence.
        /// Not driven by anything in Phase 1 - task validation is a later phase.
        /// </summary>
        public bool Advance(string reason)
        {
            if (!HasStarted)
                return BeginSequence();

            if (CurrentStepIndex + 1 >= StepCount)
            {
                if (logChanges)
                    Debug.Log("[StepRunner] Advance called on the final step - sequence complete.");

                OnSequenceComplete?.Invoke();
                return false;
            }

            return GoToStep(CurrentStepIndex + 1, reason);
        }

        /// <summary>
        /// Jumps to a specific step. Applies that step's default support level on entry.
        /// </summary>
        public bool GoToStep(int index, string reason)
        {
            if (steps == null || index < 0 || index >= steps.Count)
            {
                Debug.LogWarning($"[StepRunner] GoToStep({index}) is out of range (count {StepCount}).", this);
                return false;
            }

            StepData step = steps[index];
            if (step == null)
            {
                Debug.LogWarning($"[StepRunner] Step at index {index} is null.", this);
                return false;
            }

            CurrentStepIndex = index;

            // Apply the step's authored default level BEFORE announcing the step,
            // so presenters render the step and its level in a single pass.
            if (supportLevel != null)
                supportLevel.SetSupportLevel(step.defaultSupportLevel, "step_default");

            if (logChanges)
                Debug.Log($"[StepRunner] Step {index} '{step.StepIdentifier}' entered (reason: {reason})");

            OnStepChanged?.Invoke(step, index, reason);
            return true;
        }
    }
}
