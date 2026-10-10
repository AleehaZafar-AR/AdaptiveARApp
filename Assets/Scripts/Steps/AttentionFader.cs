// File: AttentionFader.cs
// Presentation-only focus control for the READ state.
//
// While an instruction is being read the loose parts are darkened hard (property
// block, no material edited) and cannot be grabbed: their Interaction SDK components
// are disabled AND their interaction child objects (ISDK_*) are switched off, so no
// hand-grab or ray-grab path remains. Locked parts are never touched. Everything is
// restored exactly on leaving the state.
//
// Nothing here is research state: no identity, registry, validation, physics or
// progress change; nothing is logged except by the caller.

using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class AttentionFader : MonoBehaviour
    {
        [Tooltip("Multiplier applied to each part's colour while reading. 1 = untouched.")]
        [Range(0.05f, 1f)]
        [SerializeField] private float readDim = 0.28f;

        [Tooltip("Suspend grabbing of the loose parts while reading.")]
        [SerializeField] private bool suspendGrabWhileReading = true;

        [SerializeField] private bool logChanges = true;

        public bool IsReading { get; private set; }

        private GuidanceRegistry _registry;
        private readonly List<Renderer> _dimmed = new List<Renderer>();
        private readonly List<MaterialPropertyBlock> _dimmedOriginal = new List<MaterialPropertyBlock>();
        private readonly List<MonoBehaviour> _suspended = new List<MonoBehaviour>();
        private readonly List<GameObject> _suspendedObjects = new List<GameObject>();
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

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

        private void Apply()
        {
            Restore();
            if (_registry == null)
            {
                Debug.LogWarning("[Attention] READ requested but no registry is configured; nothing faded.");
                return;
            }

            int parts = 0;
            foreach (string key in _registry.PartKeys())
            {
                if (!_registry.TryResolveQuiet(key, out GameObject go) || go == null) continue;

                var padlock = go.GetComponent<PlacementLock>();
                bool locked = padlock != null && padlock.IsLocked;
                parts++;

                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || r.sharedMaterial == null) continue;

                    var original = new MaterialPropertyBlock();
                    r.GetPropertyBlock(original);

                    var block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block);
                    Material m = r.sharedMaterial;
                    bool any = false;
                    if (m.HasProperty(ColorId))
                    {
                        Color c = m.GetColor(ColorId);
                        block.SetColor(ColorId, new Color(c.r * readDim, c.g * readDim, c.b * readDim, c.a));
                        any = true;
                    }
                    else if (m.HasProperty(BaseColorId))
                    {
                        Color c = m.GetColor(BaseColorId);
                        block.SetColor(BaseColorId, new Color(c.r * readDim, c.g * readDim, c.b * readDim, c.a));
                        any = true;
                    }
                    if (m.HasProperty(EmissionId)) { block.SetColor(EmissionId, Color.black); any = true; }
                    if (!any) continue;

                    r.SetPropertyBlock(block);
                    _dimmed.Add(r);
                    _dimmedOriginal.Add(original);
                }

                if (!suspendGrabWhileReading || locked) continue;

                foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || !mb.enabled) continue;
                    string ns = mb.GetType().Namespace;
                    if (string.IsNullOrEmpty(ns) || !ns.StartsWith("Oculus.Interaction")) continue;
                    mb.enabled = false;
                    _suspended.Add(mb);
                }

                // The interaction child objects as well, so no interactor can find the part.
                foreach (Transform child in go.transform)
                {
                    if (child == null || !child.gameObject.activeSelf) continue;
                    if (!child.name.StartsWith("ISDK_")) continue;
                    child.gameObject.SetActive(false);
                    _suspendedObjects.Add(child.gameObject);
                }
            }

            if (logChanges)
                Debug.Log($"[Attention] READ: {parts} part(s), {_dimmed.Count} renderer(s) dimmed to {readDim:F2}, " +
                          $"{_suspended.Count} interaction component(s) and {_suspendedObjects.Count} ISDK object(s) suspended.");
        }

        private void Restore()
        {
            for (int i = 0; i < _dimmed.Count; i++)
                if (_dimmed[i] != null) _dimmed[i].SetPropertyBlock(_dimmedOriginal[i]);
            int n = _dimmed.Count;
            _dimmed.Clear();
            _dimmedOriginal.Clear();

            foreach (MonoBehaviour mb in _suspended)
            {
                if (mb == null) continue;
                var padlock = mb.GetComponentInParent<PlacementLock>();
                if (padlock != null && padlock.IsLocked) continue;   // locked meanwhile: stays locked
                mb.enabled = true;
            }
            _suspended.Clear();

            foreach (GameObject go in _suspendedObjects)
            {
                if (go == null) continue;
                var padlock = go.GetComponentInParent<PlacementLock>();
                if (padlock != null && padlock.IsLocked) continue;
                go.SetActive(true);
            }
            _suspendedObjects.Clear();

            if (logChanges && n > 0)
                Debug.Log("[Attention] INTERACT: parts restored, interaction re-enabled.");
        }

        private void OnDisable()
        {
            if (IsReading) { IsReading = false; Restore(); }
        }
    }
}
