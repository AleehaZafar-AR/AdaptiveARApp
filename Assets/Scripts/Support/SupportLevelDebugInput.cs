// File: SupportLevelDebugInput.cs
// Manual, on-device switching of L1/L2/L3 before the AI decision layer exists.
//
// This is the Phase 1 validation tool: on the Quest, force the support level up
// and down and confirm the guidance changes while the engine stays anchored and
// the assembly step stays put.
//
// Button.One is deliberately NOT used - ArUcoTrackingAppCoordinator already binds
// it to the CV debug quad toggle. Raw B / Y are used instead so the two debug
// affordances cannot collide.

using UnityEngine;

namespace AdaptiveAR.Support
{
    public class SupportLevelDebugInput : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private SupportLevelController supportLevel;

        [Header("Enable")]
        [Tooltip("Master switch. Turn off for participant-facing runs.")]
        [SerializeField] private bool enableDebugInput = true;

        [Header("Controller Bindings")]
        [Tooltip("Raw button that raises the support level (default: B on the right controller).")]
        [SerializeField] private OVRInput.RawButton increaseButton = OVRInput.RawButton.B;

        [Tooltip("Raw button that lowers the support level (default: Y on the left controller).")]
        [SerializeField] private OVRInput.RawButton decreaseButton = OVRInput.RawButton.Y;

        [Header("Editor Keyboard Bindings")]
        [Tooltip("Keyboard 1/2/3 select L1/L2/L3 directly while testing in the Editor.")]
        [SerializeField] private bool enableKeyboard = true;

        [Header("Optional Readout")]
        [Tooltip("Optional TMP label that displays the current level. Leave unassigned; " +
                 "wiring it is a separate scene change.")]
        [SerializeField] private TMPro.TextMeshProUGUI levelReadout;

        private void OnEnable()
        {
            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged += HandleLevelChanged;

            UpdateReadout();
        }

        private void OnDisable()
        {
            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged -= HandleLevelChanged;
        }

        private void Update()
        {
            if (!enableDebugInput || supportLevel == null)
                return;

            if (OVRInput.GetDown(increaseButton))
                supportLevel.ApplyAction(SupportAction.INCREASE_SUPPORT, "debug_input");

            if (OVRInput.GetDown(decreaseButton))
                supportLevel.ApplyAction(SupportAction.DECREASE_SUPPORT, "debug_input");

            if (!enableKeyboard)
                return;

            if (Input.GetKeyDown(KeyCode.Alpha1))
                supportLevel.SetSupportLevel(SupportLevel.L1_Minimal, "debug_input");

            if (Input.GetKeyDown(KeyCode.Alpha2))
                supportLevel.SetSupportLevel(SupportLevel.L2_Guided, "debug_input");

            if (Input.GetKeyDown(KeyCode.Alpha3))
                supportLevel.SetSupportLevel(SupportLevel.L3_Assisted, "debug_input");
        }

        private void HandleLevelChanged(SupportLevel previous, SupportLevel current, string reason)
        {
            UpdateReadout();
        }

        private void UpdateReadout()
        {
            if (levelReadout == null || supportLevel == null)
                return;

            levelReadout.text = supportLevel.CurrentLevel.ToString();
        }
    }
}
