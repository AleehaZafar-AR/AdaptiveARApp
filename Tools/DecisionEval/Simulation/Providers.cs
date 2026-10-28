// File: Providers.cs
// The three provider modes of the simulation harness.
//   live-llm  : the Anthropic model the app uses, through LlmDecisionCore.BuildRequest /
//               Interpret (the same request body and the same strict parser as the headset);
//               blocking HTTP, token usage read from the response for the cost estimate.
//   reference : ReferencePolicyProvider from the production code. A deterministic COMPARATOR
//               for the evaluation; never ground truth, never the runtime authority.
//   canned    : deterministic scripted responses for exercising the harness itself
//               (scheduler timing, episode reset, recording). Not a policy.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision.Simulation
{
    /// <summary>
    /// Harness-only deterministic responder. Modes:
    ///   keep                         - always KEEP_SUPPORT
    ///   sequence:A,B,C               - cycles through the listed actions
    ///   episode                      - INCREASE when the current support episode holds >= 2 errors,
    ///                                  DECREASE when it holds >= 2 consecutive successes, else KEEP
    ///                                  (harness-only; exercises both directions at the looks the gate offers)
    ///   malformed                    - returns prose (exercises the fallback path)
    /// </summary>
    public sealed class CannedProvider : IDecisionProvider
    {
        private readonly string _mode;
        private readonly List<SupportAction> _sequence = new List<SupportAction>();
        private int _n;
        public string ProviderId { get { return "canned:" + _mode; } }

        public CannedProvider(string spec)
        {
            spec = string.IsNullOrEmpty(spec) ? "episode" : spec;
            _mode = spec;
            if (spec.StartsWith("sequence:", StringComparison.Ordinal))
            {
                foreach (string s in spec.Substring("sequence:".Length).Split(','))
                    if (SupportLevels.TryParseAction(s.Trim(), out SupportAction a)) _sequence.Add(a);
                if (_sequence.Count == 0) throw new ArgumentException("canned sequence needs at least one action");
            }
            else if (spec != "keep" && spec != "episode" && spec != "malformed") throw new ArgumentException("unknown canned mode '" + spec + "'");
        }

        public DecisionResponse RequestDecision(DecisionContext ctx)
        {
            _n++;
            string text;
            if (_mode == "keep") text = "KEEP_SUPPORT";
            else if (_mode == "malformed") text = "I think the operator needs more help";
            else if (_sequence.Count > 0) text = _sequence[(_n - 1) % _sequence.Count].ToString();
            else
            {
                SupportEpisode ep = ctx != null ? ctx.CurrentSupportEpisode : null;
                if (ep != null && ep.Errors >= 2) text = "INCREASE_SUPPORT";
                else if (ep != null && ep.ConsecutiveSuccesses >= 2) text = "DECREASE_SUPPORT";
                else text = "KEEP_SUPPORT";
            }
            DecisionResponse r = LlmDecisionCore.ParseActionText(text, ProviderId, 1f);
            return r;
        }
    }

    /// <summary>Blocking HTTP transport for the live model; same core as the headset. Reports token usage.</summary>
    public sealed class LiveLlmProvider : IDecisionProvider, IUsageReporting
    {
        private readonly LlmConfig _cfg; private readonly string _key; private readonly HttpClient _http = new HttpClient();
        private int? _lastIn, _lastOut;
        public int Calls { get; private set; }
        public int TotalInputTokens { get; private set; }
        public int TotalOutputTokens { get; private set; }

        public LiveLlmProvider(LlmConfig cfg, string key)
        {
            _cfg = cfg ?? new LlmConfig(); _key = key;
            _http.Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _cfg.TimeoutMs + 2000));
        }
        public string ProviderId { get { return _cfg.ProviderId; } }

        public bool TryGetLastUsage(out int inputTokens, out int outputTokens)
        {
            inputTokens = _lastIn ?? 0; outputTokens = _lastOut ?? 0;
            return _lastIn.HasValue && _lastOut.HasValue;
        }

        public DecisionResponse RequestDecision(DecisionContext context)
        {
            _lastIn = _lastOut = null;
            Calls++;
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
                        ReadUsage(body);
                        return LlmDecisionCore.Interpret(_cfg, (long)resp.StatusCode, body, (float)sw.Elapsed.TotalMilliseconds, false, null);
                    }
                }
            }
            catch (OperationCanceledException) { sw.Stop(); return LlmDecisionCore.Interpret(_cfg, 0, null, (float)sw.Elapsed.TotalMilliseconds, true, "timeout"); }
            catch (Exception e) { sw.Stop(); return LlmDecisionCore.Interpret(_cfg, 0, null, (float)sw.Elapsed.TotalMilliseconds, false, e.Message); }
        }

        private void ReadUsage(string body)
        {
            try
            {
                Dictionary<string, object> o = MiniJson.ParseObject(body);
                Dictionary<string, object> u = o != null ? MiniJson.GetObject(o, "usage") : null;
                if (u == null) return;
                _lastIn = MiniJson.GetInt(u, "input_tokens", 0); _lastOut = MiniJson.GetInt(u, "output_tokens", 0);
                TotalInputTokens += _lastIn.Value; TotalOutputTokens += _lastOut.Value;
            }
            catch (Exception) { }
        }
    }

    public static class ProviderFactory
    {
        /// <summary>provider = live-llm | reference | canned[:mode]. The key is never printed.</summary>
        public static IDecisionProvider Create(string kind, DecisionConfig cfg, string apiKey)
        {
            if (kind == null) kind = "reference";
            if (kind == "reference") return new ReferencePolicyProvider(cfg.ReferencePolicy);
            if (kind == "live-llm" || kind == "llm")
            {
                if (string.IsNullOrEmpty(apiKey)) throw new ArgumentException("provider=live-llm needs keyfile=<ai_provider.local.json> or ANTHROPIC_API_KEY");
                return new LiveLlmProvider(cfg.Llm, apiKey);
            }
            if (kind == "canned" || kind.StartsWith("canned:", StringComparison.Ordinal))
                return new CannedProvider(kind.Length > 7 ? kind.Substring(7) : "episode");
            throw new ArgumentException("unknown provider '" + kind + "' (live-llm | reference | canned[:keep|episode|malformed|sequence:A,B])");
        }
    }
}
