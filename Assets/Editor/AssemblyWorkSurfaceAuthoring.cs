// File: AssemblyWorkSurfaceAuthoring.cs (Editor only)
// Custom inspector for AssemblyWorkSurface: Edit-Mode authoring of the piston mat and
// the ONE piston-head target, plus a menu entry that audits every instruction against
// the guidance actually shown at each support level.
//
// Nothing here runs in a build. The reference objects it creates are tagged EditorOnly
// and carry no Rigidbody, collider or Interaction SDK component.

using AdaptiveAR.MR;
using AdaptiveAR.Steps;
using UnityEditor;
using UnityEngine;

namespace AdaptiveAR.EditorTools
{
    [CustomEditor(typeof(AssemblyWorkSurface))]
    public class AssemblyWorkSurfaceAuthoring : Editor
    {
        private string _status;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var ws = (AssemblyWorkSurface)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Piston mat + head target authoring (Edit Mode)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "1. Set Placement Mode = Authored and move THIS object onto the desk in front of the oil pan (Move tool).\n" +
                "2. Show Authoring Preview: the mat, the ring and a green piston-head reference appear under this object.\n" +
                "3. Select '~AuthoringPistonHead' in the Hierarchy and lay it flat where it belongs with Move/Rotate.\n" +
                "4. Capture Piston Head Target Pose: the pose is serialised relative to the mat and Head Target Mode becomes Authored.\n" +
                "5. Save the scene. At runtime the green ghost, the validation target and the snap destination are that exact pose.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(ws.EditorHasAuthoringPreview ? "Refresh Authoring Preview" : "Show Authoring Preview"))
                {
                    Undo.RegisterFullObjectHierarchyUndo(ws.gameObject, "Show authoring preview");
                    _status = ws.EditorShowAuthoringPreview();
                    EditorUtility.SetDirty(ws);
                }
                if (GUILayout.Button("Hide Authoring Preview"))
                {
                    Undo.RegisterFullObjectHierarchyUndo(ws.gameObject, "Hide authoring preview");
                    _status = ws.EditorHideAuthoringPreview();
                    EditorUtility.SetDirty(ws);
                }
                EditorGUILayout.EndHorizontal();

                GUI.backgroundColor = new Color(0.6f, 0.9f, 0.6f);
                if (GUILayout.Button("Capture Piston Head Target Pose", GUILayout.Height(28)))
                {
                    Undo.RecordObject(ws, "Capture piston head target pose");
                    _status = ws.EditorCaptureHeadPose();
                    EditorUtility.SetDirty(ws);
                    if (!Application.isPlaying)
                        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(ws.gameObject.scene);
                }
                GUI.backgroundColor = Color.white;

                if (GUILayout.Button("Select the head reference"))
                {
                    Transform h = ws.EditorAuthoringHead;
                    if (h != null) Selection.activeTransform = h;
                    else _status = "No authoring preview yet.";
                }
            }

            if (ws.EditorHasAuthoringPreview) ws.EditorUpdateRingPreview();

            string state = $"placement={ws.Placement}  headTarget={ws.HeadMode}  capturedPose={(ws.HasAuthoredHeadPose ? "yes" : "no")}  matSize={ws.MatSize:F2} m";
            EditorGUILayout.HelpBox(state + (string.IsNullOrEmpty(_status) ? "" : "\n" + _status),
                                    ws.HasAuthoredHeadPose && ws.HeadMode == AssemblyWorkSurface.HeadTargetMode.Authored ? MessageType.None : MessageType.Warning);
        }

        [MenuItem("AdaptiveAR/Guidance/Audit instruction text vs shown guidance (Console)")]
        private static void AuditGuidance()
        {
            var runner = FindAnyObjectByType<StepRunner>(FindObjectsInactive.Include);
            var registry = FindAnyObjectByType<GuidanceRegistry>(FindObjectsInactive.Include);
            if (runner == null) { Debug.LogError("[GuidanceAudit] No StepRunner in the open scene."); return; }
            int issues = GuidanceAudit.Run(runner, registry, "editor_menu");
            Debug.Log($"[GuidanceAudit] done: {issues} contradiction(s). See the lines above.");
        }
    }
}
