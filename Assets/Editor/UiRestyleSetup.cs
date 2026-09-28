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
//   DemoUICanvas    -> participant instruction panel (left)
//   OverviewCanvas  -> task list (right)
//   StatusCanvas    -> researcher / debug HUD, hidden by default
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

        // --- spatial layout, metres. Canvas scale is 0.001, so 1 canvas unit = 1 mm ---
        private static readonly Vector3 InstructionPos = new Vector3(-0.34f, 1.18f, 0.92f);
        private static readonly Vector3 InstructionEuler = new Vector3(0f, -20f, 0f);
        private static readonly Vector2 InstructionSize = new Vector2(430f, 380f);

        private static readonly Vector3 TaskListPos = new Vector3(0.36f, 1.18f, 0.92f);
        private static readonly Vector3 TaskListEuler = new Vector3(0f, 20f, 0f);
        private static readonly Vector2 TaskListSize = new Vector2(300f, 330f);

        private static readonly Vector3 DebugPos = new Vector3(0f, 1.58f, 1.05f);
        private static readonly Vector3 DebugEuler = Vector3.zero;
        private static readonly Vector2 DebugSize = new Vector2(560f, 300f);

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
            r.AppendLine("Current span is about 1.8 m at 1 m - roughly 84 deg of view, centred on the work area.");
            r.AppendLine("After restyle:");
            r.AppendLine($"  Instruction panel  {InstructionSize.x / 1000f:F2} x {InstructionSize.y / 1000f:F2} m at {InstructionPos}");
            r.AppendLine($"  Task list          {TaskListSize.x / 1000f:F2} x {TaskListSize.y / 1000f:F2} m at {TaskListPos}");
            r.AppendLine($"  Researcher HUD     {DebugSize.x / 1000f:F2} x {DebugSize.y / 1000f:F2} m at {DebugPos}  (hidden by default)");
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

            PlaceCanvas(canvas, InstructionPos, InstructionEuler, InstructionSize);

            // Hide the old prototype chrome without deleting it, so nothing else that
            // references those objects breaks.
            HideChild(canvas, "BackgroundPanel");
            HideChild(canvas, "ButtonsPanel");
            HideChild(canvas, "IntermissionPanel");
            HideChild(canvas, "BackButton");

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
            PlaceRow(title.rectTransform, inner, 110f, ref y);
            y -= MrTheme.RowSpacing;

            // --- body: progressive detail ---
            TextMeshProUGUI body = EnsureText(panel, "BodyText", "",
                MrTheme.SizeBody, MrTheme.TextSecondary, FontStyles.Normal);
            body.textWrappingMode = TextWrappingModes.Normal;
            body.lineSpacing = 12f;
            PlaceRow(body.rectTransform, inner, 130f, ref y);

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
            GameObject next = Find(scene, "DemoUICanvas/NextButton");
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

            return 6;
        }

        // =====================================================================
        // Task list  (OverviewCanvas)
        // =====================================================================

        private static int BuildTaskList(Scene scene, Sprite panelSprite, Sprite borderSprite,
                                         AssemblySessionController session, StepRunner runner)
        {
            GameObject canvas = Find(scene, "OverviewCanvas");
            if (canvas == null) { Debug.LogWarning("[UiRestyle] OverviewCanvas missing."); return 0; }

            PlaceCanvas(canvas, TaskListPos, TaskListEuler, TaskListSize);

            HideChild(canvas, "BackgroundPanel");
            HideChild(canvas, "BottomPanel");
            HideChild(canvas, "IntermissionPanel");
            HideChild(canvas, "CalibrationPanel");
            HideChild(canvas, "TopPanel");

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

            PlaceCanvas(canvas, DebugPos, DebugEuler, DebugSize);

            // The old participant-facing metrics move into this HUD, so their original
            // rows are hidden rather than deleted.
            foreach (string n in new[] { "BackgroundPanel", "TopPanel", "BottomPanel", "TimerText",
                                         "ErrorText", "ProgressText", "ProgressBar",
                                         "IntermissionPanel", "CalibrationPanel" })
                HideChild(canvas, n);

            GameObject panel = EnsurePanel(canvas, "Panel", panelSprite, borderSprite, DebugSize);

            float inner = DebugSize.x - MrTheme.PanelPadding * 2f;
            float y = -MrTheme.PanelPadding;

            TextMeshProUGUI heading = EnsureText(panel, "Heading", "RESEARCHER VIEW",
                MrTheme.SizeEyebrow, MrTheme.Warning, FontStyles.Bold | FontStyles.UpperCase);
            heading.characterSpacing = MrTheme.EyebrowCharacterSpacing;
            PlaceRow(heading.rectTransform, inner, 24f, ref y);
            y -= MrTheme.RowSpacing;

            TextMeshProUGUI readout = EnsureText(panel, "Readout", "",
                MrTheme.SizeMetricLabel + 4f, MrTheme.TextSecondary, FontStyles.Normal);
            readout.textWrappingMode = TextWrappingModes.NoWrap;
            readout.lineSpacing = 18f;
            PlaceRow(readout.rectTransform, inner, DebugSize.y - 90f, ref y);

            var hud = canvas.GetComponent<ResearcherHud>();
            if (hud == null) hud = Undo.AddComponent<ResearcherHud>(canvas);

            SetRefs(hud,
                ("session", session), ("stepRunner", runner), ("supportLevel", level),
                ("validator", validator), ("logger", logger),
                ("panelRoot", panel), ("readoutText", readout));

            // Hidden from the participant until explicitly toggled.
            if (panel.activeSelf)
            {
                Undo.RecordObject(panel, "Hide researcher HUD");
                panel.SetActive(false);
            }

            // The old StatusHud drove participant-facing metrics that no longer exist here.
            var oldHud = canvas.GetComponent<StatusHud>();
            if (oldHud != null)
                Undo.DestroyObjectImmediate(oldHud);

            return 2;
        }

        // =====================================================================
        // Building blocks
        // =====================================================================

        private static void PlaceCanvas(GameObject canvas, Vector3 pos, Vector3 euler, Vector2 size)
        {
            var rt = canvas.GetComponent<RectTransform>();
            Undo.RecordObject(rt, "Place canvas");

            rt.position = pos;
            rt.rotation = Quaternion.Euler(euler);
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
            borderGo.transform.SetAsLastSibling();

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
