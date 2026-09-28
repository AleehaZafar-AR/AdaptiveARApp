// File: DecisionTypes.cs
// The data contract between the running MR session and a support-level decision
// provider. Defined NOW, with no provider implementation, so that tomorrow's AI
// layer plugs in without the logger or the session controller being redesigned.
//
// Nothing in this file talks to a model, a network, or Unity scene state. It is
// plain C# so the same types can be replayed headlessly in the offline decision
// evaluation (CLAUDE.md section 3).

using System;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision
{
    /// <summary>Where a support-level change came from. Recorded on every transition.</summary>
    public enum SupportChangeSource
    {
        /// <summary>The step's authored default on entry.</summary>
        StepDefault = 0,

        /// <summary>A researcher or participant pressed a control.</summary>
        Manual = 1,

        /// <summary>The on-device debug bindings (B / Y).</summary>
        DebugInput = 2,

        /// <summary>A decision provider asked for it.</summary>
        DecisionProvider = 3,

        /// <summary>A safe fallback applied after a provider failure or violation.</summary>
        Fallback = 4
    }

    /// <summary>
    /// Whether a provider's output respected the constrained action set.
    /// Constraint compliance is a measured outcome of the evaluation, so every
    /// non-Ok value is recorded rather than silently corrected.
    /// </summary>
    public enum ConstraintStatus
    {
        /// <summary>Output was a valid action and was applied as requested.</summary>
        Ok = 0,

        /// <summary>Output could not be parsed at all.</summary>
        Unparseable = 1,

        /// <summary>Output parsed but was not one of KEEP / INCREASE / DECREASE.</summary>
        OutOfVocabulary = 2,

        /// <summary>Provider raised an error.</summary>
        ProviderError = 3,

        /// <summary>Provider did not answer within its budget.</summary>
        ProviderTimeout = 4,

        /// <summary>Valid action, but it pushed past the end of the scale and was clamped.</summary>
        SaturatedAtBoundary = 5
    }

    /// <summary>
    /// A physiological reading. Every field is nullable: "not measured" must be
    /// representable rather than defaulted to a fabricated number (CLAUDE.md 2.3).
    /// </summary>
    [Serializable]
    public class PhysiologicalSample
    {
        /// <summary>RMSSD in milliseconds, or null when unavailable.</summary>
        public float? RmssdMs;

        /// <summary>Heart rate in beats per minute, or null when unavailable.</summary>
        public float? HeartRateBpm;

        /// <summary>Mean inter-beat interval in milliseconds, or null when unavailable.</summary>
        public float? MeanIbiMs;

        /// <summary>Free-form provider label, e.g. a device model. Null when no source is attached.</summary>
        public string Source;

        /// <summary>Provider-reported signal quality 0-1, or null when not reported.</summary>
        public float? Quality;

        /// <summary>Age of this reading in milliseconds at the moment it was used.</summary>
        public float? AgeMs;

        /// <summary>True when no field carries a measurement.</summary>
        public bool IsEmpty
        {
            get
            {
                return !RmssdMs.HasValue && !HeartRateBpm.HasValue && !MeanIbiMs.HasValue;
            }
        }
    }

    /// <summary>
    /// The exact context handed to a decision provider. This is what gets logged as
    /// "AI input", so it must be complete and self-describing.
    /// </summary>
    [Serializable]
    public class DecisionContext
    {
        // --- identity ---
        public string ParticipantId;
        public string SessionId;
        public int DecisionIndex;

        // --- task state ---
        public int StepIndex;
        public string StepId;
        public int StepCount;
        public int TaskComplexity;
        public string OperatorExperience;

        // --- timing, milliseconds ---
        public float StepElapsedMs;
        public float OverallElapsedMs;

        /// <summary>Researcher-authored expected duration for this step, or null if unset.</summary>
        public float? StepExpectedMs;

        // --- performance ---
        public int AttemptsOnStep;
        public int ErrorsOnStep;
        public int ErrorsTotal;

        // --- support ---
        public SupportLevel CurrentSupportLevel;
        public int SupportChangesOnStep;
        public int SupportChangesTotal;

        // --- physiology (nullable throughout) ---
        public PhysiologicalSample Physiological;

        /// <summary>True when no physiological source supplied a reading for this decision.</summary>
        public bool PhysiologicalMissing
        {
            get { return Physiological == null || Physiological.IsEmpty; }
        }
    }

    /// <summary>
    /// What a provider returned, including everything needed to audit it.
    /// </summary>
    [Serializable]
    public class DecisionResponse
    {
        /// <summary>Exact text the provider produced, before parsing. Null for non-text providers.</summary>
        public string RawOutput;

        /// <summary>The action actually parsed out, after constraint checking.</summary>
        public SupportAction ParsedAction;

        /// <summary>Whether the output respected the constrained action set.</summary>
        public ConstraintStatus Status;

        /// <summary>Wall-clock time from request to response, in milliseconds.</summary>
        public float LatencyMs;

        /// <summary>Provider identifier, e.g. a model id. Recorded so runs stay interpretable.</summary>
        public string ProviderId;

        /// <summary>Optional provider-supplied rationale. Never used to drive behaviour.</summary>
        public string ProviderReason;

        /// <summary>True when the action was replaced by the safe fallback.</summary>
        public bool UsedFallback;

        public static DecisionResponse Failed(ConstraintStatus status, float latencyMs, string providerId, string raw)
        {
            return new DecisionResponse
            {
                RawOutput = raw,
                ParsedAction = SupportAction.KEEP_SUPPORT,
                Status = status,
                LatencyMs = latencyMs,
                ProviderId = providerId,
                UsedFallback = true
            };
        }
    }

    /// <summary>
    /// The seam a future AI provider implements. No implementation ships today.
    /// Implementations must not throw: return a failed DecisionResponse instead, so
    /// the failure is recorded rather than crashing a participant session.
    /// </summary>
    public interface IDecisionProvider
    {
        /// <summary>Stable identifier recorded in the log, e.g. a model id.</summary>
        string ProviderId { get; }

        /// <summary>
        /// Requests one action for the given context. Implementations are expected to
        /// be deterministic for a given context wherever the underlying model allows,
        /// because decision consistency is a measured outcome.
        /// </summary>
        DecisionResponse RequestDecision(DecisionContext context);
    }
}
