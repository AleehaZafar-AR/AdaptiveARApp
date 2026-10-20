// File: GrabParity.cs
// Keeps hand and controller grabbing of the SAME part equivalent, and proves it in the log.
//
// Every assembly part carries two Interaction SDK interactables: HandGrabInteractable (hands)
// and GrabInteractable (controllers). Both are discovered through the same trigger-based
// registry on the part's Rigidbody and both must point at the part's own Grabbable. When a
// loose part settles (dropped, fallen into a tray, recovered) the controller side is compared
// with the hand side field by field - [ControllerRegrab] - and any difference on the
// controller side is repaired to match the hand side, which is the working reference.

using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public static class GrabParity
    {
        private static Oculus.Interaction.GrabInteractor _controller;
        private static Oculus.Interaction.HandGrab.HandGrabInteractor _hand;

        private static void FindInteractors()
        {
            if (_controller == null) _controller = Object.FindAnyObjectByType<Oculus.Interaction.GrabInteractor>(FindObjectsInactive.Include);
            if (_hand == null) _hand = Object.FindAnyObjectByType<Oculus.Interaction.HandGrab.HandGrabInteractor>(FindObjectsInactive.Include);
        }

        private static string MaskOf(Component interactor, int partLayer)
        {
            if (interactor == null) return "none";
            int layer = interactor.gameObject.layer;
            var rb = interactor.GetComponent<Rigidbody>();
            if (rb != null) layer = rb.gameObject.layer;
            bool collides = !Physics.GetIgnoreLayerCollision(layer, partLayer);
            return $"{LayerMask.LayerToName(layer)}({layer})->part:{(collides ? "collides" : "IGNORED")}";
        }

        /// <summary>
        /// The moment a loose part comes to rest: one [ControllerRegrab] line with every field the
        /// controller path depends on, then the controller side is made identical to the hand side.
        /// </summary>
        public static void OnSettled(GameObject part, string why)
        {
            if (part == null) return;
            FindInteractors();
            var hand = part.GetComponentInChildren<Oculus.Interaction.HandGrab.HandGrabInteractable>(true);
            var ctrl = part.GetComponentInChildren<Oculus.Interaction.GrabInteractable>(true);
            var grabbable = part.GetComponent<Oculus.Interaction.Grabbable>();
            var rb = part.GetComponent<Rigidbody>();
            var col = part.GetComponent<Collider>();

            Debug.Log($"[ControllerRegrab] part={part.name}#{part.GetInstanceID()} why={why} " +
                      $"GrabInteractable.enabled={(ctrl != null ? ctrl.enabled.ToString() : "NONE")} " +
                      $"HandGrabInteractable.enabled={(hand != null ? hand.enabled.ToString() : "NONE")} " +
                      $"Grabbable.enabled={(grabbable != null ? grabbable.enabled.ToString() : "NONE")} " +
                      $"Rigidbody.isKinematic={(rb != null ? rb.isKinematic.ToString() : "NONE")} " +
                      $"Rigidbody.sleeping={(rb != null ? rb.IsSleeping().ToString() : "NONE")} " +
                      $"layer={LayerMask.LayerToName(part.layer)}({part.layer}) " +
                      $"collider.enabled={(col != null ? col.enabled.ToString() : "NONE")} collider.isTrigger={(col != null ? col.isTrigger.ToString() : "NONE")} " +
                      $"parent={(part.transform.parent != null ? part.transform.parent.name : "<root>")} active={part.activeInHierarchy} " +
                      $"controllerMask={MaskOf(_controller, part.layer)} handMask={MaskOf(_hand, part.layer)} " +
                      $"ctrlPointable={(ctrl != null && ctrl.PointableElement != null ? ((Component)ctrl.PointableElement).name + "#" + ((Component)ctrl.PointableElement).GetInstanceID() : "NONE")} " +
                      $"handPointable={(hand != null && hand.PointableElement != null ? ((Component)hand.PointableElement).name + "#" + ((Component)hand.PointableElement).GetInstanceID() : "NONE")} " +
                      $"ctrlRigidbody={(ctrl != null && ctrl.Rigidbody != null ? ctrl.Rigidbody.name : "NONE")} handRigidbody={(hand != null && hand.Rigidbody != null ? hand.Rigidbody.name : "NONE")}");

            RepairControllerSide(part, hand, ctrl, rb);
        }

        /// <summary>Controller interactable := hand interactable (enabled, Grabbable, Rigidbody); re-registered if anything changed.</summary>
        public static void RepairControllerSide(GameObject part, Oculus.Interaction.HandGrab.HandGrabInteractable hand, Oculus.Interaction.GrabInteractable ctrl, Rigidbody rb)
        {
            if (hand == null || ctrl == null) return;
            var fixes = new List<string>();
            if (hand.PointableElement != null && !ReferenceEquals(ctrl.PointableElement, hand.PointableElement))
            { ctrl.InjectOptionalPointableElement(hand.PointableElement); fixes.Add("pointable:=hand's"); }
            if (hand.Rigidbody != null && ctrl.Rigidbody != hand.Rigidbody)
            { ctrl.InjectRigidbody(hand.Rigidbody); fixes.Add("rigidbody:=hand's"); }
            if (ctrl.enabled != hand.enabled)
            { ctrl.enabled = hand.enabled; fixes.Add($"enabled:={hand.enabled}"); }
            else if (fixes.Count > 0 && ctrl.enabled)
            { ctrl.enabled = false; ctrl.enabled = true; fixes.Add("re-registered"); }   // the registry re-reads Rigidbody/colliders on enable
            if (rb != null && rb.IsSleeping()) rb.WakeUp();
            if (fixes.Count > 0) Debug.LogError($"[ControllerRegrab] '{part.name}': controller side differed from the hand side - repaired [{string.Join(", ", fixes)}]");
        }

        /// <summary>Kept for the earlier call sites: enables any disabled trigger broadcaster, wakes the body, logs parity.</summary>
        public static void ReassertBroadcasters(GameObject part, string why, bool log = true)
        {
            if (part == null) return;
            int fixedCount = 0;
            foreach (var b in part.GetComponentsInChildren<Oculus.Interaction.InteractableTriggerBroadcaster>(true))
            {
                if (b == null || b.enabled) continue;
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
        }
    }
}
