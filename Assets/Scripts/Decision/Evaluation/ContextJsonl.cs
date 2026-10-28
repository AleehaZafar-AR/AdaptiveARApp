// File: ContextJsonl.cs
// Replays recorded decision contexts from a session JSONL: decision_context_probe lines
// (flat context) and decision lines (nested "context" object). Plain C#.

using System;
using System.Collections.Generic;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision.Evaluation
{
    public static class ContextJsonl
    {
        public static List<Scenario> Read(IEnumerable<string> lines)
        {
            var list = new List<Scenario>();
            int n = 0;
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Dictionary<string, object> o;
                try { o = MiniJson.ParseObject(line); } catch (Exception) { continue; }
                if (o == null) continue;
                string ev = MiniJson.GetString(o, "event", "");
                Dictionary<string, object> c = (ev == "decision" || ev == "decision_context_probe") ? (MiniJson.GetObject(o, "context") ?? (ev == "decision_context_probe" ? o : null)) : null;
                if (c == null) continue;
                n++;
                DecisionContext ctx = FromObject(c, MiniJson.GetString(o, "participant_id"), MiniJson.GetString(o, "session_id"));
                list.Add(new Scenario { Id = $"replay_{n:D4}_{ev}_{MiniJson.GetString(o, "seq", "?")}", Kind = "replay", BaseId = null, Context = ctx });
            }
            foreach (Scenario s in list) s.BaseId = s.Id;
            return list;
        }

        public static DecisionContext FromObject(Dictionary<string, object> c, string participant, string session)
        {
            var ctx = new DecisionContext
            {
                ParticipantId = participant, SessionId = session,
                DecisionIndex = MiniJson.GetInt(c, "decision_index", 0),
                StepIndex = MiniJson.GetInt(c, "step_index", 0), StepId = MiniJson.GetString(c, "step_id"), StepCount = MiniJson.GetInt(c, "step_count", 0),
                TaskComplexity = MiniJson.GetInt(c, "task_complexity", 0), OperatorExperience = MiniJson.GetString(c, "operator_experience"),
                StepElapsedMs = (float)MiniJson.GetDouble(c, "step_elapsed_ms", 0), OverallElapsedMs = (float)MiniJson.GetDouble(c, "overall_elapsed_ms", 0),
                AttemptsOnStep = MiniJson.GetInt(c, "attempts_on_step", 0), ErrorsOnStep = MiniJson.GetInt(c, "errors_on_step", 0), ErrorsTotal = MiniJson.GetInt(c, "errors_total", 0),
                SupportChangesOnStep = MiniJson.GetInt(c, "support_changes_on_step", 0), SupportChangesTotal = MiniJson.GetInt(c, "support_changes_total", 0),
                ErrorsSinceLastSupportChange = MiniJson.GetInt(c, "errors_since_last_support_change", 0),
                AttemptsSinceLastSupportChange = MiniJson.GetInt(c, "attempts_since_last_support_change", 0),
                SuccessfulActionsSinceLastSupportChange = MiniJson.GetInt(c, "successful_actions_since_last_support_change", 0),
                TimeSinceLastErrorMs = F(MiniJson.GetNullableDouble(c, "time_since_last_error_ms")),
                TimeSinceLastSupportChangeMs = F(MiniJson.GetNullableDouble(c, "time_since_last_support_change_ms"))
            };
            double? exp = MiniJson.GetNullableDouble(c, "step_expected_ms");
            ctx.StepExpectedMs = exp.HasValue ? (float?)exp.Value : null;
            ctx.RecentWindowMs = (float)MiniJson.GetDouble(c, "recent_window_ms", 0);
            ctx.RecentErrorCount = MiniJson.GetInt(c, "recent_error_count", 0);
            ctx.RecentFailedAttemptCount = MiniJson.GetInt(c, "recent_failed_attempt_count", 0);
            ctx.RecentWrongComponentCount = MiniJson.GetInt(c, "recent_wrong_component_count", 0);
            ctx.ConsecutiveSuccessfulActions = MiniJson.GetInt(c, "consecutive_successful_actions", 0);
            ctx.ErrorsSinceLastSuccess = MiniJson.GetInt(c, "errors_since_last_success", 0);
            ctx.FailedAttemptsSinceLastSuccess = MiniJson.GetInt(c, "failed_attempts_since_last_success", 0);
            ctx.TimeSinceLastSuccessMs = F(MiniJson.GetNullableDouble(c, "time_since_last_success_ms"));
            List<object> ev = MiniJson.GetList(c, "recent_events");
            if (ev != null)
            {
                ctx.RecentEvents = new List<TaskEvent>();
                foreach (object item in ev)
                {
                    var eo = item as Dictionary<string, object>;
                    if (eo == null || !TaskEvent.TryParse(MiniJson.GetString(eo, "type", ""), out TaskEventKind kind)) continue;
                    ctx.RecentEvents.Add(new TaskEvent { Kind = kind, AgeMs = (float)MiniJson.GetDouble(eo, "age_ms", 0), ActionId = MiniJson.GetString(eo, "action_id"), Level = MiniJson.GetString(eo, "level") });
                }
            }
            string lvl = MiniJson.GetString(c, "current_support_level", null);
            ctx.CurrentSupportLevel = ParseLevel(lvl, MiniJson.GetInt(c, "current_support_level_num", 1));
            Dictionary<string, object> p = MiniJson.GetObject(c, "physiological");
            if (p != null)
            {
                ctx.Physiological = new PhysiologicalSample
                {
                    RmssdMs = F(MiniJson.GetNullableDouble(p, "rmssd_ms")), HeartRateBpm = F(MiniJson.GetNullableDouble(p, "heart_rate_bpm")),
                    MeanIbiMs = F(MiniJson.GetNullableDouble(p, "mean_ibi_ms")), Quality = F(MiniJson.GetNullableDouble(p, "quality")),
                    AgeMs = F(MiniJson.GetNullableDouble(p, "age_ms")), Source = MiniJson.GetString(p, "source")
                };
            }
            return ctx;
        }

        private static float? F(double? d) { return d.HasValue ? (float?)d.Value : null; }

        private static SupportLevel ParseLevel(string name, int num)
        {
            if (!string.IsNullOrEmpty(name) && Enum.TryParse(name, out SupportLevel l)) return l;
            return SupportLevels.Clamp(num);
        }
    }
}
