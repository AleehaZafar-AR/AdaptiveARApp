// File: PresentationPlacementAnimator.cs
// Unity side of the presentation-only placement staging (see PlacementMotion.cs for the
// gating, easing and lifecycle, which are plain C# and headless-tested).
//
// THE REAL PART IS NEVER MOVED BY THIS CODE. A first device run (TV2, 2026-10-29) animated
// the real part and the production validator - correctly, exactly as for a participant -
// accepted it as a genuine placement the moment it came within tolerance, before the runner
// injected anything: the scripted injection then landed on the NEXT armed action (the
// connecting rod was "skipped"), and the animator's later scale writes hit a part the join
// had just re-parented under the turntable (the crankshaft grew ~240x and "disappeared").
//
// Now the motion is carried by a disposable visual proxy: a renderer-only copy of the part
// (meshes + materials, no colliders, no Rigidbody, no scripts, not registered anywhere). The
// real part's renderers are hidden for the flight and shown again before the production
// success path runs, so the production snap / lock / join / sentinel act on an untouched part,
// identical to validation mode and to a real placement. Runs only from
// TechnicalValidationRunner.AnimatedSuccess (technical validation enabled AND mode =
// presentation). No hands or controllers simulated; no material, UI or audio change; nothing
// here references support, decision, scheduler or physiology state.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.TechnicalValidation
{
    public static class PresentationPlacementAnimator
    {
        /// <summary>Peak scale gain of the highlight pulse (8 %).</summary>
        public const float HighlightScaleGain = 0.08f;
        public const string ProxyNamePrefix = "TV_MotionProxy:";

        /// <summary>
        /// Builds a renderer-only copy of <paramref name="part"/> at its current world pose.
        /// Returns null when the part has no mesh renderer to copy.
        /// </summary>
        public static GameObject BuildProxy(Transform part)
        {
            if (part == null) return null;
            var renderers = part.GetComponentsInChildren<MeshRenderer>(false);
            if (renderers == null || renderers.Length == 0) return null;
            var root = new GameObject(ProxyNamePrefix + part.name);
            root.transform.SetPositionAndRotation(part.position, part.rotation);
            root.transform.localScale = part.lossyScale;      // no parent: local == world
            int copied = 0;
            Vector3 ps = part.lossyScale;
            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !r.enabled) continue;
                var child = new GameObject(r.name);
                child.layer = r.gameObject.layer;
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = part.InverseTransformPoint(r.transform.position);
                child.transform.localRotation = Quaternion.Inverse(part.rotation) * r.transform.rotation;
                Vector3 ls = r.transform.lossyScale;
                child.transform.localScale = new Vector3(Safe(ls.x, ps.x), Safe(ls.y, ps.y), Safe(ls.z, ps.z));
                child.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var mr = child.AddComponent<MeshRenderer>();
                mr.sharedMaterials = r.sharedMaterials;
                mr.shadowCastingMode = r.shadowCastingMode;
                mr.receiveShadows = r.receiveShadows;
                copied++;
            }
            if (copied == 0) { UnityEngine.Object.Destroy(root); return null; }
            return root;
        }

        private static float Safe(float a, float b) { return Mathf.Abs(b) > 1e-6f ? a / b : 1f; }

        /// <summary>Hides the real part's renderers for the flight; returns the ones that were enabled so they can be restored exactly.</summary>
        public static List<Renderer> HideRenderers(Transform part)
        {
            var hidden = new List<Renderer>();
            if (part == null) return hidden;
            foreach (var r in part.GetComponentsInChildren<Renderer>(false))
                if (r != null && r.enabled) { r.enabled = false; hidden.Add(r); }
            return hidden;
        }

        public static void ShowRenderers(List<Renderer> hidden)
        {
            if (hidden == null) return;
            foreach (var r in hidden) if (r != null) r.enabled = true;
            hidden.Clear();
        }

        /// <summary>
        /// Highlight pulse on the proxy, then eased position lerp + rotation slerp of the PROXY to
        /// the target pose. The real part is hidden meanwhile and shown again (and the proxy
        /// destroyed) on every exit path, before the caller runs the production success path.
        /// Finishes when <paramref name="run"/> reports Done, or early if the part is destroyed.
        /// </summary>
        public static IEnumerator Animate(Transform part, Vector3 targetPosition, Quaternion targetRotation, PlacementMotionRun run, Func<double> clock)
        {
            if (part == null || run == null || clock == null) yield break;
            GameObject proxy = BuildProxy(part);
            if (proxy == null) yield break;                 // nothing to show: the caller injects immediately
            List<Renderer> hidden = HideRenderers(part);
            Transform fly = proxy.transform;
            Vector3 fromPos = fly.position;
            Quaternion fromRot = fly.rotation;
            Vector3 baseScale = fly.localScale;
            run.Begin(clock());
            try
            {
                while (!run.MotionFinished)
                {
                    yield return null;
                    if (part == null || fly == null) yield break;
                    float f = (float)run.Advance(clock());
                    fly.localScale = baseScale * (1f + HighlightScaleGain * (float)run.HighlightPulse);
                    if (run.Current == PlacementMotionRun.Phase.Motion || run.MotionFinished)
                        fly.SetPositionAndRotation(Vector3.LerpUnclamped(fromPos, targetPosition, f), Quaternion.SlerpUnclamped(fromRot, targetRotation, f));
                }
                if (fly != null) { fly.localScale = baseScale; fly.SetPositionAndRotation(targetPosition, targetRotation); }
            }
            finally
            {
                ShowRenderers(hidden);
                if (proxy != null) UnityEngine.Object.Destroy(proxy);
            }
        }
    }
}
