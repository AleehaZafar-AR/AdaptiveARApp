// File: AssemblySessionController.cs
// Owns the research session: timing, counters, logging, and the decision seam.
//
// It is the only place that knows about ALL of steps, support level, validation and
// logging at once. StepRunner still owns the step index and SupportLevelController
// still owns the level; this component observes both and records what happened.
//
// The AI seam is complete today even though no provider exists:
//   BuildDecisionContext()  - the exact context a provider would be given
//   ApplyDecision(response) - enforces the constrained action set, applies the
//                             result, and logs context + raw + parsed + latency +
//                             constraint status + resulting level
// Tomorrow's provider calls those two methods and nothing here changes.

using System;
using AdaptiveAR.Audio;
using AdaptiveAR.Decision;
using AdaptiveAR.Logging;
using AdaptiveAR.Support;
using AdaptiveAR.XR;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class AssemblySessionController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private StepRunner stepRunner;
        [SerializeField] private SupportLevelController supportLevel;
        [SerializeField] private StepValidator validator;
        [SerializeField] private SessionLogger logger;

        [Tooltip("Single source of truth for progress. Every display subscribes to it.")]
        [SerializeField] private WorkflowState workflow;

        [Tooltip("Optional. Completes ToolAction substeps when a tool is used on a fastener.")]
        [SerializeField] private ToolInteraction toolInteraction;

        [Tooltip("Shows and clears per-action guidance. Found on this object when empty.")]
        [SerializeField] private StepPresenter presenter;

        [Tooltip("Source of the workspace placement events that are logged. Found on this object when empty.")]
        [SerializeField] private StepManager stepManager;

        [Header("Participant")]
        [Tooltip("Recorded as a decision-layer input. Free text, e.g. novice / experienced.")]
        [SerializeField] private string operatorExperience = "unspecified";

        [Header("Session")]
        [Tooltip("Open the log as soon as the app starts, so the marker-search period is captured too.")]
        [SerializeField] private bool beginSessionOnStart = true;

        [Tooltip("Close the session automatically when the last step is completed.")]
        [SerializeField] private bool endSessionOnComplete = true;

        [Header("Completion")]
        [Tooltip("Shown on the caption when every step is done.")]
        [TextArea(2, 4)]
        [SerializeField] private string completionMessage = "Assembly complete. Thank you.";

        [Tooltip("Caption to overwrite on completion. Without this the last step's text " +
                 "would stay on screen after the sequence finishes.")]
        [SerializeField] private TMPro.TextMeshProUGUI captionText;

        [Header("Feedback")]
        [Tooltip("Spoken on a valid placement.")]
        [SerializeField] private string successPhrase = "Correct.";

        [Tooltip("Spoken when a part that cannot satisfy the current action is picked up.")]
        [SerializeField] private string wrongPartPhrase = "That is not the required component.";

        [Tooltip("Controller pulses on grab, wrong part and success. Never blocks progression.")]
        [SerializeField] private bool haptics = true;

        [Header("Debug")]
        [SerializeField] private bool logToConsole = true;

        // --- session state ---
        public bool SessionActive { get; private set; }
        public bool SequenceFinished { get; private set; }

        public float OverallElapsedMs { get { return _overallMs; } }
        public float StepElapsedMs { get { return _stepMs; } }
        public int AttemptsOnStep { get { return _attemptsOnStep; } }
        public int ErrorsOnStep { get { return _errorsOnStep; } }
        public int ErrorsTotal { get { return _errorsTotal; } }
        public int SupportChangesTotal { get { return _supportChangesTotal; } }

        /// <summary>Raised when the whole sequence is finished. UI can subscribe.</summary>
        public event Action OnSessionComplete;

        /// <summary>Raised whenever counters change, so HUDs can refresh without polling every field.</summary>
        public event Action OnStateChanged;

        private float _overallMs;
        private float _stepMs;
        private int _attemptsOnStep;
        private int _errorsOnStep;
        private int _errorsTotal;
        private int _supportChangesOnStep;
        private int _supportChangesTotal;
        private int _decisionIndex;

        private bool _stepOpen;
        private StepData _currentStep;
        private int _currentIndex = -1;
        private bool _placementSubscribed;
        private bool _currentActionArmed;

        /// <summary>
        /// Latest physiological reading, or null when nothing is attached.
        /// A future BLE source sets this; nothing today writes to it, and the logger
        /// records every field as null rather than inventing a value.
        /// </summary>
        public PhysiologicalSample LatestPhysiological { get; set; }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        private void OnEnable()
        {
            if (stepRunner != null)
            {
                stepRunner.OnStepChanged += HandleStepChanged;
                stepRunner.OnSequenceComplete += HandleSequenceComplete;
            }

            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged += HandleSupportChanged;

            if (validator != null)
            {
                validator.OnAttemptEvaluated += HandleAttempt;
                validator.OnStepValidated += HandleStepValidated;
                validator.OnPartGrabbed += HandlePartGrabbed;
                validator.OnPartReleased += HandlePartReleased;
                validator.OnWrongPartGrabbed += HandleWrongPartGrabbed;
                validator.OnWrongPartReleased += HandleWrongPartReleased;
                validator.OnComponentDropped += HandleComponentDropped;
            }

            if (toolInteraction != null)
                toolInteraction.OnToolActionCompleted += HandleToolActionCompleted;

            if (presenter == null) presenter = GetComponent<StepPresenter>();
            if (stepManager == null) stepManager = GetComponent<StepManager>();

            if (stepManager != null && stepManager.Placement != null && !_placementSubscribed)
            {
                stepManager.Placement.OnPlaced += HandleWorkspacePlaced;
                _placementSubscribed = true;
            }
        }

        private void OnDisable()
        {
            if (stepRunner != null)
            {
                stepRunner.OnStepChanged -= HandleStepChanged;
                stepRunner.OnSequenceComplete -= HandleSequenceComplete;
            }

            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged -= HandleSupportChanged;

            if (validator != null)
            {
                validator.OnAttemptEvaluated -= HandleAttempt;
                validator.OnStepValidated -= HandleStepValidated;
                validator.OnPartGrabbed -= HandlePartGrabbed;
                validator.OnPartReleased -= HandlePartReleased;
                validator.OnWrongPartGrabbed -= HandleWrongPartGrabbed;
                validator.OnWrongPartReleased -= HandleWrongPartReleased;
                validator.OnComponentDropped -= HandleComponentDropped;
            }

            if (toolInteraction != null)
                toolInteraction.OnToolActionCompleted -= HandleToolActionCompleted;

            if (stepManager != null && stepManager.Placement != null)
                stepManager.Placement.OnPlaced -= HandleWorkspacePlaced;
            _placementSubscribed = false;
        }

        private void HandlePartGrabbed(string partKey, Transform part)
        {
            if (logger != null) logger.LogComponentGrabbed(partKey);
            if (haptics && part != null) HapticFeedback.Pulse(HapticFeedback.Kind.Grab, part.position);
        }

        private void HandlePartReleased(string partKey, Transform part)
        {
            if (logger != null) logger.LogComponentReleased(partKey);
        }

        private void HandleWrongPartGrabbed(string partKey, Transform part)
        {
            // A wrong component in the hand is a behavioural error: one per grab, never
            // per frame (the validator raises this on the grab transition only).
            _errorsOnStep++;
            _errorsTotal++;
            PushEnvelope();

            if (logger != null) logger.LogWrongComponent(partKey, validator != null ? validator.PartKey : null);
            if (haptics && part != null) HapticFeedback.Pulse(HapticFeedback.Kind.WrongPart, part.position);

            var speech = InstructionSpeech.Ensure(null);
            if (speech != null) speech.SpeakFeedback(wrongPartPhrase);
        }

        private void HandleWrongPartReleased(string partKey, Transform part)
        {
            if (logger != null) logger.LogNote("wrong_component_released:" + partKey);
        }

        /// <summary>The correct part was let go far from the target. An interaction event, not an error.</summary>
        private void HandleComponentDropped(string partKey, Transform part)
        {
            if (logger != null) logger.LogComponentDropped(partKey, validator != null ? validator.PartKey : null);
        }

        private void HandleWorkspacePlaced(bool reposition)
        {
            var p = stepManager != null ? stepManager.Placement : null;
            if (logger == null || p == null) return;

            logger.LogWorkspacePlaced(p.ActiveProvider.ToString(), p.LastPlacedPosition,
                                      p.LastSurfaceNormal, p.LastNormalConfidence, reposition);
        }

        /// <summary>A tool action satisfies its substep the same way a placement does.</summary>
        private void HandleToolActionCompleted()
        {
            if (workflow != null) CompleteActionAndAdvance("tool_action");
        }

        private void Start()
        {
            // The placement provider may be created in StepManager.Awake after this
            // component's OnEnable ran; make sure its events are logged either way.
            if (!_placementSubscribed && stepManager != null && stepManager.Placement != null)
            {
                stepManager.Placement.OnPlaced += HandleWorkspacePlaced;
                _placementSubscribed = true;
            }

            if (beginSessionOnStart)
                BeginSession();
        }

        public void BeginSession()
        {
            if (SessionActive) return;

            SessionActive = true;
            SequenceFinished = false;
            _overallMs = 0f;

            if (logger != null)
                logger.BeginSession();

            PushEnvelope();
        }

        public void EndSession(string reason)
        {
            if (!SessionActive) return;

            SessionActive = false;

            if (logger != null)
                logger.EndSession(reason);
        }

        private void Update()
        {
            if (!SessionActive || SequenceFinished) return;

            float dt = Time.deltaTime * 1000f;
            _overallMs += dt;

            if (_stepOpen)
                _stepMs += dt;
        }

        // =====================================================================
        // Step tracking
        // =====================================================================

        /// <summary>True when the participant is allowed to move on. Gates the Next control.</summary>
        public bool CanAdvance
        {
            get
            {
                if (!SessionActive || SequenceFinished) return false;
                if (workflow == null) return true;                     // no model: never gate

                AssemblyAction action = workflow.CurrentAction;
                if (action == null) return true;                       // stage without actions
                if (!action.enabled) return true;                      // designed, not performable
                if (!action.RequiresPhysicalValidation) return true;    // read and acknowledge
                if (!_currentActionArmed) return true;                  // cannot be validated: confirm by hand

                return workflow.CurrentActionComplete;
            }
        }

        /// <summary>
        /// READ state: the current action is an acknowledgement (read, then Continue).
        /// Otherwise the action is physical: INTERACT state, no Continue.
        /// </summary>
        public bool IsReadState
        {
            get
            {
                if (!SessionActive || SequenceFinished || workflow == null) return false;
                AssemblyAction a = workflow.CurrentAction;
                return a != null && a.enabled && !a.RequiresPhysicalValidation;
            }
        }

        /// <summary>
        /// Back is valid only from a physical action whose preceding enabled action in the
        /// same stage is an instruction. It re-reads that instruction; it never undoes a
        /// placement or touches completed-action state.
        /// </summary>
        public bool CanGoBack
        {
            get { return PreviousInstructionIndex() >= 0; }
        }

        private int PreviousInstructionIndex()
        {
            if (!SessionActive || SequenceFinished || workflow == null) return -1;
            StepData stage = workflow.CurrentStage;
            AssemblyAction current = workflow.CurrentAction;
            if (stage == null || stage.actions == null || current == null) return -1;
            if (!current.RequiresPhysicalValidation) return -1;      // already reading

            for (int i = workflow.ActionIndex - 1; i >= 0; i--)
            {
                AssemblyAction a = stage.actions[i];
                if (a == null || !a.enabled) continue;
                return a.RequiresPhysicalValidation ? -1 : i;         // stop at the first enabled one
            }
            return -1;
        }

        /// <summary>Re-enters the preceding instruction. Safe: completed actions stay completed.</summary>
        public void GoBackToInstruction()
        {
            int index = PreviousInstructionIndex();
            if (index < 0) return;

            if (logger != null) logger.LogNote("back_to_instruction:" + (workflow.CurrentAction != null ? workflow.CurrentAction.Id : ""));
            if (validator != null) validator.Clear();
            EnterAction(index);
        }

        /// <summary>Logged by the card when Continue is pressed in the READ state.</summary>
        public void NoteInstructionAcknowledged()
        {
            if (!IsReadState || logger == null || workflow == null || workflow.CurrentAction == null) return;
            logger.LogNote("instruction_acknowledged:" + workflow.CurrentAction.Id);
        }

        /// <summary>Short reason the Next control is unavailable, for the participant card.</summary>
        public string BlockedReason
        {
            get
            {
                if (CanAdvance) return null;
                AssemblyAction a = workflow != null ? workflow.CurrentAction : null;
                return a != null && a.kind == ActionKind.Fasten
                    ? "Fit the fastener to continue"
                    : "Place the part to continue";
            }
        }

        // ---------------- action lifecycle ----------------

        /// <summary>
        /// Enters one action: tears down the previous one, arms validation, logs it.
        /// Everything owned by the previous action is cleared here, which is what stops
        /// stale instructions and orphaned ghosts surviving into the next action.
        /// </summary>
        private void EnterAction(int index)
        {
            if (workflow == null) return;

            StepData stage = workflow.CurrentStage;
            if (stage == null) return;

            int resolved = stage.NextEnabledActionIndex(index);

            if (resolved < 0)
            {
                CompleteStageAndAdvance();
                return;
            }

            workflow.EnterAction(resolved);
            AssemblyAction action = workflow.CurrentAction;

            _currentActionArmed = false;
            if (validator != null)
            {
                _currentActionArmed = validator.BeginAction(action);

                if (!_currentActionArmed && action != null && action.RequiresPhysicalValidation && logToConsole)
                    Debug.LogWarning("[Session] Action '" + action.Id + "' wants validation but could " +
                                     "not be armed; it must be confirmed manually.");
            }

            // Guidance for THIS action replaces whatever the previous action showed.
            if (presenter != null)
                presenter.PresentAction(action);

            if (logger != null && action != null)
                logger.LogActionEnter(resolved, action.Id, action.kind.ToString(),
                                      action.enabled, action.disabledReason);

            RaiseStateChanged();
        }

        /// <summary>Marks the active action done and moves on, or finishes the stage.</summary>
        private void CompleteActionAndAdvance(string reason)
        {
            if (workflow == null) { AdvanceStep(reason); return; }

            AssemblyAction action = workflow.CurrentAction;
            workflow.CompleteCurrentAction();

            if (logger != null && action != null)
                logger.LogActionComplete(workflow.ActionIndex, action.Id, _stepMs, reason);

            if (validator != null) validator.Clear();

            StepData stage = workflow.CurrentStage;
            int next = stage != null ? stage.NextEnabledActionIndex(workflow.ActionIndex + 1) : -1;

            if (next >= 0) EnterAction(next);
            else CompleteStageAndAdvance();
        }

        private void CompleteStageAndAdvance()
        {
            if (workflow != null)
                workflow.CompleteStage(workflow.StageIndex);

            if (presenter != null)
                presenter.PresentAction(null);

            AdvanceStep("stage_complete");
        }

        private void HandleStepChanged(StepData step, int index, string reason)
        {
            // Close the outgoing step first, so its duration is recorded before the new
            // one resets the timer.
            if (_stepOpen && _currentStep != null)
                CloseStep(reason);

            _currentStep = step;
            _currentIndex = index;
            _stepMs = 0f;
            _attemptsOnStep = 0;
            _errorsOnStep = 0;
            _supportChangesOnStep = 0;
            _stepOpen = true;

            PushEnvelope();

            if (logger != null)
            {
                logger.LogStepEnter(index, step.StepIdentifier, stepRunner != null ? stepRunner.StepCount : 0,
                                    step.taskComplexity, ExpectedMsOrNull(step));
            }

            // Enter the stage in the authoritative model, then its first real action.
            if (workflow != null)
            {
                workflow.SetPhase(WorkflowPhase.Assembly);
                workflow.EnterStage(index);
                EnterAction(0);
            }
            else if (validator != null)
            {
                validator.Clear();
            }

            RaiseStateChanged();
        }

        private void CloseStep(string reason)
        {
            if (logger != null && _currentStep != null)
            {
                logger.LogStepComplete(_currentIndex, _currentStep.StepIdentifier,
                                       _stepMs, _attemptsOnStep, _errorsOnStep, reason);
            }
            _stepOpen = false;
        }

        private static float? ExpectedMsOrNull(StepData step)
        {
            if (step == null || step.expectedDurationSeconds <= 0f) return null;
            return step.expectedDurationSeconds * 1000f;
        }

        // =====================================================================
        // Validation
        // =====================================================================

        private void HandleAttempt(bool success, float posErr, float rotErr, string trigger)
        {
            _attemptsOnStep++;

            if (!success)
            {
                _errorsOnStep++;
                _errorsTotal++;
            }

            PushEnvelope();

            if (logger != null && _currentStep != null)
            {
                float posTol = validator != null ? validator.PositionTolerance : _currentStep.positionToleranceMeters;
                float rotTol = validator != null ? validator.RotationTolerance : _currentStep.rotationToleranceDegrees;

                // One line per ATTEMPT (zone entered and left, or released inside it) or
                // per success - never per release far away, never per frame.
                logger.LogValidationAttempt(_attemptsOnStep, success, posErr, rotErr,
                                            posTol, rotTol,
                                            validator != null ? validator.ActiveInstanceKey ?? validator.PartKey : null,
                                            validator != null ? validator.TargetKey : null,
                                            trigger,
                                            success ? "placement_success" : (validator != null ? validator.LastErrorType : null));

                if (success)
                    logger.LogComponentLocked(validator != null ? validator.ActiveInstanceKey : null, posErr, rotErr,
                                              validator != null ? validator.PartKey : null,
                                              validator != null && validator.CurrentPart != null ? validator.CurrentPart.name : null);
            }

            if (success)
            {
                if (haptics && validator != null && validator.CurrentPart != null)
                    HapticFeedback.Pulse(HapticFeedback.Kind.Success, validator.CurrentPart.position);

                var speech = InstructionSpeech.Ensure(null);
                if (speech != null) speech.SpeakFeedback(successPhrase);
            }

            RaiseStateChanged();
        }

        private void HandleStepValidated()
        {
            // A validated placement completes the current ACTION. The stage advances only
            // when it runs out of actions.
            if (workflow != null) CompleteActionAndAdvance("validation_passed");
            else AdvanceStep("validation_passed");
        }

        /// <summary>
        /// Advances the sequence. Hooked to the confirm button so a step can be completed
        /// by hand when validation is not used, or when a researcher needs to move on.
        /// </summary>
        public void AdvanceStep(string reason)
        {
            if (!SessionActive || SequenceFinished || stepRunner == null) return;

            // Never let a confirm press start the sequence. The sequence begins only when
            // StepManager has locked the workspace (placed, or the marker found), so pressing
            // confirm beforehand must do nothing rather than skip the anchoring step.
            //
            // This is logged rather than silent: an unexplained dead button on device is
            // very hard to tell apart from a broken one, which is exactly what happened
            // when the Begin control went missing.
            if (!stepRunner.HasStarted)
            {
                if (logToConsole)
                    Debug.Log("[Session] Advance ignored: the sequence has not started yet. " +
                              "Place the workspace and press Begin first.");
                return;
            }

            if (stepRunner.IsOnLastStep)
            {
                CloseStep(reason);
                stepRunner.Advance(reason); // raises OnSequenceComplete
                return;
            }

            stepRunner.Advance(reason);
        }

        /// <summary>
        /// Parameterless overload for a UI Button. Refuses to skip unfinished physical work:
        /// the participant cannot press past a placement they have not made.
        /// </summary>
        public void AdvanceStepManually()
        {
            if (!CanAdvance)
            {
                if (logger != null) logger.LogNote("advance_blocked");
                if (logToConsole) Debug.Log("[Session] Advance blocked: " + BlockedReason);
                RaiseStateChanged();
                return;
            }

            if (workflow != null) CompleteActionAndAdvance("manual_confirm");
            else AdvanceStep("manual_confirm");
        }

        /// <summary>Skips the current step without marking it validated. Researcher control.</summary>
        public void SkipStep()
        {
            if (workflow != null) CompleteActionAndAdvance("researcher_skip");
            else AdvanceStep("researcher_skip");
        }

        private void HandleSequenceComplete()
        {
            if (SequenceFinished) return;

            SequenceFinished = true;

            if (workflow != null) workflow.SetPhase(WorkflowPhase.Complete);

            if (validator != null)
                validator.Clear();

            if (logToConsole)
                Debug.Log($"[Session] Assembly complete. " +
                          $"Total {_overallMs / 1000f:F1} s, {_errorsTotal} error(s), " +
                          $"{_supportChangesTotal} support change(s).");

            if (logger != null)
                logger.LogNote("sequence_complete");

            // The ParticipantCard owns the card text when present; writing here too would
            // mean two writers for one field. The completion screen shows the message.
            if (captionText != null && (presenter == null || !presenter.TextOwnedByParticipantCard))
                captionText.text = completionMessage;

            if (validator != null) validator.Clear();
            if (presenter != null) presenter.ClearGuidance();

            OnSessionComplete?.Invoke();
            RaiseStateChanged();

            if (endSessionOnComplete)
                EndSession("sequence_complete");
        }

        public string CompletionMessage { get { return completionMessage; } }

        // =====================================================================
        // Support level
        // =====================================================================

        private void HandleSupportChanged(SupportLevel from, SupportLevel to, string reason)
        {
            _supportChangesOnStep++;
            _supportChangesTotal++;

            PushEnvelope();

            if (logger != null)
                logger.LogSupportChange(from, to, null, SourceFromReason(reason), reason, false);

            RaiseStateChanged();
        }

        private static SupportChangeSource SourceFromReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return SupportChangeSource.Manual;

            switch (reason)
            {
                case "step_default": return SupportChangeSource.StepDefault;
                case "debug_input": return SupportChangeSource.DebugInput;
                case "ai_decision": return SupportChangeSource.DecisionProvider;
                case "fallback": return SupportChangeSource.Fallback;
                default: return SupportChangeSource.Manual;
            }
        }

        // =====================================================================
        // Decision seam - complete today, unused until a provider exists
        // =====================================================================

        /// <summary>
        /// Builds the exact context a decision provider would receive. Logged verbatim as
        /// the "AI input", so it must reflect real state and never fabricate a value.
        /// </summary>
        public DecisionContext BuildDecisionContext()
        {
            var ctx = new DecisionContext
            {
                ParticipantId = logger != null ? logger.ParticipantId : null,
                SessionId = logger != null ? logger.SessionId : null,
                DecisionIndex = _decisionIndex,

                StepIndex = _currentIndex,
                StepId = _currentStep != null ? _currentStep.StepIdentifier : null,
                StepCount = stepRunner != null ? stepRunner.StepCount : 0,
                TaskComplexity = _currentStep != null ? _currentStep.taskComplexity : 0,
                OperatorExperience = operatorExperience,

                StepElapsedMs = _stepMs,
                OverallElapsedMs = _overallMs,
                StepExpectedMs = ExpectedMsOrNull(_currentStep),

                AttemptsOnStep = _attemptsOnStep,
                ErrorsOnStep = _errorsOnStep,
                ErrorsTotal = _errorsTotal,

                CurrentSupportLevel = supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal,
                SupportChangesOnStep = _supportChangesOnStep,
                SupportChangesTotal = _supportChangesTotal,

                Physiological = LatestPhysiological
            };

            return ctx;
        }

        /// <summary>
        /// Applies a provider's response under the constrained action set, and records the
        /// whole exchange. Returns the level in force afterwards.
        ///
        /// A response that violates the constraint is NOT silently corrected: the violation
        /// is recorded and the safe fallback (KEEP_SUPPORT) applies, because constraint
        /// compliance is itself a measured outcome.
        /// </summary>
        public SupportLevel ApplyDecision(DecisionContext context, DecisionResponse response)
        {
            SupportLevel before = supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal;

            if (response == null)
            {
                response = DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, null, null);
            }

            bool applied = false;

            if (response.Status == ConstraintStatus.Ok && supportLevel != null)
            {
                bool saturates = SupportLevels.WouldSaturate(before, response.ParsedAction);
                applied = supportLevel.ApplyAction(response.ParsedAction, "ai_decision");

                if (saturates)
                    response.Status = ConstraintStatus.SaturatedAtBoundary;
            }
            // Any non-Ok status leaves the level untouched: KEEP_SUPPORT is the fallback.

            SupportLevel after = supportLevel != null ? supportLevel.CurrentLevel : before;

            _decisionIndex++;

            if (logger != null)
                logger.LogDecision(context, response, before, after, applied);

            RaiseStateChanged();
            return after;
        }

        /// <summary>
        /// Convenience path for a provider: build context, ask, apply, log. Never throws -
        /// a provider failure becomes a recorded fallback rather than a lost session.
        /// </summary>
        public SupportLevel RequestAndApplyDecision(IDecisionProvider provider)
        {
            DecisionContext ctx = BuildDecisionContext();

            if (provider == null)
                return ApplyDecision(ctx, DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, null, null));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            DecisionResponse response;

            try
            {
                response = provider.RequestDecision(ctx);
            }
            catch (Exception e)
            {
                sw.Stop();
                Debug.LogWarning($"[Session] Decision provider threw: {e.Message}", this);
                response = DecisionResponse.Failed(ConstraintStatus.ProviderError,
                                                   (float)sw.Elapsed.TotalMilliseconds,
                                                   provider.ProviderId, null);
                return ApplyDecision(ctx, response);
            }

            sw.Stop();

            if (response == null)
            {
                response = DecisionResponse.Failed(ConstraintStatus.ProviderError,
                                                   (float)sw.Elapsed.TotalMilliseconds,
                                                   provider.ProviderId, null);
            }
            else if (response.LatencyMs <= 0f)
            {
                response.LatencyMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            return ApplyDecision(ctx, response);
        }

        /// <summary>Records a physiological reading and keeps it as the latest context input.</summary>
        public void SubmitPhysiological(PhysiologicalSample sample)
        {
            LatestPhysiological = sample;

            if (logger != null && sample != null)
                logger.LogPhysiological(sample);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void PushEnvelope()
        {
            if (logger == null) return;

            logger.UpdateEnvelope(
                _currentIndex,
                _currentStep != null ? _currentStep.StepIdentifier : null,
                supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal,
                _stepMs, _attemptsOnStep, _errorsOnStep, _errorsTotal);
        }

        private void RaiseStateChanged()
        {
            OnStateChanged?.Invoke();
        }
    }
}
