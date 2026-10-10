// File: PistonAutoCompleter.cs
// The explicit prototype shortcut after the second piston: the remaining SIX bores
// are filled with visual completion geometry.
//
// These are not parts. Each is a renderer-only clone (MeshFilter + MeshRenderer, the
// real parts' shared materials) of a completed piston assembly, posed exactly at the
// bore's authored final poses taken from the bore ghost group. No Rigidbody, no
// collider, no Interaction SDK, no registry key: they cannot be grabbed, validated,
// consumed, counted, or push anything. The two unused real kits stay loose in the
// tray, untouched.
//
// Logged once: remaining_pistons_auto_completed count=6, after
// "manualCompleted=2 autoCompleted=6 visualTotal=8".

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

        /// <summary>Bore ghost groups that still need a visual piston, in order.</summary>
        private static readonly string[] BoreKeys =
        {
            "ghost.piston003", "ghost.piston004",
            "ghost.piston001 (1)", "ghost.piston002 (1)", "ghost.piston003 (1)", "ghost.piston004 (1)"
        };

        public const int ExpectedCount = 6;

        /// <summary>Returns how many visual assemblies were created.</summary>
        public static int CompleteRemaining(GuidanceRegistry registry, int manualCompleted)
        {
            if (registry == null) return 0;

            // Source geometry per role: the real part of a participant-built kit (bound
            // handle first), else any real instance of the role. Mesh + materials only.
            var source = new Dictionary<string, (Mesh mesh, Material[] mats, Vector3 scale)>();
            for (int n = 1; n <= 4 && source.Count < Roles.Length; n++)
            {
                foreach (string role in Roles)
                {
                    if (source.ContainsKey(role)) continue;
                    if (!registry.TryResolveQuiet($"part.PistonKit00{n}.{role}", out GameObject part) || part == null) continue;
                    var mf = part.GetComponent<MeshFilter>();
                    var mr = part.GetComponent<MeshRenderer>();
                    if (mf == null || mf.sharedMesh == null || mr == null) continue;
                    source[role] = (mf.sharedMesh, mr.sharedMaterials, part.transform.lossyScale);
                }
            }

            Transform parent = new GameObject("AutoCompletedPistons (visual only)").transform;
            // Live under the engine so a reposition moves them with it.
            if (registry.TryResolveQuiet("ghost.piston001", out GameObject anyBore) && anyBore != null && anyBore.transform.parent != null)
                parent.SetParent(anyBore.transform.parent, false);

            int created = 0;
            foreach (string boreKey in BoreKeys)
            {
                if (!registry.TryResolveQuiet(boreKey, out GameObject bore) || bore == null)
                {
                    Debug.LogWarning($"[AutoComplete] bore '{boreKey}' not registered; skipped.");
                    continue;
                }

                var group = new GameObject("VisualPiston (" + boreKey + ")").transform;
                group.SetParent(parent, false);
                int partsMade = 0;

                foreach (string role in Roles)
                {
                    Transform target = bore.transform.Find(role);
                    if (target == null || !source.TryGetValue(role, out var src)) continue;

                    var go = new GameObject(role);
                    go.transform.SetParent(group, false);
                    go.AddComponent<MeshFilter>().sharedMesh = src.mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = src.mats;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                    // Exact authored final pose, world scale of the real part.
                    go.transform.SetPositionAndRotation(target.position, target.rotation);
                    Vector3 ls = target.lossyScale;
                    Vector3 pl = parent.lossyScale;
                    go.transform.localScale = new Vector3(ls.x / Mathf.Max(1e-6f, pl.x), ls.y / Mathf.Max(1e-6f, pl.y), ls.z / Mathf.Max(1e-6f, pl.z));
                    partsMade++;
                }

                if (partsMade == 0) { Object.Destroy(group.gameObject); continue; }
                created++;
                Debug.Log($"[AutoComplete] visual piston at {boreKey}: {partsMade} part(s), no physics, no interaction.");
            }

            Debug.Log($"[AutoComplete] manualCompleted={manualCompleted} autoCompleted={created} visualTotal={manualCompleted + created}" +
                      (created != ExpectedCount ? $"  <-- expected {ExpectedCount}" : ""));
            return created;
        }
    }
}
