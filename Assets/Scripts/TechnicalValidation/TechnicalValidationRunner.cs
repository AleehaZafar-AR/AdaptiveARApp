// File: TechnicalValidationRunner.cs
// Level 2: automated in-engine / Quest validation of the integrated runtime.
//
//   scripted participant event -> production StepValidator outcome / Continue control
//     -> AssemblySessionController (ledger, counters, feedback)
//     -> DecisionScheduler + RequestGate -> LlmDecisionProvider (real Anthropic call)
//     -> structural constraints (ApplyDecision) -> SupportLevelController
//     -> StepPresenter / AdaptivePanelController / audio (the real L1-L3 presentation)
//     -> SessionLogger (the normal production JSONL)
//
// The runner only (a) injects events through the production entry points at scripted
// real-time delays, (b) observes the production components through their public events
// and properties, (c) writes a validation JSONL beside the session log and a compact
// summary with the integration assertions. It never sets a support level, never calls the
// provider, never changes the scheduler, the gate, the prompt or the physiology layer.
//
// It exists only when TechnicalValidationBootstrap finds an enabled
// persistentDataPath/AdaptiveAR/config/technical_validation.json (see that file).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AdaptiveAR.Decision;
using AdaptiveAR.Logging;
using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using AdaptiveAR.UI;
using TMPro;
using UnityEngine;

namespace AdaptiveAR.TechnicalValidation
{
    public class TechnicalValidationRunner : MonoBehaviour
    {
        public const string ValidationSchema = "adaptivear.technical_validation.v1";
        /// <summary>The frozen, Level-1-evaluated runtime this layer was written against (informational).</summary>
        public const string FrozenRuntimeCommit = "0b918b1";

        public static TechnicalValidationRunner Instance { get; private set; }

        public TechnicalValidationConfig Config { get; private set; }
        public ValidationScenario Scenario { get; private set; }
        public string ScenarioLoadError { get; private set; }

        // ---- overlay-facing state (no chain-of-thought, no physiological interpretation) ----
        public string Phase { get; private set; } = "starting";
        public string CurrentEventText { get; private set; } = "-";
        public int EventIndex { get; private set; } = -1;
        public int EventCount { get { return Scenario != null ? Scenario.Events.Count : 0; } }
        public string LastAiText { get; private set; } = "-";
        public string LastTransitionText { get; private set; } = "";
        public float RunSeconds { get { return _runStartedAt > 0 ? Time.realtimeSinceStartup - _runStartedAt : 0f; } }
        public string ResultText { get; private set; } = "";
        public string CurrentStepText { get { return _session != null && _session.AssemblyRunning ? $"{_session.CurrentStepIndex + 1}/{(_stepRunner != null ? _stepRunner.StepCount : 0)} {_session.CurrentActionId ?? "-"}" : "-"; } }
        public string CurrentLevelText { get { return _level != null ? Levels.Short(_level.CurrentLevel.ToString()) : "-"; } }
        public string ProviderText
        {
            get
            {
                var s = DecisionScheduler.Instance;
                if (s == null) return "scheduler: none";
                string inflight = s.Gate != null && s.Gate.InFlight ? " (request in flight)" : "";
                return $"{(s.ProviderDisplayName ?? s.Status)} | {s.Status} | requests {s.Requests}{inflight}";
            }
        }

        private AssemblySessionController _session;
        private SessionLogger _logger;
        private SupportLevelController _level;
        private StepValidator _validator;
        private StepRunner _stepRunner;
        private WorkflowState _workflow;
        private StepPresenter _presenter;
        private AppFlowController _flow;
        private bool _bound;

        private ScenarioRunRecord _record;
        private StreamWriter _tvLog;
        private int _tvSeq;
        private string _tvLogPath;

        private int _lastRequests;
        private DecisionResponse _lastResponse;
        private readonly Queue<RequestRecord> _pendingRequests = new Queue<RequestRecord>();
        private string _levelAtLastPoll;
        private int _transitionsAtLastPoll;
        private float _runStartedAt = -1f;
        private bool _started, _finishing, _finished;
        private CompletionSequencer _seq;   // orders event commit vs. completion record (see CompletionSequencer.cs)
        private double _lastAiTransitionMs = -1;
        private int _aiTransitionsSeen;
        private bool _sensorOverrideDone;
        private Coroutine _run;

        // ------------------------------------------------------------------ lifecycle

        public void Configure(TechnicalValidationConfig config, ValidationScenario scenario, string loadError)
        {
            Config = config; Scenario = scenario; ScenarioLoadError = loadError;
            if (scenario == null) { Phase = "error"; CurrentEventText = loadError ?? "scenario missing"; }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Application.logMessageReceived += OnLog;
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
            Unbind();
            CloseTvLog();
            if (Instance == this) Instance = null;
        }

        private void Bind()
        {
            _session = FindAnyObjectByType<AssemblySessionController>(FindObjectsInactive.Include);
            _logger = FindAnyObjectByType<SessionLogger>(FindObjectsInactive.Include);
            _level = FindAnyObjectByType<SupportLevelController>(FindObjectsInactive.Include);
            _validator = FindAnyObjectByType<StepValidator>(FindObjectsInactive.Include);
            _stepRunner = FindAnyObjectByType<StepRunner>(FindObjectsInactive.Include);
            _workflow = FindAnyObjectByType<WorkflowState>(FindObjectsInactive.Include);
            _presenter = FindAnyObjectByType<StepPresenter>(FindObjectsInactive.Include);
            _flow = FindAnyObjectByType<AppFlowController>(FindObjectsInactive.Include);
            if (_session == null || _level == null || _stepRunner == null) return;
            _session.OnActionEntered += HandleActionEntered;
            _session.OnSessionComplete += HandleSessionComplete;
            _level.OnSupportLevelChanged += HandleLevelChanged;
            _stepRunner.OnStepChanged += HandleStepChanged;
            _levelAtLastPoll = _level.CurrentLevel.ToString();
            _bound = true;
            _record = new ScenarioRunRecord
            {
                ScenarioId = Scenario != null ? Scenario.Id : Config.Scenario,
                ScenarioTitle = Scenario != null ? Scenario.Title : null,
                Mode = Config.ModeName,
                Physiology = Scenario != null && !string.IsNullOrEmpty(Scenario.Physiology) ? Scenario.Physiology : Config.PhysiologyName,
                DelayScale = Config.EffectiveDelayScale,
                BuildLabel = $"{Application.version}/{Application.unityVersion}/runtime@{FrozenRuntimeCommit}",
                ConfigDescription = Config.Describe()
            };
            var sched = DecisionScheduler.Instance;
            if (sched != null && sched.Config != null)
            {
                _record.MinCallIntervalSeconds = sched.Config.Schedule.MinCallIntervalSeconds;
                _record.ChangeCooldownMs = sched.Config.Schedule.ChangeCooldownMs;
                _record.MinEventsPerRequest = sched.Config.Schedule.MinEventsPerRequest;
                _record.FirstDecisionAfterStepMs = sched.Config.Schedule.FirstDecisionAfterStepMs;
            }
            Phase = Scenario != null ? "waiting for assembly start" : "error";
            Debug.Log($"[TechnicalValidation] bound: session={(_session != null)} logger={(_logger != null)} validator={(_validator != null)} presenter={(_presenter != null)}; {Config.Describe()}; scenario: {(Scenario != null ? Scenario.Summary() : ScenarioLoadError)}");
        }

        private void Unbind()
        {
            if (!_bound) return;
            if (_session != null) { _session.OnActionEntered -= HandleActionEntered; _session.OnSessionComplete -= HandleSessionComplete; }
            if (_level != null) _level.OnSupportLevelChanged -= HandleLevelChanged;
            if (_stepRunner != null) _stepRunner.OnStepChanged -= HandleStepChanged;
            _bound = false;
        }

        private void Update()
        {
            if (!_bound) { Bind(); if (!_bound) return; }
            if (Scenario == null) return;

            ApplySensorOverrideOnce();
            PollDecisionLayer();

            if (!_started && _session.AssemblyRunning)
            {
                _started = true;
                _run = StartCoroutine(Run());
            }
        }

        // ------------------------------------------------------------------ observation

        private void PollDecisionLayer()
        {
            var s = DecisionScheduler.Instance;
            if (s == null) return;
            double nowMs = SessionMs();
            // A request: the scheduler's counter moved. The level at request is the level in
            // force before any transition applied since the last poll (same-frame sync providers).
            if (s.Requests != _lastRequests)
            {
                for (int i = _lastRequests; i < s.Requests; i++)
                {
                    string lvl = _levelAtLastPoll;
                    if (_record.Transitions.Count > _transitionsAtLastPoll) lvl = _record.Transitions[_transitionsAtLastPoll].From;
                    var q = new RequestRecord { Number = i + 1, SessionMs = nowMs, Level = lvl, StepIndex = _session.CurrentStepIndex };
                    _record.Requests.Add(q); _pendingRequests.Enqueue(q);
                    WriteTv("provider_request", w => { w.Num("request_number", q.Number); w.Str("level", q.Level); w.Num("step_index", q.StepIndex); });
                }
                _lastRequests = s.Requests;
            }
            // A decision: the session's last response object changed (set inside ApplyDecision, after the transition).
            DecisionResponse resp = _session.LastDecisionResponse;
            if (!ReferenceEquals(resp, _lastResponse) && resp != null)
            {
                _lastResponse = resp;
                RequestRecord q = _pendingRequests.Count > 0 ? _pendingRequests.Dequeue() : null;
                var d = new DecisionRecord
                {
                    Index = _record.Decisions.Count,
                    SessionMs = nowMs,
                    Status = resp.Status.ToString(),
                    ParsedAction = resp.ParsedAction.ToString(),
                    UsedFallback = resp.UsedFallback,
                    LatencyMs = resp.LatencyMs,
                    LevelAtRequest = q != null ? q.Level : _levelAtLastPoll,
                    LevelAfter = _level.CurrentLevel.ToString(),
                    ProviderId = resp.ProviderId,
                    StepIndex = _session.CurrentStepIndex
                };
                _record.Decisions.Add(d);
                LastAiText = $"{d.ParsedAction}{(d.UsedFallback ? " (fallback " + d.Status + ")" : "")}  {d.LatencyMs:F0} ms";
                WriteTv("provider_decision", w =>
                {
                    w.Num("decision_index", d.Index); w.Str("status", d.Status); w.Str("parsed_action", d.ParsedAction); w.Bool("used_fallback", d.UsedFallback);
                    w.Num("latency_ms", d.LatencyMs); w.Str("level_at_request", d.LevelAtRequest); w.Str("level_after", d.LevelAfter); w.Str("provider", d.ProviderId); w.Num("step_index", d.StepIndex);
                });
                if (Config.Verbose) Debug.Log($"[TechnicalValidation] decision #{d.Index}: {d.ParsedAction} {d.Status}{(d.UsedFallback ? " fallback" : "")} {d.LevelAtRequest} -> {d.LevelAfter} ({d.LatencyMs:F0} ms)");
            }
            _levelAtLastPoll = _level.CurrentLevel.ToString();
            _transitionsAtLastPoll = _record.Transitions.Count;
        }

        private void HandleLevelChanged(SupportLevel from, SupportLevel to, string reason)
        {
            var t = new TransitionRecord { SessionMs = SessionMs(), From = from.ToString(), To = to.ToString(), Reason = reason, StepIndex = _session != null ? _session.CurrentStepIndex : -1 };
            _record.Transitions.Add(t);
            LastTransitionText = $"{Levels.Short(t.From)} -> {Levels.Short(t.To)} ({reason})";
            if (reason == "ai_decision") { _lastAiTransitionMs = t.SessionMs; _aiTransitionsSeen++; StartCoroutine(PresentationCheckAfter(5.5f, "after_transition")); }
            WriteTv("support_transition", w => { w.Str("from", t.From); w.Str("to", t.To); w.Str("reason", t.Reason); w.Num("step_index", t.StepIndex); });
        }

        private void HandleStepChanged(StepData step, int index, string reason)
        {
            string before = _levelAtLastPoll;
            // StepRunner applies the step default before announcing the step: a step_default
            // transition recorded in this frame carries the level that preceded it.
            for (int i = _record.Transitions.Count - 1; i >= _transitionsAtLastPoll && i >= 0; i--)
                if (_record.Transitions[i].Reason == "step_default") { before = _record.Transitions[i].From; break; }
            var s = new StepRecord { Index = index, StepId = step != null ? step.StepIdentifier : null, SessionMs = SessionMs(), LevelOnEntry = _level.CurrentLevel.ToString(), LevelBeforeEntry = before };
            _record.Steps.Add(s);
            if (_record.LevelAtStart == null) _record.LevelAtStart = s.LevelOnEntry;
            WriteTv("step_entered", w => { w.Num("step_index", s.Index); w.Str("step_id", s.StepId); w.Str("level_on_entry", s.LevelOnEntry); w.Str("level_before_entry", s.LevelBeforeEntry); w.Str("reason", reason); });
        }

        private void HandleActionEntered(AssemblyAction action)
        {
            WriteTv("action_entered", w => { w.Str("action_id", action != null ? action.Id : null); w.Str("level", _level.CurrentLevel.ToString()); });
            StartCoroutine(PresentationCheckAfter(0.6f, "after_action_enter"));
        }

        private void HandleSessionComplete()
        {
            _record.SequenceFinished = true;
            // Raised before the session controller ends the logger session: the completion
            // record lands inside the production JSONL, before session_end. When the sequence
            // completed inside an injection (the last Success), the sequencer commits that
            // event first so its records precede the completion record and the count is final.
            Seq.Complete("sequence_complete", _session != null ? _session.CurrentActionId : null);
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (_record == null) return;
            if (type == LogType.Exception) { _record.ExceptionCount++; if (_record.FirstException == null) _record.FirstException = Truncate(condition, 160); }
            else if (type == LogType.Error) _record.ErrorLogCount++;
        }

        /// <summary>
        /// Presentation check: the instruction panel must show the current action's text for the
        /// CURRENT level (GuidanceText is the authored source the presenter renders from); the
        /// presenter's ghost state is recorded alongside. Skipped while a transition notice may
        /// still occupy the card.
        /// </summary>
        private IEnumerator PresentationCheckAfter(float seconds, string why)
        {
            yield return new WaitForSeconds(seconds);
            if (_finished || _session == null || !_session.AssemblyRunning || _workflow == null) yield break;
            StepData step = _workflow.CurrentStage;
            AssemblyAction action = _workflow.CurrentAction;
            if (step == null || action == null || _session.StepIntroPending) yield break;
            SupportLevel level = _level.CurrentLevel;
            string expected = Norm(GuidanceText.InstructionFor(step, action, level));
            if (string.IsNullOrEmpty(expected)) yield break;
            var others = new List<string>();
            foreach (SupportLevel l in new[] { SupportLevel.L1_Minimal, SupportLevel.L2_Guided, SupportLevel.L3_Assisted })
            {
                if (l == level) continue;
                string t = Norm(GuidanceText.InstructionFor(step, action, l));
                if (!string.IsNullOrEmpty(t) && t != expected) others.Add(t);
            }
            bool match = false, other = false; string seen = "";
            Transform root = _flow != null && _flow.StepPanel != null ? _flow.StepPanel.transform : null;
            TextMeshProUGUI[] texts = root != null ? root.GetComponentsInChildren<TextMeshProUGUI>(true) : FindObjectsByType<TextMeshProUGUI>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var tmp in texts)
            {
                if (tmp == null || !tmp.gameObject.activeInHierarchy) continue;
                string t = Norm(tmp.text);
                if (string.IsNullOrEmpty(t)) continue;
                if (t == expected) match = true;
                else if (others.Contains(t)) { other = true; seen = tmp.text; }
            }
            var p = new PresentationCheck
            {
                SessionMs = SessionMs(), ActionId = action.Id, Level = level.ToString(), TextMatches = match, OtherLevelTextShown = other,
                GhostState = _presenter != null ? _presenter.DescribeActiveGhosts() : "no presenter",
                Detail = why + (match ? "" : " expected=\"" + Truncate(expected, 80) + "\"") + (other ? " other_level_text=\"" + Truncate(seen, 80) + "\"" : "")
            };
            _record.Presentation.Add(p);
            WriteTv("presentation_check", w => { w.Str("why", why); w.Str("action_id", p.ActionId); w.Str("level", p.Level); w.Bool("text_matches", p.TextMatches); w.Bool("other_level_text_shown", p.OtherLevelTextShown); w.Str("ghosts", p.GhostState); });
            if (!match || other) Debug.LogWarning($"[TechnicalValidation] presentation check ({why}) at {level}: text_matches={match} other_level_text={other} ghosts={p.GhostState}");
        }

        private static string Norm(string s) { return string.IsNullOrEmpty(s) ? "" : s.Replace("\r", "").Replace("\n", " ").Trim(); }

        // ------------------------------------------------------------------ the scripted run

        private IEnumerator Run()
        {
            _runStartedAt = Time.realtimeSinceStartup;
            _record.StartUtc = DateTime.UtcNow;
            _record.SessionId = _logger != null ? _logger.SessionId : null;
            _record.LoggerPath = _logger != null ? _logger.FilePath : null;
            var sched = DecisionScheduler.Instance;
            _record.ProviderId = sched != null ? sched.ProviderId : null;
            _record.ProviderStatus = sched != null ? sched.Status : "no scheduler";
            if (_record.LevelAtStart == null) _record.LevelAtStart = _level.CurrentLevel.ToString();
            Phase = "running";
            Note("technical_validation_started", $"scenario={Scenario.Id} mode={Config.ModeName} physiology={_record.Physiology} delay_scale={Config.EffectiveDelayScale.ToString("0.##", CultureInfo.InvariantCulture)} events={Scenario.Events.Count} provider={_record.ProviderId ?? "none"} status={_record.ProviderStatus}");
            WriteTv("technical_validation_started", w =>
            {
                w.Str("scenario_id", Scenario.Id); w.Str("scenario_title", Scenario.Title); w.Str("mode", Config.ModeName); w.Str("physiology", _record.Physiology);
                w.Num("delay_scale", (float)Config.EffectiveDelayScale); w.Num("event_count", Scenario.Events.Count); w.Str("provider", _record.ProviderId); w.Str("provider_status", _record.ProviderStatus);
                w.Str("config", Config.Describe()); w.Str("build", _record.BuildLabel); w.Str("level_at_start", _record.LevelAtStart);
            });
            Debug.Log($"[TechnicalValidation] RUN {Scenario.Id} ({Config.ModeName}, physiology {_record.Physiology}) started at {_record.LevelAtStart}; first event in {Config.StartDelaySeconds:F1} s");

            if (Config.StartDelaySeconds > 0) yield return new WaitForSeconds((float)Config.StartDelaySeconds);

            for (int i = 0; i < Scenario.Events.Count && !_finishing; i++)
            {
                ValidationEvent ev = Scenario.Events[i];
                EventIndex = i;
                CurrentEventText = $"#{i + 1}/{Scenario.Events.Count} {ev.KindName}{(ev.AfterSeconds > 0 ? " (after " + ev.AfterSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s)" : "")}";
                yield return Delay(Config.ScaleDelay(ev.AfterSeconds));
                if (_session == null || _session.SequenceFinished) { Debug.Log("[TechnicalValidation] sequence already finished; remaining scripted events skipped."); break; }

                if (ev.Kind == ValidationEventKind.FinishSequence)
                {
                    yield return FinishSequence(i);
                    continue;
                }
                if (ev.Kind == ValidationEventKind.Success && PlacementMotion.Enabled(Config)) yield return AnimatedSuccess(ev, i, false);   // presentation only
                else Inject(ev, i, false);
                yield return null;
            }

            yield return Finish();
        }

        /// <summary>Real-time wait; in presentation mode an applied AI transition extends the wait so the cue and the new guidance are seen.</summary>
        private IEnumerator Delay(double seconds)
        {
            double until = Time.realtimeSinceStartup + seconds;
            while (true)
            {
                double hold = Config.EffectivePostTransitionHoldSeconds;
                if (hold > 0 && _lastAiTransitionMs >= 0)
                {
                    double sinceMs = SessionMs() - _lastAiTransitionMs;
                    double remaining = hold - sinceMs / 1000.0;
                    if (remaining > 0 && Time.realtimeSinceStartup + remaining > until) until = Time.realtimeSinceStartup + remaining;
                }
                if (Time.realtimeSinceStartup >= until) yield break;
                yield return null;
            }
        }

        private void Inject(ValidationEvent ev, int index, bool tail)
        {
            string actionBefore = _session.CurrentActionId;
            bool introBefore = _session.StepIntroPending;
            int stepBefore = _session.CurrentStepIndex;
            var rec = new InjectedEventRecord
            {
                Index = index, Kind = ev.KindName, SessionMs = SessionMs(), StepIndex = stepBefore, StepId = _stepRunner.CurrentStep != null ? _stepRunner.CurrentStep.StepIdentifier : null,
                ActionBefore = actionBefore, ExpectedAction = ev.ExpectedActionId, Level = _level.CurrentLevel.ToString(), FromFinishTail = tail,
                Aligned = string.IsNullOrEmpty(ev.ExpectedActionId) || ev.ExpectedActionId == actionBefore
            };
            // The production call below can complete the sequence synchronously (the last
            // Success). HandleSessionComplete then commits `rec` first and writes the completion
            // record afterwards, before the session controller ends the production logger session.
            Seq.Open(rec);
            bool injected = false; string detail = null; string partKey = null;
            try
            {
                switch (ev.Kind)
                {
                    case ValidationEventKind.Continue:
                        if (introBefore || _session.IsReadState || _session.CurrentActionType == AssemblySessionController.ActionType.AUTO)
                        {
                            _session.AdvanceStepManually();          // the Continue button's production method
                            injected = _session.StepIntroPending != introBefore || _session.CurrentActionId != actionBefore || _session.CurrentStepIndex != stepBefore || _session.SequenceFinished;
                            detail = injected ? "continue accepted" : "continue did not advance";
                        }
                        else { injected = false; detail = "Continue unavailable: current action is " + _session.CurrentActionType; }
                        break;
                    case ValidationEventKind.Success:
                        injected = _validator != null && _validator.TechnicalValidationSucceed(ev.Note);
                        detail = injected ? "validator Succeed()" : "validator not armed (" + (_validator != null ? _validator.ArmFailureReason ?? "inactive" : "no validator") + ")";
                        break;
                    case ValidationEventKind.FailedAttempt:
                        injected = _validator != null && _validator.TechnicalValidationFailAttempt(false, ev.Note);
                        detail = injected ? "validator FailAttempt(counted=false)" : "validator not armed";
                        break;
                    case ValidationEventKind.Error:
                        injected = _validator != null && _validator.TechnicalValidationFailAttempt(true, ev.Note);
                        detail = injected ? "validator FailAttempt(counted=true)" : "validator not armed";
                        break;
                    case ValidationEventKind.WrongComponent:
                        {
                            string used = null;
                            injected = _validator != null && _validator.TechnicalValidationWrongComponent(ev.PartKey, out used);
                            partKey = used;
                            detail = injected ? "validator wrong-part events for " + used : "no non-accepted selectable part available";
                            break;
                        }
                    case ValidationEventKind.Wait:
                        injected = true; detail = "wait"; break;
                    case ValidationEventKind.Note:
                        injected = true; detail = ev.Note ?? ""; break;
                }
            }
            catch (Exception e)
            {
                injected = false; detail = "threw: " + e.Message;
                Debug.LogError("[TechnicalValidation] injection threw: " + e);
            }
            // Returns false when the sequence completed inside this injection: HandleSessionComplete
            // already committed the record (as injected) ahead of the completion record.
            Seq.Close(injected, detail, partKey, _session.CurrentActionId);
        }

        // ------------------------------------------------------------------ presentation-only placement motion

        /// <summary>
        /// Presentation mode only (PlacementMotion.Enabled): stage the scripted success visually
        /// (brief highlight, eased move + rotate to the pose the production snap will produce) and
        /// THEN inject the same production success path that validation mode injects immediately.
        /// The logical event is recorded at that injection, exactly as in validation mode; the
        /// motion itself is diagnostic-only (presentation_motion_* lines in the .tv.jsonl). Nothing
        /// here touches support, decision, scheduler, gate or timing state.
        /// </summary>
        private IEnumerator AnimatedSuccess(ValidationEvent ev, int index, bool tail)
        {
            Transform part; Vector3 targetPos; Quaternion targetRot;
            if (_validator == null || !_validator.TechnicalValidationTryGetPlacement(out part, out targetPos, out targetRot))
            {
                string reason = _validator == null ? "no validator" : "validator not armed";
                WriteTv("presentation_motion_skipped", w => { w.Num("event_index", index); w.Bool("finish_tail", tail); w.Str("reason", reason); });
                Inject(ev, index, tail);
                yield break;
            }
            var run = new PlacementMotionRun(Config.EffectivePlacementMotionSeconds, Config.EffectivePlacementHighlightSeconds);
            Vector3 fromPos = part.position;
            float angleDeg = Quaternion.Angle(part.rotation, targetRot);
            float distanceM = Vector3.Distance(fromPos, targetPos);
            string partName = part.name;
            string actionId = _session.CurrentActionId;
            double startedMs = SessionMs();
            string savedText = CurrentEventText;
            CurrentEventText = savedText + " [motion]";
            WriteTv("presentation_motion_started", w =>
            {
                w.Num("event_index", index); w.Bool("finish_tail", tail); w.Str("action_id", actionId); w.Str("part", partName);
                w.Num("duration_s", (float)run.DurationSeconds); w.Num("highlight_s", (float)run.HighlightSeconds); w.Num("distance_m", distanceM); w.Num("angle_deg", angleDeg);
            });
            if (Config.Verbose) Debug.Log("[TechnicalValidation] presentation motion for event #" + (index + 1) + " on '" + partName + "': " + distanceM.ToString("F3", CultureInfo.InvariantCulture) + " m / " + angleDeg.ToString("F0", CultureInfo.InvariantCulture) + " deg over " + run.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + " s");

            bool wasKinematic = PresentationPlacementAnimator.PrepareBody(part);
            yield return PresentationPlacementAnimator.Animate(part, targetPos, targetRot, run, () => Time.realtimeSinceStartup);

            float residualPos = part != null ? Vector3.Distance(part.position, targetPos) : -1f;
            float residualRot = part != null ? Quaternion.Angle(part.rotation, targetRot) : -1f;
            double elapsedMs = SessionMs() - startedMs;
            WriteTv("presentation_motion_completed", w =>
            {
                w.Num("event_index", index); w.Bool("finish_tail", tail); w.Str("part", partName); w.Num("elapsed_ms", (float)elapsedMs);
                w.Num("residual_pos_m", residualPos); w.Num("residual_rot_deg", residualRot); w.Bool("motion_finished", run.MotionFinished);
            });
            CurrentEventText = savedText;

            // The same production pathway validation mode uses, recorded as the logical event.
            int before = _record.Events.Count;
            if (!run.Complete(() => Inject(ev, index, tail), Time.realtimeSinceStartup))
                Inject(ev, index, tail);   // only if the motion could not finish (part destroyed): the logical event is injected regardless
            var rec = _record.Events.Count > before ? _record.Events[_record.Events.Count - 1] : null;
            if (rec != null && !rec.Injected) PresentationPlacementAnimator.RestoreBody(part, wasKinematic);
        }

        /// <summary>Clean Continue / success events at the finish delay until the sequence ends (bounded).</summary>
        private IEnumerator FinishSequence(int scriptIndex)
        {
            Phase = "finishing sequence";
            int guard = 0, idle = 0;
            while (!_session.SequenceFinished && guard < 120)
            {
                yield return Delay(Config.EffectiveFinishDelaySeconds);
                if (_session.SequenceFinished) break;
                ValidationEventKind kind;
                if (_session.StepIntroPending || _session.IsReadState || _session.CurrentActionType == AssemblySessionController.ActionType.AUTO) kind = ValidationEventKind.Continue;
                else if (_session.CurrentActionType == AssemblySessionController.ActionType.PHYSICAL && _validator != null && _validator.IsActive) kind = ValidationEventKind.Success;
                else
                {
                    // Between actions (a stage with no enabled action advances by itself). Wait briefly.
                    if (++idle > 10) { Debug.LogWarning("[TechnicalValidation] finish tail: no injectable state for 10 rounds; stopping the tail."); break; }
                    continue;
                }
                idle = 0;
                CurrentEventText = $"finish tail {++guard}: {ValidationEvent.KindToName(kind)}";
                var tailEvent = new ValidationEvent { Kind = kind, Note = "finish_sequence" };
                if (kind == ValidationEventKind.Success && PlacementMotion.Enabled(Config)) yield return AnimatedSuccess(tailEvent, scriptIndex, true);   // presentation only
                else Inject(tailEvent, scriptIndex, true);
                yield return null;
            }
        }

        private IEnumerator Finish()
        {
            _finishing = true;
            Phase = "finishing";
            CurrentEventText = "waiting for in-flight decision";
            // Let an in-flight provider request land (timeout + apply) so it is in the record.
            float until = Time.realtimeSinceStartup + 12f;
            var s = DecisionScheduler.Instance;
            while (Time.realtimeSinceStartup < until && s != null && s.Gate != null && s.Gate.InFlight) yield return null;
            yield return null;
            PollDecisionLayer();

            _record.ErrorsTotalObserved = _session.ErrorsTotal;
            _record.AttemptsTotalObserved = _session.AttemptsTotal;

            if (!_session.SequenceFinished)
            {
                // The script ended before the sequence did: close the session here so the
                // production JSONL closes correctly (the completion record goes in first).
                Seq.Complete("script_complete", _session.CurrentActionId);
                _session.EndSession("technical_validation_complete");
            }
            else Seq.Complete("sequence_complete", _session.CurrentActionId);   // no-op when already written
            yield return null;

            _record.SessionEnded = _logger == null || !_logger.IsOpen;
            _record.EndUtc = DateTime.UtcNow;
            ReadBackJsonl();
            List<AssertionResult> results = IntegrationAssertions.Evaluate(_record);
            WriteSummaries(results);
            int failed = IntegrationAssertions.Failed(results);
            ResultText = $"{results.Count - failed} passed / {failed} failed";
            Phase = failed == 0 ? "completed" : "completed with failures";
            CurrentEventText = "done";
            _finished = true;
            CloseTvLog();
            Debug.Log($"[TechnicalValidation] RUN {Scenario.Id} finished: calls {_record.Requests.Count} K{_record.Keep}/I{_record.Increase}/D{_record.Decrease} fallbacks {_record.Fallbacks} trajectory {_record.Trajectory()} assertions {ResultText}");
        }

        private CompletionSequencer Seq
        {
            get { if (_seq == null) _seq = new CompletionSequencer(_record, CommitInjection, WriteCompletionRecord); return _seq; }
        }

        /// <summary>Console line + production note + validation-log line for one committed event (the sequencer calls this exactly once per event, right after the ledger append).</summary>
        private void CommitInjection(InjectedEventRecord rec)
        {
            string misaligned = rec.Aligned ? "" : " MISALIGNED expected " + rec.ExpectedAction + " but at " + (rec.ActionBefore ?? "<none>");
            string tailText = rec.FromFinishTail ? " (finish tail)" : "";
            string atSeconds = (rec.SessionMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
            string outcome = rec.Injected ? "injected" : "NOT injected";
            if (Config.Verbose || !rec.Injected || !rec.Aligned)
                Debug.Log("[TechnicalValidation] event #" + (rec.Index + 1) + " " + rec.Kind + tailText + " at " + atSeconds + "s step " + rec.StepIndex + " action " + (rec.ActionBefore ?? "<none>") + " level " + rec.Level + ": " + outcome + " (" + rec.Detail + ")" + misaligned);
            Note("tv_event", $"idx={rec.Index} kind={rec.Kind} injected={rec.Injected} step={rec.StepIndex} action={rec.ActionBefore ?? "none"} level={rec.Level}{(rec.PartKey != null ? " part=" + rec.PartKey : "")}{(rec.Aligned ? "" : " misaligned_expected=" + rec.ExpectedAction)}");
            WriteTv("injected_event", w =>
            {
                w.Num("event_index", rec.Index); w.Str("kind", rec.Kind); w.Bool("finish_tail", rec.FromFinishTail); w.Bool("injected", rec.Injected); w.Bool("aligned", rec.Aligned);
                w.Num("step_index", rec.StepIndex); w.Str("step_id", rec.StepId); w.Str("action_before", rec.ActionBefore); w.Str("action_after", rec.ActionAfter); w.Str("expected_action", rec.ExpectedAction);
                w.Str("level", rec.Level); w.Str("part_key", rec.PartKey); w.Str("detail", rec.Detail);
            });
        }

        /// <summary>The completion record (production note + validation-log line). The sequencer writes it exactly once, after every injected event has been committed, so events_injected is the final count.</summary>
        private void WriteCompletionRecord(string how)
        {
            Note("technical_validation_completed", $"scenario={Scenario.Id} how={how} events_injected={_record.InjectedCount}/{_record.Events.Count} requests={_record.Requests.Count} decisions={_record.Decisions.Count} keep={_record.Keep} increase={_record.Increase} decrease={_record.Decrease} fallbacks={_record.Fallbacks} max_level={_record.MaxLevel} ai_transitions={_record.AiTransitions} exceptions={_record.ExceptionCount}");
            WriteTv("technical_validation_completed", w => { w.Str("scenario_id", Scenario.Id); w.Str("how", how); w.Num("events_injected", _record.InjectedCount); w.Num("events_total", _record.Events.Count); w.Num("requests", _record.Requests.Count); w.Num("decisions", _record.Decisions.Count); w.Str("trajectory", _record.Trajectory()); });
        }

        // ------------------------------------------------------------------ files

        private void ReadBackJsonl()
        {
            string path = _record.LoggerPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { _record.JsonlReadable = false; return; }
            try
            {
                int decisions = 0, changes = 0, tv = 0; string last = null;
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    last = line;
                    if (line.Contains("\"event\":\"decision\"")) decisions++;
                    else if (line.Contains("\"event\":\"support_change\"")) changes++;
                    if (line.Contains("technical_validation") || line.Contains("tv_event")) tv++;
                }
                _record.JsonlReadable = true;
                _record.DecisionLinesInJsonl = decisions;
                _record.SupportChangeLinesInJsonl = changes;
                _record.TechnicalValidationNotesInJsonl = tv;
                if (last != null)
                {
                    var o = MiniJson.ParseObject(last);
                    _record.JsonlLastEvent = o != null ? MiniJson.GetString(o, "event", null) : "unparseable";
                    _record.JsonlClosesWithSessionEnd = _record.JsonlLastEvent == SessionLogger.Events.SessionEnd;
                }
            }
            catch (Exception e) { _record.JsonlReadable = false; Debug.LogWarning("[TechnicalValidation] could not read back the session JSONL: " + e.Message); }
        }

        private string OutputDir()
        {
            string dir = Path.Combine(Application.persistentDataPath, TechnicalValidationConfig.OutputFolder);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void WriteSummaries(List<AssertionResult> results)
        {
            try
            {
                string dir = OutputDir();
                string stem = $"{_record.ScenarioId}_{_record.SessionId ?? DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}";
                _record.ValidationLogPath = _tvLogPath;
                File.WriteAllText(Path.Combine(dir, stem + ".summary.md"), Level2SummaryWriter.ToMarkdown(_record, results), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, stem + ".summary.json"), MiniJson.Serialize(Level2SummaryWriter.ToObject(_record, results)), new UTF8Encoding(false));
                // Aggregate over every summary in the folder, oldest first.
                var all = new List<Dictionary<string, object>>();
                var files = new List<string>(Directory.GetFiles(dir, "*.summary.json"));
                files.Sort(StringComparer.Ordinal);
                foreach (string f in files)
                {
                    try { var o = MiniJson.ParseObject(File.ReadAllText(f)); if (o != null) all.Add(o); } catch { /* skip unreadable */ }
                }
                all.Sort((a, b) => string.CompareOrdinal(MiniJson.GetString(a, "start_utc", ""), MiniJson.GetString(b, "start_utc", "")));
                File.WriteAllText(Path.Combine(dir, "level2_summary.md"), Level2SummaryWriter.AggregateMarkdown(all, DateTime.UtcNow), new UTF8Encoding(false));
                Debug.Log($"[TechnicalValidation] summaries written: {Path.Combine(dir, stem + ".summary.md")} (+ .json, level2_summary.md)");
            }
            catch (Exception e) { Debug.LogError("[TechnicalValidation] writing summaries threw: " + e); }
        }

        private void EnsureTvLog()
        {
            if (_tvLog != null || _finished) return;
            if (_logger == null || !_logger.IsOpen) return;
            try
            {
                string dir = OutputDir();
                _tvLogPath = Path.Combine(dir, $"{(Scenario != null ? Scenario.Id : Config.Scenario)}_{_logger.SessionId}.tv.jsonl");
                _tvLog = new StreamWriter(_tvLogPath, append: true, encoding: new UTF8Encoding(false));
                if (_record != null) _record.ValidationLogPath = _tvLogPath;
            }
            catch (Exception e) { Debug.LogError("[TechnicalValidation] could not open the validation log: " + e.Message); }
        }

        private void WriteTv(string eventName, Action<JsonLineWriter> fill)
        {
            EnsureTvLog();
            if (_tvLog == null) return;
            try
            {
                var w = new JsonLineWriter();
                w.Str("schema", ValidationSchema); w.Num("seq", _tvSeq++); w.Str("event", eventName);
                w.Str("t_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)); w.Num("t_session_ms", (float)SessionMs());
                w.Str("session_id", _logger != null ? _logger.SessionId : null); w.Str("scenario_id", Scenario != null ? Scenario.Id : Config.Scenario); w.Str("mode", Config.ModeName);
                fill(w);
                _tvLog.WriteLine(w.Build()); _tvLog.Flush();
            }
            catch (Exception e) { Debug.LogWarning("[TechnicalValidation] validation log write failed: " + e.Message); }
        }

        private void CloseTvLog()
        {
            if (_tvLog == null) return;
            try { _tvLog.Flush(); _tvLog.Dispose(); } catch { /* best effort */ }
            _tvLog = null;
        }

        /// <summary>A note in the PRODUCTION session JSONL (existing note record; no schema change) so the script aligns with the AI/support records.</summary>
        private void Note(string key, string payload)
        {
            if (_logger != null) _logger.LogNote(key + ":" + payload);
        }

        private double SessionMs() { return _logger != null && _logger.IsOpen ? _logger.SessionElapsedMs : Time.realtimeSinceStartup * 1000.0; }

        private void ApplySensorOverrideOnce()
        {
            if (_sensorOverrideDone || !Config.SensorStabilisationSeconds.HasValue) return;
            var screen = FindAnyObjectByType<SensorSetupScreen>(FindObjectsInactive.Include);
            if (screen == null) return;
            screen.stabilisationSeconds = (float)Config.SensorStabilisationSeconds.Value;
            _sensorOverrideDone = true;
            Debug.Log($"[TechnicalValidation] sensor-setup stabilisation shortened to {screen.stabilisationSeconds:F0} s for the synthetic stream (validation mode only).");
        }

        private static string Truncate(string s, int n) { return string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "..."; }
    }
}
