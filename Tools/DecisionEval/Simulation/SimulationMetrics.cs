// File: SimulationMetrics.cs
// Aggregate metrics over simulation runs, written as CSV / JSONL / Markdown. Descriptive
// statistics only: nothing here grades a decision as right or wrong, and the reference
// policy is reported as a deterministic comparator, never as ground truth.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision.Simulation
{
    public static class Stats
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static double Mean(IList<double> v) { return v.Count == 0 ? double.NaN : v.Average(); }
        public static double Percentile(IList<double> v, double p)
        {
            if (v.Count == 0) return double.NaN;
            var s = v.OrderBy(x => x).ToList();
            double idx = (s.Count - 1) * p; int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
            return lo == hi ? s[lo] : s[lo] + (s[hi] - s[lo]) * (idx - lo);
        }
        public static double Max(IList<double> v) { return v.Count == 0 ? double.NaN : v.Max(); }

        /// <summary>Fleiss' kappa for subjects × categories counts; every subject must have the same number of ratings n (>= 2). NaN when undefined.</summary>
        public static double FleissKappa(List<int[]> counts, int categories)
        {
            if (counts == null || counts.Count < 2) return double.NaN;
            int n = counts[0].Sum();
            if (n < 2 || counts.Any(c => c.Sum() != n)) return double.NaN;
            int N = counts.Count;
            double[] pj = new double[categories];
            foreach (int[] c in counts) for (int j = 0; j < categories; j++) pj[j] += c[j];
            for (int j = 0; j < categories; j++) pj[j] /= (double)(N * n);
            double pBar = 0;
            foreach (int[] c in counts) { double s = 0; for (int j = 0; j < categories; j++) s += c[j] * (c[j] - 1); pBar += s / (double)(n * (n - 1)); }
            pBar /= N;
            double pe = pj.Sum(x => x * x);
            if (Math.Abs(1 - pe) < 1e-12) return double.NaN;    // every rating in one category: undefined
            return (pBar - pe) / (1 - pe);
        }

        /// <summary>Cohen's weighted kappa with linear weights over the ordered actions DECREASE &lt; KEEP &lt; INCREASE; confusion[a,b] = count of (provider a, comparator b).</summary>
        public static double WeightedKappaLinear(int[,] confusion)
        {
            int k = confusion.GetLength(0); double n = 0;
            for (int i = 0; i < k; i++) for (int j = 0; j < k; j++) n += confusion[i, j];
            if (n == 0) return double.NaN;
            double[] rows = new double[k], cols = new double[k];
            for (int i = 0; i < k; i++) for (int j = 0; j < k; j++) { rows[i] += confusion[i, j]; cols[j] += confusion[i, j]; }
            double num = 0, den = 0;
            for (int i = 0; i < k; i++) for (int j = 0; j < k; j++)
            {
                double w = Math.Abs(i - j) / (double)(k - 1);
                num += w * confusion[i, j];
                den += w * rows[i] * cols[j] / n;
            }
            if (den < 1e-12) return double.NaN;
            return 1 - num / den;
        }

        public static int Ordinal(SupportAction a) { return a == SupportAction.DECREASE_SUPPORT ? 0 : (a == SupportAction.KEEP_SUPPORT ? 1 : 2); }
        public static string F(double v, string fmt = "F3") { return double.IsNaN(v) ? "" : v.ToString(fmt, Ci); }
        public static string F(double? v, string fmt = "F3") { return v.HasValue ? v.Value.ToString(fmt, Ci) : ""; }
    }

    public static class Csv
    {
        public static string Cell(object v)
        {
            if (v == null) return "";
            string s = v is double d ? Stats.F(d) : (v is float f ? Stats.F(f) : Convert.ToString(v, CultureInfo.InvariantCulture));
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
        public static string Row(params object[] cells) { return string.Join(",", cells.Select(Cell)); }
        public static void Write(string path, IEnumerable<string> rows) { File.WriteAllLines(path, rows, new UTF8Encoding(false)); }
    }

    public static class SimulationMetrics
    {
        // ------------------------------------------------------------------ raw records

        public static string DecisionToJsonLine(DecisionRecord r)
        {
            var o = new Dictionary<string, object>
            {
                { "scenario_id", r.ScenarioId }, { "physiology", r.PhysiologyVariant }, { "repetition", (double)r.Repetition }, { "provider_id", r.ProviderId },
                { "decision_index", (double)r.DecisionIndex }, { "event_index", (double)r.EventIndex }, { "sim_time_s", r.SimTimeS },
                { "step_id", r.StepId }, { "action_id", r.ActionId }, { "level_before", r.LevelBefore.ToString() }, { "level_after", r.LevelAfter.ToString() },
                { "allowed_actions", new List<object>(r.AllowedActions) }, { "trigger", r.Trigger }, { "deferred_from", r.DeferredFrom },
                { "pending_events", (double)r.PendingEvents }, { "pending_errors", (double)r.PendingErrors }, { "pending_successes", (double)r.PendingSuccesses },
                { "context", MiniJson.ParseObject(r.AiInput) }, { "ai_input", r.AiInput }, { "evidence", r.Evidence },
                { "response", new Dictionary<string, object> { { "raw_output", r.RawOutput }, { "parsed_action", r.ParsedAction.ToString() }, { "constraint_status", r.Status.ToString() }, { "used_fallback", r.UsedFallback }, { "latency_ms", (double)r.LatencyMs }, { "provider_reason", r.ProviderReason },
                    { "input_tokens", r.InputTokens.HasValue ? (object)(double)r.InputTokens.Value : null }, { "output_tokens", r.OutputTokens.HasValue ? (object)(double)r.OutputTokens.Value : null }, { "cost_usd", r.CostUsd.HasValue ? (object)r.CostUsd.Value : null } } },
                { "applied", r.Applied }, { "applied_action", r.AppliedAction.ToString() }, { "constraint_applied", r.ConstraintApplied },
                { "reference_action", r.ReferenceAction.HasValue ? r.ReferenceAction.Value.ToString() : null }, { "reference_reason", r.ReferenceReason }
            };
            return MiniJson.Serialize(o);
        }

        public static void WriteRawDecisions(string path, IEnumerable<SimulationRun> runs)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
                foreach (SimulationRun run in runs) foreach (DecisionRecord d in run.Decisions) w.WriteLine(DecisionToJsonLine(d));
        }

        public static void WriteScenarioRuns(string path, IEnumerable<SimulationRun> runs)
        {
            var rows = new List<string> { Csv.Row("scenario_id", "physiology", "repetition", "provider", "duration_s", "events", "errors_total", "attempts_total", "successes", "provider_calls", "keep", "increase", "decrease", "fallbacks", "deferred_triggers", "skipped_triggers",
                "ai_changes", "ai_increases", "ai_decreases", "oscillations", "max_level", "final_level", "reached_L2", "reached_L3", "any_decrease", "difficulty_onset_s", "first_increase_s", "first_decrease_s", "events_onset_to_first_increase", "time_onset_to_first_increase_s", "transitions", "cost_usd") };
            foreach (SimulationRun r in runs)
            {
                string transitions = string.Join(" | ", r.Transitions.Select(t => $"{t.SimTimeS:F1}s {t.From}->{t.To} ({t.Reason}{(t.DecisionIndex >= 0 ? " #" + t.DecisionIndex : "")})"));
                rows.Add(Csv.Row(r.ScenarioId, r.PhysiologyVariant, r.Repetition, r.ProviderId, r.DurationS, r.EventsTotal, r.ErrorsTotal, r.AttemptsTotal, r.Successes, r.ProviderCalls, r.Keep, r.Increase, r.Decrease, r.Fallbacks, r.DeferredTriggers, r.SkippedTriggers,
                    r.AiChanges, r.AiIncreases, r.AiDecreases, r.Oscillations, r.MaxLevel, r.FinalLevel, (int)r.MaxLevel >= 2, (int)r.MaxLevel >= 3, r.AiDecreases > 0, r.DifficultyOnsetS, r.FirstIncreaseS, r.FirstDecreaseS,
                    r.FirstIncreaseEvent.HasValue && r.DifficultyOnsetEvent.HasValue ? (object)(r.FirstIncreaseEvent.Value - r.DifficultyOnsetEvent.Value) : null,
                    r.FirstIncreaseS.HasValue && r.DifficultyOnsetS.HasValue ? (object)(r.FirstIncreaseS.Value - r.DifficultyOnsetS.Value) : null,
                    transitions, r.CostKnown ? (object)r.TotalCostUsd : null));
            }
            Csv.Write(path, rows);
        }

        // ------------------------------------------------------------------ decision summary (reliability + consistency)

        public sealed class DecisionGroupSummary
        {
            public string ScenarioId, Physiology, Provider;
            public int Runs, Decisions, Keep, Increase, Decrease, Ok, Fallback, Malformed, ProviderErrors, BoundaryClamps, ImpossibleTransitions, MultiLevelTransitions;
            public int IdenticalContextGroups, IdenticalContextGroupsAllAgree; public double ModalAgreement = double.NaN, FleissKappa = double.NaN;
        }

        public static List<DecisionGroupSummary> SummariseDecisions(IEnumerable<SimulationRun> runs)
        {
            var list = new List<DecisionGroupSummary>();
            foreach (var g in runs.GroupBy(r => (r.ScenarioId, r.PhysiologyVariant, r.ProviderId)).OrderBy(g => g.Key.ScenarioId).ThenBy(g => g.Key.PhysiologyVariant))
            {
                var s = new DecisionGroupSummary { ScenarioId = g.Key.ScenarioId, Physiology = g.Key.PhysiologyVariant, Provider = g.Key.ProviderId, Runs = g.Count() };
                var all = g.SelectMany(r => r.Decisions).ToList();
                s.Decisions = all.Count;
                foreach (DecisionRecord d in all)
                {
                    if (d.ParsedAction == SupportAction.INCREASE_SUPPORT) s.Increase++; else if (d.ParsedAction == SupportAction.DECREASE_SUPPORT) s.Decrease++; else s.Keep++;
                    if (d.Status == ConstraintStatus.Ok || d.Status == ConstraintStatus.SaturatedAtBoundary) s.Ok++;
                    if (d.UsedFallback) s.Fallback++;
                    if (d.Status == ConstraintStatus.Unparseable || d.Status == ConstraintStatus.OutOfVocabulary) s.Malformed++;
                    if (d.Status == ConstraintStatus.ProviderError || d.Status == ConstraintStatus.ProviderTimeout) s.ProviderErrors++;
                    if (d.ConstraintApplied == "boundary_clamp") s.BoundaryClamps++;
                    int jump = Math.Abs((int)d.LevelAfter - (int)d.LevelBefore);
                    if (jump > 1) s.MultiLevelTransitions++;
                    if ((int)d.LevelAfter < 1 || (int)d.LevelAfter > 3) s.ImpossibleTransitions++;
                }
                // Repeated identical contexts across repetitions: group by the exact AI input (minus identity).
                var groups = all.GroupBy(d => d.AiInputKey).Where(x => x.Count() >= 2).ToList();
                s.IdenticalContextGroups = groups.Count;
                if (groups.Count > 0)
                {
                    var modal = new List<double>(); var counts = new List<int[]>();
                    foreach (var grp in groups)
                    {
                        int[] c = new int[3]; foreach (DecisionRecord d in grp) c[Stats.Ordinal(d.ParsedAction)]++;
                        int n = grp.Count(); modal.Add(c.Max() / (double)n); if (c.Max() == n) s.IdenticalContextGroupsAllAgree++; counts.Add(c);
                    }
                    s.ModalAgreement = Stats.Mean(modal);
                    // Fleiss needs equal raters per subject: use the groups with the modal group size.
                    int common = counts.GroupBy(c => c.Sum()).OrderByDescending(x => x.Count()).First().Key;
                    s.FleissKappa = Stats.FleissKappa(counts.Where(c => c.Sum() == common).ToList(), 3);
                }
                list.Add(s);
            }
            return list;
        }

        public static void WriteDecisionSummary(string path, List<DecisionGroupSummary> rows)
        {
            var lines = new List<string> { Csv.Row("scenario_id", "physiology", "provider", "runs", "decisions", "keep", "increase", "decrease", "keep_share", "increase_share", "decrease_share", "valid_parse_rate", "fallback_rate", "malformed", "provider_errors", "boundary_clamps", "multi_level_transitions", "impossible_transitions", "identical_context_groups", "groups_all_agree", "repeat_consistency", "modal_agreement", "fleiss_kappa") };
            foreach (DecisionGroupSummary s in rows)
            {
                double n = Math.Max(1, s.Decisions);
                lines.Add(Csv.Row(s.ScenarioId, s.Physiology, s.Provider, s.Runs, s.Decisions, s.Keep, s.Increase, s.Decrease, s.Keep / n, s.Increase / n, s.Decrease / n, s.Ok / n, s.Fallback / n, s.Malformed, s.ProviderErrors, s.BoundaryClamps, s.MultiLevelTransitions, s.ImpossibleTransitions,
                    s.IdenticalContextGroups, s.IdenticalContextGroupsAllAgree, s.IdenticalContextGroups > 0 ? (object)(s.IdenticalContextGroupsAllAgree / (double)s.IdenticalContextGroups) : null, s.ModalAgreement, s.FleissKappa));
            }
            Csv.Write(path, lines);
        }

        // ------------------------------------------------------------------ trajectory summary (adaptation, responsiveness, recovery)

        public sealed class TrajectorySummary
        {
            public string ScenarioId, Physiology, Provider; public int Runs;
            public double ReachL2, ReachL3, AnyDecrease, MeanChanges, MeanOscillations, MeanCalls;
            public double? MedianEventsToFirstIncrease, MedianTimeToFirstIncreaseS, MedianTimeToFirstDecreaseS, MedianSuccessesBeforeDecrease, KeepRateDuringRecovery;
            public int RecoveryDecisions; public string FinalLevels;
        }

        public static List<TrajectorySummary> SummariseTrajectories(IEnumerable<SimulationRun> runs)
        {
            var list = new List<TrajectorySummary>();
            foreach (var g in runs.GroupBy(r => (r.ScenarioId, r.PhysiologyVariant, r.ProviderId)).OrderBy(g => g.Key.ScenarioId).ThenBy(g => g.Key.PhysiologyVariant))
            {
                var rs = g.ToList(); int n = rs.Count;
                var t = new TrajectorySummary { ScenarioId = g.Key.ScenarioId, Physiology = g.Key.PhysiologyVariant, Provider = g.Key.ProviderId, Runs = n };
                t.ReachL2 = rs.Count(r => (int)r.MaxLevel >= 2) / (double)n;
                t.ReachL3 = rs.Count(r => (int)r.MaxLevel >= 3) / (double)n;
                t.AnyDecrease = rs.Count(r => r.AiDecreases > 0) / (double)n;
                t.MeanChanges = rs.Average(r => r.AiChanges); t.MeanOscillations = rs.Average(r => r.Oscillations); t.MeanCalls = rs.Average(r => r.ProviderCalls);
                var ev = rs.Where(r => r.FirstIncreaseEvent.HasValue && r.DifficultyOnsetEvent.HasValue).Select(r => (double)(r.FirstIncreaseEvent.Value - r.DifficultyOnsetEvent.Value)).ToList();
                var ti = rs.Where(r => r.FirstIncreaseS.HasValue && r.DifficultyOnsetS.HasValue).Select(r => r.FirstIncreaseS.Value - r.DifficultyOnsetS.Value).ToList();
                var td = rs.Where(r => r.FirstDecreaseS.HasValue).Select(r => r.FirstDecreaseS.Value).ToList();
                t.MedianEventsToFirstIncrease = ev.Count > 0 ? (double?)Stats.Percentile(ev, 0.5) : null;
                t.MedianTimeToFirstIncreaseS = ti.Count > 0 ? (double?)Stats.Percentile(ti, 0.5) : null;
                t.MedianTimeToFirstDecreaseS = td.Count > 0 ? (double?)Stats.Percentile(td, 0.5) : null;
                // Recovery: decisions taken above L1 with a clean current episode that already holds at least one success.
                var recovery = rs.SelectMany(r => r.Decisions).Where(d => (int)d.LevelBefore > 1 && d.Context != null && d.Context.CurrentSupportEpisode != null && d.Context.CurrentSupportEpisode.Errors == 0 && d.Context.CurrentSupportEpisode.Successes >= 1).ToList();
                t.RecoveryDecisions = recovery.Count;
                t.KeepRateDuringRecovery = recovery.Count > 0 ? (double?)(recovery.Count(d => d.ParsedAction == SupportAction.KEEP_SUPPORT) / (double)recovery.Count) : null;
                var sbd = rs.SelectMany(r => r.Decisions).Where(d => d.Applied && d.ParsedAction == SupportAction.DECREASE_SUPPORT && d.Context != null && d.Context.CurrentSupportEpisode != null).Select(d => (double)d.Context.CurrentSupportEpisode.Successes).ToList();
                t.MedianSuccessesBeforeDecrease = sbd.Count > 0 ? (double?)Stats.Percentile(sbd, 0.5) : null;
                t.FinalLevels = string.Join(" ", rs.GroupBy(r => r.FinalLevel).OrderBy(x => (int)x.Key).Select(x => $"{x.Key}:{x.Count()}"));
                list.Add(t);
            }
            return list;
        }

        public static void WriteTrajectorySummary(string path, List<TrajectorySummary> rows)
        {
            var lines = new List<string> { Csv.Row("scenario_id", "physiology", "provider", "runs", "p_reach_L2", "p_reach_L3", "p_any_decrease", "mean_ai_changes", "mean_oscillations", "mean_provider_calls", "median_events_onset_to_first_increase", "median_time_onset_to_first_increase_s", "median_time_to_first_decrease_s", "median_episode_successes_before_decrease", "recovery_decisions", "keep_rate_during_recovery", "final_levels") };
            foreach (TrajectorySummary t in rows)
                lines.Add(Csv.Row(t.ScenarioId, t.Physiology, t.Provider, t.Runs, t.ReachL2, t.ReachL3, t.AnyDecrease, t.MeanChanges, t.MeanOscillations, t.MeanCalls, t.MedianEventsToFirstIncrease, t.MedianTimeToFirstIncreaseS, t.MedianTimeToFirstDecreaseS, t.MedianSuccessesBeforeDecrease, t.RecoveryDecisions, t.KeepRateDuringRecovery, t.FinalLevels));
            Csv.Write(path, lines);
        }

        // ------------------------------------------------------------------ latency / provider reliability

        public sealed class LatencySummary { public string Provider; public int Calls, Failures, Malformed, Fallbacks, WithUsage; public double MeanMs, P50Ms, P95Ms, MaxMs; public long InputTokens, OutputTokens; public double CostUsd; }

        public static List<LatencySummary> SummariseLatency(IEnumerable<SimulationRun> runs)
        {
            var list = new List<LatencySummary>();
            foreach (var g in runs.SelectMany(r => r.Decisions).GroupBy(d => d.ProviderId))
            {
                var ds = g.ToList(); var lat = ds.Select(d => (double)d.LatencyMs).ToList();
                var s = new LatencySummary { Provider = g.Key, Calls = ds.Count, MeanMs = Stats.Mean(lat), P50Ms = Stats.Percentile(lat, 0.5), P95Ms = Stats.Percentile(lat, 0.95), MaxMs = Stats.Max(lat) };
                s.Failures = ds.Count(d => d.Status == ConstraintStatus.ProviderError || d.Status == ConstraintStatus.ProviderTimeout);
                s.Malformed = ds.Count(d => d.Status == ConstraintStatus.Unparseable || d.Status == ConstraintStatus.OutOfVocabulary);
                s.Fallbacks = ds.Count(d => d.UsedFallback);
                s.WithUsage = ds.Count(d => d.InputTokens.HasValue);
                s.InputTokens = ds.Sum(d => (long)(d.InputTokens ?? 0)); s.OutputTokens = ds.Sum(d => (long)(d.OutputTokens ?? 0)); s.CostUsd = ds.Sum(d => d.CostUsd ?? 0);
                list.Add(s);
            }
            return list;
        }

        public static void WriteLatencySummary(string path, List<LatencySummary> rows)
        {
            var lines = new List<string> { Csv.Row("provider", "calls", "mean_ms", "p50_ms", "p95_ms", "max_ms", "request_failures", "malformed_outputs", "fallbacks", "fallback_rate", "calls_with_usage", "input_tokens", "output_tokens", "estimated_cost_usd") };
            foreach (LatencySummary s in rows) lines.Add(Csv.Row(s.Provider, s.Calls, s.MeanMs, s.P50Ms, s.P95Ms, s.MaxMs, s.Failures, s.Malformed, s.Fallbacks, s.Calls > 0 ? s.Fallbacks / (double)s.Calls : 0, s.WithUsage, s.InputTokens, s.OutputTokens, s.CostUsd));
            Csv.Write(path, lines);
        }

        // ------------------------------------------------------------------ physiology sensitivity (paired runs, identical task behaviour)

        public sealed class PhysiologyPair { public string ScenarioId, A, B; public int Pairs, MatchedDecisions, Changed; public double Agreement = double.NaN, FinalLevelDiffers, ReachL3Differs, MeanAiChangesA, MeanAiChangesB; }

        /// <summary>Compares decisions of the same scenario/repetition under two physiology variants at the same decision index while the task evidence is identical (same AI input with physiology removed).</summary>
        public static List<PhysiologyPair> ComparePhysiology(IEnumerable<SimulationRun> runs, string[] variants)
        {
            var list = new List<PhysiologyPair>();
            var byKey = runs.ToDictionary(r => (r.ScenarioId, r.PhysiologyVariant, r.Repetition, r.ProviderId));
            var scenarios = runs.Select(r => r.ScenarioId).Distinct().OrderBy(x => x).ToList();
            var pairs = new List<(string, string)>();
            for (int i = 0; i < variants.Length; i++) for (int j = i + 1; j < variants.Length; j++) pairs.Add((variants[i], variants[j]));
            foreach (string sid in scenarios)
            foreach (var (a, b) in pairs)
            {
                var p = new PhysiologyPair { ScenarioId = sid, A = a, B = b };
                var ra = runs.Where(r => r.ScenarioId == sid && r.PhysiologyVariant == a).ToList();
                var rb = runs.Where(r => r.ScenarioId == sid && r.PhysiologyVariant == b).ToList();
                if (ra.Count == 0 || rb.Count == 0) continue;
                int agree = 0;
                foreach (SimulationRun xa in ra)
                {
                    if (!byKey.TryGetValue((sid, b, xa.Repetition, xa.ProviderId), out SimulationRun xb)) continue;
                    p.Pairs++;
                    if (xa.FinalLevel != xb.FinalLevel) p.FinalLevelDiffers++;
                    if (((int)xa.MaxLevel >= 3) != ((int)xb.MaxLevel >= 3)) p.ReachL3Differs++;
                    int n = Math.Min(xa.Decisions.Count, xb.Decisions.Count);
                    for (int i = 0; i < n; i++)
                    {
                        DecisionRecord da = xa.Decisions[i], db = xb.Decisions[i];
                        if (StripPhysiology(da.AiInputKey) != StripPhysiology(db.AiInputKey)) break;   // trajectories diverged: later decisions are not paired
                        p.MatchedDecisions++;
                        if (da.ParsedAction == db.ParsedAction) agree++; else p.Changed++;
                    }
                }
                if (p.Pairs > 0) { p.FinalLevelDiffers /= p.Pairs; p.ReachL3Differs /= p.Pairs; }
                p.Agreement = p.MatchedDecisions > 0 ? agree / (double)p.MatchedDecisions : double.NaN;
                p.MeanAiChangesA = ra.Average(r => r.AiChanges); p.MeanAiChangesB = rb.Average(r => r.AiChanges);
                list.Add(p);
            }
            return list;
        }

        public static string StripPhysiology(string aiInputKey)
        {
            Dictionary<string, object> o = MiniJson.ParseObject(aiInputKey);
            if (o == null) return aiInputKey;
            o.Remove("physiological"); o.Remove("physiological_missing");
            return MiniJson.Serialize(o);
        }

        public static void WritePhysiologyPairs(string path, List<PhysiologyPair> rows)
        {
            var lines = new List<string> { Csv.Row("scenario_id", "physiology_a", "physiology_b", "paired_runs", "matched_decisions", "decision_agreement", "decisions_changed", "share_changed", "p_final_level_differs", "p_reach_L3_differs", "mean_ai_changes_a", "mean_ai_changes_b") };
            foreach (PhysiologyPair p in rows) lines.Add(Csv.Row(p.ScenarioId, p.A, p.B, p.Pairs, p.MatchedDecisions, p.Agreement, p.Changed, p.MatchedDecisions > 0 ? (object)(p.Changed / (double)p.MatchedDecisions) : null, p.FinalLevelDiffers, p.ReachL3Differs, p.MeanAiChangesA, p.MeanAiChangesB));
            Csv.Write(path, lines);
        }

        // ------------------------------------------------------------------ reference comparator

        public sealed class ReferenceComparison { public string Provider, Reference; public int Decisions, Agree; public int[,] Confusion = new int[3, 3]; public double Agreement = double.NaN, WeightedKappa = double.NaN; }

        public static ReferenceComparison CompareWithReference(IEnumerable<SimulationRun> runs, string referenceId)
        {
            var c = new ReferenceComparison { Reference = referenceId };
            foreach (DecisionRecord d in runs.SelectMany(r => r.Decisions).Where(d => d.ReferenceAction.HasValue && (d.Status == ConstraintStatus.Ok || d.Status == ConstraintStatus.SaturatedAtBoundary)))
            {
                c.Provider = d.ProviderId; c.Decisions++;
                if (d.ParsedAction == d.ReferenceAction.Value) c.Agree++;
                c.Confusion[Stats.Ordinal(d.ParsedAction), Stats.Ordinal(d.ReferenceAction.Value)]++;
            }
            c.Agreement = c.Decisions > 0 ? c.Agree / (double)c.Decisions : double.NaN;
            c.WeightedKappa = Stats.WeightedKappaLinear(c.Confusion);
            return c;
        }

        public static void WriteReferenceComparison(string path, ReferenceComparison c)
        {
            string[] names = { "DECREASE_SUPPORT", "KEEP_SUPPORT", "INCREASE_SUPPORT" };
            var lines = new List<string> { Csv.Row("note", "The reference policy is a deterministic comparator (placeholder thresholds), NOT ground truth and NOT a validated gold standard.") };
            lines.Add(Csv.Row("provider", "reference", "decisions", "raw_agreement", "weighted_kappa_linear"));
            lines.Add(Csv.Row(c.Provider, c.Reference, c.Decisions, c.Agreement, c.WeightedKappa));
            lines.Add(Csv.Row("confusion (rows = provider, columns = reference)", names[0], names[1], names[2]));
            for (int i = 0; i < 3; i++) lines.Add(Csv.Row(names[i], c.Confusion[i, 0], c.Confusion[i, 1], c.Confusion[i, 2]));
            Csv.Write(path, lines);
        }

        // ------------------------------------------------------------------ markdown

        public static string Trajectory(SimulationRun r)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{r.ScenarioId} / physiology={r.PhysiologyVariant} / rep {r.Repetition} / provider {r.ProviderId}: {r.ProviderCalls} call(s), {r.Keep} KEEP / {r.Increase} INCREASE / {r.Decrease} DECREASE, final {r.FinalLevel}, max {r.MaxLevel}, duration {r.DurationS:F0} s");
            foreach (string l in r.GateLog) sb.AppendLine("  " + l);
            foreach (SupportTransition t in r.Transitions) sb.AppendLine($"  transition {t.SimTimeS:F1}s {t.StepId}: {t.From} -> {t.To} ({t.Reason}{(t.DecisionIndex >= 0 ? " decision #" + t.DecisionIndex : "")})");
            return sb.ToString();
        }
    }
}
