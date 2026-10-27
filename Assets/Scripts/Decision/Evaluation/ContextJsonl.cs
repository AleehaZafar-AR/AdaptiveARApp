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
                SupportChangesOnStep = MiniJson.GetInt(c, "support_changes_on_step", 0), SupportChangesTotal = MiniJson.GetInt(c, "support_changes_total", 0)
            };
            double? exp = MiniJson.GetNullableDouble(c, "step_expected_ms");
            ctx.StepExpectedMs = exp.HasValue ? (float?)exp.Value : null;
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
