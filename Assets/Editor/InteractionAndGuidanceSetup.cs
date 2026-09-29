// File: InteractionAndGuidanceSetup.cs
// Makes every assembly part grabbable, tints the ghosts, and wires the arrow.
//
// Three problems this fixes, all found on device:
//   1. The pistons could not be picked up. They carry a Grabbable but no Rigidbody
//      and almost no colliders - their meshes live on eight child objects - so the
//      Interaction SDK had nothing to grab.
//   2. Only the part expected by the current step was interactive. Every part
//      should be pickable at any time; the step decides what counts, not what can
//      be touched.
//   3. The ghosts rendered in the parts' own material, so a target pose was hard to
//      tell from a real part. They are now translucent green.
//
// Grab setup is cloned from the crankshaft, which already works, rather than
// assembled from scratch: the ISDK component graph has references that are easy to
// get subtly wrong by hand.

using System.Collections.Generic;
using System.IO;
using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using AdaptiveAR.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveAR.EditorTools
{
    public static class InteractionAndGuidanceSetup
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";
        private const string GeneratedFolder = "Assets/UI/Generated";
        private const string GhostMaterialPath = GeneratedFolder + "/GhostGreen.mat";
        private const string ArrowPrefabPath = "Assets/Prefabs/arrow.prefab";

        private const string ComponentsPath = "EngineAnchor/Offset/Components";
        private const string GhostiesPath = "EngineAnchor/Offset/Ghosties";
        private const string IsdkChildName = "ISDK_HandGrabInteraction";

        // =====================================================================
        // 1. GHOSTS
        // =====================================================================

        [MenuItem("AdaptiveAR/Interaction/1 - Tint Ghosts Translucent Green", false, 10)]
        public static void TintGhosts()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject ghosties = Find(scene, GhostiesPath);
            if (ghosties == null)
            {
                Debug.LogError($"[Interaction] '{GhostiesPath}' not found. Aborted.");
                return;
            }

            Material mat = EnsureGhostMaterial();
            if (mat == null) return;

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Interaction: Tint Ghosts");

            int tinted = 0;
            foreach (Renderer r in ghosties.GetComponentsInChildren<Renderer>(true))
            {
                Undo.RecordObject(r, "Tint ghost");

                var mats = new Material[r.sharedMaterials.Length == 0 ? 1 : r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;

                EditorUtility.SetDirty(r);
                tinted++;
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log($"[Interaction] Tinted {tinted} ghost renderer(s) translucent green.\n" +
                      $"  Material: {GhostMaterialPath}\n" +
                      "  Adjust its colour and alpha there; every ghost shares it.");
        }

        /// <summary>Creates the translucent green ghost material if it does not exist yet.</summary>
        private static Material EnsureGhostMaterial()
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(GhostMaterialPath);
            if (existing != null) return existing;

            Directory.CreateDirectory(GeneratedFolder);

            // URP project, so prefer a URP shader and fall back if the name ever changes.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Standard");

            if (shader == null)
            {
                Debug.LogError("[Interaction] No usable shader found for the ghost material.");
                return null;
            }

            var mat = new Material(shader) { name = "GhostGreen" };
            Color green = new Color(0.36f, 0.86f, 0.55f, 0.32f);

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", green);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", green);

            // URP transparent setup. These keywords are what actually switch the blend mode.
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);   // 1 = Transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);       // 0 = Alpha
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);

            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            AssetDatabase.CreateAsset(mat, GhostMaterialPath);
            AssetDatabase.SaveAssets();
            return mat;
        }

        // =====================================================================
        // 2. GRABBABLE PARTS
        // =====================================================================

        [MenuItem("AdaptiveAR/Interaction/2 - Make All Parts Grabbable", false, 11)]
        public static void MakePartsGrabbable()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[Interaction] Wrong scene '{scene.name}'. Aborted.");
                return;
            }

            GameObject components = Find(scene, ComponentsPath);
            if (components == null)
            {
                Debug.LogError($"[Interaction] '{ComponentsPath}' not found. Aborted.");
                return;
            }

            // The crankshaft already grabs correctly; its ISDK child is the template.
            Transform template = null;
            Transform crank = components.transform.Find("crankshaft");
            if (crank != null) template = crank.Find(IsdkChildName);

            if (template == null)
            {
                Debug.LogError("[Interaction] Could not find the crankshaft's ISDK_HandGrabInteraction " +
                               "to use as a template. Aborted.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Interaction: Make Parts Grabbable");

            var report = new List<string>();

            for (int i = 0; i < components.transform.childCount; i++)
            {
                Transform part = components.transform.GetChild(i);

                // Skip the group-level interaction block itself.
                if (part.name == IsdkChildName) continue;
                if (part.GetComponentInChildren<Renderer>(true) == null) continue;

                string note = EquipPart(part, template);
                if (!string.IsNullOrEmpty(note)) report.Add($"    {part.name,-22} {note}");
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log("[Interaction] Grab setup pass complete.\n" +
                      string.Join("\n", report) +
                      "\n  Every part is now pickable at any time; the step decides what counts as " +
                      "correct, not what can be touched.\n  SAVE THE SCENE.");
        }

        /// <summary>Gives one part the Rigidbody, colliders and ISDK block it needs to be grabbed.</summary>
        private static string EquipPart(Transform part, Transform template)
        {
            var notes = new List<string>();

            // --- Rigidbody: required for the Interaction SDK to move the part ---
            var body = part.GetComponent<Rigidbody>();
            if (body == null)
            {
                body = Undo.AddComponent<Rigidbody>(part.gameObject);
                body.useGravity = true;
                body.isKinematic = true;   // DropIntoTray decides when physics starts
                notes.Add("+Rigidbody");
            }

            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            // --- Colliders: a hand needs something solid to hit ---
            int added = EnsureColliders(part);
            if (added > 0) notes.Add($"+{added} collider(s)");

            // --- DropIntoTray so spawn and recovery behave like the other parts ---
            if (part.GetComponent<DropIntoTray>() == null)
            {
                Undo.AddComponent<DropIntoTray>(part.gameObject);
                notes.Add("+DropIntoTray");
            }

            // --- ISDK grab block, cloned from the working crankshaft ---
            if (part.Find(IsdkChildName) == null)
            {
                GameObject clone = Object.Instantiate(template.gameObject, part);
                clone.name = IsdkChildName;
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = Vector3.one;
                Undo.RegisterCreatedObjectUndo(clone, "Clone ISDK grab block");

                RetargetReferences(clone, template.root, part);
                notes.Add("+ISDK grab");
            }

            return notes.Count == 0 ? "already set up" : string.Join(", ", notes);
        }

        /// <summary>
        /// Adds convex mesh colliders where a part has none. Pistons keep their meshes on
        /// child objects, so the colliders go there too.
        /// </summary>
        private static int EnsureColliders(Transform part)
        {
            if (part.GetComponentsInChildren<Collider>(true).Length > 0)
                return 0;

            int added = 0;
            foreach (MeshFilter mf in part.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (mf.GetComponent<Collider>() != null) continue;

                var mc = Undo.AddComponent<MeshCollider>(mf.gameObject);
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = true;          // required for a moving Rigidbody
                added++;
            }

            return added;
        }

        /// <summary>
        /// Repoints every object reference inside the cloned block from the template's part
        /// onto the new one. Instantiate copies references verbatim, so without this the
        /// clone would still drive the crankshaft.
        /// </summary>
        private static void RetargetReferences(GameObject clone, Transform templateRoot, Transform newPart)
        {
            Transform templatePart = templateRoot;   // fallback

            // The template's owning part is its ISDK block's parent.
            foreach (Component c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;

                var so = new SerializedObject(c);
                SerializedProperty it = so.GetIterator();
                bool changed = false;

                while (it.NextVisible(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;

                    Object val = it.objectReferenceValue;
                    if (val == null) continue;

                    // Anything still pointing at the template's part gets moved onto this one.
                    if (val is Rigidbody)
                    {
                        Rigidbody rb = newPart.GetComponent<Rigidbody>();
                        if (rb != null && (Object)rb != val) { it.objectReferenceValue = rb; changed = true; }
                    }
                    else if (val is Transform t && !t.IsChildOf(clone.transform))
                    {
                        it.objectReferenceValue = newPart;
                        changed = true;
                    }
                    else if (val is GameObject g && !g.transform.IsChildOf(clone.transform))
                    {
                        it.objectReferenceValue = newPart.gameObject;
                        changed = true;
                    }
                }

                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // =====================================================================
        // 3. ARROW
        // =====================================================================

        [MenuItem("AdaptiveAR/Interaction/3 - Wire Guidance Arrow", false, 12)]
        public static void WireArrow()
        {
            Scene scene = SceneManager.GetActiveScene();
            var validator = FindComponent<StepValidator>(scene);
            var level = FindComponent<SupportLevelController>(scene);

            if (validator == null)
            {
                Debug.LogError("[Interaction] StepValidator not found. Run the Assembly setup first. Aborted.");
                return;
            }

            GameObject host = validator.gameObject;
            var arrow = host.GetComponent<GuidanceArrow>();
            if (arrow == null) arrow = Undo.AddComponent<GuidanceArrow>(host);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArrowPrefabPath);
            if (prefab == null)
                Debug.LogWarning($"[Interaction] Arrow prefab not found at {ArrowPrefabPath}; " +
                                 "assign one on the GuidanceArrow component.");

            var so = new SerializedObject(arrow);
            SetRef(so, "validator", validator);
            SetRef(so, "supportLevel", level);
            SetRef(so, "arrowPrefab", prefab);
            so.ApplyModifiedProperties();

            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log("[Interaction] Guidance arrow wired.\n" +
                      "  Hovers over the PART until it is picked up, then over the TARGET.\n" +
                      "  Cyan while pointing at the part, green once it is in hand.\n" +
                      "  Appears from L2 upward - it is assistance, so L1 stays minimal.\n" +
                      "  SAVE THE SCENE.");
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void SetRef(SerializedObject so, string field, Object value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null)
            {
                Debug.LogWarning($"[Interaction] Field '{field}' not found on {so.targetObject.GetType().Name}.");
                return;
            }
            p.objectReferenceValue = value;
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
