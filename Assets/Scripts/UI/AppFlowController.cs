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
using UnityEngine.UI;

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

        /// <summary>The authored Start button, cloned for runtime buttons so they match the UI.</summary>
        public GameObject ButtonTemplate
        {
            get
            {
                if (homePanel == null) return null;
                Transform t = homePanel.transform.Find("StartButton");
                return t != null ? t.gameObject : homePanel.GetComponentInChildren<Button>(true)?.gameObject;
            }
        }

        private bool _endButtonsBuilt;

        /// <summary>The running instruction card's panel (the world-space canvas the SDK ray already drives).</summary>
        public GameObject StepPanel { get { return stepPanel; } }

        /// <summary>The authored Back button of the card, cloned for the view-rotation buttons so they match.</summary>
        public GameObject BackButtonTemplate
        {
            get
            {
                if (stepPanel == null) return null;
                Transform t = stepPanel.transform.Find("BackButton");
                return t != null ? t.gameObject : null;
            }
        }

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
            BuildEndButtons();

            if (completeHeadline != null)
                completeHeadline.text = "Assembly complete";

            if (completeBody != null && session != null)
            {
                int steps = stepRunner != null ? stepRunner.StepCount : 0;
                int totalSec = Mathf.Max(0, Mathf.FloorToInt(session.OverallElapsedMs / 1000f));
                completeBody.text =
                    $"Total Errors: {session.ErrorsTotal}\n" +
                    $"Total Attempts: {session.AttemptsTotal}\n" +
                    $"Completion Time: {totalSec / 60:00}:{totalSec % 60:00}  ({steps} steps)\n\n" +
                    "Thank you. Restart Assembly starts a new session; Quit closes the app.";
            }
        }

        /// <summary>
        /// Restart Assembly: a clean reset of the whole session - every component pose, lock,
        /// consumed part, occupied target, support/action state and the log - by reloading the
        /// scene. The current log was closed on completion; the new run opens a NEW session
        /// file with a new session id. Quit: exits the application normally.
        /// </summary>
        private void BuildEndButtons()
        {
            if (_endButtonsBuilt || completePanel == null) return;
            _endButtonsBuilt = true;
            GameObject template = ButtonTemplate;

            MakeButton(template, "RestartButton", "Restart Assembly", new Vector2(-150f, 36f), new Vector2(270f, 70f), RestartAssembly);
            MakeButton(template, "QuitButton", "Quit", new Vector2(150f, 36f), new Vector2(200f, 70f), QuitApplication);
        }

        private void MakeButton(GameObject template, string name, string label, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go;
            if (template != null)
            {
                go = Instantiate(template, completePanel.transform);
                foreach (Transform extra in go.transform)
                    if (extra.GetComponent<TextMeshProUGUI>() == null) Destroy(extra.gameObject);
            }
            else
            {
                go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                go.transform.SetParent(completePanel.transform, false);
                var l = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                l.transform.SetParent(go.transform, false);
                var lrt = l.GetComponent<RectTransform>(); lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            }
            go.name = name;
            go.SetActive(true);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one;

            var text = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null) { text.text = label; text.enableAutoSizing = false; text.fontSize = 22f; text.alignment = TextAlignmentOptions.Center; }

            var button = go.GetComponent<Button>();
            if (button == null) button = go.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(onClick);
        }

        public void RestartAssembly()
        {
            Debug.Log("[Session] Restart Assembly: reloading the scene for a clean new session (new log file).");
            var logger = FindAnyObjectByType<AdaptiveAR.Logging.SessionLogger>(FindObjectsInactive.Include);
            if (logger != null) { logger.LogNote("restart_assembly"); logger.EndSession("restart"); }
            FeedbackPulse.CancelAll();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEngine.SceneManagement.SceneManager.LoadScene(scene.buildIndex >= 0 ? scene.buildIndex : 0);
        }

        public void QuitApplication()
        {
            Debug.Log("[Session] Quit requested by the participant.");
            var logger = FindAnyObjectByType<AdaptiveAR.Logging.SessionLogger>(FindObjectsInactive.Include);
            if (logger != null) { logger.LogNote("quit"); logger.EndSession("quit"); }
            Application.Quit();
        }

        public void Show(Screen screen)
        {
            Current = screen;

            SetActive(homePanel, screen == Screen.Home);
            SetActive(stepPanel, screen == Screen.Running);
            SetActive(completePanel, screen == Screen.Complete);

            // The task list is only meaningful while the sequence is running. When an
            // AdaptivePanelController owns the zones it is the single owner of that root.
            if (FindAnyObjectByType<AdaptivePanelController>(FindObjectsInactive.Include) == null)
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
