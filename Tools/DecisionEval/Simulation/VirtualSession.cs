// File: VirtualSession.cs
// A virtual participant session: replays a scripted event stream against the SAME decision
// architecture the frozen headset app runs, on a controllable fake clock.
//
// Shared production code (used as-is, not re-implemented):
//   TaskEventLedger (event record + temporal features + current_support_episode),
//   RequestGate (call protection, change hysteresis, evidence accrual, deferral, backoff),
//   DecisionConstraints / SupportLevels (structural constraints, boundaries, one level per action),
//   LlmDecisionCore (prompt, request body, strict parser) through the provider,
//   ReferencePolicyProvider (deterministic comparator), DecisionEvidence (evidence line),
//   DecisionConfig (the same decision_config.json).
//
// Mirrored here because the originals are Unity MonoBehaviours that cannot run headlessly
// (each mirror cites the production source it copies; semantics must stay identical):
//   AssemblySessionController: step/attempt/error/success bookkeeping and BuildDecisionContext
//     (HandleStepChanged, HandleAttempt, HandleWrongPartGrabbed, CompleteActionAndAdvance,
//      HandleSupportChanged, BuildDecisionContext, ApplyDecision),
//   SupportLevelController.ApplyAction (level changes only when the target differs),
//   StepRunner.GoToStep (every step opens at its default level L1, reason "step_default"),
//   DecisionScheduler (Evaluate / OnApplied / the Update loop that re-tries deferred
//     requests and flushes a lone error every 0.25 s).
// Nothing here interprets the operator; the provider decides, the constraints bound.

using System;
using System.Collections.Generic;
using System.Globalization;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision.Simulation
{
    /// <summary>Simulated time. Seconds, monotonic, advanced explicitly.</summary>
    public sealed class FakeClock
    {
        public double NowSeconds { get; private set; }
        public void Advance(double seconds) { if (seconds < 0) throw new ArgumentException("negative advance"); NowSeconds += seconds; }
        public void AdvanceMs(double ms) { Advance(ms / 1000.0); }
    }

    /// <summary>A provider that can report the token usage of its last call (the live model transport).</summary>
    public interface IUsageReporting
    {
        bool TryGetLastUsage(out int inputTokens, out int outputTokens);
    }

    /// <summary>USD per million tokens, for the cost estimate. Assumptions, set from the command line.</summary>
    public static class CostModel
    {
        public static double InputUsdPerMTok = 3.0;
        public static double OutputUsdPerMTok = 15.0;
        public static double Estimate(int inputTokens, int outputTokens) { return inputTokens * InputUsdPerMTok / 1e6 + outputTokens * OutputUsdPerMTok / 1e6; }
    }

    public sealed class DecisionRecord
    {
        public string ScenarioId, PhysiologyVariant, ProviderId;
        public int Repetition, DecisionIndex, EventIndex;
        public double SimTimeS;
        public string StepId, ActionId;
        public SupportLevel LevelBefore, LevelAfter;
        public List<string> AllowedActions;
        public string Trigger, DeferredFrom;
        public int PendingEvents, PendingErrors, PendingSuccesses;
        public DecisionContext Context;
        public string AiInput, Evidence;
        public string RawOutput;
        public SupportAction ParsedAction, AppliedAction;
        public string ConstraintApplied;
        public ConstraintStatus Status;
        public bool UsedFallback, Applied;
        public float LatencyMs;
        public int? InputTokens, OutputTokens;
        public double? CostUsd;
        public SupportAction? ReferenceAction;
        public string ReferenceReason;
        /// <summary>SHA-independent identity of the exact AI input, to find identical contexts across repetitions.</summary>
        public string AiInputKey;
    }

    public sealed class SupportTransition
    {
        public double SimTimeS; public string StepId; public SupportLevel From, To; public string Reason; public int DecisionIndex = -1; public int EventIndex;
    }

    public sealed class TrajectorySample { public double SimTimeS; public SupportLevel Level; public string Label; public int EventIndex; }

    public sealed class SimulationRun
    {
        public string ScenarioId, Title, PhysiologyVariant, ProviderId;
        public int Repetition;
        public List<DecisionRecord> Decisions = new List<DecisionRecord>();
        public List<SupportTransition> Transitions = new List<SupportTransition>();
        public List<TrajectorySample> Trajectory = new List<TrajectorySample>();
        public List<string> GateLog = new List<string>();
        public int ProviderCalls, Keep, Increase, Decrease, Fallbacks, SkippedTriggers, DeferredTriggers;
        public int EventsTotal, ErrorsTotal, AttemptsTotal, Successes;
        public double DurationS;
        public SupportLevel FinalLevel, MaxLevel;
        public double? DifficultyOnsetS; public int? DifficultyOnsetEvent;
        public double? FirstIncreaseS; public int? FirstIncreaseEvent;
        public double? FirstDecreaseS; public int? FirstDecreaseEvent;
        public int AiChanges, AiIncreases, AiDecreases, Oscillations;
        public double TotalCostUsd; public bool CostKnown = true;
    }

    public sealed class VirtualSession
    {
        private readonly DecisionConfig _cfg;
        private readonly IDecisionProvider _provider;
        private readonly IDecisionProvider _reference;
        private readonly bool _advanceClockByLatency;
        private readonly Action<string> _log;

        // --- mirrored session state (AssemblySessionController) ---
        private readonly FakeClock _clock = new FakeClock();
        private readonly TaskEventLedger _ledger = new TaskEventLedger();
        private RequestGate _gate;
        private SupportLevel _level = SupportLevel.L1_Minimal;          // SupportLevelController.initialLevel in the live scene
        private bool _stepOpen;
        private int _stepIndex = -1; private string _stepId; private int _taskComplexity; private float? _expectedMs;
        private string _actionId;
        private float _stepMs, _overallMs;
        private int _attemptsOnStep, _attemptsTotal, _errorsOnStep, _errorsTotal, _supportChangesOnStep, _supportChangesTotal;
        private int _errorsSinceChange, _attemptsSinceChange, _successesSinceChange;
        private float _lastErrorStepMs = -1f, _lastChangeStepMs = -1f;
        private int _decisionIndex;
        private string _physiologyProfile;
        private SimulationRun _run;
        private ScenarioScript _script;
        private int _eventIndex;
        private string _lastHoldReason;
        // Live-provider ordering: a request fired from inside an event handler is built there
        // (before the handler finishes its bookkeeping) but its answer arrives, and is applied,
        // only after the handler has completed, because the real provider is asynchronous.
        private bool _inHandler;
        private DecisionRecord _pendingRecord; private DecisionResponse _pendingResponse;

        public VirtualSession(DecisionConfig cfg, IDecisionProvider provider, IDecisionProvider reference, bool advanceClockByLatency, Action<string> log)
        {
            _cfg = cfg ?? new DecisionConfig();
            _provider = provider ?? throw new ArgumentNullException("provider");
            _reference = reference;
            _advanceClockByLatency = advanceClockByLatency;
            _log = log;
        }

        public FakeClock Clock { get { return _clock; } }
        public SupportLevel Level { get { return _level; } }
        public RequestGate Gate { get { return _gate; } }

        /// <summary>Runs one scenario under one physiology profile. The event stream is replayed verbatim; only the level moves, and only through the pipeline.</summary>
        public SimulationRun Run(ScenarioScript script, string physiologyProfile, int repetition)
        {
            _script = script;
            _physiologyProfile = physiologyProfile;
            _gate = new RequestGate(_cfg.Schedule);
            _run = new SimulationRun { ScenarioId = script.Id, Title = script.Title, PhysiologyVariant = physiologyProfile, Repetition = repetition, ProviderId = _provider.ProviderId };
            _run.MaxLevel = _level; _run.FinalLevel = _level;
            _run.DifficultyOnsetS = script.DifficultyOnsetSeconds;
            _eventIndex = 0;
            foreach (ScenarioEvent ev in script.Events)
            {
                _eventIndex++;
                switch (ev.Kind)
                {
                    case ScenarioEventKind.Step: EnterStep(ev); break;
                    case ScenarioEventKind.Action: EnterAction(ev.Id); break;
                    case ScenarioEventKind.Advance: Advance(ev.Seconds); break;
                    case ScenarioEventKind.FailedAttempt: HandleAttempt(false, false); break;
                    case ScenarioEventKind.Error: HandleAttempt(false, true); break;
                    case ScenarioEventKind.WrongComponent: HandleWrongPart(ev.Id); break;
                    case ScenarioEventKind.Success: HandleSuccess(); break;
                    case ScenarioEventKind.Physiology: _physiologyProfile = ev.Id == "null" ? null : ev.Id; break;
                    case ScenarioEventKind.Note: break;
                }
                if ((ev.Kind == ScenarioEventKind.Error || ev.Kind == ScenarioEventKind.WrongComponent) && !_run.DifficultyOnsetEvent.HasValue) _run.DifficultyOnsetEvent = _eventIndex;
                if (ev.Kind != ScenarioEventKind.Advance) Sample(ev.ToString());
            }
            if (_stepOpen) _stepOpen = false;
            _run.EventsTotal = script.Events.Count;
            _run.DurationS = _clock.NowSeconds;
            _run.FinalLevel = _level;
            _run.ErrorsTotal = _errorsTotal; _run.AttemptsTotal = _attemptsTotal;
            return _run;
        }

        // ------------------------------------------------------------------ events (mirrors)

        /// <summary>StepRunner.GoToStep + AssemblySessionController.HandleStepChanged + DecisionScheduler.Update (step change -> gate.ResetForStep).</summary>
        private void EnterStep(ScenarioEvent ev)
        {
            // StepRunner sets the step's default level (L1 in every authored step) BEFORE announcing the step.
            if (_level != SupportLevel.L1_Minimal) ChangeLevel(SupportLevel.L1_Minimal, "step_default", -1);

            _stepIndex++;
            _stepId = ev.Id; _taskComplexity = ev.Complexity; _expectedMs = ev.ExpectedSeconds > 0 ? (float?)(ev.ExpectedSeconds * 1000.0) : null;
            _actionId = null;
            // HandleStepChanged: counters and the ledger start afresh.
            _stepMs = 0f; _attemptsOnStep = 0; _errorsOnStep = 0; _supportChangesOnStep = 0;
            _errorsSinceChange = 0; _attemptsSinceChange = 0; _successesSinceChange = 0;
            _lastErrorStepMs = -1f; _lastChangeStepMs = -1f;
            _ledger.Reset();
            _stepOpen = true;
            _gate.ResetForStep(); _lastHoldReason = null;
            Log($"[{T()}] step {_stepId} entered at {_level}");
        }

        /// <summary>AssemblySessionController.EnterAction -> OnActionEntered -> DecisionScheduler.HandleActionEntered.</summary>
        private void EnterAction(string id)
        {
            _actionId = id;
            if (_cfg.Schedule.OnActionEnter) Evaluate("action_enter:" + id, false);
        }

        /// <summary>AssemblySessionController.HandleAttempt (validator OnAttemptEvaluated): every attempt counts; a counted failure is an error.</summary>
        private void HandleAttempt(bool success, bool counted)
        {
            _attemptsOnStep++; _attemptsTotal++; _attemptsSinceChange++;
            // Production order, kept deliberately: OnCountedError is raised (and a request may be
            // built synchronously) BEFORE the session counts the error and records it in the
            // ledger, so the context of an error-triggered request excludes the triggering error.
            if (counted && !success && _cfg.Schedule.OnCountedError)
            {
                _gate.NoteError(_clock.NowSeconds);                                             // DecisionScheduler.HandleCountedError
                _inHandler = true;
                try { Evaluate("counted_error", false); } finally { _inHandler = false; }
            }
            if (counted && !success) { _errorsOnStep++; _errorsTotal++; NoteRecentError(); }
            if (!success) _ledger.Add(counted ? TaskEventKind.Error : TaskEventKind.FailedAttempt, _stepMs, _actionId, _level.ToString());
            ApplyPending();   // the asynchronous answer lands after the handler: the triggering error precedes any change in the ledger
        }

        /// <summary>AssemblySessionController.HandleWrongPartGrabbed -> OnWrongComponentGrabbed -> DecisionScheduler.HandleWrongComponent.</summary>
        private void HandleWrongPart(string partKey)
        {
            _errorsOnStep++; _errorsTotal++; NoteRecentError();
            _ledger.Add(TaskEventKind.WrongComponent, _stepMs, _actionId, _level.ToString());
            if (_cfg.Schedule.OnWrongComponent) { _gate.NoteError(_clock.NowSeconds); Evaluate("wrong_component_grabbed:" + partKey, false); }
        }

        /// <summary>Validated success: HandleAttempt(success) then CompleteActionAndAdvance -> OnActionCompleted -> DecisionScheduler.HandleActionCompleted.</summary>
        private void HandleSuccess()
        {
            HandleAttempt(true, false);
            _successesSinceChange++; _run.Successes++;
            _ledger.Add(TaskEventKind.Success, _stepMs, _actionId, _level.ToString());
            string id = _actionId ?? "?";
            if (_cfg.Schedule.OnActionComplete) { _gate.NoteSuccess(_clock.NowSeconds); Evaluate("action_complete:" + id, false); }
            _actionId = null;   // the next scripted action entry sets it (production enters it immediately)
        }

        private void NoteRecentError() { _errorsSinceChange++; _lastErrorStepMs = _stepMs; }

        /// <summary>Simulated time passes in 0.25 s ticks: the session clocks advance and the scheduler's Update re-tries deferred requests / flushes a lone error (DecisionScheduler.Update).</summary>
        private void Advance(double seconds)
        {
            double remaining = seconds;
            while (remaining > 1e-9)
            {
                double dt = Math.Min(0.25, remaining);
                remaining -= dt;
                Tick(dt);
            }
        }

        private void Tick(double dt)
        {
            _clock.Advance(dt);
            _overallMs += (float)(dt * 1000.0);
            if (_stepOpen) _stepMs += (float)(dt * 1000.0);
            if (_gate.HasDeferred) Evaluate(_gate.Deferred, true);
            else if (_gate.NeedsFlush(_clock.NowSeconds)) Evaluate("pending_flush", false);
        }

        // ------------------------------------------------------------------ scheduler mirror

        /// <summary>DecisionScheduler.Evaluate: the gate decides; a fired request goes through the (mirrored) session seam.</summary>
        private void Evaluate(string trigger, bool quiet)
        {
            if (!_stepOpen) { Skip(trigger, "assembly not running", quiet); return; }
            RequestGate.Result r = _gate.Evaluate(trigger, _clock.NowSeconds, _stepMs, _level, false);
            if (r.Verdict == RequestGate.Verdict.Skipped) { Skip(trigger, r.Reason, quiet); _run.SkippedTriggers++; return; }
            if (r.Verdict == RequestGate.Verdict.Deferred)
            {
                // One line per hold (the production scheduler compares the full reason text, whose
                // numbers change every tick, so logcat shows a line per 0.25 s; the kind is what matters here).
                string kind = trigger + "|" + (r.Reason.IndexOf('(') >= 0 ? r.Reason.Substring(0, r.Reason.IndexOf('(')).Trim() : r.Reason);
                if (_lastHoldReason != kind) { _lastHoldReason = kind; _run.DeferredTriggers++; _run.GateLog.Add($"[{T()}] deferred {trigger}: {r.Reason}"); }
                return;
            }
            _lastHoldReason = null;
            Request(r);
            if (!_inHandler) ApplyPending();
        }

        private void Skip(string trigger, string why, bool quiet)
        {
            if (!quiet) _run.GateLog.Add($"[{T()}] skip {trigger}: {why}");
        }

        /// <summary>RequestAndApplyDecision: build the context, ask the provider, apply under the structural constraints, record everything.</summary>
        private void Request(RequestGate.Result r)
        {
            _run.ProviderCalls++;
            SupportLevel before = _level;
            DecisionContext ctx = BuildDecisionContext();
            string aiInput = LlmDecisionCore.ContextToJson(ctx);

            _gate.InFlight = true;
            DecisionResponse response;
            try { response = _provider.RequestDecision(ctx) ?? DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, _provider.ProviderId, null); }
            catch (Exception e) { response = DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, _provider.ProviderId, "exception: " + e.Message); }

            DecisionResponse refResp = null;
            if (_reference != null)
            {
                try { refResp = _reference.RequestDecision(Evaluation.ScenarioGenerator.Clone(ctx)); } catch (Exception) { refResp = null; }
            }

            // Real latency elapses on the headset while the request is in flight; events in that
            // window are deferred by the gate. The fake clock jumps by the measured latency so the
            // same holds here (capped so a timeout cannot swallow a scenario).
            if (_advanceClockByLatency && response.LatencyMs > 0f) Advance(Math.Min(10.0, response.LatencyMs / 1000.0));

            var rec = new DecisionRecord
            {
                ScenarioId = _run.ScenarioId, PhysiologyVariant = _run.PhysiologyVariant, ProviderId = response.ProviderId ?? _provider.ProviderId,
                Repetition = _run.Repetition, DecisionIndex = _decisionIndex, EventIndex = _eventIndex, SimTimeS = _clock.NowSeconds,
                StepId = _stepId, ActionId = _actionId, LevelBefore = before, AllowedActions = DecisionConstraints.AllowedActions(before),
                Trigger = r.Trigger, DeferredFrom = r.DeferredFrom, PendingEvents = r.PendingEvents, PendingErrors = r.PendingErrors, PendingSuccesses = r.PendingSuccesses,
                Context = ctx, AiInput = aiInput, Evidence = DecisionEvidence.Summarise(ctx), AiInputKey = AiInputKeyOf(aiInput),
                RawOutput = response.RawOutput, ParsedAction = response.ParsedAction, Status = response.Status, UsedFallback = response.UsedFallback, LatencyMs = response.LatencyMs,
                ReferenceAction = refResp != null ? (SupportAction?)refResp.ParsedAction : null, ReferenceReason = refResp != null ? refResp.ProviderReason : null
            };
            var usage = _provider as IUsageReporting;
            if (usage != null && usage.TryGetLastUsage(out int inTok, out int outTok)) { rec.InputTokens = inTok; rec.OutputTokens = outTok; rec.CostUsd = CostModel.Estimate(inTok, outTok); _run.TotalCostUsd += rec.CostUsd.Value; }
            else if (_provider is IUsageReporting) _run.CostKnown = false;

            _pendingRecord = rec; _pendingResponse = response;
        }

        /// <summary>The provider's answer is applied (RequestAndApplyDecisionRoutine after the yield): constraints, record, gate.OnApplied.</summary>
        private void ApplyPending()
        {
            if (_pendingRecord == null) return;
            DecisionRecord rec = _pendingRecord; DecisionResponse response = _pendingResponse;
            _pendingRecord = null; _pendingResponse = null;
            SupportLevel before = rec.LevelBefore;

            ApplyDecision(rec, response);
            _run.Decisions.Add(rec);
            switch (response.ParsedAction)
            {
                case SupportAction.INCREASE_SUPPORT: _run.Increase++; break;
                case SupportAction.DECREASE_SUPPORT: _run.Decrease++; break;
                default: _run.Keep++; break;
            }
            if (response.UsedFallback) _run.Fallbacks++;

            bool changed = _level != before;
            _gate.OnApplied(changed, _clock.NowSeconds);          // DecisionScheduler.OnApplied
            Log($"[{T()}] decision #{rec.DecisionIndex} {rec.Trigger}: {response.ParsedAction} ({response.Status}) {before} -> {_level}" + (rec.ConstraintApplied != null ? " [" + rec.ConstraintApplied + "]" : ""));
        }

        /// <summary>AssemblySessionController.ApplyDecision + SupportLevelController.ApplyAction + HandleSupportChanged.</summary>
        private void ApplyDecision(DecisionRecord rec, DecisionResponse response)
        {
            SupportLevel before = _level;
            bool applied = false;
            string constraintApplied = null;

            if (response.Status == ConstraintStatus.Ok)
            {
                bool saturates = SupportLevels.WouldSaturate(before, response.ParsedAction);
                SupportLevel target = SupportLevels.Apply(before, response.ParsedAction);
                if (target != before) { ChangeLevel(target, "ai_decision", _decisionIndex); applied = true; }
                if (saturates) { response.Status = ConstraintStatus.SaturatedAtBoundary; constraintApplied = "boundary_clamp"; }
            }
            else constraintApplied = "fallback:" + response.Status;

            SupportLevel after = _level;
            rec.Applied = applied;
            rec.LevelAfter = after;
            rec.Status = response.Status;
            rec.ConstraintApplied = constraintApplied;
            rec.AppliedAction = applied && after != before ? response.ParsedAction : SupportAction.KEEP_SUPPORT;
            _decisionIndex++;
        }

        /// <summary>SupportLevelController.SetSupportLevel -> AssemblySessionController.HandleSupportChanged (+ ledger).</summary>
        private void ChangeLevel(SupportLevel to, string reason, int decisionIndex)
        {
            SupportLevel from = _level;
            if (to == from) return;
            _level = to;
            _supportChangesOnStep++; _supportChangesTotal++;
            _errorsSinceChange = 0; _attemptsSinceChange = 0; _successesSinceChange = 0;
            _lastChangeStepMs = _stepMs;
            _ledger.Add((int)to > (int)from ? TaskEventKind.SupportIncrease : TaskEventKind.SupportDecrease, _stepMs, _actionId, to.ToString());

            var tr = new SupportTransition { SimTimeS = _clock.NowSeconds, StepId = _stepId, From = from, To = to, Reason = reason, DecisionIndex = decisionIndex, EventIndex = _eventIndex };
            _run.Transitions.Add(tr);
            if ((int)to > (int)_run.MaxLevel) _run.MaxLevel = to;
            if (reason == "ai_decision")
            {
                _run.AiChanges++;
                bool up = (int)to > (int)from;
                if (up) { _run.AiIncreases++; if (!_run.FirstIncreaseS.HasValue) { _run.FirstIncreaseS = _clock.NowSeconds; _run.FirstIncreaseEvent = _eventIndex; } }
                else { _run.AiDecreases++; if (!_run.FirstDecreaseS.HasValue) { _run.FirstDecreaseS = _clock.NowSeconds; _run.FirstDecreaseEvent = _eventIndex; } }
                // Oscillation: an AI change that reverses the previous AI change on the same step.
                SupportTransition prev = null;
                for (int i = _run.Transitions.Count - 2; i >= 0; i--) if (_run.Transitions[i].Reason == "ai_decision") { prev = _run.Transitions[i]; break; }
                if (prev != null && prev.StepId == tr.StepId && ((int)prev.To > (int)prev.From) != up) _run.Oscillations++;
            }
            Sample("support_change:" + reason);
        }

        // ------------------------------------------------------------------ context mirror

        /// <summary>AssemblySessionController.BuildDecisionContext, with the physiology profile in place of the live provider snapshot.</summary>
        private DecisionContext BuildDecisionContext()
        {
            var ctx = new DecisionContext
            {
                ParticipantId = "VP_" + _run.ScenarioId, SessionId = _run.ScenarioId + "/" + _run.PhysiologyVariant + "/r" + _run.Repetition, DecisionIndex = _decisionIndex,
                StepIndex = _stepIndex, StepId = _stepId, StepCount = _script.StepCount, TaskComplexity = _taskComplexity, OperatorExperience = _script.OperatorExperience,
                StepElapsedMs = _stepMs, OverallElapsedMs = _overallMs, StepExpectedMs = _expectedMs,
                AttemptsOnStep = _attemptsOnStep, ErrorsOnStep = _errorsOnStep, ErrorsTotal = _errorsTotal,
                CurrentSupportLevel = _level, SupportChangesOnStep = _supportChangesOnStep, SupportChangesTotal = _supportChangesTotal,
                ErrorsSinceLastSupportChange = _errorsSinceChange, AttemptsSinceLastSupportChange = _attemptsSinceChange, SuccessfulActionsSinceLastSupportChange = _successesSinceChange,
                TimeSinceLastErrorMs = _lastErrorStepMs >= 0f ? (float?)Math.Max(0f, _stepMs - _lastErrorStepMs) : null,
                TimeSinceLastSupportChangeMs = _lastChangeStepMs >= 0f ? (float?)Math.Max(0f, _stepMs - _lastChangeStepMs) : null,
                Physiological = PhysiologyProfiles.Sample(_physiologyProfile, _clock.NowSeconds)
            };
            _ledger.Fill(ctx, _stepMs, (float)_cfg.Context.RecentWindowMs, _cfg.Context.RecentEventsMax);
            return ctx;
        }

        // ------------------------------------------------------------------ helpers

        private void Sample(string label)
        {
            _run.Trajectory.Add(new TrajectorySample { SimTimeS = _clock.NowSeconds, Level = _level, Label = label, EventIndex = _eventIndex });
        }

        private string T() { return _clock.NowSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s"; }
        private void Log(string s) { _run.GateLog.Add(s); if (_log != null) _log(s); }

        /// <summary>The AI input minus the per-run identity fields, so identical situations across repetitions hash alike.</summary>
        public static string AiInputKeyOf(string aiInput)
        {
            if (aiInput == null) return "";
            Dictionary<string, object> o = MiniJson.ParseObject(aiInput);
            if (o == null) return aiInput;
            o.Remove("participant_id"); o.Remove("session_id");
            return MiniJson.Serialize(o);
        }
    }
}
