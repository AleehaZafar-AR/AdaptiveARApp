// File: FeedbackPulse.cs
// A brief colour pulse on a part, perceivable without reading the panel.
//
// Success: a green pulse on the part that just seated. Invalid attempt: a short amber
// (or red) pulse on the part in the hand. Done with a MaterialPropertyBlock on the
// renderers and restored exactly afterwards - no material is edited and nothing stays
// recoloured.
//
// The authored state is captured ONCE per part, at the start of the first pulse, and
// kept until the last pulse on that part ends. A pulse that interrupts another (red
// failure, then green success a few frames later) restores that captured state before
// it starts, instead of adopting the half-red block as the "original" - which is how a
// locked crankshaft used to stay red for good.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.UI
{
    public static class FeedbackPulse
    {
        private class Runner : MonoBehaviour { }
        private static Runner _runner;
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private class Episode
        {
            public Renderer[] renderers;
            public MaterialPropertyBlock[] originals;
            public Coroutine routine;
        }

        private static readonly Dictionary<Transform, Episode> _active = new Dictionary<Transform, Episode>();

        public static void Success(Transform part, float seconds = 0.8f)
        {
            Pulse(part, new Color(0.37f, 0.85f, 0.55f), seconds);
        }

        public static void Invalid(Transform part, float seconds = 0.4f)
        {
            Pulse(part, new Color(0.96f, 0.62f, 0.14f), seconds);
        }

        public static void Pulse(Transform part, Color colour, float seconds)
        {
            if (part == null) return;
            Runner r = EnsureRunner();
            if (r == null) return;

            Episode ep;
            if (_active.TryGetValue(part, out ep) && ep != null)
            {
                // Interrupting a pulse: stop it and put the authored state back FIRST, so the
                // new pulse lerps from, and finally restores to, the real original.
                if (ep.routine != null) r.StopCoroutine(ep.routine);
                Restore(ep);
            }
            else
            {
                ep = Capture(part);
                _active[part] = ep;
            }

            ep.routine = r.StartCoroutine(PulseRoutine(part, ep, colour, seconds));
        }

        /// <summary>Ends any pulse on the part immediately and restores its authored state.</summary>
        public static void Cancel(Transform part)
        {
            if (part == null || !_active.TryGetValue(part, out Episode ep) || ep == null) return;
            if (_runner != null && ep.routine != null) _runner.StopCoroutine(ep.routine);
            Restore(ep);
            _active.Remove(part);
        }

        /// <summary>Ends every pulse and restores every part. Used before another system edits property blocks.</summary>
        public static void CancelAll()
        {
            if (_active.Count == 0) return;
            var parts = new List<Transform>(_active.Keys);
            foreach (Transform p in parts) Cancel(p);
        }

        /// <summary>True while a pulse is running on the part (diagnostic).</summary>
        public static bool IsPulsing(Transform part)
        {
            return part != null && _active.ContainsKey(part);
        }

        private static Runner EnsureRunner()
        {
            if (_runner != null) return _runner;
            var go = new GameObject("FeedbackPulse (runtime)");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
            return _runner;
        }

        private static Episode Capture(Transform part)
        {
            var renderers = part.GetComponentsInChildren<Renderer>(false);
            var originals = new MaterialPropertyBlock[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                originals[i] = new MaterialPropertyBlock();
                if (renderers[i] != null) renderers[i].GetPropertyBlock(originals[i]);
            }
            return new Episode { renderers = renderers, originals = originals };
        }

        private static void Restore(Episode ep)
        {
            if (ep == null || ep.renderers == null) return;
            for (int i = 0; i < ep.renderers.Length; i++)
                if (ep.renderers[i] != null) ep.renderers[i].SetPropertyBlock(ep.originals[i]);
        }

        private static IEnumerator PulseRoutine(Transform part, Episode ep, Color colour, float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                // Quick rise, slow fall: reads as a flash, not a recolour.
                float k = t < seconds * 0.2f ? t / (seconds * 0.2f) : 1f - (t - seconds * 0.2f) / (seconds * 0.8f);
                k = Mathf.Clamp01(k);

                for (int i = 0; i < ep.renderers.Length; i++)
                {
                    Renderer rend = ep.renderers[i];
                    if (rend == null || rend.sharedMaterial == null) continue;

                    // Start from the AUTHORED block every frame, never from the previous frame's.
                    var block = new MaterialPropertyBlock();
                    rend.GetPropertyBlock(block);
                    MaterialPropertyBlock original = ep.originals[i];

                    Material m = rend.sharedMaterial;
                    if (m.HasProperty(ColorId))
                    {
                        Color from = original.HasColor(ColorId) ? original.GetColor(ColorId) : m.GetColor(ColorId);
                        block.SetColor(ColorId, Color.Lerp(from, colour, k));
                    }
                    else if (m.HasProperty(BaseColorId))
                    {
                        Color from = original.HasColor(BaseColorId) ? original.GetColor(BaseColorId) : m.GetColor(BaseColorId);
                        block.SetColor(BaseColorId, Color.Lerp(from, colour, k));
                    }
                    if (m.HasProperty(EmissionId))
                    {
                        Color from = original.HasColor(EmissionId) ? original.GetColor(EmissionId) : m.GetColor(EmissionId);
                        block.SetColor(EmissionId, Color.Lerp(from, colour * 0.6f, k));
                    }

                    rend.SetPropertyBlock(block);
                }

                t += Time.deltaTime;
                yield return null;
            }

            Restore(ep);
            if (_active.TryGetValue(part, out Episode current) && current == ep) _active.Remove(part);
            Debug.Log($"[FeedbackPulse] restored authored state on '{part.name}' after {seconds:F2}s pulse.");
        }
    }
}
