// File: StatusHud.cs
// Drives the existing StatusCanvas, which was laid out but had no script behind it.
//
// This is also the on-device state readout CLAUDE.md 4.3 asks for: step, support
// level, elapsed time, errors and progress, all visible in the headset without a
// cable or the Editor.
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

        [Header("Text Fields (all optional)")]
        [Tooltip("StatusCanvas/TopPanel/CaptionText - shows the support level.")]
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
            UpdateSupportLevel();
            UpdateTime();
            UpdateErrors();
            UpdateProgress();
            UpdatePlacementError();
        }

        private void UpdateSupportLevel()
        {
            if (supportLevelText == null) return;

            if (supportLevel == null)
            {
                supportLevelText.text = "Support --";
                return;
            }

            supportLevelText.text = "Support " + FriendlyLevel(supportLevel.CurrentLevel);
        }

        private static string FriendlyLevel(SupportLevel level)
        {
            switch (level)
            {
                case SupportLevel.L1_Minimal: return "L1 Minimal";
                case SupportLevel.L2_Guided: return "L2 Guided";
                case SupportLevel.L3_Assisted: return "L3 Assisted";
                default: return level.ToString();
            }
        }

        private void UpdateTime()
        {
            if (timeValueText == null) return;

            if (session == null)
            {
                timeValueText.text = "--:--";
                return;
            }

            float ms = showOverallTime ? session.OverallElapsedMs : session.StepElapsedMs;
            int total = Mathf.Max(0, Mathf.FloorToInt(ms / 1000f));
            timeValueText.text = $"{total / 60:00}:{total % 60:00}";
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

            if (progressValueText != null)
            {
                if (count == 0) progressValueText.text = "--";
                else if (finished) progressValueText.text = $"{count} / {count}";
                else if (index < 0) progressValueText.text = $"0 / {count}";
                else progressValueText.text = $"{index + 1} / {count}";
            }

            // Fraction of COMPLETED steps: being on step index means index steps are done.
            float fraction = 0f;
            if (count > 0)
                fraction = finished ? 1f : Mathf.Clamp01(Mathf.Max(0, index) / (float)count);

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
