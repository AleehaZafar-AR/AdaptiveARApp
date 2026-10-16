// File: GuidanceAudit.cs
// Checks every authored instruction against the guidance that is ACTUALLY shown for
// that action at that support level, so a participant is never told to "align with
// the ghost" when no ghost exists.
//
// Runs at task start (session self-check) and from the Editor menu. One row per
// (stage, action, level):
//   [GuidanceAudit] stage= action= level= type= text="…" detail="…" ghost= arrow= preview= parts=[…] targets=[…]
// and one error per contradiction:
//   [GuidanceAudit] CONTRADICTION stage/action@level: text mentions "ghost" but ghost=false
//
// What is "shown" is derived from the same rules the presenter, the arrow and the
// card use (not from intent): ghosts only on PHYSICAL actions and only when the level
// block authors ghost keys; the arrow only on PHYSICAL actions with showArrow at or
// above the arrow's minimum level; the ring only for the head-on-mat action; the mat
// for head/rod/pin actions; the preview whenever a part is named.

using System.Collections.Generic;
using System.Text.RegularExpressions;
using AdaptiveAR.Support;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public static class GuidanceAudit
    {
        private static readonly Regex GhostWords = new Regex(@"\b(ghost|green guide|green outline|outline)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HighlightWords = new Regex(@"\b(highlight|highlighted|highlighting)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ArrowWords = new Regex(@"\b(arrow)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RingWords = new Regex(@"\b(ring|marked)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MatWords = new Regex(@"\b(mat|assembly area)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Minimum support level at which the guidance arrow is shown (GuidanceArrow.minimumLevel).</summary>
        public static SupportLevel ArrowMinimumLevel = SupportLevel.L2_Guided;

        private static readonly string[][] EquivalentRoles =
        {
            new[] { "pistonBolt", "pistonBoltOther" },
            new[] { "PistonNut", "PistonNutOther" }
        };

        /// <summary>Audits the whole sequence. Returns the number of contradictions found.</summary>
        public static int Run(StepRunner runner, GuidanceRegistry registry, string reason)
        {
            if (runner == null) { Debug.LogError("[GuidanceAudit] no StepRunner."); return 1; }

            int issues = 0, rows = 0;
            Debug.Log($"[GuidanceAudit] BEGIN ({reason}): {runner.StepCount} stage(s), levels L1..L3, arrow from {ArrowMinimumLevel}.");

            // Workspace placement precedes every stage: a fixed screen, no ghost, no arrow.
            Debug.Log("[GuidanceAudit] stage=workspace action=place_workspace level=all type=CONFIRM text=\"Place the workspace\" ghost=false arrow=false preview=false parts=[] targets=[reticle]");

            for (int i = 0; i < runner.StepCount; i++)
            {
                StepData stage = runner.GetStepAt(i);
                if (stage == null || stage.actions == null) continue;

                foreach (SupportLevel level in new[] { SupportLevel.L1_Minimal, SupportLevel.L2_Guided, SupportLevel.L3_Assisted })
                {
                    StepSupportContent content = stage.GetContent(level);
                    bool levelShowsGhosts = content != null &&
                                            ((content.ghostKeys != null && content.ghostKeys.Length > 0) ||
                                             (content.ghostPrefabs != null && content.ghostPrefabs.Length > 0) ||
                                             stage.ghostPrefab != null);
                    bool l1Override = level == SupportLevel.L1_Minimal && content != null && !string.IsNullOrEmpty(content.instructionText);

                    for (int a = 0; a < stage.actions.Count; a++)
                    {
                        AssemblyAction action = stage.actions[a];
                        if (action == null || !action.enabled) continue;
                        rows++;

                        bool physical = action.RequiresPhysicalValidation;
                        bool auto = action.autoCompleteRemaining;
                        string type = auto ? "AUTO" : (physical ? "PHYSICAL" : "READ");
                        bool ghost = physical && levelShowsGhosts &&
                                     ((action.ghostKeys != null && action.ghostKeys.Length > 0) ||
                                      (content.ghostKeys != null && content.ghostKeys.Length > 0) ||
                                      (content.ghostPrefabs != null && content.ghostPrefabs.Length > 0) || stage.ghostPrefab != null);
                        bool arrow = physical && action.showArrow && (int)level >= (int)ArrowMinimumLevel;
                        string role = StepValidator.RoleNameOf(action.partKey);
                        bool matAction = role == "PistonHead" || role == "ConnectingRod" || role == "ConnectingPin";
                        bool headOnMat = physical && role == "PistonHead" && !string.IsNullOrEmpty(action.targetKey) && action.targetKey.StartsWith("ghost.PistonKit");
                        string previewKey = action.partKey;
                        if (string.IsNullOrEmpty(previewKey))
                            for (int j = a + 1; j < stage.actions.Count; j++)
                                if (stage.actions[j] != null && stage.actions[j].enabled && !string.IsNullOrEmpty(stage.actions[j].partKey)) { previewKey = stage.actions[j].partKey; break; }
                        bool preview = !string.IsNullOrEmpty(previewKey);

                        string text = GuidanceText.InstructionFor(stage, action, level);
                        string detail = level != SupportLevel.L1_Minimal && !string.IsNullOrEmpty(action.detail) ? action.detail : "";

                        string parts = DescribeParts(registry, action);
                        string targets = DescribeTargets(registry, action);
                        string expects = auto ? "press Continue (auto-complete)" : (physical ? "grab + place" : (!string.IsNullOrEmpty(action.partKey) ? "read, then pick up the part (or Continue)" : "read + Continue"));

                        Debug.Log($"[GuidanceAudit] stage={stage.StepIdentifier} action={action.Id} level={Short(level)} type={type} " +
                                  $"text=\"{text}\" detail=\"{detail}\" ghost={ghost} arrow={arrow} preview={preview} mat={(matAction ? "shown" : "no")} ring={(headOnMat ? "shown" : "no")} " +
                                  $"parts=[{parts}] targets=[{targets}] expects=\"{expects}\"{(l1Override ? " (L1 stage override text)" : "")}");

                        issues += CheckText(stage, action, level, "instruction", text, ghost, arrow, headOnMat, matAction, physical);
                        if (!string.IsNullOrEmpty(detail))
                            issues += CheckText(stage, action, level, "detail", detail, ghost, arrow, headOnMat, matAction, physical);
                    }
                }
            }

            string verdict = issues == 0 ? "OK" : "FAIL";
            if (issues == 0) Debug.Log($"[GuidanceAudit] END {verdict}: {rows} action-level rows, 0 contradictions.");
            else Debug.LogError($"[GuidanceAudit] END {verdict}: {rows} action-level rows, {issues} contradiction(s) - see CONTRADICTION lines.");
            return issues;
        }

        private static int CheckText(StepData stage, AssemblyAction action, SupportLevel level, string field, string text,
                                     bool ghost, bool arrow, bool ring, bool mat, bool physical)
        {
            int n = 0;
            string where = $"{stage.StepIdentifier}/{action.Id}@{Short(level)} {field}";

            if (GhostWords.IsMatch(text) && !ghost) { n++; Debug.LogError($"[GuidanceAudit] CONTRADICTION {where}: mentions a ghost/guide but ghost=false: \"{text}\""); }
            if (HighlightWords.IsMatch(text) && !ghost && !ring) { n++; Debug.LogError($"[GuidanceAudit] CONTRADICTION {where}: mentions a highlighted position but no ghost or ring is shown: \"{text}\""); }
            if (ArrowWords.IsMatch(text) && !arrow) { n++; Debug.LogError($"[GuidanceAudit] CONTRADICTION {where}: mentions an arrow but arrow=false: \"{text}\""); }
            if (RingWords.IsMatch(text) && !ring) { n++; Debug.LogError($"[GuidanceAudit] CONTRADICTION {where}: mentions the ring/marked area but this is not the head-on-mat action: \"{text}\""); }
            if (MatWords.IsMatch(text) && !mat) { n++; Debug.LogError($"[GuidanceAudit] CONTRADICTION {where}: mentions the mat but the mat is not shown for this action: \"{text}\""); }
            if (!physical && (GhostWords.IsMatch(text) || HighlightWords.IsMatch(text) || ArrowWords.IsMatch(text)))
            { n++; Debug.LogError($"[GuidanceAudit] CONTRADICTION {where}: a READ instruction describes a placement cue that has not appeared yet: \"{text}\""); }
            return n;
        }

        private static string Short(SupportLevel l)
        {
            switch (l) { case SupportLevel.L1_Minimal: return "L1"; case SupportLevel.L2_Guided: return "L2"; default: return "L3"; }
        }

        private static string DescribeParts(GuidanceRegistry registry, AssemblyAction action)
        {
            if (string.IsNullOrEmpty(action.partKey)) return "";
            var keys = new List<string>();
            foreach (string key in ExpandEquivalent(action.partKey))
            {
                if (registry == null) { keys.Add(key); continue; }
                foreach (string k in registry.RoleCandidates(key))
                    if (!keys.Contains(k)) keys.Add(k);
            }
            var names = new List<string>();
            foreach (string k in keys)
            {
                if (registry != null && registry.TryResolveQuiet(k, out GameObject go) && go != null)
                    names.Add($"{k}->{go.name}{(go.activeInHierarchy ? "" : "(inactive)")}");
                else names.Add(k + "->UNRESOLVED");
            }
            return string.Join(", ", names);
        }

        private static string DescribeTargets(GuidanceRegistry registry, AssemblyAction action)
        {
            if (string.IsNullOrEmpty(action.targetKey)) return "";
            var names = new List<string>();
            foreach (string k in ExpandEquivalent(action.targetKey))
            {
                if (registry != null && registry.TryResolveQuiet(k, out GameObject go) && go != null) names.Add($"{k}->{go.name}");
                else names.Add(k + "->UNRESOLVED");
            }
            return string.Join(", ", names);
        }

        /// <summary>A bolt/nut key expands to both equivalent roles (either side is valid).</summary>
        public static IEnumerable<string> ExpandEquivalent(string key)
        {
            string role = StepValidator.RoleNameOf(key);
            foreach (string[] group in EquivalentRoles)
            {
                if (System.Array.IndexOf(group, role) < 0) continue;
                foreach (string r in group)
                    yield return key.Substring(0, key.Length - role.Length) + r;
                yield break;
            }
            yield return key;
        }
    }
}
