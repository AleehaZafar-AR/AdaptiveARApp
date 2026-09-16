// File: SupportAction.cs
// The constrained action set the future AI decision layer is allowed to emit.
//
// IMPORTANT: this file defines only the action VOCABULARY and the mechanical
// one-step clamp that each action means by definition. It deliberately contains
// no policy about WHEN an action should be taken - that is researcher-defined
// and is not implemented yet.

namespace AdaptiveAR.Support
{
    /// <summary>
    /// The only actions a decision provider may emit. Any other output is a
    /// constraint violation and must be rejected and logged by the caller.
    /// </summary>
    public enum SupportAction
    {
        /// <summary>Remain at the current support level.</summary>
        KEEP_SUPPORT = 0,

        /// <summary>Move one level toward more assistance.</summary>
        INCREASE_SUPPORT = 1,

        /// <summary>Move one level toward less assistance.</summary>
        DECREASE_SUPPORT = 2
    }

    /// <summary>
    /// Mechanical helpers for the support-level scale. Pure functions, no Unity
    /// dependency, so they can run headless in the offline decision evaluation.
    /// </summary>
    public static class SupportLevels
    {
        /// <summary>Lowest level on the scale.</summary>
        public const SupportLevel Min = SupportLevel.L1_Minimal;

        /// <summary>Highest level on the scale.</summary>
        public const SupportLevel Max = SupportLevel.L3_Assisted;

        /// <summary>
        /// Clamps an arbitrary integer onto the valid level range.
        /// </summary>
        public static SupportLevel Clamp(int rawLevel)
        {
            if (rawLevel < (int)Min) return Min;
            if (rawLevel > (int)Max) return Max;
            return (SupportLevel)rawLevel;
        }

        /// <summary>
        /// Applies an action to a level and returns the resulting level.
        /// This is the definition of the action itself (one step, clamped at the
        /// ends of the scale) - not a policy decision. INCREASE at L3 stays L3;
        /// DECREASE at L1 stays L1.
        /// </summary>
        public static SupportLevel Apply(SupportLevel current, SupportAction action)
        {
            switch (action)
            {
                case SupportAction.INCREASE_SUPPORT:
                    return Clamp((int)current + 1);

                case SupportAction.DECREASE_SUPPORT:
                    return Clamp((int)current - 1);

                case SupportAction.KEEP_SUPPORT:
                default:
                    return current;
            }
        }

        /// <summary>
        /// True when the action would have moved past the end of the scale and was
        /// clamped instead. Callers should record this - a decision provider that
        /// repeatedly pushes past a boundary is a finding, not a no-op.
        /// </summary>
        public static bool WouldSaturate(SupportLevel current, SupportAction action)
        {
            if (action == SupportAction.INCREASE_SUPPORT) return current == Max;
            if (action == SupportAction.DECREASE_SUPPORT) return current == Min;
            return false;
        }

        /// <summary>
        /// Parses a provider's raw string output onto the action set.
        /// Returns false for anything outside the allowed vocabulary; the caller
        /// is responsible for logging the violation and applying a safe fallback.
        /// No coercion or fuzzy matching is performed by design.
        /// </summary>
        public static bool TryParseAction(string raw, out SupportAction action)
        {
            action = SupportAction.KEEP_SUPPORT;
            if (string.IsNullOrEmpty(raw)) return false;

            switch (raw.Trim().ToUpperInvariant())
            {
                case "KEEP_SUPPORT":
                    action = SupportAction.KEEP_SUPPORT;
                    return true;

                case "INCREASE_SUPPORT":
                    action = SupportAction.INCREASE_SUPPORT;
                    return true;

                case "DECREASE_SUPPORT":
                    action = SupportAction.DECREASE_SUPPORT;
                    return true;

                default:
                    return false;
            }
        }
    }
}
