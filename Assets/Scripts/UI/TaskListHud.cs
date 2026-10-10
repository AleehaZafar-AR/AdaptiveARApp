// File: TaskListHud.cs
// Drives the Overview (Steps / What's Next) panel rows. The panel's transform is
// never touched here; only the row text changes.
//
//   ✓ Workspace        ● Crankshaft        ○ Piston 1        ○ Piston 2
//   ○ Remaining pistons   ○ Camshaft holders   ○ Camshaft   ○ Complete
//
// Six text rows exist; when more rows are needed the earliest completed ones scroll
// off the top so the current row and what follows stay visible. State comes from
// WorkflowState (completed actions/stages) and StepManager (placement), never from
// a private counter. Visibility of the canvas is owned elsewhere; this component
// only reports it (one log line per change) so a vanished panel is diagnosable.

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
        [SerializeField] private WorkflowState workflow;
        [SerializeField] private StepManager stepManager;
        [SerializeField] private AdaptiveAR.Support.SupportLevelController supportLevel;

        [Header("Rows")]
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

        private float _timer;
        private bool _lastVisible;
        private bool _visibilityKnown;

        private void Awake()
        {
            if (workflow == null) workflow = FindAnyObjectByType<WorkflowState>(FindObjectsInactive.Include);
            if (stepManager == null) stepManager = FindAnyObjectByType<StepManager>(FindObjectsInactive.Include);
            if (supportLevel == null) supportLevel = FindAnyObjectByType<AdaptiveAR.Support.SupportLevelController>(FindObjectsInactive.Include);
        }

        private void OnEnable()
        {
            if (session != null) session.OnStateChanged += Refresh;
            if (workflow != null) workflow.OnChanged += Refresh;
            Refresh();
            ReportVisibility("enabled");
        }

        private void OnDisable()
        {
            if (session != null) session.OnStateChanged -= Refresh;
            if (workflow != null) workflow.OnChanged -= Refresh;
            ReportVisibility("disabled");
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 0.5f) return;
            _timer = 0f;
            Refresh();
            ReportVisibility("poll");
        }

        /// <summary>Logs once whenever the panel's effective visibility changes.</summary>
        private void ReportVisibility(string reason)
        {
            var group = GetComponent<CanvasGroup>();
            bool visible = gameObject.activeInHierarchy && (group == null || group.alpha > 0.01f);
            if (_visibilityKnown && visible == _lastVisible) return;
            _visibilityKnown = true;
            _lastVisible = visible;
            string level = supportLevel != null ? supportLevel.CurrentLevel.ToString() : "-";
            Debug.Log($"[Overview] visible={visible} level={level} reason={reason} activeSelf={gameObject.activeSelf} " +
                      $"activeInHierarchy={gameObject.activeInHierarchy} alpha={(group != null ? group.alpha.ToString("F2") : "n/a")} " +
                      $"parent={(transform.parent != null ? transform.parent.name : "<root>")} pos={transform.position}");
        }

        private struct Row { public string label; public int state; }   // 0 pending, 1 current, 2 done

        private void Refresh()
        {
            if (headingText != null) headingText.text = headingLabel;
            if (rowTexts == null || rowTexts.Count == 0) return;

            var rows = BuildRows();

            // Scroll completed rows off the top when there are more rows than fields,
            // keeping the current row visible with what follows it.
            int fields = rowTexts.Count;
            int first = 0;
            if (rows.Count > fields)
            {
                int current = rows.FindIndex(r => r.state == 1);
                if (current < 0) current = rows.Count - 1;
                first = Mathf.Clamp(current - 1, 0, rows.Count - fields);
            }

            for (int r = 0; r < fields; r++)
            {
                TextMeshProUGUI label = rowTexts[r];
                if (label == null) continue;
                int idx = first + r;
                if (idx >= rows.Count) { label.text = ""; continue; }

                Row row = rows[idx];
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

        private List<Row> BuildRows()
        {
            var rows = new List<Row>();
            bool placed = stepManager != null && stepManager.AnchorLocked;
            bool started = stepRunner != null && stepRunner.HasStarted;
            bool finished = session != null && session.SequenceFinished;
            int count = stepRunner != null ? stepRunner.StepCount : 0;
            int current = stepRunner != null ? stepRunner.CurrentStepIndex : -1;

            rows.Add(new Row { label = "Workspace", state = placed ? 2 : 1 });

            for (int i = 0; i < count; i++)
            {
                StepData step = stepRunner.GetStepAt(i);
                if (step == null) continue;
                bool stageDone = finished || (workflow != null && workflow.IsStageComplete(i));
                bool stageCurrent = started && i == current && !stageDone;

                // The camshaft stage is two rows: holders first, then the camshaft itself.
                if (step.StepIdentifier == "step_06_camshaft")
                {
                    bool holdersDone = stageDone || AllComplete(step, a => a.partKey.StartsWith("part.engineBlockSep"));
                    bool camDone = stageDone || AllComplete(step, a => a.partKey == "part.camshaft");
                    bool onHolders = stageCurrent && !holdersDone;
                    rows.Add(new Row { label = "Camshaft holders", state = holdersDone ? 2 : (onHolders ? 1 : 0) });
                    rows.Add(new Row { label = "Camshaft", state = camDone ? 2 : (stageCurrent && holdersDone ? 1 : 0) });
                    continue;
                }

                if (step.NextEnabledActionIndex(0) < 0 && !stageDone) continue;   // nothing to do here: hidden
                string label = StageLabel(step, i);
                rows.Add(new Row { label = label, state = stageDone ? 2 : (stageCurrent ? 1 : 0) });
            }

            rows.Add(new Row { label = "Complete", state = finished ? 2 : 0 });

            // Exactly one current row: the first that is neither done nor pending-before-start.
            bool currentAssigned = false;
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.state == 1) { if (currentAssigned) r.state = 0; currentAssigned = true; }
                rows[i] = r;
            }
            if (!currentAssigned && !finished)
            {
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i].state == 0) { Row r = rows[i]; r.state = 1; rows[i] = r; break; }
            }
            return rows;
        }

        private bool AllComplete(StepData step, System.Predicate<AssemblyAction> match)
        {
            if (step == null || step.actions == null || workflow == null) return false;
            bool any = false;
            foreach (AssemblyAction a in step.actions)
            {
                if (a == null || !a.enabled || string.IsNullOrEmpty(a.partKey) || !match(a)) continue;
                any = true;
                if (!workflow.IsActionComplete(step, a)) return false;
            }
            return any;
        }

        private string StageLabel(StepData step, int index)
        {
            switch (step.StepIdentifier)
            {
                case "step_01_crankshaft": return "Crankshaft";
                case "step_02_piston001": return "Piston 1";
                case "step_03_piston002": return "Piston 2";
                case "step_04_piston003": return "Remaining pistons";
                default: return DescribeStep(step, index);
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
