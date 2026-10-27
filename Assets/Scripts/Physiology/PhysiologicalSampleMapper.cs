// File: PhysiologicalSampleMapper.cs
// Maps the live window summary onto the existing decision-context type. Plain C#, so the
// mapping is covered by the headless tests. A summary that is unavailable (too few accepted
// samples) or stale maps to null: "not measured" is represented, never fabricated.

using AdaptiveAR.Decision;

namespace AdaptiveAR.Physiology
{
    public static class PhysiologicalSampleMapper
    {
        /// <summary>Null when the summary is null or not available; otherwise the context sample.</summary>
        public static PhysiologicalSample ToSample(PhysioSummary s, string source)
        {
            if (s == null || !s.Available) return null;
            return new PhysiologicalSample
            {
                RmssdMs = s.RmssdMs.HasValue ? (float?)s.RmssdMs.Value : null,
                // The device's own heart rate (its HR algorithm) when present; otherwise the rate implied by the accepted intervals.
                HeartRateBpm = s.LastDeviceHrBpm.HasValue && s.LastDeviceHrBpm.Value > 0 ? (float?)s.LastDeviceHrBpm.Value
                             : (s.HrFromIbiBpm.HasValue ? (float?)s.HrFromIbiBpm.Value : null),
                MeanIbiMs = s.MeanIbiMs.HasValue ? (float?)s.MeanIbiMs.Value : null,
                Quality = s.Quality.HasValue ? (float?)s.Quality.Value : null,
                AgeMs = s.SampleAgeMs.HasValue ? (float?)s.SampleAgeMs.Value : null,
                Source = source
            };
        }
    }
}
