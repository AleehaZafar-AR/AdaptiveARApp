// File: DecisionScheduler.cs
// WHEN a decision is requested in the live session. The policy lives in RequestGate (plain
// C#, headlessly tested): evidence accrues between calls, provider-call protection and
// support-change hysteresis are separate, and deferred requests are re-evaluated here in
// Update. The scheduler owns the provider (reference policy or model) and the API key, and
// hands every request to the session's existing seam (BuildDecisionContext -> provider ->
// ApplyDecision), which enforces the constrained action set and logs the decision. The
// scheduler never touches the support level and never interprets the operator.
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
        private RequestGate _gate;
        private int _boundStepIndex = -1;
        private SupportLevel _levelAtRequest;
        private float _nextPeriodicAt = 1e9f;
        private float _nextDeferredCheckAt;
        private string _lastHoldReason;
        private string _keyFingerprint = "none";

        /// <summary>The gate in force (diagnostics / tests).</summary>
        public RequestGate Gate { get { return _gate; } }

        private static float Now { get { return Time.realtimeSinceStartup; } }

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
            _gate = new RequestGate(Config != null ? Config.Schedule : new DecisionScheduleConfig());
            _boundStepIndex = _session != null ? _session.CurrentStepIndex : -1;
            _nextPeriodicAt = Config != null && Config.Schedule.PeriodicSeconds > 0 ? Time.realtimeSinceStartup + (float)Config.Schedule.PeriodicSeconds : 1e9f;
            if (_logger != null) _logger.LogDecisionConfig(Config != null ? Config.Describe() : "none", Status, ProviderId);
            Debug.Log($"[Decision] bound: session={(_session != null)} logger={(_logger != null)} status={Status} provider={ProviderId ?? "none"}");
        }

        private void HandleActionEntered(AssemblyAction action)
        {
            if (Config == null || !Config.Schedule.OnActionEnter) return;
            Evaluate("action_enter:" + (action != null ? action.Id : "?"));
        }

        private void HandleCountedError()
        {
            if (Config == null || !Config.Schedule.OnCountedError || _gate == null) return;
            _gate.NoteError(Now);
            Evaluate("counted_error");
        }

        /// <summary>A wrong component in the hand is behavioural evidence; it accrues like a counted error.</summary>
        private void HandleWrongComponent(string partKey)
        {
            if (Config == null || !Config.Schedule.OnWrongComponent || _gate == null) return;
            _gate.NoteError(Now);
            Evaluate("wrong_component_grabbed:" + (partKey ?? "?"));
        }

        /// <summary>A validated success is evidence too; the gate decides whether it is worth a call now.</summary>
        private void HandleActionCompleted(AssemblyAction action)
        {
            if (Config == null || !Config.Schedule.OnActionComplete || _gate == null) return;
            _gate.NoteSuccess(Now);
            Evaluate("action_complete:" + (action != null ? action.Id : "?"));
        }

        private void Update()
        {
            if (Config == null || _gate == null || _session == null) return;

            // A new step: its events belong to the new ledger (the hysteresis of an applied change is kept).
            int idx = _session.CurrentStepIndex;
            if (idx != _boundStepIndex) { _boundStepIndex = idx; _gate.ResetForStep(); _lastHoldReason = null; }

            if (Config.Schedule.PeriodicSeconds > 0 && Now >= _nextPeriodicAt)
            {
                _nextPeriodicAt = Now + (float)Config.Schedule.PeriodicSeconds;
                Evaluate("periodic");
                return;
            }

            // Deferred requests and lone-event flushes are re-tried a few times a second.
            if (Now < _nextDeferredCheckAt) return;
            _nextDeferredCheckAt = Now + 0.25f;
            if (_gate.HasDeferred) Evaluate(_gate.Deferred, quiet: true);
            else if (_gate.NeedsFlush(Now)) Evaluate("pending_flush");
        }

        // ------------------------------------------------------------------ requests

        /// <summary>Researcher control: asks now regardless of accrued evidence (the structural protections still apply).</summary>
        public string TryRequest(string trigger, bool ignoreStepMinimum)
        {
            return Evaluate(trigger, force: ignoreStepMinimum);
        }

        /// <summary>Runs the gate for a trigger; fires one request through the session seam when it says so. Returns why not otherwise.</summary>
        private string Evaluate(string trigger, bool force = false, bool quiet = false)
        {
            if (_session == null) return Skip(trigger, "no session", quiet);
            if (_syncProvider == null && _asyncProvider == null) return Skip(trigger, "provider off (" + Status + ")", quiet);
            if (!_session.AssemblyRunning) return Skip(trigger, "assembly not running", quiet);
            if (_gate == null) _gate = new RequestGate(Config != null ? Config.Schedule : new DecisionScheduleConfig());

            RequestGate.Result r = _gate.Evaluate(trigger, Now, _session.StepElapsedMs, _session.CurrentSupportLevel, force);
            if (r.Verdict == RequestGate.Verdict.Skipped) return Skip(trigger, r.Reason, quiet);
            if (r.Verdict == RequestGate.Verdict.Deferred)
            {
                // One line per distinct hold, not one per frame.
                if (_lastHoldReason != r.Reason) { _lastHoldReason = r.Reason; Debug.Log($"[Decision] deferred trigger={trigger}: {r.Reason}"); }
                return r.Reason;
            }
            _lastHoldReason = null;

            Requests++;
            _levelAtRequest = _session.CurrentSupportLevel;
            if (_logger != null) _logger.LogDecisionTrigger(r.Trigger, ProviderId, Requests, r.PendingEvents, r.PendingErrors, r.PendingSuccesses, r.DeferredFrom);
            Debug.Log($"[Decision] request #{Requests} trigger={r.Trigger} provider={ProviderId}" + (r.DeferredFrom != null ? $" (deferred from {r.DeferredFrom})" : ""));

            if (_asyncProvider != null)
            {
                _gate.InFlight = true;
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
            bool changed = level != _levelAtRequest;
            if (_gate != null) _gate.OnApplied(changed, Now);
            LastResponse = response;
            if (changed) Debug.Log($"[Decision] level changed {_levelAtRequest} -> {level}: change cooldown {(Config != null ? Config.Schedule.ChangeCooldownMs : 0):F0} ms (requests deferred, not dropped).");
        }

        private string Skip(string trigger, string why, bool quiet)
        {
            if (!quiet) Debug.Log($"[Decision] skip trigger={trigger}: {why}");
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
