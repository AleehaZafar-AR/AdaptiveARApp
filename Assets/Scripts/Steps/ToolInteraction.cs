// File: ToolInteraction.cs
// Detects that a tool has been used on a fastener, so a tightening substep can be
// a real task state rather than a line of text saying "tighten the bolt".
//
// The detection rule is deliberately configurable. Which rule actually works on a
// Quest - proximity, dwell, a trigger press, or rotation of the tool - cannot be
// decided from here; it depends on hand-tracking accuracy and how the physical tool
// behaves on the bench. All four are implemented and switchable so the question can
// be settled by testing rather than by guessing now and rewriting later.
//
// No tool model exists in the repository yet. Assign one to `toolTip` and this
// starts working; until then the ToolAction substeps stay disabled and are logged
// as skipped.

using System;
using AdaptiveAR.Logging;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class ToolInteraction : MonoBehaviour
    {
        public enum DetectionRule
        {
            /// <summary>Tool tip within range of the fastener for a dwell period.</summary>
            ProximityDwell = 0,

            /// <summary>Within range AND the trigger held for the dwell period.</summary>
            ProximityAndTrigger = 1,

            /// <summary>Within range and rotated through a total angle, like a real driver.</summary>
            ProximityAndRotation = 2,

            /// <summary>Within range; a single trigger press completes it. Most forgiving.</summary>
            ProximityAndPress = 3
        }

        [Header("References")]
        [Tooltip("The working end of the tool. No tool model exists yet - assign one here " +
                 "and tool actions become performable.")]
        [SerializeField] private Transform toolTip;

        [SerializeField] private WorkflowState workflow;
        [SerializeField] private GuidanceRegistry guidanceRegistry;
        [SerializeField] private SessionLogger logger;

        [Header("Detection")]
        [Tooltip("Which rule counts as 'tightened'. Settle this on the bench, not here.")]
        [SerializeField] private DetectionRule rule = DetectionRule.ProximityDwell;

        [Tooltip("Metres from the fastener that count as engaged. Provisional.")]
        [SerializeField] private float engageRadius = 0.06f;

        [Tooltip("Seconds engaged before the action completes. Provisional.")]
        [SerializeField] private float dwellSeconds = 1.5f;

        [Tooltip("Total degrees of tool rotation required, for the rotation rule.")]
        [SerializeField] private float requiredRotationDegrees = 360f;

        [SerializeField] private OVRInput.RawButton triggerButton = OVRInput.RawButton.RIndexTrigger;

        [Header("Debug")]
        [SerializeField] private bool logProgress = true;

        /// <summary>Fired when the active tool action has been satisfied.</summary>
        public event Action OnToolActionCompleted;

        /// <summary>0-1 progress towards completing the current tool action.</summary>
        public float Progress { get; private set; }

        /// <summary>True when a tool action is active and a tool is available.</summary>
        public bool IsArmed { get; private set; }

        private Transform _fastener;
        private float _dwell;
        private float _rotationAccumulated;
        private Quaternion _lastToolRotation;
        private bool _completedThisAction;
        private string _armedActionId;

        private void Update()
        {
            AssemblyAction action = workflow != null ? workflow.CurrentAction : null;

            // Only arm for an enabled tool action.
            if (action == null || !action.enabled || action.kind != ActionKind.ToolAction)
            {
                Disarm();
                return;
            }

            if (action.Id != _armedActionId)
            {
                Arm(action);
                return;
            }

            if (_completedThisAction || toolTip == null || _fastener == null)
                return;

            float distance = Vector3.Distance(toolTip.position, _fastener.position);
            bool engaged = distance <= engageRadius;

            if (!engaged)
            {
                _dwell = 0f;
                Progress = 0f;
                _lastToolRotation = toolTip.rotation;
                return;
            }

            switch (rule)
            {
                case DetectionRule.ProximityDwell:
                    _dwell += Time.deltaTime;
                    Progress = Mathf.Clamp01(_dwell / Mathf.Max(0.01f, dwellSeconds));
                    break;

                case DetectionRule.ProximityAndTrigger:
                    if (OVRInput.Get(triggerButton)) _dwell += Time.deltaTime;
                    else _dwell = 0f;
                    Progress = Mathf.Clamp01(_dwell / Mathf.Max(0.01f, dwellSeconds));
                    break;

                case DetectionRule.ProximityAndRotation:
                    _rotationAccumulated += Quaternion.Angle(_lastToolRotation, toolTip.rotation);
                    _lastToolRotation = toolTip.rotation;
                    Progress = Mathf.Clamp01(_rotationAccumulated / Mathf.Max(1f, requiredRotationDegrees));
                    break;

                case DetectionRule.ProximityAndPress:
                    Progress = OVRInput.GetDown(triggerButton) ? 1f : Progress;
                    break;
            }

            if (Progress >= 1f)
                Complete(action);
        }

        private void Arm(AssemblyAction action)
        {
            _armedActionId = action.Id;
            _completedThisAction = false;
            _dwell = 0f;
            _rotationAccumulated = 0f;
            Progress = 0f;
            _fastener = null;

            if (guidanceRegistry != null && !string.IsNullOrEmpty(action.targetKey) &&
                guidanceRegistry.TryResolveQuiet(action.targetKey, out GameObject go))
            {
                _fastener = go.transform;
            }

            if (toolTip != null) _lastToolRotation = toolTip.rotation;

            IsArmed = _fastener != null && toolTip != null;

            if (!IsArmed && logProgress)
            {
                Debug.LogWarning($"[ToolInteraction] Action '{action.Id}' is a tool action but " +
                                 (toolTip == null
                                     ? "no tool is assigned."
                                     : $"target '{action.targetKey}' could not be resolved."), this);
            }
        }

        private void Disarm()
        {
            IsArmed = false;
            _armedActionId = null;
            _fastener = null;
            Progress = 0f;
            _dwell = 0f;
            _rotationAccumulated = 0f;
        }

        private void Complete(AssemblyAction action)
        {
            _completedThisAction = true;
            Progress = 1f;

            if (logger != null)
                logger.LogFastenerAction(action.targetKey, toolTip != null ? toolTip.name : null, true);

            if (logProgress)
                Debug.Log($"[ToolInteraction] Tool action '{action.Id}' satisfied by {rule}.");

            OnToolActionCompleted?.Invoke();
        }
    }
}
