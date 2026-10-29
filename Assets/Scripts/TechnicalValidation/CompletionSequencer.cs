// File: CompletionSequencer.cs
// Orders the end-of-run bookkeeping of a Level 2 run. Plain C# (no Unity dependency) so the
// headless tests cover the ordering.
//
// An injected event can complete the assembly sequence synchronously, inside the production
// call that injects it: the last Success raises OnStepValidated -> sequence complete ->
// OnSessionComplete, and the session controller then ends the production logger session
// before the injection call has even returned. Without explicit ordering the completion
// record is written while the event that caused it is still unrecorded (events_injected one
// short), and the event's own records land after session_end (the production logger drops
// them). The sequencer guarantees:
//   1. the event being injected is committed (ledger entry + its log lines) first,
//   2. the completion record is written afterwards, exactly once,
//   3. its event count is therefore the final count,
// and a late Close() for an already-committed event changes nothing.
//
// Technical-validation layer only: nothing here touches scenario timing, injection semantics,
// the decision layer, support logic or the production runtime.

using System;

namespace AdaptiveAR.TechnicalValidation
{
    public sealed class CompletionSequencer
    {
        private readonly ScenarioRunRecord _record;
        private readonly Action<InjectedEventRecord> _commitEvent;
        private readonly Action<string> _writeCompletion;
        private InjectedEventRecord _open;

        public bool CompletionWritten { get; private set; }
        public string CompletionHow { get; private set; }
        public bool InjectionOpen { get { return _open != null; } }

        /// <param name="record">The run record; committed events are appended to its Events list.</param>
        /// <param name="commitEvent">Writes the log records of one committed event (called exactly once per event, after the ledger append).</param>
        /// <param name="writeCompletion">Writes the completion record for the given "how"; it reads the final counts from the record.</param>
        public CompletionSequencer(ScenarioRunRecord record, Action<InjectedEventRecord> commitEvent, Action<string> writeCompletion)
        {
            if (record == null) throw new ArgumentNullException("record");
            _record = record;
            _commitEvent = commitEvent ?? (r => { });
            _writeCompletion = writeCompletion ?? (h => { });
        }

        /// <summary>Called immediately before the production call that injects <paramref name="rec"/>.</summary>
        public void Open(InjectedEventRecord rec)
        {
            if (rec == null) throw new ArgumentNullException("rec");
            if (_open != null) Commit(_open); // never expected (injections are synchronous); keep the ledger complete anyway
            _open = rec;
        }

        /// <summary>
        /// Called after the production call returned, with its outcome. Returns false when the
        /// event was already committed by <see cref="Complete"/> (the sequence completed inside
        /// the injection); the outcome recorded there stands and nothing is duplicated.
        /// </summary>
        public bool Close(bool injected, string detail, string partKey, string actionAfter)
        {
            var rec = _open;
            if (rec == null) return false;
            _open = null;
            rec.Injected = injected; rec.Detail = detail; rec.PartKey = partKey; rec.ActionAfter = actionAfter;
            Commit(rec);
            return true;
        }

        /// <summary>
        /// Sequence/script completion. Commits an open injection first (the sequence completed
        /// inside it, so it took effect), then writes the completion record once. Returns true
        /// when the completion record was written by this call.
        /// </summary>
        public bool Complete(string how, string actionNow)
        {
            var rec = _open;
            if (rec != null)
            {
                _open = null;
                rec.Injected = true;
                rec.ActionAfter = actionNow;
                rec.Detail = (rec.Kind ?? "event") + " accepted; the sequence completed during this injection";
                Commit(rec);
            }
            if (CompletionWritten) return false;
            CompletionWritten = true;
            CompletionHow = how;
            _writeCompletion(how);
            return true;
        }

        private void Commit(InjectedEventRecord rec)
        {
            _record.Events.Add(rec);
            _commitEvent(rec);
        }
    }
}
