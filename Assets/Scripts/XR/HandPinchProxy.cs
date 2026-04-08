using UnityEngine;

public class HandPinchProxy : MonoBehaviour
{
    public OVRHand hand;
    public OVRSkeleton skeleton;

    void Update()
    {
        if (hand == null || skeleton == null) return;

        if (hand.IsTracked && skeleton.IsDataValid)
        {
            Transform indexTip = skeleton.Bones[(int)OVRSkeleton.BoneId.Hand_IndexTip].Transform;
            Transform thumbTip = skeleton.Bones[(int)OVRSkeleton.BoneId.Hand_ThumbTip].Transform;

            Vector3 pinchMid = (indexTip.position + thumbTip.position) / 2f;
            transform.position = pinchMid;
        }
    }
}
