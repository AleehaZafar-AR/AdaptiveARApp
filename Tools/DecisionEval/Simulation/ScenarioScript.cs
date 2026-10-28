// File: ScenarioScript.cs
// Virtual-participant scenario definitions (JSON) and the physiology variants a scenario
// can be run under. A scenario is a chronological, scripted event stream: step entries,
// action entries, failed attempts, counted errors, wrong-component grabs, validated
// successes and clock advances. It never scripts a support change: in the simulation the
// level moves only through the real decision pipeline (gate -> provider -> structural
// constraints). Plain C#, shared by the harness and the headless tests.

using System;
using System.Collections.Generic;
using System.IO;

namespace AdaptiveAR.Decision.Simulation
{
    public enum ScenarioEventKind { Step, Action, Advance, FailedAttempt, Error, WrongComponent, Success, Physiology, Note }

    public sealed class ScenarioEvent
    {
        public ScenarioEventKind Kind;
        /// <summary>step id / action id / part key / physiology profile / note text.</summary>
        public string Id;
        public int Complexity;
        public double ExpectedSeconds;
        /// <summary>Advance: seconds of simulated time.</summary>
        public double Seconds;
        public int Line;

        public override string ToString()
        {
            switch (Kind)
            {
                case ScenarioEventKind.Step: return $"step {Id} (complexity {Complexity}, expected {ExpectedSeconds:F0} s)";
                case ScenarioEventKind.Action: return $"action {Id}";
                case ScenarioEventKind.Advance: return $"advance {Seconds:F1} s";
                case ScenarioEventKind.WrongComponent: return $"wrong_component {Id}";
                case ScenarioEventKind.Physiology: return $"physiology {Id}";
                case ScenarioEventKind.Note: return $"note {Id}";
                default: return Kind.ToString().ToLowerInvariant();
            }
        }
    }

    public sealed class ScenarioScript
    {
        public string Id;
        public string Title;
        public string Description;
        /// <summary>Expected qualitative behaviour, for the report; never a pass/fail assertion.</summary>
        public string Expected;
        public string OperatorExperience = "unspecified";
        public int StepCount = 6;
        public List<ScenarioEvent> Events = new List<ScenarioEvent>();
        public string SourcePath;

        public int Count(ScenarioEventKind k) { int n = 0; foreach (ScenarioEvent e in Events) if (e.Kind == k) n++; return n; }

        /// <summary>Total simulated seconds of explicit advances.</summary>
        public double ScriptedSeconds { get { double s = 0; foreach (ScenarioEvent e in Events) if (e.Kind == ScenarioEventKind.Advance) s += e.Seconds; return s; } }

        /// <summary>Simulated time of the first error-like event (counted error or wrong component), seconds; null when none.</summary>
        public double? DifficultyOnsetSeconds
        {
            get
            {
                double t = 0;
                foreach (ScenarioEvent e in Events)
                {
                    if (e.Kind == ScenarioEventKind.Advance) t += e.Seconds;
                    else if (e.Kind == ScenarioEventKind.Error || e.Kind == ScenarioEventKind.WrongComponent) return t;
                }
                return null;
            }
        }

        public static ScenarioScript Parse(string json, string sourcePath = null)
        {
            Dictionary<string, object> o = MiniJson.ParseObject(json);
            if (o == null) throw new FormatException("scenario is not a JSON object: " + sourcePath);
            var s = new ScenarioScript
            {
                Id = MiniJson.GetString(o, "id", null), Title = MiniJson.GetString(o, "title", null), Description = MiniJson.GetString(o, "description", null),
                Expected = MiniJson.GetString(o, "expected", null), OperatorExperience = MiniJson.GetString(o, "operator_experience", "unspecified"),
                StepCount = MiniJson.GetInt(o, "step_count", 6), SourcePath = sourcePath
            };
            if (string.IsNullOrEmpty(s.Id)) throw new FormatException("scenario has no id: " + sourcePath);
            List<object> events = MiniJson.GetList(o, "events");
            if (events == null || events.Count == 0) throw new FormatException("scenario has no events: " + s.Id);
            int line = 0;
            foreach (object item in events)
            {
                line++;
                var eo = item as Dictionary<string, object>;
                if (eo == null) throw new FormatException($"{s.Id}: event #{line} is not an object");
                string t = MiniJson.GetString(eo, "t", "") ?? "";
                var ev = new ScenarioEvent { Line = line };
                switch (t)
                {
                    case "step": ev.Kind = ScenarioEventKind.Step; ev.Id = MiniJson.GetString(eo, "id", null); ev.Complexity = MiniJson.GetInt(eo, "complexity", 1); ev.ExpectedSeconds = MiniJson.GetDouble(eo, "expected_s", 0); break;
                    case "action": ev.Kind = ScenarioEventKind.Action; ev.Id = MiniJson.GetString(eo, "id", null); break;
                    case "advance": ev.Kind = ScenarioEventKind.Advance; ev.Seconds = MiniJson.GetDouble(eo, "s", 0); break;
                    case "failed_attempt": ev.Kind = ScenarioEventKind.FailedAttempt; break;
                    case "error": ev.Kind = ScenarioEventKind.Error; break;
                    case "wrong_component": ev.Kind = ScenarioEventKind.WrongComponent; ev.Id = MiniJson.GetString(eo, "part", "part.unknown"); break;
                    case "success": ev.Kind = ScenarioEventKind.Success; break;
                    case "physiology": ev.Kind = ScenarioEventKind.Physiology; ev.Id = MiniJson.GetString(eo, "profile", "null"); break;
                    case "note": ev.Kind = ScenarioEventKind.Note; ev.Id = MiniJson.GetString(eo, "text", ""); break;
                    default: throw new FormatException($"{s.Id}: event #{line} has unknown type '{t}'");
                }
                if ((ev.Kind == ScenarioEventKind.Step || ev.Kind == ScenarioEventKind.Action) && string.IsNullOrEmpty(ev.Id)) throw new FormatException($"{s.Id}: event #{line} ({t}) needs an id");
                if (ev.Kind == ScenarioEventKind.Advance && ev.Seconds < 0) throw new FormatException($"{s.Id}: event #{line} advance must be >= 0");
                s.Events.Add(ev);
            }
            if (s.Events[0].Kind != ScenarioEventKind.Step) throw new FormatException($"{s.Id}: the first event must be a step entry");
            return s;
        }

        public static ScenarioScript Load(string path) { return Parse(File.ReadAllText(path), path); }

        /// <summary>Every *.json in a folder (or one file), sorted by id.</summary>
        public static List<ScenarioScript> LoadAll(string pathOrDir)
        {
            var list = new List<ScenarioScript>();
            if (Directory.Exists(pathOrDir))
                foreach (string f in Directory.GetFiles(pathOrDir, "*.json")) list.Add(Load(f));
            else if (File.Exists(pathOrDir)) list.Add(Load(pathOrDir));
            else throw new FileNotFoundException("scenario path not found", pathOrDir);
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }
    }

    /// <summary>
    /// Physiology variants under which a scenario is run with IDENTICAL task behaviour.
    /// Synthetic, constant per run, plausible ranges only. They test whether physiology
    /// behaves as supplementary context, not whether any pattern means anything.
    /// </summary>
    public static class PhysiologyProfiles
    {
        public static readonly string[] All = { "null", "nominal", "elevated", "stale" };

        public static bool IsKnown(string name) { return Array.IndexOf(All, name) >= 0; }

        /// <summary>The sample for a profile at a given simulated moment; null for "null" (physiology unavailable).</summary>
        public static PhysiologicalSample Sample(string profile, double simSeconds)
        {
            switch (profile)
            {
                case null:
                case "null":
                case "none": return null;
                case "nominal": return new PhysiologicalSample { RmssdMs = 45f, HeartRateBpm = 72f, MeanIbiMs = 833f, Quality = 0.9f, AgeMs = 2000f, Source = "synthetic:nominal" };
                case "elevated": return new PhysiologicalSample { RmssdMs = 18f, HeartRateBpm = 96f, MeanIbiMs = 625f, Quality = 0.9f, AgeMs = 2000f, Source = "synthetic:elevated" };
                case "stale": return new PhysiologicalSample { RmssdMs = 30f, HeartRateBpm = 80f, MeanIbiMs = 750f, Quality = 0.35f, AgeMs = 45000f, Source = "synthetic:stale" };
                default: throw new ArgumentException("unknown physiology profile '" + profile + "' (null|nominal|elevated|stale)");
            }
        }

        public static string[] ParseList(string arg)
        {
            if (string.IsNullOrEmpty(arg) || arg == "all") return (string[])All.Clone();
            var list = new List<string>();
            foreach (string p in arg.Split(',')) { string t = p.Trim(); if (t.Length == 0) continue; if (!IsKnown(t)) throw new ArgumentException("unknown physiology profile '" + t + "'"); list.Add(t); }
            return list.ToArray();
        }
    }
}
