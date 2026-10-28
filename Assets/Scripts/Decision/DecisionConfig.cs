// File: DecisionConfig.cs
// The decision layer's configuration (JSON) and the secret store. Nothing secret lives in
// this repository: the API key is read from an untracked local file (see SecretStore).

using System;
using System.Collections.Generic;

namespace AdaptiveAR.Decision
{
    public sealed class DecisionScheduleConfig
    {
        public bool OnActionEnter = true;
        public bool OnCountedError = true;
        /// <summary>A part that cannot satisfy the current action was picked up (behavioural error). Same throttling as every other trigger.</summary>
        public bool OnWrongComponent = true;
        /// <summary>A validated action was completed (positive evidence, lets recovery be seen). Same throttling as every other trigger.</summary>
        public bool OnActionComplete = true;
        /// <summary>0 = no periodic requests.</summary>
        public double PeriodicSeconds = 0;
        /// <summary>Provider-call protection: least time between two requests, seconds. Not a hysteresis.</summary>
        public double MinCallIntervalSeconds = 4;
        /// <summary>Support-change hysteresis: after an APPLIED level change, requests are deferred this long, ms.</summary>
        public double ChangeCooldownMs = 15000;
        /// <summary>Error-like events (counted error, wrong component) that must accrue before a request; a lone one is flushed after pendingFlushMs.</summary>
        public int MinEventsPerRequest = 2;
        public double PendingFlushMs = 8000;
        /// <summary>Clean-run backoff: after a success-only request that changed nothing, the successes required double (up to successOnlyBackoffMax).</summary>
        public bool SuccessOnlyBackoff = true;
        public int SuccessOnlyBackoffMax = 8;
        /// <summary>No success-only or first-look request before this much time on a step (error batches ignore it).</summary>
        public double FirstDecisionAfterStepMs = 10000;

        public static DecisionScheduleConfig FromJson(Dictionary<string, object> o)
        {
            var c = new DecisionScheduleConfig();
            if (o == null) return c;
            c.OnActionEnter = MiniJson.GetBool(o, "onActionEnter", c.OnActionEnter);
            c.OnCountedError = MiniJson.GetBool(o, "onCountedError", c.OnCountedError);
            c.OnWrongComponent = MiniJson.GetBool(o, "onWrongComponent", c.OnWrongComponent);
            c.OnActionComplete = MiniJson.GetBool(o, "onActionComplete", c.OnActionComplete);
            c.PeriodicSeconds = MiniJson.GetDouble(o, "periodicSeconds", c.PeriodicSeconds);
            c.MinCallIntervalSeconds = MiniJson.GetDouble(o, "minCallIntervalSeconds", c.MinCallIntervalSeconds);
            c.ChangeCooldownMs = MiniJson.GetDouble(o, "changeCooldownMs", c.ChangeCooldownMs);
            c.MinEventsPerRequest = MiniJson.GetInt(o, "minEventsPerRequest", c.MinEventsPerRequest);
            c.PendingFlushMs = MiniJson.GetDouble(o, "pendingFlushMs", c.PendingFlushMs);
            c.SuccessOnlyBackoff = MiniJson.GetBool(o, "successOnlyBackoff", c.SuccessOnlyBackoff);
            c.SuccessOnlyBackoffMax = MiniJson.GetInt(o, "successOnlyBackoffMax", c.SuccessOnlyBackoffMax);
            c.FirstDecisionAfterStepMs = MiniJson.GetDouble(o, "firstDecisionAfterStepMs", c.FirstDecisionAfterStepMs);
            // The pre-v2 single gate ("minIntervalSeconds") is deliberately not read: a stale
            // headset override must not reinstate the 20 s request interval.
            return c;
        }

        public string Describe()
        {
            return $"onActionEnter={OnActionEnter};onCountedError={OnCountedError};onWrongComponent={OnWrongComponent};onActionComplete={OnActionComplete};periodicSeconds={PeriodicSeconds};" +
                   $"minCallIntervalSeconds={MinCallIntervalSeconds};changeCooldownMs={ChangeCooldownMs};minEventsPerRequest={MinEventsPerRequest};pendingFlushMs={PendingFlushMs};" +
                   $"successOnlyBackoff={SuccessOnlyBackoff};successOnlyBackoffMax={SuccessOnlyBackoffMax};firstDecisionAfterStepMs={FirstDecisionAfterStepMs}";
        }
    }

    /// <summary>How the temporal context is built: the window behind the recent_* counts and the length of the event history.</summary>
    public sealed class DecisionContextConfig
    {
        public double RecentWindowMs = 60000;
        public int RecentEventsMax = 12;

        public static DecisionContextConfig FromJson(Dictionary<string, object> o)
        {
            var c = new DecisionContextConfig();
            if (o == null) return c;
            c.RecentWindowMs = MiniJson.GetDouble(o, "recentWindowMs", c.RecentWindowMs);
            c.RecentEventsMax = MiniJson.GetInt(o, "recentEventsMax", c.RecentEventsMax);
            return c;
        }

        public string Describe() { return $"recentWindowMs={RecentWindowMs};recentEventsMax={RecentEventsMax}"; }
    }

    public sealed class DecisionConfig
    {
        public bool Enabled = true;
        /// <summary>"llm" | "reference" | "none"</summary>
        public string Provider = "llm";
        public LlmConfig Llm = new LlmConfig();
        public DecisionScheduleConfig Schedule = new DecisionScheduleConfig();
        public DecisionContextConfig Context = new DecisionContextConfig();
        public ReferencePolicyConfig ReferencePolicy = new ReferencePolicyConfig();
        public string SourceDescription = "defaults";

        public static DecisionConfig FromJson(string json, string sourceDescription)
        {
            var c = new DecisionConfig { SourceDescription = sourceDescription };
            Dictionary<string, object> o = MiniJson.ParseObject(json);
            if (o == null) throw new FormatException("decision config is not a JSON object");
            c.Enabled = MiniJson.GetBool(o, "enabled", c.Enabled);
            c.Provider = (MiniJson.GetString(o, "provider", c.Provider) ?? "none").Trim().ToLowerInvariant();
            c.Llm = LlmConfig.FromJson(MiniJson.GetObject(o, "llm"));
            c.Schedule = DecisionScheduleConfig.FromJson(MiniJson.GetObject(o, "schedule"));
            c.Context = DecisionContextConfig.FromJson(MiniJson.GetObject(o, "context"));
            c.ReferencePolicy = ReferencePolicyConfig.FromJson(MiniJson.GetObject(o, "referencePolicy"));
            return c;
        }

        public string Describe()
        {
            return $"source={SourceDescription};enabled={Enabled};provider={Provider};llm={Llm.ProviderId},timeoutMs={Llm.TimeoutMs},maxTokens={Llm.MaxTokens};schedule[{Schedule.Describe()}];context[{Context.Describe()}];reference[{ReferencePolicy.Describe()}]";
        }
    }

    /// <summary>Reads {"apiKey":"..."} from an untracked local file. The key is never logged.</summary>
    public static class SecretStore
    {
        public static string ExtractApiKey(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                Dictionary<string, object> o = MiniJson.ParseObject(json);
                string k = MiniJson.GetString(o, "apiKey", null);
                return string.IsNullOrEmpty(k) ? null : k.Trim();
            }
            catch (Exception) { return null; }
        }

        /// <summary>A loggable fingerprint that cannot reconstruct the key.</summary>
        public static string Fingerprint(string key)
        {
            if (string.IsNullOrEmpty(key)) return "none";
            return "len=" + key.Length + ",prefix=" + key.Substring(0, Math.Min(7, key.Length)) + "…";
        }
    }
}
