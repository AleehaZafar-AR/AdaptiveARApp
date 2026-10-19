// File: GrabEpisodeReporter.cs
// One per grabbable assembly part. It reports exactly two things about ITS OWN
// Grabbable - the component the Interaction SDK moves when a grab works - to one
// static callback: "grab started" and "grab ended". No registry, no scan loop, no
// cross-object state: the part reports itself.
//
// Source of truth: the Grabbable's own pointer events (Select / Unselect / Cancel).
// The same Grabbable's selecting-point count is read in Update as a local backstop,
// so a part whose event delegate never reached us on the device still reports
// (the log says which source fired: source=event or source=state).

using System;
using UnityEngine;

namespace AdaptiveAR.Steps
{
    public class GrabEpisodeReporter : MonoBehaviour
    {
        /// <summary>(part, source) when a grab episode starts on this part.</summary>
        public static Action<GameObject, string> OnGrabStarted;
        /// <summary>(part, source) when the last hand lets go of this part.</summary>
        public static Action<GameObject, string> OnGrabEnded;

        public bool EpisodeOpen { get { return _open; } }

        private Oculus.Interaction.Grabbable _grabbable;
        private Action<Oculus.Interaction.PointerEvent> _handler;
        private bool _open;

        private void OnEnable()
        {
            _grabbable = GetComponent<Oculus.Interaction.Grabbable>();
            if (_grabbable == null) return;
            _handler = OnPointerEvent;
            _grabbable.WhenPointerEventRaised += _handler;
        }

        private void OnDisable()
        {
            if (_grabbable != null && _handler != null) _grabbable.WhenPointerEventRaised -= _handler;
            if (_open) { _open = false; Raise(OnGrabEnded, "disabled"); }
        }

        private void OnPointerEvent(Oculus.Interaction.PointerEvent evt)
        {
            switch (evt.Type)
            {
                case Oculus.Interaction.PointerEventType.Select:
                    if (!_open) { _open = true; Raise(OnGrabStarted, "event"); }
                    break;
                case Oculus.Interaction.PointerEventType.Unselect:
                case Oculus.Interaction.PointerEventType.Cancel:
                    if (_open && _grabbable != null && _grabbable.SelectingPointsCount == 0) { _open = false; Raise(OnGrabEnded, "event"); }
                    break;
            }
        }

        private void Update()
        {
            if (_grabbable == null) return;
            bool selected = _grabbable.SelectingPointsCount > 0;
            if (selected && !_open) { _open = true; Raise(OnGrabStarted, "state"); }
            else if (!selected && _open) { _open = false; Raise(OnGrabEnded, "state"); }
        }

        private void Raise(Action<GameObject, string> cb, string source)
        {
            try { cb?.Invoke(gameObject, source); }
            catch (Exception e) { Debug.LogError($"[WrongGrab] reporter callback on '{name}' threw: {e}"); }
        }
    }
}
