// File: SupportInfoToggle.cs
// A small persistent "Task info" control that lets the participant reveal, on demand, the
// secondary panels the current support level hides by default (at L3: the step list and
// the performance/status panel). The adaptive system sets the DEFAULT information density;
// it does not remove access to task information. Opening it changes nothing but
// presentation: no support-level change, no decision request, no action or validation
// change, no adaptation cue. It is logged as a user information request
// (support_info_opened / support_info_closed) so how often participants ask for hidden
// information under high support can be analysed.
//
// Built at runtime on the instruction panel (same pattern as the Reset View button), shown
// only while a hidden-on-demand zone exists at the current level, i.e. at L3 with the
// shipped zone configuration. A level change closes the view and the normal authored
// presentation for the new level applies.

using AdaptiveAR.Logging;
using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.UI
{
    public class SupportInfoToggle : MonoBehaviour
    {
        public const string Surface = "instruction_panel/TaskInfoButton";

        [SerializeField] private string openLabel = "Task info";
        [SerializeField] private string closeLabel = "Hide info";
        [SerializeField] private Vector2 size = new Vector2(118f, 36f);
        [SerializeField] private Vector2 cornerInset = new Vector2(-14f, -14f);
        [SerializeField] private float fontSize = 15f;

        private AdaptivePanelController _panels;
        private AssemblySessionController _session;
        private SessionLogger _logger;
        private SupportLevelController _level;
        private GameObject _button;
        private TextMeshProUGUI _label;
        private Image _image;
        private int _opens;
        private bool _wiredLevel;

        private void Start()
        {
            _panels = FindAnyObjectByType<AdaptivePanelController>(FindObjectsInactive.Include);
            _session = FindAnyObjectByType<AssemblySessionController>(FindObjectsInactive.Include);
            _logger = FindAnyObjectByType<SessionLogger>(FindObjectsInactive.Include);
            _level = FindAnyObjectByType<SupportLevelController>(FindObjectsInactive.Include);
            if (_panels == null) { Debug.Log("[SupportInfo] no AdaptivePanelController in this scene: nothing to reveal, control not built."); enabled = false; return; }
            _panels.OnInfoRevealedChanged += HandleRevealChanged;
            TryBuild();
        }

        private void OnDestroy()
        {
            if (_panels != null) _panels.OnInfoRevealedChanged -= HandleRevealChanged;
        }

        private void TryBuild()
        {
            if (_button != null) return;
            var flow = FindAnyObjectByType<AppFlowController>(FindObjectsInactive.Include);
            RectTransform panel = flow != null && flow.StepPanel != null ? flow.StepPanel.transform as RectTransform : null;
            if (panel == null) return;

            Transform existing = panel.Find("TaskInfoButton");
            if (existing != null) { _button = existing.gameObject; }
            else
            {
                GameObject template = flow.BackButtonTemplate;
                GameObject go;
                if (template != null)
                {
                    go = Instantiate(template, panel);
                    var old = go.GetComponent<Button>(); if (old != null) DestroyImmediate(old);
                    foreach (Transform extra in go.transform) if (extra.GetComponent<TextMeshProUGUI>() == null) Destroy(extra.gameObject);
                    foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>()) if (mb != null && !(mb is Image)) Destroy(mb);
                }
                else
                {
                    go = new GameObject("TaskInfoButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    go.transform.SetParent(panel, false);
                    var l = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                    l.transform.SetParent(go.transform, false);
                }
                go.name = "TaskInfoButton";
                _button = go;
            }

            // Top-right corner of the instruction panel: away from Continue / Back at the bottom.
            var rt = _button.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = cornerInset; rt.sizeDelta = size; rt.localScale = Vector3.one;

            _label = _button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (_label != null)
            {
                _label.text = openLabel; _label.enableAutoSizing = false; _label.fontSize = fontSize; _label.fontStyle = FontStyles.Normal;
                _label.alignment = TextAlignmentOptions.Center; _label.color = MrTheme.TextSecondary;
                var trt = _label.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            }
            // Quiet chip: a faint accent tint, clearly not the filled primary/secondary buttons.
            _image = _button.GetComponent<Image>();
            if (_image != null) _image.color = MrTheme.AccentSoft;

            var button = _button.GetComponent<Button>();
            if (button == null) button = _button.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(Toggle);
            _button.SetActive(false);
            Debug.Log("[SupportInfo] 'Task info' control built on the instruction panel (shown only while a zone is hidden on demand).");
        }

        private void Toggle()
        {
            if (_panels == null) return;
            bool open = !_panels.InfoRevealed;
            _panels.SetInfoRevealed(open, "participant");
            if (open) _opens++;
            if (_logger != null)
                _logger.LogSupportInfo(open, Surface, _session != null ? _session.CurrentActionId : null, _panels.OnDemandZoneLabels(), _opens, "participant");
            Debug.Log($"[SupportInfo] {(open ? "opened" : "closed")} by participant (open #{_opens}) at {(_level != null ? _level.CurrentLevel.ToString() : "?")}: zones={string.Join(",", _panels.OnDemandZoneLabels())}");
        }

        /// <summary>A level change (or placement) closed the view: log the close as system-driven, refresh the label.</summary>
        private void HandleRevealChanged(bool revealed, string by)
        {
            if (!revealed && by != "participant" && _logger != null && _opens > 0 && _lastLoggedOpen)
                _logger.LogSupportInfo(false, Surface, _session != null ? _session.CurrentActionId : null, _panels.OnDemandZoneLabels(), _opens, by);
            _lastLoggedOpen = revealed;
            if (_label != null) _label.text = revealed ? closeLabel : openLabel;
            if (_image != null) _image.color = revealed ? MrTheme.WithAlpha(MrTheme.Accent, 0.35f) : MrTheme.AccentSoft;
        }

        private bool _lastLoggedOpen;

        private void LateUpdate()
        {
            if (_button == null) { TryBuild(); if (_button == null) return; }
            bool show = _panels != null && !_panels.PlacementMode && _panels.HasOnDemandZoneHidden()
                        && _session != null && _session.AssemblyRunning;
            if (_button.activeSelf != show) _button.SetActive(show);
        }
    }

    /// <summary>Creates the control once per scene when an AdaptivePanelController exists; no scene edit needed.</summary>
    public static class SupportInfoBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) => Ensure();
            Ensure();
        }

        private static void Ensure()
        {
            var panels = Object.FindAnyObjectByType<AdaptivePanelController>(FindObjectsInactive.Include);
            if (panels == null) return;
            if (panels.GetComponent<SupportInfoToggle>() == null) panels.gameObject.AddComponent<SupportInfoToggle>();
        }
    }
}
