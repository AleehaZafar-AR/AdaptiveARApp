// File: SessionLogger.cs
// Structured session recording for the assembly study.
//
// Writes JSON Lines (one self-contained JSON object per line) to
// Application.persistentDataPath, which is retrievable with adb pull and readable
// without Unity (CLAUDE.md 4.3).
//
// Every line carries the same identity and state envelope, so any single line is
// interpretable on its own and the file can be loaded straight into pandas or R
// with pd.read_json(path, lines=True).
//
// The decision event is fully specified TODAY even though no AI provider exists,
// so tomorrow's layer records context, raw output, parsed action, constraint
// status, latency and resulting level without the schema changing.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AdaptiveAR.Decision;
using AdaptiveAR.Support;
using UnityEngine;

namespace AdaptiveAR.Logging
{
    public class SessionLogger : MonoBehaviour
    {
        /// <summary>
        /// Bump ONLY when the meaning of an existing field changes. Adding new fields
        /// does not require a bump, because consumers key by name.
        /// </summary>
        public const string SchemaVersion = "adaptivear.session.v2";   // v2: + ppi_sample, physio_window, physio_connection; session_start + physio_* fields (additive)

        public static class Events
        {
            public const string SessionStart = "session_start";
            public const string SessionEnd = "session_end";
            public const string StepEnter = "step_enter";
            public const string StepComplete = "step_complete";
            public const string SupportChange = "support_change";
            public const string ValidationAttempt = "validation_attempt";
            public const string Decision = "decision";
            public const string Physiological = "physiological";
            public const string Note = "note";

            // --- refined workflow ---
            public const string OnboardingEnter = "onboarding_enter";
            public const string OnboardingComplete = "onboarding_complete";
            public const string AssemblyStart = "assembly_start";
            public const string ActionEnter = "action_enter";
            public const string ActionComplete = "action_complete";
            public const string ComponentGrabbed = "component_grabbed";
            public const string ComponentReleased = "component_released";
            public const string ComponentLocked = "component_locked";
            public const string HelpRequested = "help_requested";
            public const string FastenerAction = "fastener_action";

            // --- workspace registration (surface placement replaced the marker) ---
            public const string WorkspacePlaced = "workspace_placed";

            // --- a part that cannot satisfy the current action was picked up ---
            public const string WrongComponentGrabbed = "wrong_component_grabbed";

            // --- the correct part was released away from its target: interaction, not error ---
            public const string ComponentDropped = "component_dropped";

            // --- prototype shortcut: the remaining pistons were fitted automatically ---
            public const string RemainingPistonsAutoCompleted = "remaining_pistons_auto_completed";

            // --- physiology (schema v2): every raw PPI sample, window summaries, BLE/stream state ---
            public const string PpiSample = "ppi_sample";
            public const string PhysioWindow = "physio_window";
            public const string PhysioConnection = "physio_connection";

            // --- pre-task sensor initialisation / stabilisation screen (not a validated resting baseline) ---
            public const string SensorSetupEnter = "sensor_setup_enter";
            public const string SensorStreamReady = "sensor_stream_ready";
            public const string SensorStabilizationStart = "sensor_stabilization_start";
            public const string SensorStabilizationComplete = "sensor_stabilization_complete";
            public const string SensorSetupWaiting = "sensor_setup_waiting";
            public const string SensorSetupReady = "sensor_setup_ready";
            public const string SensorSetupContinue = "sensor_setup_continue";
            public const string SensorSetupSkipped = "sensor_setup_skipped";

            // --- the decision context as a provider would receive it NOW; no decision is made ---
            public const string DecisionContextProbe = "decision_context_probe";
            public const string DecisionTrigger = "decision_trigger";
            public const string DecisionConfig = "decision_config";

            // --- participant-facing cue after an applied upward AI support change (message, tone, instruction replay) ---
            public const string SupportTransitionCue = "support_transition_cue";

            // --- diagnostic snapshot of a part at a snap / lock / join / hand-over / guard event (PartWatch) ---
            public const string PartLifecycle = "part_lifecycle";
        }

        /// <summary>Set by the physiology layer before the session opens; written into session_start.</summary>
        public string PhysioSource { get; set; }
        public string PhysioAcceptRules { get; set; }
        public string PhysioBridgeVersion { get; set; }

        [Header("Participant")]
        [Tooltip("Set per participant before the session. Also settable at runtime via SetParticipant().")]
        [SerializeField] private string participantId = "P00";

        [Tooltip("Free-form condition label recorded on every line, e.g. a study arm.")]
        [SerializeField] private string conditionLabel = "";

        [Header("Output")]
        [Tooltip("Folder under Application.persistentDataPath.")]
        [SerializeField] private string subFolder = "AdaptiveAR/sessions";

        [Tooltip("Flush to disk after every line. Slower, but a crashed session keeps its data.")]
        [SerializeField] private bool flushEveryLine = true;

        [Tooltip("Also echo each line to the Unity console (visible over logcat).")]
        [SerializeField] private bool echoToConsole = false;

        [Header("State")]
        [SerializeField] private bool logToFile = true;

        public string ParticipantId { get { return participantId; } }
        public string SessionId { get; private set; }
        public string FilePath { get; private set; }
        public bool IsOpen { get; private set; }

        /// <summary>Milliseconds since the session opened.</summary>
        public float SessionElapsedMs
        {
            get { return IsOpen ? (float)((DateTime.UtcNow - _sessionStartUtc).TotalMilliseconds) : 0f; }
        }

        private StreamWriter _writer;
        private DateTime _sessionStartUtc;
        private int _sequence;
        private readonly List<string> _pendingBeforeOpen = new List<string>();
        private string _actionId;                     // last action entered (alignment for physio_window)

        // --- envelope state, stamped onto every line ---
        private int _stepIndex = -1;
        private string _stepId = null;
        private SupportLevel _supportLevel = SupportLevel.L1_Minimal;
        private float _stepElapsedMs;
        private int _attemptsOnStep;
        private int _errorsOnStep;
        private int _errorsTotal;

        // =====================================================================
        // Session lifecycle
        // =====================================================================

        public void SetParticipant(string id, string condition = null)
        {
            if (!string.IsNullOrEmpty(id)) participantId = id;
            if (condition != null) conditionLabel = condition;
        }

        /// <summary>Opens a new session file. Safe to call once; further calls are ignored.</summary>
        public bool BeginSession(string appBuildLabel = null)
        {
            if (IsOpen) return true;

            _sessionStartUtc = DateTime.UtcNow;
            _sequence = 0;
            SessionId = $"{participantId}_{_sessionStartUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}";

            if (logToFile)
            {
                try
                {
                    string dir = Path.Combine(Application.persistentDataPath, subFolder);
                    Directory.CreateDirectory(dir);
                    FilePath = Path.Combine(dir, SessionId + ".jsonl");

                    _writer = new StreamWriter(FilePath, append: true, encoding: new UTF8Encoding(false));
                    IsOpen = true;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SessionLogger] Could not open log file: {e.Message}. " +
                                   "Continuing without file logging so the session is not lost.", this);
                    logToFile = false;
                    IsOpen = true; // still stamp events to the console
                }
            }
            else
            {
                IsOpen = true;
            }

            var w = NewLine(Events.SessionStart);
            w.Str("app_build", appBuildLabel ?? Application.version);
            w.Str("unity_version", Application.unityVersion);
            w.Str("device_model", SystemInfo.deviceModel);
            w.Str("platform", Application.platform.ToString());
            w.Str("log_path", FilePath);
            w.Str("session_start_utc", _sessionStartUtc.ToString("o", CultureInfo.InvariantCulture));
            w.Str("physio_source", PhysioSource);
            w.Str("physio_accept_rules", PhysioAcceptRules);
            w.Str("physio_bridge_version", PhysioBridgeVersion);
            Write(w);

            // Anything recorded before the file existed is now flushed in order.
            foreach (string line in _pendingBeforeOpen) WriteRaw(line);
            _pendingBeforeOpen.Clear();

            Debug.Log($"[SessionLogger] Session '{SessionId}' started.\n  {FilePath}");
            return true;
        }

        public void EndSession(string reason = "normal")
        {
            if (!IsOpen) return;

            var w = NewLine(Events.SessionEnd);
            w.Str("reason", reason);
            w.Num("total_errors", _errorsTotal);
            w.Num("session_duration_ms", SessionElapsedMs);
            Write(w);

            try
            {
                if (_writer != null)
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SessionLogger] Error closing log: {e.Message}", this);
            }

            _writer = null;
            IsOpen = false;
            Debug.Log($"[SessionLogger] Session ended ({reason}). Log: {FilePath}");
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && _writer != null)
            {
                try { _writer.Flush(); } catch { /* best effort */ }
            }
        }

        private void OnApplicationQuit()
        {
            EndSession("application_quit");
        }

        private void OnDestroy()
        {
            EndSession("destroyed");
        }

        // =====================================================================
        // Envelope updates - called by the session controller
        // =====================================================================

        public void UpdateEnvelope(int stepIndex, string stepId, SupportLevel level,
                                   float stepElapsedMs, int attemptsOnStep, int errorsOnStep, int errorsTotal)
        {
            _stepIndex = stepIndex;
            _stepId = stepId;
            _supportLevel = level;
            _stepElapsedMs = stepElapsedMs;
            _attemptsOnStep = attemptsOnStep;
            _errorsOnStep = errorsOnStep;
            _errorsTotal = errorsTotal;
        }

        // =====================================================================
        // Events
        // =====================================================================

        public void LogStepEnter(int stepIndex, string stepId, int stepCount, int taskComplexity, float? expectedMs)
        {
            var w = NewLine(Events.StepEnter);
            w.Num("step_count", stepCount);
            w.Num("task_complexity", taskComplexity);
            w.NumOrNull("step_expected_ms", expectedMs);
            Write(w);
        }

        public void LogStepComplete(int stepIndex, string stepId, float durationMs,
                                    int attempts, int errors, string reason)
        {
            var w = NewLine(Events.StepComplete);
            w.Num("step_duration_ms", durationMs);
            w.Num("step_attempts", attempts);
            w.Num("step_errors", errors);
            w.Str("reason", reason);
            Write(w);
        }

        public void LogSupportChange(SupportLevel from, SupportLevel to, SupportAction? action,
                                     SupportChangeSource source, string reason, bool saturated, string evidence = null)
        {
            var w = NewLine(Events.SupportChange);
            w.Str("from_level", from.ToString());
            w.Num("from_level_num", (int)from);
            w.Str("to_level", to.ToString());
            w.Num("to_level_num", (int)to);
            w.Str("action", action.HasValue ? action.Value.ToString() : null);
            w.Str("source", source.ToString());
            w.Str("reason", reason);
            w.Bool("saturated", saturated);
            // The since-change vs cumulative evidence the deciding provider was given (AI-driven
            // changes only; null for step defaults and manual changes).
            w.Str("evidence", evidence);
            Write(w);
        }

        /// <summary>
        /// One line per placement ATTEMPT or success. error_type is one of
        /// placement_success, incorrect_position, incorrect_orientation,
        /// incorrect_position_and_orientation; position_ok / orientation_ok give the two
        /// dimensions separately. requested_key is the role the action asked for,
        /// instance_key / instance_name the object actually used. Stage, action, support
        /// level and timestamp are in the envelope every line carries.
        /// </summary>
        public void LogValidationAttempt(int attemptIndex, bool success,
                                         float positionErrorM, float rotationErrorDeg,
                                         float positionToleranceM, float rotationToleranceDeg,
                                         string partKey, string targetKey, string trigger,
                                         string rejectReason = null,
                                         string actionId = null, string requestedKey = null,
                                         string instanceKey = null, string instanceName = null,
                                         bool? positionOk = null, bool? orientationOk = null,
                                         int attemptId = 0, bool? errorCounted = null)
        {
            var w = NewLine(Events.ValidationAttempt);
            w.Num("attempt_id", attemptId);
            if (errorCounted.HasValue) w.Bool("error_counted", errorCounted.Value); else w.Str("error_counted", null);
            w.Str("error_type", rejectReason);
            w.Str("action_id", actionId);
            w.Str("requested_key", requestedKey);
            w.Str("instance_key", instanceKey);
            w.Str("instance_name", instanceName);
            w.Num("attempt_index", attemptIndex);
            w.Bool("success", success);
            if (positionOk.HasValue) w.Bool("position_ok", positionOk.Value); else w.Str("position_ok", null);
            if (orientationOk.HasValue) w.Bool("orientation_ok", orientationOk.Value); else w.Str("orientation_ok", null);
            w.Num("position_error_m", positionErrorM);
            w.Num("rotation_error_deg", rotationErrorDeg);
            w.Num("position_tolerance_m", positionToleranceM);
            w.Num("rotation_tolerance_deg", rotationToleranceDeg);
            w.Str("part_key", partKey);
            w.Str("target_key", targetKey);
            w.Str("trigger", trigger);
            Write(w);
        }

        /// <summary>One raw PPI sample as received, with the live-window decision made on it. Nothing is filtered before this line.</summary>
        public void LogPpiSample(AdaptiveAR.Physiology.PpiSample s)
        {
            if (s == null) return;
            var w = NewLine(Events.PpiSample);
            w.Num("t_receipt_session_ms", (float)s.ReceiptSessionMs);
            w.Num("t_receipt_realtime_s", (float)s.ReceiptRealtimeS);
            if (s.DeviceTimestampNs != 0) w.Num("device_ts_ns", (long)s.DeviceTimestampNs); else w.Null("device_ts_ns");
            w.Num("ppi_ms", s.PpiMs);
            w.Num("hr_bpm_device", s.DeviceHrBpm);
            w.Num("error_estimate_ms", s.ErrorEstimateMs);
            w.Bool("blocker", s.Blocker);
            w.Bool("skin_contact", s.SkinContact);
            w.Bool("skin_contact_supported", s.SkinContactSupported);
            w.Num("packet_seq", s.PacketSeq);
            w.Num("sample_index", s.SampleIndex);
            w.Num("samples_in_packet", s.SamplesInPacket);
            w.Num("packet_len", s.PacketLength);
            w.Bool("accepted", s.Accepted);
            w.Str("reject_reason", s.RejectReason);
            w.Str("source", s.Source);
            Write(w);
        }

        /// <summary>Live-window summary at a cadence (kind=rolling) or at a decision (kind=decision).</summary>
        public void LogPhysioWindow(AdaptiveAR.Physiology.PhysioSummary s, string kind, string connectionState, string source, double grabOverlapMs)
        {
            if (s == null) return;
            var w = NewLine(Events.PhysioWindow);
            w.Str("window_kind", kind);
            w.Num("window_start_session_ms", (float)(SessionElapsedMs - (s.WindowEndMs - s.WindowStartMs)));
            w.Num("window_end_session_ms", SessionElapsedMs);
            w.Num("window_ms", (float)(s.WindowEndMs - s.WindowStartMs));
            w.Bool("available", s.Available);
            w.NumOrNull("rmssd_ms", s.RmssdMs.HasValue ? (float?)s.RmssdMs.Value : null);
            w.NumOrNull("mean_ibi_ms", s.MeanIbiMs.HasValue ? (float?)s.MeanIbiMs.Value : null);
            w.NumOrNull("hr_bpm_from_ibi", s.HrFromIbiBpm.HasValue ? (float?)s.HrFromIbiBpm.Value : null);
            w.NumOrNull("hr_bpm_device_last", s.LastDeviceHrBpm);
            w.Num("n_raw", s.NRaw);
            w.Num("n_accepted", s.NAccepted);
            w.Num("n_rejected", s.NRejected);
            w.Num("n_rejected_blocker", s.NRejectedBlocker);
            w.Num("n_rejected_range", s.NRejectedRange);
            w.Num("n_rejected_skin", s.NRejectedSkin);
            w.NumOrNull("mean_error_estimate_ms", s.MeanErrorEstimateMs.HasValue ? (float?)s.MeanErrorEstimateMs.Value : null);
            w.Num("completeness", (float)s.Completeness);
            w.NumOrNull("quality", s.Quality.HasValue ? (float?)s.Quality.Value : null);
            w.NumOrNull("sample_age_ms", s.SampleAgeMs.HasValue ? (float?)s.SampleAgeMs.Value : null);
            w.Num("grab_overlap_ms", (float)grabOverlapMs);
            w.Str("connection_state", connectionState);
            w.Str("source", source);
            w.Str("action_id", _actionId);
            Write(w);
        }

        /// <summary>One sensor set-up screen transition with the physiology state at that moment.</summary>
        public void LogSensorSetup(string eventName, string phase, string detail, float setupElapsedMs,
                                   AdaptiveAR.Physiology.PhysioSummary s, bool snapshotValid, string connectionState)
        {
            var w = NewLine(eventName);
            w.Str("phase", phase);
            w.Str("detail", detail);
            w.Num("setup_elapsed_ms", setupElapsedMs);
            w.Str("connection_state", connectionState);
            w.Bool("snapshot_valid", snapshotValid);
            w.Num("n_raw", s != null ? s.NRaw : 0);
            w.Num("n_accepted", s != null ? s.NAccepted : 0);
            w.Num("n_rejected", s != null ? s.NRejected : 0);
            w.NumOrNull("quality", s != null && s.Quality.HasValue ? (float?)s.Quality.Value : null);
            w.NumOrNull("hr_bpm_device_last", s != null ? s.LastDeviceHrBpm : null);
            w.Bool("rmssd_available", s != null && s.Available);
            w.NumOrNull("rmssd_ms", s != null && s.RmssdMs.HasValue ? (float?)s.RmssdMs.Value : null);
            w.NumOrNull("sample_age_ms", s != null && s.SampleAgeMs.HasValue ? (float?)s.SampleAgeMs.Value : null);
            Write(w);
        }

        /// <summary>BLE / stream state. The device name is recorded; the Bluetooth address never is.</summary>
        public void LogPhysioConnection(string state, string detail, string deviceName)
        {
            var w = NewLine(Events.PhysioConnection);
            w.Str("state", state);
            w.Str("detail", detail);
            w.Str("device_name", deviceName);
            Write(w);
        }

        public void LogPhysiological(PhysiologicalSample sample)
        {
            var w = NewLine(Events.Physiological);
            WritePhysiological(w, "physiological", sample);
            Write(w);
        }

        /// <summary>Onboarding screen entered. Separate from assembly so the two can be timed apart.</summary>
        public void LogOnboardingEnter(int screenIndex, string screenId)
        {
            var w = NewLine(Events.OnboardingEnter);
            w.Num("screen_index", screenIndex);
            w.Str("screen_id", screenId);
            Write(w);
        }

        public void LogOnboardingComplete(float durationMs)
        {
            var w = NewLine(Events.OnboardingComplete);
            w.Num("onboarding_duration_ms", durationMs);
            Write(w);
        }

        /// <summary>The participant explicitly began the assembly. Task timing starts here.</summary>
        public void LogAssemblyStart()
        {
            Write(NewLine(Events.AssemblyStart));
        }

        /// <summary>A substep began. Disabled actions are recorded with their reason.</summary>
        public void LogActionEnter(int actionIndex, string actionId, string kind,
                                   bool enabled, string disabledReason)
        {
            _actionId = actionId;
            var w = NewLine(Events.ActionEnter);
            w.Num("action_index", actionIndex);
            w.Str("action_id", actionId);
            w.Str("action_kind", kind);
            w.Bool("action_enabled", enabled);
            w.Str("disabled_reason", string.IsNullOrEmpty(disabledReason) ? null : disabledReason);
            Write(w);
        }

        public void LogActionComplete(int actionIndex, string actionId, float durationMs, string reason)
        {
            var w = NewLine(Events.ActionComplete);
            w.Num("action_index", actionIndex);
            w.Str("action_id", actionId);
            w.Num("action_duration_ms", durationMs);
            w.Str("reason", reason);
            Write(w);
        }

        public void LogComponentGrabbed(string partKey)
        {
            var w = NewLine(Events.ComponentGrabbed);
            w.Str("part_key", partKey);
            Write(w);
        }

        public void LogComponentReleased(string partKey)
        {
            var w = NewLine(Events.ComponentReleased);
            w.Str("part_key", partKey);
            Write(w);
        }

        /// <summary>
        /// part_key is the INSTANCE that locked (e.g. part.PistonKit003.PistonHead);
        /// requested_key is the role the action asked for. With interchangeable parts
        /// they differ, and both are needed to reconstruct which object was used where.
        /// </summary>
        public void LogComponentLocked(string partKey, float positionErrorM, float rotationErrorDeg,
                                       string requestedKey = null, string instanceName = null)
        {
            var w = NewLine(Events.ComponentLocked);
            w.Str("part_key", partKey);
            w.Str("requested_key", requestedKey);
            w.Str("instance_name", instanceName);
            w.Num("position_error_m", positionErrorM);
            w.Num("rotation_error_deg", rotationErrorDeg);
            Write(w);
        }

        public void LogWrongComponent(string partKey, string requestedKey)
        {
            var w = NewLine(Events.WrongComponentGrabbed);
            w.Str("part_key", partKey);
            w.Str("requested_key", requestedKey);
            w.Str("error_type", "wrong_component");
            Write(w);
        }

        /// <summary>Workflow shortcut, not participant performance: no attempts, errors or times for these.</summary>
        public void LogRemainingPistonsAutoCompleted(int count)
        {
            var w = NewLine(Events.RemainingPistonsAutoCompleted);
            w.Num("count", count);
            Write(w);
        }

        public void LogComponentDropped(string partKey, string requestedKey)
        {
            var w = NewLine(Events.ComponentDropped);
            w.Str("part_key", partKey);
            w.Str("requested_key", requestedKey);
            Write(w);
        }

        public void LogHelpRequested(string what)
        {
            var w = NewLine(Events.HelpRequested);
            w.Str("what", what);
            Write(w);
        }

        /// <summary>Fastener or tool action. Detection rule is still to be decided on the bench.</summary>
        public void LogFastenerAction(string fastenerKey, string toolKey, bool completed)
        {
            var w = NewLine(Events.FastenerAction);
            w.Str("fastener_key", fastenerKey);
            w.Str("tool_key", toolKey);
            w.Bool("completed", completed);
            Write(w);
        }

        public void LogNote(string note)
        {
            var w = NewLine(Events.Note);
            w.Str("note", note);
            Write(w);
        }

        /// <summary>
        /// Where the virtual workspace was put and by which provider. Recorded so a session's
        /// spatial layout is reproducible and a fallback-plane placement is distinguishable
        /// from a real surface hit.
        /// </summary>
        public void LogWorkspacePlaced(string provider, Vector3 position, Vector3 surfaceNormal,
                                       float normalConfidence, bool reposition)
        {
            var w = NewLine(Events.WorkspacePlaced);
            w.Str("provider", provider);
            w.Num("position_x_m", position.x);
            w.Num("position_y_m", position.y);
            w.Num("position_z_m", position.z);
            w.Num("normal_x", surfaceNormal.x);
            w.Num("normal_y", surfaceNormal.y);
            w.Num("normal_z", surfaceNormal.z);
            w.Num("normal_confidence", normalConfidence);
            w.Bool("reposition", reposition);
            Write(w);
        }

        /// <summary>
        /// The AI-ready record. Captures the exact context given to the provider, what it
        /// returned raw, what was parsed out, whether that respected the constrained action
        /// set, how long it took, and which level was actually applied afterwards.
        /// </summary>
        public void LogDecision(DecisionContext context, DecisionResponse response,
                                SupportLevel levelBefore, SupportLevel levelAfter, bool applied)
        {
            var w = NewLine(Events.Decision);

            WriteContext(w, context);
            w.Str("ai_input", context != null ? AdaptiveAR.Decision.LlmDecisionCore.ContextToJson(context) : null);
            w.Str("evidence", AdaptiveAR.Decision.DecisionEvidence.Summarise(context));

            w.BeginObject("response");
            if (response != null)
            {
                w.Str("provider_id", response.ProviderId);
                w.Str("raw_output", response.RawOutput);
                w.Str("parsed_action", response.ParsedAction.ToString());
                w.Str("constraint_status", response.Status.ToString());
                w.Bool("constraint_ok", response.Status == ConstraintStatus.Ok);
                w.Bool("used_fallback", response.UsedFallback);
                w.Num("latency_ms", response.LatencyMs);
                w.Str("provider_reason", response.ProviderReason);
            }
            w.EndObject();

            w.Str("level_before", levelBefore.ToString());
            w.Num("level_before_num", (int)levelBefore);
            w.Str("level_after", levelAfter.ToString());
            w.Num("level_after_num", (int)levelAfter);
            w.Bool("applied", applied);

            Write(w);
        }

        /// <summary>The full context block shared by 'decision' and 'decision_context_probe' (same field order).</summary>
        private static void WriteContext(JsonLineWriter w, DecisionContext context)
        {
            w.BeginObject("context");
            if (context != null)
            {
                w.Num("decision_index", context.DecisionIndex);
                w.Num("step_index", context.StepIndex);
                w.Str("step_id", context.StepId);
                w.Num("step_count", context.StepCount);
                w.Num("task_complexity", context.TaskComplexity);
                w.Str("operator_experience", context.OperatorExperience);
                w.Num("step_elapsed_ms", context.StepElapsedMs);
                w.Num("overall_elapsed_ms", context.OverallElapsedMs);
                w.NumOrNull("step_expected_ms", context.StepExpectedMs);
                w.Num("attempts_on_step", context.AttemptsOnStep);
                w.Num("errors_on_step", context.ErrorsOnStep);
                w.Num("errors_total", context.ErrorsTotal);
                w.Str("current_support_level", context.CurrentSupportLevel.ToString());
                w.Num("current_support_level_num", (int)context.CurrentSupportLevel);
                w.Num("support_changes_on_step", context.SupportChangesOnStep);
                w.Num("support_changes_total", context.SupportChangesTotal);
                w.Num("errors_since_last_support_change", context.ErrorsSinceLastSupportChange);
                w.Num("attempts_since_last_support_change", context.AttemptsSinceLastSupportChange);
                w.Num("successful_actions_since_last_support_change", context.SuccessfulActionsSinceLastSupportChange);
                w.NumOrNull("time_since_last_error_ms", context.TimeSinceLastErrorMs);
                w.NumOrNull("time_since_last_support_change_ms", context.TimeSinceLastSupportChangeMs);
                w.Num("recent_window_ms", context.RecentWindowMs);
                w.Num("recent_error_count", context.RecentErrorCount);
                w.Num("recent_failed_attempt_count", context.RecentFailedAttemptCount);
                w.Num("recent_wrong_component_count", context.RecentWrongComponentCount);
                w.Num("consecutive_successful_actions", context.ConsecutiveSuccessfulActions);
                w.Num("errors_since_last_success", context.ErrorsSinceLastSuccess);
                w.Num("failed_attempts_since_last_success", context.FailedAttemptsSinceLastSuccess);
                w.NumOrNull("time_since_last_success_ms", context.TimeSinceLastSuccessMs);
                w.BeginArray("recent_events");
                if (context.RecentEvents != null)
                    foreach (AdaptiveAR.Decision.TaskEvent e in context.RecentEvents)
                    {
                        if (e == null) continue;
                        w.BeginObject();
                        w.Str("type", AdaptiveAR.Decision.TaskEvent.Name(e.Kind));
                        w.Num("age_ms", e.AgeMs);
                        w.Str("action_id", e.ActionId);
                        w.Str("level", e.Level);
                        w.EndObject();
                    }
                w.EndArray();
                w.Bool("physiological_missing", context.PhysiologicalMissing);
                WritePhysiological(w, "physiological", context.Physiological);
            }
            w.EndObject();
        }

        /// <summary>The exact context a provider would be handed at this moment (physiology included), with no decision requested. Replayable by the evaluation harness.</summary>
        public void LogDecisionContextProbe(DecisionContext context, string where)
        {
            var w = NewLine(Events.DecisionContextProbe);
            w.Str("where", where);
            WriteContext(w, context);
            Write(w);
        }

        /// <summary>A decision was requested by the scheduler (the 'decision' line follows when it completes).</summary>
        public void LogDecisionTrigger(string trigger, string providerId, int requestNumber)
        {
            var w = NewLine(Events.DecisionTrigger);
            w.Str("trigger", trigger);
            w.Str("provider_id", providerId);
            w.Num("request_number", requestNumber);
            Write(w);
        }

        /// <summary>
        /// The cue that told the participant guidance increased. Written twice per transition:
        /// replay="pending" when the message and tone go out, then with the replay outcome.
        /// </summary>
        public void LogSupportTransitionCue(SupportLevel from, SupportLevel to, string actionId, string message,
                                            string messageSurface, string tone, string replay, int decisionIndex)
        {
            var w = NewLine(Events.SupportTransitionCue);
            w.Str("from_level", from.ToString());
            w.Num("from_level_num", (int)from);
            w.Str("to_level", to.ToString());
            w.Num("to_level_num", (int)to);
            w.Str("action_id", actionId);
            w.Str("message", message);
            w.Str("message_surface", messageSurface);
            w.Str("tone", tone);
            w.Str("instruction_replay", replay);
            w.Num("decision_index", decisionIndex);
            Write(w);
        }

        /// <summary>
        /// Part snapshot at a lifecycle event. parent_before is written only when the event
        /// changed the parent; renderers_on / renderers_total cover the part's whole subtree.
        /// </summary>
        public void LogPartLifecycle(string where, string partName, int instanceId, bool activeSelf, bool activeInHierarchy,
                                     int renderersOn, int renderersTotal, string parent, string parentBefore, string parentName,
                                     Vector3 worldPos, string kinematic, string note)
        {
            var w = NewLine(Events.PartLifecycle);
            w.Str("where", where);
            w.Str("part", partName);
            w.Num("instance_id", instanceId);
            w.Str("action_id", _actionId);
            w.Bool("active_self", activeSelf);
            w.Bool("active_in_hierarchy", activeInHierarchy);
            w.Num("renderers_on", renderersOn);
            w.Num("renderers_total", renderersTotal);
            w.Str("parent", parent);
            w.Str("parent_before", parentBefore);
            w.Num("world_x_m", worldPos.x);
            w.Num("world_y_m", worldPos.y);
            w.Num("world_z_m", worldPos.z);
            w.Str("kinematic", kinematic);
            w.Str("note", note);
            Write(w);
        }

        /// <summary>The decision layer's configuration and provider status at bind time (no secrets).</summary>
        public void LogDecisionConfig(string description, string status, string providerId)
        {
            var w = NewLine(Events.DecisionConfig);
            w.Str("config", description);
            w.Str("status", status);
            w.Str("provider_id", providerId);
            Write(w);
        }

        private static void WritePhysiological(JsonLineWriter w, string key, PhysiologicalSample s)
        {
            if (s == null)
            {
                w.NullObject(key);
                return;
            }

            w.BeginObject(key);
            w.NumOrNull("rmssd_ms", s.RmssdMs);
            w.NumOrNull("heart_rate_bpm", s.HeartRateBpm);
            w.NumOrNull("mean_ibi_ms", s.MeanIbiMs);
            w.NumOrNull("quality", s.Quality);
            w.NumOrNull("age_ms", s.AgeMs);
            w.Str("source", s.Source);
            w.EndObject();
        }

        // =====================================================================
        // Line construction
        // =====================================================================

        /// <summary>
        /// Builds the envelope every line shares. Field order here is the schema's
        /// stable prefix and must not be reordered.
        /// </summary>
        private JsonLineWriter NewLine(string eventName)
        {
            DateTime now = DateTime.UtcNow;

            var w = new JsonLineWriter();
            w.Str("schema", SchemaVersion);
            w.Num("seq", _sequence++);
            w.Str("event", eventName);
            w.Str("t_utc", now.ToString("o", CultureInfo.InvariantCulture));
            w.Num("t_unix_ms", (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds);
            w.Num("t_session_ms", IsOpen ? (float)((now - _sessionStartUtc).TotalMilliseconds) : 0f);
            w.Str("participant_id", participantId);
            w.Str("session_id", SessionId);
            w.Str("condition", string.IsNullOrEmpty(conditionLabel) ? null : conditionLabel);
            w.Num("step_index", _stepIndex);
            w.Str("step_id", _stepId);
            w.Str("support_level", _supportLevel.ToString());
            w.Num("support_level_num", (int)_supportLevel);
            w.Num("step_elapsed_ms", _stepElapsedMs);
            w.Num("attempts_on_step", _attemptsOnStep);
            w.Num("errors_on_step", _errorsOnStep);
            w.Num("errors_total", _errorsTotal);
            return w;
        }

        private void Write(JsonLineWriter w)
        {
            WriteRaw(w.Build());
        }

        private void WriteRaw(string line)
        {
            if (echoToConsole)
                Debug.Log("[log] " + line);

            if (!IsOpen)
            {
                // Recorded before BeginSession; replayed in order once the file opens.
                _pendingBeforeOpen.Add(line);
                return;
            }

            if (_writer == null) return;

            try
            {
                _writer.WriteLine(line);
                if (flushEveryLine) _writer.Flush();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SessionLogger] Write failed: {e.Message}", this);
            }
        }
    }
}
