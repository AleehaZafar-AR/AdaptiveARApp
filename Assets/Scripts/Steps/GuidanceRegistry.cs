// File: GuidanceRegistry.cs
// Bridges ScriptableObject step data to scene objects.
//
// A ScriptableObject cannot hold a scene reference, so StepData refers to
// guidance objects by string key and this scene component resolves the key to
// the actual GameObject. That lets a step address the 20 ghosts already placed
// and aligned under EngineAnchor/Offset/Ghosties without adding new prefabs or
// restructuring the scene.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class GuidanceRegistry : MonoBehaviour
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("Stable key referenced from StepData, e.g. \"ghost.crankshaft\".")]
            public string key;

            [Tooltip("Scene object this key resolves to.")]
            public GameObject target;
        }

        [Tooltip("Key -> scene object bindings, populated once in the Inspector.")]
        [SerializeField] private List<Entry> entries = new List<Entry>();

        [Tooltip("Deactivate every registered target on Awake so guidance starts hidden.")]
        [SerializeField] private bool deactivateAllOnAwake = false;

        [Tooltip("Log a warning when a step references a key that is not registered.")]
        [SerializeField] private bool warnOnMissingKey = true;

        private readonly Dictionary<string, GameObject> _lookup = new Dictionary<string, GameObject>();

        private void Awake()
        {
            BuildLookup();

            if (deactivateAllOnAwake)
                DeactivateAll();
        }

        private void BuildLookup()
        {
            _lookup.Clear();

            foreach (Entry entry in entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.key))
                    continue;

                if (entry.target == null)
                {
                    Debug.LogWarning($"[GuidanceRegistry] Key '{entry.key}' has no target assigned.", this);
                    continue;
                }

                if (_lookup.ContainsKey(entry.key))
                {
                    Debug.LogWarning($"[GuidanceRegistry] Duplicate key '{entry.key}' ignored.", this);
                    continue;
                }

                _lookup.Add(entry.key, entry.target);
            }
        }

        /// <summary>
        /// Resolves a key to its scene object. Returns false when the key is not registered.
        /// </summary>
        public bool TryResolve(string key, out GameObject target)
        {
            target = null;
            if (string.IsNullOrEmpty(key))
                return false;

            if (_lookup.Count == 0 && entries.Count > 0)
                BuildLookup(); // resolve before Awake (e.g. edit-mode or early call)

            if (_lookup.TryGetValue(key, out target) && target != null)
                return true;

            if (warnOnMissingKey)
                Debug.LogWarning($"[GuidanceRegistry] No target registered for key '{key}'.", this);

            return false;
        }

        /// <summary>
        /// Deactivates every registered target. Used to guarantee a clean slate
        /// before presenting a step/level.
        /// </summary>
        public void DeactivateAll()
        {
            foreach (Entry entry in entries)
            {
                if (entry != null && entry.target != null)
                    entry.target.SetActive(false);
            }
        }

        /// <summary>Number of usable key bindings. Useful for an on-device readout.</summary>
        public int RegisteredCount
        {
            get
            {
                if (_lookup.Count == 0 && entries.Count > 0)
                    BuildLookup();
                return _lookup.Count;
            }
        }
    }
}
