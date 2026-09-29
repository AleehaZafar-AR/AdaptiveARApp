// File: UiRestyleSetup.cs
// Rebuilds the spatial interface's look and layout on the EXISTING canvases.
//
// It deliberately restyles rather than replaces. DemoUICanvas, OverviewCanvas and
// StatusCanvas each carry a PointableCanvas, a RayInteractable, a Surface and an
// ISDK_RayCanvasInteraction block. That wiring is what makes them pokeable in the
// headset and is fiddly to reproduce, so the canvases are kept and their contents
// are restyled and relaid out (CLAUDE.md constraint 10).
//
// The baseline problem, measured from the scene: the three panels span about 1.8 m
// at 1 m distance - roughly 84 degrees of horizontal field of view - and sit
// centred on the work area. The screenshots in docs/UIReference show them covering
// the engine. The new layout shrinks them and moves them off-centre so the
// physical V8 stays the visual focus.
//
// Role changes:
//   OverviewCanvas  -> task list      (left)
//   DemoUICanvas    -> instruction     (centre; also the home and complete screens)
//   StatusCanvas    -> status          (right; researcher detail hidden inside it)
//
// All three hang off a PanelRig parented to MarkerAnchor, so the interface travels
// with the marker and turns to face the viewer instead of sitting at fixed world
// coordinates that go stale the moment the marker or the operator moves.
//
// LAYOUT NUMBERS BELOW ARE AN ESTIMATE and need tuning in the headset. They are
// constants at the top of this file precisely so they are cheap to change and
// re-run (CLAUDE.md 4.1, 4.3).

using System.Collections.Generic;
using AdaptiveAR.Logging;
using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using AdaptiveAR.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AdaptiveAR.EditorTools
{
    public static class UiRestyleSetup
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";

        // --- layout, in PanelRig local space. The rig is anchored to the marker and
        // --- turns to face the viewer, so these are simple left / centre / right offsets.
        // --- Canvas scale is 0.001, so 1 canvas unit = 1 mm.
        private static readonly Vector2 InstructionSize = new Vector2(560f, 430f);
        private static readonly Vector2 TaskListSize = new Vector2(360f, 430f);
        private static readonly Vector2 StatusSize = new Vector2(360f, 430f);

        private const float PanelGap = 0.03f;   // metres between panels

        // Side panels are angled inwards so the triptych wraps slightly around the viewer.
        private const float SidePanelYaw = 16f;

        // =====================================================================
        // 1. VALIDATE
        // =====================================================================

        [MenuItem("AdaptiveAR/UI/1 - Validate UI Restyle (dry run)", false, 10)]
        public static void Validate()
        {
            Scene scene = SceneManager.GetActiveScene();
            var r = new System.Text.StringBuilder();
            r.AppendLine("=== UI Restyle Validation (DRY RUN - nothing modified) ===");
            r.AppendLine($"Active scene: '{scene.name}'" +
                         (scene.name != ExpectedSceneName ? "   WARNING wrong scene" : ""));
            r.AppendLine();

            foreach (string canvas in new[] { "DemoUICanvas", "OverviewCanvas", "StatusCanvas" })
            {
                GameObject go = Find(scene, canvas);
                if (go == null) { r.AppendLine($"  MISSING  {canvas}"); continue; }

                var rt = go.GetComponent<RectTransform>();
                r.AppendLine($"  found    {canvas}  size {rt.sizeDelta}  pos {rt.position}");
            }

            r.AppendLine();
            GameObject markerAnchor = Find(scene, "MarkerAnchor");
            r.AppendLine($"MarkerAnchor: {(markerAnchor == null ? "MISSING - run the Alignment tool first" : "found")}");
            r.AppendLine($"PanelRig:     {(Find(scene, "PanelRig") == null ? "will be created under MarkerAnchor" : "already exists")}");
            r.AppendLine();
            r.AppendLine("After restyle, three panels anchored to the marker and facing the viewer:");
            r.AppendLine($"  Task list    {TaskListSize.x / 1000f:F2} x {TaskListSize.y / 1000f:F2} m   (left)");
            r.AppendLine($"  Instruction  {InstructionSize.x / 1000f:F2} x {InstructionSize.y / 1000f:F2} m   (centre)");
            r.AppendLine($"  Status       {StatusSize.x / 1000f:F2} x {StatusSize.y / 1000f:F2} m   (right, researcher detail hidden inside it)");
            float span = (TaskListSize.x + InstructionSize.x + StatusSize.x) / 1000f + PanelGap * 2f;
            r.AppendLine($"  Total span   {span:F2} m, sitting above and beyond the marker.");
            r.AppendLine();
            r.AppendLine("Sprites: " + (AssetDatabase.LoadAssetAtPath<Sprite>(UiSpriteFactory.PanelSpritePath) != null
                ? "already generated" : "will be generated"));

            Debug.Log(r.ToString());
        }

        // =====================================================================
        // 2. APPLY
        // =====================================================================

        [MenuItem("AdaptiveAR/UI/2 - Apply UI Restyle", false, 11)]
        public static void Apply()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[UiRestyle] Active scene is '{scene.name}', expected '{ExpectedSceneName}'. Aborted.");
                return;
            }

            Sprite panelSprite = UiSpriteFactory.EnsureSprites();
            Sprite borderSprite = UiSpriteFactory.BorderSprite();

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("UI: Apply Spatial Restyle");

            var session = FindComponent<AssemblySessionController>(scene);
            var runner = FindComponent<StepRunner>(scene);
            var level = FindComponent<SupportLevelController>(scene);
            var validator = FindComponent<StepValidator>(scene);
            var logger = FindComponent<SessionLogger>(scene);
            var presenter = FindComponent<StepPresenter>(scene);

            int built = 0;
            built += BuildInstructionPanel(scene, panelSprite, borderSprite, presenter, session, runner);
            built += BuildTaskList(scene, panelSprite, borderSprite, session, runner);
            built += BuildResearcherHud(scene, panelSprite, borderSprite, session, runner, level, validator, logger);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[UiRestyle] Spatial interface applied.\n" +
                $"  Elements styled/created : {built}\n" +
                "  DemoUICanvas   -> instruction panel, left, 0.43 x 0.38 m\n" +
                "  OverviewCanvas -> task list, right, 0.30 x 0.33 m\n" +
                "  StatusCanvas   -> researcher HUD, above, HIDDEN by default\n" +
                "  Toggle the researcher HUD on device with the left Start/Menu button, or F1 in the Editor.\n" +
                "  ISDK interaction components were left untouched.\n" +
                "  Layout is an estimate - tune the constants at the top of UiRestyleSetup.cs and re-run.\n" +
                "  SAVE THE SCENE (Ctrl+S).");
        }

        // =====================================================================
        // Instruction panel  (DemoUICanvas)
        // =====================================================================

        private static int BuildInstructionPanel(Scene scene, Sprite panelSprite, Sprite borderSprite,
                                                 StepPresenter presenter, AssemblySessionController session,
                                                 StepRunner runner)
        {
            GameObject canvas = Find(scene, "DemoUICanvas");
            if (canvas == null) { Debug.LogWarning("[UiRestyle] DemoUICanvas missing."); return 0; }

            PlaceInRig(scene, canvas, InstructionSize, 0f, 0f);

            // Hide EVERY legacy child except the ones this tool owns. Naming them one by
            // one is what left the old InstructionPanel bar and the TaskA/B/C rows on
            // screen in the first snapshot.
            HideLegacyChildren(canvas, "Panel", "HomePanel", "CompletePanel",
                               "Surface", "ISDK_RayCanvasInteraction");

            GameObject panel = EnsurePanel(canvas, "Panel", panelSprite, borderSprite, InstructionSize);

            float inner = InstructionSize.x - MrTheme.PanelPadding * 2f;
            float y = -MrTheme.PanelPadding;

            // --- eyebrow ---
            TextMeshProUGUI eyebrow = EnsureText(panel, "Eyebrow", "V8 ASSEMBLY",
                MrTheme.SizeEyebrow, MrTheme.Accent, FontStyles.Bold | FontStyles.UpperCase);
            eyebrow.characterSpacing = MrTheme.EyebrowCharacterSpacing;
            PlaceRow(eyebrow.rectTransform, inner, 24f, ref y);
            y -= 10f;

            // --- segmented progress ---
            GameObject bar = EnsureChild(panel, "ProgressBar");
            var barRect = Rect(bar);
            PlaceRow(barRect, inner, MrTheme.ProgressSegmentHeight, ref y);

            var progress = bar.GetComponent<StepProgressBar>();
            if (progress == null) progress = Undo.AddComponent<StepProgressBar>(bar);
            progress.SetSegmentSprite(panelSprite);
            SetRefs(progress, ("session", session), ("stepRunner", runner));
            y -= 14f;

            // --- step label ---
            TextMeshProUGUI stepLabel = EnsureText(panel, "StepLabel", "STEP 01 / 06",
                MrTheme.SizeEyebrow, MrTheme.TextMuted, FontStyles.Bold);
            stepLabel.characterSpacing = 4f;
            PlaceRow(stepLabel.rectTransform, inner, 24f, ref y);
            y -= MrTheme.RowSpacing;

            // --- title: reuse the existing CaptionText so its references survive ---
            TextMeshProUGUI title = FindTmp(canvas, "InstructionPanel/CaptionText")
                                    ?? EnsureText(panel, "TitleText", "", MrTheme.SizeTitle, MrTheme.TextPrimary, FontStyles.Bold);

            ReparentKeepingName(title.gameObject, panel.transform);
            StyleText(title, MrTheme.SizeTitle, MrTheme.TextPrimary, FontStyles.Bold);
            title.textWrappingMode = TextWrappingModes.Normal;
            AutoSize(title, 22f, MrTheme.SizeTitle);
            PlaceRow(title.rectTransform, inner, 108f, ref y);
            y -= MrTheme.RowSpacing;

            // --- body: progressive detail ---
            TextMeshProUGUI body = EnsureText(panel, "BodyText", "",
                MrTheme.SizeBody, MrTheme.TextSecondary, FontStyles.Normal);
            body.textWrappingMode = TextWrappingModes.Normal;
            body.lineSpacing = 10f;
            AutoSize(body, 15f, MrTheme.SizeBody);
            PlaceRow(body.rectTransform, inner, 190f, ref y);

            // --- alignment chip, bottom left ---
            GameObject chip = EnsureChip(panel, "AlignmentChip", panelSprite);
            var chipRect = Rect(chip);
            chipRect.anchorMin = chipRect.anchorMax = new Vector2(0f, 0f);
            chipRect.pivot = new Vector2(0f, 0f);
            chipRect.sizeDelta = new Vector2(230f, 44f);
            chipRect.anchoredPosition = new Vector2(MrTheme.PanelPadding, MrTheme.PanelPadding);

            var chipHud = chip.GetComponent<AlignmentChip>();
            if (chipHud == null) chipHud = Undo.AddComponent<AlignmentChip>(chip);
            SetRefs(chipHud,
                ("validator", FindComponent<StepValidator>(scene)),
                ("stepRunner", runner),
                ("background", chip.GetComponent<Image>()),
                ("label", chip.GetComponentInChildren<TextMeshProUGUI>(true)));

            // --- Next button, bottom right ---
            // Look inside Panel first: a previous run already reparented it there, and
            // searching only the canvas root would miss it and silently skip restyling.
            GameObject next = Find(scene, "DemoUICanvas/Panel/NextButton")
                              ?? Find(scene, "DemoUICanvas/NextButton");
            if (next != null)
            {
                ReparentKeepingName(next, panel.transform);
                StyleButton(next, panelSprite, borderSprite, "Next Step", primary: true);

                var nr = Rect(next);
                nr.anchorMin = nr.anchorMax = new Vector2(1f, 0f);
                nr.pivot = new Vector2(1f, 0f);
                nr.sizeDelta = new Vector2(160f, 52f);
                nr.anchoredPosition = new Vector2(-MrTheme.PanelPadding, MrTheme.PanelPadding);
            }

            // The step label must not claim "STEP 01 / 06" before a step exists.
            stepLabel.text = "";
            title.text = "";
            body.text = "";

            // --- hand the presenter its new fields ---
            if (presenter != null)
            {
                SetRefs(presenter,
                    ("titleText", title),
                    ("bodyText", body),
                    ("stepLabelText", stepLabel),
                    ("stepRunnerForLabel", runner));
            }

            if (session != null)
                SetRefs(session, ("captionText", title));

            // --- home and completion screens, and the flow that switches between them ---
            BuildHomeAndComplete(scene, canvas, panel, panelSprite, borderSprite, session, runner, stepLabel);

            return 6;
        }

        // =====================================================================
        // Home / Complete screens
        //
        // The first snapshot had no way in at all: the Begin control lived inside
        // ButtonsPanel, which the restyle hid. A dedicated home screen with its own
        // Start button fixes that and gives the session a proper beginning and end.
        // =====================================================================

        private static void BuildHomeAndComplete(Scene scene, GameObject canvas, GameObject stepPanel,
                                                 Sprite panelSprite, Sprite borderSprite,
                                                 AssemblySessionController session, StepRunner runner,
                                                 TextMeshProUGUI stepLabel)
        {
            // ---------- HOME ----------
            GameObject home = EnsurePanel(canvas, "HomePanel", panelSprite, borderSprite, InstructionSize);
            float inner = InstructionSize.x - MrTheme.PanelPadding * 2f;
            float y = -MrTheme.PanelPadding - 22f;

            TextMeshProUGUI hEyebrow = EnsureText(home, "Eyebrow", "MIXED REALITY GUIDANCE",
                MrTheme.SizeEyebrow, MrTheme.Accent, FontStyles.Bold | FontStyles.UpperCase);
            hEyebrow.characterSpacing = MrTheme.EyebrowCharacterSpacing;
            PlaceRow(hEyebrow.rectTransform, inner, 24f, ref y);
            y -= 8f;

            TextMeshProUGUI hTitle = EnsureText(home, "Title", "V8 Engine Assembly",
                MrTheme.SizeTitle, MrTheme.TextPrimary, FontStyles.Bold);
            hTitle.textWrappingMode = TextWrappingModes.Normal;
            AutoSize(hTitle, 22f, MrTheme.SizeTitle);
            PlaceRow(hTitle.rectTransform, inner, 100f, ref y);
            y -= 6f;

            TextMeshProUGUI hBody = EnsureText(home, "Body",
                "Place the printed marker flat on the bench where you can reach it.\n\n" +
                "Press Start, then look at the marker to anchor the engine.",
                MrTheme.SizeBody, MrTheme.TextSecondary, FontStyles.Normal);
            hBody.textWrappingMode = TextWrappingModes.Normal;
            hBody.lineSpacing = 10f;
            AutoSize(hBody, 15f, MrTheme.SizeBody);
            PlaceRow(hBody.rectTransform, inner, 170f, ref y);

            GameObject startBtn = EnsureButton(home, "StartButton", panelSprite, "Start", primary: true);
            var sr = Rect(startBtn);
            sr.anchorMin = sr.anchorMax = new Vector2(1f, 0f);
            sr.pivot = new Vector2(1f, 0f);
            sr.sizeDelta = new Vector2(170f, 56f);
            sr.anchoredPosition = new Vector2(-MrTheme.PanelPadding, MrTheme.PanelPadding);

            // ---------- COMPLETE ----------
            GameObject complete = EnsurePanel(canvas, "CompletePanel", panelSprite, borderSprite, InstructionSize);
            float cy = -MrTheme.PanelPadding - 22f;

            TextMeshProUGUI cEyebrow = EnsureText(complete, "Eyebrow", "SESSION",
                MrTheme.SizeEyebrow, MrTheme.Success, FontStyles.Bold | FontStyles.UpperCase);
            cEyebrow.characterSpacing = MrTheme.EyebrowCharacterSpacing;
            PlaceRow(cEyebrow.rectTransform, inner, 24f, ref cy);
            cy -= 8f;

            TextMeshProUGUI cTitle = EnsureText(complete, "Title", "Assembly complete",
                MrTheme.SizeTitle, MrTheme.TextPrimary, FontStyles.Bold);
            cTitle.textWrappingMode = TextWrappingModes.Normal;
            AutoSize(cTitle, 22f, MrTheme.SizeTitle);
            PlaceRow(cTitle.rectTransform, inner, 100f, ref cy);
            cy -= 6f;

            TextMeshProUGUI cBody = EnsureText(complete, "Body", "",
                MrTheme.SizeBody, MrTheme.TextSecondary, FontStyles.Normal);
            cBody.textWrappingMode = TextWrappingModes.Normal;
            cBody.lineSpacing = 10f;
            AutoSize(cBody, 15f, MrTheme.SizeBody);
            PlaceRow(cBody.rectTransform, inner, 180f, ref cy);

            // ---------- flow ----------
            var flow = canvas.GetComponent<AppFlowController>();
            if (flow == null) flow = Undo.AddComponent<AppFlowController>(canvas);

            GameObject taskCanvas = Find(scene, "OverviewCanvas");

            SetRefs(flow,
                ("homePanel", home),
                ("stepPanel", stepPanel),
                ("completePanel", complete),
                ("taskListRoot", taskCanvas),
                ("session", session),
                ("stepRunner", runner),
                ("completeHeadline", cTitle),
                ("completeBody", cBody),
                ("stepLabel", stepLabel));

            // Start must drive BOTH: StepManager begins marker detection, the flow swaps panels.
            var startButton = startBtn.GetComponent<Button>();
            StepManager stepManager = FindComponent<StepManager>(scene);

            if (startButton != null)
            {
                Undo.RecordObject(startButton, "Wire start button");
                for (int i = startButton.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
                    UnityEditor.Events.UnityEventTools.RemovePersistentListener(startButton.onClick, i);

                UnityEditor.Events.UnityEventTools.AddVoidPersistentListener(
                    startButton.onClick, flow.BeginSession);

                EditorUtility.SetDirty(startButton);
            }

            // StepManager adds its own listener to whatever is in beginButton, so pointing it
            // at the new Start button is all that is needed to keep marker detection working.
            if (stepManager != null && startButton != null)
                SetRefs(stepManager, ("beginButton", startButton));

            // Correct starting visibility; AppFlowController re-asserts this at runtime.
            SetActiveRecorded(home, true);
            SetActiveRecorded(stepPanel, false);
            SetActiveRecorded(complete, false);
        }

        private static void SetActiveRecorded(GameObject go, bool active)
        {
            if (go == null || go.activeSelf == active) return;
            Undo.RecordObject(go, "Set panel visibility");
            go.SetActive(active);
        }

        private static GameObject EnsureButton(GameObject parent, string name, Sprite fill,
                                               string label, bool primary)
        {
            GameObject go = EnsureChild(parent, name);

            if (go.GetComponent<Button>() == null) Undo.AddComponent<Button>(go);

            // A button needs a Graphic to be clickable at all.
            if (go.GetComponent<Image>() == null) Undo.AddComponent<Image>(go);

            if (go.transform.Find("Label") == null)
                EnsureText(go, "Label", label, MrTheme.SizeButton, MrTheme.Accent, FontStyles.Bold);

            StyleButton(go, fill, null, label, primary);
            return go;
        }

        // =====================================================================
        // Task list  (OverviewCanvas)
        // =====================================================================

        private static int BuildTaskList(Scene scene, Sprite panelSprite, Sprite borderSprite,
                                         AssemblySessionController session, StepRunner runner)
        {
            GameObject canvas = Find(scene, "OverviewCanvas");
            if (canvas == null) { Debug.LogWarning("[UiRestyle] OverviewCanvas missing."); return 0; }

            float leftX = -(InstructionSize.x * 0.0005f + PanelGap + TaskListSize.x * 0.0005f);
            PlaceInRig(scene, canvas, TaskListSize, leftX, SidePanelYaw);

            HideLegacyChildren(canvas, "Panel", "Surface", "ISDK_RayCanvasInteraction");

            GameObject panel = EnsurePanel(canvas, "Panel", panelSprite, borderSprite, TaskListSize);

            float inner = TaskListSize.x - MrTheme.PanelPadding * 2f;
            float y = -MrTheme.PanelPadding;

            TextMeshProUGUI heading = EnsureText(panel, "Heading", "STEPS",
                MrTheme.SizeEyebrow, MrTheme.Accent, FontStyles.Bold | FontStyles.UpperCase);
            heading.characterSpacing = MrTheme.EyebrowCharacterSpacing;
            PlaceRow(heading.rectTransform, inner, 24f, ref y);
            y -= MrTheme.SectionSpacing;

            // One row per step, so the list matches the authored sequence length.
            int rowCount = runner != null && runner.StepCount > 0 ? runner.StepCount : 6;
            var rows = new List<TextMeshProUGUI>();

            for (int i = 0; i < rowCount; i++)
            {
                TextMeshProUGUI row = EnsureText(panel, $"Row{i + 1}", "",
                    MrTheme.SizeList, MrTheme.TextSecondary, FontStyles.Normal);
                AutoSize(row, 14f, MrTheme.SizeList);
                PlaceRow(row.rectTransform, inner, 34f, ref y);
                y -= 6f;
                rows.Add(row);
            }

            var hud = canvas.GetComponent<TaskListHud>();
            if (hud == null) hud = Undo.AddComponent<TaskListHud>(canvas);

            var so = new SerializedObject(hud);
            Set(so, "session", session);
            Set(so, "stepRunner", runner);
            Set(so, "headingText", null);   // the heading is static now
            SerializedProperty list = so.FindProperty("rowTexts");
            if (list != null)
            {
                list.ClearArray();
                for (int i = 0; i < rows.Count; i++)
                {
                    list.InsertArrayElementAtIndex(i);
                    list.GetArrayElementAtIndex(i).objectReferenceValue = rows[i];
                }
            }
            SerializedProperty done = so.FindProperty("donePrefix");
            if (done != null) done.stringValue = "✓  ";
            SerializedProperty cur = so.FindProperty("currentPrefix");
            if (cur != null) cur.stringValue = "▸  ";
            SerializedProperty pend = so.FindProperty("pendingPrefix");
            if (pend != null) pend.stringValue = "·  ";
            so.ApplyModifiedProperties();

            return 1 + rows.Count;
        }

        // =====================================================================
        // Researcher HUD  (StatusCanvas)
        // =====================================================================

        private static int BuildResearcherHud(Scene scene, Sprite panelSprite, Sprite borderSprite,
                                              AssemblySessionController session, StepRunner runner,
                                              SupportLevelController level, StepValidator validator,
                                              SessionLogger logger)
        {
            GameObject canvas = Find(scene, "StatusCanvas");
            if (canvas == null) { Debug.LogWarning("[UiRestyle] StatusCanvas missing."); return 0; }

            float rightX = InstructionSize.x * 0.0005f + PanelGap + StatusSize.x * 0.0005f;
            PlaceInRig(scene, canvas, StatusSize, rightX, -SidePanelYaw);

            HideLegacyChildren(canvas, "Panel", "ResearcherPanel", "Surface", "ISDK_RayCanvasInteraction");

            // ---------- participant-facing status, always visible ----------
            GameObject panel = EnsurePanel(canvas, "Panel", panelSprite, borderSprite, StatusSize);

            float inner = StatusSize.x - MrTheme.PanelPadding * 2f;
            float y = -MrTheme.PanelPadding;

            TextMeshProUGUI heading = EnsureText(panel, "Heading", "STATUS",
                MrTheme.SizeEyebrow, MrTheme.Accent, FontStyles.Bold | FontStyles.UpperCase);
            heading.characterSpacing = MrTheme.EyebrowCharacterSpacing;
            PlaceRow(heading.rectTransform, inner, 24f, ref y);
            y -= MrTheme.SectionSpacing;

            TextMeshProUGUI supportLabel = Metric(panel, "SupportLabel", "SUPPORT LEVEL", inner, ref y);
            TextMeshProUGUI supportValue = MetricValue(panel, "SupportValue", "-", inner, ref y);
            y -= MrTheme.RowSpacing;

            TextMeshProUGUI progressLabel = Metric(panel, "ProgressLabel", "PROGRESS", inner, ref y);
            TextMeshProUGUI progressValue = MetricValue(panel, "ProgressValue", "-", inner, ref y);
            y -= MrTheme.RowSpacing;

            TextMeshProUGUI timeLabel = Metric(panel, "TimeLabel", "TIME ON STEP", inner, ref y);
            TextMeshProUGUI timeValue = MetricValue(panel, "TimeValue", "--:--", inner, ref y);
            y -= MrTheme.RowSpacing;

            TextMeshProUGUI errorLabel = Metric(panel, "ErrorLabel", "PLACEMENT RETRIES", inner, ref y);
            TextMeshProUGUI errorValue = MetricValue(panel, "ErrorValue", "0", inner, ref y);

            var statusHud = canvas.GetComponent<StatusHud>();
            if (statusHud == null) statusHud = Undo.AddComponent<StatusHud>(canvas);

            SetRefs(statusHud,
                ("session", session), ("stepRunner", runner),
                ("supportLevel", level), ("validator", validator),
                ("supportLevelText", supportValue),
                ("progressValueText", progressValue),
                ("timeValueText", timeValue),
                ("errorValueText", errorValue),
                ("placementErrorText", null),
                ("progressSlider", null), ("progressFill", null));

            // ---------- researcher detail, hidden inside the same panel ----------
            GameObject research = EnsurePanel(canvas, "ResearcherPanel", panelSprite, borderSprite, StatusSize);

            float ry = -MrTheme.PanelPadding;
            TextMeshProUGUI rHeading = EnsureText(research, "Heading", "RESEARCHER VIEW",
                MrTheme.SizeEyebrow, MrTheme.Warning, FontStyles.Bold | FontStyles.UpperCase);
            rHeading.characterSpacing = MrTheme.EyebrowCharacterSpacing;
            PlaceRow(rHeading.rectTransform, inner, 24f, ref ry);
            ry -= MrTheme.RowSpacing;

            TextMeshProUGUI readout = EnsureText(research, "Readout", "",
                16f, MrTheme.TextSecondary, FontStyles.Normal);
            readout.textWrappingMode = TextWrappingModes.Normal;
            readout.lineSpacing = 14f;
            AutoSize(readout, 10f, 16f);
            PlaceRow(readout.rectTransform, inner, StatusSize.y - 90f, ref ry);

            var hud = canvas.GetComponent<ResearcherHud>();
            if (hud == null) hud = Undo.AddComponent<ResearcherHud>(canvas);

            SetRefs(hud,
                ("session", session), ("stepRunner", runner), ("supportLevel", level),
                ("validator", validator), ("logger", logger),
                ("panelRoot", research), ("readoutText", readout));

            // Participant sees STATUS; the researcher overlay stays off until toggled.
            SetActiveRecorded(panel, true);
            SetActiveRecorded(research, false);

            return 4;
        }

        /// <summary>Small uppercase metric caption.</summary>
        private static TextMeshProUGUI Metric(GameObject panel, string name, string text, float inner, ref float y)
        {
            TextMeshProUGUI t = EnsureText(panel, name, text, 16f, MrTheme.TextMuted,
                                           FontStyles.Bold | FontStyles.UpperCase);
            t.characterSpacing = 5f;
            PlaceRow(t.rectTransform, inner, 20f, ref y);
            return t;
        }

        /// <summary>The value beneath a metric caption.</summary>
        private static TextMeshProUGUI MetricValue(GameObject panel, string name, string text, float inner, ref float y)
        {
            TextMeshProUGUI t = EnsureText(panel, name, text, 30f, MrTheme.TextPrimary, FontStyles.Bold);
            AutoSize(t, 16f, 30f);
            PlaceRow(t.rectTransform, inner, 38f, ref y);
            return t;
        }

        // =====================================================================
        // Building blocks
        // =====================================================================

        /// <summary>
        /// Parents a canvas to the marker-anchored PanelRig and positions it as one panel
        /// of the triptych. Anchoring to the marker is what keeps the panels in a
        /// comfortable place no matter where the marker is put down or how the operator
        /// shifts in their seat.
        /// </summary>
        private static void PlaceInRig(Scene scene, GameObject canvas, Vector2 size, float localX, float yaw)
        {
            GameObject rig = EnsurePanelRig(scene);

            var rt = canvas.GetComponent<RectTransform>();
            Undo.RecordObject(rt, "Place canvas");

            if (rig != null && canvas.transform.parent != rig.transform)
                Undo.SetTransformParent(canvas.transform, rig.transform, "Parent canvas to PanelRig");

            rt.localPosition = new Vector3(localX, 0f, 0f);
            rt.localRotation = Quaternion.Euler(0f, yaw, 0f);
            rt.localScale = new Vector3(0.001f, 0.001f, 0.001f);
            rt.sizeDelta = size;

            // The ISDK ray surface must track the canvas or pointing lands in the wrong place.
            Transform surface = canvas.transform.Find("Surface");
            if (surface is RectTransform sr)
            {
                Undo.RecordObject(sr, "Resize ISDK surface");
                sr.sizeDelta = size;
            }
        }

        private static GameObject EnsurePanel(GameObject canvas, string name, Sprite fill, Sprite border, Vector2 size)
        {
            GameObject panel = EnsureChild(canvas, name);
            var rt = Rect(panel);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            panel.transform.SetAsFirstSibling();

            var img = panel.GetComponent<Image>();
            if (img == null) img = Undo.AddComponent<Image>(panel);
            Undo.RecordObject(img, "Style panel");
            img.sprite = fill;
            img.type = Image.Type.Sliced;
            img.color = MrTheme.PanelFill;
            img.raycastTarget = true;

            // Hairline border as a separate non-raycasting overlay.
            GameObject borderGo = EnsureChild(panel, "Border");
            var br = Rect(borderGo);
            br.anchorMin = Vector2.zero; br.anchorMax = Vector2.one;
            br.offsetMin = Vector2.zero; br.offsetMax = Vector2.zero;
            var bimg = borderGo.GetComponent<Image>();
            if (bimg == null) bimg = Undo.AddComponent<Image>(borderGo);
            bimg.sprite = border;
            bimg.type = Image.Type.Sliced;
            bimg.color = MrTheme.PanelBorder;
            bimg.raycastTarget = false;
            // First sibling, not last: it draws above the panel fill but behind the content.
            // As last sibling it would re-order on every re-run and end up covering the
            // buttons.
            borderGo.transform.SetAsFirstSibling();

            return panel;
        }

        private static GameObject EnsureChip(GameObject parent, string name, Sprite fill)
        {
            GameObject chip = EnsureChild(parent, name);
            var img = chip.GetComponent<Image>();
            if (img == null) img = Undo.AddComponent<Image>(chip);
            img.sprite = fill;
            img.type = Image.Type.Sliced;
            img.color = MrTheme.WarningSoft;
            img.raycastTarget = false;

            TextMeshProUGUI label = EnsureText(chip, "Label", "", 20f, MrTheme.Warning, FontStyles.Bold);
            var lr = label.rectTransform;
            lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
            lr.offsetMin = new Vector2(14f, 0f); lr.offsetMax = new Vector2(-14f, 0f);
            label.alignment = TextAlignmentOptions.Left;

            return chip;
        }

        private static void StyleButton(GameObject go, Sprite fill, Sprite border, string text, bool primary)
        {
            var img = go.GetComponent<Image>();
            if (img == null) img = Undo.AddComponent<Image>(go);
            Undo.RecordObject(img, "Style button");
            img.sprite = fill;
            img.type = Image.Type.Sliced;
            img.color = primary ? MrTheme.AccentSoft : MrTheme.PanelFillRaised;

            var btn = go.GetComponent<Button>();
            if (btn != null)
            {
                Undo.RecordObject(btn, "Style button colors");
                var colors = btn.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(1f, 1f, 1f, 0.85f);
                colors.pressedColor = new Color(0.8f, 1f, 1f, 1f);
                colors.fadeDuration = 0.08f;
                btn.colors = colors;
            }

            var label = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
            {
                Undo.RecordObject(label, "Style button label");
                label.text = text;
                StyleText(label, MrTheme.SizeButton, primary ? MrTheme.Accent : MrTheme.TextPrimary, FontStyles.Bold);
                label.alignment = TextAlignmentOptions.Center;
                var lr = label.rectTransform;
                lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
                lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
            }
        }

        private static TextMeshProUGUI EnsureText(GameObject parent, string name, string content,
                                                  float size, Color color, FontStyles style)
        {
            GameObject go = EnsureChild(parent, name);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp == null) tmp = Undo.AddComponent<TextMeshProUGUI>(go);

            Undo.RecordObject(tmp, "Style text");
            if (!string.IsNullOrEmpty(content)) tmp.text = content;
            StyleText(tmp, size, color, style);
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>
        /// Lets TMP shrink text that would not otherwise fit its box. This is what stops
        /// long instructions spilling past the panel edge, which no fixed font size can
        /// guarantee across six steps of differing length.
        /// </summary>
        private static void AutoSize(TextMeshProUGUI tmp, float min, float max)
        {
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = min;
            tmp.fontSizeMax = max;
            tmp.overflowMode = TextOverflowModes.Truncate;
        }

        /// <summary>
        /// Creates (or finds) the marker-anchored rig the panels hang from. Parented to
        /// MarkerAnchor so the whole interface travels with the marker; the rig itself
        /// turns to face the viewer.
        /// </summary>
        private static GameObject EnsurePanelRig(Scene scene)
        {
            GameObject rig = Find(scene, "PanelRig");
            GameObject markerAnchor = Find(scene, "MarkerAnchor");

            if (rig == null)
            {
                rig = new GameObject("PanelRig");
                Undo.RegisterCreatedObjectUndo(rig, "Create PanelRig");
            }

            if (markerAnchor != null && rig.transform.parent != markerAnchor.transform)
                Undo.SetTransformParent(rig.transform, markerAnchor.transform, "Parent PanelRig");

            var comp = rig.GetComponent<PanelRig>();
            if (comp == null) comp = Undo.AddComponent<PanelRig>(rig);

            if (markerAnchor != null)
                SetRefs(comp, ("markerAnchor", markerAnchor.transform));
            else
                Debug.LogWarning("[UiRestyle] MarkerAnchor not found. Run the Alignment tool first, " +
                                 "otherwise the panels have nothing to anchor to.");

            return rig;
        }

        private static void StyleText(TextMeshProUGUI tmp, float size, Color color, FontStyles style)
        {
            tmp.fontSize = size;
            tmp.color = color;
            tmp.fontStyle = style;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.overflowMode = TextOverflowModes.Truncate;
            tmp.margin = Vector4.zero;
        }

        /// <summary>Stacks a row from the top of the panel, advancing the running Y cursor.</summary>
        private static void PlaceRow(RectTransform rt, float width, float height, ref float y)
        {
            Undo.RecordObject(rt, "Lay out row");
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(MrTheme.PanelPadding, y);
            y -= height;
        }

        private static GameObject EnsureChild(GameObject parent, string name)
        {
            Transform t = parent.transform.Find(name);
            if (t != null) return t.gameObject;

            var go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Create UI element");
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static void ReparentKeepingName(GameObject go, Transform parent)
        {
            if (go.transform.parent == parent) return;
            Undo.SetTransformParent(go.transform, parent, "Reparent UI element");
        }

        /// <summary>
        /// Deactivates every direct child of a canvas whose name is not in the keep list.
        /// Robust against legacy objects this tool has never heard of, which a hard-coded
        /// hide list is not.
        /// </summary>
        private static void HideLegacyChildren(GameObject canvas, params string[] keep)
        {
            var keepSet = new HashSet<string>(keep);

            for (int i = 0; i < canvas.transform.childCount; i++)
            {
                GameObject child = canvas.transform.GetChild(i).gameObject;
                if (keepSet.Contains(child.name) || !child.activeSelf) continue;

                Undo.RecordObject(child, "Hide legacy panel");
                child.SetActive(false);
            }
        }

        private static void HideChild(GameObject parent, string name)
        {
            Transform t = parent.transform.Find(name);
            if (t == null || !t.gameObject.activeSelf) return;

            Undo.RecordObject(t.gameObject, "Hide legacy panel");
            t.gameObject.SetActive(false);
        }

        private static RectTransform Rect(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = Undo.AddComponent<RectTransform>(go);
            return rt;
        }

        // =====================================================================
        // Lookup helpers
        // =====================================================================

        private static void SetRefs(Component target, params (string field, Object value)[] pairs)
        {
            var so = new SerializedObject(target);
            foreach (var p in pairs) Set(so, p.field, p.value);
            so.ApplyModifiedProperties();
        }

        private static void Set(SerializedObject so, string field, Object value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null)
            {
                Debug.LogWarning($"[UiRestyle] Field '{field}' not found on {so.targetObject.GetType().Name}.");
                return;
            }
            p.objectReferenceValue = value;
        }

        private static TextMeshProUGUI FindTmp(GameObject root, string relativePath)
        {
            Transform t = root.transform.Find(relativePath);
            return t != null ? t.GetComponent<TextMeshProUGUI>() : null;
        }

        private static T FindComponent<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T c = root.GetComponentInChildren<T>(true);
                if (c != null) return c;
            }
            return null;
        }

        private static GameObject Find(Scene scene, string path)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            Transform current = null;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == parts[0]) { current = root.transform; break; }
                Transform deep = FindDeep(root.transform, parts[0]);
                if (deep != null) { current = deep; break; }
            }

            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null) return null;
            }
            return current.gameObject;
        }

        private static Transform FindDeep(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c.name == name) return c;
                Transform d = FindDeep(c, name);
                if (d != null) return d;
            }
            return null;
        }
    }
}
