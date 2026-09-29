// File: WorkflowState.cs
// The single source of truth for where the participant is in the assembly.
//
// Progress used to be inferred in several places at once - the progress bar counted
// one thing, the task list another, and the step label a third - which is why the
// display could disagree with reality. Everything now subscribes to this one object
// and nothing keeps a private counter.
//
// Completion is derived from validated actions, never from button presses or which
// screen is showing. A stage is complete only when every required action inside it
// has actually been validated.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    /// <summary>Where the session is overall.</summary>
    public enum WorkflowPhase
    {
        Idle = 0,
        Anchoring = 1,
        Onboarding = 2,
        Assembly = 3,
        Complete = 4
    }

    public class WorkflowState : MonoBehaviour
    {
        [SerializeField] private StepRunner stepRunner;

        public WorkflowPhase Phase { get; private set; } = WorkflowPhase.Idle;

        /// <summary>Index of the active stage, or -1 before assembly begins.</summary>
        public int StageIndex { get; private set; } = -1;

        /// <summary>Index of the active action inside the current stage, or -1.</summary>
        public int ActionIndex { get; private set; } = -1;

        /// <summary>True when the current action has been validated and progression is allowed.</summary>
        public bool CurrentActionComplete { get; private set; }

        public int StageCount { get { return stepRunner != null ? stepRunner.StepCount : 0; } }

        public StepData CurrentStage { get { return stepRunner != null ? stepRunner.CurrentStep : null; } }

        /// <summary>Raised whenever anything below changes. One event, one refresh.</summary>
        public event Action OnChanged;

        private readonly HashSet<string> _completedActions = new HashSet<string>();
        private readonly HashSet<int> _completedStages = new HashSet<int>();

        // =====================================================================
        // Queries used by every display
        // =====================================================================

        public AssemblyAction CurrentAction
        {
            get
            {
                StepData stage = CurrentStage;
                if (stage == null || stage.actions == null) return null;
                if (ActionIndex < 0 || ActionIndex >= stage.actions.Count) return null;
                return stage.actions[ActionIndex];
            }
        }

        /// <summary>Actions in this stage that the participant is actually expected to do.</summary>
        public int EnabledActionCount(StepData stage)
        {
            if (stage == null || stage.actions == null) return 0;

            int n = 0;
            foreach (AssemblyAction a in stage.actions)
                if (a != null && a.enabled) n++;
            return n;
        }

        /// <summary>How many enabled actions of this stage are done.</summary>
        public int CompletedActionCount(StepData stage)
        {
            if (stage == null || stage.actions == null) return 0;

            int n = 0;
            foreach (AssemblyAction a in stage.actions)
                if (a != null && a.enabled && _completedActions.Contains(ActionKey(stage, a))) n++;
            return n;
        }

        public bool IsStageComplete(int index)
        {
            return _completedStages.Contains(index);
        }

        /// <summary>Fraction of the whole assembly finished, 0-1. Derived, never assigned.</summary>
        public float OverallProgress
        {
            get
            {
                if (Phase == WorkflowPhase.Complete) return 1f;
                if (stepRunner == null || StageCount == 0) return 0f;

                int totalActions = 0;
                int doneActions = 0;

                for (int i = 0; i < StageCount; i++)
                {
                    StepData stage = stepRunner.GetStepAt(i);
                    int enabled = EnabledActionCount(stage);

                    // A stage with no authored actions still counts as one unit of work.
                    totalActions += Mathf.Max(1, enabled);
                    doneActions += _completedStages.Contains(i)
                        ? Mathf.Max(1, enabled)
                        : CompletedActionCount(stage);
                }

                return totalActions == 0 ? 0f : Mathf.Clamp01(doneActions / (float)totalActions);
            }
        }

        /// <summary>"2 of 4" within the current stage, for the action counter.</summary>
        public void GetActionCounter(out int current, out int total)
        {
            StepData stage = CurrentStage;
            total = EnabledActionCount(stage);
            current = Mathf.Min(total, CompletedActionCount(stage) + 1);
        }

        // =====================================================================
        // Mutation - only the session controller calls these
        // =====================================================================

        public void SetPhase(WorkflowPhase phase)
        {
            if (Phase == phase) return;
            Phase = phase;
            Raise();
        }

        public void EnterStage(int index)
        {
            StageIndex = index;
            ActionIndex = -1;
            CurrentActionComplete = false;
            Raise();
        }

        public void EnterAction(int index)
        {
            ActionIndex = index;
            CurrentActionComplete = false;
            Raise();
        }

        /// <summary>Marks the active action validated. Idempotent.</summary>
        public void CompleteCurrentAction()
        {
            StepData stage = CurrentStage;
            AssemblyAction action = CurrentAction;
            if (stage == null || action == null) return;

            _completedActions.Add(ActionKey(stage, action));
            CurrentActionComplete = true;
            Raise();
        }

        public void CompleteStage(int index)
        {
            _completedStages.Add(index);
            Raise();
        }

        public bool IsActionComplete(StepData stage, AssemblyAction action)
        {
            if (stage == null || action == null) return false;
            return _completedActions.Contains(ActionKey(stage, action));
        }

        public void ResetAll()
        {
            _completedActions.Clear();
            _completedStages.Clear();
            StageIndex = -1;
            ActionIndex = -1;
            CurrentActionComplete = false;
            Phase = WorkflowPhase.Idle;
            Raise();
        }

        private static string ActionKey(StepData stage, AssemblyAction action)
        {
            return stage.StepIdentifier + "/" + action.Id;
        }

        private void Raise()
        {
            OnChanged?.Invoke();
        }
    }
}
