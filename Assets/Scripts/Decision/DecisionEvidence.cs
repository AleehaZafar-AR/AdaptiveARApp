// File: DecisionEvidence.cs
// One deterministic, human-readable line stating the evidence a decision was given:
// the cumulative step counters, what happened since the last support change, the
// temporal features and the ordered recent events, and whether physiology was present.
// It describes, it does not judge: no field here classifies the operator or names a
// regime; interpretation belongs to the provider. Written on every 'decision' line and on
// every AI-driven 'support_change' line, and printed to logcat when a level changes, so a
// transition can be audited from the log without re-deriving it from the context block.
// DecisionConstraints lists the STRUCTURAL actions available at a level (boundaries
// only), which is logged beside every decision as allowed_actions. Plain C#, no Unity.

using System.Collections.Generic;
using System.Globalization;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision
{
    public static class DecisionEvidence
    {
        public static string Summarise(DecisionContext c)
        {
            if (c == null) return "no context";
            var ci = CultureInfo.InvariantCulture;
            string ratio = c.StepExpectedMs.HasValue && c.StepExpectedMs.Value > 0
                ? (c.StepElapsedMs / c.StepExpectedMs.Value).ToString("F2", ci) : "n/a";

            return "cumulative: errors_on_step=" + c.ErrorsOnStep.ToString(ci)
                 + " attempts_on_step=" + c.AttemptsOnStep.ToString(ci)
                 + " step_elapsed_ms=" + c.StepElapsedMs.ToString("F0", ci)
                 + " elapsed_ratio=" + ratio
                 + " level=" + c.CurrentSupportLevel
                 + " changes_on_step=" + c.SupportChangesOnStep.ToString(ci)
                 + "; since_change: errors=" + c.ErrorsSinceLastSupportChange.ToString(ci)
                 + " attempts=" + c.AttemptsSinceLastSupportChange.ToString(ci)
                 + " successes=" + c.SuccessfulActionsSinceLastSupportChange.ToString(ci)
                 + " last_change_ms=" + Ms(c.TimeSinceLastSupportChangeMs)
                 + "; recent(" + c.RecentWindowMs.ToString("F0", ci) + "ms): errors=" + c.RecentErrorCount.ToString(ci)
                 + " failed_attempts=" + c.RecentFailedAttemptCount.ToString(ci)
                 + " wrong=" + c.RecentWrongComponentCount.ToString(ci)
                 + " consecutive_successes=" + c.ConsecutiveSuccessfulActions.ToString(ci)
                 + " errors_since_success=" + c.ErrorsSinceLastSuccess.ToString(ci)
                 + " last_error_ms=" + Ms(c.TimeSinceLastErrorMs)
                 + " last_success_ms=" + Ms(c.TimeSinceLastSuccessMs)
                 + "; events: " + TaskEventLedger.Describe(c.RecentEvents)
                 + "; physiology=" + (c.PhysiologicalMissing ? "missing" : "present") + " (context only, never a trigger)";
        }

        private static string Ms(float? v)
        {
            return v.HasValue ? v.Value.ToString("F0", CultureInfo.InvariantCulture) : "null";
        }
    }

    /// <summary>
    /// The structural constraints on the final action, and nothing else: the valid action
    /// set and the level boundaries. No task-state or physiological condition ever removes
    /// an action here (CLAUDE.md 2.1: the application constrains, the provider decides).
    /// </summary>
    public static class DecisionConstraints
    {
        public static List<string> AllowedActions(SupportLevel level)
        {
            var list = new List<string> { SupportAction.KEEP_SUPPORT.ToString() };
            if (!SupportLevels.WouldSaturate(level, SupportAction.INCREASE_SUPPORT)) list.Add(SupportAction.INCREASE_SUPPORT.ToString());
            if (!SupportLevels.WouldSaturate(level, SupportAction.DECREASE_SUPPORT)) list.Add(SupportAction.DECREASE_SUPPORT.ToString());
            return list;
        }
    }
}
