// File: TaskListHud.cs
// Drives the Overview (Steps / What's Next) panel rows. The panel's transform is
// never touched here; only the row text changes.
//
// Rows communicate completed / current / upcoming at the level a participant thinks
// in: workspace, crankshaft, piston 1, piston 2, remaining pistons, camshaft.
//
//   ✓ Workspace placed          (green)
//   ✓ Crankshaft installed      (green)
//   ● Assemble piston 1         (current: bright, bold)
//   ○ Assemble piston 2         (upcoming: subdued)
//   ○ Complete remaining pistons
//   ○ Install camshaft
//
// State comes from WorkflowState (stage completion) and StepManager (placement), so
// it cannot disagree with reality.

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

        [Tooltip("Found at runtime when empty.")]
        [SerializeField] private WorkflowState workflow;
        [SerializeField] private StepManager stepManager;

        [Header("Rows")]
        [Tooltip("Row text fields, top to bottom.")]
        [SerializeField] private List<TextMeshProUGUI> rowTexts = new List<TextMeshProUGUI>();

        [Header("Optional")]
        [SerializeField] private TextMeshProUGUI headingText;
        [SerializeField] private string headingLabel = "Engine Assembly";

        [Header("Markers")]
        [SerializeField] private string donePrefix = "✓  ";
        [SerializeField] private string currentPrefix = "●  ";
        [SerializeField] private string pendingPrefix = "○  ";

        [Header("Display")]
        [SerializeField] private bool fallBackToFullTitle = true;

        [Tooltip("Participant-facing names per stage index; a stage without an entry uses its own display name.")]
        [SerializeField] private string[] stageLabels =
        {
            "Crankshaft installed", "Assemble piston 1", "Assemble piston 2",
            "Complete remaining pistons", "", "Install camshaft"
        };

        private float _timer;

        private void Awake()
        {
            if (workflow == null) workflow = FindAnyObjectByType<WorkflowState>(FindObjectsInactive.Include);
            if (stepManager == null) stepManager = FindAnyObjectByType<StepManager>(FindObjectsInactive.Include);
        }

        private void OnEnable()
        {
            if (session != null) session.OnStateChanged += Refresh;
            if (workflow != null) workflow.OnChanged += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (session != null) session.OnStateChanged -= Refresh;
            if (workflow != null) workflow.OnChanged -= Refresh;
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 0.5f) return;
            _timer = 0f;
            Refresh();
        }

        private struct Row { public string label; public int state; }   // 0 pending, 1 current, 2 done

        private void Refresh()
        {
            if (headingText != null) headingText.text = headingLabel;
            if (rowTexts == null || rowTexts.Count == 0) return;

            var rows = new List<Row>();

            bool placed = stepManager != null && stepManager.AnchorLocked;
            bool started = stepRunner != null && stepRunner.HasStarted;
            rows.Add(new Row { label = "Workspace placed", state = placed ? 2 : 1 });

            int count = stepRunner != null ? stepRunner.StepCount : 0;
            int current = stepRunner != null ? stepRunner.CurrentStepIndex : -1;
            bool finished = session != null && session.SequenceFinished;

            for (int i = 0; i < count; i++)
            {
                StepData step = stepRunner.GetStepAt(i);
                string label = i < stageLabels.Length ? stageLabels[i] : "";
                if (string.IsNullOrEmpty(label))
                {
                    // Unlabelled stages with nothing to do are hidden (the fourth piston stage is skipped).
                    if (step != null && step.NextEnabledActionIndex(0) < 0) continue;
                    label = DescribeStep(step, i);
                }

                int state;
                if (finished || (workflow != null && workflow.IsStageComplete(i)) || (workflow == null && i < current)) state = 2;
                else if (started && i == current) state = 1;
                else state = 0;

                rows.Add(new Row { label = label, state = state });
            }

            // Only one row is "current": the first non-done one once the workspace is placed.
            bool currentAssigned = false;
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.state == 1 && currentAssigned) r.state = 0;
                if (r.state == 1) currentAssigned = true;
                rows[i] = r;
            }

            for (int r = 0; r < rowTexts.Count; r++)
            {
                TextMeshProUGUI label = rowTexts[r];
                if (label == null) continue;

                if (r >= rows.Count) { label.text = ""; continue; }

                Row row = rows[r];
                switch (row.state)
                {
                    case 2:
                        label.text = Colour(donePrefix, MrTheme.Success) + Colour(row.label, MrTheme.TextSecondary);
                        label.fontStyle = FontStyles.Normal;
                        break;
                    case 1:
                        label.text = Colour(currentPrefix, MrTheme.Accent) + Colour(row.label, MrTheme.TextPrimary);
                        label.fontStyle = FontStyles.Bold;
                        break;
                    default:
                        label.text = Colour(pendingPrefix + row.label, MrTheme.TextMuted);
                        label.fontStyle = FontStyles.Normal;
                        break;
                }
            }
        }

        private static string Colour(string s, Color c)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{s}</color>";
        }

        private string DescribeStep(StepData step, int index)
        {
            if (step == null) return $"Step {index + 1}";
            if (!string.IsNullOrEmpty(step.displayName)) return step.displayName;
            if (fallBackToFullTitle && !string.IsNullOrEmpty(step.stepTitle))
                return step.stepTitle.Replace("\n", " ").Trim();
            return step.StepIdentifier;
        }
    }
}
