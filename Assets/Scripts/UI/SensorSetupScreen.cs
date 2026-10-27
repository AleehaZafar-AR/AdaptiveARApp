// File: SensorSetupScreen.cs
// Pre-task sensor initialisation / stabilisation, shown on the home panel before the
// existing onboarding (placement -> welcome -> ... -> Begin Assembly).
//
//   A Connecting     : hold screen, no Continue; status line driven by the BLE/physiology state.
//                      Leaves only when the stream has started, a valid PPI sample has reached
//                      the provider and the device heart rate is non-zero. No timeout.
//   B Stabilisation  : 60 s countdown ("Ready in 00:60"), live heart rate, simple signal line.
//   C Ready          : only when the provider has a valid snapshot (enough accepted samples,
//                      not stale). Otherwise "Waiting for a stable sensor signal" until it does.
//                      Continue hands over to OnboardingSequence.Begin().
//
// This is sensor initialisation and an initial physiological context, not a clinical or
// validated resting baseline, and the wording keeps it that way. Physiology stays optional:
// with the config source None (or no provider) the screen is skipped and logged as skipped.
// Nothing here touches support levels or decisions.

using System;
using AdaptiveAR.Logging;
using AdaptiveAR.Physiology;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.UI
{
    public class SensorSetupScreen : MonoBehaviour
    {
        public enum Phase { Skipped, Connecting, Stabilising, Waiting, Ready, Done }

        [Tooltip("Stabilisation countdown, seconds. An implementation choice: initial physiological context before the task.")]
        public float stabilisationSeconds = 60f;
        [Tooltip("Quality (accepted/raw) at or above which the participant line reads 'Signal: Good'.")]
        public float goodSignalQuality = 0.8f;

        public Phase Current { get; private set; } = Phase.Skipped;

        private OnboardingSequence _onboarding;
        private SessionLogger _logger;
        private PhysiologicalStateProvider _provider;
        private float _enteredAt, _stabilisationStartedAt, _nextPoll;
        private string _lastStatus = "";
        private bool _streamReadyLogged;

        /// <summary>Runs the screen (or skips it) and starts the onboarding when the participant continues.</summary>
        public static SensorSetupScreen Launch(MonoBehaviour host, OnboardingSequence onboarding)
        {
            var screen = host.gameObject.GetComponent<SensorSetupScreen>();
            if (screen == null) screen = host.gameObject.AddComponent<SensorSetupScreen>();
            screen.Begin(onboarding);
            return screen;
        }

        private void Begin(OnboardingSequence onboarding)
        {
            _onboarding = onboarding;
            _logger = FindAnyObjectByType<SessionLogger>(FindObjectsInactive.Include);
            _provider = FindAnyObjectByType<PhysiologicalStateProvider>(FindObjectsInactive.Include);
            _enteredAt = Time.realtimeSinceStartup;

            var cfg = Resources.Load<PhysiologyConfig>(PhysiologyBootstrap.ConfigResourcePath);
            bool physiologyConfigured = cfg != null && cfg.enabled && cfg.source != PhysiologyConfig.SourceKind.None;
            if (!physiologyConfigured || _provider == null || _onboarding == null)
            {
                Current = Phase.Skipped;
                Log("sensor_setup_skipped", physiologyConfigured ? "no provider" : "physiology disabled in config");
                if (_onboarding != null) _onboarding.Begin();
                enabled = false;
                return;
            }

            _onboarding.ExternalOwner = true;
            if (_onboarding.AdvanceButton != null)
            {
                _onboarding.AdvanceButton.onClick.AddListener(OnContinue);
                _onboarding.AdvanceButton.gameObject.SetActive(false);
            }
            Current = Phase.Connecting;
            Log("sensor_setup_enter", "connecting");
            RenderConnecting(force: true);
            enabled = true;
        }

        private void Update()
        {
            if (Current == Phase.Skipped || Current == Phase.Done) return;
            if (_provider == null) _provider = FindAnyObjectByType<PhysiologicalStateProvider>(FindObjectsInactive.Include);
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.25f;

            switch (Current)
            {
                case Phase.Connecting: TickConnecting(); break;
                case Phase.Stabilising: TickStabilising(); break;
                case Phase.Waiting: TickWaiting(); break;
            }
        }

        // ---------------------------------------------------------------- A: connecting

        private bool StreamReady()
        {
            if (_provider == null) return false;
            PhysioSummary s = _provider.LatestSummary;
            return _provider.SourceStreaming && s != null && s.NAccepted >= 1 && s.LastDeviceHrBpm.HasValue && s.LastDeviceHrBpm.Value > 0;
        }

        private void TickConnecting()
        {
            RenderConnecting(force: false);
            if (!StreamReady()) return;
            if (!_streamReadyLogged) { _streamReadyLogged = true; Log("sensor_stream_ready", _provider.ConnectionState); }
            Current = Phase.Stabilising;
            _stabilisationStartedAt = Time.realtimeSinceStartup;
            Log("sensor_stabilization_start", $"seconds={stabilisationSeconds:F0}");
            RenderStabilising();
        }

        private string StatusLine()
        {
            string st = _provider != null ? _provider.ConnectionState ?? "" : "";
            switch (st)
            {
                case "": case "none": case "idle": case "initialised": case "scanning": case "scan_timeout": case "reconnect_scheduled":
                    return "Searching for sensor…";
                case "permission_requested": case "permission_missing": case "permission_denied":
                    return "Waiting for Bluetooth permission…";
                case "found": return "Sensor found…";
                case "connecting": return "Connecting…";
                case "connected": case "services_discovered": case "mtu_requested": case "mtu": case "cp_notify_requested": case "cp_notifications":
                case "data_notify_requested": case "data_notifications": case "notifications_enabled": case "stream_requested": case "cp_write":
                case "stream_already_in_state": case "stream_stop_ack":
                    return "Starting physiological stream…";
                case "stream_started": case "data_first": case "data_packet": case "rebind": case "config":
                    return "Waiting for a valid heart-rate reading…";
                case "disconnected": case "error": case "stream_error": case "stream_stopped": case "gave_up":
                    return "Sensor not connected. Retrying…";
                default: return "Waiting for a valid heart-rate reading…";
            }
        }

        private void RenderConnecting(bool force)
        {
            string status = StatusLine();
            if (!force && status == _lastStatus) return;
            _lastStatus = status;
            _onboarding.ShowExternal("PREPARING SENSOR", "Preparing sensor",
                "Connecting to the Polar Verity Sense. Please keep the sensor on your arm and remain still.\n\n" + status,
                null, false);
        }

        // ---------------------------------------------------------------- B: stabilisation

        private void TickStabilising()
        {
            float remaining = stabilisationSeconds - (Time.realtimeSinceStartup - _stabilisationStartedAt);
            if (remaining > 0f) { RenderStabilising(); return; }
            Log("sensor_stabilization_complete", SnapshotValid() ? "snapshot_valid" : "snapshot_unavailable");
            if (SnapshotValid()) EnterReady();
            else { Current = Phase.Waiting; Log("sensor_setup_waiting", _provider.ConnectionState); RenderWaiting(); }
        }

        private void RenderStabilising()
        {
            float remaining = Mathf.Max(0f, stabilisationSeconds - (Time.realtimeSinceStartup - _stabilisationStartedAt));
            int sec = Mathf.CeilToInt(remaining);
            _onboarding.ShowExternal("SENSOR CONNECTED", "Sensor connected",
                "Please remain still while the system establishes an initial physiological reading.\n\n" +
                $"Ready in {sec / 60:00}:{sec % 60:00}\n" + HeartRateLine() + "\n" + SignalLine(),
                null, false);
        }

        private string HeartRateLine()
        {
            PhysioSummary s = _provider != null ? _provider.LatestSummary : null;
            return s != null && s.LastDeviceHrBpm.HasValue && s.LastDeviceHrBpm.Value > 0 ? $"Heart rate: {s.LastDeviceHrBpm.Value} bpm" : "Heart rate: --";
        }

        private string SignalLine()
        {
            PhysioSummary s = _provider != null ? _provider.LatestSummary : null;
            if (s == null || !s.Quality.HasValue || s.NRaw < 3) return "Signal: Checking…";
            return s.Quality.Value >= goodSignalQuality ? "Signal: Good" : "Signal: Weak - please stay still";
        }

        // ---------------------------------------------------------------- C: ready / waiting

        private bool SnapshotValid() { return _provider != null && _provider.Snapshot() != null; }

        private void TickWaiting()
        {
            if (SnapshotValid()) { EnterReady(); return; }
            RenderWaiting();
        }

        private void RenderWaiting()
        {
            _onboarding.ShowExternal("SENSOR", "Waiting for a stable sensor signal",
                "Waiting for a stable sensor signal. Please remain still.\n\n" + HeartRateLine() + "\n" + SignalLine(), null, false);
        }

        private void EnterReady()
        {
            Current = Phase.Ready;
            Log("sensor_setup_ready", _provider.ConnectionState);
            _onboarding.ShowExternal("SENSOR READY", "Sensor ready", "Sensor setup is complete.\n\n" + HeartRateLine(), "Continue", true);
        }

        private void OnContinue()
        {
            if (Current != Phase.Ready) return;          // the button is hidden otherwise; belt and braces
            Current = Phase.Done;
            Log("sensor_setup_continue", _provider != null ? _provider.ConnectionState : "");
            if (_onboarding.AdvanceButton != null) _onboarding.AdvanceButton.onClick.RemoveListener(OnContinue);
            _onboarding.ExternalOwner = false;
            enabled = false;
            _onboarding.Begin();                            // placement -> welcome -> ... -> Begin Assembly, unchanged
        }

        private void OnDestroy()
        {
            if (_onboarding != null && _onboarding.AdvanceButton != null) _onboarding.AdvanceButton.onClick.RemoveListener(OnContinue);
        }

        // ---------------------------------------------------------------- logging

        private void Log(string eventName, string detail)
        {
            float elapsed = (Time.realtimeSinceStartup - _enteredAt) * 1000f;
            PhysioSummary s = _provider != null ? _provider.LatestSummary : null;
            Debug.Log($"[SensorSetup] {eventName} phase={Current} elapsed={elapsed:F0}ms {detail} nAccepted={(s != null ? s.NAccepted : 0)} hr={(s != null && s.LastDeviceHrBpm.HasValue ? s.LastDeviceHrBpm.Value : 0)}");
            if (_logger != null) _logger.LogSensorSetup(eventName, Current.ToString(), detail, elapsed, s, SnapshotValid(), _provider != null ? _provider.ConnectionState : null);
        }
    }
}
