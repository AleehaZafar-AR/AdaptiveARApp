// File: ValidationScenario.cs
// A Level 2 technical-validation scenario: a chronological script of high-level
// participant events (Continue presses, placement outcomes, wrong-part grabs, waits) that
// the runner injects through the production entry points. Plain C#, parsed with MiniJson,
// so the headless tests can read the bundled TV1-TV6 files.
//
// Support-level changes are NOT scriptable: the parser rejects any event kind outside the
// list below, so a scenario cannot set L2/L3 directly. The level moves only through the
// real pipeline (ledger -> scheduler -> provider -> constraints -> support controller).

using System;
using System.Collections.Generic;
using System.Globalization;
using AdaptiveAR.Decision;

namespace AdaptiveAR.TechnicalValidation
{
    public enum ValidationEventKind
    {
        /// <summary>The Continue control (step introduction / READ acknowledgement / the auto-complete confirm).</summary>
        Continue = 0,
        /// <summary>The armed action's part placed within tolerance (production success path: snap, lock, join, advance).</summary>
        Success = 1,
        /// <summary>An unsuccessful attempt that is not a counted error (carried out of the zone).</summary>
        FailedAttempt = 2,
        /// <summary>A counted error: a deliberate failed placement (released in the zone, out of tolerance).</summary>
        Error = 3,
        /// <summary>A wrong component picked up and put down again.</summary>
        WrongComponent = 4,
        /// <summary>Nothing happens for the authored delay.</summary>
        Wait = 5,
        /// <summary>A note written to the validation log only.</summary>
        Note = 6,
        /// <summary>Completes the rest of the sequence with clean Continue/success events at the finish delay, until the sequence ends.</summary>
        FinishSequence = 7
    }

    public sealed class ValidationEvent
    {
        public ValidationEventKind Kind;
        /// <summary>Authored delay before the event, seconds of real time at delay scale 1.</summary>
        public double AfterSeconds;
        /// <summary>Expected production action id when the event fires (alignment check); null = not asserted.</summary>
        public string ExpectedActionId;
        /// <summary>Wrong-component only: preferred part key; null = any part the action does not accept.</summary>
        public string PartKey;
        public string Note;

        public string KindName { get { return KindToName(Kind); } }

        public static string KindToName(ValidationEventKind k)
        {
            switch (k)
            {
                case ValidationEventKind.Continue: return "continue";
                case ValidationEventKind.Success: return "success";
                case ValidationEventKind.FailedAttempt: return "failed_attempt";
                case ValidationEventKind.Error: return "error";
                case ValidationEventKind.WrongComponent: return "wrong_component";
                case ValidationEventKind.Wait: return "wait";
                case ValidationEventKind.Note: return "note";
                case ValidationEventKind.FinishSequence: return "finish_sequence";
                default: return k.ToString();
            }
        }

        public static bool TryParseKind(string s, out ValidationEventKind kind)
        {
            kind = ValidationEventKind.Wait;
            if (string.IsNullOrEmpty(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "continue": kind = ValidationEventKind.Continue; return true;
                case "success": kind = ValidationEventKind.Success; return true;
                case "failed_attempt": kind = ValidationEventKind.FailedAttempt; return true;
                case "error": kind = ValidationEventKind.Error; return true;
                case "wrong_component": kind = ValidationEventKind.WrongComponent; return true;
                case "wait": kind = ValidationEventKind.Wait; return true;
                case "note": kind = ValidationEventKind.Note; return true;
                case "finish_sequence": kind = ValidationEventKind.FinishSequence; return true;
                default: return false;
            }
        }

        /// <summary>True for the kinds that enter the production task-event ledger (error-like or success).</summary>
        public bool IsLedgerEvent
        {
            get { return Kind == ValidationEventKind.Success || Kind == ValidationEventKind.FailedAttempt || Kind == ValidationEventKind.Error || Kind == ValidationEventKind.WrongComponent; }
        }

        public bool IsErrorLike { get { return Kind == ValidationEventKind.Error || Kind == ValidationEventKind.WrongComponent; } }
    }

    public sealed class ValidationScenario
    {
        public string Id;
        public string Title;
        public string Description;
        /// <summary>Qualitative expectation, descriptive only; never asserted on the model's choice.</summary>
        public string Expected;
        /// <summary>Optional physiology override ("nominal" | "elevated" | "unavailable"); null = the config's condition.</summary>
        public string Physiology;
        public readonly List<ValidationEvent> Events = new List<ValidationEvent>();

        public int CountOf(ValidationEventKind k) { int n = 0; foreach (var e in Events) if (e.Kind == k) n++; return n; }

        /// <summary>Sum of the authored delays, seconds (the finish-sequence tail excluded).</summary>
        public double AuthoredSeconds { get { double t = 0; foreach (var e in Events) t += e.AfterSeconds; return t; } }

        /// <summary>Parses a scenario file. Throws FormatException on a structural problem (unknown event kind included).</summary>
        public static ValidationScenario Parse(string json, string source)
        {
            Dictionary<string, object> o = MiniJson.ParseObject(json);
            if (o == null) throw new FormatException("scenario is not a JSON object: " + source);
            var s = new ValidationScenario
            {
                Id = MiniJson.GetString(o, "id", null),
                Title = MiniJson.GetString(o, "title", null),
                Description = MiniJson.GetString(o, "description", null),
                Expected = MiniJson.GetString(o, "expected", null),
                Physiology = MiniJson.GetString(o, "physiology", null)
            };
            if (string.IsNullOrEmpty(s.Id)) throw new FormatException("scenario has no id: " + source);
            List<object> events = MiniJson.GetList(o, "events");
            if (events == null || events.Count == 0) throw new FormatException("scenario has no events: " + source);
            int i = 0;
            foreach (object item in events)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) throw new FormatException($"event #{i} is not an object in {source}");
                string kindText = MiniJson.GetString(d, "t", null) ?? MiniJson.GetString(d, "kind", null);
                if (!ValidationEvent.TryParseKind(kindText, out ValidationEventKind kind))
                    throw new FormatException($"event #{i} has an unsupported kind '{kindText}' in {source} (support levels cannot be scripted)");
                double after = MiniJson.GetDouble(d, "after", 0);
                if (after < 0 || double.IsNaN(after) || double.IsInfinity(after)) throw new FormatException($"event #{i} has an invalid delay in {source}");
                s.Events.Add(new ValidationEvent
                {
                    Kind = kind,
                    AfterSeconds = after,
                    ExpectedActionId = MiniJson.GetString(d, "action", null),
                    PartKey = MiniJson.GetString(d, "part", null),
                    Note = MiniJson.GetString(d, "note", null)
                });
                i++;
            }
            return s;
        }

        public string Summary()
        {
            var c = CultureInfo.InvariantCulture;
            return $"{Id} '{Title}': {Events.Count} event(s), authored {AuthoredSeconds.ToString("0", c)} s; " +
                   $"continue={CountOf(ValidationEventKind.Continue)} success={CountOf(ValidationEventKind.Success)} failed_attempt={CountOf(ValidationEventKind.FailedAttempt)} " +
                   $"error={CountOf(ValidationEventKind.Error)} wrong_component={CountOf(ValidationEventKind.WrongComponent)} finish_sequence={CountOf(ValidationEventKind.FinishSequence)}";
        }
    }
}
