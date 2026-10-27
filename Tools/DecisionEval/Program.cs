// Headless technical evaluation runner. Arguments are key=value pairs:
//   provider=reference|llm        which provider to evaluate (default reference)
//   config=<decision_config.json> (default Assets/Resources/Decision/decision_config.json)
//   keyfile=<ai_provider.local.json>  API key for provider=llm (never printed)
//   scenarios=synthetic|<session.jsonl>  synthetic grid sample or replayed contexts (default synthetic)
//   count=<n>  seed=<n>  repeats=<n>  perturb=true|false  out=<dir>
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using AdaptiveAR.Decision;
using AdaptiveAR.Decision.Evaluation;

namespace AdaptiveAR.Decision.Eval
{
    /// <summary>Blocking HTTP transport for the headless harness; same core as the headset.</summary>
    public sealed class HttpLlmProvider : IDecisionProvider
    {
        private readonly LlmConfig _cfg; private readonly string _key; private readonly HttpClient _http = new HttpClient();
        public HttpLlmProvider(LlmConfig cfg, string key) { _cfg = cfg; _key = key; _http.Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, cfg.TimeoutMs + 2000)); }
        public string ProviderId { get { return _cfg.ProviderId; } }

        public DecisionResponse RequestDecision(DecisionContext context)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            HttpRequestSpec spec;
            try { spec = LlmDecisionCore.BuildRequest(_cfg, _key, context); }
            catch (Exception e) { return DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, ProviderId, "build: " + e.Message); }
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, spec.Url))
                {
                    req.Content = new StringContent(spec.Body, Encoding.UTF8, "application/json");
                    foreach (var kv in spec.Headers) if (kv.Key != "content-type") req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    using (var cts = new System.Threading.CancellationTokenSource(spec.TimeoutMs))
                    {
                        HttpResponseMessage resp = _http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        sw.Stop();
                        return LlmDecisionCore.Interpret(_cfg, (long)resp.StatusCode, body, (float)sw.Elapsed.TotalMilliseconds, false, null);
                    }
                }
            }
            catch (OperationCanceledException) { sw.Stop(); return LlmDecisionCore.Interpret(_cfg, 0, null, (float)sw.Elapsed.TotalMilliseconds, true, "timeout"); }
            catch (Exception e) { sw.Stop(); return LlmDecisionCore.Interpret(_cfg, 0, null, (float)sw.Elapsed.TotalMilliseconds, false, e.Message); }
        }
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            var a = new Dictionary<string, string>();
            foreach (string s in args) { int i = s.IndexOf('='); if (i > 0) a[s.Substring(0, i).ToLowerInvariant()] = s.Substring(i + 1); }
            string providerKind = Get(a, "provider", "reference");
            string configPath = Get(a, "config", Path.Combine("Assets", "Resources", "Decision", "decision_config.json"));
            string keyFile = Get(a, "keyfile", null);
            string scenariosArg = Get(a, "scenarios", "synthetic");
            int count = int.Parse(Get(a, "count", "40"));
            int seed = int.Parse(Get(a, "seed", "7"));
            int repeats = int.Parse(Get(a, "repeats", "3"));
            bool perturb = bool.Parse(Get(a, "perturb", "true"));
            string outDir = Get(a, "out", Path.Combine("eval_out", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + providerKind));

            DecisionConfig cfg = File.Exists(configPath) ? DecisionConfig.FromJson(File.ReadAllText(configPath), "file:" + configPath) : new DecisionConfig();
            var reference = new ReferencePolicyProvider(cfg.ReferencePolicy);

            IDecisionProvider provider;
            if (providerKind == "reference") provider = reference;
            else if (providerKind == "llm")
            {
                string key = keyFile != null && File.Exists(keyFile) ? SecretStore.ExtractApiKey(File.ReadAllText(keyFile)) : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("provider=llm needs keyfile=<ai_provider.local.json> or ANTHROPIC_API_KEY"); return 2; }
                provider = new HttpLlmProvider(cfg.Llm, key);
            }
            else { Console.Error.WriteLine("unknown provider " + providerKind); return 2; }

            List<Scenario> scenarios;
            if (scenariosArg == "synthetic") scenarios = ScenarioGenerator.Synthetic(count, seed);
            else
            {
                if (!File.Exists(scenariosArg)) { Console.Error.WriteLine("scenarios file not found: " + scenariosArg); return 2; }
                scenarios = ContextJsonl.Read(File.ReadLines(scenariosArg));
                if (scenarios.Count == 0) { Console.Error.WriteLine("no decision / decision_context_probe lines with a context block in " + scenariosArg + " (lines recorded before the probe existed cannot be replayed)"); return 2; }
            }
            if (perturb)
            {
                var all = new List<Scenario>(scenarios);
                foreach (Scenario s in scenarios) all.AddRange(ScenarioGenerator.Perturb(s, seed));
                scenarios = all;
            }
            Console.WriteLine($"provider={provider.ProviderId} reference={reference.ProviderId} scenarios={scenarios.Count} repeats={repeats} config={cfg.SourceDescription}");
            Console.WriteLine("reference policy: " + cfg.ReferencePolicy.Describe());

            EvaluationResult result = EvaluationRunner.Run(provider, reference, scenarios, repeats, Console.WriteLine);
            Directory.CreateDirectory(outDir);
            using (var w = new StreamWriter(Path.Combine(outDir, "results.jsonl"), false, new UTF8Encoding(false)))
                foreach (CallRecord c in result.Calls) w.WriteLine(EvaluationRunner.CallToJsonLine(c));
            File.WriteAllText(Path.Combine(outDir, "summary.json"), EvaluationRunner.SummaryToJson(result.Summary), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "summary.md"), EvaluationRunner.SummaryToMarkdown(result.Summary), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "run.json"), MiniJson.Serialize(new Dictionary<string, object>
            {
                { "provider", provider.ProviderId }, { "reference", reference.ProviderId }, { "config", cfg.Describe() }, { "scenarios_arg", scenariosArg },
                { "count", (double)count }, { "seed", (double)seed }, { "repeats", (double)repeats }, { "perturb", perturb }, { "utc", DateTime.UtcNow.ToString("o") }
            }), new UTF8Encoding(false));
            Console.WriteLine(EvaluationRunner.SummaryToMarkdown(result.Summary));
            Console.WriteLine("written: " + Path.GetFullPath(outDir));
            return 0;
        }

        private static string Get(Dictionary<string, string> a, string k, string d) { return a.TryGetValue(k, out string v) ? v : d; }
    }
}
