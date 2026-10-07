// File: StepPresenter.cs
// Renders the guidance for (current stage x current support level x current action).
//
// Reads from StepRunner, SupportLevelController and WorkflowState; writes to none of
// them. A support level change re-presents the SAME stage and the SAME action, which
// is what keeps L1 -> L2 -> L3 from advancing or resetting the assembly.
//
// One presentation lifecycle
// --------------------------
// Every transition clears before it renders: the previous action's ghosts, the
// stage's spawned prefabs, and (when this component owns them) the text fields. That
// is what stops a ghost or a sentence surviving into the next action.
//
// Text ownership: when a ParticipantCard exists in the scene it is the single writer
// of the card text. This component then only CLEARS the legacy fields it knows about
// (the old caption and the anchoring status line) and never writes them, so the two
// can no longer fight over the same TextMeshPro.
//
// Ghost policy: ghost guidance for an action comes from the action's own ghostKeys
// when it has any, otherwise from the stage's level block. Whether ghosts appear at
// all at a level is decided by the authored level block (a level with no ghost
// guidance authored shows none), not by anything in code.

using System.Collections.Generic;
using AdaptiveAR.Audio;
using AdaptiveAR.Support;
using TMPro;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class StepPresenter : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private StepRunner stepRunner;
        [SerializeField] private SupportLevelController supportLevel;
        [SerializeField] private GuidanceRegistry guidanceRegistry;

        [Tooltip("Supplies the current action. Found on this object when empty.")]
        [SerializeField] private WorkflowState workflow;

        [Header("Output")]
        [Tooltip("Single-field fallback. Used only when titleText below is not assigned, so the " +
                 "original one-caption layout keeps working.")]
        [SerializeField] private TextMeshProUGUI captionText;

        [Header("Output - typographic hierarchy (preferred)")]
        [Tooltip("The instruction headline. Identical at every support level: levels reveal " +
                 "more detail below it, they do not restyle it.")]
        [SerializeField] private TextMeshProUGUI titleText;

        [Tooltip("Supporting detail. Empty at L1, the location at L2, the ordered breakdown at L3.")]
        [SerializeField] private TextMeshProUGUI bodyText;

        [Tooltip("Small progress label, e.g. \"STEP 02 / 06\".")]
        [SerializeField] private TextMeshProUGUI stepLabelText;

        [Tooltip("Supplies the step number and count for the label above.")]
        [SerializeField] private StepRunner stepRunnerForLabel;

        [Tooltip("Anchoring status line, e.g. \"Workspace placed.\". StepManager writes here, " +
                 "and it is hidden for good once the first step is presented so that text " +
                 "cannot linger over the instructions.")]
        [SerializeField] private TextMeshProUGUI statusLineText;

        [Tooltip("Existing scene AudioSource used for instruction audio.")]
        [SerializeField] private AudioSource audioSource;

        [Tooltip("Anchored root that spawned ghosts are positioned against (EngineAnchor).")]
        [SerializeField] private Transform anchorRoot;

        [Header("Behaviour")]
        [Tooltip("Deactivate every registry-bound object before presenting, so no guidance leaks between levels.")]
        [SerializeField] private bool clearRegistryBeforePresent = true;

        [Tooltip("Parent spawned ghost prefabs to the anchor root. Off reproduces the original world-space spawn.")]
        [SerializeField] private bool parentSpawnedGhostsToAnchor = false;

        [Tooltip("Make activated ghosts inert: kinematic, no gravity, no colliders, no grab. " +
                 "A target must never fall, collide, or be picked up.")]
        [SerializeField] private bool makeGhostsInert = true;

        [Header("Debug")]
        [SerializeField] private bool logPresentation = true;

        private readonly List<GameObject> _activatedGhosts = new List<GameObject>();
        private readonly List<GameObject> _activatedArrows = new List<GameObject>();
        private readonly List<GameObject> _spawnedInstances = new List<GameObject>();
        private readonly HashSet<GameObject> _madeInert = new HashSet<GameObject>();

        private StepData _presentedStep;
        private SupportLevel _presentedLevel;
        private bool _hasPresented;

        private string[] _stageGhostKeys;
        private GameObject[] _stageGhostPrefabs;
        private Vector3 _stageGhostOffset;
        private bool _levelShowsGhosts;

        private AssemblyAction _currentAction;
        private bool _textOwnedByCard;
        private InstructionSpeech _speech;
        private AudioClip _pendingStageClip;

        /// <summary>True when a ParticipantCard owns the card text and this component only clears.</summary>
        public bool TextOwnedByParticipantCard { get { return _textOwnedByCard; } }

        private void Awake()
        {
            if (workflow == null) workflow = GetComponent<WorkflowState>();

            _textOwnedByCard = FindAnyObjectByType<AdaptiveAR.UI.ParticipantCard>(FindObjectsInactive.Include) != null;

            // Speech goes through the scene AudioSource the authored clips already used.
            _speech = InstructionSpeech.Ensure(audioSource);

            if (_textOwnedByCard && logPresentation)
                Debug.Log("[StepPresenter] ParticipantCard found: it owns the instruction text; " +
                          "this presenter handles ghosts, arrows and audio only.");
        }

        private void OnEnable()
        {
            if (stepRunner != null)
                stepRunner.OnStepChanged += HandleStepChanged;

            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged += HandleSupportLevelChanged;
        }

        private void OnDisable()
        {
            if (stepRunner != null)
                stepRunner.OnStepChanged -= HandleStepChanged;

            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged -= HandleSupportLevelChanged;
        }

        private void OnDestroy()
        {
            ClearPresentation();
        }

        // ---------------- Event handlers ----------------

        private void HandleStepChanged(StepData step, int index, string reason)
        {
            // A new stage starts with no action until the session controller enters one.
            _currentAction = null;

            // StepRunner applies the stage's default level BEFORE raising this event, so the
            // level-change handler may already have presented this stage. Re-apply the
            // ghosts anyway so nothing from the previous stage's action can survive.
            if (_hasPresented && _presentedStep == step && _presentedLevel == CurrentLevel())
            {
                ApplyGhosts();
                return;
            }

            Present(step, CurrentLevel());
        }

        private void HandleSupportLevelChanged(SupportLevel previous, SupportLevel current, string reason)
        {
            // Re-present the SAME step at the new level. The step index is never touched.
            if (stepRunner == null || stepRunner.CurrentStep == null)
                return;

            Present(stepRunner.CurrentStep, current);
        }

        private SupportLevel CurrentLevel()
        {
            return supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal;
        }

        // ---------------- Presentation ----------------

        /// <summary>
        /// Re-renders the current step and level, bypassing the no-change guard.
        /// </summary>
        public void Refresh()
        {
            if (stepRunner == null || stepRunner.CurrentStep == null)
                return;

            _hasPresented = false;
            Present(stepRunner.CurrentStep, CurrentLevel());
        }

        /// <summary>
        /// Renders one (step, level) pair, then re-applies the current action on top.
        /// Safe to call repeatedly; identical consecutive requests are ignored.
        /// </summary>
        public void Present(StepData step, SupportLevel level)
        {
            if (step == null)
                return;

            if (_hasPresented && _presentedStep == step && _presentedLevel == level)
                return;

            ClearPresentation();
            ClearText();

            StepSupportContent content = step.GetContent(level);

            if (!_textOwnedByCard)
                PresentText(step, content);

            bool stageChanged = _presentedStep != step;

            CacheStageGhosts(step, content);
            PresentArrows(step, content);
            if (stageChanged) PresentAudio(step, content);
            WarnUnsupportedMedia(step, content, level);

            _presentedStep = step;
            _presentedLevel = level;
            _hasPresented = true;

            // The action survives a level change; its ghosts are re-applied at the new level.
            // Only adopt the workflow's action when it belongs to THIS stage.
            if (_currentAction == null && workflow != null && stepRunner != null
                && workflow.StageIndex == stepRunner.CurrentStepIndex)
                _currentAction = workflow.CurrentAction;

            ApplyGhosts();

            if (logPresentation)
                Debug.Log($"[StepPresenter] Presenting '{step.StepIdentifier}' at {level}" +
                          (_currentAction != null ? $", action '{_currentAction.Id}'." : "."));
        }

        /// <summary>
        /// Removes every ghost, arrow and spawned prefab. Used when the sequence finishes so
        /// the last stage's guidance does not stay on the engine.
        /// </summary>
        public void ClearGuidance()
        {
            _currentAction = null;
            _levelShowsGhosts = false;
            ClearPresentation();

            if (logPresentation)
                Debug.Log("[StepPresenter] Guidance cleared.");
        }

        /// <summary>
        /// Called by the session controller when an action is entered. Clears the previous
        /// action's ghosts first, then shows this one's. Called with null when a stage has
        /// no further action.
        /// </summary>
        public void PresentAction(AssemblyAction action)
        {
            _currentAction = action;
            ApplyGhosts();

            SpeakAction(action);

            if (logPresentation)
                Debug.Log("[StepPresenter] Action " + (action != null ? $"'{action.Id}'" : "<none>") +
                          $" presented with {_activatedGhosts.Count} ghost(s).");
        }

        private void PresentText(StepData step, StepSupportContent content)
        {
            string headline = FirstNonEmpty(
                content != null ? content.instructionText : null,
                step.stepTitle);

            string detail = FirstNonEmpty(
                content != null ? content.instructionDetail : null,
                step.stepDescription);

            // Preferred: separate fields, so the headline holds its position and weight at
            // every support level and only the detail below it grows.
            if (titleText != null)
            {
                titleText.text = headline;

                if (bodyText != null)
                {
                    bodyText.text = detail ?? string.Empty;
                    bodyText.gameObject.SetActive(!string.IsNullOrEmpty(detail));
                }

                UpdateStepLabel(step);
                return;
            }

            // Fallback: the original single-caption layout.
            if (captionText == null)
                return;

            captionText.text = string.IsNullOrEmpty(detail)
                ? headline
                : headline + "\n\n" + detail;

            UpdateStepLabel(step);
        }

        /// <summary>
        /// Blanks every text field this component knows about before anything new is
        /// written. The legacy caption is always cleared, even when a ParticipantCard owns
        /// the card: it is where the old prototype's serialized "Demo:" sentence lived, and
        /// nothing else ever wrote it again.
        /// </summary>
        private void ClearText()
        {
            if (captionText != null && captionText.text.Length > 0)
                captionText.text = string.Empty;

            // The anchoring status line has done its job by the time a step is shown.
            if (statusLineText != null && statusLineText.gameObject.activeSelf)
            {
                statusLineText.text = string.Empty;
                statusLineText.gameObject.SetActive(false);
            }

            if (_textOwnedByCard)
                return;

            if (titleText != null) titleText.text = string.Empty;

            if (bodyText != null)
            {
                bodyText.text = string.Empty;
                bodyText.gameObject.SetActive(false);
            }
        }

        private void UpdateStepLabel(StepData step)
        {
            if (stepLabelText == null) return;

            StepRunner source = stepRunnerForLabel != null ? stepRunnerForLabel : stepRunner;
            if (source == null)
            {
                stepLabelText.text = "";
                return;
            }

            int index = source.CurrentStepIndex;
            int count = source.StepCount;

            stepLabelText.text = index < 0 || count <= 0
                ? ""
                : $"STEP {index + 1:00} / {count:00}";
        }

        // ---------------- Ghosts ----------------

        private void CacheStageGhosts(StepData step, StepSupportContent content)
        {
            _stageGhostKeys = content != null ? content.ghostKeys : null;

            _stageGhostPrefabs = (content != null && HasItems(content.ghostPrefabs))
                ? content.ghostPrefabs
                : (step.ghostPrefab != null ? new[] { step.ghostPrefab } : null);

            _stageGhostOffset = (content != null && HasItems(content.ghostPrefabs))
                ? content.ghostSpawnOffset
                : step.defaultGhostSpawnOffset;

            // Authored data decides whether this level shows ghost guidance at all.
            _levelShowsGhosts = HasItems(_stageGhostKeys) || HasItems(_stageGhostPrefabs);
        }

        /// <summary>
        /// The one place ghosts are switched on. Always clears first, so a ghost from the
        /// previous action cannot survive, then shows the action's own ghosts or, when it
        /// has none, the stage's.
        /// </summary>
        private void ApplyGhosts()
        {
            DeactivateAll(_activatedGhosts);
            DestroyAll(_spawnedInstances);

            if (!_levelShowsGhosts)
                return;

            bool actionHasOwn = _currentAction != null && HasItems(_currentAction.ghostKeys);
            string[] keys = actionHasOwn ? _currentAction.ghostKeys : _stageGhostKeys;

            bool activatedAny = ActivateByKeys(keys, _activatedGhosts);

            bool spawnedAny = false;
            if (!actionHasOwn)
                spawnedAny = SpawnPrefabs(_stageGhostPrefabs, _stageGhostOffset);

            if (!activatedAny && !spawnedAny && logPresentation && _presentedStep != null)
                Debug.Log($"[StepPresenter] '{_presentedStep.StepIdentifier}' presents no ghost guidance for this action/level.");
        }

        private void PresentArrows(StepData step, StepSupportContent content)
        {
            ActivateByKeys(content != null ? content.arrowKeys : null, _activatedArrows);

            GameObject[] prefabs = (content != null && HasItems(content.arrowPrefabs))
                ? content.arrowPrefabs
                : (step.arrowPrefab != null ? new[] { step.arrowPrefab } : null);

            Vector3 offset = content != null ? content.arrowSpawnOffset : Vector3.zero;

            SpawnPrefabs(prefabs, offset);
        }

        /// <summary>
        /// A stage's authored clip is held until its first action is presented: if that
        /// action has its own spoken instruction the authored clip is not needed, otherwise
        /// it is played once. Never replayed on a support-level re-render.
        /// </summary>
        private void PresentAudio(StepData step, StepSupportContent content)
        {
            AudioClip clip = (content != null && content.instructionAudio != null)
                ? content.instructionAudio
                : step.instructionAudio;

            _pendingStageClip = clip;

            // A stage with no performable action gets its clip now.
            if (step.NextEnabledActionIndex(0) < 0)
                FlushStageClip();
        }

        /// <summary>
        /// Speaks the instruction exactly as the card shows it, once, when the action is
        /// entered. An authored action clip wins; then the sentence's clip; then the
        /// stage's authored clip as a fallback.
        /// </summary>
        private void SpeakAction(AssemblyAction action)
        {
            if (action == null) return;

            if (action.audioCue != null)
            {
                if (_speech != null) _speech.PlayAuthored(action.audioCue);
                _pendingStageClip = null;
                return;
            }

            if (_speech != null && _speech.SpeakInstruction(action.instruction))
            {
                _pendingStageClip = null;
                return;
            }

            FlushStageClip();
        }

        private void FlushStageClip()
        {
            if (_pendingStageClip == null) return;
            if (_speech != null) _speech.PlayAuthored(_pendingStageClip);
            _pendingStageClip = null;
        }

        private void WarnUnsupportedMedia(StepData step, StepSupportContent content, SupportLevel level)
        {
            if (content == null || !content.HasUnsupportedMedia)
                return;

            Debug.LogWarning(
                $"[StepPresenter] '{step.StepIdentifier}' at {level} references video/animation content, " +
                "but the live scene has no VideoPlayer or Animator. Playback is not wired yet.", this);
        }

        // ---------------- Helpers ----------------

        private bool ActivateByKeys(string[] keys, List<GameObject> into)
        {
            if (!HasItems(keys) || guidanceRegistry == null)
                return false;

            bool any = false;

            foreach (string key in keys)
            {
                if (!guidanceRegistry.TryResolve(key, out GameObject target))
                    continue;

                if (makeGhostsInert) MakeInert(target);

                target.SetActive(true);
                into.Add(target);
                any = true;
            }

            return any;
        }

        /// <summary>
        /// A ghost is a picture of a pose. Some scene ghosts carry a Rigidbody, a DropIntoTray
        /// or a collider copied from the part they depict; left alone they would fall, be
        /// shoved, or block the real part from reaching the pose they show.
        /// </summary>
        private void MakeInert(GameObject ghost)
        {
            if (ghost == null || _madeInert.Contains(ghost)) return;
            _madeInert.Add(ghost);

            foreach (DropIntoTray d in ghost.GetComponentsInChildren<DropIntoTray>(true))
                d.enabled = false;

            foreach (Rigidbody rb in ghost.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.detectCollisions = false;
            }

            foreach (Collider c in ghost.GetComponentsInChildren<Collider>(true))
                c.enabled = false;

            foreach (MonoBehaviour mb in ghost.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string ns = mb.GetType().Namespace;
                if (!string.IsNullOrEmpty(ns) && ns.StartsWith("Oculus.Interaction"))
                    mb.enabled = false;
            }
        }

        private bool SpawnPrefabs(GameObject[] prefabs, Vector3 offset)
        {
            if (!HasItems(prefabs))
                return false;

            bool any = false;

            foreach (GameObject prefab in prefabs)
            {
                if (prefab == null)
                    continue;

                GameObject instance = Instantiate(prefab);

                // Reproduces the original placement: offset expressed in anchor space,
                // rotation copied from the anchor, scale reset to one.
                if (anchorRoot != null)
                {
                    instance.transform.position = anchorRoot.position + anchorRoot.rotation * offset;
                    instance.transform.rotation = anchorRoot.rotation;

                    if (parentSpawnedGhostsToAnchor)
                        instance.transform.SetParent(anchorRoot, true);
                }

                instance.transform.localScale = Vector3.one;

                if (makeGhostsInert) MakeInert(instance);

                _spawnedInstances.Add(instance);
                any = true;
            }

            return any;
        }

        private void ClearPresentation()
        {
            DeactivateAll(_activatedGhosts);
            DeactivateAll(_activatedArrows);
            DestroyAll(_spawnedInstances);

            if (clearRegistryBeforePresent && guidanceRegistry != null)
                guidanceRegistry.DeactivateAll();
        }

        private static void DeactivateAll(List<GameObject> list)
        {
            foreach (GameObject target in list)
            {
                if (target != null)
                    target.SetActive(false);
            }
            list.Clear();
        }

        private static void DestroyAll(List<GameObject> list)
        {
            foreach (GameObject instance in list)
            {
                if (instance != null)
                    Destroy(instance);
            }
            list.Clear();
        }

        private static string FirstNonEmpty(string preferred, string fallback)
        {
            return string.IsNullOrEmpty(preferred) ? (fallback ?? string.Empty) : preferred;
        }

        private static bool HasItems<T>(T[] array)
        {
            return array != null && array.Length > 0;
        }
    }
}
