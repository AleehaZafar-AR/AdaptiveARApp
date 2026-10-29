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

            // 12k regression: the real 2026-10-28 Quest output (JSON reason truncated by max_tokens) stays Unparseable, fallback KEEP
            string quest = "{\"action\":\"INCREASE_SUPPORT\",\"reason\":\"Seven errors on the step at L1_Minimal, with no support changes yet, indicate the operator needs more assistance, even though elap";
            var questResp = AdaptiveAR.Decision.LlmDecisionCore.ParseActionText(quest, pid, 1500);
            Check("12k truncated JSON from Quest -> Unparseable, fallback KEEP, raw kept", questResp.Status == AdaptiveAR.Decision.ConstraintStatus.Unparseable && questResp.UsedFallback
                  && questResp.ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT && questResp.RawOutput == quest, questResp.Status.ToString());
            string questBody = "{\"id\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"" + quest.Replace("\"", "\\\"") + "\"}],\"stop_reason\":\"max_tokens\"}";
            Check("12k2 same via Interpret", AdaptiveAR.Decision.LlmDecisionCore.Interpret(lc, 200, questBody, 1500, false, null).Status == AdaptiveAR.Decision.ConstraintStatus.Unparseable);
            // 12l the contract reply: one bare token in an Anthropic body -> Ok, applied action
            string tokenBody = "{\"id\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"INCREASE_SUPPORT\"}],\"stop_reason\":\"end_turn\"}";
            var tokenResp = AdaptiveAR.Decision.LlmDecisionCore.Interpret(lc, 200, tokenBody, 1300, false, null);
            Check("12l bare token body -> Ok INCREASE", tokenResp.Status == AdaptiveAR.Decision.ConstraintStatus.Ok && !tokenResp.UsedFallback && tokenResp.ParsedAction == AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT, tokenResp.RawOutput);
            Check("12m token with whitespace/newline -> Ok", AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("  DECREASE_SUPPORT\n", pid, 1).Status == AdaptiveAR.Decision.ConstraintStatus.Ok);
            Check("12n token plus prose -> Unparseable (parser not loosened)", AdaptiveAR.Decision.LlmDecisionCore.ParseActionText("INCREASE_SUPPORT because of errors", pid, 1).Status == AdaptiveAR.Decision.ConstraintStatus.Unparseable);

            // 13 request build: headers carry the key, the body never does; context is the exact AI input
            var ctx = C(2, 30000, 60000, 1, 2); ctx.StepId = "step_02"; ctx.Physiological = null;
            var spec = AdaptiveAR.Decision.LlmDecisionCore.BuildRequest(lc, "sk-test-SECRET", ctx);
            Check("13 request spec", spec.Headers["x-api-key"] == "sk-test-SECRET" && !spec.Body.Contains("SECRET") && spec.Body.Contains("\"model\":\"" + lc.Model + "\"")
                  && spec.Body.Contains("step_02") && spec.Body.Contains("physiological_missing") && !spec.Body.Contains("temperature"), spec.Body);
            // 13c the Sonnet 5.5 request must not carry "temperature" (HTTP 400 on device), even when a config file still names it
            var sonnet = AdaptiveAR.Decision.LlmConfig.FromJson(AdaptiveAR.Decision.MiniJson.ParseObject("{\"model\":\"claude-sonnet-5-5\",\"temperature\":0,\"maxTokens\":64}"));
            var sonnetSpec = AdaptiveAR.Decision.LlmDecisionCore.BuildRequest(sonnet, "k", ctx);
            var sonnetBody = AdaptiveAR.Decision.MiniJson.ParseObject(sonnetSpec.Body);
            Check("13c sonnet-5-5 request has no temperature", sonnet.Model == "claude-sonnet-5-5" && !sonnetBody.ContainsKey("temperature") && !sonnetSpec.Body.Contains("temperature")
                  && sonnetBody.ContainsKey("model") && sonnetBody.ContainsKey("max_tokens") && sonnetBody.ContainsKey("system") && sonnetBody.ContainsKey("messages"), sonnetSpec.Body);
            Check("13d provider id has no sampling suffix", sonnet.ProviderId == "anthropic:claude-sonnet-5-5", sonnet.ProviderId);
            // 13j provider reliability: thinking.type = between_tools exactly, no other thinking field, no sampling/effort fields, every other property unchanged
            var thinking = AdaptiveAR.Decision.MiniJson.GetObject(sonnetBody, "thinking");
            Check("13j sonnet-5-5 request sends exactly thinking.type=between_tools; nothing else new", thinking != null && thinking.Count == 1 && AdaptiveAR.Decision.MiniJson.GetString(thinking, "type", "") == "between_tools"
                  && !sonnetBody.ContainsKey("temperature") && !sonnetBody.ContainsKey("top_p") && !sonnetBody.ContainsKey("top_k") && !sonnetBody.ContainsKey("output_config") && !sonnetBody.ContainsKey("tools")
                  && sonnetBody.Count == 5 && sonnetBody.ContainsKey("model") && sonnetBody.ContainsKey("max_tokens") && sonnetBody.ContainsKey("system") && sonnetBody.ContainsKey("messages")
                  && AdaptiveAR.Decision.MiniJson.GetDouble(AdaptiveAR.Decision.MiniJson.ParseObject(AdaptiveAR.Decision.LlmDecisionCore.BuildRequest(new AdaptiveAR.Decision.LlmConfig(), "k", ctx).Body), "max_tokens", 0) == 16, sonnetSpec.Body);
            // 13k regression: the observed smoke-test body (a thinking block, no text block, budget exhausted) stays an Unparseable fallback; a text token after a thinking block still parses
            string thinkOnly = "{\"model\":\"claude-sonnet-5-5\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"CAQS\"}],\"stop_reason\":\"max_tokens\",\"usage\":{\"input_tokens\":2500,\"output_tokens\":16}}";
            var thinkResp = AdaptiveAR.Decision.LlmDecisionCore.Interpret(sonnet, 200, thinkOnly, 900, false, null);
            string tokenAfterThink = "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"x\"},{\"type\":\"text\",\"text\":\"DECREASE_SUPPORT\"}],\"stop_reason\":\"end_turn\"}";
            var afterThink = AdaptiveAR.Decision.LlmDecisionCore.Interpret(sonnet, 200, tokenAfterThink, 900, false, null);
            Check("13k thinking-only body -> Unparseable fallback KEEP; a text token after a thinking block still parses", thinkResp.Status == AdaptiveAR.Decision.ConstraintStatus.Unparseable && thinkResp.UsedFallback && thinkResp.ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT
                  && afterThink.Status == AdaptiveAR.Decision.ConstraintStatus.Ok && afterThink.ParsedAction == AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT, thinkResp.Status + " / " + afterThink.Status);
            // 13f the production prompt targets one bare token: all three tokens named, no reason/JSON requested, user turn carries the instruction, small budget
            string sys = AdaptiveAR.Decision.LlmDecisionCore.DefaultSystemPrompt;
            var prodBody = AdaptiveAR.Decision.MiniJson.ParseObject(AdaptiveAR.Decision.LlmDecisionCore.BuildRequest(new AdaptiveAR.Decision.LlmConfig(), "k", ctx).Body);
            string prodSystem = AdaptiveAR.Decision.MiniJson.GetString(prodBody, "system", "");
            var prodMsgs = AdaptiveAR.Decision.MiniJson.GetList(prodBody, "messages");
            string prodUser = AdaptiveAR.Decision.MiniJson.GetString((System.Collections.Generic.Dictionary<string, object>)prodMsgs[0], "content", "");
            Check("13f production prompt expects only the enum token",
                  prodSystem == sys && sys.Contains("KEEP_SUPPORT") && sys.Contains("INCREASE_SUPPORT") && sys.Contains("DECREASE_SUPPORT")
                  && sys.Contains("exactly one of these three tokens") && sys.Contains("no JSON") && sys.Contains("no reason") && !sys.Contains("\"reason\"") && !sys.Contains("{\"action\"")
                  && prodUser.EndsWith(AdaptiveAR.Decision.LlmDecisionCore.UserTurnInstruction) && prodUser.StartsWith("{")
                  && AdaptiveAR.Decision.MiniJson.GetDouble(prodBody, "max_tokens", 0) == 16 && new AdaptiveAR.Decision.LlmConfig().MaxTokens == 16, prodSystem);
            // 13h physiology's role is explicit: primary task evidence, corroborating only, never alone, missing/stale/low-quality never penalises, no threshold
            Check("13h prompt states the physiology role", sys.Contains("Task performance evidence is always primary") && sys.Contains("corroborating context only")
                  && sys.Contains("must never by itself cause a support change") && sys.Contains("must never count against the operator")
                  && sys.Contains("no fixed physiological threshold") && !sys.ToLowerInvariant().Contains("stress level"), sys);
            // 13i schedule config: wrong-component trigger is on by default and configurable
            var schedDefault = new AdaptiveAR.Decision.DecisionScheduleConfig();
            var schedOff = AdaptiveAR.Decision.DecisionScheduleConfig.FromJson(AdaptiveAR.Decision.MiniJson.ParseObject("{\"onWrongComponent\":false,\"minCallIntervalSeconds\":30,\"minIntervalSeconds\":99}"));
            Check("13i wrong-component trigger config (legacy minIntervalSeconds ignored)", schedDefault.OnWrongComponent && !schedOff.OnWrongComponent && schedOff.MinCallIntervalSeconds == 30 && schedDefault.Describe().Contains("onWrongComponent=True") && !schedDefault.Describe().Contains("minIntervalSeconds"), schedDefault.Describe());
            Check("13g default config asset uses the token budget", AdaptiveAR.Decision.DecisionConfig.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(Program.RepoRoot(), "Assets", "Resources", "Decision", "decision_config.json")), "file").Llm.MaxTokens == 16);
            Check("13e friendly model name", AdaptiveAR.Decision.LlmDecisionCore.FriendlyModelName("claude-sonnet-5-5") == "Claude Sonnet 5.5"
                  && AdaptiveAR.Decision.LlmDecisionCore.FriendlyModelName("claude-opus-5-5") == "Claude Opus 5.5" && sonnet.DisplayName == "Claude Sonnet 5.5", sonnet.DisplayName);
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

            // 17 bidirectional adaptation under the reference policy (defaults: errors>=2 increase; recovery = 2 successes, 0 errors since change, >=20 s quiet; 20 s cooldown)
            AdaptiveAR.Decision.DecisionContext R(int level, float elapsed, int errorsOnStep, int errorsSince, int successesSince, float? sinceError, float? sinceChange, int changes)
                => new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = (AdaptiveAR.Support.SupportLevel)level, StepElapsedMs = elapsed, StepExpectedMs = 60000f,
                       ErrorsOnStep = errorsOnStep, AttemptsOnStep = errorsOnStep + 1, ErrorsSinceLastSupportChange = errorsSince, SuccessfulActionsSinceLastSupportChange = successesSince,
                       TimeSinceLastErrorMs = sinceError, TimeSinceLastSupportChangeMs = sinceChange, SupportChangesOnStep = changes };
            AdaptiveAR.Support.SupportLevel Step(AdaptiveAR.Decision.DecisionContext c, string name, AdaptiveAR.Support.SupportAction expect, AdaptiveAR.Support.SupportLevel expectLevel)
            {
                var r = refp.RequestDecision(c);
                var next = AdaptiveAR.Support.SupportLevels.Apply(c.CurrentSupportLevel, r.ParsedAction);
                Check(name, r.Status == AdaptiveAR.Decision.ConstraintStatus.Ok && r.ParsedAction == expect && next == expectLevel, $"{r.ParsedAction} -> {next} ({r.ProviderReason})");
                return next;
            }
            var l = Step(R(1, 20000, 2, 2, 0, 1000f, null, 0), "17a struggling L1 -> L2", AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            l = Step(R((int)l, 40000, 5, 3, 0, 2000f, 20000f, 1), "17b continued struggle L2 -> L3", AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L3_Assisted);
            l = Step(R((int)l, 90000, 5, 0, 2, 50000f, 50000f, 2), "17c sustained success L3 -> L2 (cumulative errors/slow step no longer re-fire)", AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            l = Step(R((int)l, 130000, 5, 0, 3, 90000f, 40000f, 3), "17d continued success L2 -> L1", AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L1_Minimal);
            Step(R(2, 130000, 6, 1, 3, 3000f, 40000f, 3), "17e new error after recovery -> KEEP (no premature downgrade)", AdaptiveAR.Support.SupportAction.KEEP_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            Step(R(2, 130000, 5, 0, 3, 15000f, 40000f, 3), "17e2 too soon after the last error -> KEEP", AdaptiveAR.Support.SupportAction.KEEP_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            var l3 = Step(R(3, 90000, 5, 0, 4, 60000f, 60000f, 2), "17f one decision from L3 lands on L2, never L1", AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            Step(R((int)l3, 91000, 5, 0, 4, 61000f, 1000f, 3), "17f2 immediate second decision -> KEEP (cooldown), still L2", AdaptiveAR.Support.SupportAction.KEEP_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            Check("17g DECREASE is one level by construction", AdaptiveAR.Support.SupportLevels.Apply(AdaptiveAR.Support.SupportLevel.L3_Assisted, AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT) == AdaptiveAR.Support.SupportLevel.L2_Guided);
            var physOnly = R(3, 90000, 5, 0, 0, 1000f, 50000f, 2); physOnly.Physiological = new AdaptiveAR.Decision.PhysiologicalSample { RmssdMs = 90f, HeartRateBpm = 60f, Quality = 1f };
            Check("17h calm physiology alone never decreases", refp.RequestDecision(physOnly).ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT);
            string recentJson = AdaptiveAR.Decision.LlmDecisionCore.ContextToJson(R(2, 40000, 5, 0, 2, null, 20000f, 1));
            Check("17i recent fields in ai_input", recentJson.Contains("\"errors_since_last_support_change\":0") && recentJson.Contains("\"successful_actions_since_last_support_change\":2")
                  && recentJson.Contains("\"time_since_last_error_ms\":null") && recentJson.Contains("\"time_since_last_support_change_ms\":20000"), recentJson);
            Check("17j prompt states bidirectional rules", sys.Contains("bidirectional") && sys.Contains("DECREASE_SUPPORT") && sys.Contains("exactly one level") && sys.Contains("must never by itself cause a support change") && sys.Contains("recent errors continue"), "");
            var schedAc = AdaptiveAR.Decision.DecisionScheduleConfig.FromJson(AdaptiveAR.Decision.MiniJson.ParseObject("{\"onActionComplete\":false}"));
            Check("17k action-complete trigger config", new AdaptiveAR.Decision.DecisionScheduleConfig().OnActionComplete && !schedAc.OnActionComplete);

            // 18 causal escalation rule: after a level change only NEW difficulty since it can escalate again
            AdaptiveAR.Decision.DecisionContext RR(int level, float elapsed, int errorsOnStep, int attemptsOnStep, int errorsSince, int attemptsSince, int successesSince, float? sinceError, float? sinceChange, int changes)
                => new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = (AdaptiveAR.Support.SupportLevel)level, StepElapsedMs = elapsed, StepExpectedMs = 60000f,
                       ErrorsOnStep = errorsOnStep, AttemptsOnStep = attemptsOnStep, ErrorsSinceLastSupportChange = errorsSince, AttemptsSinceLastSupportChange = attemptsSince,
                       SuccessfulActionsSinceLastSupportChange = successesSince, TimeSinceLastErrorMs = sinceError, TimeSinceLastSupportChangeMs = sinceChange, SupportChangesOnStep = changes };
            // 18a many historical errors at L1 (no change on the step yet): cumulative evidence justifies the first escalation
            var l18 = Step(RR(1, 45000, 6, 9, 6, 9, 0, 1500f, null, 0), "18a many historical L1 errors -> L2", AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            // 18b at L2 after that change: zero new errors and successes accruing -> never L3, whatever the cumulative counters and elapsed time say
            bool neverL3 = true; string firstBad = null;
            foreach (int successes in new[] { 1, 2, 3 })
                foreach (float elapsed in new[] { 100000f, 200000f, 400000f })
                    foreach (float sinceChange in new[] { 25000f, 60000f, 150000f })
                    {
                        var c = RR((int)l18, elapsed, 6, 9 + successes, 0, successes, successes, 60000f, sinceChange, 1);
                        var r = refp.RequestDecision(c);
                        var next = AdaptiveAR.Support.SupportLevels.Apply(c.CurrentSupportLevel, r.ParsedAction);
                        if (r.ParsedAction == AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT || next == AdaptiveAR.Support.SupportLevel.L3_Assisted)
                        { neverL3 = false; firstBad = firstBad ?? $"successes={successes} elapsed={elapsed} sinceChange={sinceChange}: {r.ParsedAction} ({r.ProviderReason})"; }
                    }
            Check("18b zero new errors + successes at L2 -> never L3 (27 contexts)", neverL3, firstBad);
            var keepHist = refp.RequestDecision(RR(2, 200000, 6, 10, 0, 1, 1, 60000f, 60000f, 1));
            Check("18b2 reason names the historical-only evidence", keepHist.ParsedAction == AdaptiveAR.Support.SupportAction.KEEP_SUPPORT && keepHist.ProviderReason.Contains("historical only") && keepHist.ProviderReason.Contains("no new difficulty"), keepHist.ProviderReason);
            // 18c new difficulty after the change makes L3 eligible: new counted errors, or repeated attempts with no success since the change
            Step(RR(2, 120000, 8, 11, 2, 2, 0, 1000f, 30000f, 1), "18c new errors after L2 -> L3 eligible", AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L3_Assisted);
            Step(RR(2, 120000, 6, 13, 0, 4, 0, 60000f, 30000f, 1), "18c2 repeated unsuccessful attempts after L2 -> L3 eligible", AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L3_Assisted);
            Step(RR(2, 120000, 6, 13, 0, 4, 1, 60000f, 30000f, 1), "18c3 the same attempts WITH a success since the change -> not an increase", AdaptiveAR.Support.SupportAction.KEEP_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            // 18d cumulative elapsed time alone after an escalation cannot force L3 (ratio 6.7 against the expected duration)
            Step(RR(2, 400000, 0, 1, 0, 0, 0, null, 60000f, 1), "18d elapsed time alone after escalation -> KEEP, not L3", AdaptiveAR.Support.SupportAction.KEEP_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            Step(RR(2, 400000, 6, 9, 0, 0, 0, 120000f, 120000f, 1), "18d2 old errors + long elapsed, nothing new -> KEEP, not L3", AdaptiveAR.Support.SupportAction.KEEP_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            // 18e recovery evidence still de-escalates, one level at a time
            Step(RR(2, 150000, 6, 11, 0, 2, 2, 40000f, 40000f, 1), "18e recovery at L2 -> L1", AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L1_Minimal);
            Step(RR(3, 150000, 9, 14, 0, 2, 2, 40000f, 40000f, 2), "18e2 recovery at L3 -> L2 (never L1)", AdaptiveAR.Support.SupportAction.DECREASE_SUPPORT, AdaptiveAR.Support.SupportLevel.L2_Guided);
            // 18f (v2) the LLM contract hands the model the since-change fields and leaves the interpretation to it: no hard escalation rule in the prompt
            Check("18f prompt names the since-change fields and no hard escalation rule",
                  sys.Contains("errors_since_last_support_change") && sys.Contains("attempts_since_last_support_change") && sys.Contains("successful_actions_since_last_support_change")
                  && !sys.Contains("must never by themselves justify") && !sys.Contains("never INCREASE_SUPPORT"), "");
            // 18g the new field travels in ai_input and replays from a recorded line
            string attJson = AdaptiveAR.Decision.LlmDecisionCore.ContextToJson(RR(2, 40000, 5, 8, 0, 3, 2, null, 20000f, 1));
            var attReplay = AdaptiveAR.Decision.Evaluation.ContextJsonl.Read(new[] { "{\"event\":\"decision_context_probe\",\"context\":{\"step_id\":\"s\",\"current_support_level\":\"L2_Guided\",\"support_changes_on_step\":1,\"errors_since_last_support_change\":0,\"attempts_since_last_support_change\":3,\"successful_actions_since_last_support_change\":2}}" });
            Check("18g attempts_since_last_support_change in ai_input and replay", attJson.Contains("\"attempts_since_last_support_change\":3") && attReplay.Count == 1 && attReplay[0].Context.AttemptsSinceLastSupportChange == 3, attJson);
            // 18h the evidence line restates every evidence set without naming a regime or a verdict
            string ev = AdaptiveAR.Decision.DecisionEvidence.Summarise(RR(2, 85000, 6, 9, 0, 3, 2, 25000f, 30000f, 1));
            Check("18h evidence summary", ev.StartsWith("cumulative: errors_on_step=6 attempts_on_step=9 step_elapsed_ms=85000 elapsed_ratio=1.42 level=L2_Guided changes_on_step=1; since_change: errors=0 attempts=3 successes=2 last_change_ms=30000; recent(")
                  && ev.Contains("last_error_ms=25000") && ev.Contains("events: (none)") && ev.Contains("physiology=missing") && !ev.Contains("governs") && !ev.Contains("historical"), ev);
            // 18i the thresholds are config keys, not constants
            var rpCfg = AdaptiveAR.Decision.DecisionConfig.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(Program.RepoRoot(), "Assets", "Resources", "Decision", "decision_config.json")), "file").ReferencePolicy;
            var rpOverride = AdaptiveAR.Decision.ReferencePolicyConfig.FromJson(AdaptiveAR.Decision.MiniJson.ParseObject("{\"errorsSinceChangeIncrease\":1,\"attemptsSinceChangeIncrease\":2}"));
            Check("18i since-change thresholds from config", rpCfg.ErrorsSinceChangeIncrease == 2 && rpCfg.AttemptsSinceChangeIncrease == 4 && rpOverride.ErrorsSinceChangeIncrease == 1 && rpOverride.AttemptsSinceChangeIncrease == 2
                  && rpCfg.Describe().Contains("errorsSinceChangeIncrease=2"), rpCfg.Describe());
            var oneErr = new AdaptiveAR.Decision.ReferencePolicyProvider(rpOverride).RequestDecision(RR(2, 120000, 6, 10, 1, 1, 0, 1000f, 30000f, 1));
            Check("18i2 override: one new error escalates", oneErr.ParsedAction == AdaptiveAR.Support.SupportAction.INCREASE_SUPPORT, oneErr.ProviderReason);
            // 18j determinism of the rule under the same context
            var detA = refp.RequestDecision(RR(2, 200000, 6, 10, 0, 1, 1, 60000f, 60000f, 1)); var detB = refp.RequestDecision(RR(2, 200000, 6, 10, 0, 1, 1, 60000f, 60000f, 1));
            Check("18j same context, same decision and reason", detA.ParsedAction == detB.ParsedAction && detA.ProviderReason == detB.ProviderReason);

            // 19 task-event ledger: objective temporal features, never a classification
            var led = new AdaptiveAR.Decision.TaskEventLedger();
            led.Add(AdaptiveAR.Decision.TaskEventKind.Error, 5000f, "a1", "L1_Minimal");
            led.Add(AdaptiveAR.Decision.TaskEventKind.Error, 12000f, "a1", "L1_Minimal");
            led.Add(AdaptiveAR.Decision.TaskEventKind.SupportIncrease, 13000f, "a1", "L2_Guided");
            led.Add(AdaptiveAR.Decision.TaskEventKind.Error, 20000f, "a1", "L2_Guided");
            led.Add(AdaptiveAR.Decision.TaskEventKind.Success, 30000f, "a1", "L2_Guided");
            led.Add(AdaptiveAR.Decision.TaskEventKind.Success, 45000f, "a2", "L2_Guided");
            var lc19 = new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = AdaptiveAR.Support.SupportLevel.L2_Guided };
            led.Fill(lc19, 50000f, 60000f, 12);
            Check("19a pattern error->error->increase->error->success->success: features",
                  lc19.RecentErrorCount == 3 && lc19.RecentFailedAttemptCount == 3 && lc19.RecentWrongComponentCount == 0 && lc19.ConsecutiveSuccessfulActions == 2
                  && lc19.ErrorsSinceLastSuccess == 0 && lc19.FailedAttemptsSinceLastSuccess == 0 && lc19.TimeSinceLastSuccessMs == 5000f && lc19.RecentEvents.Count == 6
                  && lc19.RecentEvents[0].Kind == AdaptiveAR.Decision.TaskEventKind.Error && lc19.RecentEvents[0].AgeMs == 45000f && lc19.RecentEvents[5].Kind == AdaptiveAR.Decision.TaskEventKind.Success && lc19.RecentEvents[5].AgeMs == 5000f,
                  AdaptiveAR.Decision.TaskEventLedger.Describe(lc19.RecentEvents));
            var lc19b = new AdaptiveAR.Decision.DecisionContext(); led.Fill(lc19b, 50000f, 25000f, 3);
            Check("19b window and history bounds", lc19b.RecentErrorCount == 0 && lc19b.RecentFailedAttemptCount == 0 && lc19b.RecentEvents.Count == 3
                  && lc19b.RecentEvents[0].Kind == AdaptiveAR.Decision.TaskEventKind.Error && lc19b.ConsecutiveSuccessfulActions == 2, AdaptiveAR.Decision.TaskEventLedger.Describe(lc19b.RecentEvents));
            led.Add(AdaptiveAR.Decision.TaskEventKind.WrongComponent, 52000f, "a2", "L2_Guided");
            led.Add(AdaptiveAR.Decision.TaskEventKind.FailedAttempt, 54000f, "a2", "L2_Guided");
            var lc19c = new AdaptiveAR.Decision.DecisionContext(); led.Fill(lc19c, 55000f, 60000f, 12);
            Check("19c new difficulty after successes breaks the run; wrong part counts as an error, a near miss as a failed attempt only",
                  lc19c.ConsecutiveSuccessfulActions == 0 && lc19c.ErrorsSinceLastSuccess == 1 && lc19c.FailedAttemptsSinceLastSuccess == 1 && lc19c.RecentErrorCount == 4
                  && lc19c.RecentWrongComponentCount == 1 && lc19c.RecentFailedAttemptCount == 4 && lc19c.TimeSinceLastSuccessMs == 10000f, $"{lc19c.ErrorsSinceLastSuccess}/{lc19c.FailedAttemptsSinceLastSuccess}/{lc19c.RecentErrorCount}/{lc19c.RecentFailedAttemptCount}");
            var lcEmpty = new AdaptiveAR.Decision.DecisionContext(); new AdaptiveAR.Decision.TaskEventLedger().Fill(lcEmpty, 1000f, 60000f, 12);
            Check("19d empty ledger: zeros, null time since success, empty history", lcEmpty.RecentErrorCount == 0 && lcEmpty.ConsecutiveSuccessfulActions == 0 && !lcEmpty.TimeSinceLastSuccessMs.HasValue && lcEmpty.RecentEvents.Count == 0);
            // 19e the features and the ordered history travel in ai_input and replay from a recorded line
            string evJson = AdaptiveAR.Decision.LlmDecisionCore.ContextToJson(lc19);
            var evReplay = AdaptiveAR.Decision.Evaluation.ContextJsonl.Read(new[] { "{\"event\":\"decision_context_probe\"," + evJson.Substring(1) });
            Check("19e temporal context in ai_input and replay", evJson.Contains("\"consecutive_successful_actions\":2") && evJson.Contains("\"recent_events\":[{\"type\":\"error\",\"age_ms\":45000,\"action_id\":\"a1\",\"level\":\"L1_Minimal\"}")
                  && evJson.Contains("\"time_since_last_success_ms\":5000") && evReplay.Count == 1 && evReplay[0].Context.RecentEvents.Count == 6 && evReplay[0].Context.RecentEvents[2].Kind == AdaptiveAR.Decision.TaskEventKind.SupportIncrease
                  && evReplay[0].Context.ConsecutiveSuccessfulActions == 2 && evReplay[0].Context.TimeSinceLastSuccessMs == 5000f && evReplay[0].Context.RecentWindowMs == 60000f, evJson);
            // 19f the session log writer produces the same array shape (parseable, ordered)
            var jw = new AdaptiveAR.Logging.JsonLineWriter(); jw.Str("a", "x"); jw.BeginArray("recent_events"); jw.BeginObject(); jw.Str("type", "error"); jw.Num("age_ms", 10f); jw.EndObject(); jw.BeginObject(); jw.Str("type", "success"); jw.Num("age_ms", 2f); jw.EndObject(); jw.EndArray(); jw.Bool("b", true);
            var jwo = AdaptiveAR.Decision.MiniJson.ParseObject(jw.Build()); var jwl = AdaptiveAR.Decision.MiniJson.GetList(jwo, "recent_events");
            Check("19f JsonLineWriter arrays", jwl != null && jwl.Count == 2 && AdaptiveAR.Decision.MiniJson.GetString((System.Collections.Generic.Dictionary<string, object>)jwl[1], "type", "") == "success" && AdaptiveAR.Decision.MiniJson.GetBool(jwo, "b", false), jw.Build());
            // 19g synthetic scenarios carry consistent temporal features; clone keeps them; "missing" drops the history
            var syn19 = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Synthetic(0, 1);
            var withErr = syn19.Find(x => x.Context.ErrorsOnStep == 3);
            var cl = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Clone(withErr.Context);
            var pertMissing = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Perturb(withErr, 1).Find(x => x.Kind == "missing");
            Check("19g synthetic temporal features", withErr.Context.RecentEvents.Count >= 3 && withErr.Context.ErrorsSinceLastSuccess + withErr.Context.ConsecutiveSuccessfulActions >= 0
                  && cl.RecentEvents.Count == withErr.Context.RecentEvents.Count && !ReferenceEquals(cl.RecentEvents, withErr.Context.RecentEvents) && pertMissing.Context.RecentEvents == null && !pertMissing.Context.TimeSinceLastSuccessMs.HasValue);
            Check("19h context config keys", AdaptiveAR.Decision.DecisionConfig.FromJson("{\"context\":{\"recentWindowMs\":30000,\"recentEventsMax\":5}}", "t").Context.RecentEventsMax == 5
                  && AdaptiveAR.Decision.DecisionConfig.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(Program.RepoRoot(), "Assets", "Resources", "Decision", "decision_config.json")), "file").Context.RecentWindowMs == 60000);

            // 20 request gate: evidence accrues between calls; call protection and change hysteresis are separate; no KEEP call per success
            var gcfg = new AdaptiveAR.Decision.DecisionScheduleConfig();   // 4 s calls, 15 s cooldown, 2 events, 8 s flush, backoff 2/4/8, first look at 10 s
            AdaptiveAR.Decision.RequestGate G() => new AdaptiveAR.Decision.RequestGate(gcfg);
            var L1 = AdaptiveAR.Support.SupportLevel.L1_Minimal; var L2 = AdaptiveAR.Support.SupportLevel.L2_Guided;
            // 20a a long clean run at L2: successes every 5 s for 100 s -> requests after 2, then 4 more, then 8 more; never one per success
            { var g = G(); int fired = 0; var at = new System.Collections.Generic.List<int>();
              for (int i = 1; i <= 20; i++) { double t = i * 5.0; g.NoteSuccess(t); var qr = g.Evaluate("action_complete:a" + i, t, 60000 + t * 1000, L2); if (qr.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire) { fired++; at.Add(i); g.OnApplied(false, t); } }
              Check("20a clean run at L2 -> 3 requests in 20 successes (after #2, #6, #14), backoff 2/4/8", fired == 3 && at[0] == 2 && at[1] == 6 && at[2] == 14 && g.SuccessOnlyRequired == 8, string.Join(",", at)); }
            // 20b the same run at L1: no decrease is structurally available -> no request at all
            { var g = G(); int fired = 0;
              for (int i = 1; i <= 20; i++) { double t = i * 5.0; g.NoteSuccess(t); if (g.Evaluate("action_complete:a" + i, t, 60000 + t * 1000, L1).Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire) fired++; }
              Check("20b clean run at L1 -> zero requests", fired == 0); }
            // 20c two errors a second apart on a young step: the first accrues, the second fires (error batches ignore the step minimum)
            { var g = G(); g.NoteError(1.0); var q1 = g.Evaluate("counted_error", 1.0, 1000, L1); g.NoteError(2.0); var q2 = g.Evaluate("counted_error", 2.0, 2000, L1);
              Check("20c two errors -> one request, promptly", q1.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Skipped && q2.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire && q2.PendingErrors == 2 && q2.Trigger == "counted_error+2ev(2err,0ok)", q1.Reason + " | " + q2.Trigger); }
            // 20d a lone error is not a call; it flushes after pendingFlushMs
            { var g = G(); g.NoteError(10.0); var q1 = g.Evaluate("counted_error", 10.0, 30000, L1); bool f5 = g.NeedsFlush(15.0), f9 = g.NeedsFlush(18.5); var q2 = g.Evaluate("pending_flush", 18.5, 38500, L1);
              Check("20d lone error flushes after 8 s", q1.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Skipped && !f5 && f9 && q2.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire && q2.Trigger == "pending_flush+1ev(1err,0ok)", q2.Trigger); }
            // 20e hysteresis: after an applied change, a new error pair is deferred (not dropped, not KEEP) and fires when the cooldown ends
            { var g = G(); g.NoteError(1); g.NoteError(2); var q0 = g.Evaluate("counted_error", 2, 2000, L1); g.OnApplied(true, 3);
              g.NoteError(5); g.NoteError(6); var q1 = g.Evaluate("counted_error", 6, 6000, L2); var q2 = g.Evaluate(g.Deferred, 17.9, 17900, L2); var q3 = g.Evaluate(g.Deferred, 18.1, 18100, L2);
              Check("20e change cooldown defers then fires", q0.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire && q1.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Deferred && q1.Reason.Contains("cooldown")
                    && q2.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Deferred && q3.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire && q3.DeferredFrom == "counted_error" && q3.PendingErrors == 2, q1.Reason + " | " + q3.Trigger); }
            // 20f call protection is separate from hysteresis: a KEEP result sets no cooldown; the next batch waits only for the 4 s call interval
            { var g = G(); g.NoteError(1); g.NoteError(2); g.Evaluate("counted_error", 2, 2000, L1); g.OnApplied(false, 2.5);
              g.NoteError(3); g.NoteError(3.5); var q1 = g.Evaluate("counted_error", 3.5, 3500, L1); var q2 = g.Evaluate(g.Deferred, 6.1, 6100, L1);
              Check("20f call protection 4 s, no cooldown after KEEP", q1.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Deferred && q1.Reason.Contains("call protection") && q2.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire, q1.Reason); }
            // 20g in flight: deferred until the answer arrives
            { var g = G(); g.NoteError(1); g.NoteError(2); g.Evaluate("counted_error", 2, 2000, L1); g.InFlight = true; g.NoteError(3); g.NoteError(4); var q1 = g.Evaluate("counted_error", 4, 4000, L1);
              g.OnApplied(false, 5); var q2 = g.Evaluate(g.Deferred, 6.5, 6500, L1);
              Check("20g in-flight defers; fires after the answer", q1.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Deferred && q2.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire && q2.PendingErrors == 2); }
            // 20h an error ends the clean-run backoff; an applied change ends it too
            { var g = G(); for (int i = 1; i <= 6; i++) { g.NoteSuccess(i * 5.0); if (g.Evaluate("action_complete", i * 5.0, 60000, L2).Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire) g.OnApplied(false, i * 5.0); }
              int before = g.SuccessOnlyRequired; g.NoteError(31); int after = g.SuccessOnlyRequired;
              var g2 = G(); g2.NoteSuccess(5); g2.NoteSuccess(10); g2.Evaluate("action_complete", 10, 60000, L2); g2.OnApplied(true, 10);
              Check("20h backoff reset by an error and by an applied change", before == 8 && after == 2 && g2.SuccessOnlyRequired == 2, $"{before}->{after}, {g2.SuccessOnlyRequired}"); }
            // 20i first look: an action entry with nothing pending asks once per step after firstDecisionAfterStepMs, and not again
            { var g = G(); var q1 = g.Evaluate("action_enter:a1", 1, 500, L1); var q2 = g.Evaluate(g.Deferred, 11, 10500, L1); var q3 = g.Evaluate("action_enter:a2", 40, 40000, L1); g.ResetForStep(); var r4 = g.Evaluate("action_enter:b1", 60, 12000, L1);
              Check("20i one first look per step", q1.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Deferred && q1.Reason.Contains("too young") && q2.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire
                    && q3.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Skipped && r4.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Fire, q1.Reason + " | " + q3.Reason); }
            // 20j successes pending at L1 do not make the next single error a call; the error batch counts error-like events only
            { var g = G(); g.NoteSuccess(5); g.NoteSuccess(10); g.NoteSuccess(15); g.NoteError(20); var qr = g.Evaluate("counted_error", 20, 20000, L1);
              Check("20j one error after successes still accrues", qr.Verdict == AdaptiveAR.Decision.RequestGate.Verdict.Skipped && g.PendingEvents == 4, qr.Reason); }
            // 20k the keys are config, the defaults are the documented ones
            { var sc = AdaptiveAR.Decision.DecisionConfig.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(Program.RepoRoot(), "Assets", "Resources", "Decision", "decision_config.json")), "file").Schedule;
              var so = AdaptiveAR.Decision.DecisionScheduleConfig.FromJson(AdaptiveAR.Decision.MiniJson.ParseObject("{\"minCallIntervalSeconds\":2,\"changeCooldownMs\":5000,\"minEventsPerRequest\":3,\"pendingFlushMs\":4000,\"successOnlyBackoff\":false,\"successOnlyBackoffMax\":4,\"firstDecisionAfterStepMs\":2000}"));
              Check("20k schedule config keys", sc.MinCallIntervalSeconds == 4 && sc.ChangeCooldownMs == 15000 && sc.MinEventsPerRequest == 2 && sc.PendingFlushMs == 8000 && sc.SuccessOnlyBackoff && sc.SuccessOnlyBackoffMax == 8 && sc.FirstDecisionAfterStepMs == 10000
                    && so.MinCallIntervalSeconds == 2 && so.ChangeCooldownMs == 5000 && so.MinEventsPerRequest == 3 && so.PendingFlushMs == 4000 && !so.SuccessOnlyBackoff && so.SuccessOnlyBackoffMax == 4 && so.FirstDecisionAfterStepMs == 2000 && sc.Describe().Contains("changeCooldownMs=15000"), sc.Describe()); }

            // 21 v2 contract: the model interprets; the application supplies evidence and structural constraints only
            Check("21a prompt: reasoning instruction, model owns the interpretation, temporal fields and events named",
                  sys.Contains("You are responsible for interpreting whether the operator is currently struggling, recovering or adequately supported")
                  && sys.Contains("consider the complete task history") && sys.Contains("Prioritise recent performance") && sys.Contains("lose weight when they are followed by sustained successful performance")
                  && sys.Contains("Recent repeated failures indicate unresolved difficulty") && sys.Contains("could eventually be reduced") && sys.Contains("together with the task events, never on its own")
                  && sys.Contains("recent_events") && sys.Contains("consecutive_successful_actions") && sys.Contains("time_since_last_success_ms") && sys.Contains("allowed_actions")
                  && sys.Contains("exactly one of these three tokens"), "");
            Check("21b allowed_actions are structural (boundaries only)",
                  string.Join(",", AdaptiveAR.Decision.DecisionConstraints.AllowedActions(AdaptiveAR.Support.SupportLevel.L1_Minimal)) == "KEEP_SUPPORT,INCREASE_SUPPORT"
                  && string.Join(",", AdaptiveAR.Decision.DecisionConstraints.AllowedActions(AdaptiveAR.Support.SupportLevel.L2_Guided)) == "KEEP_SUPPORT,INCREASE_SUPPORT,DECREASE_SUPPORT"
                  && string.Join(",", AdaptiveAR.Decision.DecisionConstraints.AllowedActions(AdaptiveAR.Support.SupportLevel.L3_Assisted)) == "KEEP_SUPPORT,DECREASE_SUPPORT");
            string aiL1 = AdaptiveAR.Decision.LlmDecisionCore.ContextToJson(RR(1, 20000, 6, 9, 6, 9, 0, 1500f, null, 0)); string aiL3 = AdaptiveAR.Decision.LlmDecisionCore.ContextToJson(RR(3, 20000, 0, 0, 0, 0, 2, null, 30000f, 2));
            Check("21c ai_input allowed_actions follow the level, never the task state", aiL1.Contains("\"allowed_actions\":[\"KEEP_SUPPORT\",\"INCREASE_SUPPORT\"]") && aiL3.Contains("\"allowed_actions\":[\"KEEP_SUPPORT\",\"DECREASE_SUPPORT\"]"), aiL1);
            var evCtx = RR(2, 85000, 6, 9, 0, 3, 2, 25000f, 30000f, 1); led.Fill(evCtx, 55000f, 60000f, 12);
            string ev2 = AdaptiveAR.Decision.DecisionEvidence.Summarise(evCtx);
            Check("21d evidence carries the temporal features and the ordered events", ev2.Contains("recent(60000ms): errors=4 failed_attempts=4 wrong=1 consecutive_successes=0 errors_since_success=1") && ev2.Contains("events: error@50000ms -> error@43000ms -> support_increase@42000ms -> error@35000ms -> success@25000ms -> success@10000ms -> wrong_component@3000ms -> failed_attempt@1000ms"), ev2);
            var jw2 = new AdaptiveAR.Logging.JsonLineWriter(); jw2.StrArray("allowed_actions", new[] { "KEEP_SUPPORT", "DECREASE_SUPPORT" }); jw2.StrArray("none", null); jw2.Str("applied_action", "KEEP_SUPPORT");
            Check("21e JsonLineWriter string arrays", jw2.Build() == "{\"allowed_actions\":[\"KEEP_SUPPORT\",\"DECREASE_SUPPORT\"],\"none\":null,\"applied_action\":\"KEEP_SUPPORT\"}", jw2.Build());
            Check("21f no deterministic operator classification in the decision assembly",
                  !System.IO.File.ReadAllText(System.IO.Path.Combine(Program.RepoRoot(), "Assets", "Scripts", "Decision", "DecisionEvidence.cs")).Contains("SinceChangeGoverns")
                  && !System.IO.File.ReadAllText(System.IO.Path.Combine(Program.RepoRoot(), "Assets", "Scripts", "Decision", "TaskEventLedger.cs")).ToLowerInvariant().Contains("struggling =")
                  && !System.IO.File.ReadAllText(System.IO.Path.Combine(Program.RepoRoot(), "Assets", "Scripts", "Decision", "RequestGate.cs")).Contains("ReferencePolicy"));

            // 22 current_support_episode: objective segmentation at the most recent applied support change
            var el = new AdaptiveAR.Decision.TaskEventLedger();
            el.Add(AdaptiveAR.Decision.TaskEventKind.Error, 5000f, "a1", "L1_Minimal");
            el.Add(AdaptiveAR.Decision.TaskEventKind.WrongComponent, 8000f, "a1", "L1_Minimal");
            el.Add(AdaptiveAR.Decision.TaskEventKind.FailedAttempt, 9000f, "a1", "L1_Minimal");
            el.Add(AdaptiveAR.Decision.TaskEventKind.Error, 12000f, "a1", "L1_Minimal");
            var ec0 = new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = AdaptiveAR.Support.SupportLevel.L1_Minimal }; el.Fill(ec0, 14000f, 60000f, 12);
            Check("22a no change on the step: the episode is the whole step, started by step_entry",
                  ec0.CurrentSupportEpisode.StartedBy == "step_entry" && ec0.CurrentSupportEpisode.Level == "L1_Minimal" && ec0.CurrentSupportEpisode.DurationMs == 14000f
                  && ec0.CurrentSupportEpisode.Errors == 3 && ec0.CurrentSupportEpisode.WrongComponents == 1 && ec0.CurrentSupportEpisode.FailedAttempts == 3 && ec0.CurrentSupportEpisode.Successes == 0
                  && ec0.CurrentSupportEpisode.Events.Count == 4 && ec0.CurrentSupportEpisode.TimeSinceLastErrorMs == 2000f && ec0.CurrentSupportEpisode.TimeSinceLastFailedAttemptMs == 2000f);
            el.Add(AdaptiveAR.Decision.TaskEventKind.SupportIncrease, 13000f, "a1", "L2_Guided");
            el.Add(AdaptiveAR.Decision.TaskEventKind.Success, 30000f, "a1", "L2_Guided");
            el.Add(AdaptiveAR.Decision.TaskEventKind.Success, 45000f, "a2", "L2_Guided");
            var ec1 = new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = AdaptiveAR.Support.SupportLevel.L2_Guided }; el.Fill(ec1, 50000f, 60000f, 12);
            var ep1 = ec1.CurrentSupportEpisode;
            Check("22b after an applied increase the episode resets: only the events after the change, pre-change errors excluded while the 60 s window still holds them",
                  ep1.StartedBy == "support_increase" && ep1.Level == "L2_Guided" && ep1.DurationMs == 37000f && ep1.Errors == 0 && ep1.FailedAttempts == 0 && ep1.WrongComponents == 0
                  && ep1.Successes == 2 && ep1.ConsecutiveSuccesses == 2 && !ep1.TimeSinceLastErrorMs.HasValue && !ep1.TimeSinceLastFailedAttemptMs.HasValue
                  && ep1.Events.Count == 2 && ep1.Events[0].Kind == AdaptiveAR.Decision.TaskEventKind.Success && ep1.Events[0].AgeMs == 20000f
                  && ec1.RecentErrorCount == 3 && ec1.RecentEvents.Count == 7, AdaptiveAR.Decision.TaskEventLedger.Describe(ep1.Events) + " recentErrors=" + ec1.RecentErrorCount);
            el.Add(AdaptiveAR.Decision.TaskEventKind.Error, 52000f, "a2", "L2_Guided");
            el.Add(AdaptiveAR.Decision.TaskEventKind.Success, 60000f, "a2", "L2_Guided");
            var ec2 = new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = AdaptiveAR.Support.SupportLevel.L2_Guided }; el.Fill(ec2, 61000f, 60000f, 12);
            Check("22c new difficulty under the current level is counted in the episode",
                  ec2.CurrentSupportEpisode.Errors == 1 && ec2.CurrentSupportEpisode.Successes == 3 && ec2.CurrentSupportEpisode.ConsecutiveSuccesses == 1
                  && ec2.CurrentSupportEpisode.TimeSinceLastErrorMs == 9000f && ec2.CurrentSupportEpisode.Events.Count == 4);
            el.Add(AdaptiveAR.Decision.TaskEventKind.SupportDecrease, 62000f, "a2", "L1_Minimal");
            var ec3 = new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = AdaptiveAR.Support.SupportLevel.L1_Minimal }; el.Fill(ec3, 65000f, 60000f, 12);
            Check("22d a second applied change (decrease) starts a fresh, empty episode; only the latest change counts",
                  ec3.CurrentSupportEpisode.StartedBy == "support_decrease" && ec3.CurrentSupportEpisode.Level == "L1_Minimal" && ec3.CurrentSupportEpisode.DurationMs == 3000f
                  && ec3.CurrentSupportEpisode.Errors == 0 && ec3.CurrentSupportEpisode.Successes == 0 && ec3.CurrentSupportEpisode.Events.Count == 0 && ec3.RecentEvents.Count == 10);
            var ec4 = new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = AdaptiveAR.Support.SupportLevel.L1_Minimal }; el.Fill(ec4, 65000f, 60000f, 2);
            el.Add(AdaptiveAR.Decision.TaskEventKind.Success, 66000f, "a3", "L1_Minimal"); el.Add(AdaptiveAR.Decision.TaskEventKind.Success, 67000f, "a3", "L1_Minimal"); el.Add(AdaptiveAR.Decision.TaskEventKind.Success, 68000f, "a3", "L1_Minimal");
            var ec5 = new AdaptiveAR.Decision.DecisionContext { CurrentSupportLevel = AdaptiveAR.Support.SupportLevel.L1_Minimal }; el.Fill(ec5, 69000f, 60000f, 2);
            Check("22e the episode event list is capped to the newest, counts are not", ec4.CurrentSupportEpisode.Events.Count == 0 && ec5.CurrentSupportEpisode.Events.Count == 2 && ec5.CurrentSupportEpisode.Successes == 3 && ec5.CurrentSupportEpisode.ConsecutiveSuccesses == 3);
            // 22f ai_input, log shape and replay carry the block
            string epJson = AdaptiveAR.Decision.LlmDecisionCore.ContextToJson(ec1);
            var epReplay = AdaptiveAR.Decision.Evaluation.ContextJsonl.Read(new[] { "{\"event\":\"decision_context_probe\"," + epJson.Substring(1) });
            var rep1 = epReplay[0].Context.CurrentSupportEpisode;
            Check("22f episode in ai_input and replay", epJson.Contains("\"current_support_episode\":{\"level\":\"L2_Guided\",\"started_by\":\"support_increase\",\"duration_ms\":37000,\"errors\":0,\"failed_attempts\":0,\"wrong_components\":0,\"successes\":2,\"consecutive_successes\":2,\"time_since_last_error_ms\":null,\"time_since_last_failed_attempt_ms\":null,\"events\":[{\"type\":\"success\"")
                  && rep1 != null && rep1.StartedBy == "support_increase" && rep1.Successes == 2 && rep1.ConsecutiveSuccesses == 2 && !rep1.TimeSinceLastErrorMs.HasValue && rep1.Events.Count == 2 && rep1.DurationMs == 37000f, epJson);
            var nullEp = AdaptiveAR.Decision.Evaluation.ContextJsonl.Read(new[] { "{\"event\":\"decision_context_probe\",\"context\":{\"current_support_level\":\"L2_Guided\",\"current_support_episode\":null}}" });
            Check("22f2 absent episode replays as null, never invented", nullEp.Count == 1 && nullEp[0].Context.CurrentSupportEpisode == null);
            // 22g synthetic scenarios: above L1 with errors the episode holds only the post-increase successes; clone is deep; "missing" drops it
            var synEp = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Synthetic(0, 1).Find(x => x.Context.CurrentSupportLevel == AdaptiveAR.Support.SupportLevel.L2_Guided && x.Context.ErrorsOnStep == 3 && x.Context.SuccessfulActionsSinceLastSupportChange == 2);
            var synClone = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Clone(synEp.Context);
            var synMissing = AdaptiveAR.Decision.Evaluation.ScenarioGenerator.Perturb(synEp, 1).Find(x => x.Kind == "missing");
            Check("22g synthetic episode", synEp.Context.CurrentSupportEpisode.StartedBy == "support_increase" && synEp.Context.CurrentSupportEpisode.Errors == 0 && synEp.Context.CurrentSupportEpisode.Successes == 2
                  && synEp.Context.RecentErrorCount == 3 && synEp.Context.SupportChangesOnStep == 1 && synEp.Context.ErrorsSinceLastSupportChange == 0
                  && synClone.CurrentSupportEpisode.Successes == 2 && !ReferenceEquals(synClone.CurrentSupportEpisode, synEp.Context.CurrentSupportEpisode) && synMissing.Context.CurrentSupportEpisode == null,
                  AdaptiveAR.Decision.DecisionEvidence.Summarise(synEp.Context));
            // 22h the prompt states the causal interpretation and the evidence line shows the episode
            Check("22h prompt: episode is the strongest evidence for the current level; pre-change errors are not its failures; minimum support; no automatic KEEP",
                  sys.Contains("current_support_episode") && sys.Contains("strongest evidence for whether the CURRENT level remains necessary") && sys.Contains("not failures of the current level")
                  && sys.Contains("accomplished its purpose") && sys.Contains("automatic reason to keep it indefinitely") && sys.Contains("minimum level of support currently needed")
                  && sys.Contains("increase again later if genuine new difficulty appears") && sys.Contains("New failures under the current level are evidence for retaining or increasing support")
                  && sys.Contains("never by itself justify a decrease") && !sys.Contains("RECOVERING") && !sys.Contains("STRUGGLING"), "");
            string evEp = AdaptiveAR.Decision.DecisionEvidence.Summarise(ec1);
            Check("22h2 evidence line carries the episode", evEp.Contains("episode(L2_Guided via support_increase for 37000ms): errors=0 failed_attempts=0 wrong=0 successes=2 consecutive_successes=2 last_error_ms=null last_failed_attempt_ms=null events: success@20000ms -> success@5000ms"), evEp);

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

        internal static string RepoRoot()
        {
            string d = AppContext.BaseDirectory;
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d, "CLAUDE.md"))) d = System.IO.Path.GetDirectoryName(d);
            return d ?? ".";
        }

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

            SimulationTests.Run(Check);

            TechnicalValidationTests.Run(Check);

            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }
    }
}
