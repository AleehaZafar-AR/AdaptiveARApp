// File: PistonAutoCompleter.cs
// The explicit prototype shortcut after the second piston: the remaining six piston
// assemblies are fitted into their bores automatically.
//
// The model has four real piston kits and eight bores (ghost groups piston001-004
// and piston001 (1)-004 (1)). The two unused real kits are snapped and locked into
// their bores; the four bores that have no real kit show the bore ghost group with
// the real parts' authored materials, so all eight bores read as fitted.
//
// This is not participant performance: nothing is validated, no attempt or error is
// recorded, and the instances are consumed so they can never satisfy a later action.
// One log line: remaining_pistons_auto_completed, count = 6.

using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public static class PistonAutoCompleter
    {
        private static readonly string[] Roles =
        {
            "PistonHead", "ConnectingRod", "ConnectingPin", "PistonEnd",
            "pistonBolt", "pistonBoltOther", "PistonNut", "PistonNutOther"
        };

        /// <summary>Returns how many bores were filled (real kits + solid ghost groups).</summary>
        public static int CompleteRemaining(GuidanceRegistry registry, StepValidator validator)
        {
            if (registry == null) return 0;

            int filled = 0;
            var materialByRole = new Dictionary<string, Material>();

            // 1. Real kits not yet used go into their own bores.
            for (int n = 1; n <= 4; n++)
            {
                string kit = $"PistonKit00{n}";
                if (registry.TryGetKitHandle(kit, out Transform bound) && bound != null)
                {
                    // Already built by the participant: remember its materials for the ghosts.
                    CollectMaterials(registry, kit, materialByRole);
                    continue;
                }

                Transform head = null;
                int placedParts = 0;
                foreach (string role in Roles)
                {
                    string key = $"part.{kit}.{role}";
                    if (validator != null && validator.IsConsumed(key)) continue;
                    if (!registry.TryResolveQuiet(key, out GameObject part) || part == null) continue;
                    if (!registry.TryResolveQuiet($"ghost.piston00{n}.{role}", out GameObject target) || target == null) continue;

                    var padlock = part.GetComponent<PlacementLock>();
                    if (padlock == null) padlock = part.AddComponent<PlacementLock>();
                    padlock.Unlock();
                    part.transform.SetPositionAndRotation(target.transform.position, target.transform.rotation);
                    padlock.Lock();
                    if (validator != null) validator.MarkConsumed(key);

                    var r = part.GetComponent<Renderer>();
                    if (r != null && !materialByRole.ContainsKey(role)) materialByRole[role] = r.sharedMaterial;

                    if (role == "PistonHead") { head = part.transform; registry.BindKitHandle(kit, head); }
                    else if (head != null && !part.transform.IsChildOf(head)) part.transform.SetParent(head, true);
                    if (padlock != null) padlock.RefreshLockedPose();
                    placedParts++;
                }

                if (placedParts > 0)
                {
                    filled++;
                    Debug.Log($"[AutoComplete] {kit}: {placedParts} part(s) fitted into bore piston00{n}.");
                }
            }

            // 2. Bores without a real kit: the bore ghost group, shown solid.
            for (int n = 1; n <= 4; n++)
            {
                string key = $"ghost.piston00{n} (1)";
                if (!registry.TryResolveQuiet(key, out GameObject group) || group == null) continue;

                foreach (Transform child in group.transform)
                {
                    var r = child.GetComponent<Renderer>();
                    if (r == null) continue;
                    if (materialByRole.TryGetValue(child.name, out Material m) && m != null) r.sharedMaterial = m;
                    foreach (Collider c in child.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                    child.gameObject.SetActive(true);
                }
                group.SetActive(true);
                filled++;
                Debug.Log($"[AutoComplete] bore piston00{n} (1): fitted as a solid group ({materialByRole.Count} material(s) matched).");
            }

            return filled;
        }

        private static void CollectMaterials(GuidanceRegistry registry, string kit, Dictionary<string, Material> into)
        {
            foreach (string role in Roles)
            {
                if (into.ContainsKey(role)) continue;
                if (!registry.TryResolveQuiet($"part.{kit}.{role}", out GameObject part) || part == null) continue;
                var r = part.GetComponent<Renderer>();
                if (r != null && r.sharedMaterial != null) into[role] = r.sharedMaterial;
            }
        }
    }
}
