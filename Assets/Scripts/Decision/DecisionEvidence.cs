// File: DecisionEvidence.cs
// One deterministic, human-readable line stating which evidence a decision was made on:
// what happened SINCE the last support change on the step (the only evidence that may
// justify a further escalation once the level has changed), and the cumulative step
// counters (which govern until the first change and are historical afterwards). Written
// on every 'decision' line and on every AI-driven 'support_change' line, and printed to
// logcat when a level changes, so a questionable transition can be audited from the log
// without re-deriving it from the context block. Plain C#, no Unity.

using System.Globalization;

namespace AdaptiveAR.Decision
{
    public static class DecisionEvidence
    {
        /// <summary>True once the level has changed on this step: only since-change evidence may escalate further.</summary>
        public static bool SinceChangeGoverns(DecisionContext c)
        {
            return c != null && c.SupportChangesOnStep > 0;
        }

        public static string Summarise(DecisionContext c)
        {
            if (c == null) return "no context";
            var ci = CultureInfo.InvariantCulture;
            bool since = SinceChangeGoverns(c);

            string ratio = c.StepExpectedMs.HasValue && c.StepExpectedMs.Value > 0
                ? (c.StepElapsedMs / c.StepExpectedMs.Value).ToString("F2", ci) : "n/a";

            return "governs=" + (since ? "since_change" : "cumulative")
                 + "; since_change: errors=" + c.ErrorsSinceLastSupportChange.ToString(ci)
                 + " attempts=" + c.AttemptsSinceLastSupportChange.ToString(ci)
                 + " successes=" + c.SuccessfulActionsSinceLastSupportChange.ToString(ci)
                 + " last_error_ms=" + Ms(c.TimeSinceLastErrorMs)
                 + " last_change_ms=" + Ms(c.TimeSinceLastSupportChangeMs)
                 + "; cumulative" + (since ? "(historical after a change)" : "") + ": errors_on_step=" + c.ErrorsOnStep.ToString(ci)
                 + " attempts_on_step=" + c.AttemptsOnStep.ToString(ci)
                 + " step_elapsed_ms=" + c.StepElapsedMs.ToString("F0", ci)
                 + " elapsed_ratio=" + ratio
                 + " changes_on_step=" + c.SupportChangesOnStep.ToString(ci)
                 + " level=" + c.CurrentSupportLevel
                 + "; physiology=" + (c.PhysiologicalMissing ? "missing" : "present") + " (context only, never a trigger)";
        }

        private static string Ms(float? v)
        {
            return v.HasValue ? v.Value.ToString("F0", CultureInfo.InvariantCulture) : "null";
        }
    }
}
