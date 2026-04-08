using UnityEngine;

public class PalmPoseProvider : MonoBehaviour
{
    [Header("Assign Wrist Anchors")]
    public Transform leftHandAnchor;
    public Transform rightHandAnchor;

    private Vector3 lastLeftPos;
    private Vector3 lastRightPos;

    private Quaternion lastLeftRot;
    private Quaternion lastRightRot;

    public Vector3 LeftVelocity { get; private set; }
    public Vector3 RightVelocity { get; private set; }

    public float LeftAngularDelta { get; private set; }
    public float RightAngularDelta { get; private set; }

    void Start()
    {
        lastLeftPos = leftHandAnchor.position;
        lastRightPos = rightHandAnchor.position;

        lastLeftRot = leftHandAnchor.rotation;
        lastRightRot = rightHandAnchor.rotation;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Linear velocity
        LeftVelocity = (leftHandAnchor.position - lastLeftPos) / dt;
        RightVelocity = (rightHandAnchor.position - lastRightPos) / dt;

        // Angular change (degrees per frame)
        LeftAngularDelta = Quaternion.Angle(leftHandAnchor.rotation, lastLeftRot);
        RightAngularDelta = Quaternion.Angle(rightHandAnchor.rotation, lastRightRot);

        lastLeftPos = leftHandAnchor.position;
        lastRightPos = rightHandAnchor.position;

        lastLeftRot = leftHandAnchor.rotation;
        lastRightRot = rightHandAnchor.rotation;
    }

    public Vector3 GetMidpoint()
    {
        return (leftHandAnchor.position + rightHandAnchor.position) * 0.5f;
    }

    public float GetInterHandDistance()
    {
        return Vector3.Distance(leftHandAnchor.position, rightHandAnchor.position);
    }
}