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
        public double MinIntervalSeconds = 20;
        /// <summary>No request before this much time on a step (the context is empty before it).</summary>
        public double FirstDecisionAfterStepMs = 15000;

        public static DecisionScheduleConfig FromJson(Dictionary<string, object> o)
        {
            var c = new DecisionScheduleConfig();
            if (o == null) return c;
            c.OnActionEnter = MiniJson.GetBool(o, "onActionEnter", c.OnActionEnter);
            c.OnCountedError = MiniJson.GetBool(o, "onCountedError", c.OnCountedError);
            c.OnWrongComponent = MiniJson.GetBool(o, "onWrongComponent", c.OnWrongComponent);
            c.OnActionComplete = MiniJson.GetBool(o, "onActionComplete", c.OnActionComplete);
            c.PeriodicSeconds = MiniJson.GetDouble(o, "periodicSeconds", c.PeriodicSeconds);
            c.MinIntervalSeconds = MiniJson.GetDouble(o, "minIntervalSeconds", c.MinIntervalSeconds);
            c.FirstDecisionAfterStepMs = MiniJson.GetDouble(o, "firstDecisionAfterStepMs", c.FirstDecisionAfterStepMs);
            return c;
        }

        public string Describe()
        {
            return $"onActionEnter={OnActionEnter};onCountedError={OnCountedError};onWrongComponent={OnWrongComponent};onActionComplete={OnActionComplete};periodicSeconds={PeriodicSeconds};minIntervalSeconds={MinIntervalSeconds};firstDecisionAfterStepMs={FirstDecisionAfterStepMs}";
        }
    }

    public sealed class DecisionConfig
    {
        public bool Enabled = true;
        /// <summary>"llm" | "reference" | "none"</summary>
        public string Provider = "llm";
        public LlmConfig Llm = new LlmConfig();
        public DecisionScheduleConfig Schedule = new DecisionScheduleConfig();
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
            c.ReferencePolicy = ReferencePolicyConfig.FromJson(MiniJson.GetObject(o, "referencePolicy"));
            return c;
        }

        public string Describe()
        {
            return $"source={SourceDescription};enabled={Enabled};provider={Provider};llm={Llm.ProviderId},timeoutMs={Llm.TimeoutMs},maxTokens={Llm.MaxTokens};schedule[{Schedule.Describe()}];reference[{ReferencePolicy.Describe()}]";
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
