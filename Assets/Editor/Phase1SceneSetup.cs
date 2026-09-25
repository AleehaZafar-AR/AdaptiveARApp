// File: Phase1SceneSetup.cs
// Editor-only setup tool for the Phase 1 step / support-level foundation.
//
// This tool NEVER writes scene YAML directly. It adds components and assigns
// serialized fields through Unity's own APIs, so the editor serialises the scene
// and corruption is structurally impossible.
//
// Guarantees:
//   - "Validate Scene (dry run)" makes ZERO modifications.
//   - "Wire Foundation" is idempotent (re-running finds existing components) and
//     fully Undo-able as a single collapsed operation.
//   - The new StepRunner path is NEVER activated automatically. Activation is a
//     separate, deliberate menu item, so the original fallback stays intact until
//     you explicitly flip it.
//
// This file lives in Assets/Editor/, so it is excluded from player builds and no
// UnityEditor reference reaches runtime.

using System.Collections.Generic;
using System.Text;
using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveAR.EditorTools
{
    public static class Phase1SceneSetup
    {
        // ---------------- Expected scene layout ----------------

        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";

        private const string PathStepManager = "StepManager";
        private const string PathCaptionText = "DemoUICanvas/InstructionPanel/CaptionText";
        private const string PathAudioSource = "AudioSource";
        private const string PathEngineAnchor = "EngineAnchor";
        private const string PathGhosties = "EngineAnchor/Offset/Ghosties";

        private const string StepAssetFolder = "Assets/ScriptableObjects/Steps";
        private const string StepAssetPath = StepAssetFolder + "/Step_Crankshaft_Phase1.asset";

        private const string GhostKeyPrefix = "ghost.";

        // Temporary verification fixtures. NOT research content - see TestFixtures region.
        private const string TestLabelL1 = "[L1 TEST]";
        private const string TestLabelL2 = "[L2 TEST]";
        private const string TestLabelL3 = "[L3 TEST]";

        // =========================================================================
        // 1. VALIDATE (dry run) - reads only, modifies nothing
        // =========================================================================

        [MenuItem("AdaptiveAR/Phase 1/1 - Validate Scene (dry run)", false, 10)]
        public static void ValidateScene()
        {
            var report = new StringBuilder();
            report.AppendLine("=== Phase 1 Validation (DRY RUN - nothing was modified) ===");

            Scene scene = SceneManager.GetActiveScene();
            report.AppendLine($"Active scene: '{scene.name}'");

            if (scene.name != ExpectedSceneName)
            {
                report.AppendLine($"  WARNING: expected '{ExpectedSceneName}'. Wiring will refuse to run.");
            }

            bool ok = true;

            GameObject stepManagerGo = FindByPath(scene, PathStepManager);
            ok &= ReportObject(report, PathStepManager, stepManagerGo);

            GameObject captionGo = FindByPath(scene, PathCaptionText);
            ok &= ReportObject(report, PathCaptionText, captionGo);
            if (captionGo != null && captionGo.GetComponent<TextMeshProUGUI>() == null)
            {
                report.AppendLine("    ERROR: no TextMeshProUGUI on the caption object.");
                ok = false;
            }

            GameObject audioGo = FindByPath(scene, PathAudioSource);
            ok &= ReportObject(report, PathAudioSource, audioGo);
            if (audioGo != null && audioGo.GetComponent<AudioSource>() == null)
            {
                report.AppendLine("    ERROR: no AudioSource component on the audio object.");
                ok = false;
            }

            GameObject anchorGo = FindByPath(scene, PathEngineAnchor);
            ok &= ReportObject(report, PathEngineAnchor, anchorGo);

            GameObject ghostiesGo = FindByPath(scene, PathGhosties);
            ok &= ReportObject(report, PathGhosties, ghostiesGo);

            // --- components that WOULD be added ---
            report.AppendLine();
            report.AppendLine("Components on 'StepManager' GameObject:");
            if (stepManagerGo != null)
            {
                ReportComponent<StepManager>(report, stepManagerGo);
                ReportComponent<SupportLevelController>(report, stepManagerGo);
                ReportComponent<GuidanceRegistry>(report, stepManagerGo);
                ReportComponent<StepRunner>(report, stepManagerGo);
                ReportComponent<StepPresenter>(report, stepManagerGo);
                ReportComponent<SupportLevelDebugInput>(report, stepManagerGo);
            }

            // --- guidance keys that WOULD be registered ---
            report.AppendLine();
            if (ghostiesGo != null)
            {
                int count = ghostiesGo.transform.childCount;
                report.AppendLine($"Guidance keys that would be registered from '{PathGhosties}' ({count}):");
                for (int i = 0; i < count; i++)
                {
                    Transform child = ghostiesGo.transform.GetChild(i);
                    report.AppendLine($"    {GhostKeyPrefix}{child.name}   (active: {child.gameObject.activeSelf})");
                }
            }

            // --- current behaviour that would be copied into the step asset ---
            report.AppendLine();
            StepManager stepManager = stepManagerGo != null ? stepManagerGo.GetComponent<StepManager>() : null;
            if (stepManager != null)
            {
                report.AppendLine("Values that would be copied into the step asset (from the live StepManager):");
                report.AppendLine($"    ghostPrefab             : {NameOf(stepManager.crankshaftPrefab)}");
                report.AppendLine($"    defaultGhostSpawnOffset : {stepManager.crankshaftSpawnOffset}");
                report.AppendLine("    stepTitle               : \"Pick up the crankshaft.\"");

                report.AppendLine();
                report.AppendLine($"Activation switch (StepManager.stepRunner): {(stepManager.stepRunner == null ? "EMPTY - original behaviour active" : "ASSIGNED - step system active")}");
            }

            StepData existingAsset = AssetDatabase.LoadAssetAtPath<StepData>(StepAssetPath);
            report.AppendLine();
            report.AppendLine($"Step asset '{StepAssetPath}': {(existingAsset == null ? "does not exist yet" : "already exists (would be updated in place)")}");

            report.AppendLine();
            report.AppendLine(ok
                ? "RESULT: all required scene objects resolved. Safe to run 'Wire Foundation'."
                : "RESULT: one or more objects could not be resolved. Fix the paths above before wiring.");

            Debug.Log(report.ToString());
        }

        // =========================================================================
        // 2. WIRE FOUNDATION - idempotent, Undo-able, does NOT activate
        // =========================================================================

        [MenuItem("AdaptiveAR/Phase 1/2 - Wire Foundation", false, 11)]
        public static void WireFoundation()
        {
            Scene scene = SceneManager.GetActiveScene();

            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[Phase1Setup] Active scene is '{scene.name}', expected '{ExpectedSceneName}'. Aborted.");
                return;
            }

            GameObject stepManagerGo = FindByPath(scene, PathStepManager);
            GameObject captionGo = FindByPath(scene, PathCaptionText);
            GameObject audioGo = FindByPath(scene, PathAudioSource);
            GameObject anchorGo = FindByPath(scene, PathEngineAnchor);
            GameObject ghostiesGo = FindByPath(scene, PathGhosties);

            if (stepManagerGo == null || captionGo == null || anchorGo == null)
            {
                Debug.LogError("[Phase1Setup] Required scene objects missing. Run 'Validate Scene (dry run)' first. Aborted.");
                return;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Phase 1: Wire Support-Level Foundation");

            // --- components (idempotent) ---
            SupportLevelController supportLevel = GetOrAddComponent<SupportLevelController>(stepManagerGo);
            GuidanceRegistry registry = GetOrAddComponent<GuidanceRegistry>(stepManagerGo);
            StepRunner runner = GetOrAddComponent<StepRunner>(stepManagerGo);
            StepPresenter presenter = GetOrAddComponent<StepPresenter>(stepManagerGo);
            SupportLevelDebugInput debugInput = GetOrAddComponent<SupportLevelDebugInput>(stepManagerGo);

            // --- StepRunner ---
            var runnerSo = new SerializedObject(runner);
            SetObjectRef(runnerSo, "supportLevel", supportLevel);
            runnerSo.ApplyModifiedProperties();

            // --- StepPresenter ---
            var presenterSo = new SerializedObject(presenter);
            SetObjectRef(presenterSo, "stepRunner", runner);
            SetObjectRef(presenterSo, "supportLevel", supportLevel);
            SetObjectRef(presenterSo, "guidanceRegistry", registry);
            SetObjectRef(presenterSo, "captionText", captionGo.GetComponent<TextMeshProUGUI>());
            SetObjectRef(presenterSo, "anchorRoot", anchorGo.transform);
            if (audioGo != null)
                SetObjectRef(presenterSo, "audioSource", audioGo.GetComponent<AudioSource>());
            presenterSo.ApplyModifiedProperties();

            // --- SupportLevelDebugInput ---
            var debugSo = new SerializedObject(debugInput);
            SetObjectRef(debugSo, "supportLevel", supportLevel);
            debugSo.ApplyModifiedProperties();

            // --- GuidanceRegistry entries, rebuilt from the existing Ghosties subtree ---
            int keyCount = PopulateGuidanceRegistry(registry, ghostiesGo);

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[Phase1Setup] Foundation wired.\n" +
                $"  Components on '{PathStepManager}': SupportLevelController, GuidanceRegistry, StepRunner, StepPresenter, SupportLevelDebugInput\n" +
                $"  Guidance keys registered: {keyCount}\n" +
                "  StepRunner.steps is still EMPTY - run 'Create Crankshaft Step Asset' next.\n" +
                "  StepManager.stepRunner is still EMPTY - the original behaviour is unchanged until you run 'Activate Step System'.\n" +
                "  Save the scene to persist. Ctrl+Z reverts this in one step.");
        }

        private static int PopulateGuidanceRegistry(GuidanceRegistry registry, GameObject ghosties)
        {
            if (ghosties == null)
            {
                Debug.LogWarning($"[Phase1Setup] '{PathGhosties}' not found; guidance registry left unchanged.");
                return 0;
            }

            var so = new SerializedObject(registry);
            SerializedProperty entries = so.FindProperty("entries");
            if (entries == null)
            {
                Debug.LogError("[Phase1Setup] GuidanceRegistry.entries not found.");
                return 0;
            }

            // Preserve any hand-authored keys that do not come from Ghosties.
            var preserved = new List<KeyValuePair<string, Object>>();
            for (int i = 0; i < entries.arraySize; i++)
            {
                SerializedProperty e = entries.GetArrayElementAtIndex(i);
                string key = e.FindPropertyRelative("key").stringValue;
                Object target = e.FindPropertyRelative("target").objectReferenceValue;

                if (!key.StartsWith(GhostKeyPrefix))
                    preserved.Add(new KeyValuePair<string, Object>(key, target));
            }

            entries.ClearArray();
            int index = 0;

            for (int i = 0; i < ghosties.transform.childCount; i++)
            {
                Transform child = ghosties.transform.GetChild(i);
                entries.InsertArrayElementAtIndex(index);
                SerializedProperty e = entries.GetArrayElementAtIndex(index);
                e.FindPropertyRelative("key").stringValue = GhostKeyPrefix + child.name;
                e.FindPropertyRelative("target").objectReferenceValue = child.gameObject;
                index++;
            }

            foreach (var kv in preserved)
            {
                entries.InsertArrayElementAtIndex(index);
                SerializedProperty e = entries.GetArrayElementAtIndex(index);
                e.FindPropertyRelative("key").stringValue = kv.Key;
                e.FindPropertyRelative("target").objectReferenceValue = kv.Value;
                index++;
            }

            so.ApplyModifiedProperties();
            return index;
        }

        // =========================================================================
        // 3. CREATE CRANKSHAFT STEP ASSET
        // =========================================================================

        [MenuItem("AdaptiveAR/Phase 1/3 - Create Crankshaft Step Asset", false, 12)]
        public static void CreateCrankshaftStepAsset()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject stepManagerGo = FindByPath(scene, PathStepManager);
            StepManager stepManager = stepManagerGo != null ? stepManagerGo.GetComponent<StepManager>() : null;

            if (stepManager == null)
            {
                Debug.LogError("[Phase1Setup] StepManager not found in the active scene. " +
                               "The step defaults are copied from it, so open the live scene first. Aborted.");
                return;
            }

            if (!AssetDatabase.IsValidFolder(StepAssetFolder))
            {
                Debug.LogError($"[Phase1Setup] Folder '{StepAssetFolder}' does not exist. Aborted.");
                return;
            }

            StepData step = AssetDatabase.LoadAssetAtPath<StepData>(StepAssetPath);
            bool created = false;

            if (step == null)
            {
                step = ScriptableObject.CreateInstance<StepData>();
                AssetDatabase.CreateAsset(step, StepAssetPath);
                created = true;
            }

            Undo.RecordObject(step, "Phase 1: Author Crankshaft Step Defaults");

            // Step-level DEFAULTS reproduce today's hard-coded behaviour exactly.
            // Values are copied from the live StepManager rather than guessed.
            step.stepId = "step_01_crankshaft";
            step.stepTitle = "Pick up the crankshaft.";
            step.ghostPrefab = stepManager.crankshaftPrefab;
            step.defaultGhostSpawnOffset = stepManager.crankshaftSpawnOffset;
            step.defaultSupportLevel = SupportLevel.L1_Minimal;

            // L1/L2/L3 blocks are intentionally left unauthored.
            if (step.l1Minimal == null) step.l1Minimal = new StepSupportContent();
            if (step.l2Guided == null) step.l2Guided = new StepSupportContent();
            if (step.l3Assisted == null) step.l3Assisted = new StepSupportContent();

            EditorUtility.SetDirty(step);
            AssetDatabase.SaveAssets();

            // Add it to StepRunner.steps if the runner exists and does not already have it.
            int addedToRunner = 0;
            StepRunner runner = stepManagerGo.GetComponent<StepRunner>();
            if (runner != null)
                addedToRunner = EnsureStepInRunner(runner, step, scene);

            Debug.Log(
                $"[Phase1Setup] Step asset {(created ? "created" : "updated")}: {StepAssetPath}\n" +
                $"  stepTitle               : \"{step.stepTitle}\"\n" +
                $"  ghostPrefab             : {NameOf(step.ghostPrefab)}\n" +
                $"  defaultGhostSpawnOffset : {step.defaultGhostSpawnOffset}\n" +
                $"  L1/L2/L3 blocks         : empty (unauthored, by design)\n" +
                (runner == null
                    ? "  StepRunner not found - run 'Wire Foundation' first, then re-run this.\n"
                    : $"  StepRunner.steps        : {(addedToRunner > 0 ? "step added" : "already present")}\n") +
                "  StepManager.stepRunner is still EMPTY - original behaviour unchanged.");
        }

        private static int EnsureStepInRunner(StepRunner runner, StepData step, Scene scene)
        {
            var so = new SerializedObject(runner);
            SerializedProperty steps = so.FindProperty("steps");
            if (steps == null)
                return 0;

            for (int i = 0; i < steps.arraySize; i++)
            {
                if (steps.GetArrayElementAtIndex(i).objectReferenceValue == step)
                    return 0; // already present - idempotent
            }

            steps.InsertArrayElementAtIndex(steps.arraySize);
            steps.GetArrayElementAtIndex(steps.arraySize - 1).objectReferenceValue = step;
            so.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(scene);
            return 1;
        }

        // =========================================================================
        // 4. ACTIVATE / DEACTIVATE - the deliberate switch
        // =========================================================================

        [MenuItem("AdaptiveAR/Phase 1/4 - Activate Step System", false, 13)]
        public static void ActivateStepSystem()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject go = FindByPath(scene, PathStepManager);
            StepManager stepManager = go != null ? go.GetComponent<StepManager>() : null;
            StepRunner runner = go != null ? go.GetComponent<StepRunner>() : null;

            if (stepManager == null || runner == null)
            {
                Debug.LogError("[Phase1Setup] StepManager and/or StepRunner not found. Run 'Wire Foundation' first. Aborted.");
                return;
            }

            var so = new SerializedObject(stepManager);
            SerializedProperty prop = so.FindProperty("stepRunner");
            if (prop == null)
            {
                Debug.LogError("[Phase1Setup] StepManager.stepRunner field not found. Aborted.");
                return;
            }

            prop.objectReferenceValue = runner;
            so.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log("[Phase1Setup] Step system ACTIVATED. StepManager now hands the first instruction to StepRunner.\n" +
                      "  ArUco detection, anchoring and the coordinator freeze are unchanged.\n" +
                      "  Run 'Deactivate Step System' (or Ctrl+Z) to restore the original behaviour.");
        }

        [MenuItem("AdaptiveAR/Phase 1/5 - Deactivate Step System (restore original)", false, 14)]
        public static void DeactivateStepSystem()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject go = FindByPath(scene, PathStepManager);
            StepManager stepManager = go != null ? go.GetComponent<StepManager>() : null;

            if (stepManager == null)
            {
                Debug.LogError("[Phase1Setup] StepManager not found. Aborted.");
                return;
            }

            var so = new SerializedObject(stepManager);
            SerializedProperty prop = so.FindProperty("stepRunner");
            if (prop == null)
                return;

            prop.objectReferenceValue = null;
            so.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log("[Phase1Setup] Step system DEACTIVATED. StepManager is back to its original hard-coded path.");
        }

        // =========================================================================
        // TEMPORARY TEST FIXTURES
        //
        // NOT research content. These exist only to make runtime support switching
        // visible in the headset, because unauthored L1/L2/L3 blocks all render
        // identically from the step defaults. Remove with the menu item below.
        // =========================================================================

        [MenuItem("AdaptiveAR/Phase 1/Test Fixtures/Add [L1|L2|L3 TEST] Labels", false, 30)]
        public static void AddTestLabels()
        {
            StepData step = AssetDatabase.LoadAssetAtPath<StepData>(StepAssetPath);
            if (step == null)
            {
                Debug.LogError($"[Phase1Setup] Step asset not found at {StepAssetPath}. Run step 3 first. Aborted.");
                return;
            }

            Undo.RecordObject(step, "Phase 1: Add Test Labels");

            int applied = 0;
            applied += ApplyTestLabel(step.l1Minimal, TestLabelL1, step.stepTitle);
            applied += ApplyTestLabel(step.l2Guided, TestLabelL2, step.stepTitle);
            applied += ApplyTestLabel(step.l3Assisted, TestLabelL3, step.stepTitle);

            EditorUtility.SetDirty(step);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Phase1Setup] Test labels applied to {applied}/3 levels.\n" +
                      "  These are TEMPORARY verification fixtures, not research content.\n" +
                      "  Expected in-headset: [L1 TEST] --B--> [L2 TEST] --B--> [L3 TEST] --Y--> [L2 TEST], " +
                      "with the engine anchored and the step unchanged.\n" +
                      "  Remove with 'Test Fixtures/Remove TEST Labels'.");
        }

        private static int ApplyTestLabel(StepSupportContent content, string label, string stepTitle)
        {
            if (content == null)
                return 0;

            // Never overwrite authored research content.
            if (!string.IsNullOrEmpty(content.instructionText) && !IsTestLabel(content.instructionText))
            {
                Debug.LogWarning($"[Phase1Setup] Skipped {label}: this level already has authored content.");
                return 0;
            }

            content.instructionText = label + "\n\n" + stepTitle;
            return 1;
        }

        [MenuItem("AdaptiveAR/Phase 1/Test Fixtures/Remove TEST Labels", false, 31)]
        public static void RemoveTestLabels()
        {
            StepData step = AssetDatabase.LoadAssetAtPath<StepData>(StepAssetPath);
            if (step == null)
            {
                Debug.LogError($"[Phase1Setup] Step asset not found at {StepAssetPath}. Aborted.");
                return;
            }

            Undo.RecordObject(step, "Phase 1: Remove Test Labels");

            int cleared = 0;
            cleared += ClearTestLabel(step.l1Minimal);
            cleared += ClearTestLabel(step.l2Guided);
            cleared += ClearTestLabel(step.l3Assisted);

            EditorUtility.SetDirty(step);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Phase1Setup] Test labels removed from {cleared} level(s). Authored content was left untouched.");
        }

        private static int ClearTestLabel(StepSupportContent content)
        {
            if (content == null || !IsTestLabel(content.instructionText))
                return 0;

            content.instructionText = string.Empty;
            return 1;
        }

        private static bool IsTestLabel(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            return text.StartsWith(TestLabelL1)
                || text.StartsWith(TestLabelL2)
                || text.StartsWith(TestLabelL3);
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        /// <summary>
        /// Resolves a "Root/Child/Grandchild" path within a scene.
        /// Uses Transform.Find so INACTIVE objects are found (GameObject.Find would not).
        /// </summary>
        private static GameObject FindByPath(Scene scene, string path)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(path))
                return null;

            string[] parts = path.Split('/');

            Transform current = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == parts[0])
                {
                    current = root.transform;
                    break;
                }
            }

            if (current == null)
                return null;

            for (int i = 1; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null)
                    return null;
            }

            return current.gameObject;
        }

        private static T GetOrAddComponent<T>(GameObject go) where T : Component
        {
            T existing = go.GetComponent<T>();
            if (existing != null)
                return existing;

            return Undo.AddComponent<T>(go);
        }

        private static void SetObjectRef(SerializedObject so, string fieldName, Object value)
        {
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null)
            {
                Debug.LogWarning($"[Phase1Setup] Field '{fieldName}' not found on {so.targetObject.GetType().Name}.");
                return;
            }

            prop.objectReferenceValue = value;
        }

        private static bool ReportObject(StringBuilder report, string path, GameObject go)
        {
            if (go == null)
            {
                report.AppendLine($"  MISSING : {path}");
                return false;
            }

            report.AppendLine($"  found   : {path}   (active: {go.activeInHierarchy})");
            return true;
        }

        private static void ReportComponent<T>(StringBuilder report, GameObject go) where T : Component
        {
            bool present = go.GetComponent<T>() != null;
            report.AppendLine(present
                ? $"    present    : {typeof(T).Name}"
                : $"    would add  : {typeof(T).Name}");
        }

        private static string NameOf(Object o)
        {
            return o == null ? "<none>" : o.name;
        }
    }
}
