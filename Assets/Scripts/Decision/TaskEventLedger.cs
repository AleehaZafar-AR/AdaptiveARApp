// File: TaskEventLedger.cs
// The step's ordered record of meaningful task events and the objective temporal features
// derived from it for the decision context. The application records WHAT happened and
// WHEN; it never classifies the operator (struggling / recovering / stable): that
// interpretation belongs to the decision provider, which receives these features, the
// cumulative counters, the support history and the physiology together (CLAUDE.md 2.1,
// 2.3). Plain C#, no Unity, so the same features are reproducible headlessly.
//
// Event kinds: error (a counted placement error), failed_attempt (an unsuccessful placement
// attempt that was not counted as an error), wrong_component (a part that cannot satisfy
// the action was picked up; counted as an error everywhere else too), success (a validated
// action completion), support_increase / support_decrease (an applied level change).
// Ages are step-local milliseconds at the moment the context is built.

using System.Collections.Generic;

namespace AdaptiveAR.Decision
{
    public enum TaskEventKind { Error, FailedAttempt, WrongComponent, Success, SupportIncrease, SupportDecrease }

    /// <summary>One entry of the compact recent history handed to the provider (oldest first in the list).</summary>
    public sealed class TaskEvent
    {
        public TaskEventKind Kind;
        /// <summary>Milliseconds before the context was built.</summary>
        public float AgeMs;
        /// <summary>The action active when the event happened (null when unknown).</summary>
        public string ActionId;
        /// <summary>The support level in force after the event.</summary>
        public string Level;

        public static string Name(TaskEventKind k)
        {
            switch (k)
            {
                case TaskEventKind.Error: return "error";
                case TaskEventKind.FailedAttempt: return "failed_attempt";
                case TaskEventKind.WrongComponent: return "wrong_component";
                case TaskEventKind.Success: return "success";
                case TaskEventKind.SupportIncrease: return "support_increase";
                case TaskEventKind.SupportDecrease: return "support_decrease";
                default: return "unknown";
            }
        }

        public static bool TryParse(string name, out TaskEventKind kind)
        {
            switch (name)
            {
                case "error": kind = TaskEventKind.Error; return true;
                case "failed_attempt": kind = TaskEventKind.FailedAttempt; return true;
                case "wrong_component": kind = TaskEventKind.WrongComponent; return true;
                case "success": kind = TaskEventKind.Success; return true;
                case "support_increase": kind = TaskEventKind.SupportIncrease; return true;
                case "support_decrease": kind = TaskEventKind.SupportDecrease; return true;
                default: kind = TaskEventKind.Error; return false;
            }
        }

        public static bool IsErrorLike(TaskEventKind k) { return k == TaskEventKind.Error || k == TaskEventKind.WrongComponent; }
        public static bool IsFailedAttempt(TaskEventKind k) { return k == TaskEventKind.Error || k == TaskEventKind.FailedAttempt; }
    }

    public sealed class TaskEventLedger
    {
        private struct Record { public TaskEventKind Kind; public float StepMs; public string ActionId; public string Level; }

        private readonly List<Record> _records = new List<Record>();

        public int Count { get { return _records.Count; } }

        /// <summary>Forget everything: a new step starts a new ledger.</summary>
        public void Reset() { _records.Clear(); }

        public void Add(TaskEventKind kind, float stepMs, string actionId, string level)
        {
            _records.Add(new Record { Kind = kind, StepMs = stepMs, ActionId = actionId, Level = level });
        }

        /// <summary>
        /// Writes the objective temporal features and the compact recent history into the
        /// context. nowMs is the current step time; windowMs bounds the recent_* counts;
        /// maxEvents bounds the history (the newest are kept). Pure function of the ledger.
        /// </summary>
        public void Fill(DecisionContext ctx, float nowMs, float windowMs, int maxEvents)
        {
            if (ctx == null) return;
            ctx.RecentWindowMs = windowMs;

            int recentErrors = 0, recentFailed = 0, recentWrong = 0;
            int errorsSinceSuccess = 0, failedSinceSuccess = 0;
            float? lastSuccessMs = null;

            foreach (Record r in _records)
            {
                float age = nowMs - r.StepMs;
                bool inWindow = age <= windowMs;
                bool errorLike = TaskEvent.IsErrorLike(r.Kind);
                bool failed = TaskEvent.IsFailedAttempt(r.Kind);

                if (inWindow)
                {
                    if (errorLike) recentErrors++;
                    if (r.Kind == TaskEventKind.WrongComponent) recentWrong++;
                    if (failed) recentFailed++;
                }

                if (r.Kind == TaskEventKind.Success) { lastSuccessMs = r.StepMs; errorsSinceSuccess = 0; failedSinceSuccess = 0; }
                else
                {
                    if (errorLike) errorsSinceSuccess++;
                    if (failed) failedSinceSuccess++;
                }
            }

            // Trailing run of validated successes, newest backwards; support changes do not
            // break the run, any unsuccessful attempt or wrong part does.
            int consecutive = 0;
            for (int i = _records.Count - 1; i >= 0; i--)
            {
                TaskEventKind k = _records[i].Kind;
                if (k == TaskEventKind.Success) consecutive++;
                else if (TaskEvent.IsErrorLike(k) || k == TaskEventKind.FailedAttempt) break;
            }

            ctx.RecentErrorCount = recentErrors;
            ctx.RecentFailedAttemptCount = recentFailed;
            ctx.RecentWrongComponentCount = recentWrong;
            ctx.ConsecutiveSuccessfulActions = consecutive;
            ctx.ErrorsSinceLastSuccess = errorsSinceSuccess;
            ctx.FailedAttemptsSinceLastSuccess = failedSinceSuccess;
            ctx.TimeSinceLastSuccessMs = lastSuccessMs.HasValue ? (float?)System.Math.Max(0f, nowMs - lastSuccessMs.Value) : null;

            var events = new List<TaskEvent>();
            int start = maxEvents > 0 ? System.Math.Max(0, _records.Count - maxEvents) : 0;
            for (int i = start; i < _records.Count; i++)
            {
                Record r = _records[i];
                events.Add(new TaskEvent { Kind = r.Kind, AgeMs = System.Math.Max(0f, nowMs - r.StepMs), ActionId = r.ActionId, Level = r.Level });
            }
            ctx.RecentEvents = events;
        }

        /// <summary>"error → error → support_increase → success" for logs and the evidence line.</summary>
        public static string Describe(IList<TaskEvent> events)
        {
            if (events == null || events.Count == 0) return "(none)";
            var parts = new List<string>(events.Count);
            foreach (TaskEvent e in events) parts.Add(TaskEvent.Name(e.Kind) + "@" + e.AgeMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "ms");
            return string.Join(" -> ", parts);
        }
    }
}
