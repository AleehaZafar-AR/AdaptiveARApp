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
        public const string SchemaVersion = "adaptivear.session.v1";

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
        }

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
                                     SupportChangeSource source, string reason, bool saturated)
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
            Write(w);
        }

        public void LogValidationAttempt(int attemptIndex, bool success,
                                         float positionErrorM, float rotationErrorDeg,
                                         float positionToleranceM, float rotationToleranceDeg,
                                         string partKey, string targetKey, string trigger,
                                         string rejectReason = null)
        {
            var w = NewLine(Events.ValidationAttempt);
            // "placement_success", "incorrect_position" or "incorrect_orientation".
            w.Str("error_type", rejectReason);
            w.Num("attempt_index", attemptIndex);
            w.Bool("success", success);
            w.Num("position_error_m", positionErrorM);
            w.Num("rotation_error_deg", rotationErrorDeg);
            w.Num("position_tolerance_m", positionToleranceM);
            w.Num("rotation_tolerance_deg", rotationToleranceDeg);
            w.Str("part_key", partKey);
            w.Str("target_key", targetKey);
            w.Str("trigger", trigger);
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
                w.Bool("physiological_missing", context.PhysiologicalMissing);
                WritePhysiological(w, "physiological", context.Physiological);
            }
            w.EndObject();

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
