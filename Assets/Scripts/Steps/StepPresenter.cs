// File: StepPresenter.cs
// Renders the (current step x current support level) pair.
//
// Reads from StepRunner and SupportLevelController; writes to neither. A support
// level change re-presents the SAME step, which is what keeps L1 -> L2 -> L3 from
// advancing the assembly sequence.
//
// Content resolution is per-field: any field left empty on the level falls back to
// the step-level default on StepData. That is what lets the three level blocks
// stay genuinely empty (unauthored) while the existing crankshaft instruction
// keeps working from the step defaults.

using System.Collections.Generic;
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

        [Tooltip("Anchoring status line, e.g. \"Look at the marker\" / \"Block placed.\". " +
                 "StepManager writes here, and it is hidden for good once the first step is " +
                 "presented so that text cannot linger over the instructions.")]
        [SerializeField] private TextMeshProUGUI statusLineText;

        [Tooltip("Existing scene AudioSource used for instruction audio.")]
        [SerializeField] private AudioSource audioSource;

        [Tooltip("Marker-anchored root that spawned ghosts are positioned against (EngineAnchor).")]
        [SerializeField] private Transform anchorRoot;

        [Header("Behaviour")]
        [Tooltip("Deactivate every registry-bound object before presenting, so no guidance leaks between levels.")]
        [SerializeField] private bool clearRegistryBeforePresent = true;

        [Tooltip("Parent spawned ghost prefabs to the anchor root. Off reproduces the original world-space spawn.")]
        [SerializeField] private bool parentSpawnedGhostsToAnchor = false;

        [Header("Debug")]
        [SerializeField] private bool logPresentation = true;

        private readonly List<GameObject> _activatedSceneObjects = new List<GameObject>();
        private readonly List<GameObject> _spawnedInstances = new List<GameObject>();

        private StepData _presentedStep;
        private SupportLevel _presentedLevel;
        private bool _hasPresented;

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
        /// Renders one (step, level) pair. Safe to call repeatedly; identical
        /// consecutive requests are ignored.
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

            PresentText(step, content);
            PresentGhosts(step, content);
            PresentArrows(step, content);
            PresentAudio(step, content);
            WarnUnsupportedMedia(step, content, level);

            _presentedStep = step;
            _presentedLevel = level;
            _hasPresented = true;

            if (logPresentation)
                Debug.Log($"[StepPresenter] Presenting '{step.StepIdentifier}' at {level}.");
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
            // every support level and only the detail below it grows. That is what makes
            // L1 -> L2 -> L3 read as progressive disclosure rather than a restyle.
            if (titleText != null)
            {
                titleText.text = headline;

                if (bodyText != null)
                {
                    bodyText.text = detail ?? string.Empty;
                    // Collapse the body entirely at L1 so the panel does not leave a gap.
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
        /// Blanks every text field before the new step is written. Without this the
        /// previous step's wording stays on screen whenever the incoming step leaves a
        /// field empty - which is exactly what happens at L1, where the body is blank.
        /// </summary>
        private void ClearText()
        {
            if (titleText != null) titleText.text = string.Empty;

            // The anchoring status line has done its job by the time a step is shown.
            if (statusLineText != null && statusLineText.gameObject.activeSelf)
            {
                statusLineText.text = string.Empty;
                statusLineText.gameObject.SetActive(false);
            }

            if (bodyText != null)
            {
                bodyText.text = string.Empty;
                bodyText.gameObject.SetActive(false);
            }

            if (captionText != null && titleText == null)
                captionText.text = string.Empty;
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

        private void PresentGhosts(StepData step, StepSupportContent content)
        {
            bool activatedAny = ActivateByKeys(content != null ? content.ghostKeys : null);

            GameObject[] prefabs = (content != null && HasItems(content.ghostPrefabs))
                ? content.ghostPrefabs
                : (step.ghostPrefab != null ? new[] { step.ghostPrefab } : null);

            Vector3 offset = content != null ? content.ghostSpawnOffset : Vector3.zero;

            // Step-level fallback keeps the original spawn offset authored on the step.
            if (content == null || !HasItems(content.ghostPrefabs))
                offset = step.defaultGhostSpawnOffset;

            bool spawnedAny = SpawnPrefabs(prefabs, offset);

            if (!activatedAny && !spawnedAny && logPresentation)
                Debug.Log($"[StepPresenter] '{step.StepIdentifier}' presents no ghost guidance at this level.");
        }

        private void PresentArrows(StepData step, StepSupportContent content)
        {
            ActivateByKeys(content != null ? content.arrowKeys : null);

            GameObject[] prefabs = (content != null && HasItems(content.arrowPrefabs))
                ? content.arrowPrefabs
                : (step.arrowPrefab != null ? new[] { step.arrowPrefab } : null);

            Vector3 offset = content != null ? content.arrowSpawnOffset : Vector3.zero;

            SpawnPrefabs(prefabs, offset);
        }

        private void PresentAudio(StepData step, StepSupportContent content)
        {
            if (audioSource == null)
                return;

            AudioClip clip = (content != null && content.instructionAudio != null)
                ? content.instructionAudio
                : step.instructionAudio;

            if (clip == null)
                return;

            audioSource.clip = clip;
            audioSource.Play();
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

        private bool ActivateByKeys(string[] keys)
        {
            if (!HasItems(keys) || guidanceRegistry == null)
                return false;

            bool any = false;

            foreach (string key in keys)
            {
                if (!guidanceRegistry.TryResolve(key, out GameObject target))
                    continue;

                target.SetActive(true);
                _activatedSceneObjects.Add(target);
                any = true;
            }

            return any;
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

                _spawnedInstances.Add(instance);
                any = true;
            }

            return any;
        }

        private void ClearPresentation()
        {
            foreach (GameObject target in _activatedSceneObjects)
            {
                if (target != null)
                    target.SetActive(false);
            }
            _activatedSceneObjects.Clear();

            foreach (GameObject instance in _spawnedInstances)
            {
                if (instance != null)
                    Destroy(instance);
            }
            _spawnedInstances.Clear();

            if (clearRegistryBeforePresent && guidanceRegistry != null)
                guidanceRegistry.DeactivateAll();
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
