// File: PhysiologyBootstrap.cs
// Creates the single persistent "PolarPpiReceiver" object at start-up from the config asset.
// Runtime creation (rather than a scene object) because the live scene file carries
// uncommitted researcher edits that must not be touched, and because the object must exist
// exactly once across scene reloads.

using UnityEngine;

namespace AdaptiveAR.Physiology
{
    public static class PhysiologyBootstrap
    {
        public const string ConfigResourcePath = "Physiology/PhysiologyConfig";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            if (PolarPpiReceiver.Instance != null) return;
            var cfg = Resources.Load<PhysiologyConfig>(ConfigResourcePath);
            if (cfg == null) { Debug.Log("[Physio] no Resources/" + ConfigResourcePath + " asset: physiological acquisition off."); return; }
            if (!cfg.enabled || cfg.source == PhysiologyConfig.SourceKind.None) { Debug.Log("[Physio] physiological acquisition disabled in config."); return; }

            var go = new GameObject(PolarPpiReceiver.RequiredGameObjectName);
            var receiver = go.AddComponent<PolarPpiReceiver>();
            receiver.deviceNameFilter = cfg.deviceNameFilter;
            receiver.requestedMtu = cfg.requestedMtu;
            receiver.maxReconnectAttempts = cfg.maxReconnectAttempts;
            receiver.autoStart = cfg.source == PhysiologyConfig.SourceKind.PolarVeritySense;

            if (cfg.source == PhysiologyConfig.SourceKind.Replay)
            {
                var replay = go.AddComponent<ReplayPpiSource>();
                replay.mode = cfg.replayMode;
                replay.jsonlRelativePath = cfg.replayJsonlRelativePath;
            }

            var provider = go.AddComponent<PhysiologicalStateProvider>();
            provider.windowSeconds = cfg.windowSeconds;
            provider.minAcceptedSamples = cfg.minAcceptedSamples;
            provider.minPpiMs = cfg.minPpiMs;
            provider.maxPpiMs = cfg.maxPpiMs;
            provider.rejectNoSkinContact = cfg.rejectNoSkinContact;
            provider.windowLogIntervalMs = cfg.windowLogIntervalMs;
            provider.maxSnapshotAgeMs = cfg.maxSnapshotAgeMs;

            Debug.Log($"[Physio] bootstrap: source={cfg.source} filter='{cfg.deviceNameFilter}' mtu={cfg.requestedMtu} window={cfg.windowSeconds}s minAccepted={cfg.minAcceptedSamples} range={cfg.minPpiMs}-{cfg.maxPpiMs}ms");
        }
    }
}
