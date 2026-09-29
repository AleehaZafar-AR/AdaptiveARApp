// File: AssemblyAction.cs
// One concrete action inside a major assembly stage.
//
// A stage ("Crankshaft", "Piston 1") is what the participant sees in the progress
// list. An action is what they actually do right now: pick up, align, place,
// fasten. Splitting them is what lets the interface show ONE instruction at a time
// instead of a paragraph, and lets L3 decompose a stage without the card
// overflowing.
//
// Progress is derived from actions, never from button presses: a stage is complete
// only when every one of its required actions has been validated.

using System;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    /// <summary>What kind of thing the participant has to do.</summary>
    public enum ActionKind
    {
        /// <summary>Read and acknowledge. No physical requirement.</summary>
        Acknowledge = 0,

        /// <summary>Move a part onto a target pose. Validated by the placement check.</summary>
        Place = 1,

        /// <summary>
        /// Install a fastener (nut, bolt, cap). Architecturally supported; needs the
        /// fastener to exist as a separate movable object before it can be authored.
        /// </summary>
        Fasten = 2,

        /// <summary>
        /// Use a tool on a fastener. Architecturally supported; needs a tool model and a
        /// detection rule decided on the bench.
        /// </summary>
        ToolAction = 3
    }

    /// <summary>Why a placement was rejected, so feedback can be specific.</summary>
    public enum RejectReason
    {
        None = 0,
        TooFar = 1,
        WrongRotation = 2,
        WrongComponent = 3
    }

    [Serializable]
    public class AssemblyAction
    {
        [Tooltip("Stable id used in logs, e.g. \"crank.place\".")]
        public string actionId;

        public ActionKind kind = ActionKind.Place;

        [Header("Instruction - one short line, not a paragraph")]
        [Tooltip("Shown as the action line. Keep it to a single instruction the participant " +
                 "can act on right now.")]
        [TextArea(1, 3)]
        public string instruction;

        [Tooltip("Optional extra sentence, revealed only at the richer support levels.")]
        [TextArea(1, 3)]
        public string detail;

        [Header("Validation")]
        [Tooltip("Registry key of the part to move, e.g. \"part.crankshaft\".")]
        public string partKey;

        [Tooltip("Other parts that are equally acceptable, e.g. any unplaced piston.")]
        public string[] interchangeablePartKeys;

        [Tooltip("Registry key of the target pose, e.g. \"ghost.crankshaft\".")]
        public string targetKey;

        [Tooltip("Provisional. Tuned on the physical bench, not a validated threshold.")]
        public float positionToleranceMeters = 0.04f;

        [Tooltip("Provisional. Tuned on the physical bench, not a validated threshold.")]
        public float rotationToleranceDegrees = 25f;

        [Tooltip("Seconds the part must settle before the placement is judged.")]
        public float settleSeconds = 0.45f;

        [Header("Guidance")]
        [Tooltip("Ghost keys shown while this action is active. Cleared when it completes.")]
        public string[] ghostKeys;

        [Tooltip("Show a directional cue from the part to the target while this action is active.")]
        public bool showArrow = true;

        [Tooltip("Audio played when this action begins, where a clip has been authored.")]
        public AudioClip audioCue;

        [Header("Availability")]
        [Tooltip("OFF marks an action as designed but not yet performable - a fastener with no " +
                 "movable object, or a tool action with no tool. Disabled actions are skipped at " +
                 "runtime and reported, rather than blocking the participant on something " +
                 "impossible.")]
        public bool enabled = true;

        [Tooltip("Why this action is disabled. Surfaced in the researcher HUD and the log.")]
        public string disabledReason;

        /// <summary>Identifier used in logs.</summary>
        public string Id
        {
            get { return string.IsNullOrEmpty(actionId) ? kind.ToString() : actionId; }
        }

        /// <summary>True when completing this action requires a validated physical placement.</summary>
        public bool RequiresPhysicalValidation
        {
            get { return enabled && (kind == ActionKind.Place || kind == ActionKind.Fasten); }
        }

        /// <summary>Every part key this action accepts, primary first.</summary>
        public System.Collections.Generic.List<string> AcceptedPartKeys()
        {
            var keys = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(partKey)) keys.Add(partKey);

            if (interchangeablePartKeys != null)
            {
                foreach (string k in interchangeablePartKeys)
                    if (!string.IsNullOrEmpty(k) && !keys.Contains(k)) keys.Add(k);
            }
            return keys;
        }
    }
}
