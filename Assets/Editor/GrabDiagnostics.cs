// File: GrabDiagnostics.cs
// Diagnoses and repairs why a component cannot be grabbed on Quest.
//
// The device failure and its actual cause
// ---------------------------------------
// Only the crankshaft was grabbable. The ISDK references were all correct, so the
// earlier "clone the crankshaft setup" theory was wrong. The real difference is
// structural:
//
//   crankshaft   1 Rigidbody, 1 collider, both on the part root        -> works
//   camshaft     1 Rigidbody, 1 collider, both on the part root        -> works
//   piston001-4  9 Rigidbodies (root + all 8 child meshes), 9 colliders -> fails
//
// In Unity a collider belongs to its NEAREST Rigidbody ancestor. With a Rigidbody
// on every child mesh, each collider belongs to that child's body, and the part
// root's Rigidbody - the one Grabbable and the interactables reference - owns zero
// colliders. There is nothing for a hand or ray to hit that resolves to the
// grabbable body, so the part is unselectable however correct its references are.
//
// The repair is therefore structural, not a constant tweak: exactly one Rigidbody
// at the level ISDK expects, with every mesh collider beneath it belonging to it.

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveAR.EditorTools
{
    public static class GrabDiagnostics
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";
        private const string ComponentsPath = "EngineAnchor/Offset/Components";
        private const string IsdkChildName = "ISDK_HandGrabInteraction";

        private class Report
        {
            public string name;
            public int rigidbodiesInSubtree;
            public int collidersInSubtree;
            public int collidersOwnedByRoot;
            public int duplicateColliders;
            public int nonConvex;
            public bool hasRootRigidbody;
            public bool hasIsdk;
            public bool hasGrabbable;

            public bool Grabbable
            {
                get
                {
                    return hasRootRigidbody && hasIsdk && hasGrabbable
                        && rigidbodiesInSubtree == 1 && collidersOwnedByRoot > 0;
                }
            }

            public string Verdict
            {
                get
                {
                    if (Grabbable) return "OK";
                    if (!hasRootRigidbody) return "no Rigidbody on the part root";
                    if (rigidbodiesInSubtree > 1)
                        return $"{rigidbodiesInSubtree - 1} nested Rigidbody(s) steal every collider from the root";
                    if (collidersOwnedByRoot == 0) return "root Rigidbody owns no colliders";
                    if (!hasIsdk) return "no ISDK_HandGrabInteraction child";
                    if (!hasGrabbable) return "no Grabbable on the part root";
                    return "unknown";
                }
            }
        }

        // =====================================================================
        // 1. DIAGNOSE
        // =====================================================================

        [MenuItem("AdaptiveAR/Grab/1 - Diagnose Grabbability (dry run)", false, 10)]
        public static void Diagnose()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject components = Find(scene, ComponentsPath);
            if (components == null)
            {
                Debug.LogError($"[Grab] '{ComponentsPath}' not found. Aborted.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== Grabbability diagnosis (DRY RUN - nothing modified) ===");
            sb.AppendLine();
            sb.AppendLine("A collider belongs to its NEAREST Rigidbody ancestor. More than one");
            sb.AppendLine("Rigidbody in a part means the root body owns no colliders and cannot be grabbed.");
            sb.AppendLine();
            sb.AppendLine($"{"PART",-22} {"RB",3} {"COL",4} {"ROOT-COL",9}  VERDICT");

            foreach (Report r in Survey(components.transform))
                sb.AppendLine($"{r.name,-22} {r.rigidbodiesInSubtree,3} {r.collidersInSubtree,4} " +
                              $"{r.collidersOwnedByRoot,9}  {r.Verdict}");

            sb.AppendLine();
            sb.AppendLine("Repair will, per part: keep ONE Rigidbody on the part root, remove nested");
            sb.AppendLine("Rigidbodies, make every mesh collider convex, and drop duplicate colliders.");
            sb.AppendLine("It does NOT touch the ISDK components or their references - those are correct.");

            Debug.Log(sb.ToString());
        }

        // =====================================================================
        // 2. REPAIR
        // =====================================================================

        [MenuItem("AdaptiveAR/Grab/2 - Repair Grabbability", false, 11)]
        public static void Repair()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[Grab] Wrong scene '{scene.name}'. Aborted.");
                return;
            }

            GameObject components = Find(scene, ComponentsPath);
            if (components == null)
            {
                Debug.LogError($"[Grab] '{ComponentsPath}' not found. Aborted.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Grab: Repair Grabbability");

            var log = new StringBuilder();
            int fixedParts = 0, removedRb = 0, madeConvex = 0, removedDup = 0;

            for (int i = 0; i < components.transform.childCount; i++)
            {
                Transform part = components.transform.GetChild(i);
                if (part.name == IsdkChildName) continue;
                if (part.GetComponentInChildren<Renderer>(true) == null) continue;

                int rb = 0, cx = 0, dup = 0;
                RepairPart(part, ref rb, ref cx, ref dup);

                if (rb + cx + dup > 0)
                {
                    log.AppendLine($"    {part.name,-22} removed {rb} nested RB, {cx} made convex, {dup} duplicate collider(s) removed");
                    fixedParts++;
                }

                removedRb += rb; madeConvex += cx; removedDup += dup;
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[Grab] Repair complete.\n" +
                $"  Parts changed          : {fixedParts}\n" +
                $"  Nested Rigidbodies gone: {removedRb}   <- this is what made the pistons ungrabbable\n" +
                $"  Colliders made convex  : {madeConvex}\n" +
                $"  Duplicate colliders    : {removedDup}\n" +
                (log.Length > 0 ? log.ToString() : "") +
                "  Re-run '1 - Diagnose' to confirm every part reports OK.\n" +
                "  STILL [QV]: only a Quest run proves a hand can actually select them.\n" +
                "  SAVE THE SCENE.");
        }

        /// <summary>
        /// Makes one part match the crankshaft's working shape: a single Rigidbody at the
        /// root, owning every collider beneath it.
        /// </summary>
        private static void RepairPart(Transform part, ref int removedRb, ref int madeConvex, ref int removedDup)
        {
            // --- one Rigidbody, on the part root ---
            var root = part.GetComponent<Rigidbody>();
            if (root == null)
            {
                root = Undo.AddComponent<Rigidbody>(part.gameObject);
                root.useGravity = true;
                root.isKinematic = true;
            }

            root.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            root.interpolation = RigidbodyInterpolation.Interpolate;

            // --- remove every Rigidbody below the root so its colliders revert to the root ---
            foreach (Rigidbody rb in part.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null || rb == root) continue;
                Undo.DestroyObjectImmediate(rb);
                removedRb++;
            }

            // --- colliders: convex, and no duplicates on one object ---
            foreach (MeshFilter mf in part.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;

                MeshCollider[] existing = mf.GetComponents<MeshCollider>();

                for (int i = existing.Length - 1; i >= 1; i--)
                {
                    Undo.DestroyObjectImmediate(existing[i]);
                    removedDup++;
                }

                MeshCollider mc = mf.GetComponent<MeshCollider>();
                if (mc == null)
                {
                    mc = Undo.AddComponent<MeshCollider>(mf.gameObject);
                    mc.sharedMesh = mf.sharedMesh;
                }

                // A non-kinematic Rigidbody cannot own a concave mesh collider.
                if (!mc.convex)
                {
                    Undo.RecordObject(mc, "Make collider convex");
                    mc.convex = true;
                    madeConvex++;
                }
            }
        }

        // =====================================================================

        private static List<Report> Survey(Transform components)
        {
            var list = new List<Report>();

            for (int i = 0; i < components.childCount; i++)
            {
                Transform part = components.GetChild(i);
                if (part.name == IsdkChildName) continue;
                if (part.GetComponentInChildren<Renderer>(true) == null) continue;

                var r = new Report { name = part.name };

                r.rigidbodiesInSubtree = part.GetComponentsInChildren<Rigidbody>(true).Length;
                r.collidersInSubtree = part.GetComponentsInChildren<Collider>(true).Length;
                r.hasRootRigidbody = part.GetComponent<Rigidbody>() != null;
                r.hasIsdk = part.Find(IsdkChildName) != null;

                foreach (MonoBehaviour mb in part.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    string ns = mb.GetType().Namespace;
                    if (!string.IsNullOrEmpty(ns) && ns.StartsWith("Oculus.Interaction"))
                    {
                        r.hasGrabbable = true;
                        break;
                    }
                }

                // Colliders whose nearest Rigidbody ancestor is the part root.
                foreach (Collider c in part.GetComponentsInChildren<Collider>(true))
                {
                    Rigidbody owner = c.attachedRigidbody;
                    if (owner != null && owner.transform == part) r.collidersOwnedByRoot++;
                }

                list.Add(r);
            }

            return list;
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
