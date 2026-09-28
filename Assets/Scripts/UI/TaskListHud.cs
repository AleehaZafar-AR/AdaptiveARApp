// File: TaskListHud.cs
// Drives the existing OverviewCanvas task rows, which were laid out but unwired.
//
// The canvas has three rows (TaskA / TaskB / TaskC) and the sequence has more steps
// than that, so the rows show a sliding window around the current step rather than
// the whole list. Completed steps are ticked, the current one is marked.

using System.Collections.Generic;
using AdaptiveAR.Steps;
using TMPro;
using UnityEngine;

namespace AdaptiveAR.UI
{
    public class TaskListHud : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private AssemblySessionController session;
        [SerializeField] private StepRunner stepRunner;

        [Header("Rows")]
        [Tooltip("OverviewCanvas/TaskA/TaskaText, TaskB/TaskbText, TaskC/TaskcText - in order. " +
                 "Any number of rows works; the list scrolls to keep the current step visible.")]
        [SerializeField] private List<TextMeshProUGUI> rowTexts = new List<TextMeshProUGUI>();

        [Header("Optional")]
        [Tooltip("OverviewCanvas/TopPanel/CaptionText - a heading for the list.")]
        [SerializeField] private TextMeshProUGUI headingText;

        [SerializeField] private string headingLabel = "Engine Assembly";

        [Header("Markers")]
        [SerializeField] private string donePrefix = "[x] ";
        [SerializeField] private string currentPrefix = "> ";
        [SerializeField] private string pendingPrefix = "[ ] ";

        [Header("Display")]
        [Tooltip("Fall back to the full stepTitle when a step has no short displayName.")]
        [SerializeField] private bool fallBackToFullTitle = true;

        private void OnEnable()
        {
            if (session != null)
                session.OnStateChanged += Refresh;

            Refresh();
        }

        private void OnDisable()
        {
            if (session != null)
                session.OnStateChanged -= Refresh;
        }

        private void Refresh()
        {
            if (headingText != null)
                headingText.text = headingLabel;

            if (rowTexts == null || rowTexts.Count == 0 || stepRunner == null)
                return;

            int count = stepRunner.StepCount;
            int current = stepRunner.CurrentStepIndex;
            bool finished = session != null && session.SequenceFinished;

            int rows = rowTexts.Count;
            int first = FirstVisibleIndex(current, count, rows);

            for (int r = 0; r < rows; r++)
            {
                TextMeshProUGUI label = rowTexts[r];
                if (label == null) continue;

                int stepIndex = first + r;

                if (stepIndex < 0 || stepIndex >= count)
                {
                    label.text = "";
                    continue;
                }

                StepData step = stepRunner.GetStepAt(stepIndex);
                string name = DescribeStep(step, stepIndex);

                string prefix;
                if (finished || stepIndex < current) prefix = donePrefix;
                else if (stepIndex == current) prefix = currentPrefix;
                else prefix = pendingPrefix;

                label.text = prefix + name;
            }
        }

        /// <summary>Keeps the current step inside the visible window, without running off either end.</summary>
        private static int FirstVisibleIndex(int current, int count, int rows)
        {
            if (count <= rows) return 0;
            if (current < 0) return 0;

            // Put the current step one row down where possible, so the next step is visible.
            int first = current - 1;
            first = Mathf.Clamp(first, 0, Mathf.Max(0, count - rows));
            return first;
        }

        private string DescribeStep(StepData step, int index)
        {
            if (step == null) return $"Step {index + 1}";

            // A short label is what a three-row list needs.
            if (!string.IsNullOrEmpty(step.displayName))
                return step.displayName;

            if (fallBackToFullTitle && !string.IsNullOrEmpty(step.stepTitle))
            {
                // The caption text can be multi-line; a list row must stay on one line.
                return step.stepTitle.Replace("\n", " ").Trim();
            }

            return step.StepIdentifier;
        }
    }
}
