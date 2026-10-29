// File: TechnicalValidationOverlay.cs
// The TECHNICAL VALIDATION readout: a small head-locked world-space panel built in code,
// visually distinct from every participant surface (magenta frame, monospaced block text,
// low in the view). Shows scenario, scripted event, step/action, support level, the last
// AI action and the provider status. Never shows model reasoning or any physiological
// interpretation. Exists only on the runner's GameObject, i.e. never in participant mode.

using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.TechnicalValidation
{
    public class TechnicalValidationOverlay : MonoBehaviour
    {
        private static readonly Color Frame = new Color32(0xE0, 0x3C, 0xC8, 0xFF);     // magenta: not a theme colour
        private static readonly Color Fill = new Color32(0x12, 0x06, 0x12, 0xD8);
        private static readonly Color Text = new Color32(0xFF, 0xE9, 0xFB, 0xFF);

        [SerializeField] private Vector3 localOffset = new Vector3(0f, -0.21f, 0.75f);
        [SerializeField] private Vector2 sizePixels = new Vector2(640f, 170f);
        [SerializeField] private float scale = 0.00065f;
        [SerializeField] private float fontSize = 19f;
        [SerializeField] private float refreshInterval = 0.2f;

        private Canvas _canvas;
        private TextMeshProUGUI _text;
        private Transform _head;
        private float _timer;
        private readonly StringBuilder _sb = new StringBuilder(512);

        private void Start() { TryBuild(); }

        private void TryBuild()
        {
            if (_canvas != null) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            _head = cam.transform;

            var root = new GameObject("TechnicalValidationOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            root.transform.SetParent(_head, false);
            root.transform.localPosition = localOffset;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one * scale;
            _canvas = root.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 500;
            var rt = root.GetComponent<RectTransform>();
            rt.sizeDelta = sizePixels;

            var frame = new GameObject("Frame", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            frame.transform.SetParent(root.transform, false);
            Stretch(frame.GetComponent<RectTransform>(), 0f);
            frame.GetComponent<Image>().color = Frame;

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            fill.transform.SetParent(root.transform, false);
            Stretch(fill.GetComponent<RectTransform>(), 4f);
            fill.GetComponent<Image>().color = Fill;

            var label = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            label.transform.SetParent(root.transform, false);
            Stretch(label.GetComponent<RectTransform>(), 14f);
            _text = label.GetComponent<TextMeshProUGUI>();
            _text.fontSize = fontSize;
            _text.color = Text;
            _text.alignment = TextAlignmentOptions.TopLeft;
            _text.enableWordWrapping = true;
            _text.overflowMode = TextOverflowModes.Truncate;
            _text.richText = true;
            Debug.Log("[TechnicalValidation] overlay built on the head camera (validation mode only).");
        }

        private static void Stretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
        }

        private void Update()
        {
            if (_canvas == null) { TryBuild(); if (_canvas == null) return; }
            _timer += Time.deltaTime;
            if (_timer < refreshInterval) return;
            _timer = 0f;
            var r = TechnicalValidationRunner.Instance;
            if (r == null || _text == null) return;

            _sb.Clear();
            _sb.Append("<b><color=#E03CC8>TECHNICAL VALIDATION</color></b>   ").Append(r.Phase).Append("   t=").Append(r.RunSeconds.ToString("0")).Append("s\n");
            _sb.Append("Scenario: ").Append(r.Scenario != null ? r.Scenario.Id + " " + (r.Scenario.Title ?? "") : (r.Config != null ? r.Config.Scenario : "?"))
               .Append("   mode ").Append(r.Config != null ? r.Config.ModeName : "?").Append("   physiology ").Append(r.Config != null ? r.Config.PhysiologyName : "?").Append('\n');
            _sb.Append("Event: ").Append(r.CurrentEventText).Append('\n');
            _sb.Append("Step/action: ").Append(r.CurrentStepText).Append("   Support: ").Append(r.CurrentLevelText);
            if (!string.IsNullOrEmpty(r.LastTransitionText)) _sb.Append("   last ").Append(r.LastTransitionText);
            _sb.Append('\n');
            _sb.Append("AI: ").Append(r.LastAiText).Append('\n');
            _sb.Append("Provider: ").Append(r.ProviderText);
            if (!string.IsNullOrEmpty(r.ResultText)) _sb.Append("\nAssertions: ").Append(r.ResultText);
            _text.text = _sb.ToString();
        }
    }
}
