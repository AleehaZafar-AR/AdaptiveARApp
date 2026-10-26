// File: PhysiologicalStateProvider.cs
// The physiological processing layer. Subscribes to ONE source (Polar receiver or replay),
// logs every raw sample with its live-window decision, maintains the rolling window
// (PhysiologyWindow, plain C#), writes physio_window summaries at a cadence, and feeds the
// researcher HUD. It exposes the latest summary and a Snapshot(); nothing here references
// support levels, decisions, or steps - physiology is context only.
//
// Stage 7/8 (PhysiologicalSample / DecisionContext) is deliberately NOT wired yet.

using System;
using System.Collections.Generic;
using AdaptiveAR.Logging;
using AdaptiveAR.Steps;
using AdaptiveAR.UI;
using UnityEngine;

namespace AdaptiveAR.Physiology
{
    public class PhysiologicalStateProvider : MonoBehaviour
    {
        [Header("Live window (implementation defaults, recorded in session_start)")]
        public float windowSeconds = 60f;
        public int minAcceptedSamples = 10;
        public int minPpiMs = 300;
        public int maxPpiMs = 2000;
        [Tooltip("OFF by default: Polar documents the Verity Sense skin-contact flag as not to be trusted.")]
        public bool rejectNoSkinContact = false;

        [Header("Cadence")]
        [Tooltip("physio_window line every N ms while samples exist.")]
        public float windowLogIntervalMs = 5000f;
        [Tooltip("Snapshot() returns null when the newest accepted sample is older than this.")]
        public float maxSnapshotAgeMs = 15000f;

        public PhysioSummary LatestSummary { get; private set; }
        public string SourceId { get { return _source != null ? _source.SourceId : "none"; } }
        public string ConnectionState { get; private set; } = "none";

        private IPhysiologicalSource _source;
        private PhysiologyWindow _window;
        private SessionLogger _logger;
        private StatusHud _hud;
        private StepValidator _validator;
        private float _nextWindowLog;
        private int _heldParts;
        private double _heldSinceMs = double.NaN;
        private readonly List<double[]> _grabIntervals = new List<double[]>();   // [startMs, endMs] monotonic
        private bool _configLogged;
        private long _samplesLogged;

        private static double NowMs { get { return Time.realtimeSinceStartup * 1000.0; } }

        private void Awake()
        {
            _window = new PhysiologyWindow(new PhysiologyWindowConfig
            {
                WindowMs = windowSeconds * 1000.0, MinAccepted = minAcceptedSamples, MinPpiMs = minPpiMs, MaxPpiMs = maxPpiMs, RejectNoSkinContact = rejectNoSkinContact
            });
        }

        private void OnEnable()
        {
            Bind();
        }

        private void OnDisable()
        {
            Unbind();
        }

        /// <summary>Attach to the source on this object (receiver preferred, replay otherwise) and to the scene's logger / HUD / validator.</summary>
        public void Bind()
        {
            var receiver = GetComponent<PolarPpiReceiver>();
            var replay = GetComponent<ReplayPpiSource>();
            IPhysiologicalSource chosen = replay != null && replay.enabled ? (IPhysiologicalSource)replay : receiver;
            if (!ReferenceEquals(chosen, _source))
            {
                Unsubscribe(_source);
                _source = chosen;
                if (_source != null)
                {
                    _source.SampleReceived += OnSample;
                    _source.StateChanged += OnSourceState;
                }
            }
            if (receiver != null) { receiver.Rebound -= Rebind; receiver.Rebound += Rebind; receiver.SetSessionClock(SessionMs); receiver.PacketReceived -= OnPacket; receiver.PacketReceived += OnPacket; }
            if (replay != null) replay.SetSessionClock(SessionMs);
            Rebind();
        }

        private void Unbind()
        {
            Unsubscribe(_source);
            _source = null;
            var receiver = GetComponent<PolarPpiReceiver>();
            if (receiver != null) { receiver.Rebound -= Rebind; receiver.PacketReceived -= OnPacket; }
            UnhookValidator();
        }

        private void Unsubscribe(IPhysiologicalSource s)
        {
            if (s == null) return;
            s.SampleReceived -= OnSample;
            s.StateChanged -= OnSourceState;
        }

        /// <summary>Re-find scene objects (after a scene reload the old ones are gone). Logged so it can be verified.</summary>
        private void Rebind()
        {
            _logger = FindAnyObjectByType<SessionLogger>(FindObjectsInactive.Include);
            _hud = FindAnyObjectByType<StatusHud>(FindObjectsInactive.Include);
            UnhookValidator();
            _validator = FindAnyObjectByType<StepValidator>(FindObjectsInactive.Include);
            if (_validator != null) { _validator.OnPartGrabbed += OnGrab; _validator.OnPartReleased += OnRelease; }
            _grabIntervals.Clear(); _heldParts = 0; _heldSinceMs = double.NaN;
            _window.Reset();                                   // a new session starts a new window
            _configLogged = false;
            if (_logger != null)
            {
                _logger.PhysioSource = SourceId;
                _logger.PhysioAcceptRules = _window.Config.Describe();
                _logger.PhysioBridgeVersion = "BleBridge " + BridgeVersion();
                LogConfigIfOpen();
            }
            Debug.Log($"[Physio] rebound: logger={(_logger != null ? _logger.GetInstanceID().ToString() : "none")} hud={(_hud != null)} validator={(_validator != null)} source={SourceId} rules={_window.Config.Describe()}");
            if (_logger != null) _logger.LogPhysioConnection("rebind", $"source={SourceId} logger={_logger.GetInstanceID()}", DeviceName());
        }

        private void LogConfigIfOpen()
        {
            if (_configLogged || _logger == null || !_logger.IsOpen) return;
            _configLogged = true;
            _logger.LogPhysioConnection("config", $"rules={_window.Config.Describe()} source={SourceId} bridge={BridgeVersion()}", DeviceName());
        }

        private void UnhookValidator()
        {
            if (_validator != null) { _validator.OnPartGrabbed -= OnGrab; _validator.OnPartReleased -= OnRelease; }
            _validator = null;
        }

        private string DeviceName()
        {
            var r = _source as PolarPpiReceiver;
            return r != null ? r.DeviceName : null;
        }

        private static string BridgeVersion() { return "1.0.0"; }

        private double SessionMs()
        {
            return _logger != null && _logger.IsOpen ? _logger.SessionElapsedMs : -1;
        }

        // ------------------------------------------------------------------ source events

        private void OnSourceState(string state, string detail)
        {
            ConnectionState = state;
            if (_logger != null) _logger.LogPhysioConnection(state, detail, DeviceName());
            PushHud();
        }

        private void OnPacket(long seq, int len, int n)
        {
            if (_logger != null) _logger.LogPhysioConnection("data_packet", $"seq={seq} len={len} samples={n}", DeviceName());
        }

        private void OnSample(PpiSample s)
        {
            double now = NowMs;
            if (s.ReceiptSessionMs < 0) s.ReceiptSessionMs = SessionMs();
            _window.Admit(s, now);                              // decides Accepted / RejectReason, drops nothing
            LogConfigIfOpen();
            if (_logger != null) { _logger.LogPpiSample(s); _samplesLogged++; }
            LatestSummary = _window.Summarize(now);
            PushHud();
        }

        private void Update()
        {
            if (_window == null || _window.RawCount == 0) return;
            if (Time.realtimeSinceStartup * 1000f < _nextWindowLog) return;
            _nextWindowLog = Time.realtimeSinceStartup * 1000f + windowLogIntervalMs;
            double now = NowMs;
            LatestSummary = _window.Summarize(now);
            if (_logger != null)
                _logger.LogPhysioWindow(LatestSummary, "rolling", ConnectionState, SourceId, GrabOverlapMs(LatestSummary.WindowStartMs, LatestSummary.WindowEndMs));
            PushHud();
        }

        /// <summary>Context snapshot: null when unavailable or stale. (Not consumed by the decision seam until Stage 8.)</summary>
        public PhysioSummary Snapshot()
        {
            return _window != null ? _window.Snapshot(NowMs, maxSnapshotAgeMs) : null;
        }

        // ------------------------------------------------------------------ movement proxy: grab intervals

        private void OnGrab(string key, Transform part)
        {
            if (_heldParts++ == 0) _heldSinceMs = NowMs;
        }

        private void OnRelease(string key, Transform part)
        {
            if (_heldParts > 0) _heldParts--;
            if (_heldParts == 0 && !double.IsNaN(_heldSinceMs))
            {
                _grabIntervals.Add(new[] { _heldSinceMs, NowMs });
                _heldSinceMs = double.NaN;
                double cutoff = NowMs - 10 * 60 * 1000;
                _grabIntervals.RemoveAll(iv => iv[1] < cutoff);
            }
        }

        private double GrabOverlapMs(double startMs, double endMs)
        {
            double total = 0;
            foreach (double[] iv in _grabIntervals)
                total += Math.Max(0, Math.Min(iv[1], endMs) - Math.Max(iv[0], startMs));
            if (!double.IsNaN(_heldSinceMs))
                total += Math.Max(0, endMs - Math.Max(_heldSinceMs, startMs));
            return total;
        }

        // ------------------------------------------------------------------ HUD

        private void PushHud()
        {
            if (_hud == null) return;
            PhysioSummary s = LatestSummary;
            string hr = s != null && s.LastDeviceHrBpm.HasValue && s.LastDeviceHrBpm.Value > 0 ? s.LastDeviceHrBpm.Value.ToString() : "--";
            string ppi = s != null && s.LastPpiMs.HasValue ? $"{s.LastPpiMs.Value:F0}" : "--";
            string rmssd = s != null && s.Available && s.RmssdMs.HasValue ? $"{s.RmssdMs.Value:F0}" : "--";
            string q = s != null && s.Quality.HasValue ? $"{s.Quality.Value:F2}" : "n/a";
            string n = s != null ? s.NAccepted.ToString() : "0";
            _hud.SetPhysiology($"{ConnectionState}  HR {hr}  PPI {ppi}  RMSSD {rmssd}  n {n}  q {q}");
        }
    }
}
