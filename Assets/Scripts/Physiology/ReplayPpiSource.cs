// File: ReplayPpiSource.cs
// A bench / offline source that feeds the SAME processing layer as the Polar receiver:
// either a synthetic interval series or a recorded session's ppi_sample lines replayed at
// their real pace. Lets the physiology path run in the Editor and on a headset without a
// sensor (CLAUDE.md sections 3 and 7.4).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AdaptiveAR.Physiology
{
    public class ReplayPpiSource : MonoBehaviour, IPhysiologicalSource
    {
        public enum Mode { Synthetic = 0, JsonlFile = 1 }

        public Mode mode = Mode.Synthetic;
        [Tooltip("JSONL file with ppi_sample lines, relative to Application.persistentDataPath (JsonlFile mode).")]
        public string jsonlRelativePath = "";

        [Header("Synthetic series")]
        public float meanIbiMs = 850f;
        [Tooltip("Peak-to-peak sinusoidal modulation of the interval, ms (respiratory-like).")]
        public float modulationMs = 60f;
        public float modulationPeriodS = 5f;
        [Tooltip("Uniform noise added to each interval, +/- ms.")]
        public float noiseMs = 10f;
        [Tooltip("Probability a sample is flagged blocker=1 (movement).")]
        [Range(0f, 1f)] public float blockerProbability = 0.05f;
        public int seed = 1;

        public string SourceId { get; private set; } = "replay/synthetic";
        public bool IsStreaming { get; private set; }
        public event Action<PpiSample> SampleReceived;
        public event Action<string, string> StateChanged;

        private Func<double> _sessionClockMs = () => -1;
        private System.Random _rng;
        private double _nextAt;
        private long _seq;
        private readonly List<PpiSample> _file = new List<PpiSample>();
        private int _fileIndex;
        private double _t;

        public void SetSessionClock(Func<double> sessionMs) { _sessionClockMs = sessionMs ?? (() => -1); }

        private void OnEnable()
        {
            _rng = new System.Random(seed);
            _nextAt = Time.realtimeSinceStartup + 1f;
            if (mode == Mode.JsonlFile) LoadFile();
            else SourceId = "replay/synthetic";
            IsStreaming = true;
            Raise("stream_started", mode == Mode.JsonlFile ? $"file samples={_file.Count}" : "synthetic");
        }

        private void OnDisable()
        {
            IsStreaming = false;
            Raise("stream_stopped", "replay disabled");
        }

        private void LoadFile()
        {
            _file.Clear(); _fileIndex = 0;
            string path = Path.Combine(Application.persistentDataPath, jsonlRelativePath ?? "");
            SourceId = "replay/" + Path.GetFileName(path);
            if (!File.Exists(path)) { Raise("error", "replay file not found: " + path); return; }
            foreach (string line in File.ReadLines(path))
            {
                if (!line.Contains("\"event\":\"ppi_sample\"")) continue;
                var s = new PpiSample
                {
                    PpiMs = Int(line, "ppi_ms"), DeviceHrBpm = Int(line, "hr_bpm_device"), ErrorEstimateMs = Int(line, "error_estimate_ms"),
                    Blocker = Bool(line, "blocker"), SkinContact = Bool(line, "skin_contact"), SkinContactSupported = Bool(line, "skin_contact_supported"),
                    PacketSeq = Int(line, "packet_seq"), SampleIndex = Int(line, "sample_index"), SamplesInPacket = Int(line, "samples_in_packet"),
                    PacketLength = Int(line, "packet_len"), Source = SourceId
                };
                _file.Add(s);
            }
        }

        private static int Int(string line, string key)
        {
            var m = Regex.Match(line, "\"" + key + "\":(-?[0-9]+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }
        private static bool Bool(string line, string key) { return Regex.IsMatch(line, "\"" + key + "\":true"); }

        private void Update()
        {
            if (!IsStreaming) return;
            double now = Time.realtimeSinceStartup;
            if (now < _nextAt) return;

            PpiSample s;
            if (mode == Mode.JsonlFile)
            {
                if (_file.Count == 0) { IsStreaming = false; return; }
                PpiSample src = _file[_fileIndex]; _fileIndex = (_fileIndex + 1) % _file.Count;
                s = new PpiSample
                {
                    PpiMs = src.PpiMs, DeviceHrBpm = src.DeviceHrBpm, ErrorEstimateMs = src.ErrorEstimateMs, Blocker = src.Blocker,
                    SkinContact = src.SkinContact, SkinContactSupported = src.SkinContactSupported, PacketSeq = ++_seq, SampleIndex = 0,
                    SamplesInPacket = 1, PacketLength = 16, Source = SourceId
                };
            }
            else
            {
                _t += 1.0;
                double ibi = meanIbiMs + modulationMs * 0.5 * Math.Sin(2 * Math.PI * (now / Math.Max(0.1f, modulationPeriodS))) + (_rng.NextDouble() * 2 - 1) * noiseMs;
                s = new PpiSample
                {
                    PpiMs = (int)Math.Round(ibi), DeviceHrBpm = (int)Math.Round(60000.0 / Math.Max(1, ibi)), ErrorEstimateMs = (int)(_rng.NextDouble() * 20),
                    Blocker = _rng.NextDouble() < blockerProbability, SkinContact = true, SkinContactSupported = true,
                    PacketSeq = ++_seq, SampleIndex = 0, SamplesInPacket = 1, PacketLength = 16, Source = SourceId
                };
            }
            s.ReceiptRealtimeS = now;
            s.ReceiptSessionMs = _sessionClockMs();
            _nextAt = now + Math.Max(0.2, s.PpiMs / 1000.0);
            try { SampleReceived?.Invoke(s); } catch (Exception e) { Debug.LogError("[Physio] replay handler threw: " + e); }
        }

        private void Raise(string state, string detail)
        {
            Debug.Log($"[PhysioReplay] {state} | {detail}");
            try { StateChanged?.Invoke(state, detail); } catch (Exception e) { Debug.LogError("[Physio] replay StateChanged threw: " + e); }
        }
    }
}
