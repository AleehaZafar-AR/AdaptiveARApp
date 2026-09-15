using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class DropIntoTray : MonoBehaviour
{
    private Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true; // no physics until activated
    }

    void OnEnable()
    {
        // When made active, release to gravity
        rb.isKinematic = false;
    }

    void OnDisable()
    {
        // When hidden again, freeze it
        rb.isKinematic = true;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }
}