// File: StepIntroInteractionGate.cs
// The step-introduction gate at the Interaction SDK level.
//
// Between step entry and the Continue press the incoming step's actionable parts must not be
// grabbable or selectable at all. Clearing the validator only stops them being VALIDATED, and
// AttentionFader only dims them: on the 2026-10-28 Quest run a participant could still pick up
// and move step parts before Continue. This gate disables every Oculus.Interaction component
// under each actionable part (the same mechanism PlacementLock uses to close an installed part)
// and re-enables exactly the components it disabled when the gate is released, so Continue
// restores the pre-intro interaction state and enters the first action in the same call.
//
// Scope: only the parts the step's enabled PHYSICAL actions accept (primary key, interchangeable
// keys, either-side equivalents, same-role instances in every kit) plus the kit handle a kit
// component is built on. Nothing else in the environment is touched. Installed parts (closed by
// a non-repositionable PlacementLock) are left to their lock.

using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public sealed class StepIntroInteractionGate
    {
        private struct Entry { public GameObject part; public MonoBehaviour component; }

        private readonly List<Entry> _disabled = new List<Entry>();
        private readonly List<GameObject> _parts = new List<GameObject>();

        public bool Engaged { get; private set; }
        public string StepId { get; private set; }
        public int PartCount { get { return _parts.Count; } }
        public int ComponentCount { get { return _disabled.Count; } }
        /// <summary>Parts that were selected (held) when the gate engaged; their grabs were cancelled.</summary>
        public int HeldAtEngage { get; private set; }

        /// <summary>
        /// Every scene object a step's enabled physical actions can accept, resolved the way the
        /// validator resolves candidates, plus the kit handle of any kit component. Keys are
        /// appended to keysOut when given.
        /// </summary>
        public static List<GameObject> ResolveStepParts(StepData step, GuidanceRegistry registry, List<string> keysOut = null)
        {
            var parts = new List<GameObject>();
            if (step == null || registry == null || step.actions == null) return parts;

            var keys = new List<string>();
            foreach (AssemblyAction a in step.actions)
            {
                if (a == null || !a.RequiresPhysicalValidation) continue;

                foreach (string key in a.AcceptedPartKeys())
                    foreach (string eq in GuidanceAudit.ExpandEquivalent(key))
                        foreach (string k in registry.RoleCandidates(eq))
                            if (!keys.Contains(k)) keys.Add(k);

                // A kit component is built on (or installed by) its kit's handle; the handle is
                // grabbable by design during the action, so it is a step part too.
                if (GuidanceRegistry.KitOf(a.partKey) != null && registry.TryResolveKitHandle(a.partKey, out Transform handle) && handle != null)
                    AddUnique(parts, handle.gameObject);
            }

            foreach (string k in keys)
                if (registry.TryResolveQuiet(k, out GameObject go) && go != null) AddUnique(parts, go);

            if (keysOut != null) keysOut.AddRange(keys);
            return parts;
        }

        private static void AddUnique(List<GameObject> list, GameObject go)
        {
            if (go != null && !list.Contains(go)) list.Add(go);
        }

        /// <summary>
        /// Disables the interaction components of the step's actionable parts. A part held at
        /// this moment is released by the SDK's own cancel path (disabling its PointableElement
        /// cancels every selecting point). Idempotent: an engaged gate is released first.
        /// </summary>
        public void Engage(StepData step, GuidanceRegistry registry, string why)
        {
            if (Engaged) Release("re-engage:" + why);

            StepId = step != null ? step.StepIdentifier : null;
            HeldAtEngage = 0;
            var keys = new List<string>();
            var skipped = new List<string>();

            foreach (GameObject part in ResolveStepParts(step, registry, keys))
            {
                if (part == null) continue;

                var padlock = part.GetComponent<PlacementLock>();
                if (padlock != null && padlock.IsLocked && !padlock.Repositionable)
                {
                    skipped.Add(part.name + "(installed)");
                    continue;                                   // its lock owns its interaction state
                }

                var grabbable = part.GetComponent<Oculus.Interaction.Grabbable>();
                if (grabbable != null && grabbable.SelectingPointsCount > 0) HeldAtEngage++;

                int n = 0;
                foreach (MonoBehaviour mb in part.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || !mb.enabled) continue;
                    string ns = mb.GetType().Namespace;
                    if (string.IsNullOrEmpty(ns) || !ns.StartsWith("Oculus.Interaction")) continue;
                    mb.enabled = false;
                    _disabled.Add(new Entry { part = part, component = mb });
                    n++;
                }
                if (n > 0) _parts.Add(part);
            }

            Engaged = true;

            var names = new List<string>();
            foreach (GameObject p in _parts) names.Add(p.name);
            Debug.Log($"[StepIntroGate] engaged ({why}) for '{StepId}': {_parts.Count} part(s) closed at the SDK level, {_disabled.Count} component(s) disabled, " +
                      $"{HeldAtEngage} held grab(s) cancelled; parts=[{string.Join(", ", names)}]; keys=[{string.Join(", ", keys)}]" +
                      (skipped.Count > 0 ? $"; left to their lock=[{string.Join(", ", skipped)}]" : ""));
        }

        /// <summary>
        /// Re-enables exactly the components the gate disabled (a part locked meanwhile keeps
        /// its lock's state) and repairs the SDK trigger broadcasters, as PlacementLock does.
        /// </summary>
        public void Release(string why)
        {
            if (!Engaged && _disabled.Count == 0) return;

            int restored = 0, kept = 0;
            foreach (Entry e in _disabled)
            {
                if (e.component == null || e.part == null) continue;
                var padlock = e.part.GetComponent<PlacementLock>();
                if (padlock != null && padlock.IsLocked && !padlock.Repositionable) { kept++; continue; }
                if (!e.component.enabled) { e.component.enabled = true; restored++; }
            }
            foreach (GameObject part in _parts)
                if (part != null) GrabParity.ReassertBroadcasters(part, "step intro gate released", false);

            Debug.Log($"[StepIntroGate] released ({why}) for '{StepId}': {restored} component(s) re-enabled on {_parts.Count} part(s)" +
                      (kept > 0 ? $", {kept} left disabled by a lock" : "") + ".");

            _disabled.Clear();
            _parts.Clear();
            Engaged = false;
            HeldAtEngage = 0;
        }
    }
}
