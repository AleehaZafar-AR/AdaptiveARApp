// File: FastenerExtractionSetup.cs
// Turns the baked bearing caps and bolts into real, installable parts.
//
// The problem
// -----------
// crankHolder001-004 and crankHolderBolt002-016 exist only as sub-meshes INSIDE
// Ghosties/oilPan. They are drawn as though already fitted, and nothing can pick
// them up, so no fastening substep can be performed. That is the single asset gap
// blocking the crankshaft and camshaft stages.
//
// What this does
// --------------
// For each fastener it:
//   1. moves the existing object out of oilPan into Ghosties/Fasteners, keeping its
//      world pose - that pose IS the correct assembled position, so it becomes the
//      ghost TARGET;
//   2. duplicates it into Components/Fasteners as the movable PART, staged near the
//      tray so the participant can reach it;
//   3. registers ghost.* and part.* keys for both.
//
// oilPan_lambert2_0 and oilPanCap_lambert2_0 are left alone, so the oil pan itself
// keeps its original lambert2 materials.
//
// Bolt-to-cap mapping is DERIVED from geometry, not invented: every bolt is
// assigned to the nearest cap along the crankshaft axis. See the dry run for the
// mapping it computes from the current scene.
//
// This is a structural change to the model hierarchy. Run the dry run first. It is
// Undo-able in one step, and `git checkout` of the scene is the authoritative revert.

using System.Collections.Generic;
using System.Text;
using AdaptiveAR.Steps;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveAR.EditorTools
{
    public static class FastenerExtractionSetup
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";
        private const string OilPanPath = "EngineAnchor/Offset/Ghosties/oilPan";
        private const string GhostiesPath = "EngineAnchor/Offset/Ghosties";
        private const string ComponentsPath = "EngineAnchor/Offset/Components";

        private const string GhostGroupName = "Fasteners";
        private const string PartGroupName = "Fasteners";

        private const string GhostKeyPrefix = "ghost.";
        private const string PartKeyPrefix = "part.";

        /// <summary>Fasteners staged in a grid near the tray so they are reachable.</summary>
        private const float StageSpacingMetres = 0.035f;
        private const int StageColumns = 5;

        private class Fastener
        {
            public Transform source;
            public string cleanName;
            public bool isCap;
            public string capName;   // which cap a bolt belongs to
        }

        // =====================================================================
        // 1. DRY RUN
        // =====================================================================

        [MenuItem("AdaptiveAR/Fasteners/1 - Validate Extraction (dry run)", false, 10)]
        public static void Validate()
        {
            Scene scene = SceneManager.GetActiveScene();
            var r = new StringBuilder();
            r.AppendLine("=== Fastener Extraction (DRY RUN - nothing modified) ===");

            GameObject oilPan = Find(scene, OilPanPath);
            if (oilPan == null)
            {
                r.AppendLine($"  MISSING {OilPanPath}");
                Debug.Log(r.ToString());
                return;
            }

            List<Fastener> fasteners = Collect(oilPan.transform, out List<string> kept);

            r.AppendLine($"Found {fasteners.Count} fastener(s) baked into the oil pan.");
            r.AppendLine();
            r.AppendLine("Bolt to cap mapping, derived from position along the crankshaft axis:");

            var byCap = new Dictionary<string, List<string>>();
            foreach (Fastener f in fasteners)
            {
                if (f.isCap) continue;
                if (!byCap.TryGetValue(f.capName, out var list))
                    byCap[f.capName] = list = new List<string>();
                list.Add(f.cleanName);
            }

            foreach (Fastener f in fasteners)
            {
                if (!f.isCap) continue;
                byCap.TryGetValue(f.cleanName, out var bolts);
                r.AppendLine($"    {f.cleanName,-20} <- {(bolts == null ? 0 : bolts.Count)} bolt(s): " +
                             (bolts == null ? "-" : string.Join(", ", bolts)));
            }

            r.AppendLine();
            r.AppendLine("Left inside oilPan, materials untouched:");
            foreach (string k in kept) r.AppendLine($"    {k}");

            r.AppendLine();
            r.AppendLine("Apply would:");
            r.AppendLine($"    move  {fasteners.Count} object(s) to Ghosties/{GhostGroupName}  (become ghost targets)");
            r.AppendLine($"    create {fasteners.Count} duplicate(s) in Components/{PartGroupName}  (become movable parts)");
            r.AppendLine($"    register {fasteners.Count * 2} registry key(s)");
            r.AppendLine();
            r.AppendLine("This restructures the model hierarchy. Undo reverts it in one step; " +
                         "git checkout of the scene is the authoritative revert.");

            Debug.Log(r.ToString());
        }

        // =====================================================================
        // 2. APPLY
        // =====================================================================

        [MenuItem("AdaptiveAR/Fasteners/2 - Extract Fasteners Into Parts", false, 11)]
        public static void Apply()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[Fasteners] Wrong scene '{scene.name}'. Aborted.");
                return;
            }

            GameObject oilPan = Find(scene, OilPanPath);
            GameObject ghosties = Find(scene, GhostiesPath);
            GameObject components = Find(scene, ComponentsPath);
            GuidanceRegistry registry = FindComponent<GuidanceRegistry>(scene);

            if (oilPan == null || ghosties == null || components == null || registry == null)
            {
                Debug.LogError("[Fasteners] Required objects missing. Run the dry run first. Aborted.");
                return;
            }

            List<Fastener> fasteners = Collect(oilPan.transform, out _);
            if (fasteners.Count == 0)
            {
                Debug.Log("[Fasteners] Nothing left to extract - already done.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Fasteners: Extract Into Parts");

            Transform ghostGroup = EnsureChild(ghosties.transform, GhostGroupName);
            Transform partGroup = EnsureChild(components.transform, PartGroupName);

            Vector3 stageOrigin = ComputeStageOrigin(components.transform);

            var entries = new List<(string key, GameObject go, bool isPart)>();
            int i = 0;

            foreach (Fastener f in fasteners)
            {
                // --- the original becomes the ghost target, at its assembled pose ---
                Undo.SetTransformParent(f.source, ghostGroup, "Extract fastener target");
                f.source.name = f.cleanName;
                f.source.gameObject.SetActive(false);   // shown only by its own action

                entries.Add((GhostKeyPrefix + f.cleanName, f.source.gameObject, false));

                // --- a duplicate becomes the movable part, staged near the tray ---
                GameObject part = Object.Instantiate(f.source.gameObject, partGroup);
                part.name = f.cleanName;
                part.SetActive(true);
                Undo.RegisterCreatedObjectUndo(part, "Create fastener part");

                // Restore the real materials on the part: the target may be ghost-tinted,
                // but the thing the participant picks up must look like a real component.
                CopyMaterials(f.source.gameObject, part);

                int col = i % StageColumns;
                int row = i / StageColumns;
                Vector3 world = stageOrigin
                                + components.transform.right * (col * StageSpacingMetres)
                                + components.transform.forward * (row * StageSpacingMetres);
                part.transform.position = world;

                entries.Add((PartKeyPrefix + f.cleanName, part, true));
                i++;
            }

            int registered = RegisterKeys(registry, entries);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                $"[Fasteners] Extracted {fasteners.Count} fastener(s).\n" +
                $"  Ghost targets  : Ghosties/{GhostGroupName}   (inactive until their action runs)\n" +
                $"  Movable parts  : Components/{PartGroupName}  (staged in a {StageColumns}-wide grid near the tray)\n" +
                $"  Registry keys  : {registered}\n" +
                "  oilPan_lambert2_0 and oilPanCap_lambert2_0 were left alone, so the oil pan\n" +
                "  keeps its original materials.\n" +
                "  NEXT: run 'AdaptiveAR > Fasteners > 3 - Author Fastening Substeps', then\n" +
                "  'Interaction > 2 - Make All Parts Grabbable' if you want them auto-configured.\n" +
                "  SAVE THE SCENE.");
        }


        // =====================================================================
        // 3. AUTHOR THE FASTENING SUBSTEPS
        //
        // Session-budget decision, stated rather than hidden: there are 4 caps and 15
        // bolts. Authoring every bolt as its own validated placement would add ~19
        // actions to the crankshaft stage alone and push a run far past the 15 minute
        // target. So the CAPS are enabled and the individual BOLTS are authored but
        // disabled, with the reason recorded in the data. Turn them on for a
        // full-procedure run by flipping `enabled` on those actions.
        // =====================================================================

        [MenuItem("AdaptiveAR/Fasteners/3 - Author Fastening Substeps", false, 12)]
        public static void AuthorSubsteps()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject ghostGroup = Find(scene, GhostiesPath + "/" + GhostGroupName);

            if (ghostGroup == null)
            {
                Debug.LogError("[Fasteners] Ghosties/Fasteners not found. Run step 2 first. Aborted.");
                return;
            }

            StepData crankStage = AssetDatabase.LoadAssetAtPath<StepData>(
                "Assets/ScriptableObjects/Steps/Step_01_Crankshaft.asset");

            if (crankStage == null)
            {
                Debug.LogError("[Fasteners] Step_01_Crankshaft.asset not found. " +
                               "Run 'Assembly > 2 - Apply Full Assembly Content' first. Aborted.");
                return;
            }

            Undo.RecordObject(crankStage, "Author fastening substeps");

            // Rebuild: keep locate and place, drop the old disabled placeholders, then add
            // the real fastener actions.
            var rebuilt = new List<AssemblyAction>();
            foreach (AssemblyAction a in crankStage.actions)
            {
                if (a == null) continue;
                if (a.kind == ActionKind.Fasten || a.kind == ActionKind.ToolAction) continue;
                rebuilt.Add(a);
            }

            int caps = 0, bolts = 0;

            for (int i = 0; i < ghostGroup.transform.childCount; i++)
            {
                string name = ghostGroup.transform.GetChild(i).name;
                bool isBolt = name.StartsWith("crankHolderBolt");
                bool isCap = !isBolt && name.StartsWith("crankHolder");
                if (!isCap && !isBolt) continue;

                string label = isCap
                    ? "bearing cap " + Tail(name)
                    : "bolt " + Tail(name);

                rebuilt.Add(new AssemblyAction
                {
                    actionId = "step_01_crankshaft." + name,
                    kind = ActionKind.Fasten,
                    instruction = isCap
                        ? "Fit " + label + " over the crankshaft journal."
                        : "Fit " + label + ".",
                    detail = isCap
                        ? "Take it from the tray and lower it onto the highlighted target."
                        : "",
                    partKey = PartKeyPrefix + name,
                    targetKey = GhostKeyPrefix + name,
                    ghostKeys = new[] { GhostKeyPrefix + name },
                    positionToleranceMeters = isCap ? 0.03f : 0.02f,
                    rotationToleranceDegrees = isCap ? 20f : 30f,
                    settleSeconds = 0.4f,
                    showArrow = true,

                    // Caps run; individual bolts are off by default for session length.
                    enabled = isCap,
                    disabledReason = isCap
                        ? ""
                        : "Authored but off by default: 15 individual bolts would add roughly " +
                          "19 validated placements to this stage and push a run past the 15 " +
                          "minute session target. Enable for a full-procedure run."
                });

                if (isCap) caps++; else bolts++;
            }

            // One tool action per run, still blocked on a real tool and a detection rule.
            rebuilt.Add(new AssemblyAction
            {
                actionId = "step_01_crankshaft.tighten",
                kind = ActionKind.ToolAction,
                instruction = "Tighten the bearing cap bolts with the tool.",
                enabled = false,
                disabledReason = "No tool model in the repository, and the detection rule " +
                                 "(proximity, dwell, or a button) needs bench testing before it " +
                                 "is fixed. ToolInteraction is wired and ready for a prefab."
            });

            crankStage.actions = rebuilt;
            EditorUtility.SetDirty(crankStage);
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[Fasteners] Authored fastening substeps on the crankshaft stage.\n" +
                $"  Bearing caps  : {caps} enabled, validated placements\n" +
                $"  Bolts         : {bolts} authored but DISABLED (session length - see the reason in the asset)\n" +
                $"  Tool action   : 1 authored, disabled pending a tool model\n" +
                $"  Stage actions : {rebuilt.Count} total\n" +
                "  The camshaft stage gets no caps: this model has crank bearing caps only,\n" +
                "  positioned at the crank journals. There are no camshaft-specific holders.\n" +
                "  SAVE THE SCENE.");
        }

        private static string Tail(string name)
        {
            for (int i = name.Length - 1; i >= 0; i--)
                if (!char.IsDigit(name[i]))
                    return name.Substring(i + 1).TrimStart('0');
            return name;
        }

        // =====================================================================
        // Collection and mapping
        // =====================================================================

        /// <summary>
        /// Finds the baked fasteners and assigns each bolt to the nearest cap along the
        /// crankshaft axis. The mapping comes from the model, not from an assumption about
        /// which bolt belongs where.
        /// </summary>
        private static List<Fastener> Collect(Transform oilPan, out List<string> kept)
        {
            var caps = new List<Fastener>();
            var bolts = new List<Fastener>();
            kept = new List<string>();

            for (int i = 0; i < oilPan.childCount; i++)
            {
                Transform c = oilPan.GetChild(i);
                string clean = Clean(c.name);

                if (c.name.StartsWith("crankHolderBolt"))
                    bolts.Add(new Fastener { source = c, cleanName = clean, isCap = false });
                else if (c.name.StartsWith("crankHolder"))
                    caps.Add(new Fastener { source = c, cleanName = clean, isCap = true });
                else
                    kept.Add(c.name);
            }

            // Nearest cap along the local Z axis, which runs down the crankshaft.
            foreach (Fastener b in bolts)
            {
                float bz = b.source.localPosition.z;
                Fastener best = null;
                float bestD = float.MaxValue;

                foreach (Fastener c in caps)
                {
                    float d = Mathf.Abs(c.source.localPosition.z - bz);
                    if (d < bestD) { bestD = d; best = c; }
                }

                b.capName = best != null ? best.cleanName : "";
            }

            // Caps first, then their bolts: that is the physical order.
            var ordered = new List<Fastener>();
            caps.Sort((a, b) => b.source.localPosition.z.CompareTo(a.source.localPosition.z));

            foreach (Fastener c in caps)
            {
                ordered.Add(c);
                foreach (Fastener b in bolts)
                    if (b.capName == c.cleanName) ordered.Add(b);
            }

            return ordered;
        }

        private static string Clean(string name)
        {
            int i = name.IndexOf("_lambert", System.StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? name.Substring(0, i) : name;
        }

        /// <summary>Staging spot for the movable duplicates: beside the second tray.</summary>
        private static Vector3 ComputeStageOrigin(Transform components)
        {
            Transform offset = components.parent;
            Transform tray = offset != null ? offset.Find("tray (1)") : null;
            if (tray == null && offset != null) tray = offset.Find("tray");

            if (tray != null)
                return tray.position + Vector3.up * 0.04f;

            return components.position + Vector3.up * 0.1f;
        }

        private static void CopyMaterials(GameObject from, GameObject to)
        {
            Renderer[] a = from.GetComponentsInChildren<Renderer>(true);
            Renderer[] b = to.GetComponentsInChildren<Renderer>(true);

            for (int i = 0; i < a.Length && i < b.Length; i++)
                b[i].sharedMaterials = a[i].sharedMaterials;
        }

        private static int RegisterKeys(GuidanceRegistry registry, List<(string key, GameObject go, bool isPart)> entries)
        {
            var so = new SerializedObject(registry);
            SerializedProperty list = so.FindProperty("entries");
            if (list == null) return 0;

            int n = 0;
            foreach (var e in entries)
            {
                int idx = IndexOfKey(list, e.key);
                if (idx < 0)
                {
                    list.InsertArrayElementAtIndex(list.arraySize);
                    idx = list.arraySize - 1;
                }

                SerializedProperty el = list.GetArrayElementAtIndex(idx);
                el.FindPropertyRelative("key").stringValue = e.key;
                el.FindPropertyRelative("target").objectReferenceValue = e.go;

                // Parts are looked up for validation but must never be hidden by the presenter.
                SerializedProperty ex = el.FindPropertyRelative("excludeFromAutoHide");
                if (ex != null) ex.boolValue = e.isPart;

                n++;
            }

            so.ApplyModifiedProperties();
            return n;
        }

        private static int IndexOfKey(SerializedProperty list, string key)
        {
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue == key)
                    return i;
            return -1;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

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
