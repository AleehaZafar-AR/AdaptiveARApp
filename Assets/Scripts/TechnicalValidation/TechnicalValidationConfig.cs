// File: TechnicalValidationConfig.cs
// Level 2 (automated in-engine / Quest validation) activation and options. Plain C#: no
// Unity dependency, so the headless tests cover the parsing and the defaults.
//
// ISOLATION. The runner exists only when the file
//   <persistentDataPath>/AdaptiveAR/config/technical_validation.json
// exists AND carries "enabled": true. Without that file nothing in this namespace is
// instantiated: no runner, no overlay, no synthetic physiology, no injected event, no
// change to the participant flow. The decision architecture, prompt, scheduler, gate,
// support policy, physiology interpretation and logging are never touched by this layer;
// it only feeds the production entry points a participant's hands would reach.

using System;
using System.Collections.Generic;
using System.Globalization;
using AdaptiveAR.Decision;

namespace AdaptiveAR.TechnicalValidation
{
    public enum ValidationMode
    {
        /// <summary>Formal technical validation: authored event timing, real-time production scheduler semantics.</summary>
        Validation = 0,
        /// <summary>Recording / demonstration: the same scenario, shortened delays with floors so transitions stay visible.</summary>
        Presentation = 1
    }

    public enum ValidationPhysiology
    {
        /// <summary>Synthetic PPI stream, resting-like profile. A signal profile only, no state is asserted.</summary>
        Nominal = 0,
        /// <summary>Synthetic PPI stream, higher rate / lower variability profile. A signal profile only, no state is asserted.</summary>
        Elevated = 1,
        /// <summary>No physiological source at all: the decision context carries "physiology unavailable".</summary>
        Unavailable = 2
    }

    public sealed class TechnicalValidationConfig
    {
        public const string ConfigFileName = "technical_validation.json";
        public const string ConfigFolder = "AdaptiveAR/config";
        public const string OutputFolder = "AdaptiveAR/technical_validation";
        public const string ConditionLabel = "technical_validation";

        public bool Enabled;
        public string Scenario = "TV1";
        public ValidationMode Mode = ValidationMode.Validation;
        public ValidationPhysiology Physiology = ValidationPhysiology.Nominal;

        /// <summary>Multiplier on every authored delay. Null = the mode's default (validation 1.0, presentation 0.5).</summary>
        public double? DelayScale;
        /// <summary>Lower bound on a scaled delay, seconds. Null = the mode's default (validation 0, presentation 2.5).</summary>
        public double? MinDelaySeconds;
        /// <summary>Extra hold after an applied support transition so the cue, replay and new guidance can be seen. Null = mode default (validation 0, presentation 6).</summary>
        public double? PostTransitionHoldSeconds;
        /// <summary>Delay between the injected events that finish the sequence after the scripted part. Null = mode default (validation 4, presentation 5).</summary>
        public double? FinishDelaySeconds;

        /// <summary>Presentation only: seconds of eased motion from the part's pose to the validated target pose before the production success path runs. Null = 0.9 in presentation. Always 0 (immediate injection) in validation mode, whatever the file says.</summary>
        public double? PlacementMotionSeconds;
        /// <summary>Presentation only: brief highlight (scale pulse) of the part before the motion. Null = 0.35 in presentation. Always 0 in validation mode.</summary>
        public double? PlacementHighlightSeconds;

        /// <summary>Seconds after the assembly starts (workspace placed, Begin pressed, first step entered) before the first scripted event.</summary>
        public double StartDelaySeconds = 3.0;
        /// <summary>Participant id written on every production log line. Null = "TV_<scenario>".</summary>
        public string ParticipantId;
        /// <summary>Overrides the onboarding sensor-stabilisation wait (seconds) for the synthetic stream. Null = unchanged production value.</summary>
        public double? SensorStabilisationSeconds;
        /// <summary>Show the TECHNICAL VALIDATION overlay (always true for the formal run; can be hidden for a clean recording).</summary>
        public bool Overlay = true;
        /// <summary>Also print each injected event to the Unity log.</summary>
        public bool Verbose = true;

        public string SourceDescription = "defaults";

        public double EffectiveDelayScale { get { return DelayScale ?? (Mode == ValidationMode.Presentation ? 0.5 : 1.0); } }
        public double EffectiveMinDelaySeconds { get { return MinDelaySeconds ?? (Mode == ValidationMode.Presentation ? 2.5 : 0.0); } }
        public double EffectivePostTransitionHoldSeconds { get { return PostTransitionHoldSeconds ?? (Mode == ValidationMode.Presentation ? 6.0 : 0.0); } }
        public double EffectiveFinishDelaySeconds { get { return FinishDelaySeconds ?? (Mode == ValidationMode.Presentation ? 5.0 : 4.0); } }
        public string EffectiveParticipantId { get { return string.IsNullOrEmpty(ParticipantId) ? "TV_" + (Scenario ?? "unknown") : ParticipantId; } }
        /// <summary>0 unless technical validation is enabled AND the mode is presentation: formal validation results never depend on animation timing, and a participant session never has a motion.</summary>
        public double EffectivePlacementMotionSeconds
        {
            get
            {
                if (!Enabled || Mode != ValidationMode.Presentation) return 0;
                double v = PlacementMotionSeconds ?? PlacementMotion.DefaultDurationSeconds;
                return v > 0 && !double.IsNaN(v) ? v : 0;
            }
        }
        /// <summary>0 outside presentation mode, and 0 when the motion itself is off.</summary>
        public double EffectivePlacementHighlightSeconds
        {
            get
            {
                if (EffectivePlacementMotionSeconds <= 0) return 0;
                double v = PlacementHighlightSeconds ?? PlacementMotion.DefaultHighlightSeconds;
                return v > 0 && !double.IsNaN(v) ? v : 0;
            }
        }

        /// <summary>The real-time delay for an authored delay: scaled, then floored. Never negative.</summary>
        public double ScaleDelay(double authoredSeconds)
        {
            if (authoredSeconds <= 0) return 0;
            double s = authoredSeconds * EffectiveDelayScale;
            if (s < EffectiveMinDelaySeconds) s = EffectiveMinDelaySeconds;
            return s;
        }

        /// <summary>Null or empty text = disabled (no file). Malformed JSON = disabled with the reason in SourceDescription.</summary>
        public static TechnicalValidationConfig Parse(string json, string source)
        {
            var cfg = new TechnicalValidationConfig { SourceDescription = source ?? "none" };
            if (string.IsNullOrWhiteSpace(json)) { cfg.Enabled = false; cfg.SourceDescription = "absent"; return cfg; }
            Dictionary<string, object> o;
            try { o = MiniJson.ParseObject(json); }
            catch (Exception e) { cfg.Enabled = false; cfg.SourceDescription = "unreadable: " + e.Message; return cfg; }
            if (o == null) { cfg.Enabled = false; cfg.SourceDescription = "unreadable: not a JSON object"; return cfg; }

            cfg.Enabled = MiniJson.GetBool(o, "enabled", false);
            cfg.Scenario = MiniJson.GetString(o, "scenario", "TV1");
            cfg.Mode = ParseMode(MiniJson.GetString(o, "mode", "validation"));
            cfg.Physiology = ParsePhysiology(MiniJson.GetString(o, "physiology", "nominal"));
            cfg.DelayScale = MiniJson.GetNullableDouble(o, "delayScale");
            cfg.MinDelaySeconds = MiniJson.GetNullableDouble(o, "minDelaySeconds");
            cfg.PostTransitionHoldSeconds = MiniJson.GetNullableDouble(o, "postTransitionHoldSeconds");
            cfg.FinishDelaySeconds = MiniJson.GetNullableDouble(o, "finishDelaySeconds");
            cfg.PlacementMotionSeconds = MiniJson.GetNullableDouble(o, "placementMotionSeconds");
            cfg.PlacementHighlightSeconds = MiniJson.GetNullableDouble(o, "placementHighlightSeconds");
            cfg.StartDelaySeconds = MiniJson.GetDouble(o, "startDelaySeconds", 3.0);
            cfg.ParticipantId = MiniJson.GetString(o, "participantId", null);
            cfg.SensorStabilisationSeconds = MiniJson.GetNullableDouble(o, "sensorStabilisationSeconds");
            cfg.Overlay = MiniJson.GetBool(o, "overlay", true);
            cfg.Verbose = MiniJson.GetBool(o, "verbose", true);
            return cfg;
        }

        public static ValidationMode ParseMode(string s)
        {
            return string.Equals(s, "presentation", StringComparison.OrdinalIgnoreCase) ? ValidationMode.Presentation : ValidationMode.Validation;
        }

        public static ValidationPhysiology ParsePhysiology(string s)
        {
            if (string.Equals(s, "elevated", StringComparison.OrdinalIgnoreCase)) return ValidationPhysiology.Elevated;
            if (string.Equals(s, "unavailable", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "null", StringComparison.OrdinalIgnoreCase)) return ValidationPhysiology.Unavailable;
            return ValidationPhysiology.Nominal;
        }

        public string ModeName { get { return Mode == ValidationMode.Presentation ? "presentation" : "validation"; } }
        public string PhysiologyName
        {
            get
            {
                switch (Physiology)
                {
                    case ValidationPhysiology.Elevated: return "elevated";
                    case ValidationPhysiology.Unavailable: return "unavailable";
                    default: return "nominal";
                }
            }
        }

        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            return $"enabled={Enabled};scenario={Scenario};mode={ModeName};physiology={PhysiologyName};delayScale={EffectiveDelayScale.ToString("0.##", c)};" +
                   $"minDelay={EffectiveMinDelaySeconds.ToString("0.##", c)}s;postTransitionHold={EffectivePostTransitionHoldSeconds.ToString("0.##", c)}s;" +
                   $"finishDelay={EffectiveFinishDelaySeconds.ToString("0.##", c)}s;startDelay={StartDelaySeconds.ToString("0.##", c)}s;participant={EffectiveParticipantId};" +
                   $"sensorStabilisation={(SensorStabilisationSeconds.HasValue ? SensorStabilisationSeconds.Value.ToString("0.##", c) + "s" : "unchanged")};" +
                   $"placementMotion={EffectivePlacementMotionSeconds.ToString("0.##", c)}s;placementHighlight={EffectivePlacementHighlightSeconds.ToString("0.##", c)}s;source={SourceDescription}";
        }
    }
}
