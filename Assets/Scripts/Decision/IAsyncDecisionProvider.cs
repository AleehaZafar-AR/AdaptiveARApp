// File: IAsyncDecisionProvider.cs
// A provider whose request completes later (a network model). Plain C#: the routine is an
// IEnumerator so Unity can run it as a coroutine and a headless harness can step it.

using System;
using System.Collections;

namespace AdaptiveAR.Decision
{
    public interface IAsyncDecisionProvider
    {
        string ProviderId { get; }

        /// <summary>Yields until the response is ready, then calls done exactly once. Must not throw: report a failed response instead.</summary>
        IEnumerator RequestDecisionRoutine(DecisionContext context, Action<DecisionResponse> done);
    }
}
