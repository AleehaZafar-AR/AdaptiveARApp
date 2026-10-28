// File: AdaptivePanelController.cs
// Changes how much peripheral information is on screen as the support level changes.
//
// The design principle, stated so it is not mistaken for something stronger:
// higher support means richer guidance for the IMMEDIATE task and LESS simultaneous
// peripheral information - not more screens. L1 leaves the context panels up; L3
// collapses them so the instruction and the physical work have the field to
// themselves.
//
// This is implemented neutrally as visibility and salience. It makes no claim about
// what any panel does to a participant; it only decides what is shown, how brightly,
// and whether it takes ray input.
//
// The centre of view is never occupied by a panel. That space belongs to the engine,
// the target ghost, the arrow and the local validation cue.

using System;
using System.Collections.Generic;
using AdaptiveAR.Support;
using UnityEngine;

namespace AdaptiveAR.UI
{
    public class AdaptivePanelController : MonoBehaviour
    {
        /// <summary>How present a zone is at a given support level.</summary>
        public enum Presence
        {
            /// <summary>Full opacity, interactive.</summary>
            Full = 0,

            /// <summary>Readable but clearly secondary. Still interactive.</summary>
            Reduced = 1,

            /// <summary>Barely there. Not interactive. Present for a researcher glancing over.</summary>
            Faded = 2,

            /// <summary>Not rendered and not interactive.</summary>
            Hidden = 3
        }

        [Serializable]
        public class Zone
        {
            public string label;

            [Tooltip("Root of the panel. A CanvasGroup is added automatically if missing.")]
            public GameObject root;

            [Tooltip("Presence at L1 - minimal task assistance, context available.")]
            public Presence atL1 = Presence.Full;

            [Tooltip("Presence at L2 - guided.")]
            public Presence atL2 = Presence.Reduced;

            [Tooltip("Presence at L3 - assisted and focused; peripheral information recedes.")]
            public Presence atL3 = Presence.Hidden;

            [NonSerialized] public CanvasGroup group;

            public Presence For(SupportLevel level)
            {
                switch (level)
                {
                    case SupportLevel.L3_Assisted: return atL3;
                    case SupportLevel.L2_Guided: return atL2;
                    default: return atL1;
                }
            }
        }

        [Header("Source")]
        [SerializeField] private SupportLevelController supportLevel;

        [Header("Zones")]
        [Tooltip("Zone 1 is the instruction panel and should stay Full at every level.")]
        [SerializeField]
        private List<Zone> zones = new List<Zone>();

        [Header("Transition")]
        [Tooltip("Seconds for a fade or collapse. Calm, not abrupt.")]
        [SerializeField] private float fadeSeconds = 0.35f;

        [Tooltip("Alpha used by the Reduced presence.")]
        [Range(0f, 1f)]
        [SerializeField] private float reducedAlpha = 0.55f;

        [Tooltip("Alpha used by the Faded presence.")]
        [Range(0f, 1f)]
        [SerializeField] private float fadedAlpha = 0.18f;

        [Header("On-demand information")]
        [Tooltip("Zones the participant may reveal temporarily when the current level hides them (the " +
                 "'Task info' control). Presentation only: the level, the decision layer and the task " +
                 "state are untouched. Never the Research zone.")]
        [SerializeField] private string[] onDemandZones = { "Steps", "Performance" };

        [Header("Debug")]
        [SerializeField] private bool logChanges = true;

        private readonly Dictionary<Zone, float> _targetAlpha = new Dictionary<Zone, float>();
        private bool _placementMode;
        private bool _infoRevealed;

        /// <summary>True while the participant has the hidden on-demand zones revealed.</summary>
        public bool InfoRevealed { get { return _infoRevealed; } }

        /// <summary>(revealed, by): "participant", "level_change", "placement".</summary>
        public event Action<bool, string> OnInfoRevealedChanged;

        private bool IsOnDemand(Zone z)
        {
            if (z == null || onDemandZones == null) return false;
            foreach (string l in onDemandZones) if (l == z.label) return true;
            return false;
        }

        public List<string> OnDemandZoneLabels()
        {
            var list = new List<string>();
            foreach (Zone z in zones) if (IsOnDemand(z)) list.Add(z.label);
            return list;
        }

        /// <summary>True when at least one on-demand zone is not fully present at the current level (so there is something to reveal).</summary>
        public bool HasOnDemandZoneHidden()
        {
            SupportLevel level = CurrentLevel();
            foreach (Zone z in zones)
                if (IsOnDemand(z) && z.root != null && z.For(level) != Presence.Full) return true;
            return false;
        }

        /// <summary>
        /// Reveals (or re-hides) the on-demand zones at Full presence without changing the
        /// level's authored layout for anything else. A level change always clears it.
        /// </summary>
        public void SetInfoRevealed(bool revealed, string by)
        {
            if (_infoRevealed == revealed) return;
            _infoRevealed = revealed;
            if (!_placementMode)
            {
                SupportLevel level = CurrentLevel();
                foreach (Zone z in zones)
                {
                    if (z == null || z.group == null || !IsOnDemand(z)) continue;
                    _targetAlpha[z] = revealed ? 1f : AlphaFor(z.For(level));
                }
            }
            if (logChanges) Debug.Log($"[AdaptivePanels] on-demand info {(revealed ? "revealed" : "hidden")} by {by} at {CurrentLevel()}: {string.Join(", ", OnDemandZoneLabels())}");
            try { OnInfoRevealedChanged?.Invoke(revealed, by); } catch (Exception e) { Debug.LogError("[AdaptivePanels] OnInfoRevealedChanged handler threw: " + e); }
        }

        /// <summary>True while only the main panel is shown, before the workspace exists.</summary>
        public bool PlacementMode { get { return _placementMode; } }

        /// <summary>
        /// Before the workspace is placed only the main (first) zone is shown; the side
        /// panels have nothing to be relative to yet. Turning this off re-applies the
        /// current support level's layout.
        /// </summary>
        public void SetPlacementMode(bool placing)
        {
            _placementMode = placing;
            Prepare();
            if (placing && _infoRevealed) SetInfoRevealed(false, "placement");

            if (!placing)
            {
                ApplyImmediate(CurrentLevel());
                if (logChanges) Debug.Log("[AdaptivePanels] Placement done: " + Describe(CurrentLevel()));
                return;
            }

            for (int i = 0; i < zones.Count; i++)
            {
                Zone z = zones[i];
                if (z == null || z.group == null) continue;
                float a = i == 0 ? 1f : 0f;
                _targetAlpha[z] = a;
                z.group.alpha = a;
                z.group.interactable = a > 0.5f;
                z.group.blocksRaycasts = a > 0.5f;
                z.root.SetActive(a > 0.001f);
            }

            if (logChanges) Debug.Log("[AdaptivePanels] Placement mode: main panel only.");
        }

        private void OnEnable()
        {
            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged += HandleLevelChanged;

            Prepare();
            ApplyImmediate(CurrentLevel());
        }

        private void OnDisable()
        {
            if (supportLevel != null)
                supportLevel.OnSupportLevelChanged -= HandleLevelChanged;
        }

        private SupportLevel CurrentLevel()
        {
            return supportLevel != null ? supportLevel.CurrentLevel : SupportLevel.L1_Minimal;
        }

        private void Prepare()
        {
            foreach (Zone z in zones)
            {
                if (z == null || z.root == null) continue;

                z.group = z.root.GetComponent<CanvasGroup>();
                if (z.group == null) z.group = z.root.AddComponent<CanvasGroup>();
            }
        }

        private void HandleLevelChanged(SupportLevel from, SupportLevel to, string reason)
        {
            if (_placementMode) return;   // applied when placement ends

            // A new level returns to its authored presentation: an on-demand reveal ends.
            if (_infoRevealed) { _infoRevealed = false; try { OnInfoRevealedChanged?.Invoke(false, "level_change"); } catch (Exception e) { Debug.LogError("[AdaptivePanels] OnInfoRevealedChanged handler threw: " + e); } }

            // Visibility only. Nothing here touches workflow state, so a support change
            // cannot reset progress, duplicate a canvas or clear an instruction.
            foreach (Zone z in zones)
            {
                if (z == null || z.group == null) continue;
                _targetAlpha[z] = AlphaFor(z.For(to));
            }

            if (logChanges)
                Debug.Log($"[AdaptivePanels] {from} -> {to}: " + Describe(to));
        }

        private void Update()
        {
            if (_targetAlpha.Count == 0) return;

            float step = Time.deltaTime / Mathf.Max(0.01f, fadeSeconds);

            foreach (Zone z in zones)
            {
                if (z == null || z.group == null) continue;
                if (!_targetAlpha.TryGetValue(z, out float target)) continue;

                float a = Mathf.MoveTowards(z.group.alpha, target, step);
                if (Mathf.Approximately(a, z.group.alpha)) continue;

                z.group.alpha = a;

                // A faded-out panel must not swallow rays meant for the work behind it.
                bool interactive = a > 0.5f;
                z.group.interactable = interactive;
                z.group.blocksRaycasts = interactive;

                // Fully transparent panels are switched off so they cost nothing.
                if (z.root.activeSelf && a <= 0.001f) z.root.SetActive(false);
                else if (!z.root.activeSelf && a > 0.001f) z.root.SetActive(true);
            }
        }

        /// <summary>Applies the current level with no transition. Used on enable.</summary>
        public void ApplyImmediate(SupportLevel level)
        {
            foreach (Zone z in zones)
            {
                if (z == null || z.group == null) continue;

                float a = AlphaFor(z.For(level));
                _targetAlpha[z] = a;

                z.group.alpha = a;
                bool interactive = a > 0.5f;
                z.group.interactable = interactive;
                z.group.blocksRaycasts = interactive;
                z.root.SetActive(a > 0.001f);
                if (logChanges)
                    Debug.Log($"[AdaptivePanels] zone={z.label} level={level} presence={z.For(level)} alpha={a:F2} " +
                              $"active={z.root.activeSelf} activeInHierarchy={z.root.activeInHierarchy} pos={z.root.transform.position}");
            }
        }

        private float AlphaFor(Presence p)
        {
            switch (p)
            {
                case Presence.Full: return 1f;
                case Presence.Reduced: return reducedAlpha;
                case Presence.Faded: return fadedAlpha;
                default: return 0f;
            }
        }

        private string Describe(SupportLevel level)
        {
            var parts = new List<string>();
            foreach (Zone z in zones)
            {
                if (z == null) continue;
                parts.Add($"{z.label}={z.For(level)}");
            }
            return string.Join(", ", parts);
        }

        /// <summary>Lets the editor tool define the zones without hand-wiring a list.</summary>
        public void ConfigureZone(string label, GameObject root, Presence l1, Presence l2, Presence l3)
        {
            Zone existing = zones.Find(z => z != null && z.label == label);
            if (existing == null)
            {
                existing = new Zone { label = label };
                zones.Add(existing);
            }

            existing.root = root;
            existing.atL1 = l1;
            existing.atL2 = l2;
            existing.atL3 = l3;
        }
    }
}
