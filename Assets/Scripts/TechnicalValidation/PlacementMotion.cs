// File: PlacementMotion.cs
// Presentation-only visual staging of a scripted successful placement (Level 2, mode =
// presentation). Plain C# (no Unity dependency) so the headless tests cover the gating, the
// easing, the lifecycle and the once-only hand-over to the production success path.
//
// WHAT IT IS. In presentation mode a scripted Success does not teleport the part: it is
// highlighted briefly, moved and rotated with an ease-in-out curve to the pose the production
// snap will give it, and THEN the same production success path that validation mode calls
// immediately is invoked (StepValidator.TechnicalValidationSucceed -> Succeed()). The logical
// event is recorded at that injection, exactly as in validation mode.
//
// WHAT IT IS NOT. It never activates in validation mode (formal results are unaffected by
// animation timing: EffectivePlacementMotionSeconds is 0 there whatever the file says) and
// never in a participant session (no technical_validation.json => nothing in this namespace
// exists). It touches no support level, decision, scheduler, gate, timing or physiology
// state: the only side effect is the part's transform during the motion, and the production
// snap then owns the pose.

using System;

namespace AdaptiveAR.TechnicalValidation
{
    public static class PlacementMotion
    {
        /// <summary>Presentation default: within the requested 0.6-1.2 s band.</summary>
        public const double DefaultDurationSeconds = 0.9;
        /// <summary>Presentation default for the brief pre-motion highlight (scale pulse).</summary>
        public const double DefaultHighlightSeconds = 0.35;
        public const double MinDurationSeconds = 0.2;
        public const double MaxDurationSeconds = 3.0;
        public const double MaxHighlightSeconds = 2.0;

        /// <summary>
        /// Animated placement is selected only when technical validation is enabled, the mode is
        /// presentation and the effective motion duration is positive. Validation mode and
        /// participant sessions never select it.
        /// </summary>
        public static bool Enabled(TechnicalValidationConfig cfg)
        {
            return cfg != null && cfg.Enabled && cfg.Mode == ValidationMode.Presentation && cfg.EffectivePlacementMotionSeconds > 0;
        }

        /// <summary>Smoothstep ease-in-out on [0,1]; clamped outside. Ease(0) = 0, Ease(0.5) = 0.5, Ease(1) = 1, monotonic.</summary>
        public static double EaseInOut(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>Component-wise linear interpolation (the position curve; rotation uses a slerp with the same fraction on the Unity side). t = 1 returns exactly b.</summary>
        public static double[] Lerp(double[] a, double[] b, double t)
        {
            if (a == null || b == null || a.Length != b.Length) throw new ArgumentException("vectors of equal length required");
            var r = new double[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = t >= 1 ? b[i] : (t <= 0 ? a[i] : a[i] + (b[i] - a[i]) * t);
            return r;
        }
    }

    /// <summary>One staged placement: highlight, eased motion, then a once-only hand-over to the production success path.</summary>
    public sealed class PlacementMotionRun
    {
        public enum Phase { Pending, Highlight, Motion, Done }

        public readonly double DurationSeconds;
        public readonly double HighlightSeconds;
        public Phase Current { get; private set; }
        public double StartedAt { get; private set; }
        public double CompletedAt { get; private set; }
        /// <summary>0 -> 1 -> 0 over the highlight phase (a scale pulse on the Unity side); 0 otherwise.</summary>
        public double HighlightPulse { get; private set; }
        /// <summary>Eased motion fraction, 0 during the highlight, exactly 1 once the motion finished.</summary>
        public double Fraction { get; private set; }
        public int CompletionInvocations { get; private set; }

        public double TotalSeconds { get { return HighlightSeconds + DurationSeconds; } }
        public bool MotionFinished { get { return Current == Phase.Done; } }

        public PlacementMotionRun(double durationSeconds, double highlightSeconds)
        {
            if (double.IsNaN(durationSeconds) || durationSeconds < PlacementMotion.MinDurationSeconds) durationSeconds = PlacementMotion.MinDurationSeconds;
            if (durationSeconds > PlacementMotion.MaxDurationSeconds) durationSeconds = PlacementMotion.MaxDurationSeconds;
            if (double.IsNaN(highlightSeconds) || highlightSeconds < 0) highlightSeconds = 0;
            if (highlightSeconds > PlacementMotion.MaxHighlightSeconds) highlightSeconds = PlacementMotion.MaxHighlightSeconds;
            DurationSeconds = durationSeconds;
            HighlightSeconds = highlightSeconds;
            Current = Phase.Pending;
            StartedAt = -1; CompletedAt = -1;
        }

        public void Begin(double nowSeconds)
        {
            StartedAt = nowSeconds;
            Current = HighlightSeconds > 0 ? Phase.Highlight : Phase.Motion;
            HighlightPulse = 0; Fraction = 0;
        }

        /// <summary>Advances to the given clock time and returns the eased motion fraction (0..1).</summary>
        public double Advance(double nowSeconds)
        {
            if (Current == Phase.Pending) Begin(nowSeconds);
            if (Current == Phase.Done) { HighlightPulse = 0; Fraction = 1; return 1; }
            double elapsed = nowSeconds - StartedAt;
            if (elapsed < 0) elapsed = 0;
            if (elapsed < HighlightSeconds)
            {
                Current = Phase.Highlight;
                double u = elapsed / HighlightSeconds;
                HighlightPulse = Math.Sin(Math.PI * u);          // 0 -> 1 -> 0
                Fraction = 0;
                return 0;
            }
            HighlightPulse = 0;
            double m = (elapsed - HighlightSeconds) / DurationSeconds;
            if (m >= 1)
            {
                Current = Phase.Done;
                Fraction = 1;
                return 1;
            }
            Current = Phase.Motion;
            Fraction = PlacementMotion.EaseInOut(m);
            return Fraction;
        }

        /// <summary>
        /// Hands over to the production success path exactly once, and only after the motion
        /// finished. Returns true when <paramref name="production"/> was invoked by this call.
        /// </summary>
        public bool Complete(Action production, double nowSeconds)
        {
            if (!MotionFinished || CompletionInvocations > 0) return false;
            CompletionInvocations++;
            CompletedAt = nowSeconds;
            if (production != null) production();
            return true;
        }
    }
}
