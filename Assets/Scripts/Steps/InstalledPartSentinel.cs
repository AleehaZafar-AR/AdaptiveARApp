// File: InstalledPartSentinel.cs
// A placed (locked) part is installed assembly geometry for the rest of the session. This
// sentinel sits on it from the moment it locks and:
//   - logs WHO deactivates it: OnDisable fires on SetActive(false) of the part or of any
//     ancestor, and the stack trace in that log line names the caller
//     ([HolderHideAttempt] object= caller= blocked=false), then re-activates it next frame;
//   - logs WHO re-parents it (OnTransformParentChanged + stack trace);
//   - every frame restores a disabled renderer or a collapsed scale and reports it;
//   - reports its state on demand (ReportAll: next-action presentation).
// HideGuard is the one gate every generic hide / deactivate / isolation path asks first:
// a real installed part (or an ancestor of one) is never hidden by those paths, and each
// attempt is logged with its caller.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public static class HideGuard
    {
        /// <summary>True when 'go' may be deactivated by a generic path; false (and a log line) when it is, or contains, a real installed part.</summary>
        public static bool CanHide(GameObject go, string caller, bool allowLooseParts = false)
        {
            if (go == null) return false;
            string blockedBy = null;
            if (go.GetComponent<InstalledPartSentinel>() != null) blockedBy = "installed part";
            else
            {
                var padlock = go.GetComponent<PlacementLock>();
                if (padlock != null && padlock.IsLocked) blockedBy = "locked part";
                else if (!allowLooseParts && go.GetComponent<Oculus.Interaction.PointableElement>() != null && go.GetComponent<Rigidbody>() != null) blockedBy = "grabbable part";
                else
                {
                    foreach (var s in go.GetComponentsInChildren<InstalledPartSentinel>(true)) { blockedBy = "ancestor of installed part '" + s.name + "'"; break; }
                }
            }
            bool blocked = blockedBy != null;
            if (blocked || go.name.StartsWith("engineBlockSep"))
                Debug.Log($"[HolderHideAttempt] object={go.name}#{go.GetInstanceID()} caller={caller} blocked={blocked}{(blocked ? " (" + blockedBy + ")" : "")}");
            if (blocked) Debug.LogError($"[HideGuard] '{caller}' tried to hide {blockedBy} '{go.name}' - refused.");
            return !blocked;
        }
    }

    public class InstalledPartSentinel : MonoBehaviour
    {
        private static readonly List<InstalledPartSentinel> _all = new List<InstalledPartSentinel>();
        private class Runner : MonoBehaviour { }
        private static Runner _runner;

        public string Tag { get; private set; } = "[Installed]";

        /// <summary>Set before a scene reload / quit: the mass OnDisable/OnDestroy of teardown is not a hide attempt.</summary>
        public static bool Quiet;
        public int Slot { get; private set; }
        public string Kit { get; private set; }

        private Renderer[] _renderers;
        private Vector3 _localScale;
        private float _attachedAt;
        private bool _invisibleLogged;
        private int _restores;

        public static InstalledPartSentinel Attach(Transform part, string tag, int slot, string kit)
        {
            if (part == null) return null;
            var s = part.GetComponent<InstalledPartSentinel>();
            if (s == null) s = part.gameObject.AddComponent<InstalledPartSentinel>();
            s.Tag = tag; s.Slot = slot; s.Kit = kit;
            s._renderers = part.GetComponentsInChildren<Renderer>(true);
            s._localScale = part.localScale;
            s._attachedAt = Time.time;
            if (!_all.Contains(s)) _all.Add(s);
            return s;
        }

        private static Runner EnsureRunner()
        {
            if (_runner != null) return _runner;
            var go = new GameObject("InstalledPartSentinel (runtime)");
            _runner = go.AddComponent<Runner>();
            return _runner;
        }

        /// <summary>State of every installed part, e.g. at the next action's presentation.</summary>
        public static void ReportAll(string when)
        {
            foreach (InstalledPartSentinel s in _all)
                if (s != null) s.Report(when);
        }

        public void Report(string when)
        {
            var padlock = GetComponent<PlacementLock>();
            Renderer r = _renderers != null && _renderers.Length > 0 ? _renderers[0] : null;
            bool visible = gameObject.activeInHierarchy && r != null && r.enabled;
            Debug.Log($"{Tag} at={when} real={name}#{gameObject.GetInstanceID()} active={gameObject.activeInHierarchy} activeSelf={gameObject.activeSelf} visible={visible} " +
                      $"parent={(transform.parent != null ? transform.parent.name + "#" + transform.parent.GetInstanceID() : "<root>")} position={transform.position} " +
                      $"localPos={transform.localPosition} localRot={transform.localRotation.eulerAngles} locked={(padlock != null && padlock.IsLocked)} forwarded={(padlock != null && padlock.ForwardedToHandle)} " +
                      $"scale={transform.lossyScale.x:F4} restores={_restores}");
            if (!visible) Debug.LogError($"{Tag} NOT VISIBLE at {when}: active={gameObject.activeInHierarchy} renderer={(r != null ? r.enabled.ToString() : "none")} inactiveAncestor={InactiveAncestor()}");
        }

        private string InactiveAncestor()
        {
            Transform t = transform;
            while (t != null) { if (!t.gameObject.activeSelf) return t.name + "#" + t.GetInstanceID(); t = t.parent; }
            return "none";
        }

        private void OnDisable()
        {
            if (Quiet || !Application.isPlaying) return;
            string who = InactiveAncestor();
            Debug.LogError($"[HolderHideAttempt] object={name}#{gameObject.GetInstanceID()} caller=OnDisable blocked=false {Tag} DEACTIVATED (activeSelf={gameObject.activeSelf}, inactive={who}) - re-activating next frame. Stack:\n{Environment.StackTrace}");
            PartWatch.Log("Sentinel.OnDisable", transform, "inactive ancestor " + who);
            if (Application.isPlaying) EnsureRunner().StartCoroutine(Reactivate(who));
        }

        private IEnumerator Reactivate(string who)
        {
            yield return null;
            if (this == null) yield break;
            Transform t = transform;
            Transform root = t.root;
            while (t != null && t != root)
            {
                if (!t.gameObject.activeSelf) { t.gameObject.SetActive(true); _restores++; Debug.LogError($"{Tag} re-activated '{t.name}' (it had been switched off: {who})."); }
                t = t.parent;
            }
            PartWatch.Log("Sentinel.Reactivate(after)", transform, who);
        }

        private void OnTransformParentChanged()
        {
            Debug.LogError($"{Tag} RE-PARENTED '{name}' to '{(transform.parent != null ? transform.parent.name : "<root>")}'. Stack:\n{Environment.StackTrace}");
            PartWatch.Log("Sentinel.OnTransformParentChanged", transform, null);
        }

        private void OnBecameInvisible()
        {
            if (_invisibleLogged) return;
            _invisibleLogged = true;
            Debug.Log($"{Tag} '{name}' left every camera's view (active={gameObject.activeInHierarchy}, scale={transform.lossyScale.x:F4}, position={transform.position}).");
        }

        private void OnBecameVisible() { _invisibleLogged = false; }

        private void Update()
        {
            if (_renderers == null) return;
            foreach (Renderer r in _renderers)
                if (r != null && !r.enabled) { PartWatch.Log("Sentinel.Update(renderer off)", transform, r.name); r.enabled = true; _restores++; Debug.LogError($"{Tag} renderer '{r.name}' had been DISABLED - re-enabled."); }
            if (transform.localScale.sqrMagnitude < _localScale.sqrMagnitude * 0.25f)
            { PartWatch.Log("Sentinel.Update(scale collapsed)", transform, null); transform.localScale = _localScale; _restores++; Debug.LogError($"{Tag} scale had COLLAPSED - restored."); }
        }

        private void OnDestroy()
        {
            _all.Remove(this);
            if (!Quiet && Application.isPlaying && gameObject.scene.isLoaded)
            {
                Debug.LogError($"{Tag} '{name}' DESTROYED. Stack:\n{Environment.StackTrace}");
                PartWatch.Gone("Sentinel.OnDestroy", name + "#" + gameObject.GetInstanceID(), Tag);
            }
        }
    }
}
