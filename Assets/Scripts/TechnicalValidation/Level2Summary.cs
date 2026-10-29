// File: Level2Summary.cs
// What one Level 2 run observed (plain data), the integration assertions evaluated over it,
// and the compact per-scenario / aggregate summaries. Plain C#: no Unity dependency, so the
// headless tests exercise every assertion with synthetic records.
//
// The assertions check STRUCTURAL invariants of the integrated runtime only. None of them
// asserts which action the model should have chosen; scenario expectations are descriptive.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AdaptiveAR.Decision;
using AdaptiveAR.Support;

namespace AdaptiveAR.TechnicalValidation
{
    public sealed class InjectedEventRecord
    {
        public int Index;
        public string Kind;
        public double SessionMs;
        public int StepIndex;
        public string StepId;
        public string ActionBefore;
        public string ActionAfter;
        public string ExpectedAction;
        public string Level;
        public bool Injected;
        public bool Aligned;         // expected action matched (or none expected)
        public string PartKey;
        public string Detail;
        public bool FromFinishTail;
    }

    public sealed class RequestRecord
    {
        public int Number;
        public double SessionMs;
        public string Level;
        public int StepIndex;
    }

    public sealed class DecisionRecord
    {
        public int Index;
        public double SessionMs;
        public string Status;
        public string ParsedAction;
        public bool UsedFallback;
        public float LatencyMs;
        public string LevelAtRequest;
        public string LevelAfter;
        public string ProviderId;
        public int StepIndex;
    }

    public sealed class TransitionRecord
    {
        public double SessionMs;
        public string From;
        public string To;
        public string Reason;
        public int StepIndex;
    }

    public sealed class StepRecord
    {
        public int Index;
        public string StepId;
        public double SessionMs;
        public string LevelOnEntry;
        public string LevelBeforeEntry;
    }

    public sealed class PresentationCheck
    {
        public double SessionMs;
        public string ActionId;
        public string Level;
        public bool TextMatches;
        public bool OtherLevelTextShown;
        public string GhostState;
        public string Detail;
    }

    public sealed class AssertionResult
    {
        public string Name;
        public bool Passed;
        public string Detail;
    }

    /// <summary>Everything one scenario run observed. Filled by the runner, evaluated by IntegrationAssertions.</summary>
    public sealed class ScenarioRunRecord
    {
        public string ScenarioId, ScenarioTitle, Mode, Physiology, BuildLabel, ProviderId, ProviderStatus, ConfigDescription;
        public double DelayScale = 1.0;
        public DateTime StartUtc, EndUtc;
        public string SessionId, LoggerPath, ValidationLogPath;
        public string LevelAtStart;

        public readonly List<InjectedEventRecord> Events = new List<InjectedEventRecord>();
        public readonly List<RequestRecord> Requests = new List<RequestRecord>();
        public readonly List<DecisionRecord> Decisions = new List<DecisionRecord>();
        public readonly List<TransitionRecord> Transitions = new List<TransitionRecord>();
        public readonly List<StepRecord> Steps = new List<StepRecord>();
        public readonly List<PresentationCheck> Presentation = new List<PresentationCheck>();

        public int ExceptionCount;
        public string FirstException;
        public int ErrorLogCount;
        public bool SequenceFinished;
        public bool SessionEnded;
        public bool RunnerAborted;
        public string AbortReason;

        public int ErrorsTotalObserved = -1;
        public int AttemptsTotalObserved = -1;

        public int DecisionLinesInJsonl = -1;
        public int SupportChangeLinesInJsonl = -1;
        public bool JsonlReadable;
        public bool JsonlClosesWithSessionEnd;
        public string JsonlLastEvent;
        public int TechnicalValidationNotesInJsonl = -1;

        // the production schedule in force, copied so the assertions are self-contained
        public double MinCallIntervalSeconds = 4;
        public double ChangeCooldownMs = 15000;
        public int MinEventsPerRequest = 2;
        public double FirstDecisionAfterStepMs = 10000;

        public double DurationSeconds { get { return Math.Max(0, (EndUtc - StartUtc).TotalSeconds); } }

        public int Count(string action) { int n = 0; foreach (var d in Decisions) if (d.ParsedAction == action && !d.UsedFallback) n++; return n; }
        public int Keep { get { int n = 0; foreach (var d in Decisions) if (d.UsedFallback || d.ParsedAction == "KEEP_SUPPORT") n++; return n; } }
        public int Increase { get { return Count("INCREASE_SUPPORT"); } }
        public int Decrease { get { return Count("DECREASE_SUPPORT"); } }
        public int Fallbacks { get { int n = 0; foreach (var d in Decisions) if (d.UsedFallback) n++; return n; } }
        public int ProviderErrors { get { int n = 0; foreach (var d in Decisions) if (d.Status == "ProviderError" || d.Status == "ProviderTimeout") n++; return n; } }
        public int MalformedOutputs { get { int n = 0; foreach (var d in Decisions) if (d.Status == "Unparseable" || d.Status == "OutOfVocabulary") n++; return n; } }
        public int InjectedCount { get { int n = 0; foreach (var e in Events) if (e.Injected) n++; return n; } }
        public int ScriptedCount { get { int n = 0; foreach (var e in Events) if (!e.FromFinishTail) n++; return n; } }

        public string MaxLevel
        {
            get
            {
                int max = Levels.Num(LevelAtStart);
                foreach (var t in Transitions) max = Math.Max(max, Levels.Num(t.To));
                foreach (var s in Steps) max = Math.Max(max, Levels.Num(s.LevelOnEntry));
                return Levels.Name(max);
            }
        }

        /// <summary>"L1 -(45s ai_decision)-> L2 -(60s ai_decision)-> L3 -(123s step_default)-> L1"</summary>
        public string Trajectory()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(Levels.Short(LevelAtStart));
            foreach (var t in Transitions)
                sb.Append(" -(").Append((t.SessionMs / 1000.0).ToString("0", c)).Append("s ").Append(t.Reason).Append(")-> ").Append(Levels.Short(t.To));
            return sb.ToString();
        }

        public int AiTransitions { get { int n = 0; foreach (var t in Transitions) if (t.Reason == "ai_decision") n++; return n; } }

        /// <summary>Direction reversals among AI transitions inside one step.</summary>
        public int Oscillations
        {
            get
            {
                int n = 0; int lastDir = 0; int lastStep = -2;
                foreach (var t in Transitions)
                {
                    if (t.Reason != "ai_decision") continue;
                    int dir = Math.Sign(Levels.Num(t.To) - Levels.Num(t.From));
                    if (t.StepIndex == lastStep && lastDir != 0 && dir == -lastDir) n++;
                    lastDir = dir; lastStep = t.StepIndex;
                }
                return n;
            }
        }
    }

    public static class Levels
    {
        public static int Num(string level)
        {
            if (string.IsNullOrEmpty(level)) return 0;
            if (level.StartsWith("L1")) return 1;
            if (level.StartsWith("L2")) return 2;
            if (level.StartsWith("L3")) return 3;
            return 0;
        }
        public static string Name(int n) { return n == 3 ? "L3_Assisted" : n == 2 ? "L2_Guided" : n == 1 ? "L1_Minimal" : "unknown"; }
        public static string Short(string level) { int n = Num(level); return n == 0 ? "?" : "L" + n; }
        public static bool Valid(string level) { return Num(level) >= 1 && Num(level) <= 3; }
        public static string ExpectedAfter(string levelAtRequest, string status, string action)
        {
            int n = Num(levelAtRequest);
            if (status != "Ok" && status != "SaturatedAtBoundary") return levelAtRequest;
            if (action == "INCREASE_SUPPORT") return Name(Math.Min(3, n + 1));
            if (action == "DECREASE_SUPPORT") return Name(Math.Max(1, n - 1));
            return levelAtRequest;
        }
    }

    public static class IntegrationAssertions
    {
        public static List<AssertionResult> Evaluate(ScenarioRunRecord r)
        {
            var list = new List<AssertionResult>();
            var c = CultureInfo.InvariantCulture;
            void Add(string name, bool ok, string detail) { list.Add(new AssertionResult { Name = name, Passed = ok, Detail = detail }); }

            // 1 started at L1
            bool startL1 = Levels.Num(r.LevelAtStart) == 1 && (r.Steps.Count == 0 || Levels.Num(r.Steps[0].LevelOnEntry) == 1);
            Add("scenario started at L1", startL1, $"level at start {r.LevelAtStart ?? "?"}" + (r.Steps.Count > 0 ? $", first step entered at {r.Steps[0].LevelOnEntry}" : ", no step entered"));

            // 2 every scripted event entered the production ledger
            int notInjected = 0, misaligned = 0, errLike = 0, attempts = 0;
            foreach (var e in r.Events)
            {
                if (!e.Injected) notInjected++;
                if (!e.Aligned) misaligned++;
                if (e.Injected && (e.Kind == "error" || e.Kind == "wrong_component")) errLike++;
                if (e.Injected && (e.Kind == "success" || e.Kind == "failed_attempt" || e.Kind == "error")) attempts++;
            }
            bool ledgerOk = r.Events.Count > 0 && notInjected == 0
                            && (r.ErrorsTotalObserved < 0 || r.ErrorsTotalObserved == errLike)
                            && (r.AttemptsTotalObserved < 0 || r.AttemptsTotalObserved == attempts);
            Add("all scripted events entered the production ledger", ledgerOk,
                $"{r.Events.Count} event(s), {notInjected} not injected, {misaligned} at an unexpected action; counted error-like {errLike} vs session errors_total {r.ErrorsTotalObserved}; attempts {attempts} vs session attempts_total {r.AttemptsTotalObserved}");

            // 3 requests where structurally appropriate
            var errPerStep = new Dictionary<int, int>();
            var reqPerStep = new Dictionary<int, int>();
            foreach (var e in r.Events) if (e.Injected && (e.Kind == "error" || e.Kind == "wrong_component")) errPerStep[e.StepIndex] = (errPerStep.TryGetValue(e.StepIndex, out int v) ? v : 0) + 1;
            foreach (var q in r.Requests) reqPerStep[q.StepIndex] = (reqPerStep.TryGetValue(q.StepIndex, out int v) ? v : 0) + 1;
            var missing = new List<string>();
            foreach (var kv in errPerStep)
                if (kv.Value >= r.MinEventsPerRequest && (!reqPerStep.TryGetValue(kv.Key, out int rq) || rq == 0)) missing.Add($"step {kv.Key}: {kv.Value} error-like event(s), 0 request(s)");
            bool reqOk = r.Requests.Count > 0 && missing.Count == 0;
            Add("provider requests occurred where structurally appropriate", reqOk,
                r.Requests.Count == 0 ? "no provider request was made" : (missing.Count == 0 ? $"{r.Requests.Count} request(s); every step with >= {r.MinEventsPerRequest} error-like events was asked" : string.Join("; ", missing)));

            // 4 every decision parsed or fallback logged
            var known = new HashSet<string> { "Ok", "Unparseable", "OutOfVocabulary", "ProviderError", "ProviderTimeout", "SaturatedAtBoundary" };
            int unknownStatus = 0; foreach (var d in r.Decisions) if (!known.Contains(d.Status)) unknownStatus++;
            bool countsMatch = r.Decisions.Count == r.Requests.Count && (r.DecisionLinesInJsonl < 0 || r.DecisionLinesInJsonl == r.Requests.Count);
            Add("every provider decision (parsed or fallback) was logged", countsMatch && unknownStatus == 0,
                $"requests {r.Requests.Count}, decisions observed {r.Decisions.Count}, decision lines in JSONL {(r.DecisionLinesInJsonl < 0 ? "not read" : r.DecisionLinesInJsonl.ToString(c))}, unknown statuses {unknownStatus}, fallbacks {r.Fallbacks}");

            // 5 one-level constraint on applied AI transitions
            var multi = new List<string>();
            foreach (var t in r.Transitions)
                if (t.Reason == "ai_decision" && Math.Abs(Levels.Num(t.To) - Levels.Num(t.From)) != 1) multi.Add($"{t.From}->{t.To} at {(t.SessionMs / 1000.0).ToString("0.0", c)}s");
            int clampViolations = 0;
            foreach (var d in r.Decisions)
            {
                bool atTop = Levels.Num(d.LevelAtRequest) == 3 && d.ParsedAction == "INCREASE_SUPPORT" && !d.UsedFallback;
                bool atBottom = Levels.Num(d.LevelAtRequest) == 1 && d.ParsedAction == "DECREASE_SUPPORT" && !d.UsedFallback;
                if ((atTop || atBottom) && d.Status != "SaturatedAtBoundary") clampViolations++;
                if ((atTop || atBottom) && d.LevelAfter != d.LevelAtRequest) clampViolations++;
            }
            Add("every applied support transition obeyed the one-level constraint", multi.Count == 0 && clampViolations == 0,
                $"{r.AiTransitions} AI transition(s), {multi.Count} multi-level ({string.Join(", ", multi)}), {clampViolations} boundary violation(s)");

            // 6 controller level matches the decision record
            var mismatch = new List<string>();
            foreach (var d in r.Decisions)
            {
                string expected = Levels.ExpectedAfter(d.LevelAtRequest, d.Status, d.UsedFallback ? "KEEP_SUPPORT" : d.ParsedAction);
                if (d.LevelAfter != expected) mismatch.Add($"#{d.Index} {d.LevelAtRequest} {d.ParsedAction}/{d.Status} -> controller {d.LevelAfter}, expected {expected}");
            }
            Add("SupportLevelController level matches the decision log after each decision", r.Decisions.Count > 0 && mismatch.Count == 0,
                r.Decisions.Count == 0 ? "no decision to compare" : (mismatch.Count == 0 ? $"{r.Decisions.Count} decision(s) consistent" : string.Join("; ", mismatch)));

            // 7 displayed package corresponds to the level
            int presFail = 0, presOther = 0;
            foreach (var p in r.Presentation) { if (!p.TextMatches) presFail++; if (p.OtherLevelTextShown) presOther++; }
            Add("displayed support package corresponds to the actual support level", r.Presentation.Count > 0 && presFail == 0 && presOther == 0,
                r.Presentation.Count == 0 ? "no presentation check was performed" : $"{r.Presentation.Count} check(s), {presFail} without the level's instruction text, {presOther} showing another level's text");

            // 8 no impossible support state
            var impossible = new List<string>();
            if (!Levels.Valid(r.LevelAtStart)) impossible.Add("start " + (r.LevelAtStart ?? "null"));
            foreach (var t in r.Transitions) { if (!Levels.Valid(t.From) || !Levels.Valid(t.To)) impossible.Add($"{t.From}->{t.To}"); if (t.From == t.To) impossible.Add("no-op transition " + t.From); }
            foreach (var d in r.Decisions) { if (!Levels.Valid(d.LevelAtRequest) || !Levels.Valid(d.LevelAfter)) impossible.Add($"decision #{d.Index} {d.LevelAtRequest}/{d.LevelAfter}"); }
            Add("no impossible support state", impossible.Count == 0, impossible.Count == 0 ? $"max level {Levels.Short(r.MaxLevel)}, all states in {{L1,L2,L3}}" : string.Join("; ", impossible));

            // 9 step entry reset to L1
            var notReset = new List<string>(); int afterElevated = 0;
            foreach (var s in r.Steps)
            {
                if (Levels.Num(s.LevelOnEntry) != 1) notReset.Add($"step {s.Index} entered at {s.LevelOnEntry}");
                if (Levels.Num(s.LevelBeforeEntry) > 1) afterElevated++;
            }
            Add("step entry reset to L1", r.Steps.Count > 0 && notReset.Count == 0,
                r.Steps.Count == 0 ? "no step entered" : (notReset.Count == 0 ? $"{r.Steps.Count} step entr(ies) at L1, {afterElevated} of them after an elevated level" : string.Join("; ", notReset)));

            // 10 session completed without an uncaught exception
            Add("session completed without an uncaught exception", r.ExceptionCount == 0 && r.SequenceFinished && !r.RunnerAborted,
                $"exceptions {r.ExceptionCount}{(r.FirstException != null ? " (first: " + r.FirstException + ")" : "")}, error log lines {r.ErrorLogCount}, sequence finished {r.SequenceFinished}" + (r.RunnerAborted ? ", runner aborted: " + r.AbortReason : ""));

            // 11 JSONL closes correctly
            Add("production JSONL output closed correctly", r.SessionEnded && r.JsonlReadable && r.JsonlClosesWithSessionEnd,
                $"session ended {r.SessionEnded}, file readable {r.JsonlReadable}, last event {(r.JsonlLastEvent ?? "?")}, support_change lines {(r.SupportChangeLinesInJsonl < 0 ? "?" : r.SupportChangeLinesInJsonl.ToString(c))}, technical-validation notes {(r.TechnicalValidationNotesInJsonl < 0 ? "?" : r.TechnicalValidationNotesInJsonl.ToString(c))}");

            // 12 scheduler call protection (structural): no two requests closer than the minimum call interval
            var tooClose = new List<string>();
            for (int i = 1; i < r.Requests.Count; i++)
            {
                double gap = (r.Requests[i].SessionMs - r.Requests[i - 1].SessionMs) / 1000.0;
                if (gap + 0.05 < r.MinCallIntervalSeconds) tooClose.Add($"#{r.Requests[i - 1].Number}->#{r.Requests[i].Number} {gap.ToString("0.00", c)}s");
            }
            Add("scheduler call protection respected (min call interval)", tooClose.Count == 0, tooClose.Count == 0 ? $"{r.Requests.Count} request(s), min interval {r.MinCallIntervalSeconds.ToString("0.#", c)} s" : string.Join("; ", tooClose));

            // 13 support-change hysteresis (structural): AI transitions not closer than the change cooldown
            var fast = new List<string>(); TransitionRecord last = null;
            foreach (var t in r.Transitions)
            {
                if (t.Reason != "ai_decision") continue;
                if (last != null && t.SessionMs - last.SessionMs + 50 < r.ChangeCooldownMs) fast.Add($"{last.To} -> {t.To} after {((t.SessionMs - last.SessionMs) / 1000.0).ToString("0.0", c)}s");
                last = t;
            }
            Add("support-change hysteresis respected; no rapid oscillation", fast.Count == 0 && r.Oscillations == 0,
                $"{r.AiTransitions} AI transition(s), oscillations {r.Oscillations}, cooldown {(r.ChangeCooldownMs / 1000.0).ToString("0.#", c)} s" + (fast.Count > 0 ? "; too fast: " + string.Join("; ", fast) : ""));

            return list;
        }

        public static int Failed(List<AssertionResult> results) { int n = 0; foreach (var a in results) if (!a.Passed) n++; return n; }
    }

    public static class Level2SummaryWriter
    {
        public static string ToMarkdown(ScenarioRunRecord r, List<AssertionResult> assertions)
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            int failed = IntegrationAssertions.Failed(assertions);
            sb.Append("# Level 2 technical validation: ").Append(r.ScenarioId).Append(r.ScenarioTitle != null ? " " + r.ScenarioTitle : "").Append('\n');
            sb.Append("mode=").Append(r.Mode).Append(" physiology=").Append(r.Physiology).Append(" delay_scale=").Append(r.DelayScale.ToString("0.##", c))
              .Append(" build=").Append(r.BuildLabel ?? "?").Append(" provider=").Append(r.ProviderId ?? "none").Append(" (").Append(r.ProviderStatus ?? "?").Append(")\n");
            sb.Append("start ").Append(r.StartUtc.ToString("o", c)).Append("  end ").Append(r.EndUtc.ToString("o", c)).Append("  duration ").Append(r.DurationSeconds.ToString("0", c)).Append(" s\n");
            sb.Append("scripted events ").Append(r.ScriptedCount).Append(" (+").Append(r.Events.Count - r.ScriptedCount).Append(" finish-tail), injected ").Append(r.InjectedCount).Append(" / ").Append(r.Events.Count).Append('\n');
            sb.Append("provider calls ").Append(r.Requests.Count).Append(" | KEEP ").Append(r.Keep).Append(" INCREASE ").Append(r.Increase).Append(" DECREASE ").Append(r.Decrease)
              .Append(" | fallbacks ").Append(r.Fallbacks).Append(" | provider errors/timeouts ").Append(r.ProviderErrors).Append(" | malformed ").Append(r.MalformedOutputs).Append('\n');
            sb.Append("support trajectory: ").Append(r.Trajectory()).Append('\n');
            sb.Append("maximum level reached: ").Append(Levels.Short(r.MaxLevel)).Append("   AI transitions ").Append(r.AiTransitions).Append("   oscillations ").Append(r.Oscillations).Append('\n');
            int structural = 0; foreach (var a in assertions) if (!a.Passed && (a.Name.Contains("one-level") || a.Name.Contains("impossible") || a.Name.Contains("matches"))) structural++;
            sb.Append("structural violations: ").Append(structural).Append('\n');
            sb.Append("logger output: ").Append(r.LoggerPath ?? "?").Append('\n');
            sb.Append("validation log: ").Append(r.ValidationLogPath ?? "?").Append('\n');
            sb.Append("integration assertions: ").Append(assertions.Count - failed).Append(" passed / ").Append(failed).Append(" failed\n");
            foreach (var a in assertions) sb.Append(a.Passed ? "  PASS  " : "  FAIL  ").Append(a.Name).Append(": ").Append(a.Detail).Append('\n');
            if (r.Decisions.Count > 0)
            {
                sb.Append("decisions:\n");
                foreach (var d in r.Decisions)
                    sb.Append("  #").Append(d.Index).Append(' ').Append((d.SessionMs / 1000.0).ToString("0.0", c)).Append("s step ").Append(d.StepIndex).Append(' ')
                      .Append(Levels.Short(d.LevelAtRequest)).Append(' ').Append(d.ParsedAction).Append(d.UsedFallback ? " (fallback)" : "").Append(' ').Append(d.Status)
                      .Append(" -> ").Append(Levels.Short(d.LevelAfter)).Append(' ').Append(d.LatencyMs.ToString("0", c)).Append(" ms\n");
            }
            return sb.ToString();
        }

        public static Dictionary<string, object> ToObject(ScenarioRunRecord r, List<AssertionResult> assertions)
        {
            var c = CultureInfo.InvariantCulture;
            var o = new Dictionary<string, object>
            {
                { "schema", "adaptivear.level2_summary.v1" },
                { "scenario_id", r.ScenarioId }, { "scenario_title", r.ScenarioTitle }, { "mode", r.Mode }, { "physiology", r.Physiology },
                { "delay_scale", r.DelayScale }, { "build", r.BuildLabel }, { "provider", r.ProviderId }, { "provider_status", r.ProviderStatus }, { "config", r.ConfigDescription },
                { "start_utc", r.StartUtc.ToString("o", c) }, { "end_utc", r.EndUtc.ToString("o", c) }, { "duration_s", r.DurationSeconds },
                { "session_id", r.SessionId }, { "logger_path", r.LoggerPath }, { "validation_log_path", r.ValidationLogPath },
                { "scripted_events", (double)r.ScriptedCount }, { "events_total", (double)r.Events.Count }, { "events_injected", (double)r.InjectedCount },
                { "provider_calls", (double)r.Requests.Count }, { "keep", (double)r.Keep }, { "increase", (double)r.Increase }, { "decrease", (double)r.Decrease },
                { "fallbacks", (double)r.Fallbacks }, { "provider_errors", (double)r.ProviderErrors }, { "malformed", (double)r.MalformedOutputs },
                { "trajectory", r.Trajectory() }, { "max_level", r.MaxLevel }, { "ai_transitions", (double)r.AiTransitions }, { "oscillations", (double)r.Oscillations },
                { "exceptions", (double)r.ExceptionCount }, { "sequence_finished", r.SequenceFinished }, { "session_ended", r.SessionEnded },
                { "assertions_passed", (double)(assertions.Count - IntegrationAssertions.Failed(assertions)) }, { "assertions_failed", (double)IntegrationAssertions.Failed(assertions) }
            };
            var al = new List<object>();
            foreach (var a in assertions) al.Add(new Dictionary<string, object> { { "name", a.Name }, { "passed", a.Passed }, { "detail", a.Detail } });
            o["assertions"] = al;
            var dl = new List<object>();
            foreach (var d in r.Decisions)
                dl.Add(new Dictionary<string, object> { { "index", (double)d.Index }, { "t_session_ms", d.SessionMs }, { "step_index", (double)d.StepIndex }, { "level_at_request", d.LevelAtRequest }, { "action", d.ParsedAction }, { "status", d.Status }, { "fallback", d.UsedFallback }, { "level_after", d.LevelAfter }, { "latency_ms", (double)d.LatencyMs } });
            o["decisions"] = dl;
            var tl = new List<object>();
            foreach (var t in r.Transitions) tl.Add(new Dictionary<string, object> { { "t_session_ms", t.SessionMs }, { "from", t.From }, { "to", t.To }, { "reason", t.Reason }, { "step_index", (double)t.StepIndex } });
            o["transitions"] = tl;
            return o;
        }

        /// <summary>One row per scenario summary object (as written by ToObject), newest last.</summary>
        public static string AggregateMarkdown(List<Dictionary<string, object>> summaries, DateTime nowUtc)
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# Level 2 technical validation: aggregate\n");
            sb.Append("generated ").Append(nowUtc.ToString("o", c)).Append("  scenarios ").Append(summaries.Count).Append('\n');
            sb.Append("| scenario | mode | physiology | start | events | calls | KEEP | INC | DEC | fallbacks | max | trajectory | assertions |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            int totalCalls = 0, totalFail = 0, totalK = 0, totalI = 0, totalD = 0, totalFb = 0;
            foreach (var s in summaries)
            {
                int calls = MiniJson.GetInt(s, "provider_calls", 0), k = MiniJson.GetInt(s, "keep", 0), inc = MiniJson.GetInt(s, "increase", 0), dec = MiniJson.GetInt(s, "decrease", 0);
                int fb = MiniJson.GetInt(s, "fallbacks", 0), ap = MiniJson.GetInt(s, "assertions_passed", 0), af = MiniJson.GetInt(s, "assertions_failed", 0);
                totalCalls += calls; totalK += k; totalI += inc; totalD += dec; totalFb += fb; totalFail += af;
                sb.Append("| ").Append(MiniJson.GetString(s, "scenario_id", "?")).Append(" | ").Append(MiniJson.GetString(s, "mode", "?")).Append(" | ").Append(MiniJson.GetString(s, "physiology", "?"))
                  .Append(" | ").Append(MiniJson.GetString(s, "start_utc", "?")).Append(" | ").Append(MiniJson.GetInt(s, "events_injected", 0)).Append('/').Append(MiniJson.GetInt(s, "events_total", 0))
                  .Append(" | ").Append(calls).Append(" | ").Append(k).Append(" | ").Append(inc).Append(" | ").Append(dec).Append(" | ").Append(fb)
                  .Append(" | ").Append(Levels.Short(MiniJson.GetString(s, "max_level", "?"))).Append(" | ").Append(MiniJson.GetString(s, "trajectory", "?"))
                  .Append(" | ").Append(ap).Append(" passed / ").Append(af).Append(" failed |\n");
            }
            sb.Append("\ntotals: calls ").Append(totalCalls).Append(", KEEP ").Append(totalK).Append(", INCREASE ").Append(totalI).Append(", DECREASE ").Append(totalD)
              .Append(", fallbacks ").Append(totalFb).Append(", assertion failures ").Append(totalFail).Append('\n');
            sb.Append("Descriptive only: the assertions check structural invariants of the integrated runtime; no assertion grades the model's choice.\n");
            return sb.ToString();
        }
    }
}
