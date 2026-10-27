// File: LlmDecisionProvider.cs
// The in-headset model provider: LlmDecisionCore over UnityWebRequest, run as a coroutine so
// a slow or failing network never stalls the frame. The API key is held in memory only.

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace AdaptiveAR.Decision
{
    public sealed class LlmDecisionProvider : IAsyncDecisionProvider
    {
        private readonly LlmConfig _cfg;
        private readonly string _apiKey;

        public LlmDecisionProvider(LlmConfig cfg, string apiKey)
        {
            _cfg = cfg ?? new LlmConfig();
            _apiKey = apiKey;
        }

        public string ProviderId { get { return _cfg.ProviderId; } }
        public LlmConfig Config { get { return _cfg; } }

        public IEnumerator RequestDecisionRoutine(DecisionContext context, Action<DecisionResponse> done)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            HttpRequestSpec spec;
            try { spec = LlmDecisionCore.BuildRequest(_cfg, _apiKey, context); }
            catch (Exception e)
            {
                done(DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, ProviderId, "build: " + e.Message));
                yield break;
            }

            using (var req = new UnityWebRequest(spec.Url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(spec.Body));
                req.downloadHandler = new DownloadHandlerBuffer();
                foreach (var kv in spec.Headers) req.SetRequestHeader(kv.Key, kv.Value);
                req.timeout = Mathf.Max(1, Mathf.CeilToInt(spec.TimeoutMs / 1000f));

                yield return req.SendWebRequest();

                sw.Stop();
                float latency = (float)sw.Elapsed.TotalMilliseconds;
                bool timedOut = req.result == UnityWebRequest.Result.ConnectionError && (req.error ?? "").IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
                string transportError = req.result == UnityWebRequest.Result.ConnectionError ? req.error : null;
                string body = req.downloadHandler != null ? req.downloadHandler.text : null;
                DecisionResponse r = LlmDecisionCore.Interpret(_cfg, req.responseCode, body, latency, timedOut, transportError);
                done(r);
            }
        }
    }
}
