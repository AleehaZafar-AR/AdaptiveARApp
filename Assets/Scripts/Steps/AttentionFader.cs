// File: AttentionFader.cs
// Presentation-only focus control for the READ state.
//
// While an instruction is being read the larger loose parts are dimmed (property
// block; no material is edited) so attention goes to the panel and the component
// preview. Nothing is made un-grabbable: the instruction may say "pick it up", and
// picking it up is accepted. Small loose hardware - pins, bolts, nuts - is never
// dimmed: its authored colour is how a participant tells a bolt from a nut.
//
// Nothing here is research state: no identity, registry, validation, physics or
// progress change.

using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class AttentionFader : MonoBehaviour
    {
        [Tooltip("Multiplier applied to a dimmed part's colour while reading. 1 = untouched.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float readDim = 0.45f;

        [Tooltip("Roles never dimmed: small loose hardware the participant must be able to identify.")]
        [SerializeField] private string[] neverDimRoles = { "ConnectingPin", "pistonBolt", "pistonBoltOther", "PistonNut", "PistonNutOther" };

        [SerializeField] private bool logChanges = true;

        public bool IsReading { get; private set; }

        private GuidanceRegistry _registry;
        private readonly List<Renderer> _dimmed = new List<Renderer>();
        private readonly List<MaterialPropertyBlock> _dimmedOriginal = new List<MaterialPropertyBlock>();
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public void Configure(GuidanceRegistry registry)
        {
            _registry = registry;
        }

        public void SetRead(bool reading)
        {
            if (reading == IsReading) return;
            IsReading = reading;

            if (reading) Apply();
            else Restore();
        }

        private bool IsSmallHardware(string key)
        {
            string role = StepValidator.RoleNameOf(key);
            if (role == null || neverDimRoles == null) return false;
            foreach (string r in neverDimRoles) if (r == role) return true;
            return false;
        }

        private void Apply()
        {
            Restore();
            if (_registry == null) return;

            int parts = 0, skipped = 0;
            var seen = new HashSet<GameObject>();
            foreach (string key in _registry.PartKeys())
            {
                if (GuidanceRegistry.IsKitHandleKey(key)) continue;            // alias of a head
                if (!_registry.TryResolveQuiet(key, out GameObject go) || go == null) continue;
                if (!seen.Add(go)) continue;
                if (IsSmallHardware(key)) { skipped++; continue; }

                var padlock = go.GetComponent<PlacementLock>();
                if (padlock != null && padlock.IsLocked) continue;             // installed: leave it
                parts++;

                // Only this part's own renderer: a joined child keeps its own rule.
                var r = go.GetComponent<Renderer>();
                if (r == null || r.sharedMaterial == null) continue;

                var original = new MaterialPropertyBlock();
                r.GetPropertyBlock(original);
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
                _dimmedOriginal.Add(original);
            }

            if (logChanges)
                Debug.Log($"[Attention] READ: {_dimmed.Count} part(s) dimmed to {readDim:F2}, {skipped} small hardware part(s) left as authored, grabbing untouched.");
        }

        private void Restore()
        {
            for (int i = 0; i < _dimmed.Count; i++)
                if (_dimmed[i] != null) _dimmed[i].SetPropertyBlock(_dimmedOriginal[i]);
            int n = _dimmed.Count;
            _dimmed.Clear();
            _dimmedOriginal.Clear();

            if (logChanges && n > 0)
                Debug.Log("[Attention] INTERACT: part colours restored.");
        }

        private void OnDisable()
        {
            if (IsReading) { IsReading = false; Restore(); }
        }
    }
}
