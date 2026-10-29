// Plain-C# tests for the Level 2 technical-validation layer: config parsing, scenario
// parsing (support levels cannot be scripted), delay scaling, every integration assertion on
// synthetic run records, the summary writers, and the six bundled TV scenarios.
using System;
using System.Collections.Generic;
using System.IO;
using AdaptiveAR.TechnicalValidation;

namespace AdaptiveAR.Physiology.Tests
{
    public static class TechnicalValidationTests
    {
        private static readonly HashSet<string> RealActionIds = new HashSet<string>
        {
            "step_01_crankshaft.locate", "step_01_crankshaft.place",
            "step_02_piston001.locate", "step_02_piston001.PistonHead", "step_02_piston001.ConnectingRod", "step_02_piston001.ConnectingPin", "step_02_piston001.install",
            "step_02_piston001.PistonEnd", "step_02_piston001.pistonBolt", "step_02_piston001.pistonBoltOther", "step_02_piston001.PistonNut", "step_02_piston001.PistonNutOther",
            "step_03_piston002.locate", "step_03_piston002.PistonHead", "step_03_piston002.ConnectingRod", "step_03_piston002.ConnectingPin", "step_03_piston002.install",
            "step_03_piston002.PistonEnd", "step_03_piston002.pistonBolt", "step_03_piston002.pistonBoltOther", "step_03_piston002.PistonNut", "step_03_piston002.PistonNutOther",
            "step_04_remaining.autocomplete",
            "step_06_camshaft.locate_holders", "step_06_camshaft.holder1", "step_06_camshaft.holder2", "step_06_camshaft.holder3", "step_06_camshaft.holder4", "step_06_camshaft.locate", "step_06_camshaft.place"
        };

        public static void Run(Action<string, bool, string> check)
        {
            void Check(string name, bool ok, string detail = "") { check(name, ok, detail); }

            // 40a config: defaults and parsing
            var off = TechnicalValidationConfig.Parse(null, "none");
            Check("40a absent config is disabled", !off.Enabled && off.SourceDescription == "absent");
            var bad = TechnicalValidationConfig.Parse("{not json", "x");
            Check("40a2 malformed config is disabled", !bad.Enabled && bad.SourceDescription.StartsWith("unreadable"));
            var notEnabled = TechnicalValidationConfig.Parse("{\"scenario\":\"TV2\"}", "x");
            Check("40a3 enabled defaults to false", !notEnabled.Enabled);
            var val = TechnicalValidationConfig.Parse("{\"enabled\":true,\"scenario\":\"TV2\"}", "x");
            Check("40a4 validation mode defaults", val.Enabled && val.Mode == ValidationMode.Validation && val.Physiology == ValidationPhysiology.Nominal
                  && Math.Abs(val.EffectiveDelayScale - 1.0) < 1e-9 && val.EffectiveMinDelaySeconds == 0 && val.EffectivePostTransitionHoldSeconds == 0 && Math.Abs(val.EffectiveFinishDelaySeconds - 4.0) < 1e-9
                  && val.EffectiveParticipantId == "TV_TV2" && val.StartDelaySeconds == 3.0 && !val.SensorStabilisationSeconds.HasValue, val.Describe());
            var pres = TechnicalValidationConfig.Parse("{\"enabled\":true,\"scenario\":\"TV6\",\"mode\":\"presentation\",\"physiology\":\"elevated\",\"participantId\":\"DEMO1\",\"sensorStabilisationSeconds\":10}", "x");
            Check("40a5 presentation mode defaults", pres.Mode == ValidationMode.Presentation && pres.Physiology == ValidationPhysiology.Elevated
                  && Math.Abs(pres.EffectiveDelayScale - 0.5) < 1e-9 && Math.Abs(pres.EffectiveMinDelaySeconds - 2.5) < 1e-9 && Math.Abs(pres.EffectivePostTransitionHoldSeconds - 6.0) < 1e-9
                  && Math.Abs(pres.EffectiveFinishDelaySeconds - 5.0) < 1e-9 && pres.EffectiveParticipantId == "DEMO1" && pres.SensorStabilisationSeconds == 10, pres.Describe());
            var custom = TechnicalValidationConfig.Parse("{\"enabled\":true,\"mode\":\"presentation\",\"delayScale\":0.25,\"minDelaySeconds\":1,\"postTransitionHoldSeconds\":0,\"physiology\":\"unavailable\"}", "x");
            Check("40a6 explicit overrides win", Math.Abs(custom.EffectiveDelayScale - 0.25) < 1e-9 && custom.EffectiveMinDelaySeconds == 1 && custom.EffectivePostTransitionHoldSeconds == 0 && custom.Physiology == ValidationPhysiology.Unavailable);

            // 40b delay scaling
            Check("40b validation delays unscaled", Math.Abs(val.ScaleDelay(12) - 12) < 1e-9 && val.ScaleDelay(0) == 0);
            Check("40b2 presentation delays halved with floor", Math.Abs(pres.ScaleDelay(12) - 6) < 1e-9 && Math.Abs(pres.ScaleDelay(3) - 2.5) < 1e-9 && pres.ScaleDelay(0) == 0);

            // 40c scenario parsing
            string json = "{\"id\":\"T\",\"title\":\"t\",\"events\":[{\"t\":\"continue\",\"after\":3,\"action\":\"a.locate\"},{\"t\":\"error\",\"after\":4},{\"t\":\"wrong_component\",\"after\":2,\"part\":\"part.X\"},{\"t\":\"success\",\"after\":0},{\"t\":\"finish_sequence\",\"after\":4}]}";
            var sc = ValidationScenario.Parse(json, "inline");
            Check("40c scenario parses", sc.Id == "T" && sc.Events.Count == 5 && sc.Events[0].Kind == ValidationEventKind.Continue && sc.Events[0].ExpectedActionId == "a.locate"
                  && sc.Events[1].Kind == ValidationEventKind.Error && sc.Events[2].PartKey == "part.X" && sc.Events[4].Kind == ValidationEventKind.FinishSequence
                  && Math.Abs(sc.AuthoredSeconds - 13) < 1e-9 && sc.CountOf(ValidationEventKind.Error) == 1, sc.Summary());
            bool rejected = false; string why = "";
            try { ValidationScenario.Parse("{\"id\":\"T\",\"events\":[{\"t\":\"set_level\",\"after\":1,\"level\":\"L3\"}]}", "inline"); }
            catch (FormatException e) { rejected = true; why = e.Message; }
            Check("40c2 support-level events are rejected", rejected && why.Contains("support levels cannot be scripted"), why);
            bool rejected2 = false;
            try { ValidationScenario.Parse("{\"id\":\"T\",\"events\":[]}", "inline"); } catch (FormatException) { rejected2 = true; }
            Check("40c3 empty scenario rejected", rejected2);
            bool rejected3 = false;
            try { ValidationScenario.Parse("{\"id\":\"T\",\"events\":[{\"t\":\"wait\",\"after\":-1}]}", "inline"); } catch (FormatException) { rejected3 = true; }
            Check("40c4 negative delay rejected", rejected3);

            // 40d assertions on a clean synthetic record: every check passes
            var good = CleanRecord();
            var res = IntegrationAssertions.Evaluate(good);
            Check("40d clean record passes every assertion", res.Count == 16 && IntegrationAssertions.Failed(res) == 0, Describe(res));

            // 40e one-level violation detected
            var multi = CleanRecord();
            multi.Transitions.Add(new TransitionRecord { SessionMs = 90000, From = "L1_Minimal", To = "L3_Assisted", Reason = "ai_decision", StepIndex = 1 });
            Check("40e multi-level AI transition fails the one-level assertion", Fails(multi, "one-level"));

            // 40f controller level mismatch detected
            var mism = CleanRecord();
            mism.Decisions[2].LevelAfter = "L2_Guided";   // controller says L2 after a KEEP at L3
            Check("40f controller/decision mismatch detected", Fails(mism, "matches the decision log"));

            // 40g step reset missing detected
            var noReset = CleanRecord();
            noReset.Steps[1].LevelOnEntry = "L2_Guided";
            Check("40g step entered above L1 detected", Fails(noReset, "step entry reset"));

            // 40h JSONL close check
            var open = CleanRecord(); open.JsonlClosesWithSessionEnd = false; open.JsonlLastEvent = "note";
            Check("40h JSONL not closed detected", Fails(open, "JSONL"));

            // 40i decisions vs requests, and call protection
            var fewer = CleanRecord(); fewer.Decisions.RemoveAt(fewer.Decisions.Count - 1);
            Check("40i missing decision detected", Fails(fewer, "logged"));
            var close = CleanRecord(); close.Requests[1].SessionMs = close.Requests[0].SessionMs + 1000;
            Check("40i2 requests closer than the min call interval detected", Fails(close, "call protection"));
            var fastChange = CleanRecord();
            fastChange.Transitions.Insert(1, new TransitionRecord { SessionMs = 46000, From = "L2_Guided", To = "L3_Assisted", Reason = "ai_decision", StepIndex = 1 });
            fastChange.Transitions[2].From = "L3_Assisted"; fastChange.Transitions[2].To = "L2_Guided";
            Check("40i3 AI transitions inside the change cooldown detected", Fails(fastChange, "hysteresis"));

            // 40j ledger and injection failures
            var notInjected = CleanRecord(); notInjected.Events[2].Injected = false;
            Check("40j event not injected detected", Fails(notInjected, "ledger"));
            var counterMismatch = CleanRecord(); counterMismatch.ErrorsTotalObserved = 99;
            Check("40j2 session error counter mismatch detected", Fails(counterMismatch, "ledger"));
            var noRequests = CleanRecord(); noRequests.Requests.Clear(); noRequests.Decisions.Clear();
            Check("40j3 no request despite error-like events detected", Fails(noRequests, "structurally appropriate"));
            var boundary = CleanRecord();
            boundary.Decisions.Add(new DecisionRecord { Index = 2, SessionMs = 100000, Status = "Ok", ParsedAction = "INCREASE_SUPPORT", LevelAtRequest = "L3_Assisted", LevelAfter = "L3_Assisted", StepIndex = 1 });
            boundary.Requests.Add(new RequestRecord { Number = 3, SessionMs = 99000, Level = "L3_Assisted", StepIndex = 1 }); boundary.DecisionLinesInJsonl = 3;
            Check("40j4 increase at L3 without SaturatedAtBoundary detected", Fails(boundary, "one-level"));
            var presentation = CleanRecord(); presentation.Presentation[0].TextMatches = false;
            Check("40j5 presentation mismatch detected", Fails(presentation, "displayed support package"));
            var exc = CleanRecord(); exc.ExceptionCount = 1; exc.FirstException = "NullReferenceException";
            Check("40j6 uncaught exception detected", Fails(exc, "exception"));

            // 40k derived figures and summaries
            Check("40k trajectory / counts", good.Trajectory() == "L1 -(45s ai_decision)-> L2 -(60s ai_decision)-> L3 -(123s step_default)-> L1" && good.MaxLevel == "L3_Assisted"
                  && good.Keep == 1 && good.Increase == 2 && good.Decrease == 0 && good.Oscillations == 0 && good.AiTransitions == 2, good.Trajectory());
            string md = Level2SummaryWriter.ToMarkdown(good, res);
            Check("40k2 markdown summary carries the required items", md.Contains("# Level 2 technical validation: TV2") && md.Contains("provider calls 3") && md.Contains("support trajectory: L1") && md.Contains("maximum level reached: L3")
                  && md.Contains("integration assertions: 16 passed / 0 failed") && md.Contains("logger output:") && md.Contains("validation log:") && md.Contains("structural violations: 0"), md);
            var obj = Level2SummaryWriter.ToObject(good, res);
            string ser = AdaptiveAR.Decision.MiniJson.Serialize(obj);
            var back = AdaptiveAR.Decision.MiniJson.ParseObject(ser);
            Check("40k3 JSON summary round trip", back != null && AdaptiveAR.Decision.MiniJson.GetString(back, "scenario_id") == "TV2" && AdaptiveAR.Decision.MiniJson.GetInt(back, "provider_calls", -1) == 3
                  && AdaptiveAR.Decision.MiniJson.GetList(back, "assertions").Count == 16 && AdaptiveAR.Decision.MiniJson.GetList(back, "decisions").Count == 3);
            string agg = Level2SummaryWriter.AggregateMarkdown(new List<Dictionary<string, object>> { back, back }, new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc));
            Check("40k4 aggregate summary", agg.Contains("scenarios 2") && agg.Contains("| TV2 |") && agg.Contains("totals: calls 6, KEEP 2, INCREASE 4, DECREASE 0"), agg);

            // 40l bundled scenarios
            string dir = FindScenarioDir();
            Check("40l bundled scenario folder found", dir != null, dir ?? "not found");
            if (dir != null)
            {
                int parsed = 0; var problems = new List<string>();
                foreach (string id in new[] { "TV1", "TV2", "TV3", "TV4", "TV5", "TV6" })
                {
                    string path = Path.Combine(dir, id + ".json");
                    if (!File.Exists(path)) { problems.Add(id + " missing"); continue; }
                    ValidationScenario s;
                    try { s = ValidationScenario.Parse(File.ReadAllText(path), path); parsed++; }
                    catch (Exception e) { problems.Add(id + ": " + e.Message); continue; }
                    if (s.Id != id) problems.Add(id + ": id mismatch " + s.Id);
                    if (s.Events[0].Kind != ValidationEventKind.Continue || s.Events[0].ExpectedActionId != "step_01_crankshaft.locate") problems.Add(id + ": does not start with the crankshaft acknowledgement");
                    if (s.Events[s.Events.Count - 1].Kind != ValidationEventKind.FinishSequence) problems.Add(id + ": does not end with finish_sequence");
                    foreach (var e in s.Events)
                    {
                        if (e.ExpectedActionId != null && !RealActionIds.Contains(e.ExpectedActionId)) problems.Add(id + ": unknown action " + e.ExpectedActionId);
                        if (e.Kind == ValidationEventKind.Error || e.Kind == ValidationEventKind.FailedAttempt || e.Kind == ValidationEventKind.WrongComponent || e.Kind == ValidationEventKind.Success)
                            if (e.ExpectedActionId == null || e.ExpectedActionId.EndsWith(".locate") || e.ExpectedActionId.EndsWith(".locate_holders") || e.ExpectedActionId.EndsWith(".autocomplete")) problems.Add(id + ": placement outcome scripted on a non-physical action " + e.ExpectedActionId);
                    }
                    if (!File.Exists(path + ".meta")) problems.Add(id + ": no .meta (Resources.Load needs it in a player build)");
                }
                Check("40l2 six bundled scenarios parse and reference only real physical actions", parsed == 6 && problems.Count == 0, string.Join("; ", problems));
                var tv1 = ValidationScenario.Parse(File.ReadAllText(Path.Combine(dir, "TV1.json")), "TV1");
                var tv5 = ValidationScenario.Parse(File.ReadAllText(Path.Combine(dir, "TV5.json")), "TV5");
                var tv6 = ValidationScenario.Parse(File.ReadAllText(Path.Combine(dir, "TV6.json")), "TV6");
                Check("40l3 TV1/TV5 carry no error-like events; TV6 carries both bursts", tv1.CountOf(ValidationEventKind.Error) == 0 && tv1.CountOf(ValidationEventKind.WrongComponent) == 0 && tv1.CountOf(ValidationEventKind.FailedAttempt) == 0
                      && tv5.CountOf(ValidationEventKind.Error) == 0 && tv5.AuthoredSeconds > 400 && tv6.CountOf(ValidationEventKind.Error) == 4 && tv6.CountOf(ValidationEventKind.WrongComponent) == 1 && tv6.CountOf(ValidationEventKind.FailedAttempt) == 2,
                      $"tv5 authored {tv5.AuthoredSeconds}s tv6 err {tv6.CountOf(ValidationEventKind.Error)}");
            }

            CompletionOrdering(check);
            PlacementMotionTests(check);
            PresentationRegressionTests(check);
            HysteresisAssertionTests(check);
        }

        /// <summary>
        /// 40p: the hysteresis assertion fails only on applied AI transitions closer than the
        /// production change cooldown. The TV2 device trajectory (L1->L2 73.0 s, L2->L3 89.2 s,
        /// L3->L2 128.2 s; gaps 16.2 s and 39.0 s, cooldown 15 s) must PASS: its reversal is a
        /// legitimate adaptation, reported descriptively.
        /// </summary>
        private static void HysteresisAssertionTests(Action<string, bool, string> check)
        {
            ScenarioRunRecord Tv2(double thirdMs)
            {
                var r = CleanRecord();
                r.Transitions.Clear(); r.Decisions.Clear(); r.Requests.Clear(); r.DecisionLinesInJsonl = 3;
                r.Transitions.Add(new TransitionRecord { SessionMs = 73000, From = "L1_Minimal", To = "L2_Guided", Reason = "ai_decision", StepIndex = 1 });
                r.Transitions.Add(new TransitionRecord { SessionMs = 89200, From = "L2_Guided", To = "L3_Assisted", Reason = "ai_decision", StepIndex = 1 });
                r.Transitions.Add(new TransitionRecord { SessionMs = thirdMs, From = "L3_Assisted", To = "L2_Guided", Reason = "ai_decision", StepIndex = 1 });
                r.Requests.Add(new RequestRecord { Number = 1, SessionMs = 71500, Level = "L1_Minimal", StepIndex = 1 });
                r.Requests.Add(new RequestRecord { Number = 2, SessionMs = 87800, Level = "L2_Guided", StepIndex = 1 });
                r.Requests.Add(new RequestRecord { Number = 3, SessionMs = thirdMs - 1400, Level = "L3_Assisted", StepIndex = 1 });
                r.Decisions.Add(new DecisionRecord { Index = 0, SessionMs = 73000, Status = "Ok", ParsedAction = "INCREASE_SUPPORT", LevelAtRequest = "L1_Minimal", LevelAfter = "L2_Guided", LatencyMs = 1500, StepIndex = 1 });
                r.Decisions.Add(new DecisionRecord { Index = 1, SessionMs = 89200, Status = "Ok", ParsedAction = "INCREASE_SUPPORT", LevelAtRequest = "L2_Guided", LevelAfter = "L3_Assisted", LatencyMs = 1400, StepIndex = 1 });
                r.Decisions.Add(new DecisionRecord { Index = 2, SessionMs = thirdMs, Status = "Ok", ParsedAction = "DECREASE_SUPPORT", LevelAtRequest = "L3_Assisted", LevelAfter = "L2_Guided", LatencyMs = 1400, StepIndex = 1 });
                r.Steps.Clear();
                r.Steps.Add(new StepRecord { Index = 0, StepId = "step_01_crankshaft", SessionMs = 0, LevelOnEntry = "L1_Minimal", LevelBeforeEntry = "L1_Minimal" });
                r.Steps.Add(new StepRecord { Index = 1, StepId = "step_02_piston001", SessionMs = 31000, LevelOnEntry = "L1_Minimal", LevelBeforeEntry = "L1_Minimal" });
                return r;
            }
            var tv2 = Tv2(128200);
            var res = IntegrationAssertions.Evaluate(tv2);
            string det = DetailOf(res, "hysteresis");
            check("40p TV2 device trajectory (73.0 / 89.2 / 128.2 s, cooldown 15 s) passes the hysteresis assertion", !Fails(tv2, "hysteresis") && tv2.AiTransitions == 3 && Math.Abs(tv2.ChangeCooldownMs - 15000) < 1e-9, det);
            check("40p2 its reversal is reported descriptively, not as a rapid reversal", tv2.Reversals == 1 && tv2.RapidReversals == 0 && tv2.Oscillations == 0 && det.Contains("reversals 1") && det.Contains("16.2s") && det.Contains("39.0s"), det);
            check("40p3 the TV2 record passes every other assertion too", IntegrationAssertions.Failed(res) == 0, Describe(res));

            var rapid = Tv2(100000);          // L3->L2 10.8 s after L2->L3: inside the 15 s cooldown
            string detR = DetailOf(IntegrationAssertions.Evaluate(rapid), "hysteresis");
            check("40p4 a reversal inside the cooldown still fails the assertion", Fails(rapid, "hysteresis") && rapid.RapidReversals == 1 && rapid.Reversals == 1 && detR.Contains("too fast") && detR.Contains("10.8s"), detR);

            var fastSame = CleanRecord(); fastSame.Transitions.Clear();
            fastSame.Transitions.Add(new TransitionRecord { SessionMs = 45000, From = "L1_Minimal", To = "L2_Guided", Reason = "ai_decision", StepIndex = 1 });
            fastSame.Transitions.Add(new TransitionRecord { SessionMs = 50000, From = "L2_Guided", To = "L3_Assisted", Reason = "ai_decision", StepIndex = 1 });
            check("40p5 two same-direction changes inside the cooldown fail; no reversal is counted", Fails(fastSame, "hysteresis") && fastSame.Reversals == 0 && fastSame.RapidReversals == 0, DetailOf(IntegrationAssertions.Evaluate(fastSame), "hysteresis"));

            var stepReset = Tv2(128200); stepReset.Transitions[2].Reason = "step_default"; stepReset.Transitions[2].To = "L1_Minimal"; stepReset.Transitions[2].StepIndex = 2;
            stepReset.Decisions.RemoveAt(2); stepReset.Requests.RemoveAt(2); stepReset.DecisionLinesInJsonl = 2;
            stepReset.Steps.Add(new StepRecord { Index = 2, StepId = "step_03_piston002", SessionMs = 128200, LevelOnEntry = "L1_Minimal", LevelBeforeEntry = "L3_Assisted" });
            check("40p6 a step-default reset is neither a reversal nor a hysteresis event", !Fails(stepReset, "hysteresis") && stepReset.Reversals == 0 && stepReset.AiTransitions == 2, "");
        }

        /// <summary>
        /// 40o: the two TV2 presentation-mode device regressions (2026-10-29). Both had one cause:
        /// the animator moved the REAL part, the production validator accepted it as a genuine
        /// placement when it came within tolerance, and the scripted injection then landed on the
        /// next armed action (connecting rod "skipped"); for the crankshaft the injection found
        /// nothing armed and the animator's scale writes hit a part the join had re-parented under
        /// the turntable (0.6 instead of 0.00246 world scale: "disappeared"). Guarded now by the
        /// alignment check (nothing injected at another action), by assertions 14-16, and by the
        /// animator flying a renderer-only proxy instead of the part.
        /// </summary>
        private static void PresentationRegressionTests(Action<string, bool, string> check)
        {
            // alignment guard
            string why;
            bool a1 = InjectionAlignment.ShouldInject("step_02_piston001.PistonHead", "step_02_piston001.PistonHead", out why);
            bool a2 = InjectionAlignment.ShouldInject("step_02_piston001.PistonHead", "step_02_piston001.ConnectingRod", out string why2);
            bool a3 = InjectionAlignment.ShouldInject(null, "step_02_piston001.ConnectingRod", out why);
            bool a4 = InjectionAlignment.ShouldInject("step_01_crankshaft.place", null, out string why4);
            check("40o a scripted event is injected only at its authored action", a1 && !a2 && a3 && !a4 && why2.Contains("ConnectingRod") && why2.Contains("PistonHead") && why4.Contains("<none>"), why2);

            // regression 1: the connecting rod. Production went PistonHead -> ConnectingPin after the early
            // acceptance; the rod's scripted success is misaligned. Both new assertions must name it.
            var r = CleanRecord();
            r.Actions.Clear(); r.Events.Clear();
            string head = "step_02_piston001.PistonHead", rod = "step_02_piston001.ConnectingRod", pin = "step_02_piston001.ConnectingPin";
            r.Actions.Add(new ActionRecord { SessionMs = 40000, ActionId = head, StepIndex = 1, Level = "L1_Minimal" });
            r.Actions.Add(new ActionRecord { SessionMs = 52000, ActionId = pin, StepIndex = 1, Level = "L1_Minimal" });     // the rod was never entered
            r.Events.Add(new InjectedEventRecord { Index = 0, Kind = "success", SessionMs = 52000, StepIndex = 1, Injected = true, Aligned = false, Level = "L1_Minimal", ExpectedAction = head, ActionBefore = rod, ActionAfter = pin, ObjectName = "ConnectingRod", ObjectActiveAfter = true, ObjectVisibleAfter = true });
            r.Events.Add(new InjectedEventRecord { Index = 1, Kind = "success", SessionMs = 60000, StepIndex = 1, Injected = false, Aligned = false, Level = "L1_Minimal", ExpectedAction = rod, ActionBefore = pin, ActionAfter = pin, Detail = "NOT injected: scripted for " + rod });
            var res = IntegrationAssertions.Evaluate(r);
            string det14 = DetailOf(res, "acted on its authored action"), det15 = DetailOf(res, "none skipped");
            check("40o2 regression: a success landing on the connecting rod instead of the head is detected", Fails(r, "acted on its authored action") && det14.Contains("#0") && det14.Contains("INJECTED") && det14.Contains(rod), det14);
            check("40o3 regression: the connecting rod never being entered is detected as a skipped authored action", Fails(r, "none skipped") && det15.Contains(rod) && !det15.Contains(pin), det15);
            var good = CleanRecord();
            good.Events.Clear();
            good.Events.Add(new InjectedEventRecord { Index = 0, Kind = "success", SessionMs = 50000, StepIndex = 1, Injected = true, Aligned = true, Level = "L1_Minimal", ExpectedAction = "step_02_piston001.PistonHead", ActionBefore = "step_02_piston001.PistonHead", ActionAfter = "step_02_piston001.ConnectingRod", ObjectName = "PistonHead", ObjectActiveAfter = true, ObjectVisibleAfter = true });
            good.Actions.Add(new ActionRecord { SessionMs = 50000, ActionId = "step_02_piston001.ConnectingRod", StepIndex = 1, Level = "L1_Minimal" });
            check("40o4 an aligned, in-order run passes the alignment and order assertions", !Fails(good, "acted on its authored action") && !Fails(good, "none skipped"), Describe(IntegrationAssertions.Evaluate(good)));
            var sub = CleanRecord(); sub.Events.Clear(); sub.Actions.Clear();
            sub.Events.Add(new InjectedEventRecord { Index = 0, Kind = "success", SessionMs = 1, Injected = true, Aligned = true, ExpectedAction = "a", ActionBefore = "a", ObjectVisibleAfter = true, ObjectActiveAfter = true });
            sub.Events.Add(new InjectedEventRecord { Index = 1, Kind = "success", SessionMs = 2, Injected = true, Aligned = true, ExpectedAction = "c", ActionBefore = "c", ObjectVisibleAfter = true, ObjectActiveAfter = true });
            foreach (var id in new[] { "a", "b", "c", "d" }) sub.Actions.Add(new ActionRecord { ActionId = id });
            var sub2 = CleanRecord(); sub2.Events.Clear(); sub2.Actions.Clear();
            sub2.Events.Add(new InjectedEventRecord { Index = 0, Kind = "success", SessionMs = 1, Injected = true, Aligned = true, ExpectedAction = "c", ActionBefore = "c", ObjectVisibleAfter = true, ObjectActiveAfter = true });
            sub2.Events.Add(new InjectedEventRecord { Index = 1, Kind = "success", SessionMs = 2, Injected = true, Aligned = true, ExpectedAction = "a", ActionBefore = "a", ObjectVisibleAfter = true, ObjectActiveAfter = true });
            foreach (var id in new[] { "a", "b", "c" }) sub2.Actions.Add(new ActionRecord { ActionId = id });
            check("40o5 order check: scripted actions must be an in-order subsequence of the entered actions", !Fails(sub, "none skipped") && Fails(sub2, "none skipped"), DetailOf(IntegrationAssertions.Evaluate(sub2), "none skipped"));

            // regression 2: the crankshaft. The injected success left the part re-scaled / invisible.
            var crank = CleanRecord();
            crank.Events[1].ObjectName = "crankshaft"; crank.Events[1].ObjectVisibleAfter = false; crank.Events[1].ObjectActiveAfter = true; crank.Events[1].ObjectWorldScaleAfter = "0.6,0.6,0.6"; crank.Events[1].ObjectParentAfter = "EngineTurntable";
            string det16 = DetailOf(IntegrationAssertions.Evaluate(crank), "stayed active and visible");
            check("40o6 regression: a placed part that is not visible after its injected success is detected", Fails(crank, "stayed active and visible") && det16.Contains("crankshaft") && det16.Contains("0.6,0.6,0.6"), det16);
            var unobserved = CleanRecord(); unobserved.Events[1].ObjectVisibleAfter = null;
            check("40o7 an injected success without an object observation does not pass silently", Fails(unobserved, "stayed active and visible") && DetailOf(IntegrationAssertions.Evaluate(unobserved), "stayed active and visible").Contains("without an object observation"), "");
            check("40o8 the clean reference run passes all assertions (now 16)", IntegrationAssertions.Failed(IntegrationAssertions.Evaluate(CleanRecord())) == 0 && IntegrationAssertions.Evaluate(CleanRecord()).Count == 16, Describe(IntegrationAssertions.Evaluate(CleanRecord())));

            // the animator never touches the real part: it builds and flies a proxy
            string scenarioDir = FindScenarioDir();
            string animPath = scenarioDir != null ? Path.GetFullPath(Path.Combine(scenarioDir, "..", "..", "Scripts", "TechnicalValidation", "PresentationPlacementAnimator.cs")) : null;
            string src = animPath != null && File.Exists(animPath) ? File.ReadAllText(animPath) : "";
            bool proxyOnly = src.Contains("BuildProxy") && src.Contains("HideRenderers") && src.Contains("ShowRenderers")
                             && !src.Contains("part.SetPositionAndRotation") && !src.Contains("part.position =") && !src.Contains("part.rotation =") && !src.Contains("part.localScale") && !src.Contains("isKinematic") && !src.Contains("SetParent(part");
            check("40o9 the presentation animator flies a renderer-only proxy and never writes the real part's pose, scale or body", proxyOnly, animPath ?? "animator source not found");

            // summary carries the diagnostics
            var obj = Level2SummaryWriter.ToObject(r, res);
            var events = obj["events"] as List<object>;
            var first = events != null && events.Count > 0 ? events[0] as Dictionary<string, object> : null;
            check("40o10 the summary object carries per-event alignment and object diagnostics and the action entries", first != null && (string)first["scripted_action"] == head && (string)first["action_before"] == rod && (string)first["next_action"] == pin && (string)first["object"] == "ConnectingRod" && (obj["actions"] as List<object>).Count == 2, "");
        }

        private static string DetailOf(List<AssertionResult> res, string nameFragment)
        {
            foreach (var a in res) if (a.Name.Contains(nameFragment)) return a.Detail;
            return "<assertion not found: " + nameFragment + ">";
        }

        /// <summary>
        /// 40n: presentation-only placement motion. Gating (presentation yes, validation no,
        /// participant never), easing, lifecycle, final pose = target, once-only hand-over to
        /// the production path, and no reference to support/decision/scheduler state.
        /// </summary>
        private static void PlacementMotionTests(Action<string, bool, string> check)
        {
            var participant = TechnicalValidationConfig.Parse(null, "none");
            var notEnabled = TechnicalValidationConfig.Parse("{\"enabled\":false,\"mode\":\"presentation\",\"placementMotionSeconds\":1.0}", "x");
            var validation = TechnicalValidationConfig.Parse("{\"enabled\":true,\"scenario\":\"TV1\",\"mode\":\"validation\",\"placementMotionSeconds\":1.0,\"placementHighlightSeconds\":0.5}", "x");
            var presentation = TechnicalValidationConfig.Parse("{\"enabled\":true,\"scenario\":\"TV2\",\"mode\":\"presentation\"}", "x");
            var presentationCfg = TechnicalValidationConfig.Parse("{\"enabled\":true,\"scenario\":\"TV2\",\"mode\":\"presentation\",\"placementMotionSeconds\":1.2,\"placementHighlightSeconds\":0.2}", "x");
            var presentationOff = TechnicalValidationConfig.Parse("{\"enabled\":true,\"scenario\":\"TV2\",\"mode\":\"presentation\",\"placementMotionSeconds\":0}", "x");
            check("40n presentation mode selects animated placement (0.9 s motion, 0.35 s highlight by default)",
                  PlacementMotion.Enabled(presentation) && Math.Abs(presentation.EffectivePlacementMotionSeconds - 0.9) < 1e-9 && Math.Abs(presentation.EffectivePlacementHighlightSeconds - 0.35) < 1e-9
                  && PlacementMotion.Enabled(presentationCfg) && Math.Abs(presentationCfg.EffectivePlacementMotionSeconds - 1.2) < 1e-9 && Math.Abs(presentationCfg.EffectivePlacementHighlightSeconds - 0.2) < 1e-9,
                  presentation.Describe());
            check("40n2 validation mode keeps immediate placement even when the file configures a motion",
                  !PlacementMotion.Enabled(validation) && validation.EffectivePlacementMotionSeconds == 0 && validation.EffectivePlacementHighlightSeconds == 0, validation.Describe());
            check("40n3 participant sessions never enable the animation path",
                  !PlacementMotion.Enabled(null) && !PlacementMotion.Enabled(participant) && !PlacementMotion.Enabled(notEnabled) && participant.EffectivePlacementMotionSeconds == 0 && notEnabled.EffectivePlacementMotionSeconds == 0, "");
            check("40n4 presentation motion can be switched off explicitly", !PlacementMotion.Enabled(presentationOff) && presentationOff.EffectivePlacementHighlightSeconds == 0, "");

            bool monotonic = true; double prev = -1;
            for (int i = 0; i <= 100; i++) { double e = PlacementMotion.EaseInOut(i / 100.0); if (e < prev) monotonic = false; prev = e; }
            check("40n5 ease-in-out: 0 at start, 0.5 at the midpoint, 1 at the end, monotonic, clamped",
                  monotonic && PlacementMotion.EaseInOut(0) == 0 && Math.Abs(PlacementMotion.EaseInOut(0.5) - 0.5) < 1e-12 && PlacementMotion.EaseInOut(1) == 1
                  && PlacementMotion.EaseInOut(-1) == 0 && PlacementMotion.EaseInOut(2) == 1 && PlacementMotion.EaseInOut(0.25) < 0.25 && PlacementMotion.EaseInOut(0.75) > 0.75, "");

            var run = new PlacementMotionRun(0.8, 0.2);
            run.Begin(10.0);
            double fHighlight = run.Advance(10.1);
            bool pulseUp = run.HighlightPulse > 0.99 && run.Current == PlacementMotionRun.Phase.Highlight;
            double fMid = run.Advance(10.6);
            bool midMotion = run.Current == PlacementMotionRun.Phase.Motion && run.HighlightPulse == 0;
            double fEnd = run.Advance(11.0);
            double[] from = { 0.10, 0.20, 0.30 }, to = { 0.40, 0.00, 0.90 };
            double[] mid = PlacementMotion.Lerp(from, to, fMid), end = PlacementMotion.Lerp(from, to, fEnd);
            check("40n6 lifecycle: highlight (no motion), eased motion, done at duration",
                  fHighlight == 0 && pulseUp && midMotion && fMid > 0 && fMid < 1 && Math.Abs(fMid - 0.5) < 1e-9 && fEnd == 1 && run.MotionFinished && run.Advance(12.0) == 1,
                  $"f {fHighlight}/{fMid}/{fEnd} phase {run.Current}");
            check("40n7 final pose equals the target pose exactly; the mid pose lies strictly between",
                  end[0] == to[0] && end[1] == to[1] && end[2] == to[2] && mid[0] > from[0] && mid[0] < to[0] && mid[1] < from[1] && mid[1] > to[1],
                  $"end {end[0]},{end[1]},{end[2]}");

            var run2 = new PlacementMotionRun(0.6, 0);
            int calls = 0;
            bool beforeBegin = run2.Complete(() => calls++, 0);
            run2.Begin(0);
            run2.Advance(0.3);
            bool during = run2.Complete(() => calls++, 0.3);
            run2.Advance(0.6);
            bool now = run2.Complete(() => calls++, 0.6);
            bool again = run2.Complete(() => calls++, 0.7);
            check("40n8 the production completion is invoked exactly once, only after the motion finished",
                  !beforeBegin && !during && now && !again && calls == 1 && run2.CompletionInvocations == 1 && run2.CompletedAt == 0.6, $"calls {calls}");

            var clampLow = new PlacementMotionRun(0.05, -1); var clampHigh = new PlacementMotionRun(9, 5);
            check("40n9 durations are clamped to a sane band", clampLow.DurationSeconds == PlacementMotion.MinDurationSeconds && clampLow.HighlightSeconds == 0
                  && clampHigh.DurationSeconds == PlacementMotion.MaxDurationSeconds && clampHigh.HighlightSeconds == PlacementMotion.MaxHighlightSeconds, "");

            // Isolation: the animation layer references no support / decision / scheduler / physiology type.
            string[] forbidden = { "SupportLevelController", "SupportLevel", "DecisionScheduler", "DecisionResponse", "AdaptationPolicy", "IDecisionProvider", "ContextLedger", "RequestGate", "SetLevel", "ApplyDecision", "PhysiologicalStateProvider", "AdaptiveAR.Support", "AdaptiveAR.Decision", "AssemblySessionController" };
            var offenders = new List<string>();
            foreach (var t in new[] { typeof(PlacementMotion), typeof(PlacementMotionRun) })
            {
                foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    var names = new List<string>();
                    if (m is System.Reflection.FieldInfo fi) names.Add(fi.FieldType.FullName);
                    if (m is System.Reflection.PropertyInfo pi) names.Add(pi.PropertyType.FullName);
                    if (m is System.Reflection.MethodBase mb) foreach (var prm in mb.GetParameters()) names.Add(prm.ParameterType.FullName);
                    if (m is System.Reflection.MethodInfo mi) names.Add(mi.ReturnType.FullName);
                    foreach (var n in names) foreach (var f in forbidden) if (n != null && n.Contains(f)) offenders.Add(t.Name + "." + m.Name + ": " + n);
                }
            }
            string scenarioDir = FindScenarioDir();
            string scriptsDir = scenarioDir != null ? Path.GetFullPath(Path.Combine(scenarioDir, "..", "..", "Scripts", "TechnicalValidation")) : null;
            var scanned = new List<string>();
            foreach (var file in new[] { "PlacementMotion.cs", "PresentationPlacementAnimator.cs" })
            {
                string path = scriptsDir != null ? Path.Combine(scriptsDir, file) : null;
                if (path == null || !File.Exists(path)) { offenders.Add(file + ": not found"); continue; }
                scanned.Add(file);
                string src = File.ReadAllText(path);
                foreach (var f in forbidden) if (src.Contains(f)) offenders.Add(file + " mentions " + f);
            }
            check("40n10 the animation layer references no support/decision/scheduler/physiology state", offenders.Count == 0 && scanned.Count == 2, string.Join("; ", offenders));
        }

        /// <summary>
        /// 40m: completion ordering. The event whose injection completes the sequence is committed
        /// BEFORE the completion record, the completion record is written once and carries the
        /// final count, and the summary reports the same count (device finding: TV1 wrote
        /// technical_validation_completed with events_injected 29 and the 30th injected_event after it).
        /// </summary>
        private static void CompletionOrdering(Action<string, bool, string> check)
        {
            var r = new ScenarioRunRecord { ScenarioId = "TV1", ScenarioTitle = "Clean", Mode = "validation", Physiology = "nominal", BuildLabel = "test", LevelAtStart = "L1_Minimal", SequenceFinished = true, SessionEnded = true };
            var log = new List<string>();
            int countAtCompletion = -1, totalAtCompletion = -1;
            var seq = new CompletionSequencer(r,
                rec => log.Add("injected_event:" + rec.Index + ":" + (rec.Injected ? "ok" : "no")),
                how => { countAtCompletion = r.InjectedCount; totalAtCompletion = r.Events.Count; log.Add("technical_validation_completed:" + how + ":" + r.InjectedCount); });

            for (int i = 0; i < 29; i++)
            {
                seq.Open(new InjectedEventRecord { Index = i, Kind = i % 2 == 0 ? "continue" : "success", SessionMs = i * 1000 });
                bool closed = seq.Close(true, "validator Succeed()", null, "a");
                if (!closed) log.Add("UNEXPECTED close=false at " + i);
            }
            check("40m ordinary events are committed on Close, in order", r.Events.Count == 29 && r.InjectedCount == 29 && log.Count == 29 && log[0] == "injected_event:0:ok" && log[28] == "injected_event:28:ok" && !seq.InjectionOpen && !seq.CompletionWritten,
                  $"events {r.Events.Count} injected {r.InjectedCount} log {log.Count}");

            // The 30th injection completes the sequence inside the production call: Complete() arrives before Close().
            var last = new InjectedEventRecord { Index = 29, Kind = "success", SessionMs = 29000 };
            seq.Open(last);
            bool openDuring = seq.InjectionOpen;
            bool wroteNow = seq.Complete("sequence_complete", "<none>");
            bool lateClose = seq.Close(true, "validator Succeed()", null, "<none>");
            check("40m2 the final injected event is recorded before the completion record", openDuring && log.Count == 31 && log[29] == "injected_event:29:ok" && log[30].StartsWith("technical_validation_completed:sequence_complete"),
                  string.Join(" | ", log.GetRange(Math.Max(0, log.Count - 3), Math.Min(3, log.Count))));
            check("40m3 the completion record carries the final count", wroteNow && countAtCompletion == 30 && totalAtCompletion == 30 && log[30] == "technical_validation_completed:sequence_complete:30" && r.Events.Count == 30 && r.InjectedCount == 30,
                  $"at completion {countAtCompletion}/{totalAtCompletion}, final {r.InjectedCount}/{r.Events.Count}");
            check("40m4 the late Close neither duplicates nor overwrites the committed event", !lateClose && r.Events.Count == 30 && ReferenceEquals(r.Events[29], last) && last.Injected && last.ActionAfter == "<none>" && last.Detail.Contains("sequence completed during this injection"),
                  $"closed {lateClose} events {r.Events.Count} detail '{last.Detail}'");
            bool again1 = seq.Complete("sequence_complete", null), again2 = seq.Complete("script_complete", null);
            check("40m5 the completion record is written exactly once", !again1 && !again2 && seq.CompletionWritten && seq.CompletionHow == "sequence_complete" && log.Count == 31, $"log {log.Count}");

            // The script finishing before the sequence: no open injection, the completion record alone, count unchanged.
            var r2 = new ScenarioRunRecord { ScenarioId = "TV5", LevelAtStart = "L1_Minimal" };
            var log2 = new List<string>();
            var seq2 = new CompletionSequencer(r2, rec => log2.Add("e" + rec.Index + (rec.Injected ? "+" : "-")), how => log2.Add("c:" + how + ":" + r2.InjectedCount + "/" + r2.Events.Count));
            seq2.Open(new InjectedEventRecord { Index = 0, Kind = "continue" }); seq2.Close(true, "continue accepted", null, "x");
            seq2.Open(new InjectedEventRecord { Index = 1, Kind = "success" }); seq2.Close(false, "validator not armed", null, "x");
            bool wrote2 = seq2.Complete("script_complete", "x");
            check("40m6 script_complete with no open injection writes only the completion record", wrote2 && log2.Count == 3 && log2[0] == "e0+" && log2[1] == "e1-" && log2[2] == "c:script_complete:1/2" && !r2.Events[1].Injected,
                  string.Join(" | ", log2));

            // Summary generation reports the same (final) count.
            r.StartUtc = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc); r.EndUtc = r.StartUtc.AddSeconds(252);
            string md = Level2SummaryWriter.ToMarkdown(r, IntegrationAssertions.Evaluate(r));
            var obj = Level2SummaryWriter.ToObject(r, IntegrationAssertions.Evaluate(r));
            check("40m7 the summary reports the final injected count", md.Contains("injected 30 / 30") && Convert.ToInt32(obj["events_injected"]) == 30 && Convert.ToInt32(obj["events_total"]) == 30,
                  md.Length > 300 ? md.Substring(0, 300) : md);
        }

        private static bool Fails(ScenarioRunRecord r, string nameFragment)
        {
            foreach (var a in IntegrationAssertions.Evaluate(r)) if (a.Name.Contains(nameFragment)) return !a.Passed;
            return false;
        }

        private static string Describe(List<AssertionResult> res)
        {
            var parts = new List<string>();
            foreach (var a in res) if (!a.Passed) parts.Add(a.Name + ": " + a.Detail);
            return string.Join(" | ", parts);
        }

        /// <summary>A consistent TV2-like run: 8 scripted events, 3 requests/decisions (INCREASE 45 s, INCREASE 60 s, KEEP 85 s), L1->L2->L3->L1 by step reset.</summary>
        private static ScenarioRunRecord CleanRecord()
        {
            var r = new ScenarioRunRecord
            {
                ScenarioId = "TV2", ScenarioTitle = "Initial difficulty then recovery", Mode = "validation", Physiology = "nominal", DelayScale = 1.0, BuildLabel = "test",
                ProviderId = "anthropic:claude-sonnet-5-5", ProviderStatus = "llm", StartUtc = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc), EndUtc = new DateTime(2026, 10, 30, 10, 3, 0, DateTimeKind.Utc),
                SessionId = "TV_TV2_20261030_100000", LoggerPath = "/x/TV_TV2_20261030_100000.jsonl", ValidationLogPath = "/x/TV2.tv.jsonl", LevelAtStart = "L1_Minimal",
                SequenceFinished = true, SessionEnded = true, JsonlReadable = true, JsonlClosesWithSessionEnd = true, JsonlLastEvent = "session_end",
                ErrorsTotalObserved = 3, AttemptsTotalObserved = 5, DecisionLinesInJsonl = 2, SupportChangeLinesInJsonl = 3, TechnicalValidationNotesInJsonl = 9
            };
            r.Steps.Add(new StepRecord { Index = 0, StepId = "step_01_crankshaft", SessionMs = 0, LevelOnEntry = "L1_Minimal", LevelBeforeEntry = "L1_Minimal" });
            r.Steps.Add(new StepRecord { Index = 1, StepId = "step_02_piston001", SessionMs = 31000, LevelOnEntry = "L1_Minimal", LevelBeforeEntry = "L1_Minimal" });
            r.Steps.Add(new StepRecord { Index = 2, StepId = "step_03_piston002", SessionMs = 123000, LevelOnEntry = "L1_Minimal", LevelBeforeEntry = "L3_Assisted" });
            string[] kinds = { "continue", "success", "continue", "failed_attempt", "error", "wrong_component", "error", "success" };
            int[] steps = { 0, 0, 1, 1, 1, 1, 1, 1 };
            for (int i = 0; i < kinds.Length; i++)
                r.Events.Add(new InjectedEventRecord { Index = i, Kind = kinds[i], SessionMs = 3000 + i * 9000, StepIndex = steps[i], Injected = true, Aligned = true, Level = "L1_Minimal",
                                                       ObjectName = kinds[i] == "success" ? "part" + i : null, ObjectActiveAfter = kinds[i] == "success" ? (bool?)true : null, ObjectVisibleAfter = kinds[i] == "success" ? (bool?)true : null });
            r.Actions.Add(new ActionRecord { SessionMs = 0, ActionId = "step_01_crankshaft.locate", StepIndex = 0, Level = "L1_Minimal" });
            r.Actions.Add(new ActionRecord { SessionMs = 3000, ActionId = "step_01_crankshaft.place", StepIndex = 0, Level = "L1_Minimal" });
            r.Actions.Add(new ActionRecord { SessionMs = 31000, ActionId = "step_02_piston001.locate", StepIndex = 1, Level = "L1_Minimal" });
            r.Actions.Add(new ActionRecord { SessionMs = 40000, ActionId = "step_02_piston001.PistonHead", StepIndex = 1, Level = "L1_Minimal" });
            r.Requests.Add(new RequestRecord { Number = 1, SessionMs = 44000, Level = "L1_Minimal", StepIndex = 1 });
            r.Requests.Add(new RequestRecord { Number = 2, SessionMs = 84000, Level = "L2_Guided", StepIndex = 1 });
            r.Transitions.Add(new TransitionRecord { SessionMs = 45000, From = "L1_Minimal", To = "L2_Guided", Reason = "ai_decision", StepIndex = 1 });
            r.Transitions.Add(new TransitionRecord { SessionMs = 60000, From = "L2_Guided", To = "L3_Assisted", Reason = "ai_decision", StepIndex = 1 });
            r.Transitions.Add(new TransitionRecord { SessionMs = 123000, From = "L3_Assisted", To = "L1_Minimal", Reason = "step_default", StepIndex = 2 });
            r.Decisions.Add(new DecisionRecord { Index = 0, SessionMs = 45000, Status = "Ok", ParsedAction = "INCREASE_SUPPORT", LevelAtRequest = "L1_Minimal", LevelAfter = "L2_Guided", LatencyMs = 1200, StepIndex = 1 });
            r.Decisions.Add(new DecisionRecord { Index = 1, SessionMs = 85000, Status = "Ok", ParsedAction = "KEEP_SUPPORT", LevelAtRequest = "L2_Guided", LevelAfter = "L2_Guided", LatencyMs = 900, StepIndex = 1 });
            // the L2->L3 transition above came from a request/decision pair we fold into the second record for brevity of the trajectory test:
            r.Decisions[1].LevelAtRequest = "L2_Guided"; r.Decisions[1].ParsedAction = "INCREASE_SUPPORT"; r.Decisions[1].LevelAfter = "L3_Assisted"; r.Decisions[1].SessionMs = 60000; r.Requests[1].SessionMs = 59000;
            r.Decisions.Add(new DecisionRecord { Index = 2, SessionMs = 85000, Status = "Ok", ParsedAction = "KEEP_SUPPORT", LevelAtRequest = "L3_Assisted", LevelAfter = "L3_Assisted", LatencyMs = 900, StepIndex = 1 });
            r.Requests.Add(new RequestRecord { Number = 3, SessionMs = 84000, Level = "L3_Assisted", StepIndex = 1 });
            r.DecisionLinesInJsonl = 3;
            r.Presentation.Add(new PresentationCheck { SessionMs = 46000, ActionId = "step_02_piston001.PistonHead", Level = "L2_Guided", TextMatches = true, OtherLevelTextShown = false, GhostState = "ghost(visible)" });
            r.Presentation.Add(new PresentationCheck { SessionMs = 66000, ActionId = "step_02_piston001.PistonHead", Level = "L3_Assisted", TextMatches = true, OtherLevelTextShown = false, GhostState = "ghost(visible)" });
            return r;
        }

        private static string FindScenarioDir()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, "Assets", "Resources", "TechnicalValidation");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }
}
