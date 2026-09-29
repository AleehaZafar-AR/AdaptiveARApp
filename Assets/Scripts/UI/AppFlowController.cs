// File: AppFlowController.cs
// Screen flow: Home -> Running -> Complete.
//
// Previously the app dropped straight into step one with the Begin control buried
// in a legacy panel, so there was no way in. This owns which panel is on screen
// and nothing else: it does not touch the step index, the support level or the
// anchor.
//
// StepManager keeps its own Begin behaviour. The start button is wired to BOTH
// StepManager (which starts marker detection) and this controller (which swaps
// panels), so neither has to know about the other.

using System;
using AdaptiveAR.Steps;
using TMPro;
using UnityEngine;

namespace AdaptiveAR.UI
{
    public class AppFlowController : MonoBehaviour
    {
        public enum Screen
        {
            Home = 0,
            Running = 1,
            Complete = 2
        }

        [Header("Panels")]
        [SerializeField] private GameObject homePanel;
        [SerializeField] private GameObject stepPanel;
        [SerializeField] private GameObject completePanel;

        [Header("Secondary surfaces")]
        [Tooltip("Task list canvas. Hidden on the home screen so the first thing seen is one panel, not three.")]
        [SerializeField] private GameObject taskListRoot;

        [Header("Onboarding")]
        [Tooltip("Runs on the home panel. BeginSession is called by its final screen, so the " +
                 "task starts because the participant chose to start it.")]
        [SerializeField] private OnboardingSequence onboarding;

        [Header("Sources")]
        [Tooltip("Told to start marker detection when the participant begins. Wiring the " +
                 "button directly to StepManager also hid it, which broke the later " +
                 "onboarding screens.")]
        [SerializeField] private StepManager stepManager;

        [SerializeField] private AssemblySessionController session;
        [SerializeField] private StepRunner stepRunner;

        [Header("Completion text")]
        [SerializeField] private TextMeshProUGUI completeHeadline;
        [SerializeField] private TextMeshProUGUI completeBody;

        [Header("Fields cleared on Home")]
        [Tooltip("Step label is blanked on the home screen so it cannot show STEP 01 / 06 " +
                 "before the sequence has begun.")]
        [SerializeField] private TextMeshProUGUI stepLabel;

        public Screen Current { get; private set; } = Screen.Home;

        /// <summary>Raised when the participant presses Start.</summary>
        public event Action OnStarted;

        private void OnEnable()
        {
            if (session != null)
                session.OnSessionComplete += HandleSessionComplete;
        }

        private void OnDisable()
        {
            if (session != null)
                session.OnSessionComplete -= HandleSessionComplete;
        }

        private void Start()
        {
            Show(Screen.Home);

            // Begin the introduction as soon as the home screen is up.
            if (onboarding != null) onboarding.Begin();
        }

        /// <summary>Hooked to the Start button. StepManager's own listener runs alongside this.</summary>
        public void BeginSession()
        {
            if (Current != Screen.Home) return;

            Show(Screen.Running);

            // Marker detection begins here, not on the button press, so the shared
            // onboarding button survives every screen.
            if (stepManager != null) stepManager.StartSessionExternal();

            OnStarted?.Invoke();
        }

        /// <summary>Returns to the home screen. Researcher control, e.g. between participants.</summary>
        public void ReturnHome()
        {
            Show(Screen.Home);
        }

        private void HandleSessionComplete()
        {
            Show(Screen.Complete);

            if (completeHeadline != null)
                completeHeadline.text = "Assembly complete";

            if (completeBody != null && session != null)
            {
                int steps = stepRunner != null ? stepRunner.StepCount : 0;
                completeBody.text =
                    $"{steps} steps finished in {session.OverallElapsedMs / 60000f:F1} min.\n" +
                    $"{session.ErrorsTotal} placement error{(session.ErrorsTotal == 1 ? "" : "s")}.\n\n" +
                    "Thank you. You can remove the headset.";
            }
        }

        public void Show(Screen screen)
        {
            Current = screen;

            SetActive(homePanel, screen == Screen.Home);
            SetActive(stepPanel, screen == Screen.Running);
            SetActive(completePanel, screen == Screen.Complete);

            // The task list is only meaningful while the sequence is running.
            SetActive(taskListRoot, screen == Screen.Running);

            if (screen == Screen.Home && stepLabel != null)
                stepLabel.text = "";
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active)
                go.SetActive(active);
        }
    }
}
