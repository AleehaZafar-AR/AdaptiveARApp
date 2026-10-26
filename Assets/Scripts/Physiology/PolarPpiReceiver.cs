// File: PolarPpiReceiver.cs
// The one Unity-side endpoint of BleBridge.java. The GameObject carrying it MUST be named
// "PolarPpiReceiver": that name is the UnitySendMessage target. It stamps every sample with
// receipt time, parses the raw fields, and raises events. No adaptation logic lives here.
//
// Persistence: survives scene reloads (Restart) so the GATT link is not torn down; a singleton
// guard destroys any later duplicate before it can open a second connection. On every scene
// load it raises Rebound so the processing layer re-finds the new scene's logger / HUD.

using System;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace AdaptiveAR.Physiology
{
    public class PolarPpiReceiver : MonoBehaviour, IPhysiologicalSource
    {
        public const string RequiredGameObjectName = "PolarPpiReceiver";
        public static PolarPpiReceiver Instance { get; private set; }

        [Tooltip("Substring of the advertised device name to accept, e.g. 'Polar'.")]
        public string deviceNameFilter = "Polar";
        [Tooltip("ATT MTU to request after service discovery (Polar SDK uses 512).")]
        public int requestedMtu = 512;
        [Tooltip("Reconnect / rescan attempts before giving up.")]
        public int maxReconnectAttempts = 20;
        [Tooltip("Start scanning automatically after permissions are granted.")]
        public bool autoStart = true;

        public string SourceId { get; private set; } = "PolarVeritySense";
        public bool IsStreaming { get; private set; }
        public string State { get; private set; } = "idle";
        public string StateDetail { get; private set; } = "";
        public string DeviceName { get; private set; } = "";
        public int GrantedMtu { get; private set; }
        public long PacketsReceived { get; private set; }
        public long SamplesReceived { get; private set; }
        public long MalformedPackets { get; private set; }

        public event Action<PpiSample> SampleReceived;
        public event Action<string, string> StateChanged;
        /// <summary>(packetSeq, length, samplesInPacket) for every PMD data notification.</summary>
        public event Action<long, int, int> PacketReceived;
        /// <summary>Raised after a scene load so bound components re-find scene objects.</summary>
        public event Action Rebound;

        private AndroidJavaObject _bridge;
        private Func<double> _sessionClockMs = () => -1;

        /// <summary>The processing layer supplies the session clock (ms) used for receipt stamps.</summary>
        public void SetSessionClock(Func<double> sessionMs) { _sessionClockMs = sessionMs ?? (() => -1); }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.Log($"[Physio] duplicate PolarPpiReceiver '{name}' destroyed: the persistent one (id {Instance.GetInstanceID()}) keeps the connection.");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (name != RequiredGameObjectName)
            {
                Debug.LogWarning($"[Physio] receiver object renamed '{name}' -> '{RequiredGameObjectName}' (UnitySendMessage target).");
                name = RequiredGameObjectName;
            }
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            if (Instance == this) { Instance = null; SceneManager.sceneLoaded -= OnSceneLoaded; }
        }

        private void Start()
        {
            if (Instance != this) return;
            if (autoStart) Begin();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"[Physio] scene '{scene.name}' loaded: receiver id {GetInstanceID()} persists (state={State}, streaming={IsStreaming}); rebinding processing layer.");
            try { Rebound?.Invoke(); } catch (Exception e) { Debug.LogError("[Physio] Rebound handler threw: " + e); }
        }

        /// <summary>Requests the Android 12+ Bluetooth permissions, then initialises the bridge and scans.</summary>
        public void Begin()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string Scan = "android.permission.BLUETOOTH_SCAN";
            const string Connect = "android.permission.BLUETOOTH_CONNECT";
            if (Permission.HasUserAuthorizedPermission(Scan) && Permission.HasUserAuthorizedPermission(Connect))
            {
                Report("permission_granted", "already");
                InitAndScan();
                return;
            }
            Report("permission_requested", Scan + "," + Connect);
            var cb = new PermissionCallbacks();
            cb.PermissionGranted += p =>
            {
                Report("permission_granted", p);
                if (Permission.HasUserAuthorizedPermission(Scan) && Permission.HasUserAuthorizedPermission(Connect)) InitAndScan();
            };
            cb.PermissionDenied += p => Report("permission_denied", p);
            cb.PermissionDeniedAndDontAskAgain += p => Report("permission_denied", p + " (dont_ask_again)");
            Permission.RequestUserPermissions(new[] { Scan, Connect }, cb);
#else
            Report("unavailable", "not an Android player: no native BLE (use ReplayPpiSource for bench runs)");
#endif
        }

        private void InitAndScan()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (_bridge == null)
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    {
                        var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                        _bridge = new AndroidJavaObject("com.adaptivear.ble.BleBridge");
                        _bridge.Call("init", activity, RequiredGameObjectName, deviceNameFilter, requestedMtu, maxReconnectAttempts);
                    }
                }
                _bridge.Call("startScan");
            }
            catch (Exception e)
            {
                Report("error", "bridge: " + e.Message);
            }
#endif
        }

        /// <summary>Stops the stream and closes the connection (quit).</summary>
        public void Stop()
        {
            IsStreaming = false;
            if (_bridge != null) { try { _bridge.Call("stop"); } catch (Exception e) { Debug.LogWarning("[Physio] stop threw: " + e.Message); } }
        }

        private void OnApplicationQuit() { Stop(); }

        // ------------------------------------------------------------------ called by BleBridge.java

        /// <summary>"state|detail|deviceName"</summary>
        public void OnBleState(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            string[] p = message.Split(new[] { '|' }, 3);
            string state = p[0];
            string detail = p.Length > 1 ? p[1] : "";
            if (p.Length > 2 && !string.IsNullOrEmpty(p[2])) { DeviceName = p[2]; SourceId = "PolarVeritySense/" + DeviceName; }
            if (state == "mtu")
            {
                int i = detail.IndexOf("granted=", StringComparison.Ordinal);
                if (i >= 0) { int j = detail.IndexOf(' ', i); int.TryParse(detail.Substring(i + 8, (j > 0 ? j : detail.Length) - i - 8), out int mtu); GrantedMtu = mtu; }
            }
            if (state == "stream_started") IsStreaming = true;
            if (state == "stream_stopped" || state == "stream_stop_ack" || state == "disconnected" || state == "stopped" || state == "gave_up") IsStreaming = false;
            if (state == "malformed_packet") MalformedPackets++;
            Report(state, detail);
        }

        /// <summary>"P|seq|len|type|frameType|n"</summary>
        public void OnBlePacket(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            string[] p = message.Split('|');
            if (p.Length < 6) return;
            PacketsReceived++;
            long.TryParse(p[1], out long seq); int.TryParse(p[2], out int len); int.TryParse(p[5], out int n);
            try { PacketReceived?.Invoke(seq, len, n); } catch (Exception e) { Debug.LogError("[Physio] PacketReceived handler threw: " + e); }
        }

        /// <summary>"S|seq|idx|n|len|tsNs|hr|ppi|err|flags" - receipt time is taken here, first thing.</summary>
        public void OnPpi(string message)
        {
            double realtime = Time.realtimeSinceStartup;
            double session = _sessionClockMs();
            PpiSample s = PpiSample.Parse(message, session, realtime, SourceId);
            if (s == null)
            {
                MalformedPackets++;
                Report("malformed_sample", message);
                return;
            }
            SamplesReceived++;
            try { SampleReceived?.Invoke(s); } catch (Exception e) { Debug.LogError("[Physio] SampleReceived handler threw: " + e); }
        }

        private void Report(string state, string detail)
        {
            State = state; StateDetail = detail;
            Debug.Log($"[PolarBLE] {state} | {detail}{(string.IsNullOrEmpty(DeviceName) ? "" : " | device=" + DeviceName)}");
            try { StateChanged?.Invoke(state, detail); } catch (Exception e) { Debug.LogError("[Physio] StateChanged handler threw: " + e); }
        }
    }
}
