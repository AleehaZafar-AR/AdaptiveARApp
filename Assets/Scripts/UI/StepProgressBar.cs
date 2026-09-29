// File: StepProgressBar.cs
// The segmented progress indicator from the reference design: one thin segment per
// assembly step, filled in accent as steps complete.
//
// Segmented rather than a continuous fill because it communicates BOTH how far
// along the operator is and how many steps remain, which a sliding bar does not.
// Segments are built at runtime from the step count, so the sequence length can
// change without anyone rebuilding the UI by hand.

using System.Collections.Generic;
using AdaptiveAR.Steps;
using UnityEngine;
using UnityEngine.UI;

namespace AdaptiveAR.UI
{
    [RequireComponent(typeof(RectTransform))]
    public class StepProgressBar : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private AssemblySessionController session;
        [SerializeField] private StepRunner stepRunner;

        [Tooltip("Authoritative completion. A segment fills when its stage is validated, " +
                 "not when the participant arrives at it.")]
        [SerializeField] private WorkflowState workflow;

        [Header("Appearance")]
        [SerializeField] private float segmentHeight = MrTheme.ProgressSegmentHeight;
        [SerializeField] private float segmentGap = MrTheme.ProgressSegmentGap;

        [Tooltip("Sprite used for each segment. A rounded sprite gives soft segment ends.")]
        [SerializeField] private Sprite segmentSprite;

        private readonly List<Image> _segments = new List<Image>();
        private int _builtFor = -1;

        private void OnEnable()
        {
            if (session != null)
                session.OnStateChanged += Refresh;

            Refresh();
        }

        private void OnDisable()
        {
            if (session != null)
                session.OnStateChanged -= Refresh;
        }

        private void Start()
        {
            Refresh();
        }

        public void Refresh()
        {
            int count = stepRunner != null ? stepRunner.StepCount : 0;
            if (count <= 0) return;

            if (_builtFor != count)
                Build(count);

            int current = stepRunner != null ? stepRunner.CurrentStepIndex : -1;
            bool finished = session != null && session.SequenceFinished;

            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i] == null) continue;

                bool done = finished
                            || (workflow != null ? workflow.IsStageComplete(i) : i < current);
                bool isCurrent = !finished && i == current && !done;

                // Completed reads as a calm accent; the current step is full strength;
                // everything ahead stays as an empty track.
                if (isCurrent) _segments[i].color = MrTheme.Accent;
                else if (done) _segments[i].color = MrTheme.WithAlpha(MrTheme.Accent, 0.45f);
                else _segments[i].color = MrTheme.TrackEmpty;
            }
        }

        private void Build(int count)
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                DestroyImmediateSafe(transform.GetChild(i).gameObject);

            _segments.Clear();

            var rect = (RectTransform)transform;
            float total = rect.rect.width;
            float width = (total - segmentGap * (count - 1)) / count;

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject($"Segment{i + 1}", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(transform, false);

                var r = (RectTransform)go.transform;
                r.anchorMin = new Vector2(0f, 0.5f);
                r.anchorMax = new Vector2(0f, 0.5f);
                r.pivot = new Vector2(0f, 0.5f);
                r.sizeDelta = new Vector2(width, segmentHeight);
                r.anchoredPosition = new Vector2(i * (width + segmentGap), 0f);

                var img = go.GetComponent<Image>();
                img.sprite = segmentSprite;
                if (segmentSprite != null) img.type = Image.Type.Sliced;
                img.raycastTarget = false;
                img.color = MrTheme.TrackEmpty;

                _segments.Add(img);
            }

            _builtFor = count;
        }

        private static void DestroyImmediateSafe(GameObject go)
        {
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        /// <summary>Lets the editor tool supply the sprite it generated.</summary>
        public void SetSegmentSprite(Sprite sprite)
        {
            segmentSprite = sprite;
        }
    }
}
