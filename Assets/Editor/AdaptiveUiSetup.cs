// File: AdaptiveUiSetup.cs
// THE current UI build path. One idempotent command builds all four zones and the
// controller that changes their presence with the support level.
//
// It supersedes 'UI > 3 - Build Single Card', which hid the context panels to leave
// one card. The correction is that support level should change how much PERIPHERAL
// information is on screen, not collapse the interface to a single card at every
// level:
//
//   L1  instruction + steps + performance visible, research subdued
//   L2  instruction full, steps and performance reduced in salience
//   L3  instruction full, steps and performance collapse away, research hidden
//
// Higher support therefore means richer guidance for the immediate task and LESS
// competing information around it.
//
// The centre stays empty at every level. Panels flank the work so the engine, the
// target ghost, the arrow and the local validation cue own the middle of the view.
//
// Running this twice must not duplicate anything: every element is found-or-created
// by name and every reference is reassigned rather than appended.

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
    public static class AdaptiveUiSetup
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";

        // --- zone placement in PanelRig local space, metres. Centre is left clear. ---
        private static readonly Vector3 InstructionPos = new Vector3(-0.30f, 0.00f, 0f);
        private static readonly Vector3 StepsPos = new Vector3(0.34f, 0.05f, 0f);
        private static readonly Vector3 PerformancePos = new Vector3(0.34f, -0.20f, 0f);
        private static readonly Vector3 ResearchPos = new Vector3(0.66f, -0.14f, 0.06f);

        private const float SideYaw = 18f;

        private static readonly Vector2 StepsSize = new Vector2(300f, 240f);
        private static readonly Vector2 PerfSize = new Vector2(300f, 130f);
        private static readonly Vector2 ResearchSize = new Vector2(280f, 300f);

        [MenuItem("AdaptiveAR/UI/4 - Build Adaptive UI (overwrites panel layout)", false, 13)]
        public static void Build()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[AdaptiveUI] Wrong scene '{scene.name}'. Aborted.");
                return;
            }

            // The panel positions, widths and text sizes in the scene were tuned by hand on
            // the Quest after this tool last ran. Re-running it re-places and re-sizes them.
            if (!EditorUtility.DisplayDialog(
                    "Overwrite the tuned panel layout?",
                    "This rebuilds the four UI zones and REPOSITIONS and RESIZES the panels, " +
                    "replacing the layout that was tuned by hand on the headset.\n\n" +
                    "Only run this on a scene whose UI you intend to regenerate.",
                    "Cancel (recommended)", "Rebuild anyway"))
            {
                Debug.Log("[AdaptiveUI] Cancelled; the scene's panel layout is unchanged.");
                return;
            }

            Sprite fill = UiSpriteFactory.EnsureSprites();
            Sprite border = UiSpriteFactory.BorderSprite();

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("UI: Build Adaptive Multi-Panel");

            var session = FindComponent<AssemblySessionController>(scene);
            var workflow = FindComponent<WorkflowState>(scene);
            var runner = FindComponent<StepRunner>(scene);
            var level = FindComponent<SupportLevelController>(scene);
            var validator = FindComponent<StepValidator>(scene);
            var logger = FindComponent<SessionLogger>(scene);

            // --- zone 1: the instruction card, unchanged at every support level ---
            ParticipantCardSetup.BuildInstructionCard();

            GameObject rig = Find(scene, "PanelRig");
            GameObject instruction = Find(scene, "DemoUICanvas");

            if (rig == null)
            {
                Debug.LogError("[AdaptiveUI] PanelRig not found. Run 'UI > 2 - Apply UI Restyle' " +
                               "once first so the marker-anchored rig exists. Aborted.");
                return;
            }

            Place(instruction, rig, InstructionPos, 0f, null);

            // --- zone 2: steps / what is next ---
            GameObject steps = Find(scene, "OverviewCanvas");
            BuildSteps(scene, steps, rig, fill, border, session, runner, workflow);

            // --- zone 3: performance ---
            GameObject perf = Find(scene, "StatusCanvas");
            BuildPerformance(scene, perf, rig, fill, border, session, runner, workflow, validator);

            // --- zone 4: research / system ---
            GameObject research = BuildResearch(scene, rig, fill, border, session, runner, level, validator, logger);

            // --- the controller that changes their presence ---
            var adaptive = rig.GetComponent<AdaptivePanelController>();
            if (adaptive == null) adaptive = Undo.AddComponent<AdaptivePanelController>(rig);

            SetRefs(adaptive, ("supportLevel", level));

            // Instruction stays Full throughout. Everything else recedes as support rises.
            adaptive.ConfigureZone("Instruction", instruction,
                AdaptivePanelController.Presence.Full,
                AdaptivePanelController.Presence.Full,
                AdaptivePanelController.Presence.Full);

            adaptive.ConfigureZone("Steps", steps,
                AdaptivePanelController.Presence.Full,
                AdaptivePanelController.Presence.Reduced,
                AdaptivePanelController.Presence.Hidden);

            adaptive.ConfigureZone("Performance", perf,
                AdaptivePanelController.Presence.Full,
                AdaptivePanelController.Presence.Reduced,
                AdaptivePanelController.Presence.Hidden);

            adaptive.ConfigureZone("Research", research,
                AdaptivePanelController.Presence.Faded,
                AdaptivePanelController.Presence.Faded,
                AdaptivePanelController.Presence.Hidden);

            EditorUtility.SetDirty(adaptive);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[AdaptiveUI] Adaptive multi-panel UI built.\n" +
                "  Zone 1 Instruction  left    Full / Full / Full\n" +
                "  Zone 2 Steps        right   Full / Reduced / Hidden\n" +
                "  Zone 3 Performance  right   Full / Reduced / Hidden\n" +
                "  Zone 4 Research     far     Faded / Faded / Hidden\n" +
                "  Centre of view is left clear for the engine, ghost, arrow and validation cue.\n" +
                "  Transitions are alpha fades; a faded panel stops blocking rays.\n" +
                "  Support changes touch visibility only - no workflow state is reset.\n" +
                "  Idempotent: re-running reassigns, it does not duplicate.\n" +
                "  SAVE THE SCENE.");
        }

        // =====================================================================
        // Zone 2 - steps / what is next
        // =====================================================================

        private static void BuildSteps(Scene scene, GameObject canvas, GameObject rig, Sprite fill, Sprite border,
                                       AssemblySessionController session, StepRunner runner, WorkflowState workflow)
        {
            if (canvas == null) { Debug.LogWarning("[AdaptiveUI] OverviewCanvas missing."); return; }

            Place(canvas, rig, StepsPos, SideYaw, StepsSize);
            HideAllExcept(canvas, "Panel", "Surface", "ISDK_RayCanvasInteraction");

            GameObject panel = Panel(canvas, fill, border);
            float inner = StepsSize.x - 20f;
            float y = -20f;

            TextMeshProUGUI head = Label(panel, "Heading", "WHAT'S NEXT", 12f, 15f,
                                         MrTheme.Accent, FontStyles.Bold | FontStyles.UpperCase);
            head.characterSpacing = 7f;
            Row(head.rectTransform, inner, 20f, ref y);
            y -= 10f;

            int rows = runner != null && runner.StepCount > 0 ? runner.StepCount : 6;
            var texts = new List<TextMeshProUGUI>();

            for (int i = 0; i < rows; i++)
            {
                TextMeshProUGUI t = Label(panel, $"Row{i + 1}", "", 11f, 15f,
                                          MrTheme.TextSecondary, FontStyles.Normal);
                Row(t.rectTransform, inner, 26f, ref y);
                y -= 3f;
                texts.Add(t);
            }

            var hud = canvas.GetComponent<TaskListHud>();
            if (hud == null) hud = Undo.AddComponent<TaskListHud>(canvas);

            var so = new SerializedObject(hud);
            Set(so, "session", session);
            Set(so, "stepRunner", runner);
            Set(so, "headingText", null);

            SerializedProperty list = so.FindProperty("rowTexts");
            if (list != null)
            {
                list.ClearArray();          // reassign, never append
                for (int i = 0; i < texts.Count; i++)
                {
                    list.InsertArrayElementAtIndex(i);
                    list.GetArrayElementAtIndex(i).objectReferenceValue = texts[i];
                }
            }

            SetString(so, "donePrefix", "✓  ");
            SetString(so, "currentPrefix", "▸  ");
            SetString(so, "pendingPrefix", "·  ");
            so.ApplyModifiedProperties();
        }

        // =====================================================================
        // Zone 3 - performance. Only metrics the system already has.
        // =====================================================================

        private static void BuildPerformance(Scene scene, GameObject canvas, GameObject rig, Sprite fill, Sprite border,
                                             AssemblySessionController session, StepRunner runner,
                                             WorkflowState workflow, StepValidator validator)
        {
            if (canvas == null) { Debug.LogWarning("[AdaptiveUI] StatusCanvas missing."); return; }

            Place(canvas, rig, PerformancePos, SideYaw, PerfSize);
            HideAllExcept(canvas, "Panel", "ResearcherPanel", "Surface", "ISDK_RayCanvasInteraction");

            GameObject panel = Panel(canvas, fill, border);
            float inner = PerfSize.x - 20f;
            float y = -18f;

            TextMeshProUGUI head = Label(panel, "Heading", "PROGRESS", 12f, 15f,
                                         MrTheme.Accent, FontStyles.Bold | FontStyles.UpperCase);
            head.characterSpacing = 7f;
            Row(head.rectTransform, inner, 20f, ref y);
            y -= 10f;

            TextMeshProUGUI progressValue = Label(panel, "ProgressValue", "0 / 6", 16f, 24f,
                                                  MrTheme.TextPrimary, FontStyles.Bold);
            Row(progressValue.rectTransform, inner, 28f, ref y);
            y -= 8f;

            TextMeshProUGUI timeLabel = Label(panel, "TimeLabel", "TIME ON STEP", 10f, 12f,
                                              MrTheme.TextMuted, FontStyles.Bold | FontStyles.UpperCase);
            timeLabel.characterSpacing = 4f;
            Row(timeLabel.rectTransform, inner, 14f, ref y);

            TextMeshProUGUI timeValue = Label(panel, "TimeValue", "00:00", 14f, 20f,
                                              MrTheme.TextSecondary, FontStyles.Bold);
            Row(timeValue.rectTransform, inner, 24f, ref y);

            var hud = canvas.GetComponent<StatusHud>();
            if (hud == null) hud = Undo.AddComponent<StatusHud>(canvas);

            SetRefs(hud,
                ("session", session), ("stepRunner", runner), ("workflow", workflow),
                ("validator", validator),
                ("supportLevelText", null),          // support level is NOT participant-facing
                ("progressValueText", progressValue),
                ("timeValueText", timeValue),
                ("errorValueText", null),
                ("placementErrorText", null),
                ("progressSlider", null), ("progressFill", null));
        }

        // =====================================================================
        // Zone 4 - research / system. Its own canvas, low salience, never competing.
        // =====================================================================

        private static GameObject BuildResearch(Scene scene, GameObject rig, Sprite fill, Sprite border,
                                                AssemblySessionController session, StepRunner runner,
                                                SupportLevelController level, StepValidator validator,
                                                SessionLogger logger)
        {
            Transform existing = rig.transform.Find("ResearchCanvas");
            GameObject canvas = existing != null ? existing.gameObject : null;

            if (canvas == null)
            {
                canvas = new GameObject("ResearchCanvas", typeof(RectTransform), typeof(Canvas));
                Undo.RegisterCreatedObjectUndo(canvas, "Create research canvas");
                canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            }

            Place(canvas, rig, ResearchPos, -26f, ResearchSize);

            GameObject panel = Panel(canvas, fill, border);
            var img = panel.GetComponent<Image>();
            if (img != null) img.color = MrTheme.WithAlpha(MrTheme.PanelFill, 0.55f);

            float inner = ResearchSize.x - 20f;
            float y = -18f;

            TextMeshProUGUI head = Label(panel, "Heading", "RESEARCH", 10f, 13f,
                                         MrTheme.WithAlpha(MrTheme.Warning, 0.75f),
                                         FontStyles.Bold | FontStyles.UpperCase);
            head.characterSpacing = 6f;
            Row(head.rectTransform, inner, 18f, ref y);
            y -= 6f;

            TextMeshProUGUI readout = Label(panel, "Readout", "", 8f, 12f,
                                            MrTheme.WithAlpha(MrTheme.TextSecondary, 0.8f), FontStyles.Normal);
            readout.textWrappingMode = TextWrappingModes.Normal;
            readout.lineSpacing = 10f;
            Row(readout.rectTransform, inner, ResearchSize.y - 50f, ref y);

            var hud = canvas.GetComponent<ResearcherHud>();
            if (hud == null) hud = Undo.AddComponent<ResearcherHud>(canvas);

            SetRefs(hud,
                ("session", session), ("stepRunner", runner), ("supportLevel", level),
                ("validator", validator), ("logger", logger),
                ("panelRoot", panel), ("readoutText", readout));

            return canvas;
        }

        // =====================================================================
        // Shared building blocks - bounded, fixed, idempotent
        // =====================================================================

        private static void Place(GameObject canvas, GameObject rig, Vector3 localPos, float yaw, Vector2? size)
        {
            if (canvas == null || rig == null) return;

            var rt = canvas.GetComponent<RectTransform>();
            Undo.RecordObject(rt, "Place zone");

            if (canvas.transform.parent != rig.transform)
                Undo.SetTransformParent(canvas.transform, rig.transform, "Parent zone to rig");

            rt.localPosition = localPos;
            rt.localRotation = Quaternion.Euler(0f, yaw, 0f);
            rt.localScale = new Vector3(0.001f, 0.001f, 0.001f);

            if (size.HasValue)
            {
                rt.sizeDelta = size.Value;

                // The ISDK ray surface must track the canvas or pointing lands off-target.
                Transform surface = canvas.transform.Find("Surface");
                if (surface is RectTransform sr)
                {
                    Undo.RecordObject(sr, "Size ISDK surface");
                    sr.sizeDelta = size.Value;
                }
            }
        }

        private static GameObject Panel(GameObject canvas, Sprite fill, Sprite border)
        {
            GameObject panel = Ensure(canvas, "Panel");
            var rt = panel.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            panel.transform.SetAsFirstSibling();

            var img = panel.GetComponent<Image>();
            if (img == null) img = Undo.AddComponent<Image>(panel);
            img.sprite = fill; img.type = Image.Type.Sliced;
            img.color = MrTheme.PanelFill;
            img.raycastTarget = true;

            GameObject b = Ensure(panel, "Border");
            var brt = b.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            var bi = b.GetComponent<Image>();
            if (bi == null) bi = Undo.AddComponent<Image>(b);
            bi.sprite = border; bi.type = Image.Type.Sliced;
            bi.color = MrTheme.PanelBorder; bi.raycastTarget = false;
            b.transform.SetAsFirstSibling();

            return panel;
        }

        private static TextMeshProUGUI Label(GameObject parent, string name, string content,
                                             float min, float max, Color color, FontStyles style)
        {
            GameObject go = Ensure(parent, name);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (t == null) t = Undo.AddComponent<TextMeshProUGUI>(go);

            Undo.RecordObject(t, "Label");
            t.text = content;
            t.color = color;
            t.fontStyle = style;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.raycastTarget = false;
            t.margin = Vector4.zero;

            // Bounded auto-size with truncate: content can never render outside its box.
            t.enableAutoSizing = true;
            t.fontSizeMin = min;
            t.fontSizeMax = max;
            t.fontSize = max;
            t.overflowMode = TextOverflowModes.Truncate;

            return t;
        }

        private static void Row(RectTransform rt, float width, float height, ref float y)
        {
            Undo.RecordObject(rt, "Row");
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(10f, y);
            y -= height;
        }

        private static GameObject Ensure(GameObject parent, string name)
        {
            Transform t = parent.transform.Find(name);
            if (t != null) return t.gameObject;

            var go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, "Create UI element");
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static void HideAllExcept(GameObject canvas, params string[] keep)
        {
            var set = new HashSet<string>(keep);
            for (int i = 0; i < canvas.transform.childCount; i++)
            {
                GameObject c = canvas.transform.GetChild(i).gameObject;
                if (set.Contains(c.name) || !c.activeSelf) continue;
                Undo.RecordObject(c, "Hide legacy");
                c.SetActive(false);
            }
        }

        private static void SetRefs(Component target, params (string field, Object value)[] pairs)
        {
            var so = new SerializedObject(target);
            foreach (var p in pairs) Set(so, p.field, p.value);
            so.ApplyModifiedProperties();
        }

        private static void Set(SerializedObject so, string field, Object value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null) return;
            p.objectReferenceValue = value;
        }

        private static void SetString(SerializedObject so, string field, string value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null) p.stringValue = value;
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

        private static GameObject Find(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
                Transform deep = FindDeep(root.transform, name);
                if (deep != null) return deep.gameObject;
            }
            return null;
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
