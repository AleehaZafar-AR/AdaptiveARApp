// File: OnboardingSequence.cs
// A short, deliberate introduction between anchoring and the first instruction.
//
// The first ghost used to appear abruptly the moment the marker was found. This
// gives the participant a welcome, an overview of the stages, an explanation of
// what the highlighted shapes mean, and an explicit "Begin Assembly" - so the task
// starts because they chose to start it.
//
// It is one panel with four content states rather than four panels, so there is
// only ever one thing on screen and nothing from a previous screen can survive.
//
// Participant-facing wording only: no L1/L2/L3, no research terminology.
//
// Workspace placement
// -------------------
// The first thing shown is not the welcome but the placement screen: the participant
// points at the desk and presses "Place Workspace". Only after the workspace exists
// does the introduction begin. The same panel and button are reused, so nothing new
// has to be wired in the scene.

using System;
using AdaptiveAR.MR;
using System.Collections.Generic;
using AdaptiveAR.Logging;
using AdaptiveAR.Steps;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.UI
{
    public class OnboardingSequence : MonoBehaviour
    {
        [Serializable]
        public class Screen
        {
            public string id;
            public string eyebrow;
            public string title;
            [TextArea(2, 6)] public string body;
            public string buttonLabel = "Next";

            [Tooltip("Fill the body from the authored stage list instead of the text above.")]
            public bool listStages;
        }

        [Header("References")]
        [SerializeField] private StepRunner stepRunner;
        [SerializeField] private SessionLogger logger;
        [SerializeField] private AppFlowController flow;

        [Tooltip("Optional. Found through StepManager at runtime when empty. When present and " +
                 "the workspace is not yet placed, a placement screen precedes the introduction.")]
        [SerializeField] private WorkspacePlacement placement;

        [Header("Placement screen")]
        [SerializeField] private string placementEyebrow = "SET UP";
        [SerializeField] private string placementTitle = "Place the workspace";
        [SerializeField] private string placementButtonLabel = "Place Workspace";

        [Header("Fields")]
        [SerializeField] private TextMeshProUGUI eyebrowText;
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI bodyText;
        [SerializeField] private Button advanceButton;
        [SerializeField] private TextMeshProUGUI advanceLabel;

        [Header("Screens")]
        [SerializeField]
        private List<Screen> screens = new List<Screen>
        {
            new Screen {
                id = "welcome", eyebrow = "MIXED REALITY GUIDANCE", title = "V8 Engine Assembly",
                body = "This system will guide you through assembling the engine, one step at a time.\n\n" +
                       "Work at your own pace. Nothing is timed against you.",
                buttonLabel = "Next"
            },
            new Screen {
                id = "overview", eyebrow = "WHAT YOU WILL DO", title = "Assembly overview",
                body = "", listStages = true, buttonLabel = "Next"
            },
            new Screen {
                id = "guidance", eyebrow = "HOW GUIDANCE WORKS", title = "Highlighted targets",
                body = "A translucent shape shows where the next part belongs.\n\n" +
                       "Pick up the part and move it onto the highlighted shape. The system confirms " +
                       "the placement before moving on, so you cannot get ahead of yourself.",
                buttonLabel = "Next"
            },
            new Screen {
                id = "begin", eyebrow = "READY", title = "Begin when you are ready",
                body = "Take a moment to get comfortable. The first instruction appears as soon as you begin.",
                buttonLabel = "Begin Assembly"
            }
        };

        public int Index { get; private set; } = -1;
        public bool IsFinished { get; private set; }

        /// <summary>True while the placement screen is up, before the introduction.</summary>
        public bool IsPlacingWorkspace { get; private set; }

        /// <summary>Raised when the participant presses Begin on the last screen.</summary>
        public event Action OnCompleted;

        private float _startedAtMs;

        private void OnEnable()
        {
            if (advanceButton != null)
                advanceButton.onClick.AddListener(Advance);
        }

        private void OnDisable()
        {
            if (advanceButton != null)
                advanceButton.onClick.RemoveListener(Advance);

            if (placement != null)
                placement.OnPlaced -= HandleWorkspacePlaced;
        }

        /// <summary>Restarts the sequence: placement first if needed, then screen one.</summary>
        public void Begin()
        {
            IsFinished = false;
            Index = -1;
            _startedAtMs = Time.time * 1000f;

            ResolvePlacement();

            if (placement != null && !placement.IsPlaced)
            {
                EnterPlacement();
                return;
            }

            Advance();
        }

        private void ResolvePlacement()
        {
            if (placement != null) return;

            var stepManager = FindAnyObjectByType<StepManager>(FindObjectsInactive.Include);
            if (stepManager != null) placement = stepManager.Placement;
        }

        private void EnterPlacement()
        {
            IsPlacingWorkspace = true;

            placement.OnPlaced -= HandleWorkspacePlaced;
            placement.OnPlaced += HandleWorkspacePlaced;
            placement.BeginPlacement();

            if (eyebrowText != null) eyebrowText.text = placementEyebrow;
            if (titleText != null) titleText.text = placementTitle;
            if (bodyText != null) bodyText.text = placement.StatusText;
            if (advanceLabel != null) advanceLabel.text = placementButtonLabel;

            if (logger != null) logger.LogOnboardingEnter(-1, "workspace_placement");
        }

        private void HandleWorkspacePlaced(bool reposition)
        {
            if (!IsPlacingWorkspace) return;

            placement.OnPlaced -= HandleWorkspacePlaced;
            IsPlacingWorkspace = false;

            // The workspace exists; the introduction starts on the same panel.
            Index = -1;
            Advance();
        }

        private void Update()
        {
            // Live status while placing, so "no surface yet" and "ready" are visible.
            if (IsPlacingWorkspace && placement != null && bodyText != null)
                bodyText.text = placement.StatusText;
        }

        public void Advance()
        {
            if (IsFinished) return;

            // On the placement screen the button confirms the placement instead of paging.
            // A failed confirm leaves the screen up with the reason in the body text.
            if (IsPlacingWorkspace)
            {
                if (placement != null) placement.TryConfirm();   // success arrives via OnPlaced
                return;
            }

            Index++;

            if (Index >= screens.Count)
            {
                Finish();
                return;
            }

            Render(screens[Index]);

            if (logger != null)
                logger.LogOnboardingEnter(Index, screens[Index].id);
        }

        private void Finish()
        {
            IsFinished = true;

            if (logger != null)
            {
                logger.LogOnboardingComplete(Time.time * 1000f - _startedAtMs);
                logger.LogAssemblyStart();
            }

            OnCompleted?.Invoke();

            // Handing over to the flow is what actually starts the task.
            if (flow != null) flow.BeginSession();
        }

        private void Render(Screen s)
        {
            if (eyebrowText != null) eyebrowText.text = s.eyebrow ?? "";
            if (titleText != null) titleText.text = s.title ?? "";

            if (bodyText != null)
                bodyText.text = s.listStages ? BuildStageList() : (s.body ?? "");

            if (advanceLabel != null)
                advanceLabel.text = string.IsNullOrEmpty(s.buttonLabel) ? "Next" : s.buttonLabel;
        }

        /// <summary>
        /// Reads the authored stages so the overview cannot drift out of step with the
        /// real sequence. Major stages only - substeps are deliberately not shown here.
        /// </summary>
        private string BuildStageList()
        {
            if (stepRunner == null || stepRunner.StepCount == 0)
                return "Crankshaft, pistons, then the camshaft.";

            var seen = new List<string>();
            for (int i = 0; i < stepRunner.StepCount; i++)
            {
                StepData stage = stepRunner.GetStepAt(i);
                if (stage == null) continue;

                string name = string.IsNullOrEmpty(stage.displayName) ? stage.stepTitle : stage.displayName;
                if (string.IsNullOrEmpty(name)) continue;

                // Collapse "Piston 1..4" into one line: an overview, not a checklist.
                string family = name.StartsWith("Piston") ? "Pistons" : name;
                if (!seen.Contains(family)) seen.Add(family);
            }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < seen.Count; i++)
                sb.Append(i + 1).Append(".  ").Append(seen[i]).Append('\n');

            return sb.ToString().TrimEnd();
        }
    }
}
