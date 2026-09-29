// File: GuidanceArrow.cs
// A single arrow that tells the operator where to look next.
//
// Two jobs, in order:
//   1. Before the part is picked up, it hovers over the PART, so "which one do I
//      take?" is answered without reading anything.
//   2. Once the part is in hand, it moves to the TARGET, so "where does it go?" is
//      answered the same way.
//
// It follows whichever accepted part the operator actually picked up, which matters
// for the interchangeable pistons: the arrow tracks the piston in their hand, not
// the one the step happened to name.
//
// Support level decides whether it appears at all - it is guidance, so it belongs
// to the levels that are meant to provide more of it.

using AdaptiveAR.Steps;
using AdaptiveAR.Support;
using UnityEngine;

namespace AdaptiveAR.UI
{
    public class GuidanceArrow : MonoBehaviour
    {
        [Header("Sources")]
        [SerializeField] private StepValidator validator;
        [SerializeField] private SupportLevelController supportLevel;

        [Header("Arrow")]
        [Tooltip("Prefab instanced once and reused. Assets/Prefabs/arrow.prefab.")]
        [SerializeField] private GameObject arrowPrefab;

        [Tooltip("Metres above the thing being pointed at.")]
        [SerializeField] private float hoverHeight = 0.18f;

        [Tooltip("Size of the spawned arrow, in metres along its longest axis.")]
        [SerializeField] private float arrowSize = 0.12f;

        [Header("Motion")]
        [Tooltip("Bob distance in metres. Movement is what makes it read as a pointer.")]
        [SerializeField] private float bobAmplitude = 0.02f;

        [SerializeField] private float bobSpeed = 2.2f;

        [Tooltip("How quickly the arrow glides between part and target.")]
        [SerializeField] private float followSpeed = 6f;

        [Header("Colour")]
        [SerializeField] private bool tintArrow = true;

        [Header("Support levels")]
        [Tooltip("Arrows are assistance, so they appear only from this level upward. " +
                 "L1 is meant to be minimal.")]
        [SerializeField] private SupportLevel minimumLevel = SupportLevel.L2_Guided;

        private GameObject _arrow;
        private Renderer[] _renderers;
        private Vector3 _smoothedPos;
        private bool _hasPos;

        private void Start()
        {
            EnsureArrow();
            SetVisible(false);
        }

        private void Update()
        {
            if (validator == null || !validator.IsActive)
            {
                SetVisible(false);
                return;
            }

            if (supportLevel != null && (int)supportLevel.CurrentLevel < (int)minimumLevel)
            {
                SetVisible(false);
                return;
            }

            // Held already? Point at the destination. Otherwise point at the part.
            bool handled = validator.HasBeenHandled;
            Transform focus = handled ? validator.CurrentTarget : validator.CurrentPart;

            if (focus == null)
            {
                SetVisible(false);
                return;
            }

            EnsureArrow();
            if (_arrow == null) return;

            SetVisible(true);

            Vector3 want = focus.position + Vector3.up * hoverHeight;

            if (!_hasPos)
            {
                _smoothedPos = want;
                _hasPos = true;
            }
            else
            {
                _smoothedPos = Vector3.Lerp(_smoothedPos, want, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
            }

            float bob = Mathf.Sin(Time.time * bobSpeed) * bobAmplitude;
            _arrow.transform.position = _smoothedPos + Vector3.up * bob;

            // Point straight down at whatever it is indicating.
            _arrow.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);

            if (tintArrow)
                Tint(handled ? MrTheme.Success : MrTheme.Accent);
        }

        private void EnsureArrow()
        {
            if (_arrow != null || arrowPrefab == null) return;

            _arrow = Instantiate(arrowPrefab);
            _arrow.name = "GuidanceArrow (runtime)";

            // Normalise to a predictable size: the source prefab's scale is unknown.
            Bounds b = ComputeBounds(_arrow);
            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (longest > 1e-4f)
                _arrow.transform.localScale *= arrowSize / longest;

            // Guidance must never obstruct the physical work.
            foreach (Collider col in _arrow.GetComponentsInChildren<Collider>(true))
                col.enabled = false;

            foreach (Rigidbody rb in _arrow.GetComponentsInChildren<Rigidbody>(true))
                rb.isKinematic = true;

            _renderers = _arrow.GetComponentsInChildren<Renderer>(true);
        }

        private static Bounds ComputeBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.one * 0.1f);

            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        private void Tint(Color c)
        {
            if (_renderers == null) return;

            foreach (Renderer r in _renderers)
            {
                if (r == null || r.material == null) continue;

                if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
                else if (r.material.HasProperty("_Color")) r.material.SetColor("_Color", c);
            }
        }

        private void SetVisible(bool visible)
        {
            if (_arrow != null && _arrow.activeSelf != visible)
                _arrow.SetActive(visible);

            if (!visible) _hasPos = false;
        }

        private void OnDestroy()
        {
            if (_arrow != null) Destroy(_arrow);
        }
    }
}
