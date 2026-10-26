// File: HrvMath.cs
// Time-domain HRV arithmetic on a list of inter-beat intervals. Plain C#, deterministic,
// headless-testable. These are the live (online) estimates only; the raw ppi_sample records
// allow every analysis to be recomputed offline with any artefact policy.

using System;
using System.Collections.Generic;

namespace AdaptiveAR.Physiology
{
    public static class HrvMath
    {
        /// <summary>Root mean square of successive differences, ms. Null with fewer than 2 intervals.</summary>
        public static double? Rmssd(IReadOnlyList<double> intervalsMs)
        {
            if (intervalsMs == null || intervalsMs.Count < 2) return null;
            double sum = 0;
            for (int i = 1; i < intervalsMs.Count; i++)
            {
                double d = intervalsMs[i] - intervalsMs[i - 1];
                sum += d * d;
            }
            return Math.Sqrt(sum / (intervalsMs.Count - 1));
        }

        /// <summary>Mean interval, ms. Null when empty.</summary>
        public static double? MeanIbi(IReadOnlyList<double> intervalsMs)
        {
            if (intervalsMs == null || intervalsMs.Count == 0) return null;
            double sum = 0;
            for (int i = 0; i < intervalsMs.Count; i++) sum += intervalsMs[i];
            return sum / intervalsMs.Count;
        }

        /// <summary>Heart rate implied by a mean interval, beats per minute. Null when not computable.</summary>
        public static double? HeartRateFromIbi(double? meanIbiMs)
        {
            if (!meanIbiMs.HasValue || meanIbiMs.Value <= 0) return null;
            return 60000.0 / meanIbiMs.Value;
        }
    }
}
