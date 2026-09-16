// File: StepSupportContent.cs
// Researcher-authored guidance content for ONE support level of ONE assembly step.
//
// Every field ships empty. No L1/L2/L3 content is defined here or anywhere in
// code - contents are authored per step in the Inspector, from the literature.
//
// Any field left empty falls back to the step-level default on StepData
// (see StepData.GetContent and StepPresenter). Fallback is per-field, so a level
// can override only its caption while inheriting the step's default ghost.

using System;
using UnityEngine;
using UnityEngine.Video;

namespace AdaptiveAR.Steps
{
    [Serializable]
    public class StepSupportContent
    {
        [Header("Instruction Text")]
        [Tooltip("Caption shown for this level. Empty = fall back to the step's stepTitle.")]
        [TextArea(2, 5)]
        public string instructionText;

        [Tooltip("Optional secondary line. Empty = fall back to the step's stepDescription.")]
        [TextArea(2, 5)]
        public string instructionDetail;

        [Header("Ghost / Visual Guidance (scene objects, by key)")]
        [Tooltip("Keys resolved through GuidanceRegistry to existing scene objects " +
                 "(e.g. the pre-placed ghosts under EngineAnchor/Offset/Ghosties).")]
        public string[] ghostKeys;

        [Header("Ghost / Visual Guidance (spawned prefabs)")]
        [Tooltip("Prefabs instantiated relative to the anchor root. " +
                 "Empty = fall back to the step's ghostPrefab.")]
        public GameObject[] ghostPrefabs;

        [Tooltip("Local offset from the anchor root for spawned ghost prefabs.")]
        public Vector3 ghostSpawnOffset;

        [Header("Arrow Guidance")]
        [Tooltip("Keys resolved through GuidanceRegistry to existing scene arrow objects.")]
        public string[] arrowKeys;

        [Tooltip("Arrow prefabs to spawn. Empty = fall back to the step's arrowPrefab.")]
        public GameObject[] arrowPrefabs;

        [Tooltip("Local offset from the anchor root for spawned arrow prefabs.")]
        public Vector3 arrowSpawnOffset;

        [Header("Audio")]
        [Tooltip("Spoken/played instruction for this level. Empty = fall back to the step's instructionAudio.")]
        public AudioClip instructionAudio;

        [Header("Video / Animation (data slots - playback not wired yet)")]
        [Tooltip("Reserved. The live scene has no VideoPlayer; adding one is a separate scene change.")]
        public VideoClip instructionVideo;

        [Tooltip("Reserved. The live scene has no Animator; adding one is a separate scene change.")]
        public AnimationClip demonstrationAnimation;

        [Tooltip("Reserved. Animator trigger name to fire when this level is presented.")]
        public string animatorTrigger;

        /// <summary>
        /// True when this level defines no content at all, so the step-level
        /// defaults apply in full.
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                return string.IsNullOrEmpty(instructionText)
                    && string.IsNullOrEmpty(instructionDetail)
                    && IsEmptyArray(ghostKeys)
                    && IsEmptyArray(ghostPrefabs)
                    && IsEmptyArray(arrowKeys)
                    && IsEmptyArray(arrowPrefabs)
                    && instructionAudio == null
                    && instructionVideo == null
                    && demonstrationAnimation == null
                    && string.IsNullOrEmpty(animatorTrigger);
            }
        }

        /// <summary>
        /// True when this level references video or animation content that the
        /// current scene cannot play yet. Used to warn once rather than fail silently.
        /// </summary>
        public bool HasUnsupportedMedia
        {
            get
            {
                return instructionVideo != null
                    || demonstrationAnimation != null
                    || !string.IsNullOrEmpty(animatorTrigger);
            }
        }

        private static bool IsEmptyArray(Array a)
        {
            return a == null || a.Length == 0;
        }
    }
}
