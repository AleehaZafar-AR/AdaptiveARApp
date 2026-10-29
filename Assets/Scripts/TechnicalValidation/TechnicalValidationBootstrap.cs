// File: TechnicalValidationBootstrap.cs
// The ONLY activation path of Level 2. Runs once after the first scene loads:
//
//   1. no persistentDataPath/AdaptiveAR/config/technical_validation.json  -> return (one log line)
//   2. file present but "enabled" != true                                  -> return (one log line)
//   3. enabled -> load the scenario, label the production logger, set up the synthetic /
//      unavailable physiology, create the runner (+ overlay).
//
// Participant builds never carry that file, so steps 3+ never run and nothing in this
// namespace is instantiated. A build can additionally compile this file out with the
// scripting define ADAPTIVEAR_DISABLE_TECHNICAL_VALIDATION.
//
// Physiology in validation mode uses the EXISTING abstraction: the production
// PolarPpiReceiver object hosts a ReplayPpiSource (Synthetic) that the production
// PhysiologicalStateProvider prefers when present, so the window, HRV maths, snapshot
// rules and logging are the production ones. The two profiles are signal profiles only
// (mean interval, modulation, noise); nothing classifies a state from them. "unavailable"
// leaves no provider at all, so the decision context says physiology is missing.

using System;
using System.IO;
using AdaptiveAR.Logging;
using AdaptiveAR.Physiology;
using UnityEngine;

namespace AdaptiveAR.TechnicalValidation
{
    public static class TechnicalValidationBootstrap
    {
        public const string ScenarioResourceFolder = "TechnicalValidation";
        public const string ScenarioOverrideFolder = "AdaptiveAR/technical_validation/scenarios";

#if !ADAPTIVEAR_DISABLE_TECHNICAL_VALIDATION
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            if (TechnicalValidationRunner.Instance != null) return;
            string path = Path.Combine(Application.persistentDataPath, TechnicalValidationConfig.ConfigFolder, TechnicalValidationConfig.ConfigFileName);
            if (!File.Exists(path)) { Debug.Log("[TechnicalValidation] off: no " + path); return; }

            TechnicalValidationConfig cfg;
            try { cfg = TechnicalValidationConfig.Parse(File.ReadAllText(path), "file:" + path); }
            catch (Exception e) { Debug.LogError("[TechnicalValidation] config unreadable, staying OFF: " + e.Message); return; }
            if (!cfg.Enabled) { Debug.Log("[TechnicalValidation] off: " + cfg.SourceDescription + " has enabled=false"); return; }

            Debug.LogWarning("[TechnicalValidation] ENABLED: " + cfg.Describe() + " - this is NOT a participant session.");

            ValidationScenario scenario = null; string loadError = null;
            try { scenario = LoadScenario(cfg.Scenario); }
            catch (Exception e) { loadError = e.Message; Debug.LogError("[TechnicalValidation] scenario '" + cfg.Scenario + "' failed to load: " + e.Message); }

            // The production logger labels every line of this session as technical validation.
            var logger = UnityEngine.Object.FindAnyObjectByType<SessionLogger>(FindObjectsInactive.Include);
            if (logger != null) logger.SetParticipant(cfg.EffectiveParticipantId, TechnicalValidationConfig.ConditionLabel);

            ValidationPhysiology condition = cfg.Physiology;
            if (scenario != null && !string.IsNullOrEmpty(scenario.Physiology)) condition = TechnicalValidationConfig.ParsePhysiology(scenario.Physiology);
            try { ApplyPhysiology(condition); }
            catch (Exception e) { Debug.LogError("[TechnicalValidation] physiology setup threw: " + e); }

            var go = new GameObject("TechnicalValidationRunner");
            var runner = go.AddComponent<TechnicalValidationRunner>();
            runner.Configure(cfg, scenario, loadError);
            if (cfg.Overlay) go.AddComponent<TechnicalValidationOverlay>();
        }
#endif

        /// <summary>persistentDataPath override first (edit without a rebuild), then the bundled Resources/TechnicalValidation/&lt;id&gt;.json.</summary>
        public static ValidationScenario LoadScenario(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new FormatException("no scenario id in the config");
            string over = Path.Combine(Application.persistentDataPath, ScenarioOverrideFolder, id + ".json");
            if (File.Exists(over)) return ValidationScenario.Parse(File.ReadAllText(over), "file:" + over);
            var ta = Resources.Load<TextAsset>(ScenarioResourceFolder + "/" + id);
            if (ta == null) throw new FileNotFoundException("no bundled scenario Resources/" + ScenarioResourceFolder + "/" + id + ".json and no override at " + over);
            return ValidationScenario.Parse(ta.text, "resource:" + ScenarioResourceFolder + "/" + id);
        }

        /// <summary>
        /// Runs before any Start(): the receiver (created here or by PhysiologyBootstrap, whichever
        /// ran first) never scans for a sensor in validation mode, and the provider binds to the
        /// synthetic source or does not exist at all.
        /// </summary>
        public static void ApplyPhysiology(ValidationPhysiology condition)
        {
            PolarPpiReceiver receiver = PolarPpiReceiver.Instance;
            GameObject host;
            if (receiver == null)
            {
                host = new GameObject(PolarPpiReceiver.RequiredGameObjectName);
                receiver = host.AddComponent<PolarPpiReceiver>();     // Instance set in Awake: PhysiologyBootstrap now returns early
                receiver.autoStart = false;
            }
            else
            {
                host = receiver.gameObject;
                receiver.autoStart = false;                            // Start() has not run yet (RuntimeInitialize runs before it)
                receiver.Stop();
            }

            var replay = host.GetComponent<ReplayPpiSource>();
            var provider = host.GetComponent<PhysiologicalStateProvider>();

            if (condition == ValidationPhysiology.Unavailable)
            {
                if (replay != null) UnityEngine.Object.Destroy(replay);
                if (provider != null) UnityEngine.Object.Destroy(provider);
                Debug.Log("[TechnicalValidation] physiology: unavailable (no provider; the decision context carries 'physiology missing'; sensor setup is skipped).");
                return;
            }

            if (replay == null) replay = host.AddComponent<ReplayPpiSource>();
            replay.mode = ReplayPpiSource.Mode.Synthetic;
            replay.seed = 7;
            replay.blockerProbability = 0.02f;
            if (condition == ValidationPhysiology.Elevated)
            {
                // Higher rate, lower beat-to-beat variability. A signal profile, not a state.
                replay.meanIbiMs = 625f; replay.modulationMs = 20f; replay.modulationPeriodS = 4f; replay.noiseMs = 6f;
            }
            else
            {
                // Resting-like profile.
                replay.meanIbiMs = 833f; replay.modulationMs = 60f; replay.modulationPeriodS = 5f; replay.noiseMs = 10f;
            }
            replay.enabled = true;

            if (provider == null)
            {
                provider = host.AddComponent<PhysiologicalStateProvider>();
                var cfg = Resources.Load<PhysiologyConfig>(PhysiologyBootstrap.ConfigResourcePath);
                if (cfg != null)
                {
                    provider.windowSeconds = cfg.windowSeconds; provider.minAcceptedSamples = cfg.minAcceptedSamples; provider.minPpiMs = cfg.minPpiMs; provider.maxPpiMs = cfg.maxPpiMs;
                    provider.rejectNoSkinContact = cfg.rejectNoSkinContact; provider.windowLogIntervalMs = cfg.windowLogIntervalMs; provider.maxSnapshotAgeMs = cfg.maxSnapshotAgeMs;
                }
            }
            provider.Bind();                                            // prefers the enabled ReplayPpiSource over the receiver
            Debug.Log($"[TechnicalValidation] physiology: synthetic {(condition == ValidationPhysiology.Elevated ? "elevated" : "nominal")} profile (mean IBI {replay.meanIbiMs:F0} ms, modulation {replay.modulationMs:F0} ms, noise {replay.noiseMs:F0} ms) through the production ReplayPpiSource -> PhysiologicalStateProvider path; BLE receiver idle.");
        }
    }
}
