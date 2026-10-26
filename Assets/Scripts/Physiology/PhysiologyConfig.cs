// File: PhysiologyConfig.cs
// Researcher-tunable configuration for the physiological acquisition subsystem, loaded from
// Resources/Physiology/PhysiologyConfig by PhysiologyBootstrap. Keeps the subsystem out of
// the hand-authored scene file (which carries uncommitted researcher edits) and behind a
// toggle (CLAUDE.md section 4.4).

using UnityEngine;

namespace AdaptiveAR.Physiology
{
    [CreateAssetMenu(fileName = "PhysiologyConfig", menuName = "AdaptiveAR/Physiology Config")]
    public class PhysiologyConfig : ScriptableObject
    {
        public enum SourceKind { None = 0, PolarVeritySense = 1, Replay = 2 }

        [Header("Subsystem")]
        public bool enabled = true;
        public SourceKind source = SourceKind.PolarVeritySense;

        [Header("Polar BLE")]
        public string deviceNameFilter = "Polar";
        public int requestedMtu = 512;
        public int maxReconnectAttempts = 20;

        [Header("Live window")]
        public float windowSeconds = 60f;
        public int minAcceptedSamples = 10;
        public int minPpiMs = 300;
        public int maxPpiMs = 2000;
        public bool rejectNoSkinContact = false;
        public float windowLogIntervalMs = 5000f;
        public float maxSnapshotAgeMs = 15000f;

        [Header("Replay (source = Replay)")]
        public ReplayPpiSource.Mode replayMode = ReplayPpiSource.Mode.Synthetic;
        public string replayJsonlRelativePath = "";
    }
}
