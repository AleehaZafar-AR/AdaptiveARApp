// File: PistonAssembly.cs
// Tracks one piston being built from its component parts, and turns the finished
// result into a single object that can be installed in the engine.
//
// The piston is no longer a preassembled thing to drop into a bore. The participant
// fits the head, the rod, the connecting pin, the rod end and its fastener, and only
// then installs the assembly. That means the manipulation unit CHANGES during the
// stage: individual components first, the whole assembly afterwards.
//
// This component owns that change. As each component is validated and locked it is
// parented into the kit, so the growing assembly moves as one. When every required
// component is in, the kit itself becomes grabbable and the install action can run.
//
// It watches PlacementLock state rather than subscribing to the validator, so it
// stays correct no matter which action completed a part or in what order.

using System;
using System.Collections.Generic;
using AdaptiveAR.Logging;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class PistonAssembly : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Registry key of the finished assembly, e.g. \"part.pistonKit001\".")]
        [SerializeField] private string assemblyKey;

        [Header("Required components")]
        [Tooltip("The parts that must be placed before the assembly counts as built. " +
                 "Fastener parts that are authored but disabled are deliberately not listed.")]
        [SerializeField] private List<Transform> requiredParts = new List<Transform>();

        [Header("Behaviour")]
        [Tooltip("Parent each validated component into the kit so the growing assembly " +
                 "moves as one object.")]
        [SerializeField] private bool adoptOnLock = true;

        [Tooltip("Enable the kit's own grab components once assembly finishes, so the whole " +
                 "piston becomes the thing being manipulated.")]
        [SerializeField] private bool enableKitGrabWhenComplete = true;

        [Header("Logging")]
        [SerializeField] private SessionLogger logger;

        [Header("Debug")]
        [SerializeField] private bool logProgress = true;

        /// <summary>Raised once, when every required component has been placed.</summary>
        public event Action OnAssemblyComplete;

        public bool IsComplete { get; private set; }

        /// <summary>How many required components are in place.</summary>
        public int PlacedCount { get; private set; }

        public int RequiredCount { get { return requiredParts != null ? requiredParts.Count : 0; } }

        private readonly HashSet<Transform> _adopted = new HashSet<Transform>();
        private float _timer;

        private void Start()
        {
            // The kit is not grabbable until it is something worth grabbing.
            if (enableKitGrabWhenComplete)
                SetKitInteraction(false);
        }

        private void Update()
        {
            if (IsComplete) return;

            // Polled rather than event-driven: correct regardless of which action placed a
            // part, in what order, or whether a researcher skipped one.
            _timer += Time.deltaTime;
            if (_timer < 0.2f) return;
            _timer = 0f;

            Evaluate();
        }

        private void Evaluate()
        {
            int placed = 0;

            foreach (Transform t in requiredParts)
            {
                if (t == null) continue;

                var padlock = t.GetComponent<PlacementLock>();
                bool locked = padlock != null && padlock.IsLocked;
                if (!locked) continue;

                placed++;

                if (adoptOnLock && !_adopted.Contains(t))
                {
                    // Keep the world pose: the part was snapped to its target and must stay there.
                    t.SetParent(transform, true);
                    _adopted.Add(t);

                    if (logProgress)
                        Debug.Log($"[PistonAssembly] '{t.name}' joined {name} ({placed}/{RequiredCount}).");
                }
            }

            PlacedCount = placed;

            if (RequiredCount > 0 && placed >= RequiredCount)
                Complete();
        }

        private void Complete()
        {
            IsComplete = true;

            if (enableKitGrabWhenComplete)
                SetKitInteraction(true);

            if (logger != null)
                logger.LogNote($"piston_assembly_complete:{(string.IsNullOrEmpty(assemblyKey) ? name : assemblyKey)}");

            if (logProgress)
                Debug.Log($"[PistonAssembly] {name} assembled from {RequiredCount} component(s); " +
                          "it can now be installed as one unit.");

            OnAssemblyComplete?.Invoke();
        }

        /// <summary>
        /// Turns the kit's own Interaction SDK components on or off. Found by namespace so
        /// this does not bind to a specific SDK version or grab component type.
        /// </summary>
        private void SetKitInteraction(bool enabled)
        {
            foreach (MonoBehaviour mb in GetComponents<MonoBehaviour>())
            {
                if (mb == null || mb == this) continue;

                string ns = mb.GetType().Namespace;
                if (!string.IsNullOrEmpty(ns) && ns.StartsWith("Oculus.Interaction"))
                    mb.enabled = enabled;
            }

            // The kit body must not be pushed around while its parts are still being fitted.
            var body = GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = !enabled;
        }

        /// <summary>Used by the editor tool to define the kit without hand-wiring.</summary>
        public void Configure(string key, List<Transform> parts, SessionLogger sessionLogger)
        {
            assemblyKey = key;
            requiredParts = parts;
            logger = sessionLogger;
        }
    }
}
