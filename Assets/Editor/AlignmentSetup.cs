// File: AlignmentSetup.cs
// Editor-only tool for the physical alignment fix.
//
// Same guarantees as Phase1SceneSetup: never writes scene YAML directly, dry run
// modifies nothing, apply is idempotent and Undo-able as one collapsed operation.
//
// What the fix does
// -----------------
// OpenCVARUtils.SetTransformFromMatrix writes localScale as well as position and
// rotation. For a rigid marker pose that scale is always (1,1,1), which destroyed
// EngineAnchor's authored 0.1 and sank the assembly ~10x deeper than intended -
// 12 to 18 cm below the marker plane, i.e. inside the table.
//
// Rather than patch the working tracking code, the ArUco target is moved off the
// model root onto a dedicated holder:
//
//   before:  EngineAnchor (ArUco target, scale clobbered 0.1 -> 1)
//   after:   MarkerAnchor (ArUco target, scale clobbered 1 -> 1, harmless)
//              CalibrationOffset (AnchorCalibration - tunable nudge)
//                EngineAnchor (authored scale preserved; holds the model)
//
// ArUcoMarkerTracking.cs and ArUcoTrackingAppCoordinator.cs are not modified. Only
// the coordinator's marker->GameObject mapping is repointed.

using System.Collections.Generic;
using System.Text;
using AdaptiveAR.MR;
using TryAR.MarkerTracking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveAR.EditorTools
{
    public static class AlignmentSetup
    {
        private const string ExpectedSceneName = "1 - ArUcoMarkerTracking";

        private const string NameEngineAnchor = "EngineAnchor";
        private const string NameMarkerAnchor = "MarkerAnchor";
        private const string NameCalibrationOffset = "CalibrationOffset";
        private const string NameTabletop = "TabletopSupport";
        private const string PathOffset = "EngineAnchor/Offset";

        // Original authored local transform of EngineAnchor, from the committed scene.
        // Used only by the revert operation.
        private static readonly Vector3 OriginalAnchorLocalPos = new Vector3(0.246f, 0f, 0.346f);
        private static readonly Vector3 OriginalAnchorEuler = new Vector3(-90f, 90f, 0f);
        private static readonly Vector3 OriginalAnchorLocalScale = new Vector3(0.1f, 0.1f, 0.1f);

        // Tabletop pad, in metres. The marker lies on the table, so the marker plane IS the
        // table surface. The pad is parented to MarkerAnchor - NOT to CalibrationOffset or
        // Offset - so it stays on the real table plane and is not carried upward by the
        // calibration lift. MarkerAnchor's scale is always 1, so these are true metres.
        private const float TabletopSizeMetres = 2.0f;
        private const float TabletopThicknessMetres = 0.1f;

        // Life-size assembly scale. The model's authored 0.1 produced a crankshaft smaller
        // than a finger; the scale ArUco was accidentally forcing (1.0) was roughly life size.
        // 1.0 is therefore the starting point - verify it with '5 - Measure Assembly' against a
        // tape measure and adjust on device if needed.
        private const float LifeSizeScale = 1.0f;

        // =====================================================================
        // 1. VALIDATE (dry run) - reads only
        // =====================================================================

        [MenuItem("AdaptiveAR/Alignment/1 - Validate (dry run)", false, 10)]
        public static void Validate()
        {
            var r = new StringBuilder();
            r.AppendLine("=== Alignment Validation (DRY RUN - nothing modified) ===");

            Scene scene = SceneManager.GetActiveScene();
            r.AppendLine($"Active scene: '{scene.name}'" +
                         (scene.name != ExpectedSceneName ? $"   WARNING: expected '{ExpectedSceneName}'" : ""));

            GameObject anchor = FindRoot(scene, NameEngineAnchor);
            GameObject markerAnchor = FindRoot(scene, NameMarkerAnchor);
            GameObject offset = FindByPath(scene, PathOffset);
            ArUcoTrackingAppCoordinator coord = FindCoordinator(scene);

            r.AppendLine();
            r.AppendLine($"EngineAnchor        : {(anchor == null ? "MISSING" : "found")}");
            if (anchor != null)
            {
                r.AppendLine($"    parent          : {(anchor.transform.parent == null ? "<root>" : anchor.transform.parent.name)}");
                r.AppendLine($"    localScale      : {anchor.transform.localScale}");
                r.AppendLine($"    localPosition   : {anchor.transform.localPosition}");
            }
            r.AppendLine($"MarkerAnchor        : {(markerAnchor == null ? "does not exist yet (would be created)" : "already exists")}");
            r.AppendLine($"EngineAnchor/Offset : {(offset == null ? "MISSING" : "found")}");
            r.AppendLine($"Coordinator         : {(coord == null ? "MISSING" : coord.gameObject.name)}");

            if (coord != null)
            {
                var so = new SerializedObject(coord);
                SerializedProperty pairs = so.FindProperty("m_markerGameObjectPairs");
                if (pairs != null)
                {
                    r.AppendLine($"    marker pairs    : {pairs.arraySize}");
                    for (int i = 0; i < pairs.arraySize; i++)
                    {
                        SerializedProperty e = pairs.GetArrayElementAtIndex(i);
                        int id = e.FindPropertyRelative("markerId").intValue;
                        Object tgt = e.FindPropertyRelative("gameObject").objectReferenceValue;
                        r.AppendLine($"      markerId {id} -> {(tgt == null ? "<none>" : tgt.name)}" +
                                     (tgt == anchor ? "   <- would be repointed to MarkerAnchor" : ""));
                    }
                }
            }

            // Scale-destruction impact, computed from the live values.
            if (anchor != null && offset != null)
            {
                float s = anchor.transform.localScale.y;
                r.AppendLine();
                r.AppendLine("Depth below the marker plane (Offset-local -Y maps to -marker normal):");
                foreach (string childName in new[] { "tray", "tray (1)", "Components", "Plane" })
                {
                    Transform c = offset.transform.Find(childName);
                    if (c == null) continue;
                    float yLocal = c.localPosition.y;
                    r.AppendLine($"    {childName,-12} localY {yLocal,9:F5}" +
                                 $"   at scale {s:F2} = {yLocal * s * 100f,7:F2} cm" +
                                 $"   at scale 1.00 = {yLocal * 100f,7:F2} cm");
                }
                r.AppendLine("  (ArUco forces scale to 1.00 at runtime - that is the bug being fixed.)");
            }

            int dropParts = CountDropIntoTray(scene);
            r.AppendLine();
            r.AppendLine($"DropIntoTray parts in scene : {dropParts}");
            r.AppendLine("  New serialized defaults apply automatically (no drop on enable,");
            r.AppendLine("  ContinuousSpeculative collision, out-of-bounds recovery on).");

            GameObject tabletop = markerAnchor != null ? FindChild(markerAnchor.transform, NameTabletop) : null;
            r.AppendLine($"TabletopSupport     : {(tabletop == null ? "does not exist yet (created by step 3, after step 2)" : "already exists")}");

            bool ready = anchor != null && offset != null && coord != null;
            r.AppendLine();
            r.AppendLine(ready
                ? "RESULT: ready. Run '2 - Apply Anchor Scale + Calibration Fix', then '3 - Add Tabletop Support'."
                : "RESULT: required objects missing - do not apply.");

            Debug.Log(r.ToString());
        }

        // =====================================================================
        // 2. APPLY the anchor scale + calibration fix
        // =====================================================================

        [MenuItem("AdaptiveAR/Alignment/2 - Apply Anchor Scale + Calibration Fix", false, 11)]
        public static void ApplyAnchorFix()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.name != ExpectedSceneName)
            {
                Debug.LogError($"[AlignmentSetup] Active scene is '{scene.name}', expected '{ExpectedSceneName}'. Aborted.");
                return;
            }

            GameObject anchor = FindRoot(scene, NameEngineAnchor);
            ArUcoTrackingAppCoordinator coord = FindCoordinator(scene);

            if (anchor == null || coord == null)
            {
                Debug.LogError("[AlignmentSetup] EngineAnchor and/or the ArUco coordinator not found. Run validate first. Aborted.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Alignment: Anchor Scale + Calibration Fix");

            // --- MarkerAnchor (idempotent) ---
            GameObject markerAnchor = FindRoot(scene, NameMarkerAnchor);
            if (markerAnchor == null)
            {
                markerAnchor = new GameObject(NameMarkerAnchor);
                Undo.RegisterCreatedObjectUndo(markerAnchor, "Create MarkerAnchor");

                // Start where EngineAnchor currently sits so the editor view stays familiar.
                markerAnchor.transform.SetPositionAndRotation(anchor.transform.position, anchor.transform.rotation);
                markerAnchor.transform.localScale = Vector3.one;
            }

            // --- CalibrationOffset (idempotent) ---
            GameObject calib = FindChild(markerAnchor.transform, NameCalibrationOffset);
            if (calib == null)
            {
                calib = new GameObject(NameCalibrationOffset);
                Undo.RegisterCreatedObjectUndo(calib, "Create CalibrationOffset");
                Undo.SetTransformParent(calib.transform, markerAnchor.transform, "Parent CalibrationOffset");
                calib.transform.localPosition = Vector3.zero;
                calib.transform.localRotation = Quaternion.identity;
                calib.transform.localScale = Vector3.one;
            }

            AnchorCalibration calibration = calib.GetComponent<AnchorCalibration>();
            if (calibration == null)
                calibration = Undo.AddComponent<AnchorCalibration>(calib);

            // --- Reparent EngineAnchor ---
            Vector3 previousScale = anchor.transform.localScale;
            bool reparented = false;

            if (anchor.transform.parent != calib.transform)
            {
                Undo.SetTransformParent(anchor.transform, calib.transform, "Parent EngineAnchor");
                reparented = true;
            }

            // The marker pose arrives via the parent chain, and ALL scale now lives in
            // AnchorCalibration.assemblyScale, so the model root sits at identity.
            // The authored 0.1 is deliberately NOT carried over: it produced a model about
            // ten times too small. Life size is assemblyScale = 1.
            Undo.RecordObject(anchor.transform, "Reset EngineAnchor local transform");
            anchor.transform.localPosition = Vector3.zero;
            anchor.transform.localRotation = Quaternion.identity;
            anchor.transform.localScale = Vector3.one;

            // --- Repoint the ArUco marker mapping onto MarkerAnchor ---
            int repointed = RepointMarkerPairs(coord, anchor, markerAnchor);

            // --- Life-size scale, then ground the assembly onto the table ---
            Undo.RecordObject(calibration, "Set life-size scale and ground");
            Undo.RecordObject(calib.transform, "Set life-size scale and ground");
            calibration.SetAssemblyScale(LifeSizeScale);
            bool grounded = calibration.GroundToMarkerPlane();

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[AlignmentSetup] Anchor fix applied.\n" +
                $"  MarkerAnchor / CalibrationOffset : {(reparented ? "created and EngineAnchor reparented" : "already in place")}\n" +
                $"  EngineAnchor localScale          : {previousScale} -> {Vector3.one} (all scale moved to AnchorCalibration)\n" +
                $"  assemblyScale                    : {calibration.AssemblyScale:F3}  (1 = life size)\n" +
                $"  ArUco marker pairs repointed     : {repointed}\n" +
                $"  Auto-grounded                    : {(grounded ? $"yes, lift = {calibration.LiftAboveMarker * 100f:F2} cm" : "FAILED - see warning above")}\n" +
                "  ArUcoMarkerTracking / ArUcoTrackingAppCoordinator code was NOT modified.\n" +
                "  Next: '3 - Add Tabletop Support', then '4 - Measure Assembly' to check against a tape measure.\n" +
                "  On device: left thumbstick up/down = lift, left/right = scale (auto-regrounds).\n" +
                "  Save the scene to persist. Ctrl+Z reverts this in one step.");
        }

        private static int RepointMarkerPairs(ArUcoTrackingAppCoordinator coord, GameObject from, GameObject to)
        {
            var so = new SerializedObject(coord);
            SerializedProperty pairs = so.FindProperty("m_markerGameObjectPairs");
            if (pairs == null)
            {
                Debug.LogError("[AlignmentSetup] m_markerGameObjectPairs not found on the coordinator.");
                return 0;
            }

            int changed = 0;
            for (int i = 0; i < pairs.arraySize; i++)
            {
                SerializedProperty target = pairs.GetArrayElementAtIndex(i).FindPropertyRelative("gameObject");
                if (target.objectReferenceValue == from)
                {
                    target.objectReferenceValue = to;
                    changed++;
                }
            }

            if (changed > 0)
                so.ApplyModifiedProperties();

            return changed;
        }

        // =====================================================================
        // 3. TABLETOP SUPPORT COLLIDER
        // =====================================================================

        [MenuItem("AdaptiveAR/Alignment/3 - Add Tabletop Support Collider", false, 12)]
        public static void AddTabletopSupport()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject markerAnchor = FindRoot(scene, NameMarkerAnchor);

            if (markerAnchor == null)
            {
                Debug.LogError("[AlignmentSetup] MarkerAnchor not found. Run '2 - Apply Anchor Scale + " +
                               "Calibration Fix' first - the pad is parented to MarkerAnchor so it stays on " +
                               "the real table plane. Aborted.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Alignment: Tabletop Support Collider");

            GameObject pad = FindChild(markerAnchor.transform, NameTabletop);
            if (pad == null)
            {
                pad = new GameObject(NameTabletop);
                Undo.RegisterCreatedObjectUndo(pad, "Create TabletopSupport");
                Undo.SetTransformParent(pad.transform, markerAnchor.transform, "Parent TabletopSupport");
            }

            Undo.RecordObject(pad.transform, "Configure TabletopSupport");
            pad.transform.localPosition = Vector3.zero;
            // MarkerAnchor's +Z is the marker normal. Rotating +90 deg about X puts the pad's
            // local +Y along that normal, the same correction the model's Offset node uses.
            pad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            pad.transform.localScale = Vector3.one;

            BoxCollider box = pad.GetComponent<BoxCollider>();
            if (box == null)
                box = Undo.AddComponent<BoxCollider>(pad);

            Undo.RecordObject(box, "Configure TabletopSupport collider");
            // MarkerAnchor scale is always 1, so these are metres.
            box.size = new Vector3(TabletopSizeMetres, TabletopThicknessMetres, TabletopSizeMetres);
            // Top face exactly on the marker plane, body below it.
            box.center = new Vector3(0f, -TabletopThicknessMetres * 0.5f, 0f);
            box.isTrigger = false;

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[AlignmentSetup] Tabletop support collider ready.\n" +
                $"  {TabletopSizeMetres} m x {TabletopSizeMetres} m pad, {TabletopThicknessMetres * 100f:F0} cm thick, " +
                "top face exactly on the marker plane.\n" +
                "  Parented to MarkerAnchor, so the calibration lift raises the ASSEMBLY above it\n" +
                "  rather than carrying the table along too.\n" +
                "  Invisible (collider only, no renderer). Represents the real table, so parts that\n" +
                "  miss a tray land on the surface instead of falling forever.");
        }

        // =====================================================================
        // 4. GROUND TO TABLE  /  5. MEASURE
        // =====================================================================

        [MenuItem("AdaptiveAR/Alignment/4 - Ground Assembly To Table", false, 13)]
        public static void GroundToTable()
        {
            AnchorCalibration calib = FindCalibration(SceneManager.GetActiveScene());
            if (calib == null)
            {
                Debug.LogError("[AlignmentSetup] AnchorCalibration not found. Run step 2 first. Aborted.");
                return;
            }

            Undo.RecordObject(calib, "Ground assembly to table");
            Undo.RecordObject(calib.transform, "Ground assembly to table");

            if (calib.GroundToMarkerPlane())
            {
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                Debug.Log($"[AlignmentSetup] Grounded. lift = {calib.LiftAboveMarker * 100f:F2} cm " +
                          $"at scale {calib.AssemblyScale:F3}. Save the scene to persist.");
            }
        }

        [MenuItem("AdaptiveAR/Alignment/5 - Measure Assembly (dry run)", false, 14)]
        public static void MeasureAssembly()
        {
            Scene scene = SceneManager.GetActiveScene();
            AnchorCalibration calib = FindCalibration(scene);
            GameObject anchor = FindRoot(scene, NameEngineAnchor);

            if (anchor == null)
            {
                Debug.LogError("[AlignmentSetup] EngineAnchor not found. Aborted.");
                return;
            }

            var r = new StringBuilder();
            var details = new StringBuilder();

            details.AppendLine(calib != null
                ? $"assemblyScale = {calib.AssemblyScale:F3}   lift = {calib.LiftAboveMarker * 100f:F2} cm"
                : "AnchorCalibration not present (step 2 not run yet).");
            details.AppendLine();
            details.AppendLine("Size of key parts at the CURRENT scale:");

            float crankLongest = -1f;

            foreach (string partName in new[] { "crankshaft", "camshaft", "piston001", "oilPan", "tray" })
            {
                Transform t = FindPart(anchor.transform, partName);

                if (t == null)
                {
                    details.AppendLine($"    {partName,-12} NOT FOUND by name");
                    continue;
                }

                Bounds? b = CombinedBounds(t);
                if (!b.HasValue)
                {
                    details.AppendLine($"    {partName,-12} found at '{PathOf(t, anchor.transform)}' " +
                                       "but it has no mesh to measure");
                    continue;
                }

                Vector3 s = b.Value.size;
                float longest = Mathf.Max(s.x, Mathf.Max(s.y, s.z));

                if (partName == "crankshaft") crankLongest = longest;

                details.AppendLine($"    {partName,-12} {s.x * 100f,7:F1} x {s.y * 100f,7:F1} x {s.z * 100f,7:F1} cm" +
                                   $"   longest {longest * 100f,6:F1} cm      [{PathOf(t, anchor.transform)}]");
            }

            Bounds? whole = CombinedBounds(anchor.transform);
            if (whole.HasValue)
            {
                Vector3 s = whole.Value.size;
                details.AppendLine();
                details.AppendLine($"Whole assembly: {s.x * 100f:F1} x {s.y * 100f:F1} x {s.z * 100f:F1} cm");
            }

            if (calib != null && crankLongest > 0f)
            {
                details.AppendLine();
                details.AppendLine("To resize, pick the real length you want and use:");
                details.AppendLine($"    newScale = {calib.AssemblyScale:F3} * (realLengthInCm / {crankLongest * 100f:F1})");
                details.AppendLine("Examples for the crankshaft:");
                foreach (float targetCm in new[] { 50f, 60f, 70f })
                {
                    details.AppendLine($"    want {targetCm:F0} cm  ->  set assemblyScale = " +
                                       $"{calib.AssemblyScale * (targetCm / (crankLongest * 100f)):F3}");
                }
                details.AppendLine();
                details.AppendLine("Set it in the AnchorCalibration inspector, or nudge on device with the");
                details.AppendLine("left thumbstick. Scale changes re-ground automatically.");
            }

            // Headline first, so the number is visible in the Console list without clicking.
            if (crankLongest > 0f)
                r.AppendLine($"[Measure] crankshaft is {crankLongest * 100f:F1} cm long " +
                             $"(scale {(calib != null ? calib.AssemblyScale : 1f):F3}) - click for the full report");
            else
                r.AppendLine("[Measure] could not measure the crankshaft - click for why");

            r.Append(details);
            Debug.Log(r.ToString());
        }

        /// <summary>
        /// Finds a part by name. Searches Components first (the real graspable parts) so an
        /// identically named inactive ghost copy cannot be measured by mistake.
        /// </summary>
        private static Transform FindPart(Transform anchorRoot, string name)
        {
            Transform offset = anchorRoot.Find("Offset");
            if (offset != null)
            {
                Transform components = offset.Find("Components");
                if (components != null)
                {
                    Transform hit = FindDeep(components, name);
                    if (hit != null) return hit;
                }
            }

            return FindDeep(anchorRoot, name);
        }

        /// <summary>Hierarchy path of a transform relative to an ancestor, for unambiguous reporting.</summary>
        private static string PathOf(Transform t, Transform relativeTo)
        {
            var parts = new List<string>();
            Transform cur = t;

            while (cur != null && cur != relativeTo)
            {
                parts.Insert(0, cur.name);
                cur = cur.parent;
            }

            return string.Join("/", parts);
        }

        /// <summary>
        /// World-space bounds of every mesh under a transform, or null if there are none.
        /// Falls back to the shared mesh when a renderer reports zero bounds, which is what
        /// happens for INACTIVE objects - the original cause of parts being skipped silently.
        /// </summary>
        private static Bounds? CombinedBounds(Transform root)
        {
            Bounds result = default;
            bool any = false;

            foreach (Renderer rend in root.GetComponentsInChildren<Renderer>(true))
            {
                Bounds b = rend.bounds;

                if (b.size == Vector3.zero)
                {
                    MeshFilter mf = rend.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                        continue;

                    b = TransformBounds(rend.transform, mf.sharedMesh.bounds);
                }

                if (b.size == Vector3.zero)
                    continue;

                if (!any) { result = b; any = true; }
                else result.Encapsulate(b);
            }

            return any ? result : (Bounds?)null;
        }

        /// <summary>Converts local mesh bounds into a world-space AABB.</summary>
        private static Bounds TransformBounds(Transform t, Bounds local)
        {
            Bounds b = new Bounds(t.TransformPoint(local.center), Vector3.zero);

            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = local.center + Vector3.Scale(
                    local.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f,
                                (i & 2) == 0 ? -1f : 1f,
                                (i & 4) == 0 ? -1f : 1f));

                b.Encapsulate(t.TransformPoint(corner));
            }

            return b;
        }

        private static AnchorCalibration FindCalibration(Scene scene)
        {
            if (!scene.IsValid()) return null;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var c = root.GetComponentInChildren<AnchorCalibration>(true);
                if (c != null) return c;
            }
            return null;
        }

        // =====================================================================
        // 6. PART PHYSICS
        // =====================================================================

        [MenuItem("AdaptiveAR/Alignment/6 - Enable Part Physics (parts settle into trays)", false, 15)]
        public static void EnablePartPhysics()
        {
            SetPartPhysics(true);
        }

        [MenuItem("AdaptiveAR/Alignment/6b - Disable Part Physics (freeze in place)", false, 16)]
        public static void DisablePartPhysics()
        {
            SetPartPhysics(false);
        }

        /// <summary>
        /// Flips releaseToGravityOnEnable on every part. The C# default cannot do this any more:
        /// once the scene has been saved the value is written into the scene file, so it has to
        /// be set explicitly here.
        /// </summary>
        private static void SetPartPhysics(bool enabled)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogError("[AlignmentSetup] No valid scene open. Aborted.");
                return;
            }

            var parts = new List<DropIntoTray>();
            foreach (GameObject root in scene.GetRootGameObjects())
                parts.AddRange(root.GetComponentsInChildren<DropIntoTray>(true));

            if (parts.Count == 0)
            {
                Debug.LogError("[AlignmentSetup] No DropIntoTray parts found. Aborted.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(enabled ? "Alignment: Enable Part Physics" : "Alignment: Disable Part Physics");

            int changed = 0;
            foreach (DropIntoTray part in parts)
            {
                var so = new SerializedObject(part);
                SerializedProperty prop = so.FindProperty("releaseToGravityOnEnable");
                if (prop == null) continue;

                if (prop.boolValue != enabled)
                {
                    prop.boolValue = enabled;
                    so.ApplyModifiedProperties();
                    changed++;
                }
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            if (enabled)
            {
                Debug.Log(
                    $"[AlignmentSetup] Part physics ENABLED on {changed} of {parts.Count} parts.\n" +
                    "  Parts now fall and settle into the trays instead of hanging in mid air.\n" +
                    "  Released parts fall to the tabletop collider.\n" +
                    "  Safety nets already in place: continuous collision detection so nothing\n" +
                    "  tunnels through the trays, the tabletop pad underneath, and recovery that\n" +
                    "  returns any part that escapes more than 0.75 m.\n" +
                    "  Run step 3 first if you have not added the tabletop yet.\n" +
                    "  Save the scene to persist.");
            }
            else
            {
                Debug.Log(
                    $"[AlignmentSetup] Part physics DISABLED on {changed} of {parts.Count} parts.\n" +
                    "  Parts stay exactly where they are authored and never fall.");
            }
        }

        // =====================================================================
        // 7. REVERT
        // =====================================================================

        [MenuItem("AdaptiveAR/Alignment/7 - Revert Anchor Fix", false, 17)]
        public static void RevertAnchorFix()
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject anchor = FindRoot(scene, NameEngineAnchor);
            GameObject markerAnchor = FindRoot(scene, NameMarkerAnchor);
            ArUcoTrackingAppCoordinator coord = FindCoordinator(scene);

            if (anchor == null)
            {
                Debug.LogError("[AlignmentSetup] EngineAnchor not found. Aborted.");
                return;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Alignment: Revert Anchor Fix");

            if (anchor.transform.parent != null)
                Undo.SetTransformParent(anchor.transform, null, "Unparent EngineAnchor");

            // Restore the original committed local transform, including the original 0.1 scale.
            // Reading the live scale here would be wrong: the apply step neutralises it to 1.
            Undo.RecordObject(anchor.transform, "Restore EngineAnchor local transform");
            anchor.transform.localPosition = OriginalAnchorLocalPos;
            anchor.transform.localRotation = Quaternion.Euler(OriginalAnchorEuler);
            anchor.transform.localScale = OriginalAnchorLocalScale;

            int repointed = coord != null && markerAnchor != null
                ? RepointMarkerPairs(coord, markerAnchor, anchor)
                : 0;

            if (markerAnchor != null)
                Undo.DestroyObjectImmediate(markerAnchor);

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log(
                "[AlignmentSetup] Anchor fix reverted.\n" +
                $"  ArUco marker pairs repointed back to EngineAnchor: {repointed}\n" +
                "  MarkerAnchor / CalibrationOffset removed.\n" +
                "  NOTE: the authoritative revert is 'git checkout -- \"Assets/1 - ArUcoMarkerTracking.unity\"',\n" +
                "  since the pre-fix scene is committed.");
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static GameObject FindRoot(Scene scene, string name)
        {
            if (!scene.IsValid()) return null;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;

                // The object may already have been reparented by a previous apply.
                Transform found = FindDeep(root.transform, name);
                if (found != null) return found.gameObject;
            }
            return null;
        }

        private static Transform FindDeep(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c.name == name) return c;

                Transform deeper = FindDeep(c, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private static GameObject FindChild(Transform parent, string name)
        {
            Transform t = parent.Find(name);
            return t != null ? t.gameObject : null;
        }

        /// <summary>Resolves "Root/Child/Grandchild". Uses Transform.Find so inactive objects resolve.</summary>
        private static GameObject FindByPath(Scene scene, string path)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            GameObject rootGo = FindRoot(scene, parts[0]);
            if (rootGo == null) return null;

            Transform current = rootGo.transform;
            for (int i = 1; i < parts.Length; i++)
            {
                current = current.Find(parts[i]);
                if (current == null) return null;
            }
            return current.gameObject;
        }

        private static ArUcoTrackingAppCoordinator FindCoordinator(Scene scene)
        {
            if (!scene.IsValid()) return null;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var c = root.GetComponentInChildren<ArUcoTrackingAppCoordinator>(true);
                if (c != null) return c;
            }
            return null;
        }

        private static int CountDropIntoTray(Scene scene)
        {
            if (!scene.IsValid()) return 0;

            int n = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
                n += root.GetComponentsInChildren<DropIntoTray>(true).Length;

            return n;
        }
    }
}
