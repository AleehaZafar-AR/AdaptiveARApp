// File: AttentionFader.cs
// Presentation-only focus control for the READ state.
//
// While an instruction is being read, the loose parts are dimmed and cannot be
// grabbed, so attention stays on the panel. Dimming is done with a
// MaterialPropertyBlock on each renderer - no material, object, collider, Rigidbody
// or registry entry is touched, and clearing the block restores the exact original
// look. Grab is suspended by disabling the Interaction SDK components on the loose
// parts (never on locked ones) and re-enabling the same components afterwards.
//
// Nothing here is research state: it does not consume parts, alter validation,
// change physics, or write the log.

using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class AttentionFader : MonoBehaviour
    {
        [Tooltip("Multiplier applied to each part's colour while reading. 1 = untouched.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float readDim = 0.45f;

        [Tooltip("Suspend grabbing of the loose parts while reading.")]
        [SerializeField] private bool suspendGrabWhileReading = true;

        public bool IsReading { get; private set; }

        private GuidanceRegistry _registry;
        private readonly List<Renderer> _dimmed = new List<Renderer>();
        private readonly List<MonoBehaviour> _suspended = new List<MonoBehaviour>();
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public void Configure(GuidanceRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Enters or leaves the READ state. Safe to call repeatedly.</summary>
        public void SetRead(bool reading)
        {
            if (reading == IsReading) return;
            IsReading = reading;

            if (reading) Apply();
            else Restore();
        }

        private void Apply()
        {
            Restore();
            if (_registry == null) return;

            foreach (string key in _registry.PartKeys())
            {
                if (!_registry.TryResolveQuiet(key, out GameObject go) || go == null) continue;

                var padlock = go.GetComponent<PlacementLock>();
                bool locked = padlock != null && padlock.IsLocked;

                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || r.sharedMaterial == null) continue;
                    var block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block);

                    Material m = r.sharedMaterial;
                    if (m.HasProperty(ColorId))
                    {
                        Color c = m.GetColor(ColorId);
                        block.SetColor(ColorId, new Color(c.r * readDim, c.g * readDim, c.b * readDim, c.a));
                    }
                    else if (m.HasProperty(BaseColorId))
                    {
                        Color c = m.GetColor(BaseColorId);
                        block.SetColor(BaseColorId, new Color(c.r * readDim, c.g * readDim, c.b * readDim, c.a));
                    }
                    else continue;

                    r.SetPropertyBlock(block);
                    _dimmed.Add(r);
                }

                if (suspendGrabWhileReading && !locked)
                {
                    foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null || !mb.enabled) continue;
                        string ns = mb.GetType().Namespace;
                        if (string.IsNullOrEmpty(ns) || !ns.StartsWith("Oculus.Interaction")) continue;
                        mb.enabled = false;
                        _suspended.Add(mb);
                    }
                }
            }
        }

        private void Restore()
        {
            foreach (Renderer r in _dimmed)
            {
                if (r == null) continue;
                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block);
                block.Clear();
                r.SetPropertyBlock(block);
            }
            _dimmed.Clear();

            foreach (MonoBehaviour mb in _suspended)
            {
                if (mb == null) continue;
                // A part locked while suspended stays locked: PlacementLock owns it now.
                var padlock = mb.GetComponentInParent<PlacementLock>();
                if (padlock != null && padlock.IsLocked) continue;
                mb.enabled = true;
            }
            _suspended.Clear();
        }

        private void OnDisable()
        {
            if (IsReading) { IsReading = false; Restore(); }
        }
    }
}
