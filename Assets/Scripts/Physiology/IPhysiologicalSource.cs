// File: IPhysiologicalSource.cs
// A source of raw PPI samples. The Polar receiver and the replay/synthetic source implement
// it; the physiological processing layer subscribes to whichever is active, so bench runs,
// offline evaluation and the headset use one processing path (CLAUDE.md section 7.4).

using System;

namespace AdaptiveAR.Physiology
{
    public interface IPhysiologicalSource
    {
        /// <summary>Stable source label recorded on every sample, e.g. "PolarVeritySense/Polar Sense 0A0E0132" or "replay/synthetic".</summary>
        string SourceId { get; }

        /// <summary>True while samples are expected to arrive.</summary>
        bool IsStreaming { get; }

        /// <summary>Raised on the Unity main thread for every raw sample, already stamped with receipt time.</summary>
        event Action<PpiSample> SampleReceived;

        /// <summary>(state, detail) connection / stream state changes.</summary>
        event Action<string, string> StateChanged;
    }
}
