// File: AssemblyContentSetup.cs
// Authors the full non-AI assembly workflow and wires it into the live scene.
//
// Same guarantees as the other tools: no direct scene YAML edits, dry run changes
// nothing, applies are idempotent and Undo-able.
//
// ABOUT THE L1/L2/L3 TEXT
// -----------------------
// The wording written here is a DRAFT scaffolding gradient, not settled research
// content. It varies only along two operational dimensions:
//     L1  what to do
//     L2  what to do + where it goes, with the target ghost shown
//     L3  what to do + where + an ordered breakdown, plus audio where a clip exists
// It encodes no psychological claim and no threshold. It lands in ScriptableObject
// assets, never in code, so it stays fully editable (CLAUDE.md 2.2).

using System.Collections.Generic;
using System.Text;
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
    public static class AssemblyContentSetup
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";
        private const string StepFolder = "Assets/ScriptableObjects/Steps";
        private const string LegacyPhase1Asset = StepFolder + "/Step_Crankshaft_Phase1.asset";
        private const string AudioCrankshaft = "Assets/Audio/InsertCrankshaft.mp3";

        private const string PartKeyPrefix = "part.";
        private const string GhostKeyPrefix = "ghost.";

        /// <summary>One authored assembly step. Draft content - review before running participants.</summary>
        private class StepSpec
        {
            public string assetName;
            public string stepId;
            public string shortLabel;       // task-list row label
            public string partObject;       // child of Offset/Components
            public string ghostKey;         // GuidanceRegistry key of the target pose
            public string shortGoal;        // L1
            public string whereItGoes;      // L2 adds this
            public string[] breakdown;      // L3 adds these ordered sub-actions
            public float expectedSeconds;   // session budget estimate, not a research construct
        }

        // Six steps: crankshaft, four pistons, camshaft. Sized for a 10-15 minute
        // headset session (~10 min of task time at the estimates below).
        private static readonly StepSpec[] Specs =
        {
            new StepSpec {
                assetName = "Step_01_Crankshaft", stepId = "step_01_crankshaft", shortLabel = "Crankshaft",
                partObject = "crankshaft", ghostKey = GhostKeyPrefix + "crankshaft",
                shortGoal = "Fit the crankshaft.",
                whereItGoes = "Fit the crankshaft into the main bearing saddles in the block.",
                breakdown = new[] {
                    "Take the crankshaft from the tray.",
                    "Line its journals up with the bearing saddles.",
                    "Lower it straight down until it sits flat."
                },
                expectedSeconds = 120f
            },
            new StepSpec {
                assetName = "Step_02_Piston001", stepId = "step_02_piston001", shortLabel = "Piston 1",
                partObject = "piston001", ghostKey = GhostKeyPrefix + "piston001",
                shortGoal = "Fit piston 1.",
                whereItGoes = "Fit piston 1 into the first cylinder bore.",
                breakdown = new[] {
                    "Take piston 1 from the tray.",
                    "Point the connecting rod down towards the crankshaft.",
                    "Slide the piston into the bore and seat the rod on its journal."
                },
                expectedSeconds = 90f
            },
            new StepSpec {
                assetName = "Step_03_Piston002", stepId = "step_03_piston002", shortLabel = "Piston 2",
                partObject = "piston002", ghostKey = GhostKeyPrefix + "piston002",
                shortGoal = "Fit piston 2.",
                whereItGoes = "Fit piston 2 into the second cylinder bore.",
                breakdown = new[] {
                    "Take piston 2 from the tray.",
                    "Point the connecting rod down towards the crankshaft.",
                    "Slide the piston into the bore and seat the rod on its journal."
                },
                expectedSeconds = 90f
            },
            new StepSpec {
                assetName = "Step_04_Piston003", stepId = "step_04_piston003", shortLabel = "Piston 3",
                partObject = "piston003", ghostKey = GhostKeyPrefix + "piston003",
                shortGoal = "Fit piston 3.",
                whereItGoes = "Fit piston 3 into the third cylinder bore.",
                breakdown = new[] {
                    "Take piston 3 from the tray.",
                    "Point the connecting rod down towards the crankshaft.",
                    "Slide the piston into the bore and seat the rod on its journal."
                },
                expectedSeconds = 90f
            },
            new StepSpec {
                assetName = "Step_05_Piston004", stepId = "step_05_piston004", shortLabel = "Piston 4",
                partObject = "piston004", ghostKey = GhostKeyPrefix + "piston004",
                shortGoal = "Fit piston 4.",
                whereItGoes = "Fit piston 4 into the fourth cylinder bore.",
                breakdown = new[] {
                    "Take piston 4 from the tray.",
                    "Point the connecting rod down towards the crankshaft.",
                    "Slide the piston into the bore and seat the rod on its journal."
                },
                expectedSeconds = 90f
            },
            new StepSpec {
                assetName = "Step_06_Camshaft", stepId = "step_06_camshaft", shortLabel = "Camshaft",
                partObject = "camshaft", ghostKey = GhostKeyPrefix + "camshaft",
                shortGoal = "Fit the camshaft.",
                whereItGoes = "Fit the camshaft into its bearings above the crankshaft.",
                breakdown = new[] {
                    "Take the camshaft from the tray.",
                    "Line its lobes up clear of the bearing housings.",
                    "Lay it into the bearings and check it turns freely."
                },
                expectedSeconds = 120f
            }
        };

        // =====================================================================
        // 1. VALIDATE (dry run)
        // =====================================================================

        [MenuItem("AdaptiveAR/Assembly/1 - Validate Content Setup (dry run)", false, 10)]
        public static void Validate()
        {
            var r = new StringBuilder();
            r.AppendLine("=== Assembly Content Validation (DRY RUN - nothing modified) ===");

            Scene scene = SceneManager.GetActiveScene();
            r.AppendLine($"Active scene: '{scene.name}'" +
                         (scene.name != ExpectedSceneName ? $"   WARNING expected '{ExpectedSceneName}'" : ""));

            GameObject components = FindByPath(scene, "EngineAnchor/Offset/Components");
            GuidanceRegistry registry = FindComponent<GuidanceRegistry>(scene);
            StepRunner runner = FindComponent<StepRunner>(scene);

            r.AppendLine();
            r.AppendLine($"Components node  : {(components == null ? "MISSING" : "found")}");
            r.AppendLine($"GuidanceRegistry : {(registry == null ? "MISSING" : $"found, {registry.RegisteredCount} key(s)")}");
            r.AppendLine($"StepRunner       : {(runner == null ? "MISSING" : $"found, {runner.StepCount} step(s)")}");

            r.AppendLine();
            r.AppendLine("Steps to author, and whether their scene objects resolve:");
            bool ok = components != null && registry != null && runner != null;

            foreach (StepSpec s in Specs)
            {
                Transform part = components != null ? components.transform.Find(s.partObject) : null;
                bool ghostOk = registry != null && registry.TryResolveQuiet(s.ghostKey, out _);

                r.AppendLine($"    {s.assetName,-22} part '{s.partObject}': {(part == null ? "MISSING" : "ok")}" +
                             $"   target '{s.ghostKey}': {(ghostOk ? "ok" : "MISSING")}");

                if (part == null || !ghostOk) ok = false;
            }

            r.AppendLine();
            r.AppendLine("Scene components that would be added:");
            GameObject stepManagerGo = FindByPath(scene, "StepManager");
            if (stepManagerGo != null)
            {
                r.AppendLine($"    SessionLogger              : {Has<SessionLogger>(stepManagerGo)}");
                r.AppendLine($"    StepValidator              : {Has<StepValidator>(stepManagerGo)}");
                r.AppendLine($"    AssemblySessionController  : {Has<AssemblySessionController>(stepManagerGo)}");
            }
            else
            {
                r.AppendLine("    StepManager GameObject MISSING");
                ok = false;
            }

            r.AppendLine($"    StatusHud on StatusCanvas    : {DescribeHud(scene, "StatusCanvas")}");
            r.AppendLine($"    TaskListHud on OverviewCanvas: {DescribeHud(scene, "OverviewCanvas")}");

            r.AppendLine();
            r.AppendLine("UI fields that would be bound:");
            foreach (string p in new[] {
                "StatusCanvas/TopPanel/CaptionText", "StatusCanvas/TimerText/TimeValue",
                "StatusCanvas/ErrorText/ErrorValue", "StatusCanvas/ProgressText/ProgressValue",
                "StatusCanvas/ProgressBar", "OverviewCanvas/TopPanel/CaptionText",
                "OverviewCanvas/TaskA/TaskaText", "OverviewCanvas/TaskB/TaskbText",
                "OverviewCanvas/TaskC/TaskcText" })
            {
                GameObject go = FindByPath(scene, p);
                r.AppendLine($"    {(go == null ? "MISSING" : "ok     ")} {p}");
            }

            StepData legacy = AssetDatabase.LoadAssetAtPath<StepData>(LegacyPhase1Asset);
            r.AppendLine();
            r.AppendLine(legacy == null
                ? "Legacy Phase 1 asset: not present."
                : $"Legacy Phase 1 asset present; its [Lx TEST] text would be cleared.");

            r.AppendLine();
            r.AppendLine(ok
                ? "RESULT: ready. Run '2 - Apply Full Assembly Content'."
                : "RESULT: something above is missing - fix it before applying.");

            Debug.Log(r.ToString());
        }

        private static string Has<T>(GameObject go) where T : Component
        {
            return go.GetComponent<T>() != null ? "present" : "would add";
        }

        private static string DescribeHud(Scene scene, string canvasName)
        {
            GameObject go = FindByPath(scene, canvasName);
            if (go == null) return "canvas MISSING";
            return go.GetComponent<StatusHud>() != null || go.GetComponent<TaskListHud>() != null
                ? "present" : "would add";
        }

        // =====================================================================
        // 2. APPLY
        // =====================================================================

        [MenuItem("AdaptiveAR/Assembly/2 - Apply Full Assembly Content", false, 11)]
        public static void ApplyAll()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[AssemblyContent] Active scene is '{scene.name}', expected '{ExpectedSceneName}'. Aborted.");
                return;
            }

            GameObject stepManagerGo = FindByPath(scene, "StepManager");
            GameObject components = FindByPath(scene, "EngineAnchor/Offset/Components");
            GuidanceRegistry registry = FindComponent<GuidanceRegistry>(scene);
            StepRunner runner = FindComponent<StepRunner>(scene);
            SupportLevelController supportLevel = FindComponent<SupportLevelController>(scene);
            StepPresenter presenter = FindComponent<StepPresenter>(scene);

            if (stepManagerGo == null || components == null || registry == null || runner == null)
            {
                Debug.LogError("[AssemblyContent] Required scene objects missing. Run validate first. Aborted.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Assembly: Apply Full Content");

            // --- 1. register the graspable parts (never auto-hidden) ---
            int partsRegistered = RegisterParts(registry, components.transform);

            // --- 2. author the step assets ---
            var assets = new List<StepData>();
            foreach (StepSpec spec in Specs)
                assets.Add(AuthorStep(spec));

            AssetDatabase.SaveAssets();

            // --- 3. load the sequence into the runner ---
            AssignSequence(runner, assets);

            // --- 4. session components ---
            SessionLogger logger = GetOrAdd<SessionLogger>(stepManagerGo);
            StepValidator validator = GetOrAdd<StepValidator>(stepManagerGo);
            AssemblySessionController session = GetOrAdd<AssemblySessionController>(stepManagerGo);

            SetRefs(validator, ("guidanceRegistry", registry));
            SetRefs(session,
                ("stepRunner", runner),
                ("supportLevel", supportLevel),
                ("validator", validator),
                ("logger", logger),
                ("captionText", Tmp(scene, "DemoUICanvas/InstructionPanel/CaptionText")));

            // --- 5. HUDs on the existing canvases ---
            int hudFields = WireStatusHud(scene, session, runner, supportLevel, validator);
            int taskRows = WireTaskListHud(scene, session, runner);

            // --- 6. confirm button doubles as manual step advance ---
            bool buttonWired = WireConfirmButton(scene, session);

            // --- 7. drop the temporary test labels ---
            int testCleared = ClearLegacyTestContent();

            // --- 8. remove duplicate DropIntoTray components ---
            int dupsRemoved = RemoveDuplicateDropIntoTray(scene);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[AssemblyContent] Full assembly content applied.\n" +
                $"  Step assets authored      : {assets.Count} (crankshaft, 4 pistons, camshaft)\n" +
                $"  Parts registered          : {partsRegistered} (part.* keys, excluded from auto-hide)\n" +
                $"  StepRunner sequence       : {runner.StepCount} step(s)\n" +
                $"  Session components        : SessionLogger, StepValidator, AssemblySessionController\n" +
                $"  StatusHud fields bound    : {hudFields}\n" +
                $"  Task list rows bound      : {taskRows}\n" +
                $"  Confirm button wired      : {buttonWired}\n" +
                $"  Legacy [Lx TEST] cleared  : {testCleared} level block(s)\n" +
                $"  Duplicate DropIntoTray    : {dupsRemoved} removed\n" +
                $"  Presenter found           : {(presenter != null ? "yes" : "NO - guidance will not render")}\n" +
                "  SAVE THE SCENE (Ctrl+S) to persist.");
        }

        // =====================================================================
        // Parts registry
        // =====================================================================

        private static int RegisterParts(GuidanceRegistry registry, Transform components)
        {
            var so = new SerializedObject(registry);
            SerializedProperty entries = so.FindProperty("entries");
            if (entries == null)
            {
                Debug.LogError("[AssemblyContent] GuidanceRegistry.entries not found.");
                return 0;
            }

            int added = 0;

            foreach (StepSpec spec in Specs)
            {
                string key = PartKeyPrefix + spec.partObject;
                Transform part = components.Find(spec.partObject);

                if (part == null)
                {
                    Debug.LogWarning($"[AssemblyContent] Part '{spec.partObject}' not found under Components.");
                    continue;
                }

                int existing = IndexOfKey(entries, key);
                if (existing < 0)
                {
                    entries.InsertArrayElementAtIndex(entries.arraySize);
                    existing = entries.arraySize - 1;
                    added++;
                }

                SerializedProperty e = entries.GetArrayElementAtIndex(existing);
                e.FindPropertyRelative("key").stringValue = key;
                e.FindPropertyRelative("target").objectReferenceValue = part.gameObject;

                // Critical: the parts must never be hidden by the presenter between steps.
                SerializedProperty exclude = e.FindPropertyRelative("excludeFromAutoHide");
                if (exclude != null) exclude.boolValue = true;
            }

            so.ApplyModifiedProperties();
            return added;
        }

        private static int IndexOfKey(SerializedProperty entries, string key)
        {
            for (int i = 0; i < entries.arraySize; i++)
            {
                if (entries.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue == key)
                    return i;
            }
            return -1;
        }

        // =====================================================================
        // Step authoring
        // =====================================================================

        private static StepData AuthorStep(StepSpec spec)
        {
            string path = $"{StepFolder}/{spec.assetName}.asset";
            StepData step = AssetDatabase.LoadAssetAtPath<StepData>(path);

            if (step == null)
            {
                step = ScriptableObject.CreateInstance<StepData>();
                AssetDatabase.CreateAsset(step, path);
            }

            Undo.RecordObject(step, "Author assembly step");

            step.stepId = spec.stepId;
            step.displayName = spec.shortLabel;
            step.stepTitle = spec.whereItGoes;
            step.stepDescription = "";
            step.taskComplexity = 0;              // researcher-defined; deliberately left unset
            step.expectedDurationSeconds = spec.expectedSeconds;

            // Opening level for each step. A neutral, usable starting point - the real
            // transition policy comes from the literature review.
            step.defaultSupportLevel = SupportLevel.L2_Guided;

            // --- validation ---
            step.requiresValidation = true;
            step.validationPartKey = PartKeyPrefix + spec.partObject;
            step.validationTargetKey = spec.ghostKey;
            step.positionToleranceMeters = 0.04f;
            step.rotationToleranceDegrees = 25f;
            step.snapOnSuccess = true;

            // --- L1: the goal only, no overlay ---
            step.l1Minimal = new StepSupportContent
            {
                instructionText = spec.shortGoal,
                ghostKeys = new string[0]
            };

            // --- L2: goal plus location, with the target ghost shown ---
            step.l2Guided = new StepSupportContent
            {
                instructionText = spec.whereItGoes,
                ghostKeys = new[] { spec.ghostKey }
            };

            // --- L3: ordered breakdown, target ghost, audio where a clip exists ---
            var sb = new StringBuilder();
            sb.Append(spec.whereItGoes);
            for (int i = 0; i < spec.breakdown.Length; i++)
                sb.Append('\n').Append(i + 1).Append(". ").Append(spec.breakdown[i]);

            step.l3Assisted = new StepSupportContent
            {
                instructionText = sb.ToString(),
                ghostKeys = new[] { spec.ghostKey },
                instructionAudio = spec.partObject == "crankshaft"
                    ? AssetDatabase.LoadAssetAtPath<AudioClip>(AudioCrankshaft)
                    : null
            };

            EditorUtility.SetDirty(step);
            return step;
        }

        private static void AssignSequence(StepRunner runner, List<StepData> assets)
        {
            var so = new SerializedObject(runner);
            SerializedProperty steps = so.FindProperty("steps");
            if (steps == null)
            {
                Debug.LogError("[AssemblyContent] StepRunner.steps not found.");
                return;
            }

            steps.ClearArray();
            for (int i = 0; i < assets.Count; i++)
            {
                steps.InsertArrayElementAtIndex(i);
                steps.GetArrayElementAtIndex(i).objectReferenceValue = assets[i];
            }
            so.ApplyModifiedProperties();
        }

        private static int ClearLegacyTestContent()
        {
            StepData legacy = AssetDatabase.LoadAssetAtPath<StepData>(LegacyPhase1Asset);
            if (legacy == null) return 0;

            Undo.RecordObject(legacy, "Clear test labels");

            int cleared = 0;
            foreach (StepSupportContent c in new[] { legacy.l1Minimal, legacy.l2Guided, legacy.l3Assisted })
            {
                if (c == null || string.IsNullOrEmpty(c.instructionText)) continue;
                if (!c.instructionText.Contains("TEST]")) continue;

                c.instructionText = "";
                cleared++;
            }

            if (cleared > 0)
            {
                EditorUtility.SetDirty(legacy);
                AssetDatabase.SaveAssets();
            }
            return cleared;
        }

        /// <summary>
        /// Several parts (camshaft, engineBlockSep002-004) carry TWO DropIntoTray
        /// components. Both would run their spawn and recovery logic on the same
        /// Rigidbody. Harmless but wrong, so the extras are removed.
        /// </summary>
        private static int RemoveDuplicateDropIntoTray(Scene scene)
        {
            int removed = 0;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (DropIntoTray first in root.GetComponentsInChildren<DropIntoTray>(true))
                {
                    if (first == null) continue;

                    DropIntoTray[] onObject = first.GetComponents<DropIntoTray>();
                    for (int i = onObject.Length - 1; i >= 1; i--)
                    {
                        Undo.DestroyObjectImmediate(onObject[i]);
                        removed++;
                    }
                }
            }

            return removed;
        }

        // =====================================================================
        // UI wiring
        // =====================================================================

        private static int WireStatusHud(Scene scene, AssemblySessionController session, StepRunner runner,
                                        SupportLevelController supportLevel, StepValidator validator)
        {
            GameObject canvas = FindByPath(scene, "StatusCanvas");
            if (canvas == null)
            {
                Debug.LogWarning("[AssemblyContent] StatusCanvas not found; status HUD skipped.");
                return 0;
            }

            StatusHud hud = GetOrAdd<StatusHud>(canvas);
            var so = new SerializedObject(hud);
            int bound = 0;

            bound += Set(so, "session", session);
            bound += Set(so, "stepRunner", runner);
            bound += Set(so, "supportLevel", supportLevel);
            bound += Set(so, "validator", validator);

            bound += Set(so, "supportLevelText", Tmp(scene, "StatusCanvas/TopPanel/CaptionText"));
            bound += Set(so, "timeValueText", Tmp(scene, "StatusCanvas/TimerText/TimeValue"));
            bound += Set(so, "errorValueText", Tmp(scene, "StatusCanvas/ErrorText/ErrorValue"));
            bound += Set(so, "progressValueText", Tmp(scene, "StatusCanvas/ProgressText/ProgressValue"));

            // Progress bar: prefer a Slider, otherwise scale the Fill Area.
            GameObject bar = FindByPath(scene, "StatusCanvas/ProgressBar");
            if (bar != null)
            {
                Slider slider = bar.GetComponent<Slider>();
                if (slider != null)
                {
                    bound += Set(so, "progressSlider", slider);
                }
                else
                {
                    Transform fill = bar.transform.Find("Fill Area");
                    if (fill != null)
                        bound += Set(so, "progressFill", fill as RectTransform);
                }
            }

            so.ApplyModifiedProperties();
            return bound;
        }

        private static int WireTaskListHud(Scene scene, AssemblySessionController session, StepRunner runner)
        {
            GameObject canvas = FindByPath(scene, "OverviewCanvas");
            if (canvas == null)
            {
                Debug.LogWarning("[AssemblyContent] OverviewCanvas not found; task list skipped.");
                return 0;
            }

            TaskListHud hud = GetOrAdd<TaskListHud>(canvas);
            var so = new SerializedObject(hud);

            Set(so, "session", session);
            Set(so, "stepRunner", runner);
            Set(so, "headingText", Tmp(scene, "OverviewCanvas/TopPanel/CaptionText"));

            SerializedProperty rows = so.FindProperty("rowTexts");
            int count = 0;
            if (rows != null)
            {
                rows.ClearArray();
                foreach (string path in new[] {
                    "OverviewCanvas/TaskA/TaskaText",
                    "OverviewCanvas/TaskB/TaskbText",
                    "OverviewCanvas/TaskC/TaskcText" })
                {
                    TextMeshProUGUI t = Tmp(scene, path);
                    if (t == null) continue;

                    rows.InsertArrayElementAtIndex(count);
                    rows.GetArrayElementAtIndex(count).objectReferenceValue = t;
                    count++;
                }
            }

            so.ApplyModifiedProperties();
            return count;
        }

        /// <summary>
        /// Sets up the manual step-advance control.
        ///
        /// NextButton is used rather than GotItButton, because StepManager hides GotItButton
        /// permanently once the session begins - it is the Begin control, not a per-step one.
        /// NextButton already exists on the canvas and was inactive and unused.
        ///
        /// GotItButton's stale persistent call (to a method that no longer exists) is cleared
        /// at the same time, but nothing is added to it.
        /// </summary>
        private static bool WireConfirmButton(Scene scene, AssemblySessionController session)
        {
            if (session == null) return false;

            // --- clear the dead call on the Begin button ---
            GameObject gotIt = FindByPath(scene, "DemoUICanvas/ButtonsPanel/GotItButton");
            if (gotIt != null)
            {
                Button b = gotIt.GetComponent<Button>();
                if (b != null)
                {
                    Undo.RecordObject(b, "Clear stale confirm wiring");
                    for (int i = b.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
                        UnityEditor.Events.UnityEventTools.RemovePersistentListener(b.onClick, i);
                    EditorUtility.SetDirty(b);
                }
            }

            // --- NextButton becomes the per-step advance ---
            GameObject go = FindByPath(scene, "DemoUICanvas/NextButton");
            if (go == null)
            {
                Debug.LogWarning("[AssemblyContent] DemoUICanvas/NextButton not found; " +
                                 "there will be no manual step-advance control.");
                return false;
            }

            Button button = go.GetComponent<Button>();
            if (button == null) return false;

            if (!go.activeSelf)
            {
                Undo.RecordObject(go, "Activate NextButton");
                go.SetActive(true);
            }

            Undo.RecordObject(button, "Wire advance button");
            for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
                UnityEditor.Events.UnityEventTools.RemovePersistentListener(button.onClick, i);

            UnityEditor.Events.UnityEventTools.AddVoidPersistentListener(
                button.onClick, session.AdvanceStepManually);

            // Label it for what it now does.
            TextMeshProUGUI label = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
            {
                Undo.RecordObject(label, "Label advance button");
                label.text = "Next Step";
                EditorUtility.SetDirty(label);
            }

            EditorUtility.SetDirty(button);
            return true;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static T GetOrAdd<T>(GameObject go) where T : Component
        {
            T existing = go.GetComponent<T>();
            return existing != null ? existing : Undo.AddComponent<T>(go);
        }

        private static void SetRefs(Component target, params (string field, Object value)[] pairs)
        {
            var so = new SerializedObject(target);
            foreach (var p in pairs) Set(so, p.field, p.value);
            so.ApplyModifiedProperties();
        }

        private static int Set(SerializedObject so, string field, Object value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null)
            {
                Debug.LogWarning($"[AssemblyContent] Field '{field}' not found on {so.targetObject.GetType().Name}.");
                return 0;
            }
            p.objectReferenceValue = value;
            return value != null ? 1 : 0;
        }

        private static TextMeshProUGUI Tmp(Scene scene, string path)
        {
            GameObject go = FindByPath(scene, path);
            return go != null ? go.GetComponent<TextMeshProUGUI>() : null;
        }

        private static T FindComponent<T>(Scene scene) where T : Component
        {
            if (!scene.IsValid()) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T c = root.GetComponentInChildren<T>(true);
                if (c != null) return c;
            }
            return null;
        }

        /// <summary>Resolves "A/B/C", finding the first segment anywhere in the scene.</summary>
        private static GameObject FindByPath(Scene scene, string path)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            Transform current = FindAnywhere(scene, parts[0]);
            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null) return null;
            }
            return current.gameObject;
        }

        private static Transform FindAnywhere(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root.transform;
                Transform deep = FindDeep(root.transform, name);
                if (deep != null) return deep;
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
