// File: ResearcherHud.cs
// The researcher / debug readout, hidden from the participant by default.
//
// Everything that is instrumentation rather than instruction lives here: support
// level, timers, validation error, attempt and error counts, logging status, the
// physiological stream and - once it exists - the AI decision state. Keeping it
// off the participant's field of view is a design requirement; keeping it one
// button press away is an on-device debugging requirement (CLAUDE.md 4.3).
//
// Toggle: left controller Start/Menu by default, plus F1 in the Editor. Button.One
// (CV debug quad) and B/Y (support level) are deliberately left alone.

using System.Text;
using AdaptiveAR.Decision;
using AdaptiveAR.Logging;
using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using TMPro;
using UnityEngine;

namespace AdaptiveAR.UI
{
    public class ResearcherHud : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private AssemblySessionController session;
        [SerializeField] private StepRunner stepRunner;
        [SerializeField] private SupportLevelController supportLevel;
        [SerializeField] private StepValidator validator;
        [SerializeField] private SessionLogger logger;

        [Header("Output")]
        [Tooltip("The root that is shown and hidden. Defaults to this GameObject.")]
        [SerializeField] private GameObject panelRoot;

        [Tooltip("Single text block holding the whole readout.")]
        [SerializeField] private TextMeshProUGUI readoutText;

        [Header("Visibility")]
        [Tooltip("Participants should not see this, so it starts hidden.")]
        [SerializeField] private bool visibleOnStart = true;

        [Tooltip("Controller button that toggles the HUD. Start/Menu avoids every binding " +
                 "already in use: Button.One is the CV debug quad, B/Y change support level, " +
                 "and the thumbstick nudges anchor calibration.")]
        [SerializeField] private OVRInput.RawButton toggleButton = OVRInput.RawButton.Start;

        [SerializeField] private KeyCode editorToggleKey = KeyCode.F1;

        [Header("Refresh")]
        [SerializeField] private float refreshInterval = 0.2f;

        public bool IsVisible { get; private set; }

        private float _timer;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private StepManager _stepManager;

        private void Awake()
        {
            if (panelRoot == null) panelRoot = gameObject;
        }

        private void Start()
        {
            SetVisible(visibleOnStart);
        }

        private void Update()
        {
            if (OVRInput.GetDown(toggleButton) || Input.GetKeyDown(editorToggleKey))
                Toggle();

            if (!IsVisible) return;

            _timer += Time.deltaTime;
            if (_timer < refreshInterval) return;
            _timer = 0f;

            Refresh();
        }

        public void Toggle()
        {
            SetVisible(!IsVisible);
        }

        public void SetVisible(bool visible)
        {
            IsVisible = visible;

            if (panelRoot != null && panelRoot != gameObject)
                panelRoot.SetActive(visible);
            else if (readoutText != null)
                readoutText.enabled = visible;

            if (visible) Refresh();
        }

        private void Refresh()
        {
            if (readoutText == null) return;

            _sb.Clear();

            // --- task state ---
            int idx = stepRunner != null ? stepRunner.CurrentStepIndex : -1;
            int count = stepRunner != null ? stepRunner.StepCount : 0;
            StepData step = stepRunner != null ? stepRunner.CurrentStep : null;

            _sb.Append("<b>STEP</b>  ")
               .Append(idx < 0 ? "-" : (idx + 1).ToString()).Append(" / ").Append(count)
               .Append("   ").Append(step != null ? step.StepIdentifier : "-").Append('\n');

            _sb.Append("<b>SUPPORT</b>  ")
               .Append(supportLevel != null ? supportLevel.CurrentLevel.ToString() : "-")
               .Append('\n');

            // --- timing ---
            if (session != null)
            {
                _sb.Append("<b>TIME</b>  step ").Append((session.StepElapsedMs / 1000f).ToString("F1"))
                   .Append("s   total ").Append((session.OverallElapsedMs / 1000f).ToString("F1"))
                   .Append("s\n");

                _sb.Append("<b>ATTEMPTS</b>  ").Append(session.AttemptsOnStep)
                   .Append("   <b>ERRORS</b>  ").Append(session.ErrorsOnStep)
                   .Append(" step / ").Append(session.ErrorsTotal).Append(" total\n");

                _sb.Append("<b>SUPPORT CHANGES</b>  ").Append(session.SupportChangesTotal).Append('\n');
            }

            // --- workspace registration ---
            if (_stepManager == null) _stepManager = FindAnyObjectByType<StepManager>(FindObjectsInactive.Include);
            _sb.Append("<b>WORKSPACE</b>  ");
            if (_stepManager != null && _stepManager.Placement != null)
            {
                var p = _stepManager.Placement;
                _sb.Append(p.IsPlacing ? Colorize("placing", MrTheme.Warning)
                           : p.IsPlaced ? Colorize("placed", MrTheme.Success)
                           : Colorize("not placed", MrTheme.TextMuted))
                   .Append("  ").Append(p.ActiveProvider.ToString());
                if (p.IsPlaced)
                    _sb.Append($"  conf {p.LastNormalConfidence:F2}");
            }
            else
            {
                _sb.Append(_stepManager != null && _stepManager.AnchorLocked ? "marker locked" : "marker");
            }
            _sb.Append('\n');

            // --- live placement error ---
            if (validator != null && validator.IsActive)
            {
                validator.GetCurrentError(out float pos, out float rot);
                float posTol = validator.PositionTolerance;
                float rotTol = validator.RotationTolerance;
                bool inTol = pos <= posTol && rot <= rotTol;

                _sb.Append("<b>PLACEMENT</b>  ")
                   .Append(Colorize($"{pos * 100f:F1} cm / {rot:F0}°",
                                    MrTheme.AlignmentColor(inTol)));

                _sb.Append("   tol ").Append((posTol * 100f).ToString("F1"))
                   .Append(" cm / ").Append(rotTol.ToString("F0")).Append('°');

                _sb.Append("   ").Append(validator.CurrentSymmetry.ToString());

                if (validator.IsAssisting)
                    _sb.Append("   ").Append(Colorize("assist", MrTheme.Accent));

                _sb.Append('\n');
            }
            else
            {
                _sb.Append("<b>PLACEMENT</b>  inactive\n");
            }

            // --- physiology, explicitly absent until a source exists ---
            PhysiologicalSample phys = session != null ? session.LatestPhysiological : null;
            _sb.Append("<b>HRV</b>  ");
            if (phys == null || phys.IsEmpty)
            {
                _sb.Append(Colorize("no source", MrTheme.TextMuted));
            }
            else
            {
                _sb.Append(phys.RmssdMs.HasValue ? $"RMSSD {phys.RmssdMs.Value:F0} ms" : "RMSSD -");
                if (phys.HeartRateBpm.HasValue) _sb.Append($"   HR {phys.HeartRateBpm.Value:F0}");
            }
            _sb.Append('\n');

            // --- decision layer, not implemented yet ---
            _sb.Append("<b>AI</b>  ").Append(Colorize("no provider", MrTheme.TextMuted)).Append('\n');

            // --- logging ---
            _sb.Append("<b>LOG</b>  ");
            if (logger != null && logger.IsOpen)
                _sb.Append(Colorize("recording", MrTheme.Success)).Append("  ").Append(logger.SessionId);
            else
                _sb.Append(Colorize("not recording", MrTheme.Warning));

            readoutText.text = _sb.ToString();
        }

        private static string Colorize(string s, Color c)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{s}</color>";
        }
    }
}
