// Headless tests for the virtual-participant simulation harness (Tools/DecisionEval/Simulation).
// They drive the real TaskEventLedger / RequestGate / DecisionConstraints / ReferencePolicy
// through VirtualSession on a fake clock, with canned providers where determinism is needed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdaptiveAR.Decision;
using AdaptiveAR.Decision.Simulation;
using AdaptiveAR.Support;

namespace AdaptiveAR.Physiology.Tests
{
    public static class SimulationTests
    {
        private static string ScenarioDir() { return Path.Combine(Program.RepoRoot(), "Tools", "DecisionEval", "Scenarios"); }
        private static DecisionConfig Cfg() { return DecisionConfig.FromJson(File.ReadAllText(Path.Combine(Program.RepoRoot(), "Assets", "Resources", "Decision", "decision_config.json")), "file"); }

        private static ScenarioScript Script(string id, params object[] events)
        {
            // Compact builder: "step:id:complexity:expected", "action:id", "adv:seconds", "fail", "err", "wrong:part", "ok", "phys:profile"
            var list = new List<object>();
            foreach (object e in events)
            {
                string s = (string)e; string[] p = s.Split(':');
                switch (p[0])
                {
                    case "step": list.Add(new Dictionary<string, object> { { "t", "step" }, { "id", p[1] }, { "complexity", double.Parse(p[2]) }, { "expected_s", double.Parse(p[3]) } }); break;
                    case "action": list.Add(new Dictionary<string, object> { { "t", "action" }, { "id", p[1] } }); break;
                    case "adv": list.Add(new Dictionary<string, object> { { "t", "advance" }, { "s", double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) } }); break;
                    case "fail": list.Add(new Dictionary<string, object> { { "t", "failed_attempt" } }); break;
                    case "err": list.Add(new Dictionary<string, object> { { "t", "error" } }); break;
                    case "wrong": list.Add(new Dictionary<string, object> { { "t", "wrong_component" }, { "part", p[1] } }); break;
                    case "ok": list.Add(new Dictionary<string, object> { { "t", "success" } }); break;
                    case "phys": list.Add(new Dictionary<string, object> { { "t", "physiology" }, { "profile", p[1] } }); break;
                }
            }
            return ScenarioScript.Parse(MiniJson.Serialize(new Dictionary<string, object> { { "id", id }, { "title", id }, { "events", list } }), "inline");
        }

        public static void Run(Action<string, bool, string> check)
        {
            void Check(string name, bool ok, string detail = "") { check(name, ok, detail); }
            DecisionConfig cfg = Cfg();   // 4 s call protection, 15 s cooldown, 2 error-like events per request, 8 s flush, first look 10 s, 60 s window

            // 30a fake clock
            { var c = new FakeClock(); c.Advance(1.5); c.AdvanceMs(250); bool threw = false; try { c.Advance(-1); } catch (ArgumentException) { threw = true; }
              Check("30a fake clock advances and refuses to go back", Math.Abs(c.NowSeconds - 1.75) < 1e-9 && threw); }

            // 30b scenario parsing: the eight shipped scenarios parse, start with a step, script no support change
            { var all = ScenarioScript.LoadAll(ScenarioDir());
              Check("30b eight scenarios parse", all.Count == 8 && all.All(s => s.Events[0].Kind == ScenarioEventKind.Step) && all.Select(s => s.Id).SequenceEqual(all.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal)), string.Join(",", all.Select(s => s.Id)));
              var s2 = all.First(s => s.Id.StartsWith("S2")); var s6 = all.First(s => s.Id.StartsWith("S6"));
              Check("30b2 S2 has difficulty then a clean run; S6 has no error-like events and long advances", s2.Count(ScenarioEventKind.Error) == 2 && s2.Count(ScenarioEventKind.WrongComponent) == 1 && s2.Count(ScenarioEventKind.Success) == 9
                    && s6.Count(ScenarioEventKind.Error) == 0 && s6.Count(ScenarioEventKind.WrongComponent) == 0 && !s6.DifficultyOnsetSeconds.HasValue && s6.ScriptedSeconds > 500, $"{s2.Count(ScenarioEventKind.Success)} {s6.ScriptedSeconds}");
              bool bad = false; try { ScenarioScript.Parse("{\"id\":\"x\",\"events\":[{\"t\":\"advance\",\"s\":1}]}"); } catch (FormatException) { bad = true; }
              bool bad2 = false; try { ScenarioScript.Parse("{\"id\":\"x\",\"events\":[{\"t\":\"step\",\"id\":\"s\"},{\"t\":\"support_change\"}]}"); } catch (FormatException) { bad2 = true; }
              Check("30b3 a scenario must start with a step and cannot script a support change", bad && bad2); }

            // 30c scheduler timing through the session: two errors one second apart -> one request; a lone error flushes after 8 s of simulated time
            { var sc = Script("t_timing", "step:s1:1:60", "action:a1", "adv:2", "err", "adv:1", "err", "adv:1", "err", "adv:30", "ok");
              var run = new VirtualSession(cfg, new CannedProvider("keep"), null, false, null).Run(sc, "null", 0);
              Check("30c two errors -> one request at the second; third error accrues and flushes after 8 s", run.ProviderCalls == 2 && run.Decisions[0].Trigger == "counted_error+2ev(2err,0ok)" && run.Decisions[1].Trigger == "pending_flush+1ev(1err,0ok)"
                    && Math.Abs(run.Decisions[0].SimTimeS - 3.0) < 1e-6 && Math.Abs(run.Decisions[1].SimTimeS - 12.0) < 0.26, string.Join(" | ", run.Decisions.Select(d => $"{d.Trigger}@{d.SimTimeS:F2}")));
              Check("30c2 production ordering: the context of an error-triggered request excludes the triggering error", run.Decisions[0].Context.ErrorsOnStep == 1 && run.Decisions[0].Context.ErrorsSinceLastSupportChange == 1 && run.Decisions[0].PendingErrors == 2); }

            // 30d deferred request: after an applied change the next error pair is held by the 15 s cooldown and fires when it ends
            { var sc = Script("t_defer", "step:s1:1:60", "action:a1", "adv:2", "err", "adv:1", "err", "adv:1", "err", "adv:1", "err", "adv:30", "ok");
              var run = new VirtualSession(cfg, new CannedProvider("sequence:INCREASE_SUPPORT,KEEP_SUPPORT"), null, false, null).Run(sc, "null", 0);
              var d1 = run.Decisions[1];
              Check("30d change cooldown defers, then fires with deferred_from", run.ProviderCalls == 2 && run.Decisions[0].Applied && run.Decisions[0].LevelAfter == SupportLevel.L2_Guided
                    && d1.DeferredFrom == "counted_error" && d1.SimTimeS >= 18.0 - 1e-6 && d1.SimTimeS < 18.5 && d1.PendingErrors == 2 && run.DeferredTriggers >= 1, string.Join(" | ", run.Decisions.Select(d => $"{d.Trigger}@{d.SimTimeS:F2} from={d.DeferredFrom}")) + " gate=" + string.Join("; ", run.GateLog)); }

            // 30e support episode resets at the applied change and holds only later events
            { var sc = Script("t_episode", "step:s1:1:60", "action:a1", "adv:2", "err", "adv:1", "err", "adv:20", "ok", "action:a2", "adv:5", "ok", "action:a3", "adv:5", "ok", "action:a4", "adv:5", "ok");
              var run = new VirtualSession(cfg, new CannedProvider("sequence:INCREASE_SUPPORT,KEEP_SUPPORT,KEEP_SUPPORT,KEEP_SUPPORT"), null, false, null).Run(sc, "null", 0);
              var later = run.Decisions.Where(d => d.DecisionIndex > 0).ToList();
              Check("30e episode after the increase excludes the pre-change errors while the recent window keeps them", run.Decisions[0].LevelAfter == SupportLevel.L2_Guided && later.Count >= 1
                    && later.All(d => d.Context.CurrentSupportEpisode.StartedBy == "support_increase" && d.Context.CurrentSupportEpisode.Errors == 0 && d.Context.CurrentSupportEpisode.Events.All(e => e.Kind == TaskEventKind.Success))
                    && later.Last().Context.CurrentSupportEpisode.Successes >= 2 && later.Last().Context.RecentErrorCount == 2 && later.Last().Context.ErrorsSinceLastSupportChange == 0,
                    later.Count > 0 ? later.Last().Evidence : "no later decision"); }

            // 30f physiology injection: identical task stream; null vs nominal vs stale
            { var sc = Script("t_phys", "step:s1:1:60", "action:a1", "adv:2", "err", "adv:1", "err", "adv:10", "ok");
              var a = new VirtualSession(cfg, new CannedProvider("keep"), null, false, null).Run(sc, "null", 0);
              var b = new VirtualSession(cfg, new CannedProvider("keep"), null, false, null).Run(sc, "nominal", 0);
              var c = new VirtualSession(cfg, new CannedProvider("keep"), null, false, null).Run(sc, "stale", 0);
              Check("30f physiology variants: null is missing, nominal present, stale low-quality/old; task evidence identical",
                    a.Decisions[0].Context.PhysiologicalMissing && !b.Decisions[0].Context.PhysiologicalMissing && b.Decisions[0].Context.Physiological.HeartRateBpm == 72f
                    && c.Decisions[0].Context.Physiological.Quality < 0.5f && c.Decisions[0].Context.Physiological.AgeMs > 30000f
                    && SimulationMetrics.StripPhysiology(a.Decisions[0].AiInputKey) == SimulationMetrics.StripPhysiology(b.Decisions[0].AiInputKey)
                    && a.Decisions[0].AiInputKey != b.Decisions[0].AiInputKey);
              var sw = Script("t_phys_switch", "step:s1:1:60", "action:a1", "adv:2", "err", "adv:1", "err", "phys:elevated", "adv:1", "err", "adv:1", "err", "adv:10", "ok");
              var d = new VirtualSession(cfg, new CannedProvider("keep"), null, false, null).Run(sw, "null", 0);
              Check("30f2 a scripted physiology event switches the profile mid-run", d.Decisions.Count == 2 && d.Decisions[0].Context.PhysiologicalMissing && d.Decisions[1].Context.Physiological != null && d.Decisions[1].Context.Physiological.HeartRateBpm == 96f); }

            // 30g trajectory recording: transitions, counts, step default reset, boundary clamp at L3
            { var sc = Script("t_traj", "step:s1:1:60", "action:a1", "adv:2", "err", "adv:1", "err", "adv:20", "err", "adv:1", "err", "adv:20", "err", "adv:1", "err", "adv:20", "ok", "step:s2:1:60", "action:b1", "adv:5", "ok");
              var run = new VirtualSession(cfg, new CannedProvider("sequence:INCREASE_SUPPORT"), null, false, null).Run(sc, "null", 0);
              var ai = run.Transitions.Where(t => t.Reason == "ai_decision").ToList();
              Check("30g trajectory: L1->L2->L3 by AI, clamp recorded at L3, step entry resets to L1 (step_default), counts", ai.Count == 2 && ai[0].To == SupportLevel.L2_Guided && ai[1].To == SupportLevel.L3_Assisted
                    && run.Decisions.Any(d => d.ConstraintApplied == "boundary_clamp" && d.Status == ConstraintStatus.SaturatedAtBoundary && !d.Applied)
                    && run.Transitions.Any(t => t.Reason == "step_default" && t.From == SupportLevel.L3_Assisted && t.To == SupportLevel.L1_Minimal)
                    && run.MaxLevel == SupportLevel.L3_Assisted && run.FinalLevel == SupportLevel.L1_Minimal && run.AiIncreases == 2 && run.AiDecreases == 0 && run.Increase == run.ProviderCalls
                    && run.FirstIncreaseS.HasValue && run.DifficultyOnsetS.HasValue && run.FirstIncreaseS.Value >= run.DifficultyOnsetS.Value && run.Trajectory.Count > 5,
                    string.Join(" | ", run.Transitions.Select(t => $"{t.From}->{t.To} {t.Reason}")) + " clamps=" + run.Decisions.Count(d => d.ConstraintApplied == "boundary_clamp"));
              Check("30g2 every decision line carries the structural set and the applied action", run.Decisions.All(d => d.AllowedActions.Contains("KEEP_SUPPORT") && d.AiInput.Contains("\"allowed_actions\"") && (d.Applied ? d.AppliedAction == d.ParsedAction : d.AppliedAction == SupportAction.KEEP_SUPPORT))); }

            // 30h canned provider is deterministic: the same scenario twice gives identical decisions; malformed output -> fallback recorded
            { var sc = ScenarioScript.LoadAll(ScenarioDir()).First(s => s.Id.StartsWith("S2"));
              var r1 = new VirtualSession(cfg, new CannedProvider("episode"), null, false, null).Run(sc, "nominal", 0);
              var r2 = new VirtualSession(cfg, new CannedProvider("episode"), null, false, null).Run(sc, "nominal", 1);
              Check("30h canned provider deterministic across repetitions", r1.Decisions.Count == r2.Decisions.Count && r1.Decisions.Zip(r2.Decisions, (x, y) => x.ParsedAction == y.ParsedAction && x.AiInputKey == y.AiInputKey && Math.Abs(x.SimTimeS - y.SimTimeS) < 1e-9).All(x => x)
                    && r1.Transitions.Count == r2.Transitions.Count && r1.ProviderCalls > 0, $"{r1.ProviderCalls} vs {r2.ProviderCalls}");
              var m = new VirtualSession(cfg, new CannedProvider("malformed"), null, false, null).Run(sc, "null", 0);
              Check("30h2 malformed output -> Unparseable fallback KEEP, level unchanged, constraint_applied recorded", m.Decisions.Count > 0 && m.Decisions.All(d => d.Status == ConstraintStatus.Unparseable && d.UsedFallback && d.ConstraintApplied == "fallback:Unparseable" && d.LevelAfter == d.LevelBefore) && m.MaxLevel == SupportLevel.L1_Minimal,
                    string.Join(" | ", m.Decisions.Select(d => $"{d.Status}/{d.UsedFallback}/{d.ConstraintApplied}/{d.RawOutput}"))); }

            // 30i reference comparator runs alongside and is recorded per decision; it never moves the level in a canned run
            { var sc = ScenarioScript.LoadAll(ScenarioDir()).First(s => s.Id.StartsWith("S3"));
              var refp = new ReferencePolicyProvider(cfg.ReferencePolicy);
              var run = new VirtualSession(cfg, new CannedProvider("keep"), refp, false, null).Run(sc, "null", 0);
              Check("30i comparator recorded, not applied", run.Decisions.All(d => d.ReferenceAction.HasValue) && run.Decisions.Any(d => d.ReferenceAction == SupportAction.INCREASE_SUPPORT) && run.MaxLevel == SupportLevel.L1_Minimal,
                    string.Join(" | ", run.Decisions.Select(d => $"{d.Trigger}:{(d.ReferenceAction.HasValue ? d.ReferenceAction.Value.ToString() : "null")} {d.ReferenceReason}"))); }

            // 30j summary calculations: Fleiss' kappa, weighted kappa, percentiles, decision/trajectory summaries over canned runs
            { double k1 = Stats.FleissKappa(new List<int[]> { new[] { 3, 0, 0 }, new[] { 0, 3, 0 }, new[] { 0, 0, 3 } }, 3);
              double k2 = Stats.FleissKappa(new List<int[]> { new[] { 1, 1, 1 }, new[] { 1, 1, 1 } }, 3);
              double k3 = Stats.FleissKappa(new List<int[]> { new[] { 0, 3, 0 }, new[] { 0, 3, 0 } }, 3);
              double wk = Stats.WeightedKappaLinear(new int[,] { { 5, 0, 0 }, { 0, 5, 0 }, { 0, 0, 5 } });
              double wk0 = Stats.WeightedKappaLinear(new int[,] { { 0, 0, 5 }, { 0, 0, 0 }, { 5, 0, 0 } });
              Check("30j kappas: perfect agreement 1, chance-level ~ negative, all-one-category undefined; weighted kappa 1 for identity, -1 for full reversal",
                    Math.Abs(k1 - 1) < 1e-9 && k2 < 0 && double.IsNaN(k3) && Math.Abs(wk - 1) < 1e-9 && Math.Abs(wk0 + 1) < 1e-9, $"{k1} {k2} {k3} {wk} {wk0}");
              var v = new List<double> { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
              Check("30j2 percentiles", Math.Abs(Stats.Percentile(v, 0.5) - 55) < 1e-9 && Math.Abs(Stats.Percentile(v, 0.95) - 95.5) < 1e-9 && Stats.Max(v) == 100 && Math.Abs(Stats.Mean(v) - 55) < 1e-9);
              var runs = new List<SimulationRun>();
              var sc = ScenarioScript.LoadAll(ScenarioDir()).First(s => s.Id.StartsWith("S2"));
              foreach (string ph in new[] { "null", "nominal" }) for (int rep = 0; rep < 3; rep++) runs.Add(new VirtualSession(cfg, new CannedProvider("episode"), new ReferencePolicyProvider(cfg.ReferencePolicy), false, null).Run(sc, ph, rep));
              var ds = SimulationMetrics.SummariseDecisions(runs); var ts = SimulationMetrics.SummariseTrajectories(runs); var ls = SimulationMetrics.SummariseLatency(runs);
              var pp = SimulationMetrics.ComparePhysiology(runs, new[] { "null", "nominal" }); var rc = SimulationMetrics.CompareWithReference(runs, "reference:placeholder_v1");
              var d0 = ds.First(x => x.Physiology == "null"); var t0 = ts.First(x => x.Physiology == "null");
              Check("30j3 decision summary over 3 identical canned repetitions: all identical-context groups agree, valid parse 1, no fallback",
                    ds.Count == 2 && d0.Runs == 3 && d0.Decisions == 3 * runs[0].Decisions.Count && d0.IdenticalContextGroups == runs[0].Decisions.Count && d0.IdenticalContextGroupsAllAgree == d0.IdenticalContextGroups && Math.Abs(d0.ModalAgreement - 1) < 1e-9 && d0.Fallback == 0 && d0.Ok == d0.Decisions, $"{d0.Decisions} {d0.IdenticalContextGroups}/{d0.IdenticalContextGroupsAllAgree}");
              Check("30j4 trajectory summary: S2 with the canned episode rule rises in every run and decreases after the clean run (at the 2-success look the gate offers)", t0.Runs == 3 && Math.Abs(t0.ReachL2 - 1) < 1e-9 && Math.Abs(t0.AnyDecrease - 1) < 1e-9 && t0.MedianSuccessesBeforeDecrease.HasValue && t0.MedianSuccessesBeforeDecrease.Value >= 2 && t0.MedianTimeToFirstDecreaseS.HasValue, $"{t0.ReachL2} {t0.AnyDecrease} {t0.MedianSuccessesBeforeDecrease} {t0.FinalLevels}");
              Check("30j5 latency summary, physiology pairing (canned ignores physiology -> full agreement), reference comparison populated", ls.Count == 1 && ls[0].Calls == d0.Decisions * 2 && pp.Count == 1 && pp[0].Pairs == 3 && pp[0].MatchedDecisions > 0 && Math.Abs(pp[0].Agreement - 1) < 1e-9 && rc.Decisions == ls[0].Calls && !double.IsNaN(rc.Agreement), $"{pp[0].MatchedDecisions} {rc.Decisions}");
              string tmp = Path.Combine(Path.GetTempPath(), "adaptivear_sim_test_" + Guid.NewGuid().ToString("N"));
              var opts = new SimulationOptions { ProviderKind = "canned:episode", Config = cfg, Scenarios = new List<ScenarioScript> { sc }, Physiology = new[] { "null", "nominal" }, Repetitions = 2, OutDir = tmp, Metamorphic = true, MetamorphicRepeats = 1, AdvanceClockByLatency = false };
              var res = SimulationRunner.Run(opts, null);
              string[] expected = { "raw_decisions.jsonl", "scenario_runs.csv", "decision_summary.csv", "trajectory_summary.csv", "latency_summary.csv", "physiology_pairs.csv", "reference_comparison.csv", "replay_results.csv", "metamorphic_pairs.csv", "evaluation_summary.md", "run.json", "trajectories.txt" };
              bool allFiles = expected.All(f => File.Exists(Path.Combine(tmp, f)));
              int rawLines = File.ReadAllLines(Path.Combine(tmp, "raw_decisions.jsonl")).Length;
              var firstRaw = MiniJson.ParseObject(File.ReadLines(Path.Combine(tmp, "raw_decisions.jsonl")).First());
              Check("30j6 output set written; raw record carries context, episode, allowed actions, response and applied action", allFiles && rawLines == res.Runs.Sum(r => r.Decisions.Count) && res.Metamorphic.Count > 0
                    && MiniJson.GetObject(firstRaw, "context") != null && MiniJson.GetObject(MiniJson.GetObject(firstRaw, "context"), "current_support_episode") != null && MiniJson.GetList(firstRaw, "allowed_actions") != null
                    && MiniJson.GetObject(firstRaw, "response") != null && MiniJson.GetString(firstRaw, "applied_action") != null && File.ReadAllText(Path.Combine(tmp, "evaluation_summary.md")).Contains("not ground truth"), tmp);
              try { Directory.Delete(tmp, true); } catch (Exception) { } }

            // 30k metamorphic pair construction keeps the current episode and changes exactly one thing
            { var sc = ScenarioScript.LoadAll(ScenarioDir()).First(s => s.Id.StartsWith("S2"));
              var run = new VirtualSession(cfg, new CannedProvider("episode"), null, false, null).Run(sc, "null", 0);
              DecisionRecord src = run.Decisions.Last(d => d.Context.CurrentSupportEpisode != null && d.Context.CurrentSupportEpisode.Successes >= 1);
              var pairs = MetamorphicTests.Build(src.Context, "t");
              var f1 = pairs.First(p => p.Family == "physiology_missing_vs_neutral"); var f2 = pairs.First(p => p.Family == "old_errors_shifted_into_past"); var f3 = pairs.First(p => p.Family == "elapsed_time_added_no_new_errors"); var f4 = pairs.First(p => p.Family == "irrelevant_cumulative_totals");
              Check("30k metamorphic families", pairs.Count == 4 && f1.A.PhysiologicalMissing && !f1.B.PhysiologicalMissing
                    && f2.B.RecentErrorCount == f2.A.CurrentSupportEpisode.Errors && f2.B.RecentErrorCount < f2.A.RecentErrorCount && f2.B.CurrentSupportEpisode.Successes == f2.A.CurrentSupportEpisode.Successes && f2.B.ErrorsOnStep == f2.A.ErrorsOnStep
                    && f3.B.StepElapsedMs - f3.A.StepElapsedMs == 120000f && f3.B.ErrorsOnStep == f3.A.ErrorsOnStep && f3.B.CurrentSupportEpisode.Errors == f3.A.CurrentSupportEpisode.Errors
                    && f4.B.ErrorsTotal == f4.A.ErrorsTotal + 12 && f4.B.ErrorsOnStep == f4.A.ErrorsOnStep && f4.B.RecentErrorCount == f4.A.RecentErrorCount, $"{f2.A.RecentErrorCount}->{f2.B.RecentErrorCount}");
              MetamorphicTests.Run(pairs, new CannedProvider("keep"), 2, null);
              Check("30k2 metamorphic run records paired agreement", pairs.All(p => p.ResponsesA.Count == 2 && p.ResponsesB.Count == 2 && Math.Abs(p.Agreement - 1) < 1e-9)); }

            // 30l real-session replay loader on a recorded line (synthetic here) and consistency output
            { var ctx = new DecisionContext { StepId = "s", CurrentSupportLevel = SupportLevel.L2_Guided, ErrorsOnStep = 2 };
              string line = "{\"event\":\"decision\",\"seq\":9,\"session_id\":\"S\",\"participant_id\":\"P\",\"step_id\":\"s\",\"level_before\":\"L2_Guided\",\"context\":" + LlmDecisionCore.ContextToJson(ctx) + ",\"response\":{\"provider_id\":\"anthropic:x\",\"parsed_action\":\"KEEP_SUPPORT\",\"constraint_status\":\"Ok\"}}";
              string tmp = Path.GetTempFileName(); File.WriteAllLines(tmp, new[] { "{\"event\":\"step_enter\"}", line, "not json" });
              var items = ReplayEvaluator.Load(tmp);
              ReplayEvaluator.Run(items, new CannedProvider("keep"), new ReferencePolicyProvider(cfg.ReferencePolicy), 4, null);
              Check("30l replay: one decision context loaded, 4 resubmissions, modal KEEP agrees with the original, reference recorded", items.Count == 1 && items[0].Seq == 9 && items[0].LevelBefore == SupportLevel.L2_Guided && items[0].Responses.Count == 4 && items[0].Modal == SupportAction.KEEP_SUPPORT && items[0].AgreesWithOriginal && items[0].ReferenceAction.HasValue && Math.Abs(items[0].ModalShare - 1) < 1e-9);
              try { File.Delete(tmp); } catch (Exception) { } }

            // 30m success-only evidence: a long clean run at L2 under a KEEP provider produces few calls (backoff), none at L1
            { var sc = Script("t_clean", "step:s1:1:60", "action:a1", "adv:2", "err", "adv:1", "err", "adv:12", "ok", "action:a2", "adv:5", "ok", "action:a3", "adv:5", "ok", "action:a4", "adv:5", "ok", "action:a5", "adv:5", "ok", "action:a6", "adv:5", "ok", "action:a7", "adv:5", "ok", "action:a8", "adv:5", "ok");
              var up = new VirtualSession(cfg, new CannedProvider("sequence:INCREASE_SUPPORT,KEEP_SUPPORT"), null, false, null).Run(sc, "null", 0);
              var flat = new VirtualSession(cfg, new CannedProvider("keep"), null, false, null).Run(sc, "null", 0);
              Check("30m clean run: at L2 success-only calls back off (2 then 4 successes); at L1 no success-only call at all", up.ProviderCalls == 3 && up.Decisions[1].PendingSuccesses == 2 && up.Decisions[2].PendingSuccesses == 4 && flat.ProviderCalls == 1,
                    string.Join(" | ", up.Decisions.Select(d => d.Trigger)) + " || " + string.Join(" | ", flat.Decisions.Select(d => d.Trigger))); }
        }
    }
}
