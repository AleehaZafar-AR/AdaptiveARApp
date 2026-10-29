// File: SimulationRunner.cs
// Orchestrates a virtual-participant evaluation: every scenario under every physiology
// variant for N repetitions, optional real-session replay and metamorphic pairs, and the
// output set under eval_out/<timestamp>/ (raw JSONL + CSV summaries + evaluation_summary.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AdaptiveAR.Decision.Simulation
{
    public sealed class SimulationOptions
    {
        public string ProviderKind = "reference";
        public DecisionConfig Config = new DecisionConfig();
        public string ApiKey;
        public List<ScenarioScript> Scenarios = new List<ScenarioScript>();
        public string[] Physiology = PhysiologyProfiles.All;
        public int Repetitions = 1;
        /// <summary>Default false: provider wall-clock latency is a performance metric and never advances simulated participant time, so variants and repetitions share identical scripted timelines.</summary>
        public bool AdvanceClockByLatency = false;
        public List<string> ReplayFiles = new List<string>();
        public int ReplayRepeats = 5;
        public bool Metamorphic = true;
        public int MetamorphicRepeats = 3;
        /// <summary>Metamorphic source contexts per scenario (the first decision contexts of repetition 0 under the first physiology variant).</summary>
        public int MetamorphicSourcesPerScenario = 2;
        public string OutDir;
        public bool Verbose;
    }

    public sealed class SimulationResult
    {
        public List<SimulationRun> Runs = new List<SimulationRun>();
        public List<ReplayItem> Replay = new List<ReplayItem>();
        public List<MetamorphicPair> Metamorphic = new List<MetamorphicPair>();
        public string ProviderId, ReferenceId;
        public int ProviderCalls { get { return Runs.Sum(r => r.ProviderCalls) + Replay.Sum(i => i.Responses.Count) + Metamorphic.Sum(p => p.ResponsesA.Count + p.ResponsesB.Count); } }
        public DateTime StartedUtc, FinishedUtc;
    }

    public static class SimulationRunner
    {
        public static SimulationResult Run(SimulationOptions o, Action<string> log)
        {
            if (o.Scenarios.Count == 0) throw new ArgumentException("no scenarios");
            IDecisionProvider provider = ProviderFactory.Create(o.ProviderKind, o.Config, o.ApiKey);
            var reference = new ReferencePolicyProvider(o.Config.ReferencePolicy);
            var result = new SimulationResult { ProviderId = provider.ProviderId, ReferenceId = reference.ProviderId, StartedUtc = DateTime.UtcNow };

            int total = o.Scenarios.Count * o.Physiology.Length * o.Repetitions, done = 0;
            foreach (ScenarioScript s in o.Scenarios)
            foreach (string ph in o.Physiology)
            for (int rep = 0; rep < o.Repetitions; rep++)
            {
                var session = new VirtualSession(o.Config, provider, reference, o.AdvanceClockByLatency, o.Verbose ? log : null);
                SimulationRun run = session.Run(s, ph, rep);
                result.Runs.Add(run);
                done++;
                if (log != null) log($"[{done}/{total}] {s.Id} physiology={ph} rep={rep}: {run.ProviderCalls} call(s), {run.Keep}K/{run.Increase}I/{run.Decrease}D, final {run.FinalLevel}, max {run.MaxLevel}");
            }

            if (o.Metamorphic)
            {
                foreach (ScenarioScript s in o.Scenarios)
                {
                    SimulationRun src = result.Runs.FirstOrDefault(r => r.ScenarioId == s.Id && r.Repetition == 0 && r.PhysiologyVariant == o.Physiology[0]);
                    if (src == null) continue;
                    // The later contexts of a run carry the richest history; take the last N.
                    var picks = src.Decisions.Skip(Math.Max(0, src.Decisions.Count - o.MetamorphicSourcesPerScenario)).ToList();
                    foreach (DecisionRecord d in picks) result.Metamorphic.AddRange(MetamorphicTests.Build(d.Context, $"{s.Id}/{src.PhysiologyVariant}/r0/#{d.DecisionIndex}"));
                }
                if (result.Metamorphic.Count > 0) MetamorphicTests.Run(result.Metamorphic, provider, o.MetamorphicRepeats, log);
            }

            foreach (string f in o.ReplayFiles)
            {
                if (!File.Exists(f)) { if (log != null) log("replay file not found: " + f); continue; }
                List<ReplayItem> items = ReplayEvaluator.Load(f);
                if (log != null) log($"replay {Path.GetFileName(f)}: {items.Count} recorded decision context(s)");
                ReplayEvaluator.Run(items, provider, reference, o.ReplayRepeats, log);
                result.Replay.AddRange(items);
            }

            result.FinishedUtc = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(o.OutDir)) Write(o, result, log);
            return result;
        }

        public static void Write(SimulationOptions o, SimulationResult r, Action<string> log)
        {
            Directory.CreateDirectory(o.OutDir);
            SimulationMetrics.WriteRawDecisions(Path.Combine(o.OutDir, "raw_decisions.jsonl"), r.Runs);
            SimulationMetrics.WriteScenarioRuns(Path.Combine(o.OutDir, "scenario_runs.csv"), r.Runs);
            var decisions = SimulationMetrics.SummariseDecisions(r.Runs); SimulationMetrics.WriteDecisionSummary(Path.Combine(o.OutDir, "decision_summary.csv"), decisions);
            var traj = SimulationMetrics.SummariseTrajectories(r.Runs); SimulationMetrics.WriteTrajectorySummary(Path.Combine(o.OutDir, "trajectory_summary.csv"), traj);
            var lat = SimulationMetrics.SummariseLatency(r.Runs); SimulationMetrics.WriteLatencySummary(Path.Combine(o.OutDir, "latency_summary.csv"), lat);
            var pairs = SimulationMetrics.ComparePhysiology(r.Runs, o.Physiology); SimulationMetrics.WritePhysiologyPairs(Path.Combine(o.OutDir, "physiology_pairs.csv"), pairs);
            var refc = SimulationMetrics.CompareWithReference(r.Runs, r.ReferenceId); SimulationMetrics.WriteReferenceComparison(Path.Combine(o.OutDir, "reference_comparison.csv"), refc);
            ReplayEvaluator.WriteCsv(Path.Combine(o.OutDir, "replay_results.csv"), r.Replay, o.ReplayRepeats);
            MetamorphicTests.WriteCsv(Path.Combine(o.OutDir, "metamorphic_pairs.csv"), r.Metamorphic);
            using (var w = new StreamWriter(Path.Combine(o.OutDir, "trajectories.txt"), false, new UTF8Encoding(false)))
                foreach (SimulationRun run in r.Runs) { w.WriteLine(SimulationMetrics.Trajectory(run)); }
            File.WriteAllText(Path.Combine(o.OutDir, "run.json"), MiniJson.Serialize(new Dictionary<string, object>
            {
                { "provider", r.ProviderId }, { "reference", r.ReferenceId }, { "config", o.Config.Describe() }, { "scenarios", new List<object>(o.Scenarios.Select(s => (object)s.Id)) },
                { "physiology", new List<object>(o.Physiology) }, { "repetitions", (double)o.Repetitions }, { "advance_clock_by_latency", o.AdvanceClockByLatency },
                { "replay_files", new List<object>(o.ReplayFiles) }, { "replay_repeats", (double)o.ReplayRepeats }, { "metamorphic", o.Metamorphic }, { "metamorphic_repeats", (double)o.MetamorphicRepeats },
                { "cost_model_usd_per_mtok", new Dictionary<string, object> { { "input", CostModel.InputUsdPerMTok }, { "output", CostModel.OutputUsdPerMTok } } },
                { "started_utc", r.StartedUtc.ToString("o") }, { "finished_utc", r.FinishedUtc.ToString("o") }, { "provider_calls", (double)r.ProviderCalls }
            }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(o.OutDir, "evaluation_summary.md"), SummaryMarkdown(o, r, decisions, traj, lat, pairs, refc), new UTF8Encoding(false));
            if (log != null) log("written: " + Path.GetFullPath(o.OutDir));
        }

        public static string SummaryMarkdown(SimulationOptions o, SimulationResult r, List<SimulationMetrics.DecisionGroupSummary> decisions, List<SimulationMetrics.TrajectorySummary> traj, List<SimulationMetrics.LatencySummary> lat, List<SimulationMetrics.PhysiologyPair> pairs, SimulationMetrics.ReferenceComparison refc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Decision-layer technical evaluation: summary");
            sb.AppendLine();
            sb.AppendLine($"Provider under evaluation: `{r.ProviderId}`. Deterministic comparator: `{r.ReferenceId}` (placeholder thresholds; **not ground truth, not a validated gold standard**). Config: `{o.Config.Describe()}`.");
            sb.AppendLine($"Started {r.StartedUtc:u}, finished {r.FinishedUtc:u}. Provider calls: {r.ProviderCalls}. Descriptive statistics only; no causal claim is made.");
            sb.AppendLine();
            sb.AppendLine("## 1. Synthetic virtual-participant scenarios");
            sb.AppendLine();
            sb.AppendLine($"{o.Scenarios.Count} scripted scenario(s) × {o.Physiology.Length} physiology variant(s) × {o.Repetitions} repetition(s) = {r.Runs.Count} run(s). Task behaviour is identical across repetitions and physiology variants; only the support level moves, and only through the real pipeline (ledger → request gate → provider → structural constraints).");
            sb.AppendLine();
            foreach (ScenarioScript s in o.Scenarios) sb.AppendLine($"- **{s.Id}** — {s.Title}. {s.Description} Expected (qualitative, not asserted): {s.Expected}");
            sb.AppendLine();
            sb.AppendLine("### 1.1 Reliability and structural validity");
            sb.AppendLine();
            int totalDec = decisions.Sum(d => d.Decisions), ok = decisions.Sum(d => d.Ok), fb = decisions.Sum(d => d.Fallback), clamps = decisions.Sum(d => d.BoundaryClamps), multi = decisions.Sum(d => d.MultiLevelTransitions), imp = decisions.Sum(d => d.ImpossibleTransitions), mal = decisions.Sum(d => d.Malformed), perr = decisions.Sum(d => d.ProviderErrors);
            sb.AppendLine($"Total decisions {totalDec}; valid parse rate {Rate(ok, totalDec)}; fallback rate {Rate(fb, totalDec)}; malformed outputs {mal}; provider errors/timeouts {perr}; boundary clamps (increase at L3 / decrease at L1 attempted) {clamps}; multi-level transitions {multi} (structural one-level compliance {Rate(totalDec - multi, totalDec)}); impossible levels {imp}.");
            sb.AppendLine();
            sb.AppendLine("### 1.2 Decision consistency (repeated identical contexts)");
            sb.AppendLine();
            sb.AppendLine("| scenario | physiology | decisions | KEEP | INCREASE | DECREASE | identical-context groups | repeat consistency | modal agreement | Fleiss' κ |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var d in decisions) sb.AppendLine($"| {d.ScenarioId} | {d.Physiology} | {d.Decisions} | {d.Keep} | {d.Increase} | {d.Decrease} | {d.IdenticalContextGroups} | {(d.IdenticalContextGroups > 0 ? Stats.F(d.IdenticalContextGroupsAllAgree / (double)d.IdenticalContextGroups) : "")} | {Stats.F(d.ModalAgreement)} | {Stats.F(d.FleissKappa)} |");
            sb.AppendLine();
            sb.AppendLine("Identical-context groups are decisions whose exact AI input (minus run identity) recurred across repetitions, i.e. before the trajectories diverged. Fleiss' κ is computed over the groups sharing the modal group size; it is undefined (blank) when every rating falls in one category.");
            sb.AppendLine();
            sb.AppendLine("### 1.3 Adaptation trajectory, responsiveness and recovery");
            sb.AppendLine();
            sb.AppendLine("| scenario | physiology | runs | P(reach L2) | P(reach L3) | P(any decrease) | mean AI changes | mean oscillations | mean calls | median events onset→first increase | median s onset→first increase | median s to first decrease | median episode successes before decrease | recovery decisions | KEEP rate in recovery | final levels |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var t in traj) sb.AppendLine($"| {t.ScenarioId} | {t.Physiology} | {t.Runs} | {Stats.F(t.ReachL2, "F2")} | {Stats.F(t.ReachL3, "F2")} | {Stats.F(t.AnyDecrease, "F2")} | {Stats.F(t.MeanChanges, "F2")} | {Stats.F(t.MeanOscillations, "F2")} | {Stats.F(t.MeanCalls, "F1")} | {Stats.F(t.MedianEventsToFirstIncrease, "F0")} | {Stats.F(t.MedianTimeToFirstIncreaseS, "F1")} | {Stats.F(t.MedianTimeToFirstDecreaseS, "F1")} | {Stats.F(t.MedianSuccessesBeforeDecrease, "F0")} | {t.RecoveryDecisions} | {Stats.F(t.KeepRateDuringRecovery, "F2")} | {t.FinalLevels} |");
            sb.AppendLine();
            sb.AppendLine("Recovery decisions are those taken above L1 with a clean current support episode holding at least one success. A KEEP during recovery is reported, not graded.");
            sb.AppendLine();
            sb.AppendLine("### 1.4 Physiology sensitivity (paired runs, identical task behaviour)");
            sb.AppendLine();
            sb.AppendLine("| scenario | A | B | paired runs | matched decisions | agreement | changed | P(final level differs) | P(reach L3 differs) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var p in pairs) sb.AppendLine($"| {p.ScenarioId} | {p.A} | {p.B} | {p.Pairs} | {p.MatchedDecisions} | {Stats.F(p.Agreement)} | {p.Changed} | {Stats.F(p.FinalLevelDiffers, "F2")} | {Stats.F(p.ReachL3Differs, "F2")} |");
            sb.AppendLine();
            sb.AppendLine("Decisions are matched while the task evidence is identical (same AI input with the physiological block removed); after the trajectories diverge they are no longer paired. This measures sensitivity to supplementary context, not medical validity.");
            sb.AppendLine();
            sb.AppendLine("### 1.5 Metamorphic pairs");
            sb.AppendLine();
            sb.AppendLine(MetamorphicTests.Summary(r.Metamorphic));
            sb.AppendLine("Each family changes one feature of a recorded context while the meaningful state stays equivalent; the paired agreement shows how far decisions follow stale or irrelevant information. No expected action is prescribed.");
            sb.AppendLine();
            sb.AppendLine("### 1.6 Latency and provider reliability");
            sb.AppendLine();
            sb.AppendLine("| provider | calls | mean ms | p50 ms | p95 ms | max ms | request failures | malformed | fallbacks | input tokens | output tokens | est. cost USD |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var l in lat) sb.AppendLine($"| {l.Provider} | {l.Calls} | {Stats.F(l.MeanMs, "F0")} | {Stats.F(l.P50Ms, "F0")} | {Stats.F(l.P95Ms, "F0")} | {Stats.F(l.MaxMs, "F0")} | {l.Failures} | {l.Malformed} | {l.Fallbacks} | {l.InputTokens} | {l.OutputTokens} | {(l.WithUsage > 0 ? Stats.F(l.CostUsd, "F4") : "n/a")} |");
            sb.AppendLine();
            sb.AppendLine($"Cost model assumption: {CostModel.InputUsdPerMTok} USD / M input tokens, {CostModel.OutputUsdPerMTok} USD / M output tokens (set with price_in= / price_out=).");
            sb.AppendLine();
            sb.AppendLine("## 2. Real-session replay");
            sb.AppendLine();
            sb.AppendLine(ReplayEvaluator.Summary(r.Replay));
            sb.AppendLine("Recorded headset decision contexts resubmitted as-is; consistency on real contexts, separate from the synthetic trajectories.");
            sb.AppendLine();
            sb.AppendLine("## 3. Deterministic reference comparison");
            sb.AppendLine();
            string[] names = { "DECREASE", "KEEP", "INCREASE" };
            sb.AppendLine($"Over {refc.Decisions} valid decisions: raw agreement with `{r.ReferenceId}` {Stats.F(refc.Agreement)}, Cohen's weighted κ (linear, ordered actions) {Stats.F(refc.WeightedKappa)}.");
            sb.AppendLine();
            sb.AppendLine("| provider \\ reference | DECREASE | KEEP | INCREASE |");
            sb.AppendLine("|---|---|---|---|");
            for (int i = 0; i < 3; i++) sb.AppendLine($"| {names[i]} | {refc.Confusion[i, 0]} | {refc.Confusion[i, 1]} | {refc.Confusion[i, 2]} |");
            sb.AppendLine();
            sb.AppendLine("The reference policy is a transparent deterministic comparator with placeholder thresholds. Agreement with it is a descriptive statistic, not a correctness score.");
            return sb.ToString();
        }

        private static string Rate(int a, int n) { return n > 0 ? (a / (double)n).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "n/a"; }
    }
}
