// File: ControllerGrabDiagnostics.cs
// Runtime evidence for controller grabs. In the v83 OVRInteractionComprehensive rig the
// controllers grab through controller-driven synthetic hands: the SAME HandGrabInteractor /
// HandGrabInteractable pair as tracked hands (there is no GrabInteractor in that rig). So a
// part the hand can grab but the controller cannot differs in what the controller-driven
// interactor sees: whether the part is in its trigger registry, whether it is the candidate,
// and what competes. This component logs exactly that:
//   [ControllerCandidate] ... on every state change of every grab interactor, and
//   [ControllerCandidate] attempt=grip|trigger ... the frame a controller button goes down,
//   with the registry contents, the candidate, and the nearest part whether or not it is
//   registered.
// Diagnostics only; nothing here changes selection.

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    /// <summary>
    /// Controller-driven hands only. The SDK ranks a collider its grab point is inside as the best
    /// candidate (GrabPoseHelper.CollidersScore); the controller hand's palm point sits at desk
    /// level, inside the turntable's rim slab, when it reaches for a part lying beside the oil pan,
    /// so the turntable wins over the part. While a loose part is within reach, this filter removes
    /// the turntable from that interactor's candidates. Tracked hands are not touched.
    /// </summary>
    public class ControllerReachFilter : MonoBehaviour, Oculus.Interaction.IGameObjectFilter
    {
        private static readonly Dictionary<Oculus.Interaction.HandGrab.HandGrabInteractor, ControllerReachFilter> _byInteractor = new Dictionary<Oculus.Interaction.HandGrab.HandGrabInteractor, ControllerReachFilter>();
        private Oculus.Interaction.HandGrab.HandGrabInteractor _it;
        private ControllerGrabDiagnostics _diag;
        private bool _blocking;
        private float _lastLog = -10f;

        public static bool IsBlocking(Oculus.Interaction.HandGrab.HandGrabInteractor it)
        {
            return it != null && _byInteractor.TryGetValue(it, out ControllerReachFilter f) && f != null && f._blocking;
        }

        public static void Install(Oculus.Interaction.HandGrab.HandGrabInteractor it, ControllerGrabDiagnostics diag)
        {
            if (it == null || _byInteractor.ContainsKey(it)) return;
            var f = it.gameObject.AddComponent<ControllerReachFilter>();
            f._it = it; f._diag = diag;
            _byInteractor[it] = f;

            // keep whatever filters the rig already had on this interactor
            var filters = new List<Oculus.Interaction.IGameObjectFilter>();
            try
            {
                System.Reflection.FieldInfo field = null;
                for (System.Type t = it.GetType(); t != null && field == null; t = t.BaseType)
                    field = t.GetField("_interactableFilters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                var existing = field != null ? field.GetValue(it) as List<UnityEngine.Object> : null;
                if (existing != null) foreach (var o in existing) if (o is Oculus.Interaction.IGameObjectFilter gf) filters.Add(gf);
            }
            catch (System.Exception ex) { Debug.LogWarning("[GrabCompare] could not read the rig's existing interactable filters: " + ex.Message); }
            filters.Add(f);
            it.InjectOptionalInteractableFilters(filters);
            Debug.Log($"[GrabCompare] reach filter installed on controller-driven interactor '{it.name}' ({filters.Count - 1} rig filter(s) kept): turntable excluded while a loose part is within {diag.reachMeters * 100f:F0} cm.");
        }

        public bool Filter(GameObject candidate)
        {
            if (candidate == null || _it == null) return true;
            if (candidate.GetComponentInParent<AdaptiveAR.MR.EngineTurntable>() == null) { return true; }   // only the turntable is ever filtered
            Vector3 p = _it.Rigidbody != null ? _it.Rigidbody.position : _it.transform.position;
            var part = ControllerGrabDiagnostics.NearestPart(p, out float d);
            bool block = part != null && d <= (_diag != null ? _diag.reachMeters : 0.08f);
            if (block != _blocking || Time.time - _lastLog > 2f)
            {
                _blocking = block; _lastLog = Time.time;
                if (block) Debug.Log($"[GrabCompare] controller-hand '{_it.name}': turntable removed from candidates - loose part '{part.Rigidbody.name}' is {d * 100f:F1} cm from the grab point.");
            }
            return !block;
        }

        private void OnDestroy() { if (_it != null) _byInteractor.Remove(_it); }
    }

    public class ControllerGrabDiagnostics : MonoBehaviour
    {
        private class Entry
        {
            public Oculus.Interaction.HandGrab.HandGrabInteractor it;
            public bool controller;
            public bool left;
            public string path;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly HashSet<Oculus.Interaction.HandGrab.HandGrabInteractor> _hooked = new HashSet<Oculus.Interaction.HandGrab.HandGrabInteractor>();
        private readonly List<Oculus.Interaction.HandGrab.HandGrabInteractable> _parts = new List<Oculus.Interaction.HandGrab.HandGrabInteractable>();
        private float _nextScan;

        /// <summary>World positions of every grab interactor's rigidbody (hands and controller hands), for proximity checks.</summary>
        public static readonly List<Transform> InteractorBodies = new List<Transform>();

        [Tooltip("Controller-driven hands only: while a loose part is within this distance of the grab point, the " +
                 "engine turntable is not a candidate. The SDK scores a collider the grab point is INSIDE as the best " +
                 "candidate; the controller hand's palm point sits at desk level inside the turntable's rim slab when " +
                 "reaching for a part lying beside the oil pan, where a tracked hand pinches from above.")]
        public float reachMeters = 0.08f;

        /// <summary>Loose parts' hand-grab interactables (shared with the reach filter).</summary>
        public static readonly List<Oculus.Interaction.HandGrab.HandGrabInteractable> Parts = new List<Oculus.Interaction.HandGrab.HandGrabInteractable>();

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private void Update()
        {
            if (Time.time >= _nextScan) { _nextScan = Time.time + 2f; Scan(); }

            foreach (Entry e in _entries)
            {
                if (!e.controller || e.it == null) continue;
                var c = e.left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
                if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, c)) LogAttempt(e, "grip");
                if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, c)) LogAttempt(e, "trigger");
            }
        }

        private void Scan()
        {
            foreach (var it in FindObjectsByType<Oculus.Interaction.HandGrab.HandGrabInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (it == null || _hooked.Contains(it)) continue;
                _hooked.Add(it);
                string path = PathOf(it.transform);
                var e = new Entry
                {
                    it = it,
                    controller = path.IndexOf("Controller", System.StringComparison.OrdinalIgnoreCase) >= 0,
                    left = path.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0,
                    path = path
                };
                _entries.Add(e);
                if (it.Rigidbody != null) InteractorBodies.Add(it.Rigidbody.transform); else InteractorBodies.Add(it.transform);
                Entry cap = e;
                it.WhenStateChanged += a => OnState(cap, a);
                Debug.Log($"[ControllerCandidate] hooked interactor={path} kind={(e.controller ? "controller-driven hand" : "tracked hand")} side={(e.left ? "L" : "R")}");
                if (e.controller) ControllerReachFilter.Install(it, this);
            }

            _parts.Clear();
            foreach (var h in FindObjectsByType<Oculus.Interaction.HandGrab.HandGrabInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (h != null && h.Rigidbody != null && h.GetComponentInParent<AdaptiveAR.MR.EngineTurntable>() == null) _parts.Add(h);
            Parts.Clear(); Parts.AddRange(_parts);
        }

        /// <summary>Nearest loose part to a point, by collider distance (metres), or null.</summary>
        public static Oculus.Interaction.HandGrab.HandGrabInteractable NearestPart(Vector3 p, out float distance)
        {
            Oculus.Interaction.HandGrab.HandGrabInteractable best = null; distance = float.MaxValue;
            foreach (var h in Parts)
            {
                if (h == null || !h.gameObject.activeInHierarchy || h.Rigidbody == null) continue;
                if (h.Rigidbody.isKinematic) continue;                                   // placed / held parts are not loose
                float d = Distance(p, h);
                if (d >= 0f && d < distance) { distance = d; best = h; }
            }
            return best;
        }

        /// <summary>
        /// [GrabCompare] for one interactor against one part: the same fields for a tracked hand and a
        /// controller-driven hand, so the first divergence on the same resting part is visible.
        /// </summary>
        private void LogCompare(Entry e, string when, Oculus.Interaction.HandGrab.HandGrabInteractable part)
        {
            try
            {
                var it = e.it;
                Vector3 p = it.Rigidbody != null ? it.Rigidbody.position : it.transform.position;
                if (part == null) part = NearestPart(p, out _);
                if (part == null || part.Rigidbody == null) { Debug.Log($"[GrabCompare] mode={(e.controller ? "controller_hand" : "tracked_hand")} when={when} part=none"); return; }
                Rigidbody rb = part.Rigidbody;
                float d = Distance(p, part);
                bool member = false;
                foreach (var h in Oculus.Interaction.HandGrab.HandGrabInteractable.Registry.List(it)) if (h == part) { member = true; break; }

                // does any of the interactor body's colliders (its trigger volume) overlap the part's colliders?
                bool overlap = false;
                if (it.Rigidbody != null)
                {
                    foreach (Collider mine in it.Rigidbody.GetComponentsInChildren<Collider>())
                    {
                        if (mine == null || !mine.enabled) continue;
                        foreach (Collider pc in part.Colliders)
                        {
                            if (pc == null || !pc.enabled) continue;
                            try { if (Physics.ComputePenetration(mine, mine.transform.position, mine.transform.rotation, pc, pc.transform.position, pc.transform.rotation, out _, out _)) { overlap = true; break; } }
                            catch (System.Exception) { }
                        }
                        if (overlap) break;
                    }
                }
                int bodyLayer = it.Rigidbody != null ? it.Rigidbody.gameObject.layer : it.gameObject.layer;
                string mask = $"{LayerMask.LayerToName(bodyLayer)}({bodyLayer})->{LayerMask.LayerToName(rb.gameObject.layer)}({rb.gameObject.layer}):{(!Physics.GetIgnoreLayerCollision(bodyLayer, rb.gameObject.layer) ? "collides" : "IGNORED")}";
                var hover = it.HasCandidate ? it.Candidate : null;
                var sel = it.HasSelectedInteractable ? it.SelectedInteractable : null;
                Debug.Log($"[GrabCompare] mode={(e.controller ? "controller_hand" : "tracked_hand")} side={(e.left ? "L" : "R")} when={when} part={rb.name}#{rb.gameObject.GetInstanceID()} " +
                          $"nearestDistance={(d >= 0 ? (d * 100f).ToString("F1") + "cm" : "n/a")} registryMember={member} interactableState={part.State} interactorState={it.State} " +
                          $"hoverCandidate={(hover != null && hover.Rigidbody != null ? hover.Rigidbody.name : "none")} selectCandidate={(sel != null && sel.Rigidbody != null ? sel.Rigidbody.name : "none")} " +
                          $"rigidbodySleeping={rb.IsSleeping()} colliderOverlap={overlap} interactionMask={mask} bodyDistance={(d >= 0 ? (d * 100f).ToString("F1") + "cm" : "n/a")} " +
                          $"interactableEnabled={part.enabled} partKinematic={rb.isKinematic} filterActive={ControllerReachFilter.IsBlocking(it)}");
            }
            catch (System.Exception ex) { Debug.LogError("[GrabCompare] threw: " + ex); }
        }

        private void OnState(Entry e, Oculus.Interaction.InteractorStateChangeArgs a)
        {
            try
            {
                var it = e.it;
                var target = it.HasSelectedInteractable ? it.SelectedInteractable : (it.HasInteractable ? it.Interactable : (it.HasCandidate ? it.Candidate : null));
                string targetName = target != null && target.Rigidbody != null ? target.Rigidbody.name + "#" + target.Rigidbody.gameObject.GetInstanceID() : "none";
                Debug.Log($"[ControllerCandidate] kind={(e.controller ? "controller" : "hand")} side={(e.left ? "L" : "R")} {a.PreviousState}->{a.NewState} target={targetName} " +
                          $"selected={a.NewState == Oculus.Interaction.InteractorState.Select} competing=[{Competing(it, target)}]");
                if (a.NewState == Oculus.Interaction.InteractorState.Hover || a.NewState == Oculus.Interaction.InteractorState.Select)
                    LogCompare(e, a.PreviousState + "->" + a.NewState, target != null && target.GetComponentInParent<AdaptiveAR.MR.EngineTurntable>() == null ? target : null);
            }
            catch (System.Exception ex) { Debug.LogError("[ControllerCandidate] state handler threw: " + ex); }
        }

        private string Competing(Oculus.Interaction.HandGrab.HandGrabInteractor it, Oculus.Interaction.HandGrab.HandGrabInteractable except)
        {
            var sb = new StringBuilder();
            Vector3 p = it.Rigidbody != null ? it.Rigidbody.position : it.transform.position;
            foreach (var h in Oculus.Interaction.HandGrab.HandGrabInteractable.Registry.List(it))
            {
                if (h == null || h == except || h.Rigidbody == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(h.Rigidbody.name).Append('(').Append((Distance(p, h) * 100f).ToString("F1")).Append("cm)");
            }
            return sb.ToString();
        }

        private static float Distance(Vector3 p, Oculus.Interaction.HandGrab.HandGrabInteractable h)
        {
            float best = float.MaxValue;
            foreach (Collider c in h.Colliders)
            {
                if (c == null || !c.enabled) continue;
                var mc = c as MeshCollider;
                Vector3 q = (mc != null && !mc.convex) ? c.bounds.ClosestPoint(p) : c.ClosestPoint(p);
                best = Mathf.Min(best, Vector3.Distance(p, q));
            }
            return best == float.MaxValue ? -1f : best;
        }

        private void LogAttempt(Entry e, string button)
        {
            try
            {
                var it = e.it;
                Vector3 p = it.Rigidbody != null ? it.Rigidbody.position : it.transform.position;

                Oculus.Interaction.HandGrab.HandGrabInteractable nearest = null; float nearestD = float.MaxValue;
                foreach (var h in _parts)
                {
                    if (h == null || !h.gameObject.activeInHierarchy) continue;
                    float d = Distance(p, h);
                    if (d >= 0f && d < nearestD) { nearestD = d; nearest = h; }
                }
                bool inRegistry = false;
                foreach (var h in Oculus.Interaction.HandGrab.HandGrabInteractable.Registry.List(it)) if (h == nearest) { inRegistry = true; break; }

                var candidate = it.HasCandidate ? it.Candidate : null;
                string candName = candidate != null && candidate.Rigidbody != null ? candidate.Rigidbody.name : "none";
                float candD = candidate != null ? Distance(p, candidate) : -1f;
                string nearestInfo = "none";
                if (nearest != null)
                {
                    var rb = nearest.Rigidbody;
                    int on = 0, total = 0; foreach (Collider c in nearest.Colliders) { total++; if (c != null && c.enabled) on++; }
                    nearestInfo = $"{rb.name}#{rb.gameObject.GetInstanceID()} d={nearestD * 100f:F1}cm inRegistry={inRegistry} interactable={(nearest.enabled ? "on" : "OFF")} " +
                                  $"rb={(rb.isKinematic ? "kinematic" : "dynamic")}{(rb.IsSleeping() ? ",sleeping" : "")} colliders={on}/{total} layer={LayerMask.LayerToName(rb.gameObject.layer)} " +
                                  $"pointable={(nearest.PointableElement != null ? ((Component)nearest.PointableElement).name : "NONE")}";
                }
                Debug.Log($"[ControllerCandidate] attempt={button} side={(e.left ? "L" : "R")} interactor={e.path} state={it.State} target={candName} score={(candD >= 0 ? (candD * 100f).ToString("F1") + "cm" : "n/a")} " +
                          $"selected={it.State == Oculus.Interaction.InteractorState.Select} competing=[{Competing(it, candidate)}] nearestPart={nearestInfo}");

                string intersecting = "n/a", sleeping = "n/a";
                if (nearest != null && nearest.Rigidbody != null)
                {
                    var tray = nearest.Rigidbody.GetComponent<DropIntoTray>();
                    sleeping = nearest.Rigidbody.IsSleeping().ToString();
                    if (tray != null)
                    {
                        bool penetrating = tray.DeepestPenetration(out Collider surface, out _, out float depth, out bool supported);
                        intersecting = penetrating ? $"true({depth * 1000f:F1}mm,{surface.name})" : (supported ? "false" : "n/a");
                    }
                }
                Debug.Log($"[ControllerGrabAttempt] nearestPart={(nearest != null ? nearest.Rigidbody.name : "none")} distance={(nearestD < float.MaxValue ? (nearestD * 100f).ToString("F1") + "cm" : "n/a")} " +
                          $"intersectingTray={intersecting} sleeping={sleeping} candidate={candName} inRegistry={inRegistry} button={button} side={(e.left ? "L" : "R")}");
                LogCompare(e, "attempt:" + button, nearest);
            }
            catch (System.Exception ex) { Debug.LogError("[ControllerCandidate] attempt log threw: " + ex); }
        }
    }
}
