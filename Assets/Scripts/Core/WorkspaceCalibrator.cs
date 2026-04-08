using UnityEngine;

public class WorkspaceCalibrator : MonoBehaviour
{
    public Transform assemblyRoot;

    public float forwardOffset = 0.5f;
    public float downOffset = 0.2f;

    public void SetWorkspace()
    {
        Transform headset = Camera.main.transform;

        Vector3 forward = headset.forward;
        forward.y = 0;
        forward.Normalize();

        Vector3 pos = headset.position + forward * forwardOffset;
        pos.y = 0.75f;

        assemblyRoot.position = pos;
        assemblyRoot.rotation = Quaternion.LookRotation(forward);

        Debug.Log("Workspace calibrated.");
    }
}