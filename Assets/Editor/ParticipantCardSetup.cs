// File: ParticipantCardSetup.cs
// Builds the ONE participant instruction card, with a fixed deterministic layout.
//
// Why this replaces the previous three-panel build
// ------------------------------------------------
// The old layout stacked rows with a running Y cursor and let TextMeshPro auto-size
// over an unbounded range. Two failures followed from that, both seen on device:
//
//   * content-driven geometry - a long instruction pushed rows past the panel, so
//     text escaped the card and collided with the controls beneath it;
//   * a background sized independently of its content, which left an uncovered dark
//     region when the two disagreed.
//
// Every row here has an EXPLICIT RectTransform with a fixed height, anchored to the
// card, and every text field has a conservative auto-size range with a hard maximum
// region. Content can never move a row, so it can never escape the card. If content
// is too long for its box it shrinks to the floor and then truncates - it does not
// spill.
//
// The background is anchored to the full card bounds, so it always covers exactly
// the card and cannot leave a gap.
//
// Controls are sized for ray interaction at roughly a metre: 170 x 56 mm with the
// label inset on all sides and its font capped well below the button height.

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
    public static class ParticipantCardSetup
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";

        // --- card geometry, canvas units = mm at the 0.001 canvas scale ---
        private const float CardWidth = 460f;    // 0.46 m
        private const float CardHeight = 280f;   // 0.28 m
        private const float Pad = 22f;

        // --- fixed row heights. Nothing here is content-driven. ---
        private const float HeaderH = 22f;
        private const float ProgressBarH = 8f;
        private const float ProgressLabelH = 18f;
        private const float DividerH = 1f;
        private const float TitleH = 42f;
        private const float BodyH = 62f;
        private const float FeedbackH = 26f;
        private const float ButtonH = 56f;
        private const float ButtonW = 170f;
        private const float BackW = 120f;

        // --- bounded type. Auto-size is allowed only inside these ranges. ---
        private const float HeaderMin = 12f, HeaderMax = 16f;
        private const float ProgressMin = 11f, ProgressMax = 15f;
        private const float TitleMin = 22f, TitleMax = 30f;
        private const float BodyMin = 14f, BodyMax = 19f;
        private const float FeedbackMin = 13f, FeedbackMax = 17f;
        private const float ButtonMin = 16f, ButtonMax = 20f;   // well under ButtonH

        [MenuItem("AdaptiveAR/UI/3 - Build Participant Card (single card)", false, 12)]
        public static void Build()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[Card] Wrong scene '{scene.name}'. Aborted.");
                return;
            }

            GameObject canvas = Find(scene, "DemoUICanvas");
            if (canvas == null)
            {
                Debug.LogError("[Card] DemoUICanvas not found. Aborted.");
                return;
            }

            Sprite panelSprite = UiSpriteFactory.EnsureSprites();
            Sprite borderSprite = UiSpriteFactory.BorderSprite();

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("UI: Build Participant Card");

            var session = FindComponent<AssemblySessionController>(scene);
            var workflow = FindComponent<WorkflowState>(scene);
            var runner = FindComponent<StepRunner>(scene);
            var level = FindComponent<SupportLevelController>(scene);
            var validator = FindComponent<StepValidator>(scene);
            var logger = FindComponent<SessionLogger>(scene);

            // --- the card canvas itself ---
            var canvasRect = canvas.GetComponent<RectTransform>();
            Undo.RecordObject(canvasRect, "Size card canvas");
            canvasRect.sizeDelta = new Vector2(CardWidth, CardHeight);

            Transform surface = canvas.transform.Find("Surface");
            if (surface is RectTransform sr)
            {
                Undo.RecordObject(sr, "Size ISDK surface");
                sr.sizeDelta = new Vector2(CardWidth, CardHeight);
            }

            HideAllExcept(canvas, "Panel", "HomePanel", "CompletePanel", "Surface", "ISDK_RayCanvasInteraction");

            // --- the single participant card ---
            GameObject card = BuildCard(canvas, panelSprite, borderSprite, out var parts);

            // --- one owner for all participant text ---
            var participant = canvas.GetComponent<ParticipantCard>();
            if (participant == null) participant = Undo.AddComponent<ParticipantCard>(canvas);

            SetRefs(participant,
                ("session", session), ("workflow", workflow), ("stepRunner", runner),
                ("supportLevel", level), ("validator", validator),
                ("stageText", parts.progressLabel),
                ("counterText", null),
                ("instructionText", parts.title),
                ("detailText", parts.body),
                ("feedbackText", parts.feedback),
                ("nextButton", parts.nextButton),
                ("nextLabel", parts.nextLabel));

            // --- progress bar reads completion, not arrival ---
            var bar = parts.progressBar.GetComponent<StepProgressBar>();
            if (bar == null) bar = Undo.AddComponent<StepProgressBar>(parts.progressBar);
            bar.SetSegmentSprite(panelSprite);
            SetRefs(bar, ("session", session), ("stepRunner", runner), ("workflow", workflow));

            // --- the other participant canvases are gone: one card only ---
            int hidden = HideCanvas(scene, "OverviewCanvas") + HideCanvas(scene, "StatusCanvas");

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[Card] Single participant card built.\n" +
                $"  Card            : {CardWidth / 1000f:F2} x {CardHeight / 1000f:F2} m, fixed layout\n" +
                $"  Rows            : header, progress, label, divider, title, body, feedback, controls\n" +
                $"  Participant canvases hidden : {hidden} (Steps and Status are researcher concerns now)\n" +
                "  Every row has an explicit RectTransform and a bounded auto-size range, so\n" +
                "  content cannot move a row or escape the card.\n" +
                "  Background is anchored to the full card bounds - no uncovered region.\n" +
                "  ParticipantCard is the ONLY runtime writer of these fields.\n" +
                "  SAVE THE SCENE.");
        }

        private struct CardParts
        {
            public TextMeshProUGUI header, progressLabel, title, body, feedback, nextLabel, backLabel;
            public GameObject progressBar;
            public Button nextButton, backButton;
        }

        private static GameObject BuildCard(GameObject canvas, Sprite fill, Sprite border, out CardParts p)
        {
            p = new CardParts();

            GameObject card = EnsureChild(canvas, "Panel");
            var rt = Rect(card);
            Undo.RecordObject(rt, "Card bounds");

            // Anchored to the WHOLE canvas: the background can never disagree with the card.
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            card.transform.SetAsFirstSibling();

            var bg = card.GetComponent<Image>();
            if (bg == null) bg = Undo.AddComponent<Image>(card);
            Undo.RecordObject(bg, "Card background");
            bg.sprite = fill;
            bg.type = Image.Type.Sliced;
            bg.color = MrTheme.PanelFill;
            bg.raycastTarget = true;

            GameObject borderGo = EnsureChild(card, "Border");
            var br = Rect(borderGo);
            br.anchorMin = Vector2.zero; br.anchorMax = Vector2.one;
            br.offsetMin = Vector2.zero; br.offsetMax = Vector2.zero;
            var bi = borderGo.GetComponent<Image>();
            if (bi == null) bi = Undo.AddComponent<Image>(borderGo);
            bi.sprite = border; bi.type = Image.Type.Sliced;
            bi.color = MrTheme.PanelBorder; bi.raycastTarget = false;
            borderGo.transform.SetAsFirstSibling();

            float inner = CardWidth - Pad * 2f;
            float y = -Pad;

            // 1. header
            p.header = Text(card, "Header", "V8 ASSEMBLY", HeaderMin, HeaderMax,
                            MrTheme.Accent, FontStyles.Bold | FontStyles.UpperCase);
            p.header.characterSpacing = 8f;
            Row(p.header.rectTransform, inner, HeaderH, ref y);
            y -= 8f;

            // 2. progress bar
            p.progressBar = EnsureChild(card, "ProgressBar");
            Row(Rect(p.progressBar), inner, ProgressBarH, ref y);
            y -= 6f;

            // 3. progress label
            p.progressLabel = Text(card, "ProgressLabel", "", ProgressMin, ProgressMax,
                                   MrTheme.TextMuted, FontStyles.Bold);
            p.progressLabel.characterSpacing = 3f;
            Row(p.progressLabel.rectTransform, inner, ProgressLabelH, ref y);
            y -= 10f;

            // 4. divider
            GameObject div = EnsureChild(card, "Divider");
            var di = div.GetComponent<Image>();
            if (di == null) di = Undo.AddComponent<Image>(div);
            di.color = MrTheme.WithAlpha(MrTheme.PanelBorder, 0.5f);
            di.raycastTarget = false;
            Row(Rect(div), inner, DividerH, ref y);
            y -= 12f;

            // 5. action title
            p.title = Text(card, "TitleText", "", TitleMin, TitleMax,
                           MrTheme.TextPrimary, FontStyles.Bold);
            p.title.textWrappingMode = TextWrappingModes.Normal;
            Row(p.title.rectTransform, inner, TitleH, ref y);
            y -= 6f;

            // 6. body - one instruction, at most three rendered lines
            p.body = Text(card, "BodyText", "", BodyMin, BodyMax,
                          MrTheme.TextSecondary, FontStyles.Normal);
            p.body.textWrappingMode = TextWrappingModes.Normal;
            p.body.lineSpacing = 8f;
            Row(p.body.rectTransform, inner, BodyH, ref y);
            y -= 4f;

            // 7. feedback - normally empty
            p.feedback = Text(card, "FeedbackText", "", FeedbackMin, FeedbackMax,
                              MrTheme.TextSecondary, FontStyles.Bold);
            p.feedback.textWrappingMode = TextWrappingModes.Normal;
            Row(p.feedback.rectTransform, inner, FeedbackH, ref y);

            // 8. controls, anchored to the bottom so they never move with content
            GameObject next = Button(card, "NextButton", fill, "Continue", true, ButtonW);
            var nr = Rect(next);
            nr.anchorMin = nr.anchorMax = new Vector2(1f, 0f);
            nr.pivot = new Vector2(1f, 0f);
            nr.anchoredPosition = new Vector2(-Pad, Pad);
            p.nextButton = next.GetComponent<Button>();
            p.nextLabel = next.GetComponentInChildren<TextMeshProUGUI>(true);

            GameObject back = Button(card, "BackButton", fill, "Back", false, BackW);
            var bkr = Rect(back);
            bkr.anchorMin = bkr.anchorMax = new Vector2(0f, 0f);
            bkr.pivot = new Vector2(0f, 0f);
            bkr.anchoredPosition = new Vector2(Pad, Pad);
            p.backButton = back.GetComponent<Button>();
            p.backLabel = back.GetComponentInChildren<TextMeshProUGUI>(true);

            return card;
        }

        // =====================================================================
        // Building blocks - every one produces a fixed, bounded element
        // =====================================================================

        /// <summary>A row with an explicit height. Content never changes its geometry.</summary>
        private static void Row(RectTransform rt, float width, float height, ref float y)
        {
            Undo.RecordObject(rt, "Row");
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(Pad, y);
            y -= height;
        }

        private static TextMeshProUGUI Text(GameObject parent, string name, string content,
                                            float min, float max, Color color, FontStyles style)
        {
            GameObject go = EnsureChild(parent, name);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (t == null) t = Undo.AddComponent<TextMeshProUGUI>(go);

            Undo.RecordObject(t, "Text");
            t.text = content;
            t.color = color;
            t.fontStyle = style;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.raycastTarget = false;
            t.margin = Vector4.zero;

            // Bounded auto-size: it may shrink to `min` and no further, then truncate.
            // Unbounded shrink/grow is what let content escape its box before.
            t.enableAutoSizing = true;
            t.fontSizeMin = min;
            t.fontSizeMax = max;
            t.fontSize = max;
            t.overflowMode = TextOverflowModes.Truncate;

            return t;
        }

        /// <summary>
        /// A control sized for ray interaction at arm's length, with the label inset on all
        /// sides and its font capped well below the button height so it cannot overflow.
        /// </summary>
        private static GameObject Button(GameObject parent, string name, Sprite fill,
                                         string label, bool primary, float width)
        {
            GameObject go = EnsureChild(parent, name);

            var rt = Rect(go);
            rt.sizeDelta = new Vector2(width, ButtonH);

            var img = go.GetComponent<Image>();
            if (img == null) img = Undo.AddComponent<Image>(go);
            Undo.RecordObject(img, "Button background");
            img.sprite = fill;
            img.type = Image.Type.Sliced;
            img.color = primary ? MrTheme.AccentSoft : MrTheme.PanelFillRaised;
            img.raycastTarget = true;          // the hit area IS the visible area

            var btn = go.GetComponent<Button>();
            if (btn == null) btn = Undo.AddComponent<Button>(go);
            btn.targetGraphic = img;

            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.8f);
            colors.pressedColor = new Color(0.75f, 1f, 1f, 1f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.35f);
            colors.fadeDuration = 0.06f;
            btn.colors = colors;

            TextMeshProUGUI t = Text(go, "Label", label, ButtonMin, ButtonMax,
                                     primary ? MrTheme.Accent : MrTheme.TextSecondary, FontStyles.Bold);
            t.alignment = TextAlignmentOptions.Center;

            // Inset on all sides: the label cannot reach the button edge.
            var lr = t.rectTransform;
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = new Vector2(12f, 8f);
            lr.offsetMax = new Vector2(-12f, -8f);

            return go;
        }

        // =====================================================================

        private static void HideAllExcept(GameObject canvas, params string[] keep)
        {
            var set = new System.Collections.Generic.HashSet<string>(keep);
            for (int i = 0; i < canvas.transform.childCount; i++)
            {
                GameObject c = canvas.transform.GetChild(i).gameObject;
                if (set.Contains(c.name) || !c.activeSelf) continue;
                Undo.RecordObject(c, "Hide legacy");
                c.SetActive(false);
            }
        }

        private static int HideCanvas(Scene scene, string name)
        {
            GameObject go = Find(scene, name);
            if (go == null || !go.activeSelf) return 0;

            Undo.RecordObject(go, "Hide participant canvas");
            go.SetActive(false);
            return 1;
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

        private static RectTransform Rect(GameObject go)
        {
            var rt = go.GetComponent<RectTransform>();
            return rt != null ? rt : Undo.AddComponent<RectTransform>(go);
        }

        private static void SetRefs(Component target, params (string field, Object value)[] pairs)
        {
            var so = new SerializedObject(target);
            foreach (var p in pairs)
            {
                SerializedProperty prop = so.FindProperty(p.field);
                if (prop == null)
                {
                    Debug.LogWarning($"[Card] Field '{p.field}' not found on {target.GetType().Name}.");
                    continue;
                }
                prop.objectReferenceValue = p.value;
            }
            so.ApplyModifiedProperties();
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
