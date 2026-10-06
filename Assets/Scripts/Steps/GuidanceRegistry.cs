// File: GuidanceRegistry.cs
// Bridges ScriptableObject step data to scene objects.
//
// A ScriptableObject cannot hold a scene reference, so StepData refers to
// guidance objects by string key and this scene component resolves the key to
// the actual GameObject. That lets a step address the 20 ghosts already placed
// and aligned under EngineAnchor/Offset/Ghosties without adding new prefabs or
// restructuring the scene.
//
// Convention binding
// ------------------
// Keys that have no scene target are resolved by naming convention against the
// CURRENT hierarchy, so a key does not go dead when objects are reorganised in the
// Editor:
//
//   part.PistonKit001.ConnectingRod  ->  <part.piston001 target> / ConnectingRod
//   part.PistonKit001                ->  <part.piston001 target> / PistonHead  (the kit handle)
//   ghost.piston001.PistonHead       ->  <ghost.piston001 target> / PistonHead
//
// Every auto-binding is logged once, so a wrong guess is visible rather than silent.

using System;
using System.Text.RegularExpressions;
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

            [Tooltip("Keep this object out of the presenter's show/hide cycle. Used for the " +
                     "physical parts the operator manipulates: they are looked up by key for " +
                     "validation, but must never be hidden between steps. " +
                     "Default false preserves the behaviour of existing ghost entries.")]
            public bool excludeFromAutoHide;
        }

        [Tooltip("Key -> scene object bindings, populated once in the Inspector.")]
        [SerializeField] private List<Entry> entries = new List<Entry>();

        [Tooltip("Deactivate every registered target on Awake so guidance starts hidden.")]
        [SerializeField] private bool deactivateAllOnAwake = false;

        [Tooltip("Log a warning when a step references a key that is not registered.")]
        [SerializeField] private bool warnOnMissingKey = true;

        [Header("Convention binding")]
        [Tooltip("Resolve unbound keys by naming convention against the live hierarchy.")]
        [SerializeField] private bool bindByConvention = true;

        [Tooltip("Child that acts as the handle of an assembled kit: the part grabbed to " +
                 "move the whole assembly once its components are joined to it.")]
        [SerializeField] private string kitHandleChildName = "PistonHead";

        private readonly Dictionary<string, GameObject> _lookup = new Dictionary<string, GameObject>();
        private readonly HashSet<string> _autoBound = new HashSet<string>();
        private readonly HashSet<string> _autoBindFailed = new HashSet<string>();

        private static readonly Regex KitChildKey = new Regex(@"^part\.PistonKit(\d+)\.(.+)$", RegexOptions.Compiled);
        private static readonly Regex KitHandleKey = new Regex(@"^part\.PistonKit(\d+)$", RegexOptions.Compiled);
        private static readonly Regex ChildKey = new Regex(@"^(ghost|part)\.([^.]+)\.(.+)$", RegexOptions.Compiled);

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
                    // Deliberately quiet for convention keys: they bind on first use.
                    if (!bindByConvention || !IsConventionKey(entry.key))
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

            if (TryBindByConvention(key, out target))
                return true;

            if (warnOnMissingKey)
                Debug.LogWarning($"[GuidanceRegistry] No target registered for key '{key}'.", this);

            return false;
        }

        /// <summary>
        /// Same as TryResolve but never logs. For validation and tooling passes that
        /// probe for keys and expect misses.
        /// </summary>
        public bool TryResolveQuiet(string key, out GameObject target)
        {
            target = null;
            if (string.IsNullOrEmpty(key)) return false;

            if (_lookup.Count == 0 && entries.Count > 0)
                BuildLookup();

            if (_lookup.TryGetValue(key, out target) && target != null)
                return true;

            return TryBindByConvention(key, out target);
        }

        /// <summary>
        /// The kit a part key belongs to, e.g. "part.PistonKit001.ConnectingRod" -> "PistonKit001".
        /// Null for keys that are not kit components.
        /// </summary>
        public static string KitOf(string partKey)
        {
            if (string.IsNullOrEmpty(partKey)) return null;
            Match m = KitChildKey.Match(partKey);
            return m.Success ? "PistonKit" + m.Groups[1].Value : null;
        }

        /// <summary>True for "part.PistonKit001": the key that names a whole assembled kit.</summary>
        public static bool IsKitHandleKey(string partKey)
        {
            return !string.IsNullOrEmpty(partKey) && KitHandleKey.IsMatch(partKey);
        }

        /// <summary>
        /// Resolves the handle of the kit a component key belongs to, i.e. the object the
        /// finished assembly is grabbed and installed by. False when the key is not a kit
        /// component or the handle cannot be found.
        /// </summary>
        public bool TryResolveKitHandle(string partKey, out Transform handle)
        {
            handle = null;
            string kit = KitOf(partKey);
            if (kit == null) return false;

            if (TryResolveQuiet("part." + kit, out GameObject go) && go != null)
            {
                handle = go.transform;
                return true;
            }
            return false;
        }

        private static bool IsConventionKey(string key)
        {
            return KitChildKey.IsMatch(key) || KitHandleKey.IsMatch(key) || ChildKey.IsMatch(key);
        }

        private bool TryBindByConvention(string key, out GameObject target)
        {
            target = null;
            if (!bindByConvention || string.IsNullOrEmpty(key) || _autoBindFailed.Contains(key))
                return false;

            GameObject found = null;

            // part.PistonKitNNN.Child -> part.pistonNNN / Child
            Match m = KitChildKey.Match(key);
            if (m.Success)
            {
                string rootKey = "part.piston" + m.Groups[1].Value;
                if (_lookup.TryGetValue(rootKey, out GameObject root) && root != null)
                    found = FindChildByName(root.transform, m.Groups[2].Value);
            }

            // part.PistonKitNNN -> part.pistonNNN / <handle>
            if (found == null)
            {
                m = KitHandleKey.Match(key);
                if (m.Success)
                {
                    string rootKey = "part.piston" + m.Groups[1].Value;
                    if (_lookup.TryGetValue(rootKey, out GameObject root) && root != null)
                        found = FindChildByName(root.transform, kitHandleChildName);
                }
            }

            // ghost.X.Child / part.X.Child -> <ghost.X or part.X> / Child
            if (found == null)
            {
                m = ChildKey.Match(key);
                if (m.Success)
                {
                    string parentKey = m.Groups[1].Value + "." + m.Groups[2].Value;
                    if (_lookup.TryGetValue(parentKey, out GameObject parent) && parent != null)
                        found = FindChildByName(parent.transform, m.Groups[3].Value);
                }
            }

            if (found == null)
            {
                _autoBindFailed.Add(key);
                return false;
            }

            _lookup[key] = found;
            _autoBound.Add(key);
            target = found;

            Debug.Log($"[GuidanceRegistry] '{key}' bound by convention to '{PathOf(found.transform)}'.", this);
            return true;
        }

        private static GameObject FindChildByName(Transform parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName)) return null;

            // Direct children first (the normal case), then any depth.
            Transform direct = parent.Find(childName);
            if (direct != null) return direct.gameObject;

            foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
                if (t != parent && t.name == childName) return t.gameObject;

            return null;
        }

        private static string PathOf(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Deactivates every registered target. Used to guarantee a clean slate
        /// before presenting a step/level.
        /// </summary>
        public void DeactivateAll()
        {
            foreach (Entry entry in entries)
            {
                if (entry == null || entry.target == null)
                    continue;

                // Parts the operator manipulates are registered for lookup only.
                if (entry.excludeFromAutoHide)
                    continue;

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
