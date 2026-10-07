// File: ParticipantCard.cs
// The single participant-facing instruction card.
//
// One component owns every field on the card, so entering a new action clears the
// old one in one place. It renders ONE action at a time and ONE feedback line.
//
// Two action states, made explicit here:
//   READ      - instruction to read; "Continue  >" is the one thing to do.
//   INTERACT  - a part to place; Continue is gone, the ghost and arrow are up, and the
//               feedback line says what to do with the part right now.
//
// The feedback line is the only validation cue in the participant interface. It is
// derived from live validator state, in plain words, and reflects the CURRENT
// interaction, not history. Numbers stay on the researcher HUD.

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

        [Tooltip("Feedback line: amber correction, green confirmation. The only validation cue.")]
        [SerializeField] private TextMeshProUGUI feedbackText;

        [Header("Controls")]
        [SerializeField] private Button nextButton;
        [SerializeField] private TextMeshProUGUI nextLabel;

        [Tooltip("Optional. Found by name (\"BackButton\") in the panel when empty.")]
        [SerializeField] private Button backButton;

        [Header("Control styling")]
        [SerializeField] private string continueLabel = "Continue  →";
        [SerializeField] private string backLabel = "←  Back";
        [SerializeField] private Vector2 continueSize = new Vector2(236f, 66f);
        [SerializeField] private Vector2 backSize = new Vector2(128f, 50f);
        [SerializeField] private float continueFontSize = 22f;
        [SerializeField] private float backFontSize = 17f;

        [Header("Behaviour")]
        [Tooltip("Seconds a success confirmation stays on screen before the card moves on.")]
        [SerializeField] private float successHoldSeconds = 1.2f;

        [Header("Single presentation")]
        [Tooltip("Siblings of the instruction field left over from the earlier layout. They are " +
                 "switched off at start so only this card's fields render.")]
        [SerializeField] private string[] legacySiblingNames = { "Eyebrow", "StepLabel", "CaptionText", "StatusLine" };

        [Tooltip("Also switch off the old \"Alignment needed\" chip: the feedback line replaces it.")]
        [SerializeField] private bool removeAlignmentChip = true;

        [Tooltip("Guarantee the instruction field can show two wrapped lines inside the panel.")]
        [SerializeField] private bool ensureInstructionFits = true;

        [Tooltip("Smallest font size the instruction may shrink to before wrapping to a second line.")]
        [SerializeField] private float instructionMinFontSize = 18f;

        [Header("Participant wording")]
        [SerializeField] private string wrongPartMessage = "That is not the required component";
        [SerializeField] private string successMessage = "Correct";
        [SerializeField] private string moveCloserMessage = "Move closer to the highlighted position";
        [SerializeField] private string turnToMatchMessage = "Turn the part to match the ghost";
        [SerializeField] private string almostThereMessage = "Almost there — align with the ghost";

        [SerializeField] private float refreshInterval = 0.12f;

        private enum FeedbackKind { None, Cue, WrongPart, Failed, Success }

        private float _timer;
        private float _successUntil;
        private FeedbackKind _feedbackKind;
        private string _lastActionKey;
        private Image _nextImage;
        private Image _backImage;
        private TextMeshProUGUI _backLabel;

        // =====================================================================
        // Setup
        // =====================================================================

        private void Awake()
        {
            RemoveLegacyPresentation();
            if (ensureInstructionFits) EnsureInstructionFits();
            StyleControls();
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
        /// The panel still carries the fields of the earlier layout and a second label
        /// inside the Continue button. This card is the single presentation, so they are
        /// switched off rather than blanked. The alignment chip goes too: the feedback
        /// line is the one validation cue now.
        /// </summary>
        private void RemoveLegacyPresentation()
        {
            Transform panel = instructionText != null ? instructionText.transform.parent : transform;
            if (panel == null) return;

            int removed = 0;
            foreach (Transform child in panel)
            {
                if (child == null) continue;
                bool legacy = System.Array.IndexOf(legacySiblingNames, child.name) >= 0
                              || (removeAlignmentChip && child.GetComponent<AlignmentChip>() != null);
                if (!legacy) continue;
                if (child.gameObject.activeSelf) { child.gameObject.SetActive(false); removed++; }
            }

            removed += KeepOnlyLabel(nextButton != null ? nextButton.transform : null, nextLabel);

            if (backButton == null && panel != null)
            {
                Transform b = panel.Find("BackButton");
                if (b != null) backButton = b.GetComponent<Button>();
            }
            if (backButton != null)
            {
                _backLabel = backButton.GetComponentInChildren<TextMeshProUGUI>(true);
                removed += KeepOnlyLabel(backButton.transform, _backLabel);
            }

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
        }

        private static void ShiftDown(RectTransform rt, float px)
        {
            if (rt == null) return;
            Vector2 p = rt.anchoredPosition;
            rt.anchoredPosition = new Vector2(p.x, p.y - px);
        }

        /// <summary>
        /// Continue is the primary action: larger, filled, bold, with an arrow. Back is a
        /// quiet secondary action. Both stay bottom-anchored where the panel has them.
        /// </summary>
        private void StyleControls()
        {
            if (nextButton != null)
            {
                _nextImage = nextButton.GetComponent<Image>();
                var rt = nextButton.transform as RectTransform;
                if (rt != null) rt.sizeDelta = continueSize;

                if (nextLabel != null)
                {
                    nextLabel.text = continueLabel;
                    nextLabel.enableAutoSizing = false;
                    nextLabel.fontSize = continueFontSize;
                    nextLabel.fontStyle = FontStyles.Bold;
                    nextLabel.alignment = TextAlignmentOptions.Center;
                }

                nextButton.onClick.RemoveListener(OnContinuePressed);
                nextButton.onClick.AddListener(OnContinuePressed);
            }

            if (backButton != null)
            {
                _backImage = backButton.GetComponent<Image>();
                var rt = backButton.transform as RectTransform;
                if (rt != null) rt.sizeDelta = backSize;

                if (_backLabel != null)
                {
                    _backLabel.text = backLabel;
                    _backLabel.enableAutoSizing = false;
                    _backLabel.fontSize = backFontSize;
                    _backLabel.fontStyle = FontStyles.Normal;
                    _backLabel.alignment = TextAlignmentOptions.Center;
                    _backLabel.color = MrTheme.TextSecondary;
                }

                if (_backImage != null) _backImage.color = MrTheme.WithAlpha(MrTheme.PanelFillRaised, 0.9f);

                backButton.onClick.RemoveListener(OnBackPressed);
                backButton.onClick.AddListener(OnBackPressed);
            }
        }

        private void OnContinuePressed()
        {
            // The scene also wires this button to AdvanceStepManually; the card only logs
            // the acknowledgement so the two cannot double-advance.
            if (session != null) session.NoteInstructionAcknowledged();
        }

        private void OnBackPressed()
        {
            if (session != null) session.GoBackToInstruction();
        }

        // =====================================================================
        // Feedback lifecycle: current interaction state, not history
        // =====================================================================

        private void HandleEligibleGrabbed(string key, Transform part)
        {
            if (_feedbackKind != FeedbackKind.Success) ClearFeedback();
        }

        private void HandleWrongGrabbed(string key, Transform part)
        {
            SetFeedback(FeedbackKind.WrongPart, wrongPartMessage, MrTheme.Warning);
        }

        private void HandleWrongReleased(string key, Transform part)
        {
            if (_feedbackKind == FeedbackKind.WrongPart) ClearFeedback();
        }

        private void HandleAttempt(bool success, float posErr, float rotErr, string trigger)
        {
            if (success)
            {
                SetFeedback(FeedbackKind.Success, successMessage, MrTheme.Success);
                _successUntil = Time.time + successHoldSeconds;
                return;
            }

            string msg = validator != null && validator.LastErrorType == "incorrect_orientation"
                ? turnToMatchMessage
                : almostThereMessage;
            SetFeedback(FeedbackKind.Failed, msg, MrTheme.Warning);
        }

        private void SetFeedback(FeedbackKind kind, string text, Color color)
        {
            _feedbackKind = kind;
            if (feedbackText == null) return;
            feedbackText.text = text;
            feedbackText.color = color;
        }

        private void ClearFeedback()
        {
            _feedbackKind = FeedbackKind.None;
            _successUntil = 0f;
            if (feedbackText != null) feedbackText.text = "";
        }

        /// <summary>Live cue from the validator while a part is being worked with.</summary>
        private void UpdateLiveCue()
        {
            if (validator == null || !validator.IsActive) return;

            // Sticky states (wrong part held, success hold) win over the live cue.
            if (_feedbackKind == FeedbackKind.WrongPart && validator.WrongPartHeld) return;
            if (_feedbackKind == FeedbackKind.Success && Time.time < _successUntil) return;

            switch (validator.CurrentCue)
            {
                case StepValidator.Cue.MoveCloser:
                    SetFeedback(FeedbackKind.Cue, moveCloserMessage, MrTheme.Warning); break;
                case StepValidator.Cue.TurnToMatch:
                    SetFeedback(FeedbackKind.Cue, turnToMatchMessage, MrTheme.Warning); break;
                case StepValidator.Cue.AlmostThere:
                    SetFeedback(FeedbackKind.Cue, almostThereMessage, MrTheme.Warning); break;
                default:
                    // Outside the zone: a failed-attempt message may stay until the next grab;
                    // a plain cue clears.
                    if (_feedbackKind == FeedbackKind.Cue) ClearFeedback();
                    break;
            }
        }

        // =====================================================================
        // Refresh
        // =====================================================================

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < refreshInterval) return;
            _timer = 0f;
            Refresh();
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

            // Entering a new action clears the previous action's feedback; a success
            // confirmation is allowed to finish its short hold first.
            string actionKey = (stage != null ? stage.StepIdentifier : "") + "/" + (action != null ? action.Id : "");
            if (actionKey != _lastActionKey)
            {
                _lastActionKey = actionKey;
                if (_feedbackKind != FeedbackKind.Success) ClearFeedback();
            }

            if (stageText != null)
            {
                string stageName = stage != null
                    ? (string.IsNullOrEmpty(stage.displayName) ? stage.stepTitle : stage.displayName)
                    : "";

                int stageNum = (stepRunner != null ? stepRunner.CurrentStepIndex : -1) + 1;
                int stageCount = stepRunner != null ? stepRunner.StepCount : 0;

                string line = stageCount > 0 ? $"STAGE {stageNum} / {stageCount}" : "";
                if (!string.IsNullOrEmpty(stageName)) line += "     " + stageName.ToUpperInvariant();

                if (workflow != null)
                {
                    workflow.GetActionCounter(out int cur, out int total);
                    if (total > 1) line += $"     STEP {cur} / {total}";
                }

                stageText.text = line;
            }

            if (counterText != null)
            {
                if (workflow != null)
                {
                    workflow.GetActionCounter(out int cur, out int total);
                    counterText.text = total > 1 ? $"{cur} of {total}" : "";
                }
                else counterText.text = "";
            }

            if (instructionText != null)
                instructionText.text = action != null ? action.instruction : (stage != null ? stage.stepTitle : "");

            if (detailText != null)
            {
                bool showDetail = supportLevel != null
                                  && supportLevel.CurrentLevel != SupportLevel.L1_Minimal
                                  && action != null
                                  && !string.IsNullOrEmpty(action.detail);

                detailText.text = showDetail ? action.detail : "";
                detailText.gameObject.SetActive(showDetail);
            }

            if (_feedbackKind == FeedbackKind.Success && Time.time > _successUntil)
                ClearFeedback();

            UpdateLiveCue();
            UpdateControls(action);
        }

        /// <summary>
        /// READ state: Continue shown and active. INTERACT state: Continue hidden - the part
        /// completes the action. Back only when a backward move is valid.
        /// </summary>
        private void UpdateControls(AssemblyAction action)
        {
            bool read = session != null && session.IsReadState;
            bool can = session != null && session.CanAdvance;

            if (nextButton != null)
            {
                bool show = read || can;
                if (nextButton.gameObject.activeSelf != show) nextButton.gameObject.SetActive(show);
                nextButton.interactable = can;

                if (_nextImage != null)
                    _nextImage.color = can ? MrTheme.WithAlpha(MrTheme.Accent, 0.28f) : MrTheme.WithAlpha(MrTheme.TrackEmpty, 0.5f);

                if (nextLabel != null)
                {
                    nextLabel.color = can ? MrTheme.TextPrimary : MrTheme.TextMuted;
                    nextLabel.text = continueLabel;
                }
            }

            if (backButton != null)
            {
                bool show = session != null && session.CanGoBack;
                if (backButton.gameObject.activeSelf != show) backButton.gameObject.SetActive(show);
            }
        }

        private void ClearCard()
        {
            if (stageText != null) stageText.text = "";
            if (counterText != null) counterText.text = "";
            if (instructionText != null) instructionText.text = "";
            ClearFeedback();
            _lastActionKey = null;

            if (detailText != null)
            {
                detailText.text = "";
                detailText.gameObject.SetActive(false);
            }

            if (nextButton != null && nextButton.gameObject.activeSelf) nextButton.gameObject.SetActive(false);
            if (backButton != null && backButton.gameObject.activeSelf) backButton.gameObject.SetActive(false);
        }
    }
}
