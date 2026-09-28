// File: AlignmentChip.cs
// The small status chip from the reference design: "Alignment needed" in amber
// while a part is outside tolerance, "Aligned" in restrained green once it is
// inside, and nothing at all when no placement is being judged.
//
// This is the only place amber appears in the participant interface, which is what
// keeps it meaningful as a warning rather than decoration.

using AdaptiveAR.Steps;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.UI
{
    public class AlignmentChip : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private StepValidator validator;
        [SerializeField] private StepRunner stepRunner;

        [Header("Output")]
        [SerializeField] private Image background;
        [SerializeField] private TextMeshProUGUI label;

        [Header("Copy")]
        [SerializeField] private string alignedText = "Aligned";
        [SerializeField] private string notAlignedText = "Alignment needed";

        [Header("Behaviour")]
        [Tooltip("Stay hidden until the operator has actually moved the part, so a chip does " +
                 "not sit there warning about a part nobody has touched.")]
        [SerializeField] private bool hideUntilMoved = true;

        [Tooltip("Metres of movement from the step's starting placement error before the chip appears.")]
        [SerializeField] private float movementThreshold = 0.05f;

        [SerializeField] private float refreshInterval = 0.12f;

        private float _timer;
        private float _errorAtStepStart = -1f;
        private StepData _watchedStep;

        private void OnEnable()
        {
            if (stepRunner != null)
                stepRunner.OnStepChanged += HandleStepChanged;

            SetShown(false);
        }

        private void OnDisable()
        {
            if (stepRunner != null)
                stepRunner.OnStepChanged -= HandleStepChanged;
        }

        private void HandleStepChanged(StepData step, int index, string reason)
        {
            _watchedStep = step;
            _errorAtStepStart = -1f;
            SetShown(false);
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < refreshInterval) return;
            _timer = 0f;

            Refresh();
        }

        private void Refresh()
        {
            if (validator == null || !validator.IsActive)
            {
                SetShown(false);
                return;
            }

            StepData step = _watchedStep ?? (stepRunner != null ? stepRunner.CurrentStep : null);
            if (step == null)
            {
                SetShown(false);
                return;
            }

            validator.GetCurrentError(out float posErr, out float rotErr);

            if (_errorAtStepStart < 0f)
                _errorAtStepStart = posErr;

            if (hideUntilMoved && Mathf.Abs(_errorAtStepStart - posErr) < movementThreshold)
            {
                SetShown(false);
                return;
            }

            bool ok = posErr <= step.positionToleranceMeters
                   && rotErr <= step.rotationToleranceDegrees;

            SetShown(true);
            Apply(ok);
        }

        private void Apply(bool aligned)
        {
            Color accent = MrTheme.AlignmentColor(aligned);

            if (background != null)
                background.color = MrTheme.WithAlpha(accent, 0.18f);

            if (label != null)
            {
                label.text = aligned ? alignedText : notAlignedText;
                label.color = accent;
            }
        }

        private void SetShown(bool shown)
        {
            if (background != null && background.enabled != shown)
                background.enabled = shown;

            if (label != null && label.enabled != shown)
                label.enabled = shown;
        }
    }
}
