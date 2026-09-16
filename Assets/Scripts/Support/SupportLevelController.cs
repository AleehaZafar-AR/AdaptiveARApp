// File: SupportLevelController.cs
// Single owner of the current instructional-support level.
//
// This is the seam the future AI decision provider plugs into: it calls
// SetSupportLevel() or ApplyAction() and nothing else.
//
// This component holds NO reference to StepRunner and exposes no method that can
// change the assembly step. Changing L1 -> L2 -> L3 therefore cannot advance the
// assembly sequence - the separation is structural, not conventional.

using System;
using UnityEngine;

namespace AdaptiveAR.Support
{
    public class SupportLevelController : MonoBehaviour
    {
        [Header("Initial State")]
        [Tooltip("Level used before a step supplies its own default.")]
        [SerializeField] private SupportLevel initialLevel = SupportLevel.L1_Minimal;

        [Header("Debug")]
        [Tooltip("Log every level change to the Unity console (visible over logcat on device).")]
        [SerializeField] private bool logChanges = true;

        /// <summary>The level currently being presented.</summary>
        public SupportLevel CurrentLevel { get; private set; }

        /// <summary>
        /// Raised after the level changes. Arguments: previous, current, reason.
        /// The reason string is carried so the future session logger can subscribe
        /// here without any further refactoring ("step_default", "debug_input",
        /// and later "ai_decision").
        /// </summary>
        public event Action<SupportLevel, SupportLevel, string> OnSupportLevelChanged;

        private void Awake()
        {
            CurrentLevel = initialLevel;
        }

        /// <summary>
        /// Sets the support level directly. Returns true if the level actually changed.
        /// Calling this with the current level is a no-op and raises no event.
        /// </summary>
        public bool SetSupportLevel(SupportLevel newLevel, string reason)
        {
            if (newLevel == CurrentLevel)
                return false;

            SupportLevel previous = CurrentLevel;
            CurrentLevel = newLevel;

            if (logChanges)
                Debug.Log($"[SupportLevel] {previous} -> {CurrentLevel} (reason: {reason})");

            OnSupportLevelChanged?.Invoke(previous, CurrentLevel, reason);
            return true;
        }

        /// <summary>
        /// Applies one of the constrained actions. Returns true if the level changed.
        /// KEEP_SUPPORT, and actions that saturate at the ends of the scale, return false.
        /// </summary>
        public bool ApplyAction(SupportAction action, string reason)
        {
            SupportLevel target = SupportLevels.Apply(CurrentLevel, action);

            if (SupportLevels.WouldSaturate(CurrentLevel, action) && logChanges)
                Debug.Log($"[SupportLevel] {action} saturated at {CurrentLevel} (reason: {reason})");

            return SetSupportLevel(target, reason);
        }

        // ---------------- Convenience entry points ----------------
        // Parameterless overloads so these can be driven from a UI Button's
        // onClick in the Inspector without writing another adapter script.

        public void SetMinimalSupport() { SetSupportLevel(SupportLevel.L1_Minimal, "manual"); }
        public void SetGuidedSupport() { SetSupportLevel(SupportLevel.L2_Guided, "manual"); }
        public void SetAssistedSupport() { SetSupportLevel(SupportLevel.L3_Assisted, "manual"); }

        public void IncreaseSupport() { ApplyAction(SupportAction.INCREASE_SUPPORT, "manual"); }
        public void DecreaseSupport() { ApplyAction(SupportAction.DECREASE_SUPPORT, "manual"); }
    }
}
