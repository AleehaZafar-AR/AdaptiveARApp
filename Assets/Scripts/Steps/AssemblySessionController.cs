// File: AssemblySessionController.cs
// Owns the research session: timing, counters, logging, and the decision seam.
//
// It is the only place that knows about ALL of steps, support level, validation and
// logging at once. StepRunner still owns the step index and SupportLevelController
// still owns the level; this component observes both and records what happened.
//
// The AI seam is complete today even though no provider exists:
//   BuildDecisionContext()  - the exact context a provider would be given
//   ApplyDecision(response) - enforces the constrained action set, applies the
//                             result, and logs context + raw + parsed + latency +
//                             constraint status + resulting level
// Tomorrow's provider calls those two methods and nothing here changes.

using System;
using AdaptiveAR.Audio;
using AdaptiveAR.Decision;
using AdaptiveAR.Logging;
using AdaptiveAR.Support;
using AdaptiveAR.XR;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class AssemblySessionController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private StepRunner stepRunner;
        [SerializeField] private SupportLevelController supportLevel;
        [SerializeField] private StepValidator validator;
        [SerializeField] private SessionLogger logger;

        [Tooltip("Single source of truth for progress. Every display subscribes to it.")]
        [SerializeField] private WorkflowState workflow;

        [Tooltip("Optional. Completes ToolAction substeps when a tool is used on a fastener.")]
        [SerializeField] private ToolInteraction toolInteraction;

        [Tooltip("Shows and clears per-action guidance. Found on this object when empty.")]
        [SerializeField] private StepPresenter presenter;

        [Tooltip("Source of the workspace placement events that are logged. Found on this object when empty.")]
        [SerializeField] private StepManager stepManager;

        [Header("Participant")]
        [Tooltip("Recorded as a decision-layer input. Free text, e.g. novice / experienced.")]
        [SerializeField] private string operatorExperience = "unspecified";

        [Header("Session")]
        [Tooltip("Open the log as soon as the app starts, so the marker-search period is captured too.")]
        [SerializeField] private bool beginSessionOnStart = true;

        [Tooltip("Close the session automatically when the last step is completed.")]
        [SerializeField] private bool endSessionOnComplete = true;

        [Header("Support transition cue")]
        [Tooltip("When an AI decision raises the support level (L1->L2, L2->L3): a short neutral " +
                 "message on the participant card, a soft tone, then the current action's " +
                 "instruction is spoken again with the richer guidance already on the bench. " +
                 "Never for KEEP, downward, saturated, fallback or failed decisions.")]
        [SerializeField] private bool transitionCueEnabled = true;

        [Tooltip("Neutral wording. Not anthropomorphic.")]
        [SerializeField] private string guidanceIncreasedMessage = "Additional guidance enabled";

        [Tooltip("How long the message stays on the card before the live cues take over again.")]
        [SerializeField] private float transitionMessageSeconds = 4f;

        [Tooltip("Pause between the tone and the replayed instruction so they do not overlap.")]
        [SerializeField] private float transitionReplayDelaySeconds = 0.7f;

        [Header("Completion")]
        [Tooltip("Shown on the caption when every step is done.")]
        [TextArea(2, 4)]
        [SerializeField] private string completionMessage = "Assembly complete. Thank you.";

        [Tooltip("Caption to overwrite on completion. Without this the last step's text " +
                 "would stay on screen after the sequence finishes.")]
        [SerializeField] private TMPro.TextMeshProUGUI captionText;

        [Header("Feedback")]
        [Tooltip("Spoken on a valid placement.")]
        [SerializeField] private string successPhrase = "Correct.";

        [Tooltip("Spoken when a part that cannot satisfy the current action is picked up.")]
        [SerializeField] private string wrongPartPhrase = "That is not the required component.";

        [Tooltip("Controller pulses on grab, wrong part and success. Never blocks progression.")]
        [SerializeField] private bool haptics = true;

        [Tooltip("Serialized feedback clips: the same reference mechanism as the authored " +
                 "crankshaft clip. Used before the Resources lookup; a synthesised tone is the last fallback.")]
        [SerializeField] private AudioClip successClip;
        [SerializeField] private AudioClip wrongPartClip;
        [SerializeField] private AudioClip invalidAttemptClip;

        [Tooltip("Brief colour pulse on the part: green on success, amber/red on a failed attempt.")]
        [SerializeField] private bool spatialPulses = true;

        [Tooltip("Resolves parts for the auto-complete shortcut. Found on this object when empty.")]
        [SerializeField] private GuidanceRegistry registry;

        [Header("Debug")]
        [SerializeField] private bool logToConsole = true;

        // --- session state ---
        public bool SessionActive { get; private set; }
        public bool SequenceFinished { get; private set; }

        public float OverallElapsedMs { get { return _overallMs; } }
        public float StepElapsedMs { get { return _stepMs; } }
        public int AttemptsOnStep { get { return _attemptsOnStep; } }
        public int AttemptsTotal { get { return _attemptsTotal; } }
        private int _attemptsTotal;
        public int ErrorsOnStep { get { return _errorsOnStep; } }
        public int ErrorsTotal { get { return _errorsTotal; } }
        public int SupportChangesTotal { get { return _supportChangesTotal; } }

        /// <summary>Raised when the whole sequence is finished. UI can subscribe.</summary>
        public event Action OnSessionComplete;

        /// <summary>Raised whenever counters change, so HUDs can refresh without polling every field.</summary>
        public event Action OnStateChanged;

        /// <summary>An action was entered (after its presentation). The decision scheduler listens here.</summary>
        public event Action<AssemblyAction> OnActionEntered;
        /// <summary>A placement attempt was counted as an error.</summary>
        public event Action OnCountedError;
        /// <summary>A part that cannot satisfy the current action was picked up (one per grab). The decision scheduler listens here.</summary>
        public event Action<string> OnWrongComponentGrabbed;
        /// <summary>A physically validated (or tool) action was completed successfully. The decision scheduler listens here.</summary>
        public event Action<AssemblyAction> OnActionCompleted;

        /// <summary>True between the first step and the end of the sequence: the window in which decisions make sense.</summary>
        public bool AssemblyRunning { get { return SessionActive && !SequenceFinished && _currentStep != null; } }
        public SupportLevel CurrentSupportLevel { get { return supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal; } }
        /// <summary>The last response the seam applied (any provider), for the HUD and diagnostics.</summary>
        public DecisionResponse LastDecisionResponse { get; private set; }

        private float _overallMs;
        private float _stepMs;
        private int _attemptsOnStep;
        private int _errorsOnStep;
        private int _errorsTotal;
        private int _supportChangesOnStep;

        // Recent performance since the later of step entry / last level change (decision context).
        private int _errorsSinceChange;
        private int _successesSinceChange;
        private float _lastErrorStepMs = -1f;      // -1 = no error yet on this step
        private float _lastChangeStepMs = -1f;     // -1 = level unchanged since step entry
        private int _supportChangesTotal;
        private int _decisionIndex;

        private bool _stepOpen;
        private StepData _currentStep;
        private int _currentIndex = -1;
        private bool _placementSubscribed;
        private bool _currentActionArmed;
        private bool _selfCheckDone;

        // =====================================================================
        // Regression self-check at task start
        // =====================================================================

        private void Check(bool ok, string what)
        {
            if (ok) Debug.Log("[SelfCheck] OK   " + what);
            else Debug.LogError("[SelfCheck] FAIL " + what);
        }

        private void RunSelfCheck()
        {
            try
            {
                int actions = 0, physical = 0, physicalWithTarget = 0;
                for (int i = 0; stepRunner != null && i < stepRunner.StepCount; i++)
                {
                    StepData s = stepRunner.GetStepAt(i);
                    if (s == null || s.actions == null) continue;
                    foreach (AssemblyAction a in s.actions)
                    {
                        if (a == null || !a.enabled) continue;
                        actions++;
                        if (!a.RequiresPhysicalValidation) continue;
                        physical++;
                        bool t = registry != null && !string.IsNullOrEmpty(a.targetKey) && registry.TryResolveQuiet(a.targetKey, out GameObject tg) && tg != null;
                        bool p = registry != null && !string.IsNullOrEmpty(a.partKey) && registry.RoleCandidates(a.partKey).Count > 0;
                        if (t && p) physicalWithTarget++;
                        else Debug.LogError($"[SelfCheck] FAIL physical action '{a.Id}': target={(t ? "ok" : a.targetKey + " unresolved")} part={(p ? "ok" : a.partKey + " no candidates")}");
                    }
                }
                Check(actions > 0, $"StepRunner has {actions} enabled action(s)");
                Check(physical > 0 && physicalWithTarget == physical, $"{physicalWithTarget}/{physical} physical actions resolve a target and a part");
                Check(validator != null, "StepValidator present");
                Check(registry != null && registry.TryResolveQuiet("ghost.crankshaft", out GameObject cg) && cg != null, "crankshaft ghost resolves");
                Check(registry != null && registry.TryResolveQuiet("part.camshaft", out GameObject cam) && cam != null, "camshaft resolves");
                int seps = 0;
                for (int n = 1; n <= 4; n++)
                    if (registry != null && registry.TryResolveQuiet($"part.engineBlockSep00{n}", out GameObject sep) && sep != null
                        && registry.TryResolveQuiet($"ghost.engineBlockSep00{n}", out GameObject sg) && sg != null) seps++;
                Check(seps == 4, $"{seps}/4 EngineBlockSep holders and targets resolve");
                int pistonParts = 0;
                foreach (string role in new[] { "PistonHead", "ConnectingRod", "ConnectingPin", "PistonEnd", "pistonBolt", "pistonBoltOther", "PistonNut", "PistonNutOther" })
                    if (registry != null && registry.RoleCandidates("part.PistonKit001." + role).Count > 0) pistonParts++;
                Check(pistonParts == 8, $"{pistonParts}/8 piston roles resolve instances");
                var overview = FindAnyObjectByType<AdaptiveAR.UI.TaskListHud>(FindObjectsInactive.Include);
                Check(overview != null, "overview panel (TaskListHud) resolves" + (overview != null ? $" activeInHierarchy={overview.gameObject.activeInHierarchy}" : ""));
                var mat = FindAnyObjectByType<AdaptiveAR.MR.AssemblyWorkSurface>(FindObjectsInactive.Include);
                Check(mat != null || (stepManager != null && stepManager.createWorkSurface), "piston work surface resolves or will be created");
                int selectable = 0;
                if (registry != null)
                    foreach (string k in registry.PartKeys())
                        if (!GuidanceRegistry.IsKitHandleKey(k) && registry.TryResolveQuiet(k, out GameObject go) && go != null && go.GetComponent<Oculus.Interaction.PointableElement>() != null && go.GetComponent<Collider>() != null) selectable++;
                Check(selectable > 0, $"{selectable} selectable part(s) available to the wrong-component watcher");
            }
            catch (Exception e)
            {
                Debug.LogError("[SelfCheck] threw: " + e);
            }

            try { RunStep1Check(); }
            catch (Exception e) { Debug.LogError("[Step1Check] threw: " + e); }

            // What the participant can actually pick up vs what the wrong-component system watches.
            try { if (validator != null) validator.AuditGrabCoverage(); }
            catch (Exception e) { Debug.LogError("[GrabCoverage] threw: " + e); }

            // Every instruction against the guidance actually shown at each support level.
            try
            {
                var arrow = FindAnyObjectByType<AdaptiveAR.UI.GuidanceArrow>(FindObjectsInactive.Include);
                if (arrow != null) GuidanceAudit.ArrowMinimumLevel = arrow.MinimumLevel;
                GuidanceAudit.Run(stepRunner, registry, "task_start");
            }
            catch (Exception e) { Debug.LogError("[GuidanceAudit] threw: " + e); }
        }

        /// <summary>
        /// Step-1-only check, before any participant interaction. Every field false is an
        /// explicit ERROR with the reason.
        /// </summary>
        private void RunStep1Check()
        {
            StepData stage = null;
            for (int i = 0; stepRunner != null && i < stepRunner.StepCount; i++)
            {
                StepData s = stepRunner.GetStepAt(i);
                if (s != null && s.StepIdentifier == "step_01_crankshaft") { stage = s; break; }
            }

            AssemblyAction locate = null, place = null;
            if (stage != null && stage.actions != null)
            {
                foreach (AssemblyAction a in stage.actions)
                {
                    if (a == null || !a.enabled || a.partKey != "part.crankshaft") continue;
                    if (!a.RequiresPhysicalValidation && locate == null) locate = a;
                    if (a.RequiresPhysicalValidation && a.targetKey == "ghost.crankshaft" && place == null) place = a;
                }
            }

            SupportLevel level = supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal;
            string placeText = place != null ? GuidanceText.InstructionFor(stage, place, level) : null;

            GameObject real = null, ghost = null;
            if (registry != null) registry.TryResolveQuiet("part.crankshaft", out real);
            if (registry != null) registry.TryResolveQuiet("ghost.crankshaft", out ghost);

            bool realOk = real != null && real.GetComponent<Oculus.Interaction.PointableElement>() != null
                          && real.GetComponent<Collider>() != null && real.GetComponent<Rigidbody>() != null;
            bool ghostOk = ghost != null && ghost != real && ghost.GetComponent<Oculus.Interaction.PointableElement>() == null;
            bool validatorOk = validator != null && validator.isActiveAndEnabled;
            int watched = validatorOk ? validator.EnsureGrabSource() : 0;
            bool grabSourceOk = validatorOk && watched > 0 && real != null && validator.IsWatched(real);

            bool locateOk = locate != null, placeOk = place != null, textOk = !string.IsNullOrEmpty(placeText);

            Debug.Log($"[Step1Check] crankshaftLocateAction={locateOk} crankshaftPlaceAction={placeOk} placeText={textOk} " +
                      $"realCrankshaft={realOk} targetGhost={ghostOk} validator={validatorOk} grabEventSource={grabSourceOk}" +
                      $" | placeText=\"{placeText}\" real={(real != null ? real.name + "#" + real.GetInstanceID() : "<null>")} " +
                      $"ghost={(ghost != null ? ghost.name + "#" + ghost.GetInstanceID() : "<null>")} watched={watched} level={level}");

            if (!locateOk) Debug.LogError("[Step1Check] ERROR crankshaftLocateAction=false: no enabled READ action with partKey part.crankshaft in step_01_crankshaft" + (stage == null ? " (stage not found)" : ""));
            if (!placeOk) Debug.LogError("[Step1Check] ERROR crankshaftPlaceAction=false: no enabled Place action with partKey part.crankshaft and targetKey ghost.crankshaft");
            if (!textOk) Debug.LogError("[Step1Check] ERROR placeText=false: the place action's instruction is empty (asset text missing)");
            if (!realOk) Debug.LogError("[Step1Check] ERROR realCrankshaft=false: " + (real == null ? "registry cannot resolve part.crankshaft" : $"'{real.name}' lacks " + (real.GetComponent<Oculus.Interaction.PointableElement>() == null ? "a PointableElement/Grabbable " : "") + (real.GetComponent<Collider>() == null ? "a Collider " : "") + (real.GetComponent<Rigidbody>() == null ? "a Rigidbody" : "")));
            if (!ghostOk) Debug.LogError("[Step1Check] ERROR targetGhost=false: " + (ghost == null ? "registry cannot resolve ghost.crankshaft" : (ghost == real ? "ghost.crankshaft resolves to the REAL crankshaft" : "ghost.crankshaft resolves to a grabbable object")));
            if (!validatorOk) Debug.LogError("[Step1Check] ERROR validator=false: " + (validator == null ? "no StepValidator reference on the session controller" : "StepValidator is disabled or on an inactive object"));
            if (!grabSourceOk) Debug.LogError("[Step1Check] ERROR grabEventSource=false: " + (!validatorOk ? "no validator" : (watched == 0 ? "no selectable part could be watched" : "the real crankshaft is not in the watched set")));
        }

        [Header("Turntable / timer / completion")]
        [Tooltip("Minutes of assembly time after which the timer signal sounds once (elapsed-time notification only).")]
        [SerializeField] private float timerMinutes = 10f;

        private float _assemblyStartMs = -1f;
        private bool _timerFired;
        private GameObject _resetViewButton;
        private AdaptiveAR.MR.EngineTurntable _turntable;

        /// <summary>
        /// Viewing convenience: a subtle turntable under the oil pan turns the ENGINE (and
        /// everything installed on it) about the world vertical axis; trays, mat and UI stay.
        /// A single Reset View button appears on the card only while the engine is turned.
        /// </summary>
        private void EnsureTurntable()
        {
            try
            {
                Transform engine = stepManager != null && stepManager.oilPan != null ? stepManager.oilPan.transform : null;
                if (engine == null) { Debug.LogError("[Turntable] EngineAnchor not found."); return; }

                GameObject template = null;
                if (registry != null && registry.TryResolveQuiet("part.crankshaft", out GameObject crank) && crank != null)
                {
                    foreach (Transform t in crank.transform)
                        if (t.GetComponentInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true) != null) { template = t.gameObject; break; }
                }
                _turntable = AdaptiveAR.MR.EngineTurntable.Ensure(engine, template, note => { if (logger != null) logger.LogNote(note); });

                var flow = FindAnyObjectByType<AdaptiveAR.UI.AppFlowController>(FindObjectsInactive.Include);
                RectTransform panel = flow != null && flow.StepPanel != null ? flow.StepPanel.transform as RectTransform : null;
                if (_turntable != null && _turntable.AssemblyRotationEnabled && panel != null) _resetViewButton = MakeResetViewButton(panel, flow.BackButtonTemplate);
            }
            catch (Exception e) { Debug.LogError("[Turntable] setup threw: " + e); }
        }

        private GameObject MakeResetViewButton(RectTransform panel, GameObject template)
        {
            Transform existing = panel.Find("ResetViewButton");
            if (existing != null) return existing.gameObject;

            GameObject go;
            if (template != null)
            {
                go = Instantiate(template, panel);
                var old = go.GetComponent<UnityEngine.UI.Button>(); if (old != null) DestroyImmediate(old);
                foreach (Transform extra in go.transform) if (extra.GetComponent<TMPro.TextMeshProUGUI>() == null) Destroy(extra.gameObject);
                foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>()) if (mb != null && !(mb is UnityEngine.UI.Image)) Destroy(mb);
            }
            else
            {
                go = new GameObject("ResetViewButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
                go.transform.SetParent(panel, false);
                var l = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TMPro.TextMeshProUGUI));
                l.transform.SetParent(go.transform, false);
            }
            go.name = "ResetViewButton";
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 24f); rt.sizeDelta = new Vector2(170f, 44f); rt.localScale = Vector3.one;
            var text = go.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
            if (text != null)
            {
                text.text = "Reset View"; text.enableAutoSizing = false; text.fontSize = 16f; text.alignment = TMPro.TextAlignmentOptions.Center;
                var trt = text.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            }
            var button = go.AddComponent<UnityEngine.UI.Button>();
            button.onClick.AddListener(() => { if (_turntable != null) _turntable.ResetView(); });
            go.SetActive(false);
            return go;
        }

        private void LateUpdate()
        {
            if (_resetViewButton != null && _turntable != null)
            {
                bool show = _turntable.IsRotated && !SequenceFinished;
                if (_resetViewButton.activeSelf != show) _resetViewButton.SetActive(show);
            }
        }

        /// <summary>Completion: only the finished engine (on its oil pan) and the result UI remain.</summary>
        private void CompletionCleanup()
        {
            try
            {
                if (presenter != null) presenter.ClearGuidance();
                var arrow = FindAnyObjectByType<AdaptiveAR.UI.GuidanceArrow>(FindObjectsInactive.Include);
                if (arrow != null) arrow.HideForGood();
                if (stepManager != null) stepManager.SetMatVisible(false, "assembly_complete", 0);
                InstalledPistonPivot.LockAll("assembly complete");
                if (_resetViewButton != null) _resetViewButton.SetActive(false);
                // The finished engine is one inspection object: free rotation in place, two-hand zoom 0.6-1.6x.
                if (_turntable != null) _turntable.EnterInspectionMode(0.6f, 1.6f);

                Transform engine = stepManager != null && stepManager.oilPan != null ? stepManager.oilPan.transform : null;
                Transform offset = engine != null ? engine.Find("Offset") : null;
                int hidden = 0;
                var names = new System.Collections.Generic.List<string>();
                if (offset != null)
                {
                    foreach (Transform child in offset)
                    {
                        if (child == null) continue;
                        if (child.name.StartsWith("tray") || child.name.StartsWith("Plane") || child.name.StartsWith("PistonKits") || child.name.StartsWith("Worksurface") || child.name.StartsWith("WorkSurface"))
                        { if (child.gameObject.activeSelf && HideGuard.CanHide(child.gameObject, "CompletionCleanup(container)")) { child.gameObject.SetActive(false); hidden++; names.Add(child.name); } }
                    }
                }
                // Every loose, never-installed part (anything with a tray behaviour that is not locked,
                // not installed and not on the engine) disappears with the trays - registered or not.
                foreach (DropIntoTray tray in FindObjectsByType<DropIntoTray>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    GameObject go = tray.gameObject;
                    if (_turntable != null && go.transform.IsChildOf(_turntable.transform)) continue;
                    if (go.GetComponent<InstalledPartSentinel>() != null) continue;
                    var padlock = go.GetComponent<PlacementLock>();
                    if (padlock != null && padlock.IsLocked) continue;
                    if (go.activeSelf && HideGuard.CanHide(go, "CompletionCleanup(loose)", allowLooseParts: true)) { go.SetActive(false); hidden++; names.Add(go.name); }
                }
                Debug.Log($"[Session] completion cleanup: {hidden} tray/mat/loose object(s) hidden [{string.Join(", ", names)}]; the engine, its installed (locked) parts and the result UI remain.");
            }
            catch (Exception e) { Debug.LogError("[Session] completion cleanup threw: " + e); }
        }

        /// <summary>
        /// Latest physiological reading, or null when nothing is attached.
        /// A future BLE source sets this; nothing today writes to it, and the logger
        /// records every field as null rather than inventing a value.
        /// </summary>
        public PhysiologicalSample LatestPhysiological { get; set; }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        private void OnEnable()
        {
            PartWatch.Logger = logger;   // part_lifecycle lines go into the session JSONL beside the decision data

            if (stepRunner != null)
            {
                stepRunner.OnStepChanged += HandleStepChanged;
                stepRunner.OnSequenceComplete += HandleSequenceComplete;
            }

            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged += HandleSupportChanged;

            if (validator != null)
            {
                validator.OnAttemptEvaluated += HandleAttempt;
                validator.OnStepValidated += HandleStepValidated;
                validator.OnPartGrabbed += HandlePartGrabbed;
                validator.OnPartReleased += HandlePartReleased;
                validator.OnWrongPartGrabbed += HandleWrongPartGrabbed;
                validator.OnWrongPartReleased += HandleWrongPartReleased;
                validator.OnComponentDropped += HandleComponentDropped;
                validator.OnAcknowledgedByGrab += HandleAcknowledgedByGrab;
                validator.OnProximityChanged += HandleProximityChanged;
            }

            if (toolInteraction != null)
                toolInteraction.OnToolActionCompleted += HandleToolActionCompleted;

            if (presenter == null) presenter = GetComponent<StepPresenter>();
            if (registry == null) registry = GetComponent<GuidanceRegistry>();
            if (stepManager == null) stepManager = GetComponent<StepManager>();

            if (stepManager != null && stepManager.Placement != null && !_placementSubscribed)
            {
                stepManager.Placement.OnPlaced += HandleWorkspacePlaced;
                _placementSubscribed = true;
            }
        }

        private void OnDisable()
        {
            if (stepRunner != null)
            {
                stepRunner.OnStepChanged -= HandleStepChanged;
                stepRunner.OnSequenceComplete -= HandleSequenceComplete;
            }

            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged -= HandleSupportChanged;

            if (validator != null)
            {
                validator.OnAttemptEvaluated -= HandleAttempt;
                validator.OnStepValidated -= HandleStepValidated;
                validator.OnPartGrabbed -= HandlePartGrabbed;
                validator.OnPartReleased -= HandlePartReleased;
                validator.OnWrongPartGrabbed -= HandleWrongPartGrabbed;
                validator.OnWrongPartReleased -= HandleWrongPartReleased;
                validator.OnComponentDropped -= HandleComponentDropped;
                validator.OnAcknowledgedByGrab -= HandleAcknowledgedByGrab;
                validator.OnProximityChanged -= HandleProximityChanged;
            }

            if (toolInteraction != null)
                toolInteraction.OnToolActionCompleted -= HandleToolActionCompleted;

            if (stepManager != null && stepManager.Placement != null)
                stepManager.Placement.OnPlaced -= HandleWorkspacePlaced;
            _placementSubscribed = false;
        }

        private static readonly Color FarRed = new Color(0.93f, 0.3f, 0.26f);
        private static readonly Color NearAmber = new Color(0.96f, 0.62f, 0.14f);

        /// <summary>
        /// FAR: red tint on the held part. NEAR (in the zone, not yet valid): amber. VALID:
        /// the green success pulse (HandleAttempt). Nothing held: authored colour.
        /// </summary>
        private void HandleProximityChanged(StepValidator.Proximity state, Transform part)
        {
            if (!spatialPulses || part == null) return;
            try
            {
                // FAR = authored colour (exploring is not an error); NEAR = amber; anything
                // else = authored. Red is reserved for a counted failed placement.
                switch (state)
                {
                    case StepValidator.Proximity.Near: AdaptiveAR.UI.FeedbackPulse.Hold(part, NearAmber); break;
                    default: AdaptiveAR.UI.FeedbackPulse.Release(part); break;
                }
            }
            catch (Exception e) { Debug.LogError("[Session] proximity tint threw: " + e); }
            RaiseStateChanged();
        }

        private void HandlePartGrabbed(string partKey, Transform part)
        {
            if (logger != null) logger.LogComponentGrabbed(partKey);
            if (haptics && part != null) HapticFeedback.Pulse(HapticFeedback.Kind.Grab, part.position);
        }

        private void HandlePartReleased(string partKey, Transform part)
        {
            if (logger != null) logger.LogComponentReleased(partKey);
        }

        private void HandleWrongPartGrabbed(string partKey, Transform part)
        {
            // A wrong component in the hand is a behavioural error: one per grab, never
            // per frame (the validator raises this on the grab transition only).
            _errorsOnStep++;
            _errorsTotal++;
            NoteRecentError();
            PushEnvelope();

            try { OnWrongComponentGrabbed?.Invoke(partKey); } catch (Exception e) { Debug.LogError("[Session] OnWrongComponentGrabbed handler threw: " + e); }

            // Four independent channels; one failing must not silence the others.
            string requested = validator != null ? validator.PartKey : null;
            string log = "skipped", haptic = "skipped", visual = "skipped", spoken = "skipped";

            try { if (logger != null) { logger.LogWrongComponent(partKey, requested); log = "ok"; } }
            catch (Exception e) { log = "threw"; Debug.LogError("[WrongPart] log threw: " + e); }

            try { if (haptics && part != null) { HapticFeedback.Pulse(HapticFeedback.Kind.WrongPart, part.position); haptic = "ok"; } }
            catch (Exception e) { haptic = "threw"; Debug.LogError("[WrongPart] haptic threw: " + e); }

            try { if (spatialPulses && part != null) { AdaptiveAR.UI.FeedbackPulse.Invalid(part, 0.5f); visual = "ok"; } }
            catch (Exception e) { visual = "threw"; Debug.LogError("[WrongPart] visual pulse threw: " + e); }

            // One spoken warning per distinct wrong-grab episode (the validator raises this on
            // the grab transition only; the speech layer adds a cooldown on top).
            try
            {
                // One spoken warning per grab EPISODE: the validator raises this event exactly
                // once per grab and closes the episode on release, so a time cooldown would only
                // silence a legitimate second episode. 0.3 s guards against a double dispatch.
                var speech = InstructionSpeech.Ensure(null);
                if (speech == null) spoken = "no speech component";
                else if (speech.PlayFeedbackClipGated(wrongPartClip, "wrong component", 0.3f)) spoken = "clip";
                else spoken = speech.SpeakFeedbackGated(wrongPartPhrase, 0.3f) ? "sentence" : "none";
            }
            catch (Exception e) { spoken = "threw"; Debug.LogError("[WrongPart] speech threw: " + e); }

            Debug.Log($"[WrongPart] feedback object={(part != null ? part.name : "?")} key={partKey} requested={requested} " +
                      $"log={log} haptic={haptic} visual={visual} spoken={spoken} (card text via validator event)");
        }

        /// <summary>
        /// Serialized clip first (the proven path), then the sentence's Resources clip,
        /// then a synthesised tone so the cue is never silent.
        /// </summary>
        private void Feedback(AudioClip clip, string phrase, ProceduralTones.Kind tone, string label)
        {
            var speech = InstructionSpeech.Ensure(null);
            if (speech == null) return;

            if (speech.PlayFeedbackClip(clip, label)) return;
            if (speech.SpeakFeedback(phrase)) return;
            speech.PlayTone(tone);
        }

        private void HandleWrongPartReleased(string partKey, Transform part)
        {
            if (logger != null) logger.LogNote("wrong_component_released:" + partKey);
            Debug.Log($"[WrongPart] released object={(part != null ? part.name : "?")} key={partKey}: episode over, the next wrong grab warns again.");
        }

        /// <summary>The correct part was let go far from the target. An interaction event, not an error.</summary>
        private void HandleComponentDropped(string partKey, Transform part)
        {
            if (logger != null) logger.LogComponentDropped(partKey, validator != null ? validator.PartKey : null);
        }

        /// <summary>"Locate X and pick it up": picking it up completes the instruction.</summary>
        private void HandleAcknowledgedByGrab(string partKey, Transform part)
        {
            if (!IsReadState) return;
            if (logger != null) logger.LogNote("instruction_acknowledged_by_pickup:" + partKey);
            if (haptics && part != null) HapticFeedback.Pulse(HapticFeedback.Kind.Grab, part.position);
            RequestAdvance("pick-up acknowledgement", "picked_up");
        }

        /// <summary>True while the current action is the "Complete Remaining Pistons" shortcut.</summary>
        public bool IsAutoCompleteAction
        {
            get
            {
                AssemblyAction a = workflow != null ? workflow.CurrentAction : null;
                return a != null && a.enabled && a.autoCompleteRemaining;
            }
        }

        private void HandleWorkspacePlaced(bool reposition)
        {
            var p = stepManager != null ? stepManager.Placement : null;
            if (logger == null || p == null) return;

            logger.LogWorkspacePlaced(p.ActiveProvider.ToString(), p.LastPlacedPosition,
                                      p.LastSurfaceNormal, p.LastNormalConfidence, reposition);
        }

        /// <summary>A tool action satisfies its substep the same way a placement does.</summary>
        private void HandleToolActionCompleted()
        {
            if (workflow != null) CompleteActionAndAdvance("tool_action");
        }

        private void Start()
        {
            // The placement provider may be created in StepManager.Awake after this
            // component's OnEnable ran; make sure its events are logged either way.
            if (!_placementSubscribed && stepManager != null && stepManager.Placement != null)
            {
                stepManager.Placement.OnPlaced += HandleWorkspacePlaced;
                _placementSubscribed = true;
            }

            if (beginSessionOnStart)
                BeginSession();
        }

        public void BeginSession()
        {
            if (SessionActive) return;

            SessionActive = true;
            SequenceFinished = false;
            _overallMs = 0f;

            if (logger != null)
                logger.BeginSession();

            PushEnvelope();
        }

        public void EndSession(string reason)
        {
            if (!SessionActive) return;

            SessionActive = false;

            if (logger != null)
                logger.EndSession(reason);
        }

        private void Update()
        {
            if (!SessionActive || SequenceFinished) return;

            float dt = Time.deltaTime * 1000f;
            _overallMs += dt;

            // Elapsed-time notification: one distinct signal when the assembly has run for
            // timerMinutes. Nothing is terminated by it.
            if (!_timerFired && _assemblyStartMs >= 0f && timerMinutes > 0f && _overallMs - _assemblyStartMs >= timerMinutes * 60000f)
            {
                _timerFired = true;
                var sp = InstructionSpeech.Ensure(null);
                if (sp != null) sp.PlayTone(ProceduralTones.Kind.Timer);
                if (logger != null) logger.LogNote($"timer_elapsed:{timerMinutes:F0}min");
                Debug.Log($"[Session] {timerMinutes:F0}-minute timer elapsed: signal played once; the assembly continues.");
            }

            if (_stepOpen)
                _stepMs += dt;
        }

        // =====================================================================
        // Step tracking
        // =====================================================================

        /// <summary>
        /// THE ONE PROGRESSION GATE. READ actions may advance by Continue (or a pick-up
        /// acknowledgement); the auto-complete action by its own button; a PHYSICAL action
        /// ONLY by validator success - never by Continue, never because validation could not
        /// be armed. Every caller that advances goes through RequestAdvance().
        /// </summary>
        public bool CanAdvance
        {
            get
            {
                if (!SessionActive || SequenceFinished) return false;
                if (workflow == null) return true;                     // no model: never gate

                AssemblyAction action = workflow.CurrentAction;
                if (action == null) return true;                       // stage without actions
                if (!action.enabled) return true;                      // designed, not performable
                if (!action.RequiresPhysicalValidation) return true;    // READ / AUTO: a button may advance

                return workflow.CurrentActionComplete;                  // PHYSICAL: validation only
            }
        }

        public enum ActionType { READ, PHYSICAL, AUTO, NONE }

        public ActionType CurrentActionType
        {
            get
            {
                AssemblyAction a = workflow != null ? workflow.CurrentAction : null;
                if (a == null || !a.enabled) return ActionType.NONE;
                if (a.autoCompleteRemaining) return ActionType.AUTO;
                return a.RequiresPhysicalValidation ? ActionType.PHYSICAL : ActionType.READ;
            }
        }

        /// <summary>
        /// Single entry point for every manual advance request (Continue, Back/Continue,
        /// pick-up acknowledgement, researcher skip). Logs the decision; refuses PHYSICAL.
        /// </summary>
        private bool RequestAdvance(string requestedBy, string reason)
        {
            AssemblyAction a = workflow != null ? workflow.CurrentAction : null;
            ActionType type = CurrentActionType;
            string id = a != null ? a.Id : "<none>";

            bool allowed = CanAdvance && type != ActionType.PHYSICAL;
            if (type == ActionType.PHYSICAL)
                allowed = workflow != null && workflow.CurrentActionComplete;   // only ever true via validation

            Debug.Log($"[Progression] action={id} type={type} advance requested by={requestedBy} allowed={allowed}");
            if (!allowed)
            {
                if (logger != null) logger.LogNote("advance_blocked:" + requestedBy);
                RaiseStateChanged();
                return false;
            }

            if (workflow != null) CompleteActionAndAdvance(reason);
            else AdvanceStep(reason);
            return true;
        }

        /// <summary>
        /// READ state: the current action is an acknowledgement (read, then Continue).
        /// Otherwise the action is physical: INTERACT state, no Continue.
        /// </summary>
        public bool IsReadState
        {
            get
            {
                if (!SessionActive || SequenceFinished || workflow == null) return false;
                AssemblyAction a = workflow.CurrentAction;
                return a != null && a.enabled && !a.RequiresPhysicalValidation;
            }
        }

        /// <summary>
        /// Back is valid only from a physical action whose preceding enabled action in the
        /// same stage is an instruction. It re-reads that instruction; it never undoes a
        /// placement or touches completed-action state.
        /// </summary>
        public bool CanGoBack
        {
            get { return PreviousInstructionIndex() >= 0; }
        }

        private int PreviousInstructionIndex()
        {
            if (!SessionActive || SequenceFinished || workflow == null) return -1;
            StepData stage = workflow.CurrentStage;
            AssemblyAction current = workflow.CurrentAction;
            if (stage == null || stage.actions == null || current == null) return -1;
            if (!current.RequiresPhysicalValidation) return -1;      // already reading

            for (int i = workflow.ActionIndex - 1; i >= 0; i--)
            {
                AssemblyAction a = stage.actions[i];
                if (a == null || !a.enabled) continue;
                return a.RequiresPhysicalValidation ? -1 : i;         // stop at the first enabled one
            }
            return -1;
        }

        /// <summary>Re-enters the preceding instruction. Safe: completed actions stay completed.</summary>
        public void GoBackToInstruction()
        {
            int index = PreviousInstructionIndex();
            if (index < 0) return;

            if (logger != null) logger.LogNote("back_to_instruction:" + (workflow.CurrentAction != null ? workflow.CurrentAction.Id : ""));
            if (validator != null) validator.Clear();
            EnterAction(index);
        }

        /// <summary>Logged by the card when Continue is pressed in the READ state.</summary>
        public void NoteInstructionAcknowledged()
        {
            if (!IsReadState || logger == null || workflow == null || workflow.CurrentAction == null) return;
            logger.LogNote("instruction_acknowledged:" + workflow.CurrentAction.Id);
        }

        /// <summary>Short reason the Next control is unavailable, for the participant card.</summary>
        public string BlockedReason
        {
            get
            {
                if (CanAdvance) return null;
                AssemblyAction a = workflow != null ? workflow.CurrentAction : null;
                return a != null && a.kind == ActionKind.Fasten
                    ? "Fit the fastener to continue"
                    : "Place the part to continue";
            }
        }

        // ---------------- action lifecycle ----------------

        /// <summary>
        /// Enters one action: tears down the previous one, arms validation, logs it.
        /// Everything owned by the previous action is cleared here, which is what stops
        /// stale instructions and orphaned ghosts surviving into the next action.
        /// </summary>
        private void EnterAction(int index)
        {
            if (workflow == null) return;

            StepData stage = workflow.CurrentStage;
            if (stage == null) return;

            int resolved = stage.NextEnabledActionIndex(index);

            if (resolved < 0)
            {
                CompleteStageAndAdvance();
                return;
            }

            workflow.EnterAction(resolved);
            AssemblyAction action = workflow.CurrentAction;

            // Each step is isolated: a failure in one subsystem is logged loudly and must not
            // prevent the others (guidance, mat, logging) from running.
            _currentActionArmed = false;
            if (validator != null)
            {
                try
                {
                    _currentActionArmed = validator.BeginAction(action);
                    if (action != null && action.enabled && !action.RequiresPhysicalValidation)
                        validator.BeginAcknowledge(action);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Session] validator.BeginAction threw for '{(action != null ? action.Id : "?")}': {e}");
                }
            }

            // The mat exists while the partial piston is being built on it.
            if (stepManager != null && action != null && action.enabled)
            {
                string role = GuidanceRegistry.RoleOf(action.partKey);
                bool onMat = role == "PistonHead" || role == "ConnectingRod" || role == "ConnectingPin";
                if (onMat)
                {
                    try { stepManager.SetMatVisible(true, "piston_action:" + action.Id, KitNumber(action.partKey)); }
                    catch (Exception e) { Debug.LogError("[Session] mat show threw: " + e); }
                }
            }

            // The grab event source is session-wide: armed for AUTO/NONE actions too, so a
            // grab is logged whatever the state.
            try { if (validator != null) validator.EnsureGrabSource(); }
            catch (Exception e) { Debug.LogError("[Session] validator.EnsureGrabSource threw: " + e); }

            try
            {
                // Either-side actions hand the presenter every free target so all of them show.
                if (presenter != null)
                    presenter.PresentAction(action, validator != null && validator.IsActive && validator.EitherSideTargets ? validator.ActiveTargetKeys : null);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Session] presenter.PresentAction threw for '{(action != null ? action.Id : "?")}': {e}");
            }
            InstalledPartSentinel.ReportAll("next-action:" + (action != null ? action.Id : "<none>"));
            LogDecisionContextProbe("action_enter:" + (action != null ? action.Id : "<none>"));
            try { OnActionEntered?.Invoke(action); } catch (Exception e) { Debug.LogError("[Session] OnActionEntered handler threw: " + e); }

            if (logger != null && action != null)
                logger.LogActionEnter(resolved, action.Id, action.kind.ToString(),
                                      action.enabled, action.disabledReason);

            LogActionLine(action);
            try { LogPhysicalActionDiagnostic(stage, action); }
            catch (Exception e) { Debug.LogError("[Session] action diagnostic threw: " + e); }
            RaiseStateChanged();
        }

        /// <summary>
        /// One diagnostic per PHYSICAL action entered: the resolved real part, the resolved
        /// target, whether the validator armed, and the sentence the card must be showing.
        /// Tagged [CrankshaftAction] for the crankshaft, [PhysicalAction] otherwise.
        /// </summary>
        private void LogPhysicalActionDiagnostic(StepData stage, AssemblyAction action)
        {
            if (action == null || !action.RequiresPhysicalValidation) return;

            string tag = "[" + StepValidator.RoleTag(action.partKey) + "Action]";
            SupportLevel level = supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal;
            string text = GuidanceText.InstructionFor(stage, action, level);

            Transform real = validator != null ? validator.CurrentPart : null;
            Transform target = validator != null ? validator.CurrentTarget : null;
            bool armed = validator != null && validator.IsActive;

            string realDesc, targetDesc;
            if (real != null) realDesc = $"{real.name}#{real.GetInstanceID()} ({PathOf(real)})";
            else
            {
                bool resolves = registry != null && !string.IsNullOrEmpty(action.partKey) && registry.RoleCandidates(action.partKey).Count > 0;
                realDesc = "<null> reason=\"" + (validator == null ? "no validator" : (validator.ArmFailureReason ?? (resolves ? "validator did not arm" : $"registry cannot resolve '{action.partKey}'"))) + "\"";
            }
            if (target != null) targetDesc = $"{target.name}#{target.GetInstanceID()} ({PathOf(target)}) activeInHierarchy={target.gameObject.activeInHierarchy}";
            else
            {
                bool resolves = registry != null && !string.IsNullOrEmpty(action.targetKey) && registry.TryResolveQuiet(action.targetKey, out GameObject tg) && tg != null;
                targetDesc = "<null> reason=\"" + (resolves ? "resolves in the registry but the validator did not take it (not armed)" : $"registry cannot resolve '{action.targetKey}'") + "\"";
            }

            Debug.Log($"{tag} actionId={action.Id} requestedPart={action.partKey} realPart={realDesc} target={targetDesc} " +
                      $"validatorArmed={armed}{(armed ? "" : $" reason=\"{(validator != null ? validator.ArmFailureReason ?? "unknown" : "no validator")}\"")} " +
                      $"guidanceText=\"{text}\"{(string.IsNullOrEmpty(text) ? " reason=\"action.instruction is empty and no L1 override\"" : "")}");

            if (real == null || target == null || !armed || string.IsNullOrEmpty(text))
                Debug.LogError($"{tag} '{action.Id}' is incomplete: realPart={(real != null)} target={(target != null)} armed={armed} text={!string.IsNullOrEmpty(text)}. " +
                               "A PHYSICAL action that is not armed stays blocked until it validates.");
        }

        private static string PathOf(Transform t)
        {
            if (t == null) return "<null>";
            var sb = new System.Text.StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        /// <summary>One concise line per action entered; loud error on an inconsistent PHYSICAL action.</summary>
        private void LogActionLine(AssemblyAction action)
        {
            ActionType type = CurrentActionType;
            string level = supportLevel != null ? supportLevel.CurrentLevel.ToString().Substring(0, 2) : "L?";
            bool continueShown = CanAdvance;
            bool hasValidator = validator != null && validator.IsActive;
            string ghost = presenter != null ? presenter.DescribeActiveGhosts() : "-";

            Debug.Log($"[Action] id={(action != null ? action.Id : "<none>")} type={type} support={level} " +
                      $"continue={continueShown} validator={hasValidator} ghost={ghost}" +
                      (type == ActionType.PHYSICAL && !hasValidator ? $" armFailure=\"{validator?.ArmFailureReason}\"" : ""));

            if (type == ActionType.PHYSICAL && (continueShown || !hasValidator))
                Debug.LogError($"[Action] PHYSICAL action '{(action != null ? action.Id : "?")}' is inconsistent " +
                               $"(continue={continueShown}, validator={hasValidator}). Progression is refused until it validates.");
        }

        /// <summary>Marks the active action done and moves on, or finishes the stage.</summary>
        private void CompleteActionAndAdvance(string reason)
        {
            // The action's narration is obsolete the moment the action completes.
            var sp = InstructionSpeech.Ensure(null);
            if (sp != null) sp.StopInstruction("action complete: " + reason);   // the success cue is kept; the next instruction waits for it

            if (workflow == null) { AdvanceStep(reason); return; }

            AssemblyAction action = workflow.CurrentAction;
            workflow.CompleteCurrentAction();

            if (logger != null && action != null)
                logger.LogActionComplete(workflow.ActionIndex, action.Id, _stepMs, reason);

            // A validated placement or a tool action done is positive evidence; an acknowledged
            // READ step is not. Raised before the next action is entered so the trigger name
            // says what happened; the scheduler's gate decides whether a request follows.
            if (action != null && (action.RequiresPhysicalValidation || reason == "tool_action"))
            {
                _successesSinceChange++;
                PushEnvelope();
                try { OnActionCompleted?.Invoke(action); } catch (Exception e) { Debug.LogError("[Session] OnActionCompleted handler threw: " + e); }
            }

            if (validator != null) validator.Clear();

            StepData stage = workflow.CurrentStage;
            int next = stage != null ? stage.NextEnabledActionIndex(workflow.ActionIndex + 1) : -1;

            if (next >= 0) EnterAction(next);
            else CompleteStageAndAdvance();
        }

        private void CompleteStageAndAdvance()
        {
            if (workflow != null)
                workflow.CompleteStage(workflow.StageIndex);

            if (presenter != null)
                presenter.PresentAction(null);

            AdvanceStep("stage_complete");
        }

        private void HandleStepChanged(StepData step, int index, string reason)
        {
            // Close the outgoing step first, so its duration is recorded before the new
            // one resets the timer.
            if (_stepOpen && _currentStep != null)
                CloseStep(reason);

            _currentStep = step;
            _currentIndex = index;
            _stepMs = 0f;
            _attemptsOnStep = 0;
            _errorsOnStep = 0;
            _supportChangesOnStep = 0;
            _errorsSinceChange = 0;
            _successesSinceChange = 0;
            _lastErrorStepMs = -1f;
            _lastChangeStepMs = -1f;
            _stepOpen = true;

            PushEnvelope();

            if (logger != null)
            {
                logger.LogStepEnter(index, step.StepIdentifier, stepRunner != null ? stepRunner.StepCount : 0,
                                    step.taskComplexity, ExpectedMsOrNull(step));
            }

            if (!_selfCheckDone) { _selfCheckDone = true; RunSelfCheck(); EnsureTurntable(); _assemblyStartMs = _overallMs; }
            InstructionSpeech.CurrentActionProvider = () => workflow != null && workflow.CurrentAction != null ? workflow.CurrentAction.Id : "<none>";

            // A piston whose stage is over is locked for good (its one-axis pivot too).
            InstalledPistonPivot.LockAll("stage changed");

            // Enter the stage in the authoritative model, then its first real action.
            if (workflow != null)
            {
                workflow.SetPhase(WorkflowPhase.Assembly);
                workflow.EnterStage(index);
                EnterAction(0);
            }
            else if (validator != null)
            {
                validator.Clear();
            }

            RaiseStateChanged();
        }

        private void CloseStep(string reason)
        {
            if (logger != null && _currentStep != null)
            {
                logger.LogStepComplete(_currentIndex, _currentStep.StepIdentifier,
                                       _stepMs, _attemptsOnStep, _errorsOnStep, reason);
            }
            _stepOpen = false;
        }

        private static float? ExpectedMsOrNull(StepData step)
        {
            if (step == null || step.expectedDurationSeconds <= 0f) return null;
            return step.expectedDurationSeconds * 1000f;
        }

        // =====================================================================
        // Validation
        // =====================================================================

        private void HandleAttempt(bool success, float posErr, float rotErr, string trigger)
        {
            _attemptsOnStep++;
            _attemptsTotal++;

            // Discrete error model: one counted error at most per deliberate placement attempt
            // (released near the target without fitting). Carrying the part in and out of the
            // zone, holding it, or adjusting it during the same attempt is never an error.
            bool counted = !success && validator != null && validator.LastAttemptCounted;
            if (counted) { try { OnCountedError?.Invoke(); } catch (Exception e) { Debug.LogError("[Session] OnCountedError handler threw: " + e); } }
            if (counted)
            {
                _errorsOnStep++;
                _errorsTotal++;
                NoteRecentError();
            }

            PushEnvelope();

            if (logger != null && _currentStep != null)
            {
                float posTol = validator != null ? validator.PositionTolerance : _currentStep.positionToleranceMeters;
                float rotTol = validator != null ? validator.RotationTolerance : _currentStep.rotationToleranceDegrees;

                // One line per ATTEMPT (zone entered and left, or released inside it) or
                // per success - never per release far away, never per frame.
                AssemblyAction current = workflow != null ? workflow.CurrentAction : null;
                logger.LogValidationAttempt(_attemptsOnStep, success, posErr, rotErr,
                                            posTol, rotTol,
                                            validator != null ? validator.ActiveInstanceKey ?? validator.PartKey : null,
                                            validator != null ? validator.TargetKey : null,
                                            trigger,
                                            success ? "placement_success" : (validator != null ? validator.LastErrorType : null),
                                            current != null ? current.Id : null,
                                            validator != null ? validator.PartKey : null,
                                            validator != null ? validator.ActiveInstanceKey : null,
                                            validator != null && validator.CurrentPart != null ? validator.CurrentPart.name : null,
                                            validator != null ? validator.LastAttemptPositionOk : (bool?)null,
                                            validator != null ? validator.LastAttemptOrientationOk : (bool?)null,
                                            validator != null ? validator.AttemptId : 0,
                                            success ? false : counted);

                if (success)
                    logger.LogComponentLocked(validator != null ? validator.ActiveInstanceKey : null, posErr, rotErr,
                                              validator != null ? validator.PartKey : null,
                                              validator != null && validator.CurrentPart != null ? validator.CurrentPart.name : null);
            }

            Transform part = validator != null ? validator.CurrentPart : null;
            var speech = InstructionSpeech.Ensure(null);
            if (success)
            {
                // Success: chime + haptic + green pulse. No speech - the chime is clearer. Any
                // failure pulse still running is cancelled (authored state restored) first, so
                // the green pulse ends on the authored material, never on a red block.
                if (haptics && part != null) HapticFeedback.Pulse(HapticFeedback.Kind.Success, part.position);
                if (spatialPulses && part != null)
                {
                    AdaptiveAR.UI.FeedbackPulse.Cancel(part);
                    AdaptiveAR.UI.FeedbackPulse.Success(part, 0.8f);
                }
                if (speech != null) speech.PlayCue(ProceduralTones.Kind.Success);
            }
            else if (counted)
            {
                // One deliberate failed placement: one brief tone, warning haptic, amber pulse.
                // Never speech. Abandoned attempts (carried out of the zone) get nothing.
                // Red is for exactly this: a counted failed placement.
                if (haptics && part != null) HapticFeedback.Pulse(HapticFeedback.Kind.WrongPart, part.position);
                if (spatialPulses && part != null) AdaptiveAR.UI.FeedbackPulse.Pulse(part, FarRed, 0.5f);
                if (speech != null) speech.PlayCue(ProceduralTones.Kind.Invalid);
            }

            RaiseStateChanged();
        }

        private void HandleStepValidated()
        {
            // The ONLY way a PHYSICAL action advances.
            AssemblyAction a = workflow != null ? workflow.CurrentAction : null;
            string tag = "[" + StepValidator.RoleTag(a != null ? a.partKey : null) + "Success]";

            // Mark the action complete FIRST, then report, then advance through the gate.
            if (workflow != null) workflow.CompleteCurrentAction();
            Debug.Log($"{tag} snapped={(validator != null && validator.LastSuccessSnapped)} locked={(validator != null && validator.LastSuccessLocked)} " +
                      $"actionComplete={(workflow != null && workflow.CurrentActionComplete)} action={(a != null ? a.Id : "<none>")}");
            Debug.Log($"[Progression] action={(a != null ? a.Id : "<none>")} validation success -> allowed=true");

            // Installing the partial piston ends the mat's job for this piston.
            if (a != null && GuidanceRegistry.IsKitHandleKey(a.partKey) && stepManager != null)
                stepManager.SetMatVisible(false, "partial_piston_installed", KitNumber(a.partKey));

            if (workflow != null) CompleteActionAndAdvance("validation_passed");
            else AdvanceStep("validation_passed");
        }

        private static int KitNumber(string key)
        {
            string kit = GuidanceRegistry.KitOf(key);
            if (kit == null && GuidanceRegistry.IsKitHandleKey(key)) kit = key.Substring(5);
            if (kit == null) return 0;
            int.TryParse(kit.Substring(kit.Length - 1), out int n);
            return n;
        }

        /// <summary>
        /// Advances the sequence. Hooked to the confirm button so a step can be completed
        /// by hand when validation is not used, or when a researcher needs to move on.
        /// </summary>
        public void AdvanceStep(string reason)
        {
            if (!SessionActive || SequenceFinished || stepRunner == null) return;

            // Never let a confirm press start the sequence. The sequence begins only when
            // StepManager has locked the workspace (placed, or the marker found), so pressing
            // confirm beforehand must do nothing rather than skip the anchoring step.
            //
            // This is logged rather than silent: an unexplained dead button on device is
            // very hard to tell apart from a broken one, which is exactly what happened
            // when the Begin control went missing.
            if (!stepRunner.HasStarted)
            {
                if (logToConsole)
                    Debug.Log("[Session] Advance ignored: the sequence has not started yet. " +
                              "Place the workspace and press Begin first.");
                return;
            }

            if (stepRunner.IsOnLastStep)
            {
                CloseStep(reason);
                stepRunner.Advance(reason); // raises OnSequenceComplete
                return;
            }

            stepRunner.Advance(reason);
        }

        /// <summary>
        /// Parameterless overload for a UI Button. Refuses to skip unfinished physical work:
        /// the participant cannot press past a placement they have not made.
        /// </summary>
        public void AdvanceStepManually()
        {
            // The deliberate prototype shortcut runs BEFORE the advance it unlocks.
            if (IsAutoCompleteAction && !_autoCompleted)
            {
                int manual = CountBoundKits();
                int made = PistonAutoCompleter.CompleteRemaining(registry, manual);
                _autoCompleted = made > 0;
                if (logger != null) logger.LogRemainingPistonsAutoCompleted(made);
                if (made != PistonAutoCompleter.ExpectedCount)
                    Debug.LogError($"[Session] Auto-complete made {made} visual piston(s), expected {PistonAutoCompleter.ExpectedCount}.");
            }

            RequestAdvance("Continue button", IsAutoCompleteAction ? "auto_complete_confirm" : "manual_confirm");
        }

        private bool _autoCompleted;

        private int CountBoundKits()
        {
            int n = 0;
            if (registry == null) return 0;
            for (int k = 1; k <= 4; k++)
                if (registry.TryGetKitHandle($"PistonKit00{k}", out Transform h) && h != null) n++;
            return n;
        }

        /// <summary>Researcher control. Logged like any other request; still refused for PHYSICAL.</summary>
        public void SkipStep()
        {
            RequestAdvance("researcher skip", "researcher_skip");
        }

        private void HandleSequenceComplete()
        {
            if (SequenceFinished) return;

            SequenceFinished = true;

            if (workflow != null) workflow.SetPhase(WorkflowPhase.Complete);

            if (validator != null)
                validator.Clear();

            if (logToConsole)
                Debug.Log($"[Session] Assembly complete. " +
                          $"Total {_overallMs / 1000f:F1} s, {_errorsTotal} error(s), " +
                          $"{_supportChangesTotal} support change(s).");

            if (logger != null)
            {
                logger.LogNote("sequence_complete");
                int sec = Mathf.Max(0, Mathf.FloorToInt(_overallMs / 1000f));
                logger.LogNote($"run_summary: completion_time={sec / 60:00}:{sec % 60:00} total_attempts={_attemptsTotal} total_errors={_errorsTotal}");
            }

            // One distinct positive completion signal, once (the arbiter stops anything else first).
            var completionSpeech = InstructionSpeech.Ensure(null);
            if (completionSpeech != null) { completionSpeech.StopAll("assembly complete"); completionSpeech.PlayTone(ProceduralTones.Kind.Complete); }

            // The ParticipantCard owns the card text when present; writing here too would
            // mean two writers for one field. The completion screen shows the message.
            if (captionText != null && (presenter == null || !presenter.TextOwnedByParticipantCard))
                captionText.text = completionMessage;

            if (validator != null) validator.Clear();
            if (presenter != null) presenter.ClearGuidance();
            CompletionCleanup();

            OnSessionComplete?.Invoke();
            RaiseStateChanged();

            if (endSessionOnComplete)
                EndSession("sequence_complete");
        }

        public string CompletionMessage { get { return completionMessage; } }

        // =====================================================================
        // Support level
        // =====================================================================

        private void HandleSupportChanged(SupportLevel from, SupportLevel to, string reason)
        {
            _supportChangesOnStep++;
            _supportChangesTotal++;
            _errorsSinceChange = 0;
            _successesSinceChange = 0;
            _lastChangeStepMs = _stepMs;

            PushEnvelope();

            if (logger != null)
                logger.LogSupportChange(from, to, null, SourceFromReason(reason), reason, false);

            RaiseStateChanged();
        }

        private static SupportChangeSource SourceFromReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return SupportChangeSource.Manual;

            switch (reason)
            {
                case "step_default": return SupportChangeSource.StepDefault;
                case "debug_input": return SupportChangeSource.DebugInput;
                case "ai_decision": return SupportChangeSource.DecisionProvider;
                case "fallback": return SupportChangeSource.Fallback;
                default: return SupportChangeSource.Manual;
            }
        }

        // =====================================================================
        // Decision seam - complete today, unused until a provider exists
        // =====================================================================

        /// <summary>
        /// Builds the exact context a decision provider would receive. Logged verbatim as
        /// the "AI input", so it must reflect real state and never fabricate a value.
        /// </summary>
        public DecisionContext BuildDecisionContext()
        {
            var ctx = new DecisionContext
            {
                ParticipantId = logger != null ? logger.ParticipantId : null,
                SessionId = logger != null ? logger.SessionId : null,
                DecisionIndex = _decisionIndex,

                StepIndex = _currentIndex,
                StepId = _currentStep != null ? _currentStep.StepIdentifier : null,
                StepCount = stepRunner != null ? stepRunner.StepCount : 0,
                TaskComplexity = _currentStep != null ? _currentStep.taskComplexity : 0,
                OperatorExperience = operatorExperience,

                StepElapsedMs = _stepMs,
                OverallElapsedMs = _overallMs,
                StepExpectedMs = ExpectedMsOrNull(_currentStep),

                AttemptsOnStep = _attemptsOnStep,
                ErrorsOnStep = _errorsOnStep,
                ErrorsTotal = _errorsTotal,

                CurrentSupportLevel = supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal,
                SupportChangesOnStep = _supportChangesOnStep,
                SupportChangesTotal = _supportChangesTotal,

                ErrorsSinceLastSupportChange = _errorsSinceChange,
                SuccessfulActionsSinceLastSupportChange = _successesSinceChange,
                TimeSinceLastErrorMs = _lastErrorStepMs >= 0f ? (float?)Mathf.Max(0f, _stepMs - _lastErrorStepMs) : null,
                TimeSinceLastSupportChangeMs = _lastChangeStepMs >= 0f ? (float?)Mathf.Max(0f, _stepMs - _lastChangeStepMs) : null,

                Physiological = ResolvePhysiologicalSnapshot()
            };

            return ctx;
        }

        private void NoteRecentError()
        {
            _errorsSinceChange++;
            _lastErrorStepMs = _stepMs;
        }

        private AdaptiveAR.Physiology.PhysiologicalStateProvider _physiology;

        /// <summary>
        /// The physiological context for a decision: the provider's CURRENT valid snapshot
        /// (enough accepted samples, not stale), or null. Falls back to the last submitted
        /// sample only when no provider exists (e.g. a replayed context). Physiology is
        /// context only; nothing here changes the support level.
        /// </summary>
        private PhysiologicalSample ResolvePhysiologicalSnapshot()
        {
            if (_physiology == null) _physiology = FindAnyObjectByType<AdaptiveAR.Physiology.PhysiologicalStateProvider>(FindObjectsInactive.Include);
            if (_physiology != null) return _physiology.SnapshotSample();
            return LatestPhysiological;
        }

        /// <summary>Logs what a decision provider would have been given right now, without any decision being made.</summary>
        public void LogDecisionContextProbe(string where)
        {
            if (logger == null) return;
            try { logger.LogDecisionContextProbe(BuildDecisionContext(), where); }
            catch (Exception e) { Debug.LogWarning("[Session] context probe threw: " + e.Message, this); }
        }

        /// <summary>
        /// Applies a provider's response under the constrained action set, and records the
        /// whole exchange. Returns the level in force afterwards.
        ///
        /// A response that violates the constraint is NOT silently corrected: the violation
        /// is recorded and the safe fallback (KEEP_SUPPORT) applies, because constraint
        /// compliance is itself a measured outcome.
        /// </summary>
        public SupportLevel ApplyDecision(DecisionContext context, DecisionResponse response)
        {
            SupportLevel before = supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal;

            if (response == null)
            {
                response = DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, null, null);
            }

            bool applied = false;

            if (response.Status == ConstraintStatus.Ok && supportLevel != null)
            {
                bool saturates = SupportLevels.WouldSaturate(before, response.ParsedAction);
                applied = supportLevel.ApplyAction(response.ParsedAction, "ai_decision");

                if (saturates)
                    response.Status = ConstraintStatus.SaturatedAtBoundary;
            }
            // Any non-Ok status leaves the level untouched: KEEP_SUPPORT is the fallback.

            SupportLevel after = supportLevel != null ? supportLevel.CurrentLevel : before;

            LastDecisionResponse = response;
            _decisionIndex++;

            if (logger != null)
                logger.LogDecision(context, response, before, after, applied);

            // A real upward change only: the level moved, by an applied Ok decision.
            if (applied && (int)after > (int)before && response.Status == ConstraintStatus.Ok)
                StartSupportTransitionCue(before, after);

            RaiseStateChanged();
            return after;
        }

        // =====================================================================
        // Support transition cue (upward AI changes only)
        // =====================================================================

        private AdaptiveAR.UI.ParticipantCard _card;
        private Coroutine _transitionCue;

        private void StartSupportTransitionCue(SupportLevel from, SupportLevel to)
        {
            if (!transitionCueEnabled) return;
            if (_transitionCue != null) StopCoroutine(_transitionCue);
            _transitionCue = StartCoroutine(SupportTransitionCueRoutine(from, to));
        }

        /// <summary>
        /// By the time this runs the presenter has already re-rendered the step at the new
        /// level (it listens to OnSupportLevelChanged synchronously inside ApplyAction), so
        /// the replayed instruction is heard with the new guidance already visible.
        /// </summary>
        private System.Collections.IEnumerator SupportTransitionCueRoutine(SupportLevel from, SupportLevel to)
        {
            string actionId = workflow != null && workflow.CurrentAction != null ? workflow.CurrentAction.Id : null;
            string message = "skipped", tone = "skipped", replay = "skipped";

            try
            {
                if (_card == null) _card = FindAnyObjectByType<AdaptiveAR.UI.ParticipantCard>(FindObjectsInactive.Include);
                if (_card != null) { _card.ShowNotice(guidanceIncreasedMessage, transitionMessageSeconds); message = "card"; }
                else if (captionText != null) { captionText.text = guidanceIncreasedMessage; message = "caption"; }
                else message = "no surface";
            }
            catch (Exception e) { message = "threw"; Debug.LogError("[Transition] message threw: " + e); }

            var speech = InstructionSpeech.Ensure(null);
            try
            {
                if (speech != null) { speech.PlayTone(ProceduralTones.Kind.Notice); tone = "notice_tone"; }
                else tone = "no speech component";
            }
            catch (Exception e) { tone = "threw"; Debug.LogError("[Transition] tone threw: " + e); }

            if (logger != null) logger.LogSupportTransitionCue(from, to, actionId, guidanceIncreasedMessage, message, tone, "pending", _decisionIndex - 1);
            Debug.Log($"[Transition] {from} -> {to} action={actionId ?? "<none>"} message={message} tone={tone}; replaying instruction in {transitionReplayDelaySeconds:F1}s");

            yield return new WaitForSeconds(transitionReplayDelaySeconds);

            try
            {
                if (presenter == null) replay = "no presenter";
                else replay = presenter.ReplayCurrentActionInstruction("support_increase") ? "replayed" : "nothing to replay";
            }
            catch (Exception e) { replay = "threw"; Debug.LogError("[Transition] replay threw: " + e); }

            if (logger != null) logger.LogSupportTransitionCue(from, to, actionId, guidanceIncreasedMessage, message, tone, replay, _decisionIndex - 1);
            Debug.Log($"[Transition] replay={replay}");
            _transitionCue = null;
        }

        /// <summary>
        /// Convenience path for a provider: build context, ask, apply, log. Never throws -
        /// a provider failure becomes a recorded fallback rather than a lost session.
        /// </summary>
        public SupportLevel RequestAndApplyDecision(IDecisionProvider provider)
        {
            DecisionContext ctx = BuildDecisionContext();

            if (provider == null)
                return ApplyDecision(ctx, DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, null, null));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            DecisionResponse response;

            try
            {
                response = provider.RequestDecision(ctx);
            }
            catch (Exception e)
            {
                sw.Stop();
                Debug.LogWarning($"[Session] Decision provider threw: {e.Message}", this);
                response = DecisionResponse.Failed(ConstraintStatus.ProviderError,
                                                   (float)sw.Elapsed.TotalMilliseconds,
                                                   provider.ProviderId, null);
                return ApplyDecision(ctx, response);
            }

            sw.Stop();

            if (response == null)
            {
                response = DecisionResponse.Failed(ConstraintStatus.ProviderError,
                                                   (float)sw.Elapsed.TotalMilliseconds,
                                                   provider.ProviderId, null);
            }
            else if (response.LatencyMs <= 0f)
            {
                response.LatencyMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            return ApplyDecision(ctx, response);
        }

        /// <summary>
        /// The asynchronous path (a network model): build the context now, wait for the
        /// provider's routine, then apply through the same constrained ApplyDecision. A
        /// provider that throws or never answers becomes a recorded fallback.
        /// </summary>
        public System.Collections.IEnumerator RequestAndApplyDecisionRoutine(IAsyncDecisionProvider provider, Action<DecisionResponse, SupportLevel> onApplied)
        {
            DecisionContext ctx = BuildDecisionContext();
            if (provider == null)
            {
                SupportLevel l0 = ApplyDecision(ctx, DecisionResponse.Failed(ConstraintStatus.ProviderError, 0f, null, null));
                onApplied?.Invoke(LastDecisionResponse, l0);
                yield break;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DecisionResponse response = null;
            System.Collections.IEnumerator routine = null;
            try { routine = provider.RequestDecisionRoutine(ctx, r => response = r); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Session] async provider threw on start: {e.Message}", this);
                response = DecisionResponse.Failed(ConstraintStatus.ProviderError, (float)sw.Elapsed.TotalMilliseconds, provider.ProviderId, e.Message);
            }
            if (routine != null)
            {
                bool more = true;
                while (more)
                {
                    try { more = routine.MoveNext(); }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Session] async provider threw: {e.Message}", this);
                        response = DecisionResponse.Failed(ConstraintStatus.ProviderError, (float)sw.Elapsed.TotalMilliseconds, provider.ProviderId, e.Message);
                        break;
                    }
                    if (more) yield return routine.Current;
                }
            }
            sw.Stop();
            if (response == null) response = DecisionResponse.Failed(ConstraintStatus.ProviderError, (float)sw.Elapsed.TotalMilliseconds, provider.ProviderId, null);
            else if (response.LatencyMs <= 0f) response.LatencyMs = (float)sw.Elapsed.TotalMilliseconds;
            SupportLevel after = ApplyDecision(ctx, response);
            onApplied?.Invoke(LastDecisionResponse, after);
        }

        /// <summary>Records a physiological reading and keeps it as the latest context input.</summary>
        public void SubmitPhysiological(PhysiologicalSample sample)
        {
            LatestPhysiological = sample;

            if (logger != null && sample != null)
                logger.LogPhysiological(sample);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void PushEnvelope()
        {
            if (logger == null) return;

            logger.UpdateEnvelope(
                _currentIndex,
                _currentStep != null ? _currentStep.StepIdentifier : null,
                supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal,
                _stepMs, _attemptsOnStep, _errorsOnStep, _errorsTotal);
        }

        private void RaiseStateChanged()
        {
            OnStateChanged?.Invoke();
        }
    }
}
