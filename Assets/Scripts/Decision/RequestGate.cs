// File: RequestGate.cs
// WHEN the live session asks the provider for a decision. Plain C# with an injected clock
// so the policy is headlessly testable; DecisionScheduler drives it from Unity.
//
// Two protections are kept apart on purpose:
//   provider-call protection  - one request in flight, minCallIntervalSeconds between calls;
//   support-change hysteresis - changeCooldownMs after an APPLIED level change, during which
//                               requests are deferred (never dropped, never turned into KEEP).
// Evidence accumulates between calls: error-like events (counted error, wrong component)
// fire a request once minEventsPerRequest of them have accrued, or after pendingFlushMs
// for a lone event, so a small meaningful pattern is seen promptly without a call per
// error. Success-only evidence (a clean run) fires only when a decrease is structurally
// possible (level above L1) and after successOnly successes, which double after each
// success-only request that changed nothing (2, 4, 8, ...; reset by any error or any
// applied change): a long clean sequence cannot produce a KEEP call every few seconds.
// An action entry with nothing pending gives one first look per step after
// firstDecisionAfterStepMs. None of this decides WHAT the action is: the gate never reads
// the task state beyond "is a decrease structurally available", and the provider sees
// everything that accrued.

using System;
using System.Globalization;
using AdaptiveAR.Support;

namespace AdaptiveAR.Decision
{
    public sealed class RequestGate
    {
        public enum Verdict { Fire, Deferred, Skipped }

        public struct Result
        {
            public Verdict Verdict;
            public string Reason;
            /// <summary>The trigger label of a fired request: "<trigger>+<n>ev(<errors>err,<successes>ok)".</summary>
            public string Trigger;
            public int PendingEvents, PendingErrors, PendingSuccesses;
            public string DeferredFrom;
        }

        private readonly DecisionScheduleConfig _cfg;

        public int PendingEvents { get; private set; }
        public int PendingErrors { get; private set; }
        public int PendingSuccesses { get; private set; }
        public double FirstPendingAt { get; private set; } = double.NaN;
        public double LastRequestAt { get; private set; } = double.NegativeInfinity;
        public double CooldownUntil { get; private set; } = double.NegativeInfinity;
        public int SuccessOnlyRequired { get; private set; }
        public bool LastRequestSuccessOnly { get; private set; }
        public bool InFlight { get; set; }
        /// <summary>A trigger waiting for a protection to lift; re-evaluated by the scheduler's Update.</summary>
        public string Deferred { get; private set; }
        public int RequestsOnStep { get; private set; }

        public RequestGate(DecisionScheduleConfig cfg)
        {
            _cfg = cfg ?? new DecisionScheduleConfig();
            SuccessOnlyRequired = Math.Max(1, _cfg.MinEventsPerRequest);
        }

        /// <summary>A counted error or a wrong-component grab happened.</summary>
        public void NoteError(double nowS)
        {
            PendingEvents++; PendingErrors++;
            SuccessOnlyRequired = Math.Max(1, _cfg.MinEventsPerRequest);   // difficulty ends the clean-run backoff
            if (double.IsNaN(FirstPendingAt)) FirstPendingAt = nowS;
        }

        /// <summary>A validated action completed.</summary>
        public void NoteSuccess(double nowS)
        {
            PendingEvents++; PendingSuccesses++;
            // FirstPendingAt is the first pending ERROR-like event: only those are flushed by time.
        }

        /// <summary>A new step: its events belong to the new ledger; the hysteresis of an applied change is kept.</summary>
        public void ResetForStep()
        {
            PendingEvents = PendingErrors = PendingSuccesses = 0;
            FirstPendingAt = double.NaN;
            Deferred = null;
            RequestsOnStep = 0;
            SuccessOnlyRequired = Math.Max(1, _cfg.MinEventsPerRequest);
        }

        /// <summary>True when a lone error-like batch has waited pendingFlushMs and should be evaluated now.</summary>
        public bool NeedsFlush(double nowS)
        {
            return PendingErrors > 0 && !double.IsNaN(FirstPendingAt) && (nowS - FirstPendingAt) * 1000.0 >= _cfg.PendingFlushMs;
        }

        public bool HasDeferred { get { return Deferred != null; } }

        /// <summary>
        /// Decides whether a request is made now. force bypasses the evidence rule only
        /// (researcher control); the structural protections always apply.
        /// </summary>
        public Result Evaluate(string trigger, double nowS, double stepElapsedMs, SupportLevel level, bool force = false)
        {
            bool errorBatch = PendingErrors > 0;
            bool successOnly = !errorBatch && PendingSuccesses > 0;
            bool actionEnter = trigger != null && trigger.StartsWith("action_enter", StringComparison.Ordinal);
            bool periodic = trigger == "periodic";
            bool flush = trigger == "pending_flush";

            // 1. Evidence: is there anything worth asking about?
            if (!force)
            {
                if (errorBatch)
                {
                    // Successes pending at the same time ride along in the context; only the
                    // error-like events count towards the threshold.
                    bool enough = PendingErrors >= _cfg.MinEventsPerRequest || NeedsFlush(nowS);
                    if (!enough)
                        return Skip($"accumulating {PendingErrors}/{_cfg.MinEventsPerRequest} error-like events (flush after {_cfg.PendingFlushMs:F0} ms)");
                }
                else if (successOnly)
                {
                    if ((int)level <= (int)SupportLevel.L1_Minimal)
                        return Skip("success-only evidence at L1_Minimal: no decrease is structurally available, nothing to ask");
                    if (PendingSuccesses < SuccessOnlyRequired)
                        return Skip($"accumulating successes {PendingSuccesses}/{SuccessOnlyRequired} (clean-run backoff)");
                }
                else if (actionEnter)
                {
                    if (RequestsOnStep > 0) return Skip("action entry with nothing pending: first look already taken on this step");
                }
                else if (!periodic && !flush)
                {
                    return Skip("nothing pending");
                }
                else if (flush && !errorBatch)
                {
                    return Skip("nothing to flush");
                }
            }

            // 2. Structural protections (deferred, never dropped).
            if (InFlight) return Defer(trigger, "request in flight");
            if (nowS < CooldownUntil) return Defer(trigger, $"change cooldown ({(CooldownUntil - nowS):F1} s left)");
            double sinceCall = nowS - LastRequestAt;
            if (sinceCall < _cfg.MinCallIntervalSeconds) return Defer(trigger, $"call protection ({sinceCall:F1} s < {_cfg.MinCallIntervalSeconds} s)");
            if (!errorBatch && !force && stepElapsedMs < _cfg.FirstDecisionAfterStepMs)
                return Defer(trigger, $"step too young ({stepElapsedMs:F0} ms < {_cfg.FirstDecisionAfterStepMs:F0} ms)");

            // 3. Fire.
            var r = new Result
            {
                Verdict = Verdict.Fire,
                Trigger = trigger + "+" + PendingEvents.ToString(CultureInfo.InvariantCulture) + "ev(" + PendingErrors.ToString(CultureInfo.InvariantCulture) + "err," + PendingSuccesses.ToString(CultureInfo.InvariantCulture) + "ok)",
                PendingEvents = PendingEvents, PendingErrors = PendingErrors, PendingSuccesses = PendingSuccesses,
                DeferredFrom = Deferred,
                Reason = null
            };
            LastRequestSuccessOnly = successOnly;
            PendingEvents = PendingErrors = PendingSuccesses = 0;
            FirstPendingAt = double.NaN;
            Deferred = null;
            LastRequestAt = nowS;
            RequestsOnStep++;
            return r;
        }

        /// <summary>The request finished. An applied level change starts the hysteresis; a success-only request that changed nothing doubles the clean-run requirement.</summary>
        public void OnApplied(bool levelChanged, double nowS)
        {
            InFlight = false;
            if (levelChanged)
            {
                CooldownUntil = nowS + _cfg.ChangeCooldownMs / 1000.0;
                SuccessOnlyRequired = Math.Max(1, _cfg.MinEventsPerRequest);
            }
            else if (LastRequestSuccessOnly && _cfg.SuccessOnlyBackoff)
            {
                SuccessOnlyRequired = Math.Min(Math.Max(1, SuccessOnlyRequired) * 2, Math.Max(1, _cfg.SuccessOnlyBackoffMax));
            }
        }

        private Result Skip(string why) { return new Result { Verdict = Verdict.Skipped, Reason = why }; }

        private Result Defer(string trigger, string why)
        {
            Deferred = trigger;
            return new Result { Verdict = Verdict.Deferred, Reason = why };
        }
    }
}
