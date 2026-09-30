// File: PistonAssemblySetup.cs
// Turns each piston into a kit that must be BUILT before it is installed.
//
// The task was wrong before: it treated piston001-004 as finished objects whose only
// job was to be dropped into a bore. The real procedure is head -> rod -> connecting
// pin -> rod end -> fastener -> install the completed assembly.
//
// NON-DESTRUCTIVE, deliberately
// -----------------------------
// The imported model is never edited. For each piston this creates DERIVED objects:
//
//   Components/PistonKits/PistonKit00N          the growing assembly (a kit root)
//     <component duplicates>                    grabbable, staged in the tray
//   Ghosties/PistonKits/PistonKit00N            ghost targets at the assembled poses,
//                                               positioned at a build zone on the bench
//
// The original Components/piston00N is deactivated, never deleted, so reverting is
// one checkbox and `git checkout` of the scene restores everything regardless.
//
// The build zone sits beside the tray: the piston is assembled on the bench and then
// installed into the engine, which is what the physical task actually looks like.

using System.Collections.Generic;
using System.Text;
using AdaptiveAR.Logging;
using AdaptiveAR.Steps;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveAR.EditorTools
{
    public static class PistonAssemblySetup
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";
        private const string ComponentsPath = "EngineAnchor/Offset/Components";
        private const string GhostiesPath = "EngineAnchor/Offset/Ghosties";
        private const string KitGroup = "PistonKits";
        private const string IsdkChildName = "ISDK_HandGrabInteraction";

        private const string PartPrefix = "part.";
        private const string GhostPrefix = "ghost.";

        private static readonly string[] Pistons = { "piston001", "piston002", "piston003", "piston004" };

        /// <summary>
        /// The assembly order the reviewer specified, mapped onto the real child meshes.
        /// Nothing beyond this is invented: no torque, no thread direction, no bolt count
        /// that the model does not contain.
        /// </summary>
        private static readonly (string child, string verb, bool enabledByDefault)[] Sequence =
        {
            ("PistonHead",      "Fit the piston head onto the build area.",                  true),
            ("ConnectingRod",   "Align the connecting rod with the piston head.",            true),
            ("ConnectingPin",   "Fit the connecting pin to join the rod to the head.",       true),
            ("PistonEnd",       "Fit the rod end cap onto the connecting rod.",              true),
            ("pistonBolt",      "Fit the retaining bolt.",                                   true),
            ("PistonNut",       "Fit the retaining nut.",                                    false),
            ("pistonBoltOther", "Fit the second retaining bolt.",                            false),
            ("PistonNutOther",  "Fit the second retaining nut.",                             false)
        };

        // =====================================================================
        // 1. DRY RUN
        // =====================================================================

        [MenuItem("AdaptiveAR/Piston/1 - Validate Piston Kits (dry run)", false, 10)]
        public static void Validate()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject components = Find(scene, ComponentsPath);

            var r = new StringBuilder();
            r.AppendLine("=== Piston kit build (DRY RUN - nothing modified) ===");

            if (components == null)
            {
                r.AppendLine($"  MISSING {ComponentsPath}");
                Debug.Log(r.ToString());
                return;
            }

            foreach (string p in Pistons)
            {
                Transform piston = components.transform.Find(p);
                if (piston == null) { r.AppendLine($"  {p,-12} MISSING"); continue; }

                var found = new List<string>();
                var missing = new List<string>();

                foreach (var step in Sequence)
                {
                    if (piston.Find(step.child) != null) found.Add(step.child);
                    else missing.Add(step.child);
                }

                r.AppendLine($"  {p,-12} {found.Count}/{Sequence.Length} component(s) present" +
                             (missing.Count > 0 ? "   missing: " + string.Join(", ", missing) : ""));
            }

            int enabled = 0;
            foreach (var s in Sequence) if (s.enabledByDefault) enabled++;

            r.AppendLine();
            r.AppendLine("Apply would, per piston:");
            r.AppendLine($"    create a kit root in Components/{KitGroup}");
            r.AppendLine($"    duplicate {Sequence.Length} component(s) as grabbable parts, staged in the tray");
            r.AppendLine($"    duplicate {Sequence.Length} ghost target(s) at the assembled poses, at a bench build zone");
            r.AppendLine("    deactivate (NOT delete) the original preassembled piston");
            r.AppendLine($"    author {enabled} enabled assembly action(s) + 1 install action");
            r.AppendLine($"    author {Sequence.Length - enabled} further fastener action(s), DISABLED for session length");
            r.AppendLine();
            r.AppendLine("The imported model is not edited. Undo reverts in one step; git checkout of");
            r.AppendLine("the scene is the authoritative revert.");

            Debug.Log(r.ToString());
        }

        // =====================================================================
        // 2. APPLY
        // =====================================================================

        [MenuItem("AdaptiveAR/Piston/2 - Build Piston Kits", false, 11)]
        public static void Apply()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[Piston] Wrong scene '{scene.name}'. Aborted.");
                return;
            }

            GameObject components = Find(scene, ComponentsPath);
            GameObject ghosties = Find(scene, GhostiesPath);
            GuidanceRegistry registry = FindComponent<GuidanceRegistry>(scene);
            SessionLogger logger = FindComponent<SessionLogger>(scene);

            if (components == null || ghosties == null || registry == null)
            {
                Debug.LogError("[Piston] Required objects missing. Run the dry run first. Aborted.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Piston: Build Kits");

            Transform partGroup = EnsureChild(components.transform, KitGroup);
            Transform ghostGroup = EnsureChild(ghosties.transform, KitGroup);

            Vector3 buildZone = ComputeBuildZone(components.transform);
            Vector3 stageOrigin = ComputeStageOrigin(components.transform);

            var entries = new List<(string key, GameObject go, bool isPart)>();
            int kits = 0, parts = 0;

            for (int pi = 0; pi < Pistons.Length; pi++)
            {
                string pistonName = Pistons[pi];
                Transform source = components.transform.Find(pistonName);
                if (source == null) continue;

                string kitName = "PistonKit" + pistonName.Substring("piston".Length);

                // --- kit root: the growing assembly, offset per piston so kits do not overlap ---
                Transform kit = EnsureChild(partGroup, kitName);
                kit.position = buildZone + components.transform.right * (pi * 0.22f);
                kit.rotation = source.rotation;

                // --- ghost root: targets at the assembled poses, at this kit's build spot ---
                Transform ghostKit = EnsureChild(ghostGroup, kitName);
                ghostKit.position = kit.position;
                ghostKit.rotation = kit.rotation;

                var required = new List<Transform>();
                int slot = 0;

                foreach (var step in Sequence)
                {
                    Transform child = source.Find(step.child);
                    if (child == null) continue;

                    string key = kitName + "." + step.child;

                    // --- ghost target: the child's pose RELATIVE to its piston, reproduced
                    // --- at the build zone, so assembling on the bench matches the real part.
                    GameObject target = Object.Instantiate(child.gameObject, ghostKit);
                    target.name = step.child;
                    target.transform.localPosition = child.localPosition;
                    target.transform.localRotation = child.localRotation;
                    target.transform.localScale = child.localScale;
                    Undo.RegisterCreatedObjectUndo(target, "Create piston ghost target");

                    StripInteraction(target);
                    target.SetActive(false);              // shown only by its own action
                    entries.Add((GhostPrefix + key, target, false));

                    // --- grabbable part: same mesh, staged out in the tray ---
                    GameObject part = Object.Instantiate(child.gameObject, kit);
                    part.name = step.child;
                    Undo.RegisterCreatedObjectUndo(part, "Create piston part");

                    part.transform.position = stageOrigin
                        + components.transform.right * (slot % 4) * 0.045f
                        + components.transform.forward * (slot / 4) * 0.045f
                        + components.transform.up * (pi * 0.05f);
                    part.transform.rotation = child.rotation;

                    MakeGrabbable(part, source);
                    entries.Add((PartPrefix + key, part, true));

                    if (step.enabledByDefault) required.Add(part.transform);
                    parts++;
                    slot++;
                }

                // --- the kit itself becomes the manipulation unit once built ---
                var assembly = kit.GetComponent<PistonAssembly>();
                if (assembly == null) assembly = Undo.AddComponent<PistonAssembly>(kit.gameObject);
                assembly.Configure(PartPrefix + kitName, required, logger);

                MakeGrabbable(kit.gameObject, source);
                entries.Add((PartPrefix + kitName, kit.gameObject, true));

                // --- the original preassembled piston steps aside, intact ---
                if (source.gameObject.activeSelf)
                {
                    Undo.RecordObject(source.gameObject, "Deactivate preassembled piston");
                    source.gameObject.SetActive(false);
                }

                kits++;
            }

            int registered = RegisterKeys(registry, entries);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                $"[Piston] Built {kits} piston kit(s).\n" +
                $"  Grabbable components : {parts}, staged in the tray\n" +
                $"  Registry keys        : {registered}\n" +
                "  Original piston001-004 deactivated, NOT deleted - the imported model is untouched.\n" +
                "  NEXT: 'AdaptiveAR > Piston > 3 - Author Piston Substeps'.\n" +
                "  SAVE THE SCENE.");
        }

        // =====================================================================
        // 3. AUTHOR THE SUBSTEPS
        // =====================================================================

        [MenuItem("AdaptiveAR/Piston/3 - Author Piston Substeps", false, 12)]
        public static void AuthorSubsteps()
        {
            int stages = 0, actions = 0;

            for (int pi = 0; pi < Pistons.Length; pi++)
            {
                string assetPath = $"Assets/ScriptableObjects/Steps/Step_0{pi + 2}_Piston00{pi + 1}.asset";
                StepData stage = AssetDatabase.LoadAssetAtPath<StepData>(assetPath);

                if (stage == null)
                {
                    Debug.LogWarning($"[Piston] {assetPath} not found. Run " +
                                     "'Assembly > 2 - Apply Full Assembly Content' first.");
                    continue;
                }

                Undo.RecordObject(stage, "Author piston substeps");

                string kitName = "PistonKit00" + (pi + 1);
                string pistonName = Pistons[pi];
                var list = new List<AssemblyAction>();

                // Locate stays: it orients the participant before any manipulation.
                list.Add(new AssemblyAction
                {
                    actionId = stage.stepId + ".locate",
                    kind = ActionKind.Acknowledge,
                    instruction = "Find the piston components in the parts tray.",
                    detail = "You will build the piston, then install it.",
                    enabled = true,
                    showArrow = false
                });

                foreach (var step in Sequence)
                {
                    string key = kitName + "." + step.child;

                    list.Add(new AssemblyAction
                    {
                        actionId = stage.stepId + "." + step.child,
                        kind = step.child.Contains("Bolt") || step.child.Contains("Nut")
                            ? ActionKind.Fasten
                            : ActionKind.Place,
                        instruction = step.verb,
                        detail = "",
                        partKey = PartPrefix + key,
                        targetKey = GhostPrefix + key,
                        ghostKeys = new[] { GhostPrefix + key },
                        positionToleranceMeters = 0.025f,
                        rotationToleranceDegrees = 25f,
                        settleSeconds = 0.4f,
                        showArrow = true,
                        enabled = step.enabledByDefault,
                        disabledReason = step.enabledByDefault
                            ? ""
                            : "Authored but off by default to keep a run inside the 15 minute " +
                              "session target. Enable for a full-procedure run."
                    });

                    actions++;
                }

                // Install the finished assembly into the engine.
                list.Add(new AssemblyAction
                {
                    actionId = stage.stepId + ".install",
                    kind = ActionKind.Place,
                    instruction = "Install the assembled piston into its cylinder bore.",
                    detail = "Lower the completed assembly onto the highlighted target.",
                    partKey = PartPrefix + kitName,
                    targetKey = GhostPrefix + pistonName,
                    ghostKeys = new[] { GhostPrefix + pistonName },
                    positionToleranceMeters = 0.04f,
                    rotationToleranceDegrees = 25f,
                    settleSeconds = 0.45f,
                    showArrow = true,
                    enabled = true
                });

                stage.actions = list;
                stage.displayName = "Piston " + (pi + 1);
                EditorUtility.SetDirty(stage);
                stages++;
            }

            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[Piston] Authored substeps on {stages} piston stage(s).\n" +
                "  Sequence per piston: locate, head, rod, connecting pin, rod end, bolt, install.\n" +
                "  Three further fasteners are authored but DISABLED for session length; the\n" +
                "  reason is stored in the asset and written to the log.\n" +
                "  No torque, thread direction or extra bolt count was invented - only the\n" +
                "  component meshes the model actually contains.\n" +
                "  SAVE THE SCENE.");
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>
        /// Gives a derived object the single-Rigidbody, convex-collider shape that the
        /// grab diagnosis proved is required, and clones the ISDK block from the source
        /// piston so the interaction components match what already works.
        /// </summary>
        private static void MakeGrabbable(GameObject go, Transform interactionSource)
        {
            // Remove any nested Rigidbody: a collider belongs to its nearest Rigidbody
            // ancestor, so nested bodies leave the root owning nothing to hit.
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true))
                if (rb.gameObject != go) Object.DestroyImmediate(rb);

            var body = go.GetComponent<Rigidbody>();
            if (body == null) body = Undo.AddComponent<Rigidbody>(go);
            body.isKinematic = true;
            body.useGravity = false;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;

                MeshCollider[] existing = mf.GetComponents<MeshCollider>();
                for (int i = existing.Length - 1; i >= 1; i--) Object.DestroyImmediate(existing[i]);

                MeshCollider mc = mf.GetComponent<MeshCollider>();
                if (mc == null)
                {
                    mc = Undo.AddComponent<MeshCollider>(mf.gameObject);
                    mc.sharedMesh = mf.sharedMesh;
                }
                mc.convex = true;
            }

            // Clone the working interaction block if this object has none.
            if (go.transform.Find(IsdkChildName) == null && interactionSource != null)
            {
                Transform template = interactionSource.Find(IsdkChildName);
                if (template != null)
                {
                    GameObject clone = Object.Instantiate(template.gameObject, go.transform);
                    clone.name = IsdkChildName;
                    clone.transform.localPosition = Vector3.zero;
                    clone.transform.localRotation = Quaternion.identity;
                    Undo.RegisterCreatedObjectUndo(clone, "Clone interaction block");
                    RetargetRigidbodies(clone, body);
                }
            }

            if (go.GetComponent<PlacementLock>() == null)
                Undo.AddComponent<PlacementLock>(go);
        }

        /// <summary>Points every Rigidbody reference inside a cloned block at the new body.</summary>
        private static void RetargetRigidbodies(GameObject clone, Rigidbody body)
        {
            foreach (Component c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;

                var so = new SerializedObject(c);
                SerializedProperty it = so.GetIterator();
                bool changed = false;

                while (it.NextVisible(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (it.objectReferenceValue is Rigidbody && (Object)body != it.objectReferenceValue)
                    {
                        it.objectReferenceValue = body;
                        changed = true;
                    }
                }

                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        /// <summary>A ghost is a target, never something to pick up.</summary>
        private static void StripInteraction(GameObject go)
        {
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(rb);

            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);

            foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string ns = mb.GetType().Namespace;
                if (!string.IsNullOrEmpty(ns) && ns.StartsWith("Oculus.Interaction"))
                    Object.DestroyImmediate(mb);
            }

            Transform isdk = go.transform.Find(IsdkChildName);
            if (isdk != null) Object.DestroyImmediate(isdk.gameObject);
        }

        private static Vector3 ComputeBuildZone(Transform components)
        {
            Transform offset = components.parent;
            Transform tray = offset != null ? offset.Find("tray") : null;

            return tray != null
                ? tray.position + Vector3.up * 0.06f + offset.forward * 0.12f
                : components.position + Vector3.up * 0.15f;
        }

        private static Vector3 ComputeStageOrigin(Transform components)
        {
            Transform offset = components.parent;
            Transform tray = offset != null ? offset.Find("tray (1)") : null;
            if (tray == null && offset != null) tray = offset.Find("tray");

            return tray != null ? tray.position + Vector3.up * 0.05f
                                : components.position + Vector3.up * 0.1f;
        }

        private static int RegisterKeys(GuidanceRegistry registry, List<(string key, GameObject go, bool isPart)> entries)
        {
            var so = new SerializedObject(registry);
            SerializedProperty list = so.FindProperty("entries");
            if (list == null) return 0;

            int n = 0;
            foreach (var e in entries)
            {
                int idx = -1;
                for (int i = 0; i < list.arraySize; i++)
                    if (list.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue == e.key) { idx = i; break; }

                if (idx < 0)
                {
                    list.InsertArrayElementAtIndex(list.arraySize);
                    idx = list.arraySize - 1;
                }

                SerializedProperty el = list.GetArrayElementAtIndex(idx);
                el.FindPropertyRelative("key").stringValue = e.key;
                el.FindPropertyRelative("target").objectReferenceValue = e.go;

                SerializedProperty ex = el.FindPropertyRelative("excludeFromAutoHide");
                if (ex != null) ex.boolValue = e.isPart;

                n++;
            }

            so.ApplyModifiedProperties();
            return n;
        }

        private static Transform EnsureChild(Transform parent, string name)
        {
            Transform t = parent.Find(name);
            if (t != null) return t;

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create group");
            go.transform.SetParent(parent, false);
            return go.transform;
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
