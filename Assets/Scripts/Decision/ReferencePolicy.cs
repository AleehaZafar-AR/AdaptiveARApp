// File: ReferencePolicy.cs
// The deterministic reference policy: a configurable, researcher-authored rule set that
// maps a DecisionContext to one constrained action. It is (a) the offline reference the
// evaluation measures agreement against, and (b) a drop-in provider for sessions run
// without a model. Every number comes from the JSON config; the defaults below are
// PLACEHOLDERS to be replaced from the literature review (CLAUDE.md section 2.2), and the
// config name is recorded in every decision so runs stay interpretable.
//
// Physiology is deliberately NOT a rule input here: CLAUDE.md section 2.4 forbids any
// "HRV value X => support level Y" mapping. It travels in the context for the model and
// for the log only.

using System;
using System.Collections.Generic;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision
{
    public sealed class ReferencePolicyConfig
    {
        public string Name = "placeholder_v1";
        /// <summary>step_elapsed / step_expected at or above which the step is "slow" (INCREASE). Placeholder.</summary>
        public double SlowRatioIncrease = 1.5;
        /// <summary>Counted errors on the step at or above which support increases. Placeholder.</summary>
        public int ErrorsOnStepIncrease = 2;
        /// <summary>Placement attempts on the step at or above which support increases. Placeholder.</summary>
        public int AttemptsOnStepIncrease = 4;
        /// <summary>step_elapsed / step_expected at or below which a clean step is "fast" (DECREASE). Placeholder.</summary>
        public double FastRatioDecrease = 0.6;
        /// <summary>No timing-based INCREASE before this much time on the step, ms. Placeholder.</summary>
        public double MinStepElapsedMsForIncrease = 10000;
        /// <summary>No DECREASE before this much time on the step, ms. Placeholder.</summary>
        public double MinStepElapsedMsForDecrease = 20000;
        /// <summary>Emit KEEP instead of an action that would push past L1/L3.</summary>
        public bool ClampAtBoundaries = true;
        /// <summary>When a step has no authored expected duration, use this one for the timing rules (0 = skip timing rules).</summary>
        public double FallbackExpectedMs = 0;
        /// <summary>Successful actions since the last level change at or above which, with no recent error, support decreases one level. Placeholder.</summary>
        public int RecoverySuccessesForDecrease = 2;
        /// <summary>No recovery DECREASE until this long after the last error, ms (a step with no error yet passes). Placeholder.</summary>
        public double RecoveryQuietMs = 20000;
        /// <summary>Oscillation guard: KEEP for this long after any level change on the step, ms. Placeholder.</summary>
        public double MinMsBetweenChanges = 20000;
        /// <summary>After a level change on the step: counted errors SINCE that change at or above which support increases again. Placeholder.</summary>
        public int ErrorsSinceChangeIncrease = 2;
        /// <summary>After a level change on the step: placement attempts since that change, with no success since it, at or above which support increases again. Placeholder.</summary>
        public int AttemptsSinceChangeIncrease = 4;

        public static ReferencePolicyConfig FromJson(Dictionary<string, object> o)
        {
            var c = new ReferencePolicyConfig();
            if (o == null) return c;
            c.Name = MiniJson.GetString(o, "name", c.Name);
            c.SlowRatioIncrease = MiniJson.GetDouble(o, "slowRatioIncrease", c.SlowRatioIncrease);
            c.ErrorsOnStepIncrease = MiniJson.GetInt(o, "errorsOnStepIncrease", c.ErrorsOnStepIncrease);
            c.AttemptsOnStepIncrease = MiniJson.GetInt(o, "attemptsOnStepIncrease", c.AttemptsOnStepIncrease);
            c.FastRatioDecrease = MiniJson.GetDouble(o, "fastRatioDecrease", c.FastRatioDecrease);
            c.MinStepElapsedMsForIncrease = MiniJson.GetDouble(o, "minStepElapsedMsForIncrease", c.MinStepElapsedMsForIncrease);
            c.MinStepElapsedMsForDecrease = MiniJson.GetDouble(o, "minStepElapsedMsForDecrease", c.MinStepElapsedMsForDecrease);
            c.ClampAtBoundaries = MiniJson.GetBool(o, "clampAtBoundaries", c.ClampAtBoundaries);
            c.FallbackExpectedMs = MiniJson.GetDouble(o, "fallbackExpectedMs", c.FallbackExpectedMs);
            c.RecoverySuccessesForDecrease = MiniJson.GetInt(o, "recoverySuccessesForDecrease", c.RecoverySuccessesForDecrease);
            c.RecoveryQuietMs = MiniJson.GetDouble(o, "recoveryQuietMs", c.RecoveryQuietMs);
            c.MinMsBetweenChanges = MiniJson.GetDouble(o, "minMsBetweenChanges", c.MinMsBetweenChanges);
            c.ErrorsSinceChangeIncrease = MiniJson.GetInt(o, "errorsSinceChangeIncrease", c.ErrorsSinceChangeIncrease);
            c.AttemptsSinceChangeIncrease = MiniJson.GetInt(o, "attemptsSinceChangeIncrease", c.AttemptsSinceChangeIncrease);
            return c;
        }

        public string Describe()
        {
            return $"name={Name};slowRatioIncrease={SlowRatioIncrease};errorsOnStepIncrease={ErrorsOnStepIncrease};attemptsOnStepIncrease={AttemptsOnStepIncrease};" +
                   $"fastRatioDecrease={FastRatioDecrease};minElapsedIncreaseMs={MinStepElapsedMsForIncrease};minElapsedDecreaseMs={MinStepElapsedMsForDecrease};" +
                   $"clampAtBoundaries={ClampAtBoundaries};fallbackExpectedMs={FallbackExpectedMs};" +
                   $"recoverySuccessesForDecrease={RecoverySuccessesForDecrease};recoveryQuietMs={RecoveryQuietMs};minMsBetweenChanges={MinMsBetweenChanges};" +
                   $"errorsSinceChangeIncrease={ErrorsSinceChangeIncrease};attemptsSinceChangeIncrease={AttemptsSinceChangeIncrease};physiologyRule=none";
        }
    }

    public sealed class ReferencePolicyProvider : IDecisionProvider
    {
        private readonly ReferencePolicyConfig _cfg;
        public ReferencePolicyProvider(ReferencePolicyConfig cfg) { _cfg = cfg ?? new ReferencePolicyConfig(); }
        public ReferencePolicyConfig Config { get { return _cfg; } }
        public string ProviderId { get { return "reference:" + _cfg.Name; } }

        /// <summary>Pure function of the context: same context, same action, always.</summary>
        public DecisionResponse RequestDecision(DecisionContext ctx)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (ctx == null) return DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, ProviderId, null);

            SupportAction action = SupportAction.KEEP_SUPPORT;
            var why = new List<string>();

            double? expected = ctx.StepExpectedMs.HasValue && ctx.StepExpectedMs.Value > 0 ? (double?)ctx.StepExpectedMs.Value
                             : (_cfg.FallbackExpectedMs > 0 ? (double?)_cfg.FallbackExpectedMs : null);
            double? ratio = expected.HasValue ? (double?)(ctx.StepElapsedMs / expected.Value) : null;

            // Causal interpretation (shared with the LLM contract): until the level has changed on
            // this step the cumulative counters govern and may justify the FIRST escalation. After
            // a change they are historical - the change already answered them - and only NEW
            // difficulty since the change (errors / repeated unsuccessful attempts since it) can
            // escalate again; sustained success since the change can de-escalate. Bidirectional,
            // one level per decision, cooldown against oscillation.
            bool recentGoverns = DecisionEvidence.SinceChangeGoverns(ctx);
            bool quietSinceError = !ctx.TimeSinceLastErrorMs.HasValue || ctx.TimeSinceLastErrorMs.Value >= _cfg.RecoveryQuietMs;
            bool recovered = ctx.SuccessfulActionsSinceLastSupportChange >= _cfg.RecoverySuccessesForDecrease
                             && ctx.ErrorsSinceLastSupportChange == 0 && quietSinceError;
            bool newFailedAttempts = ctx.AttemptsSinceLastSupportChange >= _cfg.AttemptsSinceChangeIncrease
                                     && ctx.SuccessfulActionsSinceLastSupportChange == 0;

            if (ctx.TimeSinceLastSupportChangeMs.HasValue && ctx.TimeSinceLastSupportChangeMs.Value < _cfg.MinMsBetweenChanges)
            { why.Add($"cooldown time_since_last_support_change={ctx.TimeSinceLastSupportChangeMs.Value:F0}<{_cfg.MinMsBetweenChanges}"); }
            else if (!recentGoverns && ctx.ErrorsOnStep >= _cfg.ErrorsOnStepIncrease)
            { action = SupportAction.INCREASE_SUPPORT; why.Add($"cumulative errors_on_step={ctx.ErrorsOnStep}>={_cfg.ErrorsOnStepIncrease} (no change on step yet)"); }
            else if (!recentGoverns && ctx.AttemptsOnStep >= _cfg.AttemptsOnStepIncrease)
            { action = SupportAction.INCREASE_SUPPORT; why.Add($"cumulative attempts_on_step={ctx.AttemptsOnStep}>={_cfg.AttemptsOnStepIncrease} (no change on step yet)"); }
            else if (!recentGoverns && ratio.HasValue && ctx.StepElapsedMs >= _cfg.MinStepElapsedMsForIncrease && ratio.Value >= _cfg.SlowRatioIncrease)
            { action = SupportAction.INCREASE_SUPPORT; why.Add($"cumulative elapsed_ratio={ratio.Value:F2}>={_cfg.SlowRatioIncrease} (no change on step yet)"); }
            else if (recentGoverns && ctx.ErrorsSinceLastSupportChange >= _cfg.ErrorsSinceChangeIncrease)
            { action = SupportAction.INCREASE_SUPPORT; why.Add($"new difficulty errors_since_last_support_change={ctx.ErrorsSinceLastSupportChange}>={_cfg.ErrorsSinceChangeIncrease}"); }
            else if (recentGoverns && newFailedAttempts)
            { action = SupportAction.INCREASE_SUPPORT; why.Add($"new difficulty attempts_since_last_support_change={ctx.AttemptsSinceLastSupportChange}>={_cfg.AttemptsSinceChangeIncrease} with no success since the change"); }
            else if (recovered)
            { action = SupportAction.DECREASE_SUPPORT; why.Add($"recovered successes_since_change={ctx.SuccessfulActionsSinceLastSupportChange}>={_cfg.RecoverySuccessesForDecrease},errors_since_change=0,quiet"); }
            else if (ratio.HasValue && ctx.StepElapsedMs >= _cfg.MinStepElapsedMsForDecrease && ratio.Value <= _cfg.FastRatioDecrease && ctx.ErrorsOnStep == 0 && ctx.AttemptsOnStep <= 1)
            { action = SupportAction.DECREASE_SUPPORT; why.Add($"elapsed_ratio={ratio.Value:F2}<={_cfg.FastRatioDecrease},clean"); }
            else if (recentGoverns)
            { why.Add($"no new difficulty since the change (errors_since={ctx.ErrorsSinceLastSupportChange},attempts_since={ctx.AttemptsSinceLastSupportChange},successes_since={ctx.SuccessfulActionsSinceLastSupportChange}); " +
                      $"historical only: errors_on_step={ctx.ErrorsOnStep},attempts_on_step={ctx.AttemptsOnStep},elapsed_ratio={(ratio.HasValue ? ratio.Value.ToString("F2") : "n/a")} not eligible after a change"); }
            else why.Add(ratio.HasValue ? $"within_band ratio={ratio.Value:F2}" : "no_expected_duration");

            if (_cfg.ClampAtBoundaries && SupportLevels.WouldSaturate(ctx.CurrentSupportLevel, action))
            { why.Add($"boundary:{ctx.CurrentSupportLevel}->keep"); action = SupportAction.KEEP_SUPPORT; }

            sw.Stop();
            return new DecisionResponse
            {
                RawOutput = action.ToString(),
                ParsedAction = action,
                Status = ConstraintStatus.Ok,
                LatencyMs = (float)sw.Elapsed.TotalMilliseconds,
                ProviderId = ProviderId,
                ProviderReason = string.Join(";", why),
                UsedFallback = false
            };
        }
    }
}
