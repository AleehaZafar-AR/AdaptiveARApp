// File: FeedbackPulse.cs
// A brief colour pulse on a part, perceivable without reading the panel.
//
// Success: a green pulse on the part that just seated. Invalid attempt: a short amber
// pulse on the part in the hand. Done with a MaterialPropertyBlock on the renderers,
// restored exactly afterwards - no material is edited and nothing stays recoloured.

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
        private static readonly Dictionary<Transform, Coroutine> _active = new Dictionary<Transform, Coroutine>();

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

            if (_active.TryGetValue(part, out Coroutine running) && running != null)
                r.StopCoroutine(running);

            _active[part] = r.StartCoroutine(PulseRoutine(part, colour, seconds));
        }

        private static Runner EnsureRunner()
        {
            if (_runner != null) return _runner;
            var go = new GameObject("FeedbackPulse (runtime)");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
            return _runner;
        }

        private static IEnumerator PulseRoutine(Transform part, Color colour, float seconds)
        {
            var renderers = part.GetComponentsInChildren<Renderer>(false);
            var originals = new List<MaterialPropertyBlock>();
            foreach (Renderer rend in renderers)
            {
                var original = new MaterialPropertyBlock();
                rend.GetPropertyBlock(original);
                originals.Add(original);
            }

            float t = 0f;
            while (t < seconds)
            {
                // Quick rise, slow fall: reads as a flash, not a recolour.
                float k = t < seconds * 0.2f ? t / (seconds * 0.2f) : 1f - (t - seconds * 0.2f) / (seconds * 0.8f);
                k = Mathf.Clamp01(k);

                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer rend = renderers[i];
                    if (rend == null || rend.sharedMaterial == null) continue;
                    var block = new MaterialPropertyBlock();
                    rend.GetPropertyBlock(block);

                    Material m = rend.sharedMaterial;
                    if (m.HasProperty(ColorId))
                        block.SetColor(ColorId, Color.Lerp(m.GetColor(ColorId), colour, k));
                    else if (m.HasProperty(BaseColorId))
                        block.SetColor(BaseColorId, Color.Lerp(m.GetColor(BaseColorId), colour, k));
                    if (m.HasProperty(EmissionId))
                        block.SetColor(EmissionId, colour * (0.6f * k));

                    rend.SetPropertyBlock(block);
                }

                t += Time.deltaTime;
                yield return null;
            }

            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].SetPropertyBlock(originals[i]);

            _active.Remove(part);
        }
    }
}
