// File: ReplayAndMetamorphic.cs
// (1) Real-session replay: decision contexts recorded by the headset (decision lines of a
//     session JSONL) are resubmitted to a provider repeatedly to measure consistency on
//     real contexts. Kept separate from the synthetic trajectories.
// (2) Metamorphic pairs: a recorded or simulated context is transformed in ONE respect that
//     should leave the meaningful state equivalent (physiology missing vs neutral, old errors
//     pushed further into the past, more elapsed time without errors, different irrelevant
//     cumulative totals); both versions are submitted and the agreement is reported. No
//     expected action is prescribed; the pairs show whether decisions follow stale or
//     irrelevant information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdaptiveAR.Decision.Evaluation;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision.Simulation
{
    public sealed class ReplayItem
    {
        public string SessionFile, SessionId, StepId; public int Seq, DecisionIndex; public SupportLevel LevelBefore;
        public string OriginalAction, OriginalStatus, OriginalProvider;
        public DecisionContext Context;
        public List<DecisionResponse> Responses = new List<DecisionResponse>();
        public SupportAction? ReferenceAction;
        public SupportAction Modal; public double ModalShare = double.NaN; public bool AgreesWithOriginal;
    }

    public static class ReplayEvaluator
    {
        /// <summary>Reads every 'decision' line with a context block; the original response is kept for comparison.</summary>
        public static List<ReplayItem> Load(string path)
        {
            var items = new List<ReplayItem>();
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Dictionary<string, object> o; try { o = MiniJson.ParseObject(line); } catch (Exception) { continue; }
                if (o == null || MiniJson.GetString(o, "event", "") != "decision") continue;
                Dictionary<string, object> c = MiniJson.GetObject(o, "context"); if (c == null) continue;
                var item = new ReplayItem
                {
                    SessionFile = Path.GetFileName(path), SessionId = MiniJson.GetString(o, "session_id"), StepId = MiniJson.GetString(o, "step_id"), Seq = MiniJson.GetInt(o, "seq", 0),
                    DecisionIndex = MiniJson.GetInt(c, "decision_index", 0), Context = ContextJsonl.FromObject(c, MiniJson.GetString(o, "participant_id"), MiniJson.GetString(o, "session_id"))
                };
                Dictionary<string, object> resp = MiniJson.GetObject(o, "response");
                item.OriginalAction = resp != null ? MiniJson.GetString(resp, "parsed_action") : null;
                item.OriginalStatus = resp != null ? MiniJson.GetString(resp, "constraint_status") : null;
                item.OriginalProvider = resp != null ? MiniJson.GetString(resp, "provider_id") : null;
                if (Enum.TryParse(MiniJson.GetString(o, "level_before", "L1_Minimal"), out SupportLevel lb)) item.LevelBefore = lb;
                items.Add(item);
            }
            return items;
        }

        public static void Run(List<ReplayItem> items, IDecisionProvider provider, IDecisionProvider reference, int repeats, Action<string> log)
        {
            int done = 0;
            foreach (ReplayItem it in items)
            {
                if (reference != null) { try { it.ReferenceAction = reference.RequestDecision(ScenarioGenerator.Clone(it.Context)).ParsedAction; } catch (Exception) { } }
                for (int r = 0; r < repeats; r++)
                {
                    DecisionResponse resp;
                    try { resp = provider.RequestDecision(ScenarioGenerator.Clone(it.Context)) ?? DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, provider.ProviderId, null); }
                    catch (Exception e) { resp = DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, provider.ProviderId, "exception: " + e.Message); }
                    it.Responses.Add(resp);
                    done++;
                    if (log != null && done % 10 == 0) log($"replay {done}/{items.Count * repeats}");
                }
                int[] c = new int[3]; foreach (DecisionResponse resp in it.Responses) c[Stats.Ordinal(resp.ParsedAction)]++;
                int best = 0; for (int i = 1; i < 3; i++) if (c[i] > c[best]) best = i;
                it.Modal = best == 0 ? SupportAction.DECREASE_SUPPORT : (best == 1 ? SupportAction.KEEP_SUPPORT : SupportAction.INCREASE_SUPPORT);
                it.ModalShare = it.Responses.Count > 0 ? c[best] / (double)it.Responses.Count : double.NaN;
                it.AgreesWithOriginal = it.OriginalAction == it.Modal.ToString();
            }
        }

        public static void WriteCsv(string path, List<ReplayItem> items, int repeats)
        {
            var lines = new List<string> { Csv.Row("session_file", "session_id", "seq", "decision_index", "step_id", "level_before", "original_provider", "original_action", "original_status", "repeats", "keep", "increase", "decrease", "modal_action", "modal_share", "all_agree", "modal_agrees_with_original", "fallbacks", "mean_latency_ms", "reference_action") };
            foreach (ReplayItem it in items)
            {
                int k = it.Responses.Count(x => x.ParsedAction == SupportAction.KEEP_SUPPORT), inc = it.Responses.Count(x => x.ParsedAction == SupportAction.INCREASE_SUPPORT), dec = it.Responses.Count(x => x.ParsedAction == SupportAction.DECREASE_SUPPORT);
                lines.Add(Csv.Row(it.SessionFile, it.SessionId, it.Seq, it.DecisionIndex, it.StepId, it.LevelBefore, it.OriginalProvider, it.OriginalAction, it.OriginalStatus, repeats, k, inc, dec, it.Modal, it.ModalShare, it.ModalShare >= 0.999, it.AgreesWithOriginal,
                    it.Responses.Count(x => x.UsedFallback), it.Responses.Count > 0 ? it.Responses.Average(x => (double)x.LatencyMs) : 0, it.ReferenceAction.HasValue ? it.ReferenceAction.Value.ToString() : null));
            }
            Csv.Write(path, lines);
        }

        public static string Summary(List<ReplayItem> items)
        {
            if (items.Count == 0) return "No real-session decision contexts were replayed.";
            double modal = items.Average(i => i.ModalShare), allAgree = items.Count(i => i.ModalShare >= 0.999) / (double)items.Count;
            double orig = items.Count(i => i.AgreesWithOriginal) / (double)items.Count;
            var counts = items.Select(i => { int[] c = new int[3]; foreach (var r in i.Responses) c[Stats.Ordinal(r.ParsedAction)]++; return c; }).ToList();
            double kappa = Stats.FleissKappa(counts, 3);
            int fb = items.Sum(i => i.Responses.Count(r => r.UsedFallback)), total = items.Sum(i => i.Responses.Count);
            return $"{items.Count} recorded contexts from {items.Select(i => i.SessionFile).Distinct().Count()} session file(s), {total} resubmissions: mean modal share {modal:F3}, all-repeats-agree {allAgree:F3}, modal action equals the original headset decision {orig:F3}, Fleiss' kappa {Stats.F(kappa)}, fallbacks {fb}.";
        }
    }

    public sealed class MetamorphicPair
    {
        public string Family, SourceId; public DecisionContext A, B;
        public List<DecisionResponse> ResponsesA = new List<DecisionResponse>(), ResponsesB = new List<DecisionResponse>();
        public SupportAction ModalA, ModalB; public double Agreement = double.NaN;
    }

    public static class MetamorphicTests
    {
        /// <summary>Builds the paired variants of a context. Each family changes one thing and keeps the meaningful state equivalent.</summary>
        public static List<MetamorphicPair> Build(DecisionContext src, string sourceId)
        {
            var pairs = new List<MetamorphicPair>();
            // F1 physiology: missing vs present-but-neutral.
            {
                DecisionContext a = ScenarioGenerator.Clone(src); a.Physiological = null;
                DecisionContext b = ScenarioGenerator.Clone(src); b.Physiological = PhysiologyProfiles.Sample("nominal", 0);
                pairs.Add(new MetamorphicPair { Family = "physiology_missing_vs_neutral", SourceId = sourceId, A = a, B = b });
            }
            // F2 old errors shifted further into the past (outside the recent window) while the current episode is unchanged.
            if (src.RecentEvents != null && src.RecentEvents.Count > 0 && src.CurrentSupportEpisode != null)
            {
                DecisionContext a = ScenarioGenerator.Clone(src);
                DecisionContext b = ScenarioGenerator.Clone(src);
                float shift = Math.Max(src.RecentWindowMs, 60000f) + 1000f;
                foreach (TaskEvent e in b.RecentEvents) if (!IsInEpisode(src, e)) e.AgeMs += shift;
                b.StepElapsedMs += shift; b.OverallElapsedMs += shift;
                if (b.TimeSinceLastErrorMs.HasValue && (src.CurrentSupportEpisode.Errors == 0)) b.TimeSinceLastErrorMs += shift;
                if (b.TimeSinceLastSupportChangeMs.HasValue) b.TimeSinceLastSupportChangeMs += shift;
                b.CurrentSupportEpisode.DurationMs += shift;
                if (src.CurrentSupportEpisode.Events != null) foreach (TaskEvent e in b.CurrentSupportEpisode.Events) e.AgeMs += 0f;
                // Recent-window counts recomputed from the shifted ages.
                b.RecentErrorCount = b.RecentEvents.Count(e => e.AgeMs <= b.RecentWindowMs && TaskEvent.IsErrorLike(e.Kind));
                b.RecentFailedAttemptCount = b.RecentEvents.Count(e => e.AgeMs <= b.RecentWindowMs && TaskEvent.IsFailedAttempt(e.Kind));
                b.RecentWrongComponentCount = b.RecentEvents.Count(e => e.AgeMs <= b.RecentWindowMs && e.Kind == TaskEventKind.WrongComponent);
                pairs.Add(new MetamorphicPair { Family = "old_errors_shifted_into_past", SourceId = sourceId, A = a, B = b });
            }
            // F3 more elapsed time, no new errors.
            {
                DecisionContext a = ScenarioGenerator.Clone(src);
                DecisionContext b = ScenarioGenerator.Clone(src);
                float add = 120000f;
                b.StepElapsedMs += add; b.OverallElapsedMs += add;
                if (b.TimeSinceLastErrorMs.HasValue) b.TimeSinceLastErrorMs += add;
                if (b.TimeSinceLastSupportChangeMs.HasValue) b.TimeSinceLastSupportChangeMs += add;
                if (b.TimeSinceLastSuccessMs.HasValue) b.TimeSinceLastSuccessMs += add;
                if (b.RecentEvents != null) foreach (TaskEvent e in b.RecentEvents) e.AgeMs += add;
                if (b.CurrentSupportEpisode != null)
                {
                    b.CurrentSupportEpisode.DurationMs += add;
                    if (b.CurrentSupportEpisode.TimeSinceLastErrorMs.HasValue) b.CurrentSupportEpisode.TimeSinceLastErrorMs += add;
                    if (b.CurrentSupportEpisode.TimeSinceLastFailedAttemptMs.HasValue) b.CurrentSupportEpisode.TimeSinceLastFailedAttemptMs += add;
                    if (b.CurrentSupportEpisode.Events != null) foreach (TaskEvent e in b.CurrentSupportEpisode.Events) e.AgeMs += add;
                }
                b.RecentErrorCount = b.RecentEvents != null ? b.RecentEvents.Count(e => e.AgeMs <= b.RecentWindowMs && TaskEvent.IsErrorLike(e.Kind)) : 0;
                b.RecentFailedAttemptCount = b.RecentEvents != null ? b.RecentEvents.Count(e => e.AgeMs <= b.RecentWindowMs && TaskEvent.IsFailedAttempt(e.Kind)) : 0;
                b.RecentWrongComponentCount = b.RecentEvents != null ? b.RecentEvents.Count(e => e.AgeMs <= b.RecentWindowMs && e.Kind == TaskEventKind.WrongComponent) : 0;
                pairs.Add(new MetamorphicPair { Family = "elapsed_time_added_no_new_errors", SourceId = sourceId, A = a, B = b });
            }
            // F4 same recent difficulty, different irrelevant cumulative totals (other steps' errors).
            {
                DecisionContext a = ScenarioGenerator.Clone(src);
                DecisionContext b = ScenarioGenerator.Clone(src);
                b.ErrorsTotal += 12; b.SupportChangesTotal += 3; b.OverallElapsedMs += 600000f;
                pairs.Add(new MetamorphicPair { Family = "irrelevant_cumulative_totals", SourceId = sourceId, A = a, B = b });
            }
            return pairs;
        }

        private static bool IsInEpisode(DecisionContext c, TaskEvent e)
        {
            // Strictly inside: an event recorded at the same step time as the change (e.g. the wrong
            // part that triggered it) precedes the change in the ledger and belongs to the old episode.
            return c.CurrentSupportEpisode != null && e.AgeMs < c.CurrentSupportEpisode.DurationMs;
        }

        public static void Run(List<MetamorphicPair> pairs, IDecisionProvider provider, int repeats, Action<string> log)
        {
            int done = 0;
            foreach (MetamorphicPair p in pairs)
            {
                for (int r = 0; r < repeats; r++)
                {
                    p.ResponsesA.Add(Safe(provider, p.A)); p.ResponsesB.Add(Safe(provider, p.B));
                    done += 2;
                    if (log != null && done % 10 == 0) log($"metamorphic {done}/{pairs.Count * repeats * 2}");
                }
                p.ModalA = Modal(p.ResponsesA); p.ModalB = Modal(p.ResponsesB);
                int agree = 0; for (int i = 0; i < repeats; i++) if (p.ResponsesA[i].ParsedAction == p.ResponsesB[i].ParsedAction) agree++;
                p.Agreement = repeats > 0 ? agree / (double)repeats : double.NaN;
            }
        }

        private static DecisionResponse Safe(IDecisionProvider p, DecisionContext c)
        {
            try { return p.RequestDecision(ScenarioGenerator.Clone(c)) ?? DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, p.ProviderId, null); }
            catch (Exception e) { return DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, p.ProviderId, "exception: " + e.Message); }
        }

        private static SupportAction Modal(List<DecisionResponse> rs)
        {
            int[] c = new int[3]; foreach (DecisionResponse r in rs) c[Stats.Ordinal(r.ParsedAction)]++;
            int best = 1; for (int i = 0; i < 3; i++) if (c[i] > c[best]) best = i;
            return best == 0 ? SupportAction.DECREASE_SUPPORT : (best == 1 ? SupportAction.KEEP_SUPPORT : SupportAction.INCREASE_SUPPORT);
        }

        public static void WriteCsv(string path, List<MetamorphicPair> pairs)
        {
            var lines = new List<string> { Csv.Row("family", "source_id", "repeats", "modal_a", "modal_b", "paired_agreement", "a_keep", "a_increase", "a_decrease", "b_keep", "b_increase", "b_decrease") };
            foreach (MetamorphicPair p in pairs)
                lines.Add(Csv.Row(p.Family, p.SourceId, p.ResponsesA.Count, p.ModalA, p.ModalB, p.Agreement,
                    p.ResponsesA.Count(r => r.ParsedAction == SupportAction.KEEP_SUPPORT), p.ResponsesA.Count(r => r.ParsedAction == SupportAction.INCREASE_SUPPORT), p.ResponsesA.Count(r => r.ParsedAction == SupportAction.DECREASE_SUPPORT),
                    p.ResponsesB.Count(r => r.ParsedAction == SupportAction.KEEP_SUPPORT), p.ResponsesB.Count(r => r.ParsedAction == SupportAction.INCREASE_SUPPORT), p.ResponsesB.Count(r => r.ParsedAction == SupportAction.DECREASE_SUPPORT)));
            Csv.Write(path, lines);
        }

        public static string Summary(List<MetamorphicPair> pairs)
        {
            if (pairs.Count == 0) return "No metamorphic pairs were run.";
            var sb = new System.Text.StringBuilder();
            foreach (var g in pairs.GroupBy(p => p.Family))
                sb.AppendLine($"- `{g.Key}`: {g.Count()} pair(s), mean paired agreement {Stats.Mean(g.Select(p => p.Agreement).Where(x => !double.IsNaN(x)).ToList()):F3}, modal action differs in {g.Count(p => p.ModalA != p.ModalB)} pair(s).");
            return sb.ToString();
        }
    }
}
