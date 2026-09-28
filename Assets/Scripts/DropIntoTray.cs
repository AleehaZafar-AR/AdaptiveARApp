using UnityEngine;

/// <summary>
/// Physics behaviour for a stageable assembly part.
///
/// Originally this released every part to gravity the instant it was enabled, which
/// meant all 74 parts free-fell from their authored poses the moment EngineAnchor
/// was activated. Combined with Discrete collision detection, thin non-convex tray
/// shells and no floor underneath, parts tunnelled through the trays and fell
/// forever out of reach.
///
/// Three changes address that, all of them configurable rather than hard-coded:
///   1. Parts stay put by default instead of being dropped (releaseToGravityOnEnable).
///   2. Continuous (speculative) collision detection, which works for kinematic and
///      dynamic bodies alike, so a fast-moving small part cannot tunnel.
///   3. Out-of-bounds recovery, so a part that escapes is returned to its authored
///      pose instead of ending the bench session.
///
/// The original drop behaviour is still available by ticking releaseToGravityOnEnable.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DropIntoTray : MonoBehaviour
{
    [Header("Spawn Behaviour")]
    [Tooltip("Original behaviour: release to gravity as soon as this part is enabled. " +
             "Left OFF, the part stays exactly where it was authored and never free-falls.")]
    [SerializeField] private bool releaseToGravityOnEnable = false;

    [Header("Collision")]
    [Tooltip("Speculative continuous detection is valid for both kinematic and dynamic " +
             "bodies, which matters because hand-held parts are moved kinematically and fast.")]
    [SerializeField] private CollisionDetectionMode collisionDetection = CollisionDetectionMode.ContinuousSpeculative;

    [Tooltip("Interpolation smooths the visual pose of a moving part between physics steps.")]
    [SerializeField] private RigidbodyInterpolation interpolation = RigidbodyInterpolation.Interpolate;

    [Header("Out-of-Bounds Recovery")]
    [Tooltip("Return this part to its authored pose if it falls away, instead of letting it fall forever.")]
    [SerializeField] private bool recoverIfLost = true;

    [Tooltip("Metres this part may drop below where it started before it counts as lost. " +
             "This is the real failure: falling through the table. Carrying a part sideways " +
             "to the engine never trips it.")]
    [SerializeField] private float maxFallBelowStart = 0.5f;

    [Tooltip("Generous catch-all distance in metres, for a part thrown right out of the work area. " +
             "Keep this well beyond the reach of normal assembly moves.")]
    [SerializeField] private float maxDistanceFromStart = 3f;

    [Tooltip("After this many recoveries the part is frozen instead of released again, so a gap " +
             "in the colliders cannot cause an endless fall-and-respawn loop.")]
    [SerializeField] private int maxRecoveriesBeforeHolding = 3;

    [Header("Debug")]
    [SerializeField] private bool logRecovery = true;

    private Rigidbody rb;

    private Vector3 authoredLocalPosition;
    private Quaternion authoredLocalRotation;
    private bool authoredPoseCaptured;
    private int recoveryCount;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        CaptureAuthoredPose();

        rb.collisionDetectionMode = collisionDetection;
        rb.interpolation = interpolation;

        rb.isKinematic = true; // no physics until explicitly released
    }

    void OnEnable()
    {
        if (releaseToGravityOnEnable)
            ReleaseToPhysics();
        else
            Hold();
    }

    void OnDisable()
    {
        // When hidden again, freeze it
        Hold();
    }

    void FixedUpdate()
    {
        // While a hand is holding the part the Interaction SDK drives it kinematically,
        // so this check is naturally skipped during a grab.
        if (!recoverIfLost || rb == null || rb.isKinematic || !authoredPoseCaptured)
            return;

        Vector3 start = AuthoredWorldPosition();

        // Gravity is world -Y, so "fell away" means it dropped well below where it began.
        float fallen = start.y - transform.position.y;
        bool fellThrough = fallen > maxFallBelowStart;
        bool wayOff = Vector3.Distance(transform.position, start) > maxDistanceFromStart;

        if (fellThrough || wayOff)
            RecoverToAuthoredPose();
    }

    /// <summary>Records the pose this part was authored at, used as the recovery target.</summary>
    private void CaptureAuthoredPose()
    {
        authoredLocalPosition = transform.localPosition;
        authoredLocalRotation = transform.localRotation;
        authoredPoseCaptured = true;
    }

    private Vector3 AuthoredWorldPosition()
    {
        return transform.parent != null
            ? transform.parent.TransformPoint(authoredLocalPosition)
            : authoredLocalPosition;
    }

    /// <summary>Makes the part dynamic so gravity and collisions act on it.</summary>
    public void ReleaseToPhysics()
    {
        if (rb == null) return;

        rb.collisionDetectionMode = collisionDetection;
        rb.isKinematic = false;
    }

    /// <summary>Freezes the part in place and clears any motion.</summary>
    public void Hold()
    {
        if (rb == null) return;

        rb.isKinematic = true;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    /// <summary>Returns the part to where it was authored, cancelling all motion.</summary>
    public void RecoverToAuthoredPose()
    {
        if (rb == null || !authoredPoseCaptured) return;

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        transform.localPosition = authoredLocalPosition;
        transform.localRotation = authoredLocalRotation;

        recoveryCount++;

        // Let it settle naturally the first few times. If it keeps escaping there is a real
        // gap in the colliders, so freeze it rather than loop forever.
        bool freeze = recoveryCount >= maxRecoveriesBeforeHolding;
        if (freeze)
            Hold();

        if (logRecovery)
            Debug.Log($"[DropIntoTray] '{name}' fell away and was returned to its start pose " +
                      $"(recovery {recoveryCount}{(freeze ? ", now frozen" : "")}).", this);
    }
}
