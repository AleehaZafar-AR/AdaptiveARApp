// File: AnchorCalibration.cs
// Researcher-tunable alignment AND scale between the physical ArUco marker and the
// virtual assembly, adjustable IN-HEADSET without a rebuild (CLAUDE.md 4.3).
//
// Why this component exists
// -------------------------
// OpenCVARUtils.SetTransformFromMatrix writes localPosition, localRotation AND
// localScale onto the ArUco target. The marker pose is a rigid transform, so
// ExtractScaleFromMatrix always returns (1,1,1) - so whatever scale was authored on
// the ArUco target was silently overwritten every frame.
//
// The fix is structural rather than a per-frame correction:
//
//     MarkerAnchor        <- ArUco target; its scale may be clobbered to 1, harmless
//       CalibrationOffset <- THIS component; owns lift, lateral, yaw AND scale
//         EngineAnchor    <- left at scale 1; holds the model
//
// Because the marker pose lands on the parent, nothing here has to undo it: no
// accumulation risk, no dependence on script execution order.
//
// All assembly scale lives in assemblyScale below, so there is exactly ONE scale
// knob for the whole model and it can be tuned against a tape measure on the bench.
//
// Axis convention
// ---------------
// The ArUco pose maps the marker normal onto the target's +Z (forward), not +Y. So
// "up, out of the table" is local +Z in this transform's space. The fields below are
// named physically and do that mapping internally.

using UnityEngine;

namespace AdaptiveAR.MR
{
    public class AnchorCalibration : MonoBehaviour
    {
        [Header("Assembly Scale")]
        [Tooltip("Uniform scale of the whole assembly. 1 = life size. This is the only " +
                 "scale knob; EngineAnchor itself stays at 1. Tune against a tape measure.")]
        [SerializeField] private float assemblyScale = 1f;

        [Header("Marker-Plane Alignment (metres)")]
        [Tooltip("Lift along the marker normal, out of the table. Use GroundToMarkerPlane() " +
                 "or the editor tool to compute this from the model's real bounds rather than guessing.")]
        [SerializeField] private float liftAboveMarker = 0f;

        [Tooltip("Sideways shift within the marker plane (x = marker right, y = marker up-plane).")]
        [SerializeField] private Vector2 lateralOffset = Vector2.zero;

        [Tooltip("Rotation of the assembly about the marker normal, in degrees.")]
        [SerializeField] private float yawAboutMarkerNormal = 0f;

        [Header("Auto-Ground")]
        [Tooltip("Extra gap left between the lowest point of the assembly and the table, in metres.")]
        [SerializeField] private float groundClearance = 0f;

        [Tooltip("Re-ground automatically whenever the scale is nudged, so changing size " +
                 "never re-sinks the assembly into the table.")]
        [SerializeField] private bool autoGroundOnScaleChange = true;

        [Header("In-Headset Nudge")]
        [Tooltip("Metres added or removed per lift nudge.")]
        [SerializeField] private float liftNudgeStep = 0.005f;

        [Tooltip("Scale added or removed per scale nudge.")]
        [SerializeField] private float scaleNudgeStep = 0.05f;

        [Tooltip("Allow controller nudging on device.")]
        [SerializeField] private bool enableControllerNudge = true;

        [Tooltip("Raise the assembly. Left thumbstick up by default - clear of Button.One " +
                 "(CV debug quad) and B/Y (support level).")]
        [SerializeField] private OVRInput.RawButton nudgeUpButton = OVRInput.RawButton.LThumbstickUp;

        [Tooltip("Lower the assembly.")]
        [SerializeField] private OVRInput.RawButton nudgeDownButton = OVRInput.RawButton.LThumbstickDown;

        [Tooltip("Make the assembly bigger.")]
        [SerializeField] private OVRInput.RawButton scaleUpButton = OVRInput.RawButton.LThumbstickRight;

        [Tooltip("Make the assembly smaller.")]
        [SerializeField] private OVRInput.RawButton scaleDownButton = OVRInput.RawButton.LThumbstickLeft;

        [Header("Debug")]
        [SerializeField] private bool logChanges = true;

        [Tooltip("Optional TMP label showing current scale and lift, for an on-device readout.")]
        [SerializeField] private TMPro.TextMeshProUGUI calibrationReadout;

        /// <summary>Current lift along the marker normal, in metres.</summary>
        public float LiftAboveMarker { get { return liftAboveMarker; } }

        /// <summary>Current uniform assembly scale. 1 = life size.</summary>
        public float AssemblyScale { get { return assemblyScale; } }

        private void OnEnable()
        {
            Apply();
        }

        private void Update()
        {
            if (!enableControllerNudge)
                return;

            if (OVRInput.GetDown(nudgeUpButton)) NudgeUp();
            if (OVRInput.GetDown(nudgeDownButton)) NudgeDown();
            if (OVRInput.GetDown(scaleUpButton)) NudgeScaleUp();
            if (OVRInput.GetDown(scaleDownButton)) NudgeScaleDown();

            // Editor convenience while testing without a headset.
            if (Input.GetKeyDown(KeyCode.PageUp)) NudgeUp();
            if (Input.GetKeyDown(KeyCode.PageDown)) NudgeDown();
            if (Input.GetKeyDown(KeyCode.Equals)) NudgeScaleUp();
            if (Input.GetKeyDown(KeyCode.Minus)) NudgeScaleDown();
        }

        /// <summary>
        /// Writes the tunable values onto this transform. Safe to call repeatedly:
        /// every value is absolute, never accumulated.
        /// </summary>
        public void Apply()
        {
            // Parent (MarkerAnchor) carries the marker pose, whose +Z is the marker normal.
            // localPosition lives in the parent's space, so it is unaffected by localScale:
            // changing the scale never corrupts the lift.
            transform.localPosition = new Vector3(lateralOffset.x, lateralOffset.y, liftAboveMarker);
            transform.localRotation = Quaternion.AngleAxis(yawAboutMarkerNormal, Vector3.forward);
            transform.localScale = Vector3.one * assemblyScale;

            UpdateReadout();
        }

        // ---------------- Lift ----------------

        public void NudgeUp() { SetLift(liftAboveMarker + liftNudgeStep); }
        public void NudgeDown() { SetLift(liftAboveMarker - liftNudgeStep); }

        /// <summary>Sets the lift along the marker normal, in metres.</summary>
        public void SetLift(float metres)
        {
            liftAboveMarker = metres;
            Apply();

            if (logChanges)
                Debug.Log($"[AnchorCalibration] lift = {liftAboveMarker * 100f:F2} cm, scale = {assemblyScale:F3}");
        }

        // ---------------- Scale ----------------

        public void NudgeScaleUp() { SetAssemblyScale(assemblyScale + scaleNudgeStep); }
        public void NudgeScaleDown() { SetAssemblyScale(assemblyScale - scaleNudgeStep); }

        /// <summary>
        /// Sets the uniform assembly scale. 1 = life size. Re-grounds afterwards by default,
        /// because a larger assembly reaches further below the marker plane.
        /// </summary>
        public void SetAssemblyScale(float scale)
        {
            assemblyScale = Mathf.Max(0.001f, scale);
            Apply();

            if (autoGroundOnScaleChange)
                GroundToMarkerPlane();
            else if (logChanges)
                Debug.Log($"[AnchorCalibration] scale = {assemblyScale:F3}, lift = {liftAboveMarker * 100f:F2} cm");
        }

        // ---------------- Auto-ground ----------------

        /// <summary>
        /// Measures the assembly's real rendered bounds and sets the lift so its lowest
        /// point rests on the marker plane (plus groundClearance). This is what makes the
        /// vertical alignment correct at ANY scale instead of a hand-tuned magic number.
        ///
        /// Requires the model to be active so renderer bounds are valid.
        /// </summary>
        public bool GroundToMarkerPlane()
        {
            Transform plane = transform.parent != null ? transform.parent : transform;
            Vector3 origin = plane.position;
            Vector3 normal = plane.forward;   // marker normal = "up, out of the table"

            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogWarning("[AnchorCalibration] GroundToMarkerPlane found no renderers under " +
                                 "this transform; lift unchanged.", this);
                return false;
            }

            float lowest = float.PositiveInfinity;
            int counted = 0;

            foreach (Renderer r in renderers)
            {
                Bounds b = r.bounds;

                // An INACTIVE renderer reports zero bounds, so fall back to its mesh.
                // Without this, grounding silently ignores every hidden part - and the model
                // is hidden until the marker is found, so it would ignore everything.
                if (b.size == Vector3.zero)
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                        continue;

                    b = TransformBounds(r.transform, mf.sharedMesh.bounds);
                }

                if (b.size == Vector3.zero)
                    continue;

                // Project all eight world-space AABB corners onto the marker normal.
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(
                        b.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f,
                                    (i & 2) == 0 ? -1f : 1f,
                                    (i & 4) == 0 ? -1f : 1f));

                    float h = Vector3.Dot(corner - origin, normal);
                    if (h < lowest) lowest = h;
                }
                counted++;
            }

            if (counted == 0 || float.IsInfinity(lowest))
            {
                Debug.LogWarning("[AnchorCalibration] GroundToMarkerPlane could not measure valid " +
                                 "bounds; lift unchanged.", this);
                return false;
            }

            float before = liftAboveMarker;
            liftAboveMarker += (groundClearance - lowest);
            Apply();

            if (logChanges)
                Debug.Log($"[AnchorCalibration] Grounded to marker plane. " +
                          $"Lowest point was {lowest * 100f:F2} cm relative to the table; " +
                          $"lift {before * 100f:F2} -> {liftAboveMarker * 100f:F2} cm " +
                          $"(scale {assemblyScale:F3}, {counted} renderers measured).");

            return true;
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

        /// <summary>Returns the assembly to the raw marker plane with no offsets, keeping scale.</summary>
        public void ResetAlignment()
        {
            liftAboveMarker = 0f;
            lateralOffset = Vector2.zero;
            yawAboutMarkerNormal = 0f;
            Apply();

            if (logChanges)
                Debug.Log("[AnchorCalibration] Alignment reset to the raw marker plane.");
        }

        private void UpdateReadout()
        {
            if (calibrationReadout != null)
                calibrationReadout.text = $"Scale {assemblyScale:F2}   Lift {liftAboveMarker * 100f:F1} cm";
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Keep the scene view honest while values are tuned in the Inspector.
            assemblyScale = Mathf.Max(0.001f, assemblyScale);
            transform.localPosition = new Vector3(lateralOffset.x, lateralOffset.y, liftAboveMarker);
            transform.localRotation = Quaternion.AngleAxis(yawAboutMarkerNormal, Vector3.forward);
            transform.localScale = Vector3.one * assemblyScale;
        }
#endif
    }
}
