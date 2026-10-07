// File: ParticipantCard.cs
// The single participant-facing instruction card.
//
// One component owns every field on the card, so entering a new action clears the
// old one in one place. The previous design had StepManager, StepPresenter and the
// HUDs each writing their own fields, which is why text from a finished state could
// survive into the next one.
//
// It renders ONE action at a time - "Place the crankshaft", "2 of 3" - rather than a
// paragraph, which is what lets L3 decompose a stage without overflowing.
//
// It also owns the Next control's enabled state: Next is only interactive when the
// session says progression is permitted, so the button never implies an action that
// is unavailable.

using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.UI
{
    public class ParticipantCard : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private AssemblySessionController session;
        [SerializeField] private WorkflowState workflow;
        [SerializeField] private StepRunner stepRunner;
        [SerializeField] private SupportLevelController supportLevel;
        [SerializeField] private StepValidator validator;

        [Header("Card fields")]
        [Tooltip("Stage name, e.g. \"Crankshaft\".")]
        [SerializeField] private TextMeshProUGUI stageText;

        [Tooltip("Action counter within the stage, e.g. \"2 of 3\".")]
        [SerializeField] private TextMeshProUGUI counterText;

        [Tooltip("The one instruction to act on now.")]
        [SerializeField] private TextMeshProUGUI instructionText;

        [Tooltip("Supporting detail. Shown only at the richer support levels.")]
        [SerializeField] private TextMeshProUGUI detailText;

        [Tooltip("Feedback line: amber correction, green confirmation.")]
        [SerializeField] private TextMeshProUGUI feedbackText;

        [Header("Controls")]
        [SerializeField] private Button nextButton;
        [SerializeField] private TextMeshProUGUI nextLabel;

        [Header("Behaviour")]
        [Tooltip("Seconds a success confirmation stays on screen before the card moves on.")]
        [SerializeField] private float successHoldSeconds = 1.2f;

        [Header("Single presentation")]
        [Tooltip("Siblings of the instruction field left over from the earlier layout. They are " +
                 "switched off at start so only this card's fields render: one instruction, " +
                 "one progress line, one Continue label.")]
        [SerializeField] private string[] legacySiblingNames = { "Eyebrow", "StepLabel", "CaptionText", "StatusLine" };

        [Tooltip("Guarantee the instruction field can show two wrapped lines inside the panel " +
                 "instead of truncating a long sentence. Fields below it move down by the " +
                 "extra height.")]
        [SerializeField] private bool ensureInstructionFits = true;

        [Tooltip("Smallest font size the instruction may shrink to before wrapping to a second line.")]
        [SerializeField] private float instructionMinFontSize = 18f;

        [SerializeField] private string wrongPartMessage = "That is not the required component";

        [SerializeField] private float refreshInterval = 0.12f;

        private float _timer;
        private float _successUntil;
        private string _lastFeedback;
        private bool _feedbackIsSuccess;
        private bool _feedbackIsWrongPart;
        private string _lastActionKey;

        private void Awake()
        {
            RemoveLegacyPresentation();
            if (ensureInstructionFits) EnsureInstructionFits();
        }

        private void OnEnable()
        {
            if (session != null) session.OnStateChanged += Refresh;
            if (workflow != null) workflow.OnChanged += Refresh;
            if (validator != null)
            {
                validator.OnAttemptEvaluated += HandleAttempt;
                validator.OnPartGrabbed += HandleEligibleGrabbed;
                validator.OnWrongPartGrabbed += HandleWrongGrabbed;
                validator.OnWrongPartReleased += HandleWrongReleased;
            }

            Refresh();
        }

        private void OnDisable()
        {
            if (session != null) session.OnStateChanged -= Refresh;
            if (workflow != null) workflow.OnChanged -= Refresh;
            if (validator != null)
            {
                validator.OnAttemptEvaluated -= HandleAttempt;
                validator.OnPartGrabbed -= HandleEligibleGrabbed;
                validator.OnWrongPartGrabbed -= HandleWrongGrabbed;
                validator.OnWrongPartReleased -= HandleWrongReleased;
            }
        }

        /// <summary>
        /// The panel still carries the fields of the earlier layout (eyebrow, step label,
        /// caption, status line) and a second label inside the Continue button. With two
        /// layouts active everything rendered twice. This card is the single presentation,
        /// so the others are switched off here rather than merely blanked.
        /// </summary>
        private void RemoveLegacyPresentation()
        {
            Transform panel = instructionText != null ? instructionText.transform.parent : transform;
            if (panel == null) return;

            int removed = 0;
            foreach (Transform child in panel)
            {
                if (child == null) continue;
                if (System.Array.IndexOf(legacySiblingNames, child.name) < 0) continue;
                if (child.gameObject.activeSelf) { child.gameObject.SetActive(false); removed++; }
            }

            // One label per button: anything else inside the button is the old caption.
            removed += KeepOnlyLabel(nextButton != null ? nextButton.transform : null, nextLabel);

            if (removed > 0)
                Debug.Log($"[ParticipantCard] {removed} legacy presentation object(s) switched off; this card is the single writer.");
        }

        private static int KeepOnlyLabel(Transform button, TextMeshProUGUI keep)
        {
            if (button == null) return 0;
            int n = 0;
            foreach (TextMeshProUGUI t in button.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (t == null || t == keep) continue;
                if (t.gameObject.activeSelf) { t.gameObject.SetActive(false); n++; }
            }
            return n;
        }

        /// <summary>
        /// A long instruction was truncated because its field is one line tall with
        /// Truncate overflow. Allow wrapping, let the font shrink a little, and give the
        /// field room for two lines - moving only the fields below it, inside the panel.
        /// </summary>
        private void EnsureInstructionFits()
        {
            if (instructionText == null) return;

            instructionText.overflowMode = TextOverflowModes.Overflow;
            instructionText.textWrappingMode = TextWrappingModes.Normal;
            instructionText.enableAutoSizing = true;
            instructionText.fontSizeMin = Mathf.Min(instructionText.fontSizeMin, instructionMinFontSize);

            var rt = instructionText.rectTransform;
            float lineHeight = instructionText.fontSizeMin * 1.2f;
            float needed = lineHeight * 2f + 6f;
            float current = rt.sizeDelta.y;
            if (current >= needed) return;

            float delta = needed - current;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, needed);

            ShiftDown(detailText != null ? detailText.rectTransform : null, delta);
            ShiftDown(feedbackText != null ? feedbackText.rectTransform : null, delta);

            Debug.Log($"[ParticipantCard] Instruction field height {current:F0} -> {needed:F0} px so two lines fit; detail and feedback moved down {delta:F0} px.");
        }

        private static void ShiftDown(RectTransform rt, float px)
        {
            if (rt == null) return;
            Vector2 p = rt.anchoredPosition;
            rt.anchoredPosition = new Vector2(p.x, p.y - px);
        }

        // ---------------- feedback lifecycle: current state, not history ----------------

        private void HandleEligibleGrabbed(string key, Transform part)
        {
            // The right kind of part is in the hand: any correction from before is stale.
            if (!_feedbackIsSuccess) ClearFeedback();
        }

        private void HandleWrongGrabbed(string key, Transform part)
        {
            Show(feedbackText, wrongPartMessage, MrTheme.Warning);
            _feedbackIsSuccess = false;
            _feedbackIsWrongPart = true;
            _successUntil = 0f;
        }

        private void HandleWrongReleased(string key, Transform part)
        {
            if (_feedbackIsWrongPart) ClearFeedback();
        }

        private void ClearFeedback()
        {
            Show(feedbackText, "", MrTheme.TextSecondary);
            _feedbackIsSuccess = false;
            _feedbackIsWrongPart = false;
            _successUntil = 0f;
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < refreshInterval) return;
            _timer = 0f;
            Refresh();
        }

        private void HandleAttempt(bool success, float posErr, float rotErr, string trigger)
        {
            if (success)
            {
                Show(feedbackText, "Placed correctly", MrTheme.Success);
                _feedbackIsSuccess = true;
                _feedbackIsWrongPart = false;
                _successUntil = Time.time + successHoldSeconds;
                return;
            }

            _feedbackIsSuccess = false;
            _feedbackIsWrongPart = false;

            // Specific enough to act on. "Alignment needed" tells the participant nothing.
            string msg;
            switch (validator != null ? validator.LastRejectReason : RejectReason.None)
            {
                case RejectReason.WrongComponent: msg = wrongPartMessage; break;
                case RejectReason.TooFar: msg = "Move it closer to the highlighted target"; break;
                case RejectReason.WrongRotation: msg = "Rotate the part to match the target"; break;
                default: msg = "Adjust the placement"; break;
            }

            Show(feedbackText, msg, MrTheme.Warning);
            _successUntil = 0f;
        }

        private void Refresh()
        {
            bool assembling = session != null && session.SessionActive
                              && stepRunner != null && stepRunner.HasStarted
                              && !(session.SequenceFinished);

            if (!assembling)
            {
                ClearCard();
                return;
            }

            StepData stage = workflow != null ? workflow.CurrentStage : stepRunner.CurrentStep;
            AssemblyAction action = workflow != null ? workflow.CurrentAction : null;

            // --- entering a new action clears the previous action's correction feedback. A
            // --- success confirmation is allowed to finish its short hold, then goes too.
            string actionKey = (stage != null ? stage.StepIdentifier : "") + "/" + (action != null ? action.Id : "");
            if (actionKey != _lastActionKey)
            {
                _lastActionKey = actionKey;
                if (!_feedbackIsSuccess) ClearFeedback();
            }

            // --- one compact progress line: stage position, stage name, action position.
            // --- Every number comes from WorkflowState, so it cannot disagree with reality.
            if (stageText != null)
            {
                string stageName = stage != null
                    ? (string.IsNullOrEmpty(stage.displayName) ? stage.stepTitle : stage.displayName)
                    : "";

                int stageNum = (stepRunner != null ? stepRunner.CurrentStepIndex : -1) + 1;
                int stageCount = stepRunner != null ? stepRunner.StepCount : 0;

                string line = stageCount > 0 ? $"STAGE {stageNum} / {stageCount}" : "";

                if (!string.IsNullOrEmpty(stageName))
                    line += "     " + stageName.ToUpperInvariant();

                if (workflow != null)
                {
                    workflow.GetActionCounter(out int cur, out int total);
                    if (total > 1) line += $"     STEP {cur} / {total}";
                }

                stageText.text = line;
            }

            // Optional separate counter; the combined line above covers it by default.
            if (counterText != null)
            {
                if (workflow != null)
                {
                    workflow.GetActionCounter(out int cur, out int total);
                    counterText.text = total > 1 ? $"{cur} of {total}" : "";
                }
                else counterText.text = "";
            }

            // --- the one instruction ---
            if (instructionText != null)
                instructionText.text = action != null ? action.instruction : (stage != null ? stage.stepTitle : "");

            // --- detail, revealed with support level ---
            if (detailText != null)
            {
                bool showDetail = supportLevel != null
                                  && supportLevel.CurrentLevel != SupportLevel.L1_Minimal
                                  && action != null
                                  && !string.IsNullOrEmpty(action.detail);

                detailText.text = showDetail ? action.detail : "";
                detailText.gameObject.SetActive(showDetail);
            }

            // --- feedback clears with its task state ---
            if (feedbackText != null && Time.time > _successUntil && !string.IsNullOrEmpty(_lastFeedback))
            {
                bool blocked = session != null && !session.CanAdvance;
                bool holdingWrong = validator != null && validator.WrongPartHeld;
                if ((!blocked || _feedbackIsSuccess) && !holdingWrong)
                    ClearFeedback();
            }

            // --- Next is only offered when progression is actually permitted ---
            UpdateNext();
        }

        private void UpdateNext()
        {
            if (nextButton == null) return;

            bool can = session != null && session.CanAdvance;
            nextButton.interactable = can;

            var img = nextButton.GetComponent<Image>();
            if (img != null)
                img.color = can ? MrTheme.AccentSoft : MrTheme.WithAlpha(MrTheme.TrackEmpty, 0.5f);

            if (nextLabel != null)
            {
                nextLabel.color = can ? MrTheme.Accent : MrTheme.TextMuted;
                nextLabel.text = can ? "Continue" : (session != null ? session.BlockedReason ?? "Continue" : "Continue");
            }
        }

        private void ClearCard()
        {
            Show(stageText, "", MrTheme.TextPrimary);
            Show(counterText, "", MrTheme.TextMuted);
            Show(instructionText, "", MrTheme.TextPrimary);
            Show(feedbackText, "", MrTheme.TextSecondary);
            _feedbackIsSuccess = false;
            _feedbackIsWrongPart = false;
            _lastActionKey = null;

            if (detailText != null)
            {
                detailText.text = "";
                detailText.gameObject.SetActive(false);
            }
        }

        private void Show(TextMeshProUGUI field, string text, Color color)
        {
            if (field == null) return;
            field.text = text;
            field.color = color;

            if (field == feedbackText) _lastFeedback = text;
        }
    }
}
