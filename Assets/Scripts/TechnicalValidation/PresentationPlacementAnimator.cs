// File: PresentationPlacementAnimator.cs
// Unity side of the presentation-only placement staging (see PlacementMotion.cs for the
// gating, easing and lifecycle, which are plain C# and headless-tested).
//
// Runs only from TechnicalValidationRunner.AnimatedSuccess, i.e. only when technical
// validation is enabled AND mode = presentation. It moves ONE transform: the armed action's
// part, from where it is to the pose the production snap will give it. The Rigidbody is made
// kinematic for the motion (the production snap keeps it kinematic after success; on a failed
// hand-over RestoreBody puts the previous state back). The highlight is a scale pulse that is
// restored exactly. No hands or controllers are simulated, no material, UI or audio is
// changed, and nothing here references support, decision, scheduler or physiology state.

using System;
using System.Collections;
using UnityEngine;

namespace AdaptiveAR.TechnicalValidation
{
    public static class PresentationPlacementAnimator
    {
        /// <summary>Peak scale gain of the highlight pulse (8 %).</summary>
        public const float HighlightScaleGain = 0.08f;

        /// <summary>Stops and freezes the part's body for the motion. Returns the previous kinematic state.</summary>
        public static bool PrepareBody(Transform part)
        {
            if (part == null) return false;
            var body = part.GetComponent<Rigidbody>();
            if (body == null) return false;
            bool was = body.isKinematic;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
            return was;
        }

        /// <summary>Only when the production success path did not accept the part: the previous kinematic state returns.</summary>
        public static void RestoreBody(Transform part, bool wasKinematic)
        {
            if (part == null) return;
            var body = part.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = wasKinematic;
        }

        /// <summary>
        /// Highlight pulse, then eased position lerp + rotation slerp to the target pose, then the
        /// exact target pose. Yields per frame; finishes when <paramref name="run"/> reports Done
        /// (or stops early if the part is destroyed, leaving the run unfinished).
        /// </summary>
        public static IEnumerator Animate(Transform part, Vector3 targetPosition, Quaternion targetRotation, PlacementMotionRun run, Func<double> clock)
        {
            if (part == null || run == null || clock == null) yield break;
            Vector3 fromPos = part.position;
            Quaternion fromRot = part.rotation;
            Vector3 baseScale = part.localScale;
            run.Begin(clock());
            while (!run.MotionFinished)
            {
                yield return null;
                if (part == null) yield break;
                float f = (float)run.Advance(clock());
                part.localScale = baseScale * (1f + HighlightScaleGain * (float)run.HighlightPulse);
                if (run.Current == PlacementMotionRun.Phase.Motion || run.MotionFinished)
                    part.SetPositionAndRotation(Vector3.LerpUnclamped(fromPos, targetPosition, f), Quaternion.SlerpUnclamped(fromRot, targetRotation, f));
            }
            part.localScale = baseScale;
            part.SetPositionAndRotation(targetPosition, targetRotation);
        }
    }
}
