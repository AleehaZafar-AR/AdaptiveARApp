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
            Check("40d clean record passes every assertion", res.Count == 13 && IntegrationAssertions.Failed(res) == 0, Describe(res));

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
                  && md.Contains("integration assertions: 13 passed / 0 failed") && md.Contains("logger output:") && md.Contains("validation log:") && md.Contains("structural violations: 0"), md);
            var obj = Level2SummaryWriter.ToObject(good, res);
            string ser = AdaptiveAR.Decision.MiniJson.Serialize(obj);
            var back = AdaptiveAR.Decision.MiniJson.ParseObject(ser);
            Check("40k3 JSON summary round trip", back != null && AdaptiveAR.Decision.MiniJson.GetString(back, "scenario_id") == "TV2" && AdaptiveAR.Decision.MiniJson.GetInt(back, "provider_calls", -1) == 3
                  && AdaptiveAR.Decision.MiniJson.GetList(back, "assertions").Count == 13 && AdaptiveAR.Decision.MiniJson.GetList(back, "decisions").Count == 3);
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
                r.Events.Add(new InjectedEventRecord { Index = i, Kind = kinds[i], SessionMs = 3000 + i * 9000, StepIndex = steps[i], Injected = true, Aligned = true, Level = "L1_Minimal" });
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
