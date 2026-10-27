// Plain-C# tests for HrvMath / PhysiologyWindow / PpiSample. Exit code 0 = all passed.
using System;
using System.Collections.Generic;
using AdaptiveAR.Physiology;

namespace AdaptiveAR.Physiology.Tests
{
    public static class DecisionTests
    {
        public static void Run(Action<string, bool, string> check)
        {
            void Check(string name, bool ok, string detail = "") { check(name, ok, detail); }
            var cfg = new AdaptiveAR.Decision.ReferencePolicyConfig();
            var refp = new AdaptiveAR.Decision.ReferencePolicyProvider(cfg);
            AdaptiveAR.Decision.DecisionContext C(int level, float elapsed, float? expected, int errors, int attempts) => new AdaptiveAR.Decision.DecisionContext
            { CurrentSupportLevel = (AdaptiveAR.Support.SupportLevel)level, StepElapsedMs = elapsed, StepExpectedMs = expected, ErrorsOnStep = errors, AttemptsOnStep = attempts };

            // 10 MiniJson round trip
            var parsed = AdaptiveAR.Decision.MiniJson.ParseObject("{\"a\":1.5,\"b\":\"x\\\"y\",\"c\":[true,null,{\"d\":-2}],\"e\":false}");
            Check("10 MiniJson parse", parsed != null && AdaptiveAR.Decision.MiniJson.GetDouble(parsed, "a", 0) == 1.5 && AdaptiveAR.Decision.MiniJson.GetString(parsed, "b") == "x\"y"
                  && AdaptiveAR.Decision.MiniJson.GetList(parsed, "c").Count == 3 && !AdaptiveAR.Decision.MiniJson.GetBool(parsed, "e", true), "");
            string ser = AdaptiveAR.Decision.MiniJson.Serialize(parsed);
            var again = AdaptiveAR.Decision.MiniJson.ParseObject(ser);
            Check("10b MiniJson serialize round trip", again != null && AdaptiveAR.Decision.MiniJson.GetString(again, "b") == "x\"y" && ser.Contains("\"e\":false"), ser);

            // 11 reference policy: deterministic + each rule
            var r1 = refp.RequestDecision(C(2, 45000, 60000, 0, 1)); var r2 = refp.RequestDecision(C(2, 45000, 60000, 0, 1));
            Check("11 reference deterministic", r1.ParsedAction == r2.ParsedAction && r1.Status == AdaptiveAR.Decision.ConstraintStatus.Ok && r1.ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT, r1.ProviderReason);
            Check("11b errors -> INCREASE", refp.RequestDecision(C(2, 5000, 60000, 2, 1)).ParsedAction == AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT);
            Check("11c attempts -> INCREASE", refp.RequestDecision(C(1, 5000, 60000, 0, 4)).ParsedAction == AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT);
            Check("11d slow -> INCREASE", refp.RequestDecision(C(2, 95000, 60000, 0, 1)).ParsedAction == AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT);
            Check("11e fast+clean -> DECREASE", refp.RequestDecision(C(2, 25000, 60000, 0, 1)).ParsedAction == AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT);
            Check("11f fast but too early -> KEEP", refp.RequestDecision(C(2, 15000, 60000, 0, 1)).ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT);
            Check("11g boundary clamp L3 -> KEEP", refp.RequestDecision(C(3, 5000, 60000, 3, 1)).ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT);
            Check("11h boundary clamp L1 -> KEEP", refp.RequestDecision(C(1, 25000, 60000, 0, 1)).ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT);
            Check("11i no expected duration -> timing rules skipped", refp.RequestDecision(C(2, 95000, null, 0, 1)).ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT);
            var physCtx = C(2, 45000, 60000, 0, 1); physCtx.Physiological = new AdaptiveAR.Decision.PhysiologicalSample { RmssdMs = 5f, HeartRateBpm = 150f };
            Check("11j physiology alone never changes the reference decision", refp.RequestDecision(physCtx).ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT);
            var json = AdaptiveAR.Decision.ReferencePolicyConfig.FromJson(AdaptiveAR.Decision.MiniJson.ParseObject("{\"name\":\"lit_v1\",\"errorsOnStepIncrease\":9}"));
            Check("11k config from JSON", json.Name == "lit_v1" && json.ErrorsOnStepIncrease == 9 && new AdaptiveAR.Decision.ReferencePolicyProvider(json).ProviderId == "reference:lit_v1");

            // 12 model output parsing under the constrained action set
            string pid = "test";
            Check("12 JSON action ok", AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("{\"action\":\"INCREASE_SUPPORT\",\"reason\":\"slow\"}", pid, 1).Status == AdaptiveAR.Decision.ConstraintStatus.Ok
                  && AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("{\"action\":\"INCREASE_SUPPORT\"}", pid, 1).ParsedAction == AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT);
            Check("12b JSON with fences ok", AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("```json\n{\"action\":\"keep_support\"}\n```", pid, 1).Status == AdaptiveAR.Decision.ConstraintStatus.Ok);
            Check("12c bare token ok", AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("DECREASE_SUPPORT", pid, 1).ParsedAction == AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT);
            var oov = AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("{\"action\":\"MAXIMUM_SUPPORT\"}", pid, 1);
            Check("12d out of vocabulary -> fallback KEEP, recorded", oov.Status == AdaptiveAR.Decision.ConstraintStatus.OutOfVocabulary && oov.UsedFallback && oov.ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT && oov.RawOutput.Contains("MAXIMUM"));
            Check("12e bare unknown token -> OutOfVocabulary", AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("RESET_SUPPORT", pid, 1).Status == AdaptiveAR.Decision.ConstraintStatus.OutOfVocabulary);
            Check("12f prose -> Unparseable", AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("I think you should increase support now.", pid, 1).Status == AdaptiveAR.Decision.ConstraintStatus.Unparseable);
            Check("12g empty -> Unparseable", AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("", pid, 1).Status == AdaptiveAR.Decision.ConstraintStatus.Unparseable);
            var lc = new AdaptiveAR.Decision.LlmConfig();
            Check("12h http 500 -> ProviderError", AdaptiveAR.Decision.LlmDecisionCore.Interpret(lc, 500, "{\"error\":\"x\"}", 10, false, null).Status == AdaptiveAR.Decision.ConstraintStatus.ProviderError);
            Check("12i timeout -> ProviderTimeout", AdaptiveAR.Decision.LlmDecisionCore.Interpret(lc, 0, null, 8000, true, "timeout").Status == AdaptiveAR.Decision.ConstraintStatus.ProviderTimeout);
            string body = "{\"id\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"action\\\":\\\"KEEP_SUPPORT\\\",\\\"reason\\\":\\\"ok\\\"}\"}],\"usage\":{}}";
            var ok = AdaptiveAR.Decision.LlmDecisionCore.Interpret(lc, 200, body, 300, false, null);
            Check("12j anthropic body -> Ok KEEP", ok.Status == AdaptiveAR.Decision.ConstraintStatus.Ok && ok.ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT && ok.ProviderReason == "ok" && ok.LatencyMs == 300, ok.RawOutput);

            // 13 request build: headers carry the key, the body never does; context is the exact AI input
            var ctx = C(2, 30000, 60000, 1, 2); ctx.StepId = "step_02"; ctx.Physiological = null;
            var spec = AdaptiveAR.Decision.LlmDecisionCore.BuildRequest(lc, "sk-test-SECRET", ctx);
            Check("13 request spec", spec.Headers["x-api-key"] == "sk-test-SECRET" && !spec.Body.Contains("SECRET") && spec.Body.Contains("\"model\":\"" + lc.Model + "\"")
                  && spec.Body.Contains("step_02") && spec.Body.Contains("physiological_missing") && spec.Body.Contains("\"temperature\":0"), spec.Body);
            Check("13b context json has null physiology", AdaptiveAR.Decision.LlmDecisionCore.ContextToJson(ctx).Contains("\"physiological\":null"));

            // 14 scenario generator determinism + perturbations
            var s1 = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Synthetic(12, 3); var s2 = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Synthetic(12, 3);
            bool same = s1.Count == 12 && s2.Count == 12; for (int i = 0; same && i < 12; i++) same &= s1[i].Id == s2[i].Id && s1[i].Context.StepElapsedMs == s2[i].Context.StepElapsedMs;
            Check("14 synthetic scenarios deterministic", same);
            var pert = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Perturb(s1[0], 3);
            Check("14b perturbations", pert.Count == 4 && pert[1].Context.Physiological == null && !pert[1].Context.StepExpectedMs.HasValue && pert[2].Kind == "adversarial" && pert[0].BaseId == s1[0].Id);

            // 15 evaluation metrics with a deliberately flaky provider
            var flaky = new FlakyProvider();
            var scen = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Synthetic(6, 1);
            var all = new System.Collections.Generic.List<AdaptiveAR.Decision.Evaluation.Scenario>(scen);
            foreach (var b in scen) all.AddRange(AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Perturb(b, 1));
            var res = AdaptiveAR.Decision.Evaluation.EvaluationRunner.Run(flaky, refp, all, 3, null);
            var sum = res.Summary;
            Check("15 harness call count", res.Calls.Count == all.Count * 3 && sum.Scenarios == all.Count && sum.BaseScenarios == 6);
            Check("15b compliance < 1 with violations counted", sum.Compliance < 1.0 && sum.StatusCounts.ContainsKey("OutOfVocabulary") && sum.Failures.Count > 0);
            Check("15c consistency in (0,1]", sum.Consistency > 0 && sum.Consistency <= 1.0, sum.Consistency.ToString());
            Check("15d robustness kinds present", sum.RobustnessByKind.ContainsKey("noise") && sum.RobustnessByKind.ContainsKey("adversarial"));
            var refRes = AdaptiveAR.Decision.Evaluation.EvaluationRunner.Run(refp, refp, all, 2, null);
            Check("15e reference vs itself: consistency 1, agreement 1, compliance 1", refRes.Summary.Consistency == 1.0 && refRes.Summary.AgreementWithReference == 1.0 && refRes.Summary.Compliance == 1.0);
            string md = AdaptiveAR.Decision.Evaluation.EvaluationRunner.SummaryToMarkdown(refRes.Summary);
            Check("15f summary outputs", md.Contains("Decision consistency") && AdaptiveAR.Decision.MiniJson.ParseObject(AdaptiveAR.Decision.Evaluation.EvaluationRunner.SummaryToJson(refRes.Summary)) != null
                  && AdaptiveAR.Decision.MiniJson.ParseObject(AdaptiveAR.Decision.Evaluation.EvaluationRunner.CallToJsonLine(refRes.Calls[0])) != null);

            // 16 replay from a recorded decision_context_probe line
            string line = "{\"schema\":\"adaptivear.session.v2\",\"seq\":5,\"event\":\"decision_context_probe\",\"participant_id\":\"P1\",\"session_id\":\"S\",\"where\":\"x\",\"context\":{\"decision_index\":0,\"step_index\":2,\"step_id\":\"st\",\"step_count\":6,\"task_complexity\":2,\"operator_experience\":\"novice\",\"step_elapsed_ms\":12345,\"overall_elapsed_ms\":50000,\"step_expected_ms\":null,\"attempts_on_step\":1,\"errors_on_step\":0,\"errors_total\":1,\"current_support_level\":\"L2_Guided\",\"current_support_level_num\":2,\"support_changes_on_step\":0,\"support_changes_total\":1,\"physiological_missing\":false,\"physiological\":{\"rmssd_ms\":41.5,\"heart_rate_bpm\":70,\"mean_ibi_ms\":850,\"quality\":0.9,\"age_ms\":300,\"source\":\"Polar\"}}}";
            var replay = AdaptiveAR.Decision.Evaluation.ContextJsonl.Read(new[] { line, "not json", "" });
            Check("16 replay probe line", replay.Count == 1 && replay[0].Context.StepId == "st" && replay[0].Context.CurrentSupportLevel == AdaptiveAR.Support.SupportLevel.L2_Guided
                  && !replay[0].Context.StepExpectedMs.HasValue && replay[0].Context.Physiological != null && Math.Abs(replay[0].Context.Physiological.RmssdMs.Value - 41.5f) < 1e-4);
        }

        /// <summary>Returns a valid action most of the time, an out-of-vocabulary token sometimes, prose rarely - exercising the metrics.</summary>
        private sealed class FlakyProvider : AdaptiveAR.Decision.IDecisionProvider
        {
            private int _n;
            public string ProviderId { get { return "flaky"; } }
            public AdaptiveAR.Decision.DecisionResponse RequestDecision(AdaptiveAR.Decision.DecisionContext c)
            {
                _n++;
                string text = _n % 7 == 0 ? "{\"action\":\"MAXIMUM_SUPPORT\"}" : (_n % 11 == 0 ? "sure, keep going" : (_n % 3 == 0 ? "{\"action\":\"INCREASE_SUPPORT\"}" : "{\"action\":\"KEEP_SUPPORT\"}"));
                var r = AdaptiveAR.Decision.LlmDecisionCore.ParseActionText(text, ProviderId, 50 + _n % 5);
                return r;
            }
        }
    }

    public static class Program
    {
        private static int _failed, _passed;

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { _passed++; Console.WriteLine($"PASS  {name}"); }
            else { _failed++; Console.WriteLine($"FAIL  {name}  {detail}"); }
        }

        private static PpiSample S(int ppi, bool blocker = false, int hr = 70, bool skin = true, bool skinSupported = true, int err = 5)
        {
            return new PpiSample { PpiMs = ppi, Blocker = blocker, DeviceHrBpm = hr, SkinContact = skin, SkinContactSupported = skinSupported, ErrorEstimateMs = err, Source = "test" };
        }

        public static int Main()
        {
            // 1. constant intervals -> RMSSD 0
            double? r1 = HrvMath.Rmssd(new double[] { 800, 800, 800, 800, 800 });
            Check("1 constant intervals -> RMSSD 0", r1.HasValue && Math.Abs(r1.Value) < 1e-9, $"got {r1}");

            // 2. known short sequence: 800, 820, 790, 810 -> diffs 20, -30, 20 -> squares 400, 900, 400 -> mean 566.667 -> sqrt 23.8048
            double? r2 = HrvMath.Rmssd(new double[] { 800, 820, 790, 810 });
            double expected = Math.Sqrt((400.0 + 900.0 + 400.0) / 3.0);
            Check("2 known sequence RMSSD", r2.HasValue && Math.Abs(r2.Value - expected) < 1e-9, $"got {r2} expected {expected}");
            Check("2b mean IBI", Math.Abs(HrvMath.MeanIbi(new double[] { 800, 820, 790, 810 }).Value - 805.0) < 1e-9);
            Check("2c HR from IBI", Math.Abs(HrvMath.HeartRateFromIbi(800).Value - 75.0) < 1e-9);
            Check("2d RMSSD needs 2 intervals", !HrvMath.Rmssd(new double[] { 800 }).HasValue && !HrvMath.Rmssd(new double[0]).HasValue);

            // 3. insufficient samples -> unavailable / null snapshot
            var cfg = new PhysiologyWindowConfig { WindowMs = 60000, MinAccepted = 10, MinPpiMs = 300, MaxPpiMs = 2000 };
            var w = new PhysiologyWindow(cfg);
            double t = 0;
            for (int i = 0; i < 9; i++) { t += 800; w.Admit(S(800 + (i % 2) * 20), t); }
            PhysioSummary s3 = w.Summarize(t);
            Check("3 insufficient samples -> RMSSD null, unavailable", !s3.Available && !s3.RmssdMs.HasValue && s3.NAccepted == 9, $"available={s3.Available} n={s3.NAccepted}");
            Check("3b snapshot null when unavailable", w.Snapshot(t, 15000) == null);

            // 4. blocker sample -> recorded, excluded from the live window
            w = new PhysiologyWindow(cfg); t = 0;
            for (int i = 0; i < 12; i++) { t += 800; w.Admit(S(800), t); }
            t += 800; var blocked = S(400, blocker: true); w.Admit(blocked, t);
            PhysioSummary s4 = w.Summarize(t);
            Check("4 blocker sample excluded but counted", !blocked.Accepted && blocked.RejectReason == PhysiologyWindow.RejectBlocker
                  && s4.NRaw == 13 && s4.NAccepted == 12 && s4.NRejectedBlocker == 1 && Math.Abs(s4.RmssdMs.Value) < 1e-9,
                  $"accepted={blocked.Accepted} reason={blocked.RejectReason} raw={s4.NRaw} acc={s4.NAccepted} rmssd={s4.RmssdMs}");

            // 5. out-of-range sample -> recorded, excluded
            t += 800; var outOfRange = S(2500); w.Admit(outOfRange, t);
            t += 800; var tooShort = S(250); w.Admit(tooShort, t);
            PhysioSummary s5 = w.Summarize(t);
            Check("5 out-of-range samples excluded", !outOfRange.Accepted && outOfRange.RejectReason == PhysiologyWindow.RejectRange
                  && !tooShort.Accepted && s5.NRejectedRange == 2 && s5.NAccepted == 12 && Math.Abs(s5.Quality.Value - 12.0 / 15.0) < 1e-9,
                  $"range={s5.NRejectedRange} acc={s5.NAccepted} q={s5.Quality}");

            // 5b. skin-contact rejection OFF by default, ON when configured
            var noSkin = S(800, skin: false, skinSupported: true); t += 800; w.Admit(noSkin, t);
            Check("5b skin-contact not rejected by default", noSkin.Accepted);
            var wSkin = new PhysiologyWindow(new PhysiologyWindowConfig { RejectNoSkinContact = true });
            var noSkin2 = S(800, skin: false, skinSupported: true); wSkin.Admit(noSkin2, 1000);
            var noSkinUnsupported = S(800, skin: false, skinSupported: false); wSkin.Admit(noSkinUnsupported, 2000);
            Check("5c skin-contact rejected only when configured AND supported", !noSkin2.Accepted && noSkin2.RejectReason == PhysiologyWindow.RejectSkin && noSkinUnsupported.Accepted);

            // 6. valid samples populate the rolling window; old ones leave it
            w = new PhysiologyWindow(new PhysiologyWindowConfig { WindowMs = 10000, MinAccepted = 3 }); t = 0;
            for (int i = 0; i < 20; i++) { t += 1000; w.Admit(S(1000 + (i % 3) * 10, hr: 60), t); }
            PhysioSummary s6 = w.Summarize(t);
            Check("6 rolling window keeps only the last 10 s", s6.NAccepted == 10 && s6.NRaw == 10 && s6.Available && s6.RmssdMs.HasValue && s6.LastDeviceHrBpm == 60,
                  $"acc={s6.NAccepted} raw={s6.NRaw}");
            Check("6b completeness ~ accepted time / window", Math.Abs(s6.Completeness - (s6.MeanIbiMs.Value * 10) / 10000.0) < 1e-6, $"c={s6.Completeness}");
            Check("6c HR from IBI consistent", Math.Abs(s6.HrFromIbiBpm.Value - 60000.0 / s6.MeanIbiMs.Value) < 1e-9);

            // 7. stale snapshot -> null
            PhysioSummary fresh = w.Snapshot(t + 1000, 15000);
            PhysioSummary stale = w.Snapshot(t + 20000, 15000);
            Check("7 fresh snapshot available", fresh != null && fresh.Available);
            Check("7b stale snapshot null (age > maxAge)", stale == null);
            Check("7c window emptied after time passes -> unavailable", !w.Summarize(t + 60000).Available);

            // 8. bridge message parsing: official layout fields
            PpiSample p = PpiSample.Parse("S|12|1|3|28|1234567890|62|830|7|6", 5000, 12.5, "PolarVeritySense/Polar Sense X");
            Check("8 parse bridge sample", p != null && p.PacketSeq == 12 && p.SampleIndex == 1 && p.SamplesInPacket == 3 && p.PacketLength == 28
                  && p.DeviceTimestampNs == 1234567890UL && p.DeviceHrBpm == 62 && p.PpiMs == 830 && p.ErrorEstimateMs == 7
                  && !p.Blocker && p.SkinContact && p.SkinContactSupported && p.ReceiptSessionMs == 5000);
            Check("8b parse rejects malformed", PpiSample.Parse("S|12|1", 0, 0, "x") == null && PpiSample.Parse("X|1|2|3|4|5|6|7|8|9", 0, 0, "x") == null);
            Check("8c flags bit0 blocker", PpiSample.Parse("S|1|0|1|16|0|60|900|3|1", 0, 0, "x").Blocker);

            // 9. mapping to the decision-context sample: null when unavailable / stale, fields otherwise
            var w9 = new PhysiologyWindow(new PhysiologyWindowConfig { WindowMs = 60000, MinAccepted = 5 });
            double t9 = 0;
            for (int i = 0; i < 3; i++) { t9 += 800; w9.Admit(S(800, hr: 75), t9); }
            Check("9 mapper returns null when unavailable", PhysiologicalSampleMapper.ToSample(w9.Summarize(t9), "test") == null
                  && PhysiologicalSampleMapper.ToSample(null, "test") == null);
            for (int i = 0; i < 5; i++) { t9 += 800; w9.Admit(S(i % 2 == 0 ? 800 : 820, hr: 75), t9); }
            var mapped = PhysiologicalSampleMapper.ToSample(w9.Snapshot(t9 + 100, 15000), "PolarVeritySense/x");
            double expectedIbi = HrvMath.MeanIbi(new double[] { 800, 800, 800, 800, 820, 800, 820, 800 }).Value;
            Check("9b mapper not null / not empty", mapped != null && !mapped.IsEmpty);
            Check("9b1 mapper HR = device HR", mapped != null && mapped.HeartRateBpm.HasValue && Math.Abs(mapped.HeartRateBpm.Value - 75f) < 1e-6, mapped == null ? "null" : $"hr={mapped.HeartRateBpm}");
            Check("9b2 mapper RMSSD present", mapped != null && mapped.RmssdMs.HasValue, mapped == null ? "null" : $"rmssd={mapped.RmssdMs}");
            Check("9b3 mapper mean IBI", mapped != null && mapped.MeanIbiMs.HasValue && Math.Abs(mapped.MeanIbiMs.Value - expectedIbi) < 1e-3, mapped == null ? "null" : $"ibi={mapped.MeanIbiMs} expected={expectedIbi}");
            Check("9b4 mapper quality / age / source", mapped != null && mapped.Quality.HasValue && Math.Abs(mapped.Quality.Value - 1f) < 1e-6 && mapped.AgeMs.HasValue && mapped.Source == "PolarVeritySense/x",
                  mapped == null ? "null" : $"q={mapped.Quality} age={mapped.AgeMs} src={mapped.Source}");
            Check("9c mapper null when stale", PhysiologicalSampleMapper.ToSample(w9.Snapshot(t9 + 30000, 15000), "x") == null);
            var ctx = new AdaptiveAR.Decision.DecisionContext { Physiological = PhysiologicalSampleMapper.ToSample(w9.Snapshot(t9 + 30000, 15000), "x") };
            Check("9d DecisionContext.PhysiologicalMissing when stale", ctx.PhysiologicalMissing);
            ctx.Physiological = mapped;
            Check("9e DecisionContext carries the sample", !ctx.PhysiologicalMissing);

            DecisionTests.Run(Check);

            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }
    }
}
