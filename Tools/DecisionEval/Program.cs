// Headless technical evaluation runner. Arguments are key=value pairs.
//
// mode=simulate   (virtual-participant trajectories, real-session replay, metamorphic pairs)
//   provider=canned[:episode|keep|malformed|sequence:A,B] | reference | live-llm
//   scenarios=<dir or file>          (default Tools/DecisionEval/Scenarios)
//   scenario=S2,S8                   (optional id filter, prefix match)
//   physiology=all | null,nominal,elevated,stale   (default all)
//   reps=<n>                         repetitions per scenario × physiology (default 1)
//   replay=<session.jsonl>[;more]    real headset sessions to replay (default none)
//   replay_reps=<n>                  resubmissions per recorded context (default 5)
//   metamorphic=true|false           (default true)   metamorphic_reps=<n> (default 3)
//   latency_clock=true|false         advance the fake clock by measured latency (default false: latency is a metric, not simulated time)
//   price_in=<usd per M input tokens> price_out=<usd per M output tokens> (cost estimate assumptions)
//   config=<decision_config.json>    keyfile=<ai_provider.local.json> (live-llm; never printed)
//   out=<dir>                        (default eval_out/<utc timestamp>_<provider>)
//   verbose=true                     per-decision log lines
//
// mode=grid (the earlier context-grid evaluation; default when mode is absent)
//   provider=reference|llm  scenarios=synthetic|<session.jsonl>  count= seed= repeats= perturb= out=

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using AdaptiveAR.Decision;
using AdaptiveAR.Decision.Evaluation;
using AdaptiveAR.Decision.Simulation;

namespace AdaptiveAR.Decision.Eval
{
    /// <summary>Blocking HTTP transport for the grid harness; same core as the headset.</summary>
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
            string mode = Get(a, "mode", "grid");
            try
            {
                return mode == "simulate" ? Simulate(a) : Grid(a);
            }
            catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
            catch (FileNotFoundException e) { Console.Error.WriteLine(e.Message + (e.FileName != null ? ": " + e.FileName : "")); return 2; }
            catch (FormatException e) { Console.Error.WriteLine(e.Message); return 2; }
        }

        private static string LoadKey(Dictionary<string, string> a)
        {
            string keyFile = Get(a, "keyfile", null);
            string key = keyFile != null && File.Exists(keyFile) ? SecretStore.ExtractApiKey(File.ReadAllText(keyFile)) : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrEmpty(key))
            {
                string local = Path.Combine("Assets", "Resources", "Decision", "ai_provider.local.json");
                if (File.Exists(local)) key = SecretStore.ExtractApiKey(File.ReadAllText(local));
            }
            return key;
        }

        private static DecisionConfig LoadConfig(Dictionary<string, string> a)
        {
            string configPath = Get(a, "config", Path.Combine("Assets", "Resources", "Decision", "decision_config.json"));
            return File.Exists(configPath) ? DecisionConfig.FromJson(File.ReadAllText(configPath), "file:" + configPath) : new DecisionConfig();
        }

        // ------------------------------------------------------------------ mode=simulate

        private static int Simulate(Dictionary<string, string> a)
        {
            var o = new SimulationOptions
            {
                ProviderKind = Get(a, "provider", "reference"),
                Config = LoadConfig(a),
                Physiology = PhysiologyProfiles.ParseList(Get(a, "physiology", "all")),
                Repetitions = int.Parse(Get(a, "reps", "1")),
                AdvanceClockByLatency = bool.Parse(Get(a, "latency_clock", "false")),
                ReplayRepeats = int.Parse(Get(a, "replay_reps", "5")),
                Metamorphic = bool.Parse(Get(a, "metamorphic", "true")),
                MetamorphicRepeats = int.Parse(Get(a, "metamorphic_reps", "3")),
                Verbose = bool.Parse(Get(a, "verbose", "false"))
            };
            if (o.ProviderKind == "live-llm" || o.ProviderKind == "llm") o.ApiKey = LoadKey(a);
            CostModel.InputUsdPerMTok = double.Parse(Get(a, "price_in", "3.0"), System.Globalization.CultureInfo.InvariantCulture);
            CostModel.OutputUsdPerMTok = double.Parse(Get(a, "price_out", "15.0"), System.Globalization.CultureInfo.InvariantCulture);

            string scenarioPath = Get(a, "scenarios", Path.Combine("Tools", "DecisionEval", "Scenarios"));
            o.Scenarios = ScenarioScript.LoadAll(scenarioPath);
            string filter = Get(a, "scenario", null);
            if (!string.IsNullOrEmpty(filter))
            {
                string[] ids = filter.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                o.Scenarios = o.Scenarios.Where(s => ids.Any(id => s.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            if (o.Scenarios.Count == 0) { Console.Error.WriteLine("no scenarios matched in " + scenarioPath); return 2; }
            string replay = Get(a, "replay", null);
            if (!string.IsNullOrEmpty(replay)) o.ReplayFiles = replay.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            o.OutDir = Get(a, "out", Path.Combine("eval_out", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_sim_" + o.ProviderKind.Replace(':', '-')));

            Console.WriteLine($"mode=simulate provider={o.ProviderKind} scenarios={o.Scenarios.Count} [{string.Join(", ", o.Scenarios.Select(s => s.Id))}] physiology=[{string.Join(",", o.Physiology)}] reps={o.Repetitions} replay={o.ReplayFiles.Count} file(s) × {o.ReplayRepeats} metamorphic={o.Metamorphic}");
            Console.WriteLine("config: " + o.Config.Describe());
            SimulationResult r = SimulationRunner.Run(o, Console.WriteLine);
            Console.WriteLine();
            Console.WriteLine(File.ReadAllText(Path.Combine(o.OutDir, "evaluation_summary.md")));
            Console.WriteLine($"provider calls total: {r.ProviderCalls}");
            return 0;
        }

        // ------------------------------------------------------------------ mode=grid (unchanged behaviour)

        private static int Grid(Dictionary<string, string> a)
        {
            string providerKind = Get(a, "provider", "reference");
            string scenariosArg = Get(a, "scenarios", "synthetic");
            int count = int.Parse(Get(a, "count", "40"));
            int seed = int.Parse(Get(a, "seed", "7"));
            int repeats = int.Parse(Get(a, "repeats", "3"));
            bool perturb = bool.Parse(Get(a, "perturb", "true"));
            string outDir = Get(a, "out", Path.Combine("eval_out", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + providerKind));

            DecisionConfig cfg = LoadConfig(a);
            var reference = new ReferencePolicyProvider(cfg.ReferencePolicy);

            IDecisionProvider provider;
            if (providerKind == "reference") provider = reference;
            else if (providerKind == "llm" || providerKind == "live-llm")
            {
                string key = LoadKey(a);
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
