// File: HapticFeedback.cs
// Short, deterministic controller pulses for the three events that matter:
// a valid grab, a wrong part, a successful placement.
//
// Uses OVRInput.SetControllerVibration, which is already in the project. The pulse
// goes to the controller nearest the part (that is the hand that touched it); with
// hand tracking and no controllers there is nothing to vibrate and nothing happens.
// Every call is guarded: haptics can never block or break progression.

using System.Collections;
using UnityEngine;

namespace AdaptiveAR.XR
{
    public static class HapticFeedback
    {
        public enum Kind { Grab = 0, WrongPart = 1, Success = 2 }

        /// <summary>Global switch, e.g. for a condition without haptics.</summary>
        public static bool Enabled = true;

        private class Runner : MonoBehaviour { }
        private static Runner _runner;

        /// <summary>Pulses the controller nearest to <paramref name="nearWorldPoint"/>.</summary>
        public static void Pulse(Kind kind, Vector3 nearWorldPoint)
        {
            if (!Enabled) return;

            try
            {
                OVRInput.Controller c = NearestController(nearWorldPoint);
                if (c == OVRInput.Controller.None) return;

                Runner r = EnsureRunner();
                if (r == null) return;

                switch (kind)
                {
                    case Kind.Grab:
                        r.StartCoroutine(PulseRoutine(c, 0.5f, 0.25f, 0.06f));
                        break;
                    case Kind.WrongPart:
                        r.StartCoroutine(DoublePulseRoutine(c, 1f, 0.6f, 0.07f, 0.06f));
                        break;
                    case Kind.Success:
                        r.StartCoroutine(PulseRoutine(c, 0.8f, 0.9f, 0.14f));
                        break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Haptics] Ignored: " + e.Message);
            }
        }

        private static OVRInput.Controller NearestController(Vector3 point)
        {
            var rig = Object.FindAnyObjectByType<OVRCameraRig>();
            Transform space = rig != null ? rig.trackingSpace : null;

            float best = float.MaxValue;
            OVRInput.Controller bestC = OVRInput.Controller.None;

            foreach (OVRInput.Controller c in new[] { OVRInput.Controller.LTouch, OVRInput.Controller.RTouch })
            {
                if (!OVRInput.IsControllerConnected(c)) continue;

                Vector3 lp = OVRInput.GetLocalControllerPosition(c);
                Vector3 wp = space != null ? space.TransformPoint(lp) : lp;
                float d = (wp - point).sqrMagnitude;
                if (d < best) { best = d; bestC = c; }
            }

            return bestC;
        }

        private static Runner EnsureRunner()
        {
            if (_runner != null) return _runner;
            var go = new GameObject("HapticFeedback (runtime)");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<Runner>();
            return _runner;
        }

        private static IEnumerator PulseRoutine(OVRInput.Controller c, float frequency, float amplitude, float seconds)
        {
            OVRInput.SetControllerVibration(frequency, amplitude, c);
            yield return new WaitForSeconds(seconds);
            OVRInput.SetControllerVibration(0f, 0f, c);
        }

        private static IEnumerator DoublePulseRoutine(OVRInput.Controller c, float frequency, float amplitude, float on, float gap)
        {
            OVRInput.SetControllerVibration(frequency, amplitude, c);
            yield return new WaitForSeconds(on);
            OVRInput.SetControllerVibration(0f, 0f, c);
            yield return new WaitForSeconds(gap);
            OVRInput.SetControllerVibration(frequency, amplitude, c);
            yield return new WaitForSeconds(on);
            OVRInput.SetControllerVibration(0f, 0f, c);
        }
    }
}
