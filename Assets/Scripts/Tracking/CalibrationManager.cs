// File: CalibrationManager.cs
using UnityEngine;
using System;
using System.Collections;

public class CalibrationManager : MonoBehaviour
{
    public PalmPoseProvider palmProvider;
    public Transform placementZone;

    [Header("Calibration Settings")]
    public float countdownTime = 3f;

    [Header("Outputs")]
    public Vector3 positionOffset;
    public Quaternion rotationOffset;
    public bool isCalibrated = false;

    public event Action<float> OnCountdownTick;   // sends seconds remaining
    public event Action OnCalibrationComplete;

    public void StartCalibration()
    {
        StopAllCoroutines();
        StartCoroutine(CalibrationRoutine());
    }

    private IEnumerator CalibrationRoutine()
    {
        isCalibrated = false;

        float timer = countdownTime;
        while (timer > 0f)
        {
            OnCountdownTick?.Invoke(timer);
            timer -= Time.deltaTime;
            yield return null;
        }

        CaptureCalibration();
        OnCountdownTick?.Invoke(0f);
        OnCalibrationComplete?.Invoke();
    }

    private void CaptureCalibration()
    {
        Vector3 midpoint = palmProvider.GetMidpoint();

        Quaternion avgRotation = Quaternion.Slerp(
            palmProvider.leftHandAnchor.rotation,
            palmProvider.rightHandAnchor.rotation,
            0.5f
        );

        positionOffset = placementZone.position - midpoint;
        rotationOffset = placementZone.rotation * Quaternion.Inverse(avgRotation);

        isCalibrated = true;
        Debug.Log("Calibration complete.");
    }

    public Vector3 GetEstimatedObjectPosition()
    {
        return palmProvider.GetMidpoint() + positionOffset;
    }

    public Quaternion GetEstimatedObjectRotation()
    {
        Quaternion avgRotation = Quaternion.Slerp(
            palmProvider.leftHandAnchor.rotation,
            palmProvider.rightHandAnchor.rotation,
            0.5f
        );

        return rotationOffset * avgRotation;
    }
}