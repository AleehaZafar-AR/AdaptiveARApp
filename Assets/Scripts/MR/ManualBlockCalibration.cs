using UnityEngine;
using UnityEngine.Events;

public class ManualBlockCalibration : MonoBehaviour
{
    [Header("References")]
    public Transform engineBlockAnchor;
    public Transform grabHandle;

    [Header("Hand Tracking")]
    public OVRHand leftHand;
    public OVRHand rightHand;
    public Transform leftHandPinchPoint;
    public Transform rightHandPinchPoint;

    [Header("Calibration Settings")]
    public float grabDistance = 0.08f;
    public float pinchGrabThreshold = 0.7f;
    public float pinchReleaseThreshold = 0.3f;
    public bool allowRotation = true;

    [Header("State")]
    public bool calibrationLocked = false;

    [Header("Events")]
    public UnityEvent onCalibrationConfirmed;

    private bool isGrabbing = false;
    private OVRHand activeHand = null;
    private Transform activePinchPoint = null;

    private Vector3 positionOffset;
    private Quaternion rotationOffset;

    private void Update()
    {
        if (calibrationLocked)
            return;

        if (!isGrabbing)
        {
            TryStartGrab(leftHand, leftHandPinchPoint);
            if (!isGrabbing)
                TryStartGrab(rightHand, rightHandPinchPoint);
        }
        else
        {
            UpdateGrab();

            float pinchStrength = activeHand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
            if (pinchStrength < pinchReleaseThreshold)
            {
                EndGrab();
            }
        }
    }

    private void TryStartGrab(OVRHand hand, Transform pinchPoint)
    {
        if (hand == null || pinchPoint == null || grabHandle == null || engineBlockAnchor == null)
            return;

        float pinchStrength = hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
        if (pinchStrength < pinchGrabThreshold)
            return;

        float dist = Vector3.Distance(pinchPoint.position, grabHandle.position);
        if (dist > grabDistance)
            return;

        StartGrab(hand, pinchPoint);
    }

    private void StartGrab(OVRHand hand, Transform pinchPoint)
    {
        isGrabbing = true;
        activeHand = hand;
        activePinchPoint = pinchPoint;

        positionOffset = engineBlockAnchor.position - pinchPoint.position;

        if (allowRotation)
        {
            rotationOffset = Quaternion.Inverse(pinchPoint.rotation) * engineBlockAnchor.rotation;
        }
    }

    private void UpdateGrab()
    {
        if (activePinchPoint == null || engineBlockAnchor == null)
            return;

        engineBlockAnchor.position = activePinchPoint.position + positionOffset;

        if (allowRotation)
        {
            engineBlockAnchor.rotation = activePinchPoint.rotation * rotationOffset;
        }
    }

    private void EndGrab()
    {
        isGrabbing = false;
        activeHand = null;
        activePinchPoint = null;
    }

    public void ConfirmCalibration()
    {
        calibrationLocked = true;
        EndGrab();
        onCalibrationConfirmed?.Invoke();
        Debug.Log("Calibration confirmed. EngineBlockAnchor locked.");
    }

    public void UnlockCalibration()
    {
        calibrationLocked = false;
        Debug.Log("Calibration unlocked.");
    }
}