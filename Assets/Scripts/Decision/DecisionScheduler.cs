// File: DecisionScheduler.cs
// WHEN a decision is requested in the live session, under a config-driven policy: on action
// entry, after a counted error, periodically, with a minimum interval and a minimum time on
// the step. It owns the provider (reference policy or model) and the API key, and hands every
// request to the session's existing seam (BuildDecisionContext -> provider -> ApplyDecision),
// which enforces the constrained action set and logs the decision. The scheduler never
// touches the support level itself.
//
// Config: persistentDataPath/AdaptiveAR/config/decision_config.json overrides
// Resources/Decision/decision_config.json. API key: persistentDataPath/AdaptiveAR/config/
// ai_provider.local.json, else Resources/Decision/ai_provider.local (untracked). Without a
// key the model provider is off and the ResearcherHud says so; nothing silently substitutes.

using System;
using System.IO;
using AdaptiveAR.Logging;
using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveAR.Decision
{
    public class DecisionScheduler : MonoBehaviour
    {
        public const string ConfigResource = "Decision/decision_config";
        public const string KeyResource = "Decision/ai_provider.local";
        public const string ConfigFolder = "AdaptiveAR/config";

        public static DecisionScheduler Instance { get; private set; }

        public DecisionConfig Config { get; private set; }
        public string Status { get; private set; } = "off";
        public int Requests { get; private set; }
        public DecisionResponse LastResponse { get; private set; }

        private IDecisionProvider _syncProvider;
        private IAsyncDecisionProvider _asyncProvider;
        private AssemblySessionController _session;
        private SessionLogger _logger;
        private bool _inFlight;
        private float _lastRequestAt = -1e9f;
        private float _nextPeriodicAt = 1e9f;
        private string _keyFingerprint = "none";

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += (s, m) => Bind();
            LoadConfigAndProvider();
        }

        private void Start() { Bind(); }

        private void OnDestroy() { if (Instance == this) Instance = null; }

        // ------------------------------------------------------------------ configuration

        private void LoadConfigAndProvider()
        {
            string overridePath = Path.Combine(Application.persistentDataPath, ConfigFolder, "decision_config.json");
            try
            {
                if (File.Exists(overridePath)) Config = DecisionConfig.FromJson(File.ReadAllText(overridePath), "file:" + overridePath);
                else
                {
                    var ta = Resources.Load<TextAsset>(ConfigResource);
                    Config = ta != null ? DecisionConfig.FromJson(ta.text, "resource:" + ConfigResource) : new DecisionConfig { SourceDescription = "built-in defaults" };
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Decision] config unreadable, decisions OFF: " + e.Message);
                Config = new DecisionConfig { Enabled = false, Provider = "none", SourceDescription = "unreadable" };
            }

            _syncProvider = null; _asyncProvider = null;
            if (!Config.Enabled || Config.Provider == "none") { Status = "off"; }
            else if (Config.Provider == "reference") { _syncProvider = new ReferencePolicyProvider(Config.ReferencePolicy); Status = "reference"; }
            else if (Config.Provider == "llm")
            {
                string key = LoadApiKey();
                _keyFingerprint = SecretStore.Fingerprint(key);
                if (string.IsNullOrEmpty(key)) { Status = "no key"; Debug.LogError("[Decision] provider=llm but no API key found (persistentDataPath/" + ConfigFolder + "/ai_provider.local.json or Resources/" + KeyResource + "). Decisions OFF."); }
                else { _asyncProvider = new LlmDecisionProvider(Config.Llm, key); Status = "llm"; }
            }
            else { Status = "unknown provider '" + Config.Provider + "'"; Debug.LogError("[Decision] " + Status); }

            Debug.Log($"[Decision] config {Config.Describe()} -> status={Status} key={_keyFingerprint}");
        }

        private string LoadApiKey()
        {
            string p = Path.Combine(Application.persistentDataPath, ConfigFolder, "ai_provider.local.json");
            try { if (File.Exists(p)) { string k = SecretStore.ExtractApiKey(File.ReadAllText(p)); if (!string.IsNullOrEmpty(k)) return k; } }
            catch (Exception e) { Debug.LogWarning("[Decision] key file unreadable: " + e.Message); }
            var ta = Resources.Load<TextAsset>(KeyResource);
            return ta != null ? SecretStore.ExtractApiKey(ta.text) : null;
        }

        /// <summary>The provider in force, for diagnostics and the evaluation; null when decisions are off.</summary>
        public string ProviderId { get { return _asyncProvider != null ? _asyncProvider.ProviderId : (_syncProvider != null ? _syncProvider.ProviderId : null); } }

        // ------------------------------------------------------------------ session binding

        private void Bind()
        {
            if (_session != null) { _session.OnActionEntered -= HandleActionEntered; _session.OnCountedError -= HandleCountedError; _session.OnWrongComponentGrabbed -= HandleWrongComponent; _session.OnActionCompleted -= HandleActionCompleted; }
            _session = FindAnyObjectByType<AssemblySessionController>(FindObjectsInactive.Include);
            _logger = FindAnyObjectByType<SessionLogger>(FindObjectsInactive.Include);
            if (_session != null) { _session.OnActionEntered += HandleActionEntered; _session.OnCountedError += HandleCountedError; _session.OnWrongComponentGrabbed += HandleWrongComponent; _session.OnActionCompleted += HandleActionCompleted; }
            if (_session != null && Config != null) _session.ConfigureRecentContext((float)Config.Context.RecentWindowMs, Config.Context.RecentEventsMax);
            _inFlight = false;
            _nextPeriodicAt = Config != null && Config.Schedule.PeriodicSeconds > 0 ? Time.realtimeSinceStartup + (float)Config.Schedule.PeriodicSeconds : 1e9f;
            if (_logger != null) _logger.LogDecisionConfig(Config != null ? Config.Describe() : "none", Status, ProviderId);
            Debug.Log($"[Decision] bound: session={(_session != null)} logger={(_logger != null)} status={Status} provider={ProviderId ?? "none"}");
        }

        private void HandleActionEntered(AssemblyAction action)
        {
            if (Config != null && Config.Schedule.OnActionEnter) TryRequest("action_enter:" + (action != null ? action.Id : "?"), ignoreStepMinimum: false);
        }

        private void HandleCountedError()
        {
            if (Config != null && Config.Schedule.OnCountedError) TryRequest("counted_error", ignoreStepMinimum: true);
        }

        /// <summary>
        /// Wrong-component selection is behavioural evidence of needing guidance. It goes
        /// through the same gate as every trigger: one request in flight, the minimum
        /// interval, and the minimum time on the step, so repeated wrong grabs can never
        /// spam the provider. The context carries the cumulative error/attempt counts.
        /// </summary>
        /// <summary>Success is evidence too (recovery). Same gate: never a request per success without throttling.</summary>
        private void HandleActionCompleted(AssemblyAction action)
        {
            if (Config != null && Config.Schedule.OnActionComplete) TryRequest("action_complete:" + (action != null ? action.Id : "?"), ignoreStepMinimum: false);
        }

        private void HandleWrongComponent(string partKey)
        {
            if (Config != null && Config.Schedule.OnWrongComponent) TryRequest("wrong_component_grabbed:" + (partKey ?? "?"), ignoreStepMinimum: false);
        }

        private void Update()
        {
            if (Config == null || Config.Schedule.PeriodicSeconds <= 0) return;
            if (Time.realtimeSinceStartup < _nextPeriodicAt) return;
            _nextPeriodicAt = Time.realtimeSinceStartup + (float)Config.Schedule.PeriodicSeconds;
            TryRequest("periodic", ignoreStepMinimum: false);
        }

        // ------------------------------------------------------------------ requests

        /// <summary>Requests one decision through the session seam if the policy allows it now. Returns why not otherwise.</summary>
        public string TryRequest(string trigger, bool ignoreStepMinimum)
        {
            if (_session == null) return Skip(trigger, "no session");
            if (_syncProvider == null && _asyncProvider == null) return Skip(trigger, "provider off (" + Status + ")");
            if (!_session.AssemblyRunning) return Skip(trigger, "assembly not running");
            if (_inFlight) return Skip(trigger, "request in flight");
            float since = Time.realtimeSinceStartup - _lastRequestAt;
            if (since < Config.Schedule.MinIntervalSeconds) return Skip(trigger, $"min interval ({since:F1}s < {Config.Schedule.MinIntervalSeconds}s)");
            if (!ignoreStepMinimum && _session.StepElapsedMs < Config.Schedule.FirstDecisionAfterStepMs) return Skip(trigger, $"step too young ({_session.StepElapsedMs:F0}ms < {Config.Schedule.FirstDecisionAfterStepMs}ms)");

            _lastRequestAt = Time.realtimeSinceStartup;
            Requests++;
            if (_logger != null) _logger.LogDecisionTrigger(trigger, ProviderId, Requests);
            Debug.Log($"[Decision] request #{Requests} trigger={trigger} provider={ProviderId}");

            if (_asyncProvider != null)
            {
                _inFlight = true;
                StartCoroutine(_session.RequestAndApplyDecisionRoutine(_asyncProvider, OnApplied));
            }
            else
            {
                _session.RequestAndApplyDecision(_syncProvider);
                OnApplied(_session.LastDecisionResponse, _session.CurrentSupportLevel);
            }
            return null;
        }

        private void OnApplied(DecisionResponse response, SupportLevel level)
        {
            _inFlight = false;
            LastResponse = response;
        }

        private string Skip(string trigger, string why)
        {
            Debug.Log($"[Decision] skip trigger={trigger}: {why}");
            return why;
        }

        /// <summary>Researcher-panel name of the provider in force; never derived from the key. Null when decisions are off.</summary>
        public string ProviderDisplayName
        {
            get
            {
                if (_asyncProvider != null && Config != null) return Config.Llm.DisplayName;
                if (_syncProvider != null && Config != null) return "reference policy " + Config.ReferencePolicy.Name;
                return null;
            }
        }
    }

    /// <summary>Creates the single scheduler at start-up; it binds to each loaded scene's session.</summary>
    public static class DecisionBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            if (DecisionScheduler.Instance != null) return;
            new GameObject("DecisionScheduler").AddComponent<DecisionScheduler>();
        }
    }
}
