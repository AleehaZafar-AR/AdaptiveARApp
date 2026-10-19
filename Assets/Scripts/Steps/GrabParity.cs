// File: GrabParity.cs
// Keeps hand and controller grabbing of the SAME part equivalent.
//
// The Interaction SDK finds interactables by physics triggers: at registration it adds an
// InteractableTriggerBroadcaster to the part (one per interactable: hand grab, controller
// grab). When an interactable is disabled and enabled again (a placement lock, a kit
// handle unlocked, a forwarded subassembly grab, an activation toggle) the SDK disables
// that broadcaster on unregister and does NOT re-enable it on re-register - the part is
// then invisible to that interactor type although its interactable is enabled. Which
// broadcaster dies depends on the order the two interactables were toggled, which is how
// a part ends up grabbable by hand but not by controller. ReassertBroadcasters() repairs
// that and logs the parity of both sides.

using UnityEngine;

namespace AdaptiveAR.Steps
{
    public static class GrabParity
    {
        public static void ReassertBroadcasters(GameObject part, string why, bool log = true)
        {
            if (part == null) return;
            int fixedCount = 0;
            foreach (var b in part.GetComponentsInChildren<Oculus.Interaction.InteractableTriggerBroadcaster>(true))
            {
                if (b == null || b.enabled) continue;
                var owner = b.GetComponent<Oculus.Interaction.Grabbable>();
                b.enabled = true;
                fixedCount++;
            }
            var rb = part.GetComponent<Rigidbody>();
            if (rb != null && rb.IsSleeping()) rb.WakeUp();
            if (log || fixedCount > 0) Describe(part, why, fixedCount);
        }

        public static void Describe(GameObject part, string why, int fixedCount = 0)
        {
            var hand = part.GetComponentInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true);
            var ctrl = part.GetComponentInChildren<Oculus.Interaction.GrabInteractable>(true);
            var broadcasters = part.GetComponentsInChildren<Oculus.Interaction.InteractableTriggerBroadcaster>(true);
            int bOn = 0;
            foreach (var b in broadcasters) if (b != null && b.enabled) bOn++;
            var rb = part.GetComponent<Rigidbody>();
            int colliders = 0, collidersOn = 0;
            foreach (Collider c in part.GetComponents<Collider>()) { colliders++; if (c.enabled) collidersOn++; }
            var grabbable = part.GetComponent<Oculus.Interaction.Grabbable>();

            Debug.Log($"[GrabParity] part={part.name}#{part.GetInstanceID()} why={why} " +
                      $"hand(interactable={(hand != null ? (hand.enabled ? "on" : "OFF") : "none")}) controller(interactable={(ctrl != null ? (ctrl.enabled ? "on" : "OFF") : "none")}) " +
                      $"triggerBroadcasters={bOn}/{broadcasters.Length} enabled (re-enabled {fixedCount}) grabbable={(grabbable != null ? (grabbable.enabled ? "on" : "OFF") : "none")} " +
                      $"rigidbody={(rb != null ? (rb.isKinematic ? "kinematic" : "dynamic") + (rb.IsSleeping() ? ",sleeping" : "") : "none")} layer={LayerMask.LayerToName(part.layer)} " +
                      $"colliders={collidersOn}/{colliders} parent={(part.transform.parent != null ? part.transform.parent.name : "<root>")} active={part.activeInHierarchy}");
            if (hand != null && ctrl != null && hand.enabled != ctrl.enabled)
                Debug.LogError($"[GrabParity] '{part.name}': hand and controller interactables differ (hand={hand.enabled}, controller={ctrl.enabled}).");
            if (broadcasters.Length > 0 && bOn < broadcasters.Length)
                Debug.LogError($"[GrabParity] '{part.name}': {broadcasters.Length - bOn} trigger broadcaster(s) still disabled - that interactor type cannot discover this part.");
        }
    }
}
