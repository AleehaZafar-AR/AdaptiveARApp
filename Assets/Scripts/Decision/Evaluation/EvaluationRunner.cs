// File: EvaluationRunner.cs
// The technical evaluation of the decision framework (CLAUDE.md section 3): runs a provider
// over scenarios and computes
//   consistency   - same context, repeated: share of repeats equal to the modal action
//   agreement     - share of decisions equal to the reference policy's decision
//   robustness    - share of perturbed contexts whose decision equals the base decision,
//                   per perturbation kind; and compliance on adversarial inputs
//   compliance    - share of responses inside the constrained action set (status Ok)
//   latency       - mean / median / p95 / max of provider latency
//   failures      - every non-Ok response with its raw output
// Plain C#; providers are called synchronously (the harness wraps the model in a blocking
// HTTP client). Results are JSONL + a JSON summary + a Markdown summary.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AdaptiveAR.Logging;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision.Evaluation
{
    public sealed class CallRecord
    {
        public string ScenarioId, Kind, BaseId, ProviderId;
        public int Repeat;
        public SupportAction Action;
        public ConstraintStatus Status;
        public bool UsedFallback;
        public float LatencyMs;
        public string RawOutput, Reason;
        public SupportAction ReferenceAction;
        public string ReferenceReason;
        public DecisionContext Context;
    }

    public sealed class EvaluationSummary
    {
        public string ProviderId, ReferenceId;
        public int Scenarios, BaseScenarios, Repeats, Calls;
        public double Consistency;                       // mean over scenarios with repeats > 1
        public double AgreementWithReference;            // over all calls with status Ok
        public double AgreementBaseOnly;                 // base scenarios only
        public double Compliance;                        // share Ok
        public Dictionary<string, int> StatusCounts = new Dictionary<string, int>();
        public Dictionary<string, double> RobustnessByKind = new Dictionary<string, double>();   // kind -> share equal to base decision
        public double AdversarialCompliance;
        public double LatencyMeanMs, LatencyMedianMs, LatencyP95Ms, LatencyMaxMs;
        public Dictionary<string, int> ActionCounts = new Dictionary<string, int>();
        public List<CallRecord> Failures = new List<CallRecord>();
    }

    public sealed class EvaluationResult
    {
        public List<CallRecord> Calls = new List<CallRecord>();
        public EvaluationSummary Summary;
    }

    public static class EvaluationRunner
    {
        public static EvaluationResult Run(IDecisionProvider provider, IDecisionProvider reference, List<Scenario> scenarios, int repeats, Action<string> log)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (repeats < 1) repeats = 1;
            var result = new EvaluationResult();
            int done = 0;
            foreach (Scenario s in scenarios)
            {
                DecisionResponse refResp = reference != null ? reference.RequestDecision(ScenarioGenerator.Clone(s.Context)) : null;
                for (int r = 0; r < repeats; r++)
                {
                    DecisionResponse resp;
                    try { resp = provider.RequestDecision(ScenarioGenerator.Clone(s.Context)) ?? DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, provider.ProviderId, null); }
                    catch (Exception e) { resp = DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, provider.ProviderId, "exception: " + e.Message); }
                    result.Calls.Add(new CallRecord
                    {
                        ScenarioId = s.Id, Kind = s.Kind, BaseId = s.BaseId, ProviderId = resp.ProviderId ?? provider.ProviderId, Repeat = r,
                        Action = resp.ParsedAction, Status = resp.Status, UsedFallback = resp.UsedFallback, LatencyMs = resp.LatencyMs,
                        RawOutput = resp.RawOutput, Reason = resp.ProviderReason,
                        ReferenceAction = refResp != null ? refResp.ParsedAction : SupportAction.KEEP_SUPPORT, ReferenceReason = refResp != null ? refResp.ProviderReason : null,
                        Context = s.Context
                    });
                    done++;
                    if (log != null && done % 10 == 0) log($"{done}/{scenarios.Count * repeats} calls");
                }
            }
            result.Summary = Summarize(result.Calls, provider.ProviderId, reference != null ? reference.ProviderId : null, repeats);
            return result;
        }

        public static EvaluationSummary Summarize(List<CallRecord> calls, string providerId, string referenceId, int repeats)
        {
            var s = new EvaluationSummary { ProviderId = providerId, ReferenceId = referenceId, Repeats = repeats, Calls = calls.Count };
            var byScenario = calls.GroupBy(c => c.ScenarioId).ToList();
            s.Scenarios = byScenario.Count;
            s.BaseScenarios = byScenario.Count(g => g.First().Kind == "base" || g.First().Kind == "replay");

            // consistency
            var cons = new List<double>();
            foreach (var g in byScenario)
            {
                if (g.Count() < 2) continue;
                int modal = g.GroupBy(c => c.Action).Max(x => x.Count());
                cons.Add((double)modal / g.Count());
            }
            s.Consistency = cons.Count > 0 ? cons.Average() : 1.0;

            // compliance + status counts + actions
            foreach (var c in calls)
            {
                string st = c.Status.ToString(); s.StatusCounts[st] = s.StatusCounts.TryGetValue(st, out int v) ? v + 1 : 1;
                string a = c.Action.ToString(); s.ActionCounts[a] = s.ActionCounts.TryGetValue(a, out int w) ? w + 1 : 1;
                if (c.Status != ConstraintStatus.Ok) s.Failures.Add(c);
            }
            s.Compliance = calls.Count > 0 ? (double)calls.Count(c => c.Status == ConstraintStatus.Ok) / calls.Count : 0;

            // agreement with the reference
            if (referenceId != null)
            {
                var ok = calls.Where(c => c.Status == ConstraintStatus.Ok).ToList();
                s.AgreementWithReference = ok.Count > 0 ? (double)ok.Count(c => c.Action == c.ReferenceAction) / ok.Count : 0;
                var okBase = ok.Where(c => c.Kind == "base" || c.Kind == "replay").ToList();
                s.AgreementBaseOnly = okBase.Count > 0 ? (double)okBase.Count(c => c.Action == c.ReferenceAction) / okBase.Count : 0;
            }

            // robustness: perturbed decision equals the base scenario's modal decision
            var baseModal = new Dictionary<string, SupportAction>();
            foreach (var g in byScenario)
            {
                var first = g.First();
                if (first.Kind != "base" && first.Kind != "replay") continue;
                baseModal[first.ScenarioId] = g.GroupBy(c => c.Action).OrderByDescending(x => x.Count()).ThenBy(x => (int)x.Key).First().Key;
            }
            foreach (var kindGroup in calls.Where(c => c.Kind != "base" && c.Kind != "replay").GroupBy(c => c.Kind))
            {
                var comparable = kindGroup.Where(c => c.BaseId != null && baseModal.ContainsKey(c.BaseId)).ToList();
                s.RobustnessByKind[kindGroup.Key] = comparable.Count > 0 ? (double)comparable.Count(c => c.Action == baseModal[c.BaseId]) / comparable.Count : double.NaN;
            }
            var adv = calls.Where(c => c.Kind == "adversarial").ToList();
            s.AdversarialCompliance = adv.Count > 0 ? (double)adv.Count(c => c.Status == ConstraintStatus.Ok) / adv.Count : double.NaN;

            // latency
            var lat = calls.Select(c => (double)c.LatencyMs).OrderBy(x => x).ToList();
            if (lat.Count > 0)
            {
                s.LatencyMeanMs = lat.Average();
                s.LatencyMedianMs = lat[lat.Count / 2];
                s.LatencyP95Ms = lat[Math.Min(lat.Count - 1, (int)Math.Ceiling(lat.Count * 0.95) - 1)];
                s.LatencyMaxMs = lat[lat.Count - 1];
            }
            return s;
        }

        // ---------------------------------------------------------------- output

        public static string CallToJsonLine(CallRecord c)
        {
            var w = new JsonLineWriter();
            w.Str("scenario_id", c.ScenarioId); w.Str("kind", c.Kind); w.Str("base_id", c.BaseId); w.Str("provider_id", c.ProviderId); w.Num("repeat", c.Repeat);
            w.Str("action", c.Action.ToString()); w.Str("status", c.Status.ToString()); w.Bool("constraint_ok", c.Status == ConstraintStatus.Ok); w.Bool("used_fallback", c.UsedFallback);
            w.Num("latency_ms", c.LatencyMs); w.Str("raw_output", c.RawOutput); w.Str("reason", c.Reason);
            w.Str("reference_action", c.ReferenceAction.ToString()); w.Str("reference_reason", c.ReferenceReason);
            w.Str("ai_input", LlmDecisionCore.ContextToJson(c.Context));
            return w.Build();
        }

        public static string SummaryToJson(EvaluationSummary s)
        {
            var o = new Dictionary<string, object>
            {
                { "provider_id", s.ProviderId }, { "reference_id", s.ReferenceId }, { "scenarios", (double)s.Scenarios }, { "base_scenarios", (double)s.BaseScenarios },
                { "repeats", (double)s.Repeats }, { "calls", (double)s.Calls }, { "consistency", s.Consistency }, { "agreement_with_reference", s.AgreementWithReference },
                { "agreement_base_only", s.AgreementBaseOnly }, { "compliance", s.Compliance }, { "adversarial_compliance", s.AdversarialCompliance },
                { "latency_mean_ms", s.LatencyMeanMs }, { "latency_median_ms", s.LatencyMedianMs }, { "latency_p95_ms", s.LatencyP95Ms }, { "latency_max_ms", s.LatencyMaxMs },
                { "status_counts", s.StatusCounts.ToDictionary(k => k.Key, k => (object)(double)k.Value) },
                { "action_counts", s.ActionCounts.ToDictionary(k => k.Key, k => (object)(double)k.Value) },
                { "robustness_by_kind", s.RobustnessByKind.ToDictionary(k => k.Key, k => (object)k.Value) },
                { "failures", (double)s.Failures.Count }
            };
            return MiniJson.Serialize(o);
        }

        public static string SummaryToMarkdown(EvaluationSummary s)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("# Decision framework technical evaluation");
            sb.AppendLine();
            sb.AppendLine($"Provider: `{s.ProviderId}`   Reference: `{s.ReferenceId ?? "-"}`   Scenarios: {s.Scenarios} ({s.BaseScenarios} base)   Repeats: {s.Repeats}   Calls: {s.Calls}");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Decision consistency (same context, repeated) | {s.Consistency.ToString("P1", ci)} |");
            sb.AppendLine($"| Agreement with reference policy (all Ok calls) | {s.AgreementWithReference.ToString("P1", ci)} |");
            sb.AppendLine($"| Agreement with reference policy (base scenarios) | {s.AgreementBaseOnly.ToString("P1", ci)} |");
            sb.AppendLine($"| Constraint compliance (status Ok) | {s.Compliance.ToString("P1", ci)} |");
            sb.AppendLine($"| Adversarial-input compliance | {(double.IsNaN(s.AdversarialCompliance) ? "n/a" : s.AdversarialCompliance.ToString("P1", ci))} |");
            foreach (var kv in s.RobustnessByKind) sb.AppendLine($"| Robustness: decision unchanged under `{kv.Key}` | {(double.IsNaN(kv.Value) ? "n/a" : kv.Value.ToString("P1", ci))} |");
            sb.AppendLine($"| Latency mean / median / p95 / max (ms) | {s.LatencyMeanMs.ToString("F0", ci)} / {s.LatencyMedianMs.ToString("F0", ci)} / {s.LatencyP95Ms.ToString("F0", ci)} / {s.LatencyMaxMs.ToString("F0", ci)} |");
            sb.AppendLine();
            sb.AppendLine("Status counts: " + string.Join(", ", s.StatusCounts.Select(k => k.Key + "=" + k.Value)));
            sb.AppendLine();
            sb.AppendLine("Action counts: " + string.Join(", ", s.ActionCounts.Select(k => k.Key + "=" + k.Value)));
            sb.AppendLine();
            sb.AppendLine($"## Failures ({s.Failures.Count})");
            sb.AppendLine();
            if (s.Failures.Count == 0) sb.AppendLine("None.");
            else
            {
                sb.AppendLine("| Scenario | Kind | Status | Raw output |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var f in s.Failures.Take(200))
                    sb.AppendLine($"| {f.ScenarioId} | {f.Kind} | {f.Status} | {(f.RawOutput ?? "").Replace("|", "\\|").Replace("\n", " ").Substring(0, Math.Min(120, (f.RawOutput ?? "").Length))} |");
            }
            return sb.ToString();
        }
    }
}
