// File: StatusHud.cs
// Drives the existing StatusCanvas, which was laid out but had no script behind it.
//
// Participant-facing only: stage, elapsed time, errors, attempts and progress.
// Support level, physiology and the decision layer are research instrumentation
// and live on the ResearcherHud (the CLAUDE.md 4.3 readout), never here.
//
// Every field is optional. Leave one unassigned and that part of the HUD is simply
// skipped, so the same component works against a partially wired canvas.

using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.UI
{
    public class StatusHud : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private AssemblySessionController session;
        [SerializeField] private StepRunner stepRunner;
        [SerializeField] private SupportLevelController supportLevel;
        [SerializeField] private StepValidator validator;

        [Tooltip("Authoritative progress. Without it the panel counted the step you are ON " +
                 "as a step you had FINISHED, so it read 1/6 before anything was done.")]
        [SerializeField] private WorkflowState workflow;

        [Header("Text Fields (all optional)")]
        [Tooltip("Participant-facing stage name. Support level is research instrumentation " +
                 "and is deliberately NOT shown here.")]
        [SerializeField] private TextMeshProUGUI supportLevelText;

        [Tooltip("StatusCanvas/TimerText/TimeValue - time on the current step.")]
        [SerializeField] private TextMeshProUGUI timeValueText;

        [Tooltip("StatusCanvas/ErrorText/ErrorValue - failed placement attempts.")]
        [SerializeField] private TextMeshProUGUI errorValueText;

        [Tooltip("StatusCanvas/ProgressText/ProgressValue - step x of n.")]
        [SerializeField] private TextMeshProUGUI progressValueText;

        [Tooltip("Optional live placement error, useful while calibrating tolerances.")]
        [SerializeField] private TextMeshProUGUI placementErrorText;

        [Header("Progress Bar (optional)")]
        [Tooltip("Assign either a Slider, or the Fill Area RectTransform below.")]
        [SerializeField] private Slider progressSlider;

        [Tooltip("Scaled horizontally from 0..1 when no Slider is assigned.")]
        [SerializeField] private RectTransform progressFill;

        [Header("Display")]
        [Tooltip("Show total session time instead of time on the current step.")]
        [SerializeField] private bool showOverallTime = false;

        [Tooltip("Seconds between refreshes. The HUD does not need to update every frame.")]
        [SerializeField] private float refreshInterval = 0.1f;

        private float _timer;
        private TextMeshProUGUI _stepValue, _attemptsValue;
        private bool _resolved;

        /// <summary>
        /// The StatusCanvas was laid out by hand and some of its value fields were never
        /// wired (ErrorValue - the "Placement Retries" that never updated). Resolve every
        /// field by name under the panel, retitle the labels for the discrete attempt/error
        /// model, and add the one extra participant row (attempts this step).
        /// </summary>
        private void ResolveFields()
        {
            if (_resolved) return;
            _resolved = true;

            Transform panel = transform.Find("Panel");
            if (panel == null) return;
            TextMeshProUGUI T(string n) { Transform t = panel.Find(n); return t != null ? t.GetComponent<TextMeshProUGUI>() : null; }

            if (supportLevelText == null) supportLevelText = T("SupportValue");
            if (progressValueText == null) progressValueText = T("ProgressValue");
            if (timeValueText == null) timeValueText = T("TimeValue");
            if (errorValueText == null) errorValueText = T("ErrorValue");
            _stepValue = supportLevelText;

            var supportLabel = T("SupportLabel"); if (supportLabel != null) supportLabel.text = "CURRENT STEP";
            var timeLabel = T("TimeLabel");       if (timeLabel != null) timeLabel.text = "ELAPSED TIME  (total / step)";
            var errorLabel = T("ErrorLabel");     if (errorLabel != null) errorLabel.text = "TOTAL ERRORS";

            // One more row below the errors, cloned from the error row so they match.
            var errorValueRt = errorValueText != null ? errorValueText.rectTransform : null;
            if (errorLabel != null && errorValueRt != null)
                _attemptsValue = CloneRow(panel, errorLabel, errorValueText, "ATTEMPTS THIS STEP", 64f);
            Debug.Log($"[StatusHud] fields resolved: step={(supportLevelText != null)} progress={(progressValueText != null)} time={(timeValueText != null)} errors={(errorValueText != null)} attempts={(_attemptsValue != null)}");
        }

        private static TextMeshProUGUI CloneRow(Transform panel, TextMeshProUGUI label, TextMeshProUGUI value, string title, float down)
        {
            var l = Instantiate(label, panel); l.name = title.Replace(" ", "") + "Label"; l.text = title;
            l.rectTransform.anchoredPosition = label.rectTransform.anchoredPosition + new Vector2(0f, -down);
            var v = Instantiate(value, panel); v.name = title.Replace(" ", "") + "Value"; v.text = "-";
            v.rectTransform.anchoredPosition = value.rectTransform.anchoredPosition + new Vector2(0f, -down);
            return v;
        }

        private void OnEnable()
        {
            ResolveFields();
            if (session != null)
                session.OnStateChanged += Refresh;

            Refresh();
        }

        private void OnDisable()
        {
            if (session != null)
                session.OnStateChanged -= Refresh;
        }

        private void Update()
        {
            // Timers move continuously, so the clock still needs a periodic tick.
            _timer += Time.deltaTime;
            if (_timer < refreshInterval) return;

            _timer = 0f;
            Refresh();
        }

        private void Refresh()
        {
            ResolveFields();
            UpdateSupportLevel();
            UpdateTime();
            UpdateErrors();
            UpdateProgress();
            UpdatePlacementError();
            UpdateExtraRows();
        }

        /// <summary>Discrete attempts on the current step.</summary>
        private void UpdateExtraRows()
        {
            if (_attemptsValue != null)
                _attemptsValue.text = session != null ? session.AttemptsOnStep.ToString() : "-";
        }

        /// <summary>
        /// Shows the current STAGE, not the support level. The level is a research variable;
        /// naming it to the participant would tell them they are being adapted to.
        /// </summary>
        private void UpdateSupportLevel()
        {
            if (supportLevelText == null) return;

            StepData stage = workflow != null ? workflow.CurrentStage
                           : (stepRunner != null ? stepRunner.CurrentStep : null);

            if (stage == null) { supportLevelText.text = ""; return; }

            supportLevelText.text = string.IsNullOrEmpty(stage.displayName)
                ? stage.stepTitle : stage.displayName;
        }

        private void UpdateTime()
        {
            if (timeValueText == null) return;

            if (session == null)
            {
                timeValueText.text = "--:--";
                return;
            }

            int total = Mathf.Max(0, Mathf.FloorToInt(session.OverallElapsedMs / 1000f));
            int step = Mathf.Max(0, Mathf.FloorToInt(session.StepElapsedMs / 1000f));
            timeValueText.text = $"{total / 60:00}:{total % 60:00}  /  {step / 60:00}:{step % 60:00}";
        }

        private void UpdateErrors()
        {
            if (errorValueText == null) return;

            errorValueText.text = session != null ? session.ErrorsTotal.ToString() : "--";
        }

        private void UpdateProgress()
        {
            int count = stepRunner != null ? stepRunner.StepCount : 0;
            int index = stepRunner != null ? stepRunner.CurrentStepIndex : -1;

            bool finished = session != null && session.SequenceFinished;

            // COMPLETED stages, not the stage you happen to be on. Being on stage 1 with
            // nothing validated is 0 of 6, not 1 of 6.
            int completed = 0;
            if (workflow != null)
            {
                for (int i = 0; i < count; i++)
                    if (workflow.IsStageComplete(i)) completed++;
            }
            else if (index > 0)
            {
                completed = index;
            }

            if (finished) completed = count;

            if (progressValueText != null)
                progressValueText.text = count == 0 ? "--" : $"{completed} / {count}";

            // Fraction is derived from validated work, including part-finished stages.
            float fraction = 0f;
            if (count > 0)
                fraction = workflow != null
                    ? workflow.OverallProgress
                    : Mathf.Clamp01(completed / (float)count);

            if (progressSlider != null)
            {
                progressSlider.minValue = 0f;
                progressSlider.maxValue = 1f;
                progressSlider.value = fraction;
            }
            else if (progressFill != null)
            {
                Vector3 s = progressFill.localScale;
                progressFill.localScale = new Vector3(fraction, s.y, s.z);
            }
        }

        private void UpdatePlacementError()
        {
            if (placementErrorText == null) return;

            if (validator == null || !validator.IsActive)
            {
                placementErrorText.text = "";
                return;
            }

            validator.GetCurrentError(out float pos, out float rot);
            placementErrorText.text = $"{pos * 100f:F1} cm  {rot:F0}°";
        }
    }
}
