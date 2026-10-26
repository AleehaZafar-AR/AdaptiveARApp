// File: PhysiologyWindow.cs
// The live physiological window: raw sample admission under an explicit, configurable policy,
// a rolling time window of accepted intervals, and the summary the application may use as
// CONTEXT. Plain C# (no Unity): the same code runs under the Polar receiver, the replay source
// and the headless tests.
//
// What this is not: a stress classifier. RMSSD here is a supplementary indicator of autonomic
// response computed online from PPG-derived intervals; it never changes a support level.

using System;
using System.Collections.Generic;

namespace AdaptiveAR.Physiology
{
    /// <summary>Live acceptance policy. Defaults are implementation defaults, not physiological truth.</summary>
    public sealed class PhysiologyWindowConfig
    {
        /// <summary>Rolling window length, ms.</summary>
        public double WindowMs = 60000;
        /// <summary>Accepted intervals required before RMSSD is reported.</summary>
        public int MinAccepted = 10;
        /// <summary>Plausibility range for a PP interval, ms (inclusive).</summary>
        public int MinPpiMs = 300;
        public int MaxPpiMs = 2000;
        /// <summary>Reject samples whose skin-contact flag is 0 (only when the device says the flag is supported).
        /// OFF by default: Polar states the Verity Sense skin-contact flag should not be trusted.</summary>
        public bool RejectNoSkinContact = false;

        public string Describe()
        {
            return $"window_ms={WindowMs:F0};min_accepted={MinAccepted};ppi_range_ms={MinPpiMs}-{MaxPpiMs};reject_no_skin_contact={RejectNoSkinContact};successive_filter=none";
        }
    }

    /// <summary>One window summary. Every numeric field is null when it cannot be computed.</summary>
    public sealed class PhysioSummary
    {
        public double WindowStartMs, WindowEndMs;
        public bool Available;                 // enough accepted samples for RMSSD
        public double? RmssdMs, MeanIbiMs, HrFromIbiBpm;
        public int? LastDeviceHrBpm;
        public int NRaw, NAccepted, NRejected, NRejectedBlocker, NRejectedRange, NRejectedSkin;
        public double? MeanErrorEstimateMs;
        /// <summary>Sum of accepted intervals / window length (0..1+).</summary>
        public double Completeness;
        /// <summary>Accepted / raw within the window; null when no raw samples.</summary>
        public double? Quality;
        /// <summary>Age of the newest accepted sample, ms; null when none.</summary>
        public double? SampleAgeMs;
        public double? LastPpiMs;
    }

    public sealed class PhysiologyWindow
    {
        public const string RejectBlocker = "blocker";
        public const string RejectRange = "range";
        public const string RejectSkin = "skin_contact";

        private readonly PhysiologyWindowConfig _cfg;
        private readonly List<PpiSample> _raw = new List<PpiSample>();       // every sample inside the window
        private readonly List<double> _acceptedIntervals = new List<double>();
        private double _lastAcceptedAtMs = double.NaN;
        private int? _lastDeviceHr;
        private double? _lastPpi;

        public PhysiologyWindow(PhysiologyWindowConfig config) { _cfg = config ?? new PhysiologyWindowConfig(); }

        public PhysiologyWindowConfig Config { get { return _cfg; } }
        public int RawCount { get { return _raw.Count; } }
        public int AcceptedCount { get { return _acceptedIntervals.Count; } }

        public void Reset()
        {
            _raw.Clear(); _acceptedIntervals.Clear(); _lastAcceptedAtMs = double.NaN; _lastDeviceHr = null; _lastPpi = null;
        }

        /// <summary>
        /// Decides whether the sample enters the live window and records the decision ON the
        /// sample (Accepted / RejectReason). Nothing is discarded: the caller logs the sample
        /// with its decision. nowMs = monotonic clock at receipt.
        /// </summary>
        public void Admit(PpiSample s, double nowMs)
        {
            if (s == null) return;
            s.Accepted = false; s.RejectReason = null;
            if (s.Blocker) s.RejectReason = RejectBlocker;
            else if (s.PpiMs < _cfg.MinPpiMs || s.PpiMs > _cfg.MaxPpiMs) s.RejectReason = RejectRange;
            else if (_cfg.RejectNoSkinContact && s.SkinContactSupported && !s.SkinContact) s.RejectReason = RejectSkin;
            else s.Accepted = true;

            _lastDeviceHr = s.DeviceHrBpm;
            _lastPpi = s.PpiMs;
            _raw.Add(s);
            _rawAt.Add(nowMs);
            if (s.Accepted) { _acceptedIntervals.Add(s.PpiMs); _acceptedAt.Add(nowMs); _lastAcceptedAtMs = nowMs; }
            Trim(nowMs);
        }

        private readonly List<double> _rawAt = new List<double>();
        private readonly List<double> _acceptedAt = new List<double>();

        private void Trim(double nowMs)
        {
            double cutoff = nowMs - _cfg.WindowMs;
            // the window is (now - WindowMs, now]: a sample exactly WindowMs old has left it
            while (_rawAt.Count > 0 && _rawAt[0] <= cutoff) { _rawAt.RemoveAt(0); _raw.RemoveAt(0); }
            while (_acceptedAt.Count > 0 && _acceptedAt[0] <= cutoff) { _acceptedAt.RemoveAt(0); _acceptedIntervals.RemoveAt(0); }
        }

        /// <summary>The live summary at nowMs. Never throws; fields are null when not computable.</summary>
        public PhysioSummary Summarize(double nowMs)
        {
            Trim(nowMs);
            var s = new PhysioSummary { WindowStartMs = nowMs - _cfg.WindowMs, WindowEndMs = nowMs };
            s.NRaw = _raw.Count;
            double errSum = 0;
            foreach (PpiSample r in _raw)
            {
                errSum += r.ErrorEstimateMs;
                if (r.Accepted) continue;
                s.NRejected++;
                if (r.RejectReason == RejectBlocker) s.NRejectedBlocker++;
                else if (r.RejectReason == RejectRange) s.NRejectedRange++;
                else if (r.RejectReason == RejectSkin) s.NRejectedSkin++;
            }
            s.NAccepted = _acceptedIntervals.Count;
            s.MeanErrorEstimateMs = s.NRaw > 0 ? errSum / s.NRaw : (double?)null;
            s.Quality = s.NRaw > 0 ? (double?)((double)s.NAccepted / s.NRaw) : null;
            double acceptedTime = 0; foreach (double v in _acceptedIntervals) acceptedTime += v;
            s.Completeness = _cfg.WindowMs > 0 ? acceptedTime / _cfg.WindowMs : 0;
            s.SampleAgeMs = double.IsNaN(_lastAcceptedAtMs) ? (double?)null : nowMs - _lastAcceptedAtMs;
            s.LastDeviceHrBpm = _lastDeviceHr;
            s.LastPpiMs = _lastPpi;
            s.Available = s.NAccepted >= Math.Max(2, _cfg.MinAccepted);
            if (s.Available)
            {
                s.RmssdMs = HrvMath.Rmssd(_acceptedIntervals);
                s.MeanIbiMs = HrvMath.MeanIbi(_acceptedIntervals);
                s.HrFromIbiBpm = HrvMath.HeartRateFromIbi(s.MeanIbiMs);
            }
            return s;
        }

        /// <summary>
        /// The summary for use as decision CONTEXT: null when unavailable (too few accepted
        /// samples) or stale (newest accepted sample older than maxAgeMs).
        /// </summary>
        public PhysioSummary Snapshot(double nowMs, double maxAgeMs)
        {
            PhysioSummary s = Summarize(nowMs);
            if (!s.Available) return null;
            if (!s.SampleAgeMs.HasValue || s.SampleAgeMs.Value > maxAgeMs) return null;
            return s;
        }
    }
}
